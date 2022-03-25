//-----------------------------------------------------------------------------
// ClawbackQueueItem.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License file under the project root for
// license information.
//-----------------------------------------------------------------------------

using System;
using System.ComponentModel.DataAnnotations;

namespace MicrosoftStoreServicesSample
{
    /// <summary>
    /// Object class to be added to our persistent database that will represent a
    /// User Purchase Id we will use for our Clawback checks and reconciliation
    /// to check for refunds on the account for up to 90 days.
    /// </summary>
    public class ClawbackQueueItem
    {
        /// <summary>
        /// Unique key for lookup in the queue based on the account type
        /// Single-purchasing accounts - UserId for the user in our system
        /// Multi-purchasing accounts - GUID generated on creation of item
        /// 
        /// For more information see the API summary for 
        /// ConsumableManager.AddUserPurchaseIdToClawbackQueue()
        /// </summary>
        [Key]
        public string DbKey { get; set; }

        /// <summary>
        /// UserPurchaseId that we have cached and keep updating for the
        /// user so that we can call the Clawback service even if they
        /// are not online.
        /// </summary>
        public string UserPurchaseId { get; set; }

        /// <summary>
        /// When the consume (or latest consume) happened
        /// </summary>
        public DateTimeOffset ConsumeDate { get; set; }
        
        public ClawbackQueueItem() { }
        
        /// <summary>
        /// Creates an item to add to the Clawback validation queue 
        /// from a completed PendingConsumeRequest object.
        /// </summary>
        /// <param name="request">Completed consume request info</param>
        public ClawbackQueueItem(PendingConsumeRequest request, bool isSinglePurchasingAccount = true)
        {
            ConsumeDate    = DateTimeOffset.UtcNow;
            UserPurchaseId = request.UserPurchaseId;

            // For more information on single vs multi-purchasing
            // accounts, see the API summary for 
            // ConsumableManager.AddUserPurchaseIdToClawbackQueue()
            if (isSinglePurchasingAccount)
            {
                //  For single-purchasing accounts we only need one
                //  UserPurchaseId per user in our system.
                DbKey = request.UserId;
            }
            else
            {
                //  For multi-purchasing accounts we must keep each
                //  transaction's UserPurchaseId as it may or may
                //  not be tied to the user's XBL account in our
                //  system.  So create a new unique GUID for the key.
                DbKey = Guid.NewGuid().ToString();
            }
        }
    }
}
