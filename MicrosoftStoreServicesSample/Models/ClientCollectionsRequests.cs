//-----------------------------------------------------------------------------
// ClientCollectionsRequests.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
//-----------------------------------------------------------------------------

namespace Microsoft.StoreServices
{
    public class ClientCollectionsQueryRequest
    {
        public string UserCollectionsId { get; set; }
        public string sbx { get; set; }
    }

    public class ClientConsumeRequest
    {
        public string UserPurchaseId { get; set; }
        public string UserCollectionsId { get; set; }
        public uint Quantity { get; set; }
        public string ProductId { get; set; }
        public string TransactionId { get; set; }
        public string UserId { get; set; }
    }
}
