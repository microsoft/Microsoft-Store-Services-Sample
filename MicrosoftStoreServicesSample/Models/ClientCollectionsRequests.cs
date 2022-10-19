//-----------------------------------------------------------------------------
// ClientCollectionsRequests.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License file under the project root for
// license information.
//-----------------------------------------------------------------------------

using System.Collections.Generic;

namespace Microsoft.StoreServices
{
    public class ClientCollectionsQueryRequest
    {
        public string UserCollectionsId { get; set; }
        public string Sbx { get; set; }
        public List<string> EntitlementFilters { get; set; }
        public List<string> productIds { get; set; }
    }

    public class ClientConsumeRequest
    {
        public string UserPurchaseId { get; set; }
        public string UserCollectionsId { get; set; }
        public uint Quantity { get; set; }
        public string ProductId { get; set; }
        public string TransactionId { get; set; }
        public string UserId { get; set; }
        public bool IsUnmanagedConsumable { get; set; } = false;
        public bool IncludeOrderids { get; set; } = false;
        public string Sbx { get; set; }
    }
}
