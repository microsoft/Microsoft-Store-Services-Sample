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
using Microsoft.StoreServices.Clawback.V1;
using MicrosoftStoreServicesSample.PersistentDB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MicrosoftStoreServicesSample
{
    public class ClawbackV1Manager
    {
        protected IConfiguration _config;
        protected IStoreServicesClientFactory _storeServicesClientFactory;
        protected ILogger _logger;

        public ClawbackV1Manager(IConfiguration config,
                               IStoreServicesClientFactory storeServicesClientFactory,
                               ILogger logger)
        {
            _config = config;
            _storeServicesClientFactory = storeServicesClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Adds the purchaseId from a successful consume to the database so we can use
        /// it to call the Clawback and check if there are refunds issued to this transaction
        /// for the next 90 days.
        /// 
        /// NOTE - Single-purchasing (Xbox) vs multi-purchasing account (Windows PC) support
        /// 
        /// Single-purchasing account support:
        /// On the Xbox console, all store consumable purchases are tied to the Xbox Live 
        /// account actively playing your title.  We call these single-purchasing accounts
        /// and for calling Clawback,  we only need to do one call per-user to see all their
        /// refunded items.  Therefore only need to cache one UserPurchaseId in the queue for
        /// these accounts.  In this case, the database key value will be our system's UserId
        /// for each individual user.
        /// 
        /// Multi-purchasing account support:
        /// Alternatively on a Windows PC, the account signed into the store app is the 
        /// account that will make the purchases of consumable products.  The UserStoreIds generated
        /// on the PC client at that time are tied to the Microsoft Account signed into the store app
        /// and may or may not be the same Microsoft Account signed into Xbox Live and represents the
        /// active XBL account in your title.  Therefore, in this scenario there may be multiple
        /// accounts making purchases for a single UserId within our title service.  If supporting
        /// this flow, we cannot rely on a single UserPurchaseId in our ClawbackQueue for each user
        /// in our system.  We must instead create a Clawback queue item for each consume transaction
        /// that succeeds so we can check for that specific account's refunds.  This means we will be
        /// making repeated calls for the same user the UserPurchaseId represents, but it guarantees
        /// we can still check for any refunds related to consume transactions we have completed. In
        /// this case, the database key value will be a uniquely generated GUID and not tied to the
        /// UserId at all.
        /// 
        /// You can configure your Windows PC title to require users to sign into the Windows Store
        /// app with the same MSA as the XBL account they are using to play your title.  That would
        /// then allow you to treat these as single-purchasing accounts.  The decision is up to you
        /// and what is best for your Windows PC community.  See more information in the GDK
        /// documentation article "Handling mismatched store account scenarios on PC"
        /// https://docs.microsoft.com/en-us/gaming/gdk/_content/gc/commerce/pc-specific-considerations/xstore-handling-mismatched-store-accounts
        /// </summary>
        /// <param name="request">Pending consume request that we will take the UserPurchaseId and UserId from</param>
        /// <param name="isSinglePurchasingAccount">Indicates this is a single-purchasing account or multi-purchasing account (see summary notes)r</param>
        /// <param name="cV"></param>
        /// <returns></returns>
        public async Task<bool> AddUserPurchaseIdToClawbackQueue(PendingConsumeRequest request, bool isSinglePurchasingAccount, CorrelationVector cV)
        {
            ClawbackV1QueueItem item;
            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {


                    //  If this is a single-purchasing account check if we already have a
                    //  cached purchaseId
                    if (isSinglePurchasingAccount &&
                        dbContext.ClawbackQueue.Where(b => b.DbKey == request.UserId).Any())
                    {   
                        //  Update the existing item to have the latest date of consume
                        //  and UserPurchaseId
                        item = dbContext.ClawbackQueue.Find(request.UserId);
                        item.UserPurchaseId = request.UserPurchaseId;
                        item.ConsumeDate = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        //  This item either doesn't already exist if single-purchasing
                        //  or is a multi-purchasing account and needs a unique Id per
                        //  transaction.
                        item = new ClawbackV1QueueItem(request, isSinglePurchasingAccount);
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
        public List<ClawbackV1QueueItem> GetClawbackQueue(CorrelationVector cV)
        {
            using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
            {
                return dbContext.ClawbackQueue.ToList();
            }
        }

        private async Task RemoveQueueItemAsync(ClawbackV1QueueItem queueItem, CorrelationVector cV)
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
                _logger.ServiceWarning(cV.Value, $"Unable to remove ClawbackQueueItem for {queueItem.DbKey}", ex);
            }
        }

        /// <summary>
        /// Update the Clawback queue item's PurchaseId to a refreshed one
        /// </summary>
        /// <param name="itemKey">User Id in our system if supporting single-purchasing accounts, unique GUID created when item was first added if supporting multi-purchasing accounts </param>
        /// <param name="newUserPurchaseId">Refreshed UserPurchaseId</param>
        /// <param name="cV"></param>
        /// <returns></returns>
        private async Task UpdateQueueItemPurchaseId(String itemKey, String newUserPurchaseId, CorrelationVector cV)
        {
            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    //  We should have a unique LineItemId
                    var item = dbContext.ClawbackQueue.Find(itemKey);
                    
                    if (item != null)
                    {
                        item.UserPurchaseId = newUserPurchaseId;
                        await dbContext.SaveChangesAsync();
                    }
                    else
                    {
                        _logger.ServiceWarning(cV.Value, $"Unable to remove CompletedConsumeTransaction with TrackingId {itemKey}", null);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ServiceWarning(cV.Value, $"Unable to update the UserPurchaseId for TrackingId {itemKey}", ex);
            }
        }

        /// <summary>
        /// This is the main logic that checks for any refunds within the past 90 days for each of entries in
        /// the Clawback Queue database.  The difference between single and multi-purchasing accounts in our
        /// system is controlled by the ConsumeAsync API, please see that API for more information on how
        /// these are different and recorded into the Clawback Queue.
        /// 
        /// NOTE: This is an example of the flow that works with a small sample data set.  This code
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
                    logMessage = $"Queue item for {currentQueueItem.DbKey} is older than 90 days, removing from the ClawbackQueue";
                    _logger.ServiceInfo(cV.Value, logMessage);
                    response.AppendFormat("INFO: {0}\n", logMessage);
                    await RemoveQueueItemAsync(currentQueueItem, cV);
                }
                else
                {
                    //  Step 2:
                    //  Call the clawback service for this UserPurchaseId
                    var clawbackResults = new ClawbackV1QueryResponse();
                    using (var storeClient = _storeServicesClientFactory.CreateClient())
                    {
                        //  Check if the UserPurchaseId needs to be refreshed
                        var userPurchaseId = new UserStoreId(currentQueueItem.UserPurchaseId);
                        if (DateTimeOffset.UtcNow > userPurchaseId.RefreshAfter)
                        {
                            var serviceToken = await storeClient.GetServiceAccessTokenAsync();
                            await userPurchaseId.RefreshStoreId(serviceToken.Token);
                            //  Update the CompletedConsumeTransaction with the new Token
                            await UpdateQueueItemPurchaseId(currentQueueItem.DbKey, userPurchaseId.Key, cV);
                        }

                        //  Create the request with the Revoked filter to omit
                        //  active entitlements that have not been refunded and
                        //  refunded items that our service did not consume yet
                        var clawbackRequest = new ClawbackV1QueryRequest()
                        {
                            UserPurchaseId = userPurchaseId.Key,
                            LineItemStateFilter = new List<string>() { LineItemStates.Revoked }
                        };

                        //  Make the request 
                        clawbackResults = await storeClient.ClawbackV1QueryAsync(clawbackRequest);
                    }

                    logMessage = $"{clawbackResults.Items.Count} items found for {currentQueueItem.DbKey}";
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
