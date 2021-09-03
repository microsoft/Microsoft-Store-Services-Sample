//-----------------------------------------------------------------------------
// ClientPurchaseId.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
//-----------------------------------------------------------------------------

using System.Collections.Generic;

namespace MicrosoftStoreServicesSample
{
    public class ClientPurchaseId
    {
        public string UserPurchaseId { get; set; }
        public string UserId { get; set; }
        public List<string> LineItemStateFilter { get; set; }
        public string sbx { get; set; }
    }
}
