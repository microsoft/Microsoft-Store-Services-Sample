//-----------------------------------------------------------------------------
// ClawbackManager.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License file under the project root for
// license information.
//-----------------------------------------------------------------------------

using Microsoft.CorrelationVector;
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
        /// <param name="request">Pending consume request that we will take the UserPurchaseId and UserId from</param>
        /// <param name="singlePurchasingAccount">Indicates this is a single-purchasing account so we only need one UserPurchaseId cached for the user</param>
        /// <returns></returns>
        public async Task<bool> AddUserPurchaseIdToClawbackQueue(PendingConsumeRequest request, bool singlePurchasingAccount, CorrelationVector cV)
        {
            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    ClawbackQueueItem item;

                    //  Check if there is already a tracked UserPurchaseId for this user if single purchasing account,
                    if (singlePurchasingAccount && dbContext.ClawbackQueue.Where(b => b.UserId == request.UserId).Any())
                    {   
                        //  Update the existing item to have the latest date of consume and UserPurchaseId
                        item = dbContext.ClawbackQueue.Find(request.UserId);
                        item.UserPurchaseId = request.UserPurchaseId;
                        item.ConsumeDate = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        //  This item either doesn't already exist if single purchasing
                        //  or is a multi-purchasing flagged account and needs a unique
                        //  Id per transaction.
                        item = new ClawbackQueueItem(request, singlePurchasingAccount);
                        await dbContext.ClawbackQueue.AddAsync(item);
                    }

                    await dbContext.SaveChangesAsync();
                }
                _logger.AddUserPurchaseIdToClawbackQueue(cV.Increment(),
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

        private async Task RemoveQueueItemAsync(ClawbackQueueItem queueItem, CorrelationVector cV)
        {
            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    dbContext.ClawbackQueue.Remove(queueItem);
                    await dbContext.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.ServiceWarning(cV.Value, $"Unable to remove ClawbackQueueItem for {queueItem.UserId}", ex);
            }
        }

        /// <summary>
        /// Update the Queue item's PurchaseId to a refreshed one
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="newKeyPurchaseId"></param>
        /// <param name="cV"></param>
        /// <returns></returns>
        private async Task UpdateQueueItemPurchaseId(String userId, String newKeyPurchaseId, CorrelationVector cV)
        {
            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    //  We should have a unique LineItemId
                    var item = dbContext.ClawbackQueue.Find(userId);
                    
                    if (item != null)
                    {
                        item.UserPurchaseId = newKeyPurchaseId;
                        await dbContext.SaveChangesAsync();
                    }
                    else
                    {
                        _logger.ServiceWarning(cV.Value, $"Unable to remove CompletedConsumeTransaction with TrackingId {userId}", null);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ServiceWarning(cV.Value, $"Unable to update the UserPurchaseId for TrackingId {userId}", ex);
            }
        }


        ///////
        /// todo:Cagood
        /// Update this to have seperate single-purchasing-account and multi-purchasing-accounts APIs supported
        /// /////////////////////////////


        /// <summary>
        /// This is the main logic that checks for any refunds within the past 90 days for each of entries in
        /// the ClawbackQueue database.  For Xbox (single-purchasing accounts) we only need to do one call to
        /// Clawback per-user.  For PC if the title supports multi-purchasing accounts per-user we need to 
        /// call Clawback with each UserPurchaseId that was generated at the same time as the
        /// UserCollectionsId used to make the consume request (because the user in our database may not be
        /// the account in the Microsoft store that made the purchase).  The logic to handle both of those
        /// is controlled by the ConsumeAsync API.  
        /// 
        /// This is an example of the flow that works with a small sample data set.  This code
        /// and the supporting functions would need to be updated to scale better to a larger data set.
        /// </summary>
        /// <param name="cV"></param>
        /// <returns></returns>
        public async Task<string> RunClawbackReconciliationAsync(CorrelationVector cV)
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

            foreach (var currentQueueItem in clawbackQueue)
            {
                //  Step 1:
                //  Check if the queue item is older than 90 days, if it is, remove it and go to the next.
                if (DateTimeOffset.UtcNow > currentQueueItem.ConsumeDate.AddDays(90))
                {
                    logMessage = $"Queue item for {currentQueueItem.UserId} is older than 90 days, removing from the ClawbackQueue";
                    _logger.ServiceInfo(cV.Value, logMessage);
                    response.AppendFormat("INFO: {0}\n", logMessage);
                    await RemoveQueueItemAsync(currentQueueItem, cV);
                }
                else
                {
                    //  Step 2:
                    //  Call the clawback service for this UserPurchaseId
                    var clawbackResults = new ClawbackQueryResponse();
                    using (var storeClient = _storeServicesClientFactory.CreateClient())
                    {
                        //  Check if the UserPurchaseId needs to be refreshed
                        var userPurchaseId = new UserStoreId(currentQueueItem.UserPurchaseId);
                        if (DateTimeOffset.UtcNow > userPurchaseId.RefreshAfter)
                        {
                            var serviceToken = await storeClient.GetServiceAccessTokenAsync();
                            await userPurchaseId.RefreshStoreId(serviceToken.Token);
                            //  Update the CompletedConsumeTransaction with the new Token
                            await UpdateQueueItemPurchaseId(currentQueueItem.UserId, userPurchaseId.Key, cV);
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

                    logMessage = $"{clawbackResults.Items.Count} items found for {currentQueueItem.UserId}";
                    _logger.ServiceInfo(cV.Value, logMessage);
                    response.AppendFormat("INFO: {0}\n", logMessage);

                    //  Step 3:
                    //  Check each clawback item, pull out the list of orderLineItems to
                    //  take action on each one.
                    foreach (var clawbackItem in clawbackResults.Items)
                    {
                        logMessage = $"OrderId {clawbackItem.OrderId} has {clawbackItem.OrderLineItems.Count} LineItems";
                        _logger.ServiceInfo(cV.Value, logMessage);
                        response.AppendFormat("INFO: {0}\n", logMessage);

                        //  Check each orderLineItem to see if we have a record of
                        //  consuming it and filter out any that we have already
                        //  reconciled
                        foreach (var orderLineItem in clawbackItem.OrderLineItems)
                        {
                            var matchingConsumeTransactions = new List<CompletedConsumeTransaction>();
                            try
                            {
                                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                                {
                                    //  Look for any items that have a matching OrderID, OrderLineItemID,
                                    //  and are not yet reconciled
                                    matchingConsumeTransactions = dbContext.CompletedConsumeTransactions.Where(
                                        b => b.OrderId == clawbackItem.OrderId &&
                                        b.OrderLineItemId == orderLineItem.LineItemId &&
                                        b.TransactionStatus != CompletedConsumeTransactionState.Reconciled).ToList();
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.ServiceWarning(cV.Value, "Error searching for existing CompletedConsumeTransactions", ex);
                                response.AppendFormat("Warning: Error searching for existing CompletedConsumeTransactions {0}\n", ex.Message);
                            }

                            //  Log the results that we got so far
                            if (matchingConsumeTransactions.Count == 0)
                            {
                                logMessage = $"No unreconciled transactions found for OrderId: {clawbackItem.OrderId} OrderLineItemId: {orderLineItem.LineItemId}";
                                _logger.ServiceInfo(cV.Value, logMessage);
                                response.AppendFormat("INFO: {0}\n", logMessage);
                            }
                            else
                            {
                                logMessage = $"Found {matchingConsumeTransactions.Count} matching unreconciled transactions for OrderId: {clawbackItem.OrderId} OrderLineItemId: {orderLineItem.LineItemId}";
                                _logger.ServiceInfo(cV.Value, logMessage);
                                response.AppendFormat("INFO: {0}\n", logMessage);

                                //  Step 4:
                                //  Take action on each of the transactions we found
                                var consumeManager = new ConsumableManager(_config, _storeServicesClientFactory, _logger);
                                foreach (var transaction in matchingConsumeTransactions)
                                {
                                    //  TODO: Take action by clawing back balance, items, or whatever you determine to be 
                                    //        the appropriate action based on this item that the user got a refund for.
                                    var newBalance = await consumeManager.RevokeUserConsumableValue(transaction.UserId,    // User who was granted the credit of the transaction
                                                                                                     transaction.ProductId, // ProductId the user was granted
                                                                                                     (int)transaction.QuantityConsumed, // How much they received from this transaction
                                                                                                     cV);

                                    logMessage = $"Removed {transaction.QuantityConsumed} of {transaction.ProductId} from user {transaction.UserId}.  Remaining balance: {newBalance}";
                                    _logger.ServiceInfo(cV.Value, logMessage);
                                    response.AppendFormat("INFO: {0}\n", logMessage);

                                    //  Mark this transaction as Reconciled so we don't process it again
                                    await consumeManager.UpdateCompletedConsumeTransactionState(transaction.DbKey, CompletedConsumeTransactionState.Reconciled, cV);
                                }
                            }
                        }
                    }
                }        
            }
            return response.ToString();
        }
    }
}
