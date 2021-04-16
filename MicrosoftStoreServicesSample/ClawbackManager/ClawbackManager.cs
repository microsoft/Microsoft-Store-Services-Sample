//-----------------------------------------------------------------------------
// ClawbackManager.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
//-----------------------------------------------------------------------------

using Microsoft.CorrelationVector;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.StoreServices;
using MicrosoftStoreServicesSample.PersistentDB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MicrosoftStoreServicesSample
{
    public class ClawbackManager
    {
        protected IConfiguration _config;
        protected IStoreServicesClientFactory _storeServicesClientFactory;
        protected ILogger _logger;

        public ClawbackManager(IConfiguration config,
                               IStoreServicesClientFactory storeServicesClientFactory,
                               ILogger logger)
        {
            _config = config;
            _storeServicesClientFactory = storeServicesClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Adds the information from a succeeded consume request to the database where we
        /// are tracking these transactions for possible refunds with the clawback service
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public async Task<bool> AddConsumeToClawbackQueueAsync(PendingConsumeRequest request, CorrelationVector cV)
        {
            var clawbackQueueItem = new ClawbackQueueItem(request);

            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    await dbContext.ClawbackQueue.AddAsync(clawbackQueueItem);
                    await dbContext.SaveChangesAsync();
                }
                _logger.AddConsumeToClawbackQueue(cV.Increment(),
                                   request.UserId,
                                   request.TrackingId,
                                   request.ProductId,
                                   request.RemoveQuantity);
            }
            catch (Exception e)
            {
                _logger.ServiceError(cV.Value, "Unable to add consume to the Clawback Queue" ,e);
            }

            return true;
        }

        /// <summary>
        /// Provides all the outstanding ClawbackQueueItems that we should run reconciliation on and check for
        /// any refunds that were issued.
        /// </summary>
        /// <param name="cV"></param>
        /// <returns></returns>
        public List<ClawbackQueueItem> GetClawbackQueue(CorrelationVector cV)
        {
            using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
            {
                return dbContext.ClawbackQueue.ToList();
            }
        }

        private async Task RemoveClawbackQueueItemAsync(ClawbackQueueItem clawbackItem, CorrelationVector cV)
        {
            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    dbContext.ClawbackQueue.Remove(clawbackItem);
                    await dbContext.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.ServiceWarning(cV.Value, $"Unable to remove ClawbackQueueItem with TrackingId {clawbackItem.TrackingId}", ex);
            }
        }

        private async Task RemoveClawbackQueueItemFromTrackingIdAsync(string trackingId, CorrelationVector cV)
        {
            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    var clwabackItem = new ClawbackQueueItem()
                    {
                        TrackingId = trackingId
                    };

                    dbContext.ClawbackQueue.Remove(clwabackItem);
                    await dbContext.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.ServiceWarning(cV.Value, $"Unable to remove ClawbackQueueItem with TrackingId {trackingId}", ex);
            }
        }

        /// <summary>
        /// This is the main logic that checks for any refunds within the past 90 days of all our fulfilled
        /// consumables.  This is an example of the flow that works with a small sample data set.  This code
        /// and the supporting functions would need to be updated to scale better to a larger data set.
        /// </summary>
        /// <param name="cV"></param>
        /// <returns></returns>
        public async Task<string> RunClawbackReconciliationAsync(CorrelationVector cV)
        {
            var response = new StringBuilder();
            var logMessage = "Starting Clawback Reconciliation";
            _logger.ServiceInfo(cV.Value, logMessage);
            response.AppendFormat("INFO: {0}\n", logMessage);           

            //  Get every Clawback Queue entry
            var clawbackQueue = GetClawbackQueue(cV);
            
            logMessage = $"{clawbackQueue.Count} items found in the ClawbackQueue";
            _logger.ServiceInfo(cV.Value, logMessage);
            response.AppendFormat("INFO: {0}\n", logMessage);

            foreach (var clawbackQueueItem in clawbackQueue)
            {
                //  Step 1:
                //  Check if the queue item is older than 90 days, if it is, remove it and go to the next
                if (DateTime.UtcNow > clawbackQueueItem.ConsumeDate.AddDays(90))
                {
                    logMessage = $"Item {clawbackQueueItem.TrackingId} is older than 90 days, removing from the ClawbackQueue";
                    _logger.ServiceInfo(cV.Value, logMessage);
                    response.AppendFormat("INFO: {0}\n", logMessage);
                    await RemoveClawbackQueueItemAsync(clawbackQueueItem, cV);
                }
                else
                {
                    //  Step 2:
                    //  Call the clawback service for this UserPurchaseId
                    var clawbackResults = new ClawbackQueryResponse();
                    using (var storeClient = _storeServicesClientFactory.CreateClient())
                    {
                        //  Check if the UserPurchaseId has expired, if so, refresh it
                        var userPurchaseId = new UserStoreId(clawbackQueueItem.UserPurchaseId);
                        if (DateTime.UtcNow > userPurchaseId.Expires)
                        {
                            var serviceToken = await storeClient.GetServiceAccessTokenAsync();
                            await userPurchaseId.RefreshStoreId(serviceToken.Token);
                        }

                        //  Create the request with the Revoked and Refunded filters
                        //  to omit active entitlements that have not been refunded
                        var clawbackRequest = new ClawbackQueryRequest()
                        {
                            UserPurchaseId = userPurchaseId.Key,
                            LineItemStateFilter = new List<string>() { LineItemStates.Revoked, LineItemStates.Refunded }
                        };

                        //  Make the request 
                        clawbackResults = await storeClient.ClawbackQueryAsync(clawbackRequest);
                    }

                    logMessage = $"{clawbackResults.Items.Count} items found for ClawbackItem {clawbackQueueItem.TrackingId}";
                    _logger.ServiceInfo(cV.Value, logMessage);
                    response.AppendFormat("INFO: {0}\n", logMessage);

                    //  Step 3:
                    //  Check each orderLineItem in each clawback item returned to
                    //  identify if it is new or if we have already acted on this
                    //  one in a previous call
                    foreach (var item in clawbackResults.Items)
                    {
                        logMessage = $"OrderId {item.OrderId} has {item.OrderLineItems.Count} LineItems";
                        _logger.ServiceInfo(cV.Value, logMessage);
                        response.AppendFormat("INFO: {0}\n", logMessage);

                        foreach (var orderLineItem in item.OrderLineItems)
                        {
                            ClawbackActionItem actionItem = null;
                            //  Step 3.a:
                            //  If there is a product that is Revoked or Refunded in the results, check if we have already
                            //  taken action based on it's OrderId.
                            try
                            {
                                var existingActionItems = new List<ClawbackActionItem>();
                                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                                {
                                    //  We should have a unique LineItemId
                                    existingActionItems = dbContext.ClawbackActionItems.Where(
                                        b => b.LineItemId == orderLineItem.LineItemId
                                        ).ToList();
                                }

                                if (existingActionItems.Count == 1)
                                {
                                    actionItem = existingActionItems.First();
                                }
                                else if (existingActionItems.Count > 1)
                                {
                                    logMessage = $"Unexpected number of ClawbackActionItems in DB for {orderLineItem.LineItemId}, found {existingActionItems.Count}";
                                    _logger.ServiceWarning(cV.Value, logMessage, null);
                                    response.AppendFormat("WARNING: {0}\n", logMessage);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.ServiceWarning(cV.Value, "Error searching for existing ClawbackActionItems", ex);
                                response.AppendFormat("Warning: Error searching for existing ClawbackActionItems {0}\n", ex.Message);
                            }

                            if (actionItem == null)
                            {
                                //  This item doesn't exist or there was an error getting it
                                //  Try to create and add this to the database
                                try
                                {
                                    //  Step 3.b:
                                    //  Add this to our tracking database of items we have seen and
                                    //  have or will take action on this cycle.
                                    actionItem = new ClawbackActionItem(clawbackQueueItem,
                                                                        item,
                                                                        orderLineItem);

                                    if (orderLineItem.LineItemState.Equals(LineItemStates.Refunded))
                                    {
                                        //  Refunded - No action needed on our server side.  The item's quantity was deducted from the 
                                        //             user's store balance in the Collections service before our service consumed the 
                                        //             product.  But we can still log this if wanted.
                                        logMessage = $"LineItemId {orderLineItem.LineItemId} from OrderId {item.OrderId} has a state of Refunded.  No additional action should be needed for this item";
                                        _logger.ServiceInfo(cV.Value, logMessage);
                                        response.AppendFormat("INFO: {0}\n", logMessage);

                                        //  Add this item to the Clawback action database as Completed if it doesn't already exist because
                                        //  no further action is needed other than to avoid this in future queries.
                                        actionItem.State = ClawbackActionItemState.Completed;
                                    }
                                    else if (orderLineItem.LineItemState.Equals(LineItemStates.Revoked))
                                    {
                                        //  Revoked  - Action needed on our server side to remove balance from our own tracked quantity
                                        //             of the user.  User got a refund, but the balance on the Collections service was
                                        //             not enough to cover the refunded balance in their quantity.  Therefore we need to
                                        //             revoke quantity or take proper action on our own server side to remove those items
                                        logMessage = $"LineItemId {orderLineItem.LineItemId} from OrderId {item.OrderId} has a state of Revoked.  Adding to the ClawbackActionItem database.";
                                        _logger.ServiceInfo(cV.Value, logMessage);
                                        response.AppendFormat("INFO: {0}\n", logMessage);

                                        //  Add this item to the Clawback action database as Building if it doesn't already exist, if it already exists, add the user's
                                        //  Id to the list of possible accounts where the action would need to be taken
                                        actionItem.State = ClawbackActionItemState.Building;
                                    }

                                    //  Write it to the database
                                    using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                                    {
                                        //  We should have a unique LineItemId
                                        await dbContext.ClawbackActionItems.AddAsync(actionItem);
                                        await dbContext.SaveChangesAsync();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logMessage = $"Error adding ClawbackActionItem for LineItemId {orderLineItem.LineItemId} from OrderId {item.OrderId} : {ex.Message}";
                                    _logger.ServiceWarning(cV.Value, logMessage, ex);
                                    response.AppendFormat("WARNING: {0}\n", logMessage);
                                }
                            }
                            else
                            {
                                //  Step 3.c:
                                //  There is already a tracking Item for this clawback, we need check its status.  If Building, 
                                //  we need to add the UserId and consume tracking info from the ClawbackQueue to the
                                //  the clawback candidates list.  Later we will go through the list (in case there are multiple
                                //  users tied to the same UserPurchaseId which is possible with PC store scenarios) and determine
                                //  which consume best matches the purchase info to clawback the items.
                                if (actionItem.State == ClawbackActionItemState.Building)
                                {
                                    logMessage = $"Adding Candidate for LineItemId {actionItem.LineItemId} from OrderId {actionItem.OrderId} : {clawbackQueueItem.UserId} | {clawbackQueueItem.TrackingId}";
                                    _logger.ServiceInfo(cV.Value, logMessage);
                                    response.AppendFormat("INFO: {0}\n", logMessage);

                                    try
                                    {
                                        //  Save modified entity using new Context
                                        using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                                        {
                                            //  Mark entity as modified to save the changes we made outside of the dbContext
                                            var existingActionItem = dbContext.ClawbackActionItems.SingleOrDefault(
                                                b => b.LineItemId == actionItem.LineItemId
                                                );
                                            var candidates = existingActionItem.GetClawbackCandidates();

                                            //  Verify that this action item is not already marked on the list of  candidates
                                            var candidate = new ClawbackCandidate(clawbackQueueItem);
                                            if (!candidates.Any( b => b.TrackingId == candidate.TrackingId))
                                            {
                                                candidates.Add(candidate);
                                                existingActionItem.SetCandidatesJSON(candidates);
                                                await dbContext.SaveChangesAsync();
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        //  If the status is Completed or Pending, then we don't need to add this user and can ignore it.
                                        logMessage = $"Error adding candidate for LineItemId {orderLineItem.LineItemId} from OrderId {item.OrderId} : {ex.Message}";
                                        _logger.ServiceWarning(cV.Value, logMessage, ex);
                                        response.AppendFormat("WARNING: {0}\n", logMessage);
                                    }
                                }
                                else
                                {
                                    //  If the status is Completed or Pending, then we don't need to add this user and can ignore it.
                                    logMessage = $"LineItemId {actionItem.LineItemId} from OrderId {actionItem.OrderId} has state of {actionItem.State}.  No additional action being taken.";
                                    _logger.ServiceInfo(cV.Value, logMessage);
                                    response.AppendFormat("INFO: {0}\n", logMessage);
                                }
                            }
                        }
                    }
                }
            }

            //  Step 4:
            //  We are now done "Building" the list and need to change all Building -> Pending states for our action items
            using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
            {
                var builtItems = dbContext.ClawbackActionItems.Where(b => b.State == ClawbackActionItemState.Building).ToList();
                foreach (var item in builtItems)
                {
                    item.State = ClawbackActionItemState.Pending;
                }
                await dbContext.SaveChangesAsync();
            }

            //  Step 5:
            //  Get all items in the Pending Clawback state (this may include some that were not included in Step 4,
            //  so we do a new query rather than just relying on that list.  
            var pendingItems = new List<ClawbackActionItem>();
            using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
            {
                pendingItems = dbContext.ClawbackActionItems.Where(
                    b => b.State == ClawbackActionItemState.Pending
                    ).ToList();
            }

            //  Step 6:
            //  Go through each pending item, find the best candidate (UserId) for who received the consumable credit tied
            //  to the refund, then claw back that value.
            foreach (var pendingItem in pendingItems)
            {
                //  Step 6.a:
                //  Find the best candidate for the clawback
                //  Check the full list of UserIds who had this item show up against, find the consume date that
                //  most closely matches the purchase date of the item that was refunded.  This is not 100% accurate
                //  but will be the best guess as to which consume matches up to the account who consumed the items
                //  that were refunded.
                ClawbackCandidate bestCandidate = null;
                TimeSpan bestCandiateTimeSpan = TimeSpan.MaxValue;
                var candidates = pendingItem.GetClawbackCandidates();
                foreach (var candidate in candidates)
                {
                    //  Make sure the purchase date is before the consume date or else
                    //  we can ignore this candidate
                    if (pendingItem.OrderPurchaseDate < candidate.ConsumeDate)
                    {
                        //  Calculate the time span between purchase and this candidate's consume 
                        var candiateTimeSpan = candidate.ConsumeDate - pendingItem.OrderPurchaseDate;

                        //  Check if this candidate's time span is shorter than our previous best
                        //  candidate.  If so, then make this our best candidate for the clawback
                        if (candiateTimeSpan < bestCandiateTimeSpan)
                        {
                            bestCandiateTimeSpan = candiateTimeSpan;
                            bestCandidate = candidate;
                        }
                    }
                }

                //  Step 6.b:
                //  We now have our best candidate and we can take action to claw back the value of the
                //  item that was refunded to them
                {
                    //  TODO: Take action by clawing back balance, items, or whatever you determine to be 
                    //        the appropriate action based on this item that the user got a refund for.
                    var consumeManager = new ConsumableManager(_config, _storeServicesClientFactory, _logger);
                    var newBalance = await consumeManager.RevokeUserConsumableValue(bestCandidate.UserId, pendingItem.ProductId, (int)bestCandidate.ConsumedQuantity, cV);

                    logMessage = $"LineItemId {pendingItem.LineItemId} best candidate: User {bestCandidate.UserId} consumed {bestCandidate.ConsumedQuantity} on {bestCandidate.ConsumeDate}.  Removed consumed quantity, {bestCandidate.UserId}'s remaining balance is now {newBalance}";
                    _logger.ServiceInfo(cV.Value, logMessage);
                    response.AppendFormat("INFO: {0}\n", logMessage);
                }

                //  Step 6.c:
                //  Update the ClawbackActionItem to be state Completed and replace the candidate list
                //  with the one candidate we actually took action against for record keeping
                candidates.Clear();
                candidates.Add(bestCandidate);
                pendingItem.SetCandidatesJSON(candidates);
                pendingItem.State = ClawbackActionItemState.Completed;

                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    //  Mark entity as modified to save the changes we made outside of the dbContext
                    dbContext.Entry(pendingItem).State = EntityState.Modified;
                    await dbContext.SaveChangesAsync();
                }

                //  Step 6.d:
                //  Remove the consume that we determined was our candidate from future Clawback queue searches
                await RemoveClawbackQueueItemFromTrackingIdAsync(bestCandidate.TrackingId, cV);
            }

            return response.ToString();
        }
    }
}
