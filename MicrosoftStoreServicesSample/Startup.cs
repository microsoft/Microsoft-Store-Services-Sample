//-----------------------------------------------------------------------------
// Startup.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License file under the project root for
// license information.
//-----------------------------------------------------------------------------

// Ignore Spelling: env

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.CorrelationVector;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.StoreServices;
using System;
using System.Net.Http;

namespace MicrosoftStoreServicesSample
{

    public class Startup
    {
        private ILogger _logger;
        private CorrelationVector _cV;

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            // Add the configuration singleton here
            services.AddSingleton<IConfiguration>(Configuration);

            //  Initialize in-memory cache for Access Tokens and secrets
            services.AddMemoryCache();

            //  So that we can use an HttpClientFactory for better performance
            //  and proper management of HttpClients see the following:
            //  https://www.stevejgordon.co.uk/introduction-to-httpclientfactory-aspnetcore
            //  https://aspnetmonsters.com/2016/08/2016-08-27-httpclientwrong/
            services.AddHttpClient();
            services.AddControllers();

            //  Initialize our persistent database for pending consume transactions
            //  if the connection string is not in the settings, then we will fall back to
            //  an in-memory cache but that would not be safe for a production deployment
            var connectionString = PersistentDB.ServerDBController.GetConnectionString(Configuration);
            if (!String.IsNullOrEmpty(connectionString))
            {
                //  This is needed at startup for creating EF Core migrations for persistent Databases.
                //  Add any more contexts and connections here for other persistent databases you create.
                services.AddDbContext<PersistentDB.ServerDBContext>
                    (options => options.UseSqlServer(connectionString));
            }
            else
            {
                services.AddDbContext<PersistentDB.ServerDBContext>
                    (options => options.UseInMemoryDatabase(ServiceConstants.InMemoryDB));
            }

            // Add the configuration singleton here
            services.AddSingleton<IStoreServicesClientFactory>( sp => new StoreServicesClientFactory() );
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app,
                              IWebHostEnvironment env,
                              IHttpClientFactory httpClientFactory,
                              IMemoryCache serverCache,
                              IStoreServicesClientFactory storeServiceClientFactory,
                              ILogger<Startup> logger)
        {
            _logger = logger;
            _cV = new CorrelationVector();
            _logger.StartupInfo(_cV.Value, "Starting Initialization of server...");

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseHttpsRedirection()
               .UseRouting()
               .UseAuthorization()
               .UseEndpoints(endpoints =>
               {
                   endpoints.MapControllers();
               });

            //-------------------------------------------------------------
            //  Initializing Access Tokens and StoreServicesClientFactory
            //-------------------------------------------------------------
            {
                _logger.StartupInfo(_cV.Value, "Initializing StoreServicesCachedTokenProvider...");

                var tenantId = Configuration.GetValue(ServiceConstants.EntraTenantIdKey, "");
                var clientId = Configuration.GetValue(ServiceConstants.EntraClientIdKey, "");
                var clientSecret = Configuration.GetValue(ServiceConstants.EntraSecretKey, "");
                var managedIdentity = Configuration.GetValue(ServiceConstants.EntraManagedIdKey, "");

                if (string.IsNullOrEmpty(tenantId))
                {
                    _logger.StartupError(_cV.Value, "Unable to get TenantId from config settings", null);
                }
                if (string.IsNullOrEmpty(clientId))
                {
                    _logger.StartupError(_cV.Value, "Unable to get ClientId from config settings", null);
                }
                if (string.IsNullOrEmpty(clientSecret) && string.IsNullOrEmpty(managedIdentity))
                {
                    _logger.StartupWarning(_cV.Value, "Unable to get ManagedIdentity or ClientSecret from config settings.  At least one is required", null);
                }

                //  Override the HttpClient creation functions in the token provider
                //  and store client to be from our httpClientFactory for better performance.
                StoreServicesCachedTokenProvider.CreateHttpClientFunc = httpClientFactory.CreateClient;
                StoreServicesClient.CreateHttpClientFunc = httpClientFactory.CreateClient;

                _logger.StartupInfo(_cV.Value, "Initializing Entra Access Tokens...");

                // temporary values to switch between secret and managed identity.
                var secretOrManagedID = "";
                bool useManagedIdentity = false;

                if (string.IsNullOrEmpty(clientSecret))
                {
                    _logger.StartupInfo(_cV.Value, "Using Azure Managed Identity...");
                    secretOrManagedID = managedIdentity;
                    useManagedIdentity = true;
                }
                else
                {
                    _logger.StartupInfo(_cV.Value, "Using Entra Secret Key...");
                    secretOrManagedID = clientSecret;
                    useManagedIdentity = false;
                }
                
                var cachedTokenProvider = new StoreServicesCachedTokenProvider( serverCache,
                                                                                tenantId,
                                                                                clientId,
                                                                                secretOrManagedID,
                                                                                useManagedIdentity);

                var serviceTokenTask = cachedTokenProvider.GetServiceAccessTokenAsync();
                var purchaseTokenTask = cachedTokenProvider.GetPurchaseAccessTokenAsync();
                var collectionsTokenTask = cachedTokenProvider.GetCollectionsAccessTokenAsync();

                serviceTokenTask.Wait();
                purchaseTokenTask.Wait();
                collectionsTokenTask.Wait();

                _logger.StartupInfo(_cV.Value, 
                    $"AccessTokensCached: " +
                    $"Service:{serviceTokenTask.Result.Token}, " +
                    $"Collections:{collectionsTokenTask.Result.Token}, " +
                    $"Purchase:{purchaseTokenTask.Result.Token}");
              
                //  Now get the ServiceIdenty and we can configure the IStoreServicesFactory
                var serviceIdentity = Configuration.GetValue(ServiceConstants.ServiceIdentity, ServiceConstants.ServiceName);
                if (string.IsNullOrEmpty(serviceIdentity))
                {
                    //  Warning because this is recoverable and the default Service Identity will be used,
                    //  but each service should set their own identity.
                    _logger.StartupWarning(_cV.Value, "Unable to get ServiceIdentity from config settings, using application name", null);
                    serviceIdentity = env.ApplicationName;
                }

                _logger.StartupInfo(_cV.Value, "Initializing StoreServicesClientFactory...");
                storeServiceClientFactory.Initialize(serviceIdentity, cachedTokenProvider);
            }

            //  Ensure our persistent DB is created
            using (var context = PersistentDB.ServerDBController.CreateDbContext(Configuration, _cV, _logger))
            {
                try
                { 
                    if (context.Database.EnsureCreated())
                    {
                        _logger.StartupInfo(_cV.Value, "PersistentDB is now created");
                    }
                    else
                    {
                        _logger.StartupInfo(_cV.Value, "PersistentDB already existed");
                    }
                }
                catch (Exception ex)
                {
                    _logger.StartupError(_cV.Value, "Unable to validate PersistentDB connection and creation", ex);
                }
            }

            //  Todo: For testing you can use this block of code to pre-populate the database with values
            //  to re-run tests when using Clawback and not deleting the messages from the queue.
            //  If you are going to be using this and debugging, make sure to comment out the block of
            //  code for Step 6 in RunClawbackV2ReconciliationAsync so the events are not removed from
            //  the queue as you test and debug.
            //var consumeManager = new ConsumableManager(Configuration, storeServiceClientFactory, _logger);
            //var testPopulateTask = consumeManager.PopulateTestValuesInDatabases(_cV);
            //testPopulateTask.Wait();

            _logger.StartupInfo(_cV.Value, "Server initialized and ready for requests");
        }
    }
}
