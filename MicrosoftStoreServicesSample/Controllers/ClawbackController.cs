//-----------------------------------------------------------------------------
// ClawbackController.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License file under the project root for
// license information.
//-----------------------------------------------------------------------------

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.StoreServices;
using Microsoft.StoreServices.Clawback.V1;
using Microsoft.StoreServices.Clawback.V2;
using MicrosoftStoreServicesSample.PersistentDB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MicrosoftStoreServicesSample.Controllers
{
    /// <summary>
    /// Example endpoints to demonstrate the abilities of using the
    /// Clawback V2 ClawbackEvent service to query refunded products
    /// </summary>
    [ApiController]
    [Route("[controller]/[action]")]
    public class ClawbackController : ServiceControllerBase
    {
        private readonly IConfiguration _config;
        public ClawbackController(IConfiguration config,
                                  IStoreServicesClientFactory storeServicesClientFactory,
                                  ILogger<CollectionsController> logger) : base(storeServicesClientFactory, logger)
        {
            _config = config;
        }

        ////////////////////////////////////////////////////////////////////
        //  Testing Endpoints - Not for RETAIL release
        ////////////////////////////////////////////////////////////////////
        //  TODO: Remove these APIs if you are using this as a framework to
        //        build your service from.  These are only test endpoints to
        //        help demonstrate how to use the Purchase service.  These
        //        generally will be used and controlled only from your own
        //        service for reconciling refunds.
        ////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Returns any refunds found by the Clawback service for the UserPurchaseId provided
        /// </summary>
        /// <param name="clientRequest"></param>
        /// <returns></returns>
        [HttpPost]
        public async Task<ActionResult<string>> ClawbackV1Query([FromBody] ClientPurchaseId clientRequest)
        {
            //  Check that we have a properly formatted request body
            if (string.IsNullOrEmpty(clientRequest.UserPurchaseId))
            {
                return BadRequest("Request body missing PurchaseId. ex: {\"UserPurchaseId\": \"...\"}");
            }

            var response = new StringBuilder("");

            var clawbackRequest = new ClawbackV1QueryRequest
            {
                UserPurchaseId = clientRequest.UserPurchaseId
            };

            if (clientRequest.LineItemStateFilter != null)
            {
                clawbackRequest.LineItemStateFilter = clientRequest.LineItemStateFilter;
            }
            else
            {
                //  Revoked and Refunded are added by default when the clawbackV1QueryRequest is created
                //  since the caller passed in null, just give them all states which would mean just
                //  adding Purchased to the default values.
                clawbackRequest.LineItemStateFilter.Add(LineItemStates.Purchased);
            }

            if (!string.IsNullOrEmpty(clientRequest.Sbx))
            {
                clawbackRequest.SandboxId = clientRequest.Sbx;
            }

            var clawbackResults = new ClawbackV1QueryResponse();
            var clawbackItems = new List<ClawbackV1Item>();

            using (var storeClient = _storeServicesClientFactory.CreateClient())
            {
                do
                {
                    clawbackResults = await storeClient.ClawbackV1QueryAsync(clawbackRequest);

                    //  If there was a continuation token add it to the next cycle.
                    clawbackRequest.ContinuationToken = clawbackResults.ContinuationToken;

                    //  Append the results to our collections list before possibly doing
                    //  another request to get the rest.
                    foreach (var item in clawbackResults.Items)
                    {
                        clawbackItems.Add(item);
                    }

                } while (!string.IsNullOrEmpty(clawbackResults.ContinuationToken));
            }

            try
            {
                response.Append(
                    "| ProductId    | Qty | State     | OrderID                              | LineItemId                           | Refunded Date                 |\n" +
                    "|----------------------------------------------------------------------------------------------------------------------------------------------|\n");

                foreach (var item in clawbackItems)
                {
                    foreach (var lineItem in item.OrderLineItems)
                    {
                        //  If the item is in the Purchase state, then we don't show the Refund date
                        string refundedDate = "";
                        if (lineItem.LineItemState != LineItemStates.Purchased)
                        {
                            refundedDate = item.OrderRefundedDate.ToString();
                        }

                        response.AppendFormat("| {0,-12} | {1,-3} | {2,-9} | {3,-36} | {4,-36} | {5,-29} |\n",
                                              lineItem.ProductId,
                                              lineItem.Quantity,
                                              lineItem.LineItemState,
                                              item.OrderId,
                                              lineItem.LineItemId,
                                              item.OrderRefundedDate.ToString());
                    }
                }
            }
            catch (Exception e)
            {
                response.Append(e.Message);
            }

            var finalResponse = response.ToString();
            return new OkObjectResult(finalResponse);
        }

        /// <summary>
        /// Returns any refund events found in the Clawback v2 service
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        public async Task<ActionResult<string>> ClawbackV2Query([FromBody] string sandboxFilter)
        {
            InitializeLoggingCv();
            string returnVal = "";

            //  Check that we have a properly formatted request body
            if (string.IsNullOrEmpty(sandboxFilter))
            {
                sandboxFilter = "";  // If no sandbox filter is provided, we will just process them all
            }

            using (var storeClient = _storeServicesClientFactory.CreateClient())
            {
                //  GET message limit is 30 seconds, so if we are still processing
                //  queue messages after 30 seconds we are probably re-processing
                //  messages we have already seen as this function does not delete
                //  any messages from the queue.
                var timeoutLimit = DateTime.Now.AddSeconds(30); 

                var clawbackMessages = new List<ClawbackV2Message>();
                do
                {
                    returnVal += FormatResponseForClawbackV2Messages(clawbackMessages);
                    clawbackMessages = await storeClient.ClawbackV2QueryEventsAsync(32);
                }
                while (clawbackMessages.Count > 0 && timeoutLimit > DateTime.Now); 

                if (timeoutLimit <= DateTime.Now)
                {
                    returnVal += "\n WARNING: There are more messages on the queue than we were able to see with only GET requests.  To see all messages some must be deleted from the queue.\n";

                    _logger.ServiceWarning(_cV.Value, returnVal, null);
                }
            }
            FinalizeLoggingCv();
            return returnVal;
        }

        /// <summary>
        /// Returns any refunds found by the Clawback service for the UserPurchaseId provided
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<ActionResult<string>> ClawbackV2Peek()
        {
            var response = new StringBuilder("");
            var peekMessages = new List<ClawbackV2Message>();

            //  add the header of the output table by passing in an empty list
            response.Append(FormatResponseForClawbackV2Messages(peekMessages));

            using (var storeClient = _storeServicesClientFactory.CreateClient())
            {
                peekMessages = await storeClient.ClawbackV2PeekEventsAsync(32);
                response.Append(FormatResponseForClawbackV2Messages(peekMessages));
            }

            return response.ToString();
        }

        /// <summary>
        /// TODO: This endpoint would not be an endpoint in your deployed service.  It would be
        /// function that you would run once a day and be controlled without a client request.
        /// 
        /// Runs the task to go through and reconcile all of the items in the Clawback Queue to find
        /// if there were any refunds issued for items that we have already consumed and therefore
        /// need to revoke / remove items from the user's account balance on our server
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<ActionResult<string>> RunClawbackValidation()
        {
            InitializeLoggingCv();
            var response = new StringBuilder("Running Clawback Reconciliation Task...  \n");

            var clawManager = new ClawbackV1Manager(_config, _storeServicesClientFactory, _logger);
            response.Append(await clawManager.RunClawbackReconciliationAsync(_cV));

            FinalizeLoggingCv();
            return new OkObjectResult(response.ToString());
        }

        /// <summary>
        /// TODO: This endpoint would not be an endpoint in your deployed service.  It would be
        /// function that you would run once a day and be controlled without a client request.
        /// 
        /// Runs the task to go through and reconcile all of the items in the Clawback Queue to find
        /// if there were any refunds issued for items that we have already consumed and therefore
        /// need to revoke / remove items from the user's account balance on our server
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<ActionResult<string>> RunClawbackV2Validation()
        {
            InitializeLoggingCv();
            var response = new StringBuilder("Running Clawback Reconciliation Task...  \n");

            var clawManager = new ClawbackV2Manager(_config, _storeServicesClientFactory, _logger);
            response.Append(await clawManager.RunClawbackV2ReconciliationAsync("RETAIL", _cV));

            FinalizeLoggingCv();
            return new OkObjectResult(response.ToString());
        }

        /// <summary>
        /// NOTE: This is a test API only and should not be part of a production deployment
        /// Returns to the items in the Clawback queue which represent consumes that we are
        /// acted on and the UserId was granted value in their game account for the consume.
        /// We are tracking these and they are used to look for returns / refunds so that 
        /// we can remove those items from the user's balance within our own databases.
        /// </summary>
        /// <returns></returns>

        [HttpGet]
        public ActionResult<string> ViewClawbackQueue()
        {
            InitializeLoggingCv();
            var response = new StringBuilder("Clawback items being tracked:\n");

            var clawManager = new ClawbackV1Manager(_config, _storeServicesClientFactory, _logger);

            var clawbackQueue = clawManager.GetClawbackQueue(_cV);
            foreach (var clawbackQueueItem in clawbackQueue)
            {
                response.AppendFormat("User {0}'s last transaction on {1}, {2}\n",
                                      clawbackQueueItem.DbKey,
                                      clawbackQueueItem.ConsumeDate,
                                      clawbackQueueItem.UserPurchaseId);
            }

            FinalizeLoggingCv();
            return new OkObjectResult(response.ToString());
        }

        /// <summary>
        /// NOTE: This is a test API only and should not be part of a production deployment
        /// Full list of all Clawback action items in the building state
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public ActionResult<string> ViewReconciledTransactions()
        {
            InitializeLoggingCv();
            var response = new StringBuilder("Getting CompletedTransactionItems that have been reconciled:\n");

            var reconciledTransactions = new List<CompletedConsumeTransaction>();
            using (var dbContext = ServerDBController.CreateDbContext(_config, _cV, _logger))
            {
                reconciledTransactions = dbContext.CompletedConsumeTransactions.Where(
                    b => b.TransactionStatus == CompletedConsumeTransactionState.Reconciled
                    ).ToList();
            }

            if (reconciledTransactions.Count > 0)
            {
                response.Append(FormatResponseForCompletedTransactions(reconciledTransactions));
            }
            else
            {
                response.Append("No CompletedTransactionItems found");
            }

            FinalizeLoggingCv();
            return new OkObjectResult(response.ToString());
        }

        /// <summary>
        /// NOTE: This is a test API only and should not be part of a production deployment
        /// Full list of all Clawback action items in the building state
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public ActionResult<string> ViewCompletedTransactions()
        {
            InitializeLoggingCv();
            var response = new StringBuilder("Getting CompletedTransactionItems:\n");

            var transactions = new List<CompletedConsumeTransaction>();
            using (var dbContext = ServerDBController.CreateDbContext(_config, _cV, _logger))
            {
                transactions = dbContext.CompletedConsumeTransactions.ToList();
            }

            if (transactions.Count > 0)
            {
                response.Append(FormatResponseForCompletedTransactions(transactions));
            }
            else
            {
                response.Append("No CompletedTransactionItems found");
            }

            FinalizeLoggingCv();
            return new OkObjectResult(response.ToString());
        }

        /// <summary>
        /// Utility function to help format the response to send back down to the client for the 
        /// test endpoints
        /// </summary>
        /// <param name="actionItems"></param>
        /// <returns></returns>
        private static string FormatResponseForCompletedTransactions(List<CompletedConsumeTransaction> transactions)
        {
            var response = new StringBuilder("\n");
            response.Append(
                    "| TrackingId                           | Status       | ProductId    | Quantity | UserId           | Consumed Date                 | OrderId                              | OrderLineItemId                      |\n" +
                    "|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|\n");
            
            foreach (var transaction in transactions)
            {
                response.AppendFormat("| {0,-36} | {1,-12} | {2,-12} | {3,-8} | {4,-16} | {5,-29} | {6,-36} | {7,-36} |\n",
                                      transaction.TrackingId,
                                      transaction.TransactionStatus,
                                      transaction.ProductId,
                                      transaction.QuantityConsumed,
                                      transaction.UserId,
                                      transaction.ConsumeDate, 
                                      transaction.OrderId,
                                      transaction.OrderLineItemId);

                response.AppendFormat(
                   "|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|\n");
            }

            response.AppendFormat("\n");

            return response.ToString();
        }

        /// <summary>
        /// Utility function to help format the response to send back down to the client for the 
        /// test endpoints
        /// </summary>
        /// <param name="actionItems"></param>
        /// <returns></returns>
        private static string FormatResponseForClawbackV2Messages(List<ClawbackV2Message> clawbackMessages)
        {
            var response = new StringBuilder("");
            if (clawbackMessages.Count == 0)
            {
                //  This is the end of the list so add this at the top
                response.Insert(0,
                        "\n" +
                        "| ProductId    | Sandbox    | EventState | Source               | OrderId                              | LineItemId                           | Purchase Date                 | Refund Initiated Date         | Inserted On                   | Message Id                           | Clawback Event Id                    | Sub. Start Date               | Sub. Days | Sub. Used Days |\r\n"
                         +                                                                                                                                                                                                                                                                                                                              
                        "|--------------|------------|------------|----------------------|--------------------------------------|--------------------------------------|-------------------------------|-------------------------------|-------------------------------|--------------------------------------|--------------------------------------|-------------------------------|-----------|----------------|\r\n"
                        );
            }


            foreach (var clawbackMessage in clawbackMessages)
            {
                var subStart = "";
                var subTotalDays = "";
                var subUsedDays = "";

                if (clawbackMessage.ClawbackEvent.OrderInfo.SubscriptionData != null)
                {
                    subStart = clawbackMessage.ClawbackEvent.OrderInfo.SubscriptionData.DurationIntervalStart.ToString();
                    subTotalDays = clawbackMessage.ClawbackEvent.OrderInfo.SubscriptionData.DurationInDays.ToString();
                    subUsedDays = clawbackMessage.ClawbackEvent.OrderInfo.SubscriptionData.ConsumedDurationInDays.ToString();
                }
                
                response.AppendFormat("| {0,-12} | {1,-10} | {2,-10} | {3,-20} | {4,-36} | {5,-36} | {6,-29} | {7,-29} | {8,-29} | {9,-36} | {10,-36} | {11,-29} | {12,-9} | {13,-14} |\r\n",
                                      clawbackMessage.ClawbackEvent.OrderInfo.ProductId,
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
                                      subUsedDays
                                      );
             }

            return response.ToString();
        }
    }    
}
