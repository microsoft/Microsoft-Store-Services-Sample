//-----------------------------------------------------------------------------
// ClientRecurrneceChangeRequest.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License file under the project root for
// license information.
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
        public string Sbx { get; set; }
    }
}
