//-----------------------------------------------------------------------------
// ServiceControllerBase.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
//-----------------------------------------------------------------------------

using Microsoft.AspNetCore.Mvc;
using Microsoft.CorrelationVector;
using Microsoft.Extensions.Logging;
using Microsoft.StoreServices;
using MicrosoftStoreServicesSample.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MicrosoftStoreServicesSample.Controllers
{
    /// <summary>
    /// Base functions used as a framework for more specific endpoints in this sample
    /// </summary>
    public class ServiceControllerBase : ControllerBase
    {
        protected ILogger _logger;
        protected CorrelationVector _cV;
        protected IStoreServicesClientFactory _storeServicesClientFactory;

        public ServiceControllerBase(IStoreServicesClientFactory storeServicesClientFactory,
                                     ILogger logger)
        {
            _storeServicesClientFactory = storeServicesClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Initializes the MS-CV logging which can't be setup in the constructor
        /// because the HttpContext is only available when the actual endpoint is
        /// being executed so this should be added at the start of each endpoint
        /// function for an incoming request.
        /// </summary>
        protected void InitializeLoggingCv()
        {
            string cvHeader = this.HttpContext.Request.Headers["MS-CV"];
            if (!string.IsNullOrEmpty(cvHeader))
            {
                //  This request has a cV header on it,
                //  we now extend the cV for this service's logging
                //  Ex: pDWfNQcD7Eqdr74xjZa0mg.0 -> pDWfNQcD7Eqdr74xjZa0mg.0.0
                _cV = CorrelationVector.Extend(cvHeader);
            }
            else
            {
                //  This request does not have a cV header, create one for logging
                _cV = new CorrelationVector(CorrelationVectorVersion.V2);
            }

            //  This allows the API controllers (or subsequent delegates in the flow) to access it.
            //  We also stamp our response with the cV here in case something goes wrong to ensure
            //  it gets back to the client for lookup later
            this.HttpContext.Response.Headers.Add("MS-CV", _cV.Value);
        }

        /// <summary>
        /// Finishes up the MS-CV logging and adds the MS-CV header to the response
        /// </summary>
        protected void FinalizeLoggingCv()
        {
            this.HttpContext.Items["MS-CV"] = _cV.Value;
            this.HttpContext.Response.Headers.Remove("MS-CV");
            this.HttpContext.Response.Headers.Add("MS-CV", _cV.Value);
        }

        /// <summary>
        /// TODO: Replace this with your own user ID tracking, for the sample we just ask the sample
        /// client to identify the UserID.  For Xbox Live enabled titles this will come from the
        /// X-token in the Authorization header.
        /// </summary>
        /// <returns>Unique ID for the caller</returns>
        protected string GetUserId()
        {
            string userId = this.HttpContext.Request.Headers["Authorization"];
            if(userId == null)
            {
                userId="NoUserIdProvided";
            }
            return userId;
        }

        /// <summary>
        /// Utility function to retrieve the access tokens that will need to be sent to the
        /// client to generate the UserCollectionsId or the UserPurchaseId
        /// </summary>
        /// <returns></returns>
        protected async Task<List<AccessTokenResponse>> GetAccessTokens()
        {
            var tokens = new List<AccessTokenResponse>();

            var storeClient = _storeServicesClientFactory.CreateClient();

            var collectionsTokenTask = storeClient.GetCollectionsAccessTokenAsync();
            var purchaseTokenTask = storeClient.GetPurchaseAccessTokenAsync();

            var collectionsToken = await collectionsTokenTask;
            var purchaseToken = await purchaseTokenTask;

            tokens.Insert(0, new AccessTokenResponse() { Token = collectionsToken.Token, Audience = collectionsToken.Audience });
            tokens.Insert(0, new AccessTokenResponse() { Token = purchaseToken.Token, Audience = purchaseToken.Audience });

            return tokens;
        }
    }
}
