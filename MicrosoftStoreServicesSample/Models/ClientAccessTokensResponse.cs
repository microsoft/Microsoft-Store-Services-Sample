//-----------------------------------------------------------------------------
// ClientAccessTokensResponse.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
//-----------------------------------------------------------------------------

using System.Collections.Generic;

namespace MicrosoftStoreServicesSample.Models
{
    public class ClientAccessTokensResponse
    {
        public List<AccessTokenResponse> AccessTokens { get; set; }
        public string UserID { get; set; }

        public ClientAccessTokensResponse()
        {
            AccessTokens = new List<AccessTokenResponse>();
        }
    }

    public class AccessTokenResponse
    {
        public string Audience { get; set; }
        public string Token { get; set; }
    }


}
