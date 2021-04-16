//-----------------------------------------------------------------------------
// Program.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
//-----------------------------------------------------------------------------

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace MicrosoftStoreServicesSample
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
