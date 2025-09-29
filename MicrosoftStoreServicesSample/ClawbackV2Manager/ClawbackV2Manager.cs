//-----------------------------------------------------------------------------
// ClawbackV2Manager.cs
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
using Microsoft.StoreServices.Clawback.V2;
using MicrosoftStoreServicesSample.PersistentDB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MicrosoftStoreServicesSample
{
    public class ClawbackV2Manager
    {
        protected IConfiguration _config;
        protected IStoreServicesClientFactory _storeServicesClientFactory;
        protected ILogger _logger;

        public ClawbackV2Manager(IConfiguration config,
                                 IStoreServicesClientFactory storeServicesClientFactory,
                                 ILogger logger)
        {
            _config = config;
            _storeServicesClientFactory = storeServicesClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Provides all the completedConsumeTransactions in our database to check any
        /// Clawback V2 Events against.
        /// </summary>
        /// <param name="cV"></param>
        /// <returns></returns>
        public List<CompletedConsumeTransaction> GetCompletedConsumeTransactions(CorrelationVector cV)
        {
            using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
            {
                return dbContext.CompletedConsumeTransactions.ToList();
            }
        }

        /// <summary>
        /// This is the main logic flow to query for any outstanding refund events from the Clawback V2
        /// service, check those against known completed transactions in our database, and then adjust
        /// the user's balance to 'claw-back' the items that were refunded.
        /// 
        /// The flow will only act on and delete messages in the queue that match the provided SandboxId.
        /// This is a safeguard to prevent RETAIL facing messages and events being skipped or deleted
        /// from the queue while in development or running tests.
        /// 
        /// NOTE: This is an example of the flow that works with a small sample data set.  This code
        /// and the supporting functions would need to be updated to scale better to a larger data set.
        /// </summary>
        /// <param name="sandboxId">Only act on messages for this Sandbox</param>
        /// <param name="cV"></param>
        /// <returns></returns>
        public async Task<string> RunClawbackV2ReconciliationAsync(string sandboxId, CorrelationVector cV, bool printMessages = false)
        {
            if(string.IsNullOrWhiteSpace(sandboxId))
            {
                throw new ArgumentException($"{nameof(sandboxId)} You must provide a SandboxId target to prevent events from a different sandbox or RETAIL being deleted while testing.", nameof(sandboxId));
            }

            var response = new StringBuilder();
            var logMessage = $"Starting Clawback V2 Reconciliation for Sandbox {sandboxId}";
            _logger.ServiceInfo(cV.Value, logMessage);
            response.AppendFormat("INFO: {0}\n", logMessage);
            int numProcessed = 0;
            int numDeleted   = 0;
            int numSkipped   = 0;

            var messagesToPrint = new List<ClawbackV2Message>();

            using (var storeClient = _storeServicesClientFactory.CreateClient())
            {
                var queueMessages = new List<ClawbackV2Message>();
                do
                {
                    //  Step 1:
                    //  Call the ClawbackV2 service to see if there are any messages we can process
                    //  from the queue.
                    queueMessages = await storeClient.ClawbackV2QueryEventsAsync(32);
                    
                    logMessage = $"{queueMessages.Count} clawback events found";
                    _logger.ServiceInfo(cV.Value, logMessage);
                    response.AppendFormat("INFO: {0}\n", logMessage);

                    foreach (var currentMessage in queueMessages)
                    {
                        //  Step 2:
                        //  Verify that the clawback event matches the Sandbox that we are targeting
                        //  to operate on.  Otherwise, we could end up skipping and deleting events
                        //  that would match the RETAIL sandbox (Production) during testing and
                        //  development.  When we skip an event that doesn't match our target sandbox
                        //  it will appear in the ClawbackV2 message queue again in 30 seconds for the
                        //  next queue from your game service looking for the RETAIL events.
                        //
                        //  This is also why it is a good idea if your services will be checking for
                        //  updates occasionally, that you make sure they are staggered and don't
                        //  call the queue at the same time.  Otherwise the 2nd service to query the 
                        //  queue will not see messages that the 1st service grabbed until that 30
                        //  second timeout.
                        if (currentMessage.ClawbackEvent.OrderInfo.SandboxId == sandboxId)
                        {
                            //  Step 3:
                            //  Check the EventState to know if we should take action on this or not:
                            //  Refunded - User got their $ and the Microsoft Store was able to remove the item from their account
                            //  Revoked  - User got their $ but the item was already consumed and the Microsoft Store was
                            //             unable to remove the granted qty or item.  The consume completedTransaction for this order
                            //             should be in our CompletedConsumeTransaction database
                            try
                            {
                                if (currentMessage.ClawbackEvent.OrderInfo.EventState == EventStates.Refunded)
                                {
                                    logMessage = $"Clawback Event {currentMessage.ClawbackEvent.Id}'s state is {EventStates.Refunded}.  No further action needed.";
                                    _logger.ServiceInfo(cV.Value, logMessage);
                                    response.AppendFormat("INFO: {0}\n", logMessage);

                                    // TODO: Your service could mark this in the logs for future tracking or
                                    //       metrics around refunded items that get consumed vs not consumed
                                }
                                else
                                {
                                    //  Step 4:
                                    //  Look for any completed consume transactions that match
                                    //  the ClawbackV2 event's OrderId and LineItemId.
                                    var matchingConsumeTransactions = new List<CompletedConsumeTransaction>();
                                    try
                                    {
                                        using (var dbContext = ServerDBController.CreateDbContext(_config, cV, _logger))
                                        {
                                            //  Look for any items that have a matching OrderID, OrderLineItemID,
                                            matchingConsumeTransactions = dbContext.CompletedConsumeTransactions.Where(
                                                b => b.OrderId == currentMessage.ClawbackEvent.OrderInfo.OrderId &&
                                                b.OrderLineItemId == currentMessage.ClawbackEvent.OrderInfo.LineItemId &&
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
                                        logMessage = $"No unreconciled transactions found for OrderId: {currentMessage.ClawbackEvent.OrderInfo.OrderId} OrderLineItemId: {currentMessage.ClawbackEvent.OrderInfo.LineItemId}";
                                        _logger.ServiceInfo(cV.Value, logMessage);
                                        response.AppendFormat("INFO: {0}\n", logMessage);
                                    }
                                    else
                                    {
                                        logMessage = $"Found {matchingConsumeTransactions.Count} matching unreconciled transactions for OrderId: {currentMessage.ClawbackEvent.OrderInfo.OrderId} OrderLineItemId: {currentMessage.ClawbackEvent.OrderInfo.LineItemId}";
                                        _logger.ServiceInfo(cV.Value, logMessage);
                                        response.AppendFormat("INFO: {0}\n", logMessage);

                                        //  Step 5:
                                        //  Take action on each of the consume transactions that match the refund event
                                        var consumeManager = new ConsumableManager(_config, _storeServicesClientFactory, _logger);
                                        foreach (var completedTransaction in matchingConsumeTransactions)
                                        {
                                            //  TODO: Take action by clawing back balance, items, or whatever you determine to be 
                                            //        the appropriate action based on this item that the user got a refund for.
                                            var newBalance = await consumeManager.RevokeUserConsumableValue(completedTransaction.UserId,    // User who was granted the credit of the completedTransaction
                                                                                                            completedTransaction.ProductId, // ProductId the user was granted
                                                                                                            (int)completedTransaction.QuantityConsumed, // How much they received from this completedTransaction
                                                                                                            cV);

                                            logMessage = $"Removed {completedTransaction.QuantityConsumed} of {completedTransaction.ProductId} from user {completedTransaction.UserId}.  Remaining balance: {newBalance}";
                                            _logger.ServiceInfo(cV.Value, logMessage);
                                            response.AppendFormat("INFO: {0}\n", logMessage);

                                            //  Mark this completedTransaction as Reconciled so we don't process it again
                                            await consumeManager.UpdateCompletedConsumeTransactionState(completedTransaction.DbKey, CompletedConsumeTransactionState.Reconciled, cV);
                                        }
                                    }
                                }

                                //  Step 6: 
                                //  Delete the message from the ClawbackV2 message queue now that we have processed it.
                                //  Note: If you are testing and using pre-populated consumed transactions, make sure to 
                                //        comment this block out so that the messages are not deleted as you debug through
                                //        the events.
                                var result = await storeClient.ClawbackV2DeleteMessageAsync(currentMessage);
                                if (result == 200 || result == 204)
                                {
                                    logMessage = $"Deleted messageId {currentMessage.MessageId} from the queue";
                                    _logger.ServiceInfo(cV.Value, logMessage);
                                    response.AppendFormat("INFO: {0}\n", logMessage);
                                    numDeleted++;
                                }
                                else
                                {
                                    logMessage = $"Unexpected http result {result} when deleting MessageId {currentMessage.MessageId} from the queue";
                                    throw new Exception(logMessage);
                                }
                            }
                            catch (Exception ex)
                            {
                                logMessage = $"Exception processing ClawbackV2 MessageID {currentMessage.MessageId}: {ex.Message}";
                                _logger.ServiceWarning(cV.Value, logMessage, ex);
                                response.AppendFormat("Warning: {0}\n", logMessage);
                            }

                            if(printMessages)
                            {
                                messagesToPrint.Add(currentMessage);
                            }

                            numProcessed++;
                        }
                        else
                        {
                            numSkipped++;
                        }
                    }
                } while (queueMessages.Count > 0);
            }

            logMessage = $"ClawbackV2 event reconciliation task finished: {numProcessed} processed, {numDeleted} deleted, {numSkipped} skipped";
            _logger.ServiceInfo(cV.Value, logMessage);
            response.AppendFormat("INFO: {0}\n", logMessage);
            
            if (printMessages)
            {
                //  Print the header of the messages
                response.Append(FormatClawbackMessagesToTextHeader());

                //  Print the messages
                response.Append(FormatClawbackMessagesToText(messagesToPrint));
            }

            return response.ToString();
        }

        /// <summary>
        /// Utility function to print the header of the columns for the text format of event messages 
        /// test endpoints
        /// </summary>
        /// <returns></returns>
        public static string FormatClawbackMessagesToTextHeader()
        {
            var response = new StringBuilder("");

            response.Insert(0,
                    "\n" +
                    "| ProductId    | Product Type        | Sandbox    | EventState | Source               | OrderId                              | LineItemId                           | Purchase Date                 | Refund Initiated Date         | Inserted On                   | Message Id                           | Clawback Event Id                    | Sub. Start Date               | Sub. Days | Sub. Used Days | Sub. Refund Type |\r\n"
                        +
                    "|--------------|---------------------|------------|------------|----------------------|--------------------------------------|--------------------------------------|-------------------------------|-------------------------------|-------------------------------|--------------------------------------|--------------------------------------|-------------------------------|-----------|----------------|------------------|\r\n"
                    );

            return response.ToString();
        }

        /// <summary>
        /// Utility function to print out the messages in a set text format that can be exported to a spreadsheet or other text document 
        /// test endpoints
        /// </summary>
        /// <param name="clawbackMessages"></param>
        /// <returns></returns>
        public static string FormatClawbackMessagesToText(List<ClawbackV2Message> clawbackMessages)
        {
            // Column width constants for output formatting
            const int ProductIdWidth = 12;
            const int ProductTypeWidth = 19;
            const int SandboxIdWidth = 10;
            const int EventStateWidth = 10;
            const int SourceWidth = 20;
            const int OrderIdWidth = 36;
            const int LineItemIdWidth = 36;
            const int PurchasedDateWidth = 29;
            const int EventDateWidth = 29;
            const int InsertedOnWidth = 29;
            const int MessageIdWidth = 36;
            const int ClawbackEventIdWidth = 36;
            const int SubStartWidth = 29;
            const int SubTotalDaysWidth = 9;
            const int SubUsedDaysWidth = 14;
            const int SubRefundTypeWidth = 16;

            var response = new StringBuilder("");

            foreach (var clawbackMessage in clawbackMessages)
            {
                var subStart = "";
                var subTotalDays = "";
                var subUsedDays = "";
                var subRefundType = "";

                if (clawbackMessage.ClawbackEvent.OrderInfo.RecurrenceData != null)
                {
                    subStart = clawbackMessage.ClawbackEvent.OrderInfo.RecurrenceData.DurationIntervalStart.ToString();
                    subTotalDays = clawbackMessage.ClawbackEvent.OrderInfo.RecurrenceData.DurationInDays.ToString();
                    subUsedDays = clawbackMessage.ClawbackEvent.OrderInfo.RecurrenceData.ConsumedDurationInDays.ToString();
                    subRefundType = clawbackMessage.ClawbackEvent.OrderInfo.RecurrenceData.RefundType.ToString();
                }

                response.AppendFormat(
                    "| {0,-" + ProductIdWidth + "} | {1,-" + ProductTypeWidth + "} | {2,-" + SandboxIdWidth + "} | {3,-" + EventStateWidth + "} | {4,-" + SourceWidth + "} | {5,-" + OrderIdWidth + "} | {6,-" + LineItemIdWidth + "} | {7,-" + PurchasedDateWidth + "} | {8,-" + EventDateWidth + "} | {9,-" + InsertedOnWidth + "} | {10,-" + MessageIdWidth + "} | {11,-" + ClawbackEventIdWidth + "} | {12,-" + SubStartWidth + "} | {13,-" + SubTotalDaysWidth + "} | {14,-" + SubUsedDaysWidth + "} | {15,-" + SubRefundTypeWidth + "} |\r\n",
                    clawbackMessage.ClawbackEvent.OrderInfo.ProductId,
                    clawbackMessage.ClawbackEvent.OrderInfo.ProductType,
                    clawbackMessage.ClawbackEvent.OrderInfo.SandboxId,
                    clawbackMessage.ClawbackEvent.OrderInfo.EventState,
                    clawbackMessage.ClawbackEvent.Source,
                    clawbackMessage.ClawbackEvent.OrderInfo.OrderId,
                    clawbackMessage.ClawbackEvent.OrderInfo.LineItemId,
                    clawbackMessage.ClawbackEvent.OrderInfo.PurchasedDate,
                    clawbackMessage.ClawbackEvent.OrderInfo.EventDate,
                    clawbackMessage.InsertedOn,
                    clawbackMessage.MessageId,
                    clawbackMessage.ClawbackEvent.Id,
                    subStart,
                    subTotalDays,
                    subUsedDays,
                    subRefundType
                );
            }

            return response.ToString();
        }
    }
}
