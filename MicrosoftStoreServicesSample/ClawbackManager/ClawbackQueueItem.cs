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
        public string TrackingId { get; set; }
        public DateTimeOffset ConsumeDate { get; set; }
        public string ProductId { get; set; }
        public string UserId { get; set; }
        public string UserPurchaseId { get; set; }
        public uint Quantity { get; set; }

        public ClawbackQueueItem() { }
        
        /// <summary>
        /// Creates an item to add to the Clawback validation queue from a completed
        /// PendingConsumeRequest object
        /// </summary>
        /// <param name="request">Completed consume request info</param>
        public ClawbackQueueItem(PendingConsumeRequest request)
        {
            ConsumeDate    = DateTimeOffset.UtcNow;
            TrackingId     = request.TrackingId;
            ProductId      = request.ProductId;
            UserId         = request.UserId;
            UserPurchaseId = request.UserPurchaseId;
            Quantity       = request.RemoveQuantity;
        }
    }
}
