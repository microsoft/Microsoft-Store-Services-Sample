//-----------------------------------------------------------------------------
// PendingConsumeRequest.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License file under the project root for
// license information.
//-----------------------------------------------------------------------------

using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;

namespace MicrosoftStoreServicesSample
{
    /// <summary>
    /// Service specific class to cache pending consume items in our database.  Contains
    /// additionally UserId and the UserPurchaseId that would be needed for calling the
    /// Clawback service and check for refunds status of the consume.
    /// </summary>
    public class PendingConsumeRequest
    {
        /// <summary>
        /// Unique Id for the user withing your system.
        /// </summary>
        public string UserId { get; set; }

        /// <summary>
        /// This is needed to be cached if you are planning to call the Clawback service
        /// to check for any refunds of this transaction after you consumed it.
        /// </summary>
        public string UserPurchaseId { get; set; }

        /// <summary>
        /// Identifies the store account to consume the quantity from.  This
        /// contains the UserCollectionsId obtained from the client. This is
        /// marked with virtual so it can be overridden to define a key value.
        /// </summary>
        public string UserCollectionsId { get; set; }

        /// <summary>
        /// ProductId / StoreId of the consumable product.
        /// </summary>
        public string ProductId { get; set; }

        /// <summary>
        /// Unique Id that is used to track the consume request and can
        /// be used to replay the request and verify the resulting status.
        /// Generally this would be the Key if you are using this in a DB
        /// so it is marked with virtual so it can be overridden.
        /// </summary>
        [Key]
        public string TrackingId { get; set; }

        /// <summary>
        /// Quantity to be removed from the user's balance of the consumable product.
        /// </summary>
        public uint RemoveQuantity { get; set; }

        /// <summary>
        /// Used to determine if this is a managed or unmanaged consumable as the consume request JSON is different
        /// between them.
        /// </summary>
        public bool IsUnmanagedConsumable { get; set; }
    }
}
