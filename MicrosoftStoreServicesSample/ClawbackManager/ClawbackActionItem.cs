//-----------------------------------------------------------------------------
// ClawbackActionItem.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License file under the project root for
// license information.
//-----------------------------------------------------------------------------

using Microsoft.StoreServices;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace MicrosoftStoreServicesSample
{
    public class ClawbackActionItem : OrderLineItem
    {
        public ClawbackActionItemState State { get; set; }
        public DateTimeOffset OrderRefundedDate { get; set; }
        public DateTimeOffset OrderPurchaseDate { get; set; }
        public string OrderId { get; set; }

        //  Key override for our database
        [Key] [JsonProperty("lineItemId")] new public string LineItemId { get; set; }

        /// <summary>
        /// List of UserId candidates that this OrderId and LineItemId showed up
        /// for results when checking Clawback.  This can happen if the same
        /// account signed into the Windows Store app is used to buy products
        /// for different game users / UserIds on our service side.  
        /// 
        /// We will then need to determine which of the UserIds this refund
        /// applied to in our service and clawback the value of that item that
        /// was refunded.
        /// </summary>
        public string ClawbackCandidatesJSON { get; set; }

        public ClawbackActionItem()
        {
            State = ClawbackActionItemState.Building;
            ClawbackCandidatesJSON = "";
        }

        public ClawbackActionItem(ClawbackQueueItem queueItem, ClawbackItem parentOrder, OrderLineItem lineItem)
        {
            //  Building state means we are building the list of candidates
            //  where the clawback action might need to be taken against
            State = ClawbackActionItemState.Building;

            OrderPurchaseDate = parentOrder.OrderPurchaseDate;
            OrderRefundedDate = parentOrder.OrderRefundedDate;
            
            LineItemId    = lineItem.LineItemId;
            Quantity      = lineItem.Quantity;
            SkuId         = lineItem.SkuId;
            ProductId     = lineItem.ProductId;
            LineItemState = lineItem.LineItemState;

            var candidates = new List<ClawbackCandidate>
            {
                new ClawbackCandidate(queueItem)
            };

            SetCandidatesJSON(candidates);
        }

        /// <summary>
        /// Takes in a List of ClawbackCandidates and coverts it to JSON to be
        /// stored in the ClwabackCandidatesJSON value which is then stored in 
        /// the DB Table for this Action Item.
        /// </summary>
        /// <param name="candidates"></param>
        public void SetCandidatesJSON(List<ClawbackCandidate> candidates)
        {
            ClawbackCandidatesJSON = JsonConvert.SerializeObject(candidates);
        }

        /// <summary>
        /// Converts the json string of the candidates' info into a List for
        /// easier operations from the caller
        /// </summary>
        /// <returns>List of ClawbackCandidates</returns>
        public List<ClawbackCandidate> GetClawbackCandidates()
        {
            return JsonConvert.DeserializeObject<List<ClawbackCandidate>>(ClawbackCandidatesJSON);
        }
    }

    /// <summary>
    /// Class that contains the basic info we need for an identified ClawbackCandidate
    /// which a refund may have been tied to.  We use JSON to store this as part of the
    /// ActionItem database
    /// </summary>
    public class ClawbackCandidate
    {
        [JsonProperty("tid")] public string TrackingId { get; set; }
        [JsonProperty("id")]  public string UserId { get; set; }
        [JsonProperty("d")]   public DateTimeOffset ConsumeDate { get; set; } = DateTime.MaxValue;
        [JsonProperty("q")]   public uint ConsumedQuantity { get; set; }

        public ClawbackCandidate()
        { }

        public ClawbackCandidate(ClawbackQueueItem QueueItem)
        {
            ConsumeDate      = QueueItem.ConsumeDate;
            TrackingId       = QueueItem.TrackingId;
            UserId           = QueueItem.UserId;
            ConsumedQuantity = QueueItem.Quantity;
        }
    }

    /// <summary>
    /// Tracks the current state of the clawback action items through the discovery and reconciliation process.
    /// </summary>
    public enum ClawbackActionItemState
    {
        /// <summary>
        /// This action item is currently being worked on and possible clawback candidates are being identified and added to it.
        /// </summary>
        Building = 0,

        /// <summary>
        /// Action item is done building and has a list of candidates.  It is now pending for the next step of reconciliation once
        /// all other action items are done building.
        /// </summary>
        Pending,

        /// <summary>
        /// This action item is currently being reconciled by identifying which candidate is best and then resolving the action
        /// needed for that candidate's balance and account.
        /// </summary>
        Running,

        /// <summary>
        /// This action item was previously reconciled and action was taken.  This can now be ignored if it comes up in future
        /// clawback calls.
        /// </summary>
        Completed
    }
}
