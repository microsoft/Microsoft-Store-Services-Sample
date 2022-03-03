//-----------------------------------------------------------------------------
// ClawbackManager.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License file under the project root for
// license information.
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
        /// Adds the purchaseId from a successful consume to the database so we can use
        /// it to call the Clawback and check if there were refunds issues to this user
        /// for the next 90 days.
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        public async Task<bool> AddUserPurchaseIdToClawbackQueue(PendingConsumeRequest request, CorrelationVector cV)
        {
            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    //  Check if there is already a tracked UserPurchaseId for this user,
                    //  since this sample is setup for a single-purchasing account per user
                    //  we only need to keep one UserPurchaseId to see the user's refunds
                    //  from the last 30 days.
                    //  NOTE: if your service is supporting multi-purchasing account scenarios
                    //  you would need to save each UserPurchaseId and to use a GUID as the
                    //  UserID in the database.
                    var item = dbContext.ClawbackQueue.Find(request.UserId);
                    if (item == null)
                    {
                        //  This doesn't exist yet so we need to create it
                        item = new ClawbackQueueItem(request);
                        dbContext.Add(item);
                    }
                    else
                    {
                        item.UserPurchaseId = request.UserPurchaseId;
                    }

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
        /// Provides all the outstanding completedConsumeTransactions that we should run reconciliation on and check for
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

        //  todo-cagood - is this needed anymore?
        private async Task RemoveCompletedConsumeTransactionAsync(CompletedConsumeTransaction clawbackItem, CorrelationVector cV)
        {
            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    dbContext.CompletedConsumeTransactions.Remove(clawbackItem);
                    await dbContext.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.ServiceWarning(cV.Value, $"Unable to remove CompletedConsumeTransaction with TrackingId {clawbackItem.TrackingId}", ex);
            }
        }

        private async Task RemoveCompletedConsumeTransactionFromTrackingIdAsync(string trackingId, CorrelationVector cV)
        {
            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    var clwabackItem = new CompletedConsumeTransaction()
                    {
                        TrackingId = trackingId
                    };

                    dbContext.ClawbackQueue.Remove(clwabackItem);
                    await dbContext.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.ServiceWarning(cV.Value, $"Unable to remove CompletedConsumeTransaction with TrackingId {trackingId}", ex);
            }
        }

        /// <summary>
        /// Update the Queue item's PurchaseId to a refreshed one
        /// </summary>
        /// <param name="TrackingId"></param>
        /// <param name="NewKeyPurchaseId"></param>
        /// <param name="cV"></param>
        /// <returns></returns>
        private async Task UpdateCompletedConsumeTransactionPurchaseId(String TrackingId, String NewKeyPurchaseId, CorrelationVector cV)
        {
            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    //  We should have a unique LineItemId
                    var item = dbContext.ClawbackQueue.Find(TrackingId);
                    
                    if (item != null)
                    {
                        item.UserPurchaseId = NewKeyPurchaseId;
                        await dbContext.SaveChangesAsync();
                    }
                    else
                    {
                        _logger.ServiceWarning(cV.Value, $"Unable to remove CompletedConsumeTransaction with TrackingId {TrackingId}", null);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ServiceWarning(cV.Value, $"Unable to update the UserPurchaseId for TrackingId {TrackingId}", ex);
            }
        }


        ///////
        /// todo:Cagood
        /// Update this to have seperate single-purchasing-account and multi-purchasing-accounts APIs supported
        /// /////////////////////////////
        

        /// <summary>
        /// This is the main logic that checks for any refunds within the past 90 days for each of the users
        /// in our database.  This is an example of the flow that works with a small sample data set.  This code
        /// and the supporting functions would need to be updated to scale better to a larger data set.
        /// </summary>
        /// <param name="cV"></param>
        /// <returns></returns>
        public async Task<string> RunClawbackReconciliationForSinglePurchasingAccountsAsync(CorrelationVector cV)
        {
            var response = new StringBuilder();
            var logMessage = "Starting Clawback Reconciliation for single-purchasing accounts (Xbox standard)";
            _logger.ServiceInfo(cV.Value, logMessage);
            response.AppendFormat("INFO: {0}\n", logMessage);           

            //  Get the UserPurchaseID from each user to call Clawback on their behalf
            var clawbackQueue = GetClawbackQueue(cV);
            
            logMessage = $"{clawbackQueue.Count} items found in the ClawbackQueue";
            _logger.ServiceInfo(cV.Value, logMessage);
            response.AppendFormat("INFO: {0}\n", logMessage);

            foreach (var completedConsumeTransaction in clawbackQueue)
            {
                //  Step 1:
                //  Check if the queue item is older than 90 days, if it is, remove it and go to the next.
                //  Optionally, you could hold onto these for longer if you plan to get reports for 
                //  charge-back transactions that can take longer than 90 days to resolve.  But if older than
                //  90 days you don't need to call Clawback for this item.
                if (DateTimeOffset.UtcNow > completedConsumeTransaction.ConsumeDate.AddDays(90))
                {
                    logMessage = $"Item {completedConsumeTransaction.TrackingId} is older than 90 days, removing from the ClawbackQueue";
                    _logger.ServiceInfo(cV.Value, logMessage);
                    response.AppendFormat("INFO: {0}\n", logMessage);
                    await RemoveCompletedConsumeTransactionAsync(completedConsumeTransaction, cV);
                }
                else
                {
                    //  Step 2:
                    //  Call the clawback service for this UserPurchaseId
                    var clawbackResults = new ClawbackQueryResponse();
                    using (var storeClient = _storeServicesClientFactory.CreateClient())
                    {
                        //  Check if the UserPurchaseId is older the refresh window, if so, refresh it
                        var userPurchaseId = new UserStoreId(completedConsumeTransaction.UserPurchaseId);
                        if (DateTimeOffset.UtcNow > userPurchaseId.RefreshAfter)
                        {
                            var serviceToken = await storeClient.GetServiceAccessTokenAsync();
                            await userPurchaseId.RefreshStoreId(serviceToken.Token);
                            //  Update the CompletedConsumeTransaction with the new Token
                            await UpdateCompletedConsumeTransactionPurchaseId(completedConsumeTransaction.TrackingId, userPurchaseId.Key, cV);
                        }

                        //  Create the request with the Revoked filter to omit
                        //  active entitlements that have not been refunded and
                        //  refunded items that our service did not consume yet
                        var clawbackRequest = new ClawbackQueryRequest()
                        {
                            UserPurchaseId = userPurchaseId.Key,
                            LineItemStateFilter = new List<string>() { LineItemStates.Revoked }
                        };

                        //  Make the request 
                        clawbackResults = await storeClient.ClawbackQueryAsync(clawbackRequest);
                    }

                    logMessage = $"{clawbackResults.Items.Count} items found for ClawbackItem {completedConsumeTransaction.TrackingId}";
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
                            //  If there is a product that is Revoked in the results, check if we have already
                            //  taken action based on it's OrderId and OrderLineItemId
                            try
                            {
                                var existingActionItems = new List<ClawbackActionItem>();
                                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                                {
                                    //  Look for any items that have a matching OrderID and OrderLineItemID
                                    existingActionItems = dbContext.ClawbackActionItems.Where(
                                        b => b.OrderId == item.OrderId &&
                                        b.LineItemId == orderLineItem.LineItemId).ToList();
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
                                    actionItem = new ClawbackActionItem(completedConsumeTransaction,
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
                                    logMessage = $"Adding Candidate for LineItemId {actionItem.LineItemId} from OrderId {actionItem.OrderId} : {completedConsumeTransaction.UserId} | {completedConsumeTransaction.TrackingId}";
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
                                            var candidate = new ClawbackCandidate(completedConsumeTransaction);
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
                await RemoveCompletedConsumeTransactionFromTrackingIdAsync(bestCandidate.TrackingId, cV);
            }

            return response.ToString();
        }
    }
}
