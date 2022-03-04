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
    /// consume transaction that we will want to validate with the Clawback service
    /// for up to 90 days to see if the user requested a refund on the item.
    /// </summary>
    public class ClawbackQueueItem
    {
        [Key]
        //  Unique UserId within our service for the user
        //  If your service is supporting multi-purchasing accounts
        //  per UserId then this needs to be a unique GUID instead
        //  of the actual UserId.
        public string UserId { get; set; }

        /// <summary>
        /// UserPurchaseId that we have cached and keep updating for the
        /// user so that we can call the Clawback service even if they
        /// are not online.
        /// </summary>
        public string UserPurchaseId { get; set; }

        /// <summary>
        /// When the consume (or last consume) happened
        /// </summary>
        public DateTimeOffset ConsumeDate { get; set; }
        
        public ClawbackQueueItem() { }
        
        /// <summary>
        /// Creates an item to add to the Clawback validation queue from a completed
        /// PendingConsumeRequest object
        /// </summary>
        /// <param name="request">Completed consume request info</param>
        public ClawbackQueueItem(PendingConsumeRequest request, bool SinglePurchasingAccount = true)
        {
            ConsumeDate    = DateTimeOffset.UtcNow;
            UserPurchaseId = request.UserPurchaseId;

            if (SinglePurchasingAccount)
            {
                UserId = request.UserId;
            }
            else
            {
                //  If your service is supporting multi-purchasing accounts
                //  for a single UserId, then this must be a unique GUID
                //  and not the actual UserId.
                UserId = Guid.NewGuid().ToString();
            }
        }
    }
}
