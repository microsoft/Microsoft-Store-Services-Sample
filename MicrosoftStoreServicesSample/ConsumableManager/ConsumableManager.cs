//-----------------------------------------------------------------------------
// ConsumbleManager.cs
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
using Microsoft.StoreServices.Collections;
using Microsoft.StoreServices.Collections.V8;
using MicrosoftStoreServicesSample.PersistentDB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace MicrosoftStoreServicesSample
{

    /// <summary>
    /// Example of a class provided to manage consumable transactions from the title's services.
    /// </summary>
    public class ConsumableManager
    {
        private readonly IConfiguration _config;
        private readonly IStoreServicesClientFactory _storeServicesClientFactory;
        private readonly ILogger _logger;

        public ConsumableManager(IConfiguration config,
                                 IStoreServicesClientFactory storeServicesClientFactory,
                                 ILogger logger)
        {
            _config = config;
            _storeServicesClientFactory = storeServicesClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Performs the actual consume request.  This code is used by both a normal consume and also a
        /// pending consume retry.  Will successfully remove the consume from the pending queue if 
        /// a retry is required.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="cV"></param>
        /// <returns></returns>
        public async Task<string> ConsumeAsync(PendingConsumeRequest request, CorrelationVector cV)
        {
            //  This will cache the request into the pending consume list if
            //  it is not already being tracked.
            await TrackPendingConsumeAsync(request, cV);

            CollectionsV8ConsumeResponse consumeResult;
            var consumeRequest = await CreateConsumeRequestFromPendingRequestAsync(request);

            string response;
            //  Send the request to the Collections service using the 
            //  StoreServicesClient from our factory.
            //  This is wrapped in a try/catch to log any exceptions and to format
            //  the response to the client to remove call stack info.
            try
            {
                using (var storeClient = _storeServicesClientFactory.CreateClient())
                {
                    consumeResult = await storeClient.CollectionsConsumeAsync(consumeRequest);
                }
            }
            catch (StoreServicesClientConsumeException consumeEx)
            {
                //  This is a specific consume request error which usually means the user does not have enough
                //  balance do consume the amount we specified.  The exception should have a ConsumeError that
                //  we can check.  This means the request did go through and should be removed from the queue.
                response = $"Error attempting to consume {request.RemoveQuantity}" +
                           $" from product {request.ProductId} for UserId {request.UserId}: " +
                           $"{consumeEx.ConsumeErrorInformation.Code}, {consumeEx.ConsumeErrorInformation.Message}";

                _logger.ServiceWarning(cV.Value, response, consumeEx);

                await RemovePendingConsumeAsync(request, cV);
                return response;
            }
            catch (Exception ex)
            {
                if (ex is HttpRequestException)
                {
                    //  This exception mean that we didn't get back a response and so we
                    //  are unsure if the consume happened or not on the Collections side.
                    //  So, we keep this consume pending to retry and verify it later
                    _logger.ConsumeError(cV.Value,
                                         request.UserId,
                                         request.TrackingId.ToString(),
                                         request.ProductId,
                                         request.RemoveQuantity,
                                         "Error getting consume response, keeping request in the pending queue",
                                         ex);

                    //  Return here so that we don't remove this consume from the pending
                    //  cache.
                    return "Error getting consume response, keeping request in the pending queue";
                }
                else if (ex is TaskCanceledException)
                {
                    //  The call was canceled from our side, but we may have already sent out the request,
                    //  so we need to hold onto this in the pending consumes to ensure we give the user
                    //  credit if it did go through.
                    _logger.ConsumeError(cV.Value,
                                         request.UserId,
                                         request.TrackingId.ToString(),
                                         request.ProductId,
                                         request.RemoveQuantity,
                                         "Consume was canceled, keeping request in the pending queue",
                                         ex);

                    //  Return here so that we don't remove this consume from the pending
                    //  cache.
                    return "Error getting consume response, keeping request in the pending queue";
                }
                else
                {
                    //  this is not an expected exception so it should be thrown back up
                    throw;
                }
            }

            response = $"  Consumed {request.RemoveQuantity} from product {consumeResult.ProductId}, " +
                       $"new balance is {consumeResult.NewQuantity} for UserId {request.UserId}.  Transaction: {consumeResult.TrackingId}\n";

            //  TODO: Your own server logic here on granting the item to the user's
            //        account within your own game service / database
            await GrantUserConsumableValue(request, cV);

            //  Add the results of the Consume to our completed consume transactions
            //  so that we can lookup the information on the transaction if the OrderIds
            //  show up in a Clawback response
            await AddToCompletedConsumeTransactions(request.UserId, consumeResult, cV);

            //
            //  Cache the UserPurchaseId so that Clawback can use it to make calls
            //  for this user and check if a refund has been issued later on.
            if (!string.IsNullOrEmpty(request.UserPurchaseId))
            {
                var clawManager = new ClawbackV1Manager(_config,
                                                      _storeServicesClientFactory,
                                                      _logger);

                //  TODO: Implement logic to indicate if the account supports single or
                //        multi-purchasing accounts.  For the default sample we will
                //        treat all accounts as single-purchasing.
                //
                //        For more information on single vs multi-purchasing accounts,
                //        see the API summary for 
                //        ConsumableManager.AddUserPurchaseIdToClawbackQueue().
                bool isSinglePurchasingAccount = true;
                await clawManager.AddUserPurchaseIdToClawbackQueue(request, isSinglePurchasingAccount, cV);
            }
            
            //  We have now taken action on the results of the consume and added the balance to the
            //  user's account if it succeeded. We can now remove this from the pending consume list.
            await RemovePendingConsumeAsync(request, cV);

            return response;
        }

        /// <summary>
        /// Converts a pendingRequest to a CollectionsConsumeRequest object.
        /// </summary>
        /// <param name="pendingRequest"></param>
        /// <returns></returns>
        public async Task<CollectionsV8ConsumeRequest> CreateConsumeRequestFromPendingRequestAsync(PendingConsumeRequest pendingRequest)
        {
            //  Check if the UserCollectionsId has expired, if so, refresh it
            var userCollectionsId = new UserStoreId(pendingRequest.UserCollectionsId);
            if (DateTimeOffset.UtcNow > userCollectionsId.RefreshAfter)
            {
                using (var storeClient = _storeServicesClientFactory.CreateClient())
                {
                    var serviceToken = await storeClient.GetServiceAccessTokenAsync();
                    await userCollectionsId.RefreshStoreId(serviceToken.Token);
                }
            }

            var beneficiary = new CollectionsRequestBeneficiary
            {
                IdentityType = "b2b",
                UserCollectionsId = userCollectionsId.Key,
                LocalTicketReference = ""
            };

            var consumeRequest = new CollectionsV8ConsumeRequest
            {
                RequestBeneficiary = beneficiary,
                ProductId = pendingRequest.ProductId,
                RemoveQuantity = pendingRequest.RemoveQuantity,
                TrackingId = pendingRequest.TrackingId,
                IsUnmanagedConsumable = pendingRequest.IsUnmanagedConsumable,
                IncludeOrderIds = pendingRequest.IncludeOrderIds
            };

            return consumeRequest;
        }

        /// <summary>
        /// Used by the Consume endpoint and the testing endpoints, this creates the actual
        /// consume request including the TrackingId if not provided and validates the
        /// needed values are in the request
        /// </summary>
        /// <param name="clientRequest"></param>
        /// <returns>Validated PendingConsumeRequest to send to Collections or cache</returns>
        public static PendingConsumeRequest CreateAndVerifyPendingConsumeRequest(ClientConsumeRequest clientRequest)
        {
            //  build our request structure
            var pendingConsumeRequest = new PendingConsumeRequest
            {
                UserCollectionsId     = clientRequest.UserCollectionsId,
                ProductId             = clientRequest.ProductId,
                RemoveQuantity        = clientRequest.Quantity,
                UserPurchaseId        = clientRequest.UserPurchaseId,
                UserId                = clientRequest.UserId,
                TrackingId            = clientRequest.TransactionId,
                IsUnmanagedConsumable = clientRequest.IsUnmanagedConsumable,
                IncludeOrderIds       = clientRequest.IncludeOrderIds
            };

            if (!string.IsNullOrEmpty(clientRequest.Sbx))
            {
                pendingConsumeRequest.SandboxId = clientRequest.Sbx;
            }

            //  A TransactionId is required to validate if the consume succeeded
            //  in case of a network error and we don't get the response.  Once
            //  we have one, we can then cache the request parameters and replay
            //  the call to validate if the consume succeeded or not when we are
            //  able to communicate with the Microsoft Store APIs again.
            if (pendingConsumeRequest.TrackingId == Guid.Empty)
            {
                pendingConsumeRequest.TrackingId = Guid.NewGuid();
            }

            ValidatePendingConsumeRequest(pendingConsumeRequest);

            return pendingConsumeRequest;
        }

        /// <summary>
        /// Validates the PendingConsumeRequest to make sure it has the needed values
        /// </summary>
        /// <param name="request"></param>
        /// <returns>Throws ArgumentException if a value is missing</returns>
        private static bool ValidatePendingConsumeRequest(PendingConsumeRequest request)
        {
            if (string.IsNullOrEmpty(request.UserId))
            {
                throw new ArgumentException("No UserId in request", nameof(request));
            }
            if (string.IsNullOrEmpty(request.ProductId))
            {
                throw new ArgumentException("No ProductId in request", nameof(request));
            }
            if (request.RemoveQuantity <= 0)
            {
                throw new ArgumentException("RemoveQuantity cannot be negative or 0", nameof(request));
            }
            if (string.IsNullOrEmpty(request.UserPurchaseId))
            {
                throw new ArgumentException("No UserPurchaseId in request", nameof(request));
            }
            if (string.IsNullOrEmpty(request.UserCollectionsId))
            {
                throw new ArgumentException("No Beneficiary.UserCollectionsId in request", nameof(request));
            }
            if (request.TrackingId == Guid.Empty)
            {
                throw new ArgumentException("No TrackingId in request", nameof(request));
            }
            return true;
        }

        /// <summary>
        /// Adds the request to the pending consume cache or DB so that if we
        /// do not get a response back, we can replay the request to see if
        /// it went through or not with the Microsoft Store.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="cV"></param>
        /// <returns></returns>
        public async Task<bool> TrackPendingConsumeAsync(PendingConsumeRequest request, CorrelationVector cV)
        {
            //  Validate that all of the parameters are provided
            ValidatePendingConsumeRequest(request);

            try
            {
                //  Check if this already exists in the pending consume db based on the TrackingId
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    if (!dbContext.PendingConsumeRequests.Where(b => b.TrackingId == request.TrackingId).Any())
                    {
                        await dbContext.PendingConsumeRequests.AddAsync(request);
                        await dbContext.SaveChangesAsync();
                        _logger.AddPendingTransaction(cV.Increment(),
                                                      request.UserId,
                                                      request.TrackingId.ToString(),
                                                      request.ProductId,
                                                      request.RemoveQuantity);
                    }
                }
            }
            catch (Exception e)
            {
                _logger.ServiceWarning(cV.Value,
                    $"Unable to track the pending consume {request.UserId} {request.TrackingId} {request.ProductId} {request.RemoveQuantity}",
                    e);
            }

            return true;
        }

        /// <summary>
        /// Removes the consume request from the pending cache
        /// </summary>
        /// <param name="request">Request item to be removed</param>
        /// <param name="cV"></param>
        /// <returns></returns>
        private async Task RemovePendingConsumeAsync(PendingConsumeRequest request, CorrelationVector cV)
        {
            using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
            {
                dbContext.PendingConsumeRequests.Remove(request);
                await dbContext.SaveChangesAsync();
            }
            _logger.RemovePendingTransaction(cV.Increment(),
                                             request.UserId,
                                             request.TrackingId.ToString(),
                                             request.ProductId,
                                             request.RemoveQuantity);
        }

        /// <summary>
        /// Helper to get all of the consume requests that are pending
        /// but we have not been able to validate we got a response
        /// back yet and may need to replay them.
        /// </summary>
        /// <param name="cV"></param>
        /// <returns></returns>
        public List<PendingConsumeRequest> GetAllPendingRequests(CorrelationVector cV)
        {
            var result = new List<PendingConsumeRequest>();
            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    var count = dbContext.PendingConsumeRequests.Count();
                    result = dbContext.PendingConsumeRequests.ToList();
                }
            }
            catch (Exception e)
            {
                _logger.ServiceWarning(cV.Value, e.Message, e);
            }

            return result;
        }

        /// <summary>
        /// Placeholder function that resolves the specified consumable and grants the user
        /// the appropriate in-game currency or item where the remaining balance is tracked
        /// through our game service.
        /// </summary>
        /// <param name="request">Consume request that was completed</param>
        /// <param name="cV"></param>
        /// <returns></returns>
        public async Task<int> GrantUserConsumableValue(PendingConsumeRequest request, CorrelationVector cV)
        {
            //  TODO: This function is completely example for the sample purposes.  You would
            //  want to rewrite this function with your own balance and in-game currency
            //  transaction functionality.
            var dbKey = new StringBuilder().AppendFormat("{0}:{1}",
                                                         request.UserId,
                                                         request.ProductId);

            int newBalance = 0;
            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    var userConsumableBalance = dbContext.UserBalances.Find(dbKey.ToString());
                    if (userConsumableBalance == null)
                    {
                        //  This doesn't exist yet so we need to create it
                        userConsumableBalance = new UserConsumableBalance()
                        {
                            ProductId = request.ProductId,
                            Quantity = (int)request.RemoveQuantity,
                            UserId = request.UserId,
                            DbKey = request.UserId + ":" + request.ProductId
                        };
                        dbContext.Add(userConsumableBalance);
                        newBalance = (int)request.RemoveQuantity;
                    }
                    else
                    {
                        userConsumableBalance.Quantity += (int)request.RemoveQuantity;
                    }

                    await dbContext.SaveChangesAsync();
                }
            }
            catch (Exception e)
            {
                _logger.ServiceWarning(cV.Value, e.Message, e);
            }

            return newBalance;
        }

        /// <summary>
        /// Placeholder function that revokes any consumables that were refunded and detected
        /// by the clawback service.
        /// </summary>
        /// <param name="userId">Id of the user to revoke from</param>
        /// <param name="productId">Id of the product to revoke</param>
        /// <param name="amountToRevoke">Quantity to revoke from the user's account of the product</param>
        /// <param name="cV"></param>
        /// <returns>New balance after the quantity was revoked</returns>
        public async Task<int> RevokeUserConsumableValue(string userId, string productId, int amountToRevoke, CorrelationVector cV)
        {
            //  TODO: This function is completely example for the sample purposes.  You would
            //  want to rewrite this function with your own balance and in-game currency
            //  transaction functionality.
            var dbKey = $"{userId}:{productId}";

            int newBalance = 0;
            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    var userConsumableBalance = dbContext.UserBalances.Find(dbKey.ToString());
                    if (userConsumableBalance == null)
                    {
                        //  This doesn't exist yet, but there is a revoke being reported
                        throw new InvalidOperationException($"Clawback attempted for {amountToRevoke} of product {productId} on user {userId} but user balance was not found in the balance DB.");
                    }
                    else
                    {
                        //  NOTE: This test method can make a user's balance go below 0.  A production
                        //  server would probably want to keep the balance at 0 and have another
                        //  balance noting the discrepancy that the user has vs what they spent.
                        userConsumableBalance.Quantity -= amountToRevoke;
                        newBalance = userConsumableBalance.Quantity;
                    }

                    await dbContext.SaveChangesAsync();
                }
            }
            catch (Exception e)
            {
                _logger.ServiceWarning(cV.Value, e.Message, e);
            }

            return newBalance;
        }

        /// <summary>
        /// This will add an item in our completed list of consume transactions for each
        /// OrderId info set in the consume response.  Since we can consume multiple orders
        /// worth of consumables in a single consume transaction, we may end up with multiple
        /// orders that were fulfilled with the same TrackingId.
        /// </summary>
        /// <param name="userId">User who got credit for the consume in the system</param>
        /// <param name="response"></param>
        /// <param name="cV"></param>
        /// <returns></returns>
        public async Task AddToCompletedConsumeTransactions(string userId, CollectionsV8ConsumeResponse response, CorrelationVector cV)
        {
            foreach(var currentOrderTransaction in response.OrderTransactions)
            {
                var completedTransaction = new CompletedConsumeTransaction(userId, response.ProductId, response.TrackingId, currentOrderTransaction);
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    await dbContext.CompletedConsumeTransactions.AddAsync(completedTransaction);
                    await dbContext.SaveChangesAsync();
                }
            }
        }

        /// <summary>
        /// Update the state of a transaction to identify if it has been part of a reconciliation or not
        /// </summary>
        /// <param name="DBkey">Unique GUID for the completed transaction in the DB</param>
        /// <param name="newState">The new state for the item</param>
        /// <param name="cV"></param>
        /// <returns></returns>
        public async Task UpdateCompletedConsumeTransactionState(string dbKey, CompletedConsumeTransactionState newState, CorrelationVector cV)
        {
            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    var transaction = dbContext.CompletedConsumeTransactions.Where(b => b.DbKey == dbKey).First();

                    if(transaction != null)
                    {
                        _logger.ServiceInfo(cV.Value, $"Updating transaction {dbKey}'s status from {transaction.TransactionStatus} to {newState.ToString()}");
                        transaction.TransactionStatus = newState;
                        await dbContext.SaveChangesAsync();
                    }
                    else
                    {
                        _logger.ServiceWarning(cV.Value, $"Unable to find completed consume transaction with DBKey: {dbKey}", null);
                    }
                }
            }
            catch (Exception e)
            {
                _logger.ServiceWarning(cV.Value,
                    $"Unable to update consume transaction with DBKey: {dbKey}", e);
            }
        }

        /// <summary>
        /// Helper to get all of the user balances in our tracking DB
        /// </summary>
        /// <param name="cV"></param>
        /// <returns></returns>
        public List<UserConsumableBalance> GetAllUserBalances(CorrelationVector cV)
        {
            var result = new List<UserConsumableBalance>();
            try
            {
                using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    result = dbContext.UserBalances.ToList();
                }
            }
            catch (Exception e)
            {
                _logger.ServiceWarning(cV.Value, e.Message, e);
            }

            return result;
        }

        /// <summary>
        /// Helper function for testing / re-populating values into your ConsumedTransaction db to
        /// facilitate easier testing of scenarios.  Just be sure not to delete the clawback event
        /// from the queue and using this you can do multiple test runs and debugging.
        /// </summary>
        /// <param name="cV"></param>
        /// <returns></returns>
        public async Task<int> PopulateTestValuesInDatabases(CorrelationVector cV)
        {
            var testTransactions = new List<CompletedConsumeTransaction>
            {
                //  Example test value data - Multiple transactions and purchases
                //new CompletedConsumeTransaction("2 Dev 908710919", "9N0297GK108W", "fb120de0-3ef4-4ba3-a659-465d018c0243", new ConsumeOrderTransactionContractV8() { OrderId = "70fd35f2-7e4a-4f27-8df3-a673a5a4d9d9", OrderLineItemId ="230e9063-bffe-411a-8aa1-6f99ca091452", QuantityConsumed=1}),
                //new CompletedConsumeTransaction("2 Dev 908710919", "9MT5TGW893HV", "334b2fb1-4b4d-474d-ad7d-2f886b3eaecd", new ConsumeOrderTransactionContractV8() { OrderId = "a19d5e0d-f738-46ac-b56c-8d32367d163b", OrderLineItemId ="54b29900-8ea5-4c2d-9fb8-312e4b4d8d0c", QuantityConsumed=10}),
                //new CompletedConsumeTransaction("2 Dev 908710919", "9MT5TGW893HV", "0cd4dcc4-4899-48a6-b72f-0308f48fbdb0", new ConsumeOrderTransactionContractV8() { OrderId = "f8368c62-966d-4036-a795-5a4b65c79468", OrderLineItemId ="715d3f99-24f5-4e15-adc3-0393bb53ff46", QuantityConsumed=10}),
                //new CompletedConsumeTransaction("2 Dev 908710919", "9PFL4RQTB1P6", "8295b0ed-314f-4b3b-a8d0-a24b01e0e1cf", new ConsumeOrderTransactionContractV8() { OrderId = "8d25bd33-9856-453a-b494-b390a5cde27c", OrderLineItemId ="3371ce65-79f7-4c81-9910-f718eaba3efb", QuantityConsumed=1}),
                //new CompletedConsumeTransaction("2 Dev 908710919", "9MT5TGW893HV", "d1985ed5-e1a3-4a4a-ab22-2a1e936dfda3", new ConsumeOrderTransactionContractV8() { OrderId = "97c6d89d-6f65-45a8-92e7-d4ceecc09f6e", OrderLineItemId ="9abc1045-b08c-4f12-a7cb-1a5118455aca", QuantityConsumed=2}),
                //new CompletedConsumeTransaction("2 Dev 908710919", "9MT5TGW893HV", "a237d06a-0e1d-480a-bd91-a710f8305bf5", new ConsumeOrderTransactionContractV8() { OrderId = "97c6d89d-6f65-45a8-92e7-d4ceecc09f6e", OrderLineItemId ="9abc1045-b08c-4f12-a7cb-1a5118455aca", QuantityConsumed=8}),
                //new CompletedConsumeTransaction("2 Dev 927487264", "9NCX1H100M18", "a0d73d4d-bb1a-4b33-b9b3-84aa10cab220", new ConsumeOrderTransactionContractV8() { OrderId = "92431685-572b-448a-82e1-d2ce23b189a3", OrderLineItemId ="ccbf26e3-d2b2-4077-963b-b4fc8696e1e4", QuantityConsumed=50}),
                //new CompletedConsumeTransaction("2 Dev 927487264", "9PFL4RQTB1P6", "2b2e2d30-bbe8-4fcf-8420-025363d8dbdf", new ConsumeOrderTransactionContractV8() { OrderId = "92431685-572b-448a-82e1-d2ce23b189a3", OrderLineItemId ="ccbf26e3-d2b2-4077-963b-b4fc8696e1e4", QuantityConsumed=1}),
                //new CompletedConsumeTransaction("2 Dev 927487264", "9PFL4RQTB1P6", "7c24fe5f-3231-4ffd-9a6c-e23dba9ee5af", new ConsumeOrderTransactionContractV8() { OrderId = "3f438557-92bf-47a0-82dd-448d4bf16cf8", OrderLineItemId ="02644953-3061-4c5f-8b0d-8be2506821b8", QuantityConsumed=1}),
                //new CompletedConsumeTransaction("2 Dev 927487264", "9MT5TGW893HV", "276cbf9e-c846-4b0a-bbbb-f75aa580135f", new ConsumeOrderTransactionContractV8() { OrderId = "3f438557-92bf-47a0-82dd-448d4bf16cf8", OrderLineItemId ="02644953-3061-4c5f-8b0d-8be2506821b8", QuantityConsumed=10}),
            };

            foreach (var testTransaction in testTransactions)
            {
               using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                {
                    await dbContext.CompletedConsumeTransactions.AddAsync(testTransaction);
                    await dbContext.SaveChangesAsync();
                }

                var testConsumeRequest = new PendingConsumeRequest()
                {
                    ProductId = testTransaction.ProductId,
                    RemoveQuantity = (uint)testTransaction.QuantityConsumed,
                    UserId = testTransaction.UserId
                };
                await GrantUserConsumableValue(testConsumeRequest, cV);
            }

            return 1;
        }
    }
}
