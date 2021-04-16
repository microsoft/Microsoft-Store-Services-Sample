//-----------------------------------------------------------------------------
// ClientAccessTokensRequest.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
//-----------------------------------------------------------------------------

namespace MicrosoftStoreServicesSample
{
    public class ClientRecurrneceChangeRequest
    {
        public string UserPurchaseId { get; set; }
        public string UserId { get; set; }
        public string ChangeType { get; set; }
        public string RecurrenceId { get; set; }
        public int ExtensionTime { get; set; }
    }
}
