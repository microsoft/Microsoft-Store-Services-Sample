//-----------------------------------------------------------------------------
// ServiceConstants.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License file under the project root for
// license information.
//-----------------------------------------------------------------------------

using System.Reflection;

namespace MicrosoftStoreServicesSample
{
    public class ServiceConstants
    {
        public const string InMemoryDB  = "InMemoryDB";
        public static string ServiceName 
        {
            get
            {
                return $"MicrosoftStoreServiceSample_{Assembly.GetExecutingAssembly().GetName().Version}";
            }
        }

        public const string EntraClientIdKey  = "ENTRA_CLIENT_ID";
        public const string EntraTenantIdKey  = "ENTRA_TENANT_ID";
        public const string EntraManagedIdKey = "ENTRA_MANAGED_ID";
        public const string EntraSecretKey    = "ENTRA_SECRET";
        public const string ServiceIdentity   = "SERVICE_IDENTITY";
    }
}
