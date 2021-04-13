//-----------------------------------------------------------------------------
// ClawbackActionItem.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
//-----------------------------------------------------------------------------

using Microsoft.StoreServices;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace MicrosoftStoreServicesSample
{
    /// <summary>
    /// This class is used to track the clawback items that need action
    /// and the ones that we have taken action on.  This class inherits from
    /// OrderLineItem instead of ClwabackItem to simplify the database and 
    /// not having to pull the list of OrderLineItems in a ClawbackItem.
    /// Most ClawbackItems would only include a single Line Item anyways.
    /// </summary>
    public class ClawbackActionItem : OrderLineItem
    {
        public ClawbackActionItemState State { get; set; }
        public DateTime OrderRefundedDate { get; set; }
        public DateTime OrderPurchaseDate { get; set; }
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
        [JsonProperty("id")] public string UserId { get; set; }
        [JsonProperty("d")] public DateTime ConsumeDate { get; set; }
        [JsonProperty("q")] public uint ConsumedQuantity { get; set; }

        public ClawbackCandidate()
        {
            ConsumeDate = DateTime.MaxValue;
        }

        public ClawbackCandidate(ClawbackQueueItem QueueItem)
        {
            ConsumeDate      = QueueItem.ConsumeDate;
            TrackingId       = QueueItem.TrackingId;
            UserId           = QueueItem.UserId;
            ConsumedQuantity = QueueItem.Quantity;
        }
    }

    public enum ClawbackActionItemState
    {
        Building = 0,
        Pending,
        Running,
        Completed
    }
}
