//-----------------------------------------------------------------------------
// PurchaseController.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
//-----------------------------------------------------------------------------

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.StoreServices;
using MicrosoftStoreServicesSample.Models;
using MicrosoftStoreServicesSample.PersistentDB;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MicrosoftStoreServicesSample.Controllers
{
    /// <summary>
    /// Example endpoints to demonstrate the abilities of using the
    /// Purchase endpoints to manage subscriptions (recurrence) and
    /// to monitor and manage refunds to the customer (clawback).
    /// </summary>
    [ApiController]
    [Route("[controller]/[action]")]
    public class PurchaseController : ServiceControllerBase
    {
        private readonly IConfiguration _config;
        public PurchaseController(IConfiguration config,
                                  IStoreServicesClientFactory storeServicesClientFactory,
                                  ILogger<CollectionsController> logger) : base(storeServicesClientFactory, logger)
        {
            _config = config;
        }

        /// <summary>
        /// TODO: You will want to likely incorporate this flow into your authorization
        /// handshake with the client ranter than having a dedicated endpoint to hand
        /// these out.
        /// 
        /// Sends the access tokens to the client that will be needed to create the
        /// required UserCollectionsId and UserPurchaseId for functional calls
        /// made to the store services.
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<ActionResult<string>> AccessTokens()
        {
            InitializeLoggingCv();
            var response = new ClientAccessTokensResponse
            {
                //  TODO: Replace this code obtaining and noting the UserId with your own
                //        authentication ID system for each user.
                //  make up a unique ID for this client
                UserID = GetUserId()
            };

            try
            {
                response.AccessTokens = await GetAccessTokens();
            }
            catch (Exception e)
            {
                return e.Message;
            }

            //  Send these access tokens to the client for them to then
            //  get the UserCollectionsId and UserPurchaseId and return
            //  them to us
            FinalizeLoggingCv();
            return new OkObjectResult(JsonConvert.SerializeObject(response));
        }

        ////////////////////////////////////////////////////////////////////
        //  Testing Endpoints - Not for RETAIL release
        ////////////////////////////////////////////////////////////////////
        //  TODO: Remove these APIs if you are using this as a framework to
        //        build your service from.  These are only test endpoints to
        //        help demonstrate how to use the Purchase service.  These
        //        generally will be used and controlled only from your own
        //        service for reconciling subscriptions or refunds.
        ////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Returns any subscriptions for the UserPurchaseId provided
        /// </summary>
        /// <param name="clientRequest"></param>
        /// <returns></returns>
        [HttpPost]
        public async Task<ActionResult<string>> RecurrenceQuery([FromBody] ClientPurchaseId clientRequest)
        {
            //  Check that we have a properly formatted request body
            if (string.IsNullOrEmpty(clientRequest.UserPurchaseId))
            {
                return BadRequest("Request body missing PurchaseId. ex: {\"PurchaseId\": \"...\"}");
            }

            bool includeJson = false;
            if(Request.Headers.ContainsKey("User-Agent"))
            {
                string userAgent = Request.Headers["User-Agent"];
                if (!string.IsNullOrEmpty(userAgent) && userAgent.Equals("Microsoft.StoreServicesClientSample"))
                {
                    //  This call is from the Client sample that is tied to this sample, so include the added JSON
                    //  in the response body so that it can use those values to update the UI.
                    includeJson = true;
                }
            }

            var response = new StringBuilder("");

            var recurrenceRequest = new RecurrenceQueryRequest
            {
                UserPurchaseId = clientRequest.UserPurchaseId
            };

            if (!string.IsNullOrEmpty(clientRequest.sbx))
            {
                recurrenceRequest.SandboxId = clientRequest.sbx;
            }

            var recurrenceResults = new RecurrenceQueryResponse();
            using (var storeClient = _storeServicesClientFactory.CreateClient())
            {
                recurrenceResults = await storeClient.RecurrenceQueryAsync(recurrenceRequest);
            }

            try
            {
                response.Append(
                    "| ProductId    | Renew | State     | Start Date                    | Expire Date (With Grace)      |\n" +
                    "|--------------------------------------------------------------------------------------------------|\n");

                foreach (var item in recurrenceResults.Items)
                {
                    response.AppendFormat("| {0,-12} | {1,-5} | {2,-9} | {3,-29} | {4,-29} |\n",
                                            item.ProductId,
                                            item.AutoRenew,
                                            item.RecurrenceState,
                                            item.StartTime.ToString(),
                                            item.ExpirationTimeWithGrace);
                }
            }
            catch (Exception e)
            {
                response.Append(e.Message);
            }
            if(includeJson)
            {
                response.Append("RawResponse:");
                response.Append(JsonConvert.SerializeObject(recurrenceResults));
            }

            var finalResponse = response.ToString();
            return new OkObjectResult(finalResponse);
        }

        /// <summary>
        /// Returns any subscriptions for the UserPurchaseId provided
        /// </summary>
        /// <param name="clientRequest"></param>
        /// <returns></returns>
        [HttpPost]
        public async Task<ActionResult<string>> RecurrenceChange([FromBody] ClientRecurrneceChangeRequest clientRequest)
        {
            //  Check that we have a properly formatted request body
            if (string.IsNullOrEmpty(clientRequest.UserPurchaseId))
            {
                return BadRequest("Request body missing PurchaseId. ex: {\"PurchaseId\": \"...\"}");
            }
            else if (string.IsNullOrEmpty(clientRequest.RecurrenceId))
            {
                return BadRequest("Request body missing RecurrenceId. ex: {\"RecurrenceId\": \"...\"}");
            }
            else if (string.IsNullOrEmpty(clientRequest.ChangeType))
            {
                return BadRequest("Request body missing RecurrenceId. ex: {\"ChangeType\": \"...\"}, Cancel, Extend, Refund, ToggleAutoRenew");
            }


            bool includeJson = false;
            if (Request.Headers.ContainsKey("User-Agent"))
            {
                string userAgent = Request.Headers["User-Agent"];
                if (!string.IsNullOrEmpty(userAgent) && userAgent.Equals("Microsoft.StoreServicesClientSample"))
                {
                    //  This call is from the Client sample that is tied to this sample, so include the added JSON
                    //  in the response body so that it can use those values to update the UI.
                    includeJson = true;
                }
            }

            var response = new StringBuilder("");

            var recurrenceRequest = new RecurrenceChangeRequest
            {
                UserPurchaseId = clientRequest.UserPurchaseId,
                ChangeType = clientRequest.ChangeType,
                RecurrenceId = clientRequest.RecurrenceId
            };

            if (!string.IsNullOrEmpty(clientRequest.sbx))
            {
                recurrenceRequest.SandboxId = clientRequest.sbx;
            }

            if (!string.IsNullOrEmpty(clientRequest.sbx))
            {
                recurrenceRequest.SandboxId = clientRequest.sbx;
            }

            if (clientRequest.ChangeType == RecurrenceChangeType.Extend.ToString())
            {
                recurrenceRequest.ExtensionTimeInDays = clientRequest.ExtensionTime;
            }

            var recurrenceResult = new RecurrenceChangeResponse();
            using (var storeClient = _storeServicesClientFactory.CreateClient())
            {
                recurrenceResult = await storeClient.RecurrenceChangeAysnc(recurrenceRequest);
            }

            try
            {
                response.Append(
                    "| ProductId    | Renew | State     | Start Date                    | Expire Date (With Grace)      |\n" +
                    "|--------------------------------------------------------------------------------------------------|\n");


                response.AppendFormat("| {0,-12} | {1,-5} | {2,-9} | {3,-29} | {4,-29} |\n",
                                      recurrenceResult.ProductId,
                                      recurrenceResult.AutoRenew,
                                      recurrenceResult.RecurrenceState,
                                      recurrenceResult.StartTime.ToString(),
                                      recurrenceResult.ExpirationTimeWithGrace);
                

            }
            catch (Exception e)
            {
                response.Append(e.Message);
            }

            if (includeJson)
            {
                response.Append("RawResponse:");
                response.Append(JsonConvert.SerializeObject(recurrenceResult));
            }

            var finalResponse = response.ToString();
            return new OkObjectResult(finalResponse);
        }

        /// <summary>
        /// Returns any refunds found by the Clawback service for the UserPurchaseId provided
        /// </summary>
        /// <param name="clientRequest"></param>
        /// <returns></returns>
        [HttpPost]
        public async Task<ActionResult<string>> ClawbackQuery([FromBody] ClientPurchaseId clientRequest)
        {
            //  Check that we have a properly formatted request body
            if (string.IsNullOrEmpty(clientRequest.UserPurchaseId))
            {
                return BadRequest("Request body missing PurchaseId. ex: {\"PurchaseId\": \"...\"}");
            }

            var response = new StringBuilder("");

            var clawbackRequest = new ClawbackQueryRequest
            {
                UserPurchaseId = clientRequest.UserPurchaseId
            };

            if (clientRequest.LineItemStateFilter != null )
            {
                clawbackRequest.LineItemStateFilter = clientRequest.LineItemStateFilter;
            }
            else
            {
                clawbackRequest.LineItemStateFilter.Add(LineItemStates.Purchased);
            }

            var clawbackResults = new ClawbackQueryResponse();
            using (var storeClient = _storeServicesClientFactory.CreateClient())
            {
                clawbackResults = await storeClient.ClawbackQueryAsync(clawbackRequest);                                                     
            }

            try
            {
                response.Append(
                    "| ProductId    | Qty | State     | LineItemId                           | Refunded Date                 |\n" +
                    "|-------------------------------------------------------------------------------------------------------|\n");

                foreach (var item in clawbackResults.Items)
                {
                    foreach (var lineItem in item.OrderLineItems)
                    {
                        //  If the item is in the Purchase state, then we don't show the Refund date
                        string refundedDate = "";
                        if(lineItem.LineItemState != LineItemStates.Purchased)
                        {
                            refundedDate = item.OrderRefundedDate.ToString();
                        }

                        response.AppendFormat("| {0,-12} | {1,-3} | {2,-9} | {3,-36} | {4,-29} |\n",
                                              lineItem.ProductId,
                                              lineItem.Quantity,
                                              lineItem.LineItemState,
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

            var clawManager = new ClawbackManager(_config, _storeServicesClientFactory, _logger);
            response.Append(await clawManager.RunClawbackReconciliationAsync(_cV));

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

            var clawManager = new ClawbackManager(_config, _storeServicesClientFactory, _logger);

            var clawbackQueue = clawManager.GetClawbackQueue(_cV);
            foreach (var clawbackItem in clawbackQueue)
            {
                response.AppendFormat("User {0}'s transaction {1} of product {2} for {3} quantity on {4}\n",
                                      clawbackItem.UserId,
                                      clawbackItem.TrackingId,
                                      clawbackItem.ProductId,
                                      clawbackItem.Quantity,
                                      clawbackItem.ConsumeDate);
            }

            FinalizeLoggingCv();
            return new OkObjectResult(response.ToString());
        }

        /// <summary>
        /// NOTE: This is a test API only and should not be part of a production deployment
        /// Full list of clawback action items (all states)
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public ActionResult<string> ViewClawbackActionItems()
        {
            InitializeLoggingCv();
            var response = new StringBuilder("Getting all ClawbackActionItems:\n");

            using (var dbContext = ServerDBController.CreateDbContext(_config, _cV, _logger))
            {
                var actionItems = dbContext.ClawbackActionItems.ToList();

                if (actionItems.Count > 0)
                {
                    response.Append(FormatResponseFromClawbackActionList(actionItems));
                }
                else
                {
                    response.Append("No ClawbackActionItems found");
                }
            }

            FinalizeLoggingCv();
            return new OkObjectResult(response.ToString());
        }

        /// <summary>
        /// NOTE: This is a test API only and should not be part of a production deployment
        /// Full list of all clawback action items in the pending state
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public ActionResult<string> ViewPendingClawbackActionItems()
        {
            InitializeLoggingCv();
            var response = new StringBuilder("Getting ClawbackActionItems that are Pending:\n");

            using (var dbContext = ServerDBController.CreateDbContext(_config, _cV, _logger))
            {
                var pendingItems = dbContext.ClawbackActionItems.Where(
                    b => b.State == ClawbackActionItemState.Pending
                    ).ToList();

                if (pendingItems.Count > 0)
                {
                    response.Append(FormatResponseFromClawbackActionList(pendingItems));
                }
                else
                {
                    response.Append("No ClawbackActionItems found");
                }
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
        public ActionResult<string> ViewBuildingClawbackActionItems()
        {
            InitializeLoggingCv();
            var response = new StringBuilder("Getting ClawbackActionItems that are Building:\n");

            var buildingItems = new List<ClawbackActionItem>();
            using (var dbContext = ServerDBController.CreateDbContext(_config, _cV, _logger))
            {
                buildingItems = dbContext.ClawbackActionItems.Where(
                    b => b.State == ClawbackActionItemState.Building
                    ).ToList();
            }

            if (buildingItems.Count > 0)
            {
                response.Append(FormatResponseFromClawbackActionList(buildingItems));
            }
            else
            {
                response.Append("No ClawbackActionItems found");
            }

            FinalizeLoggingCv();
            return new OkObjectResult(response.ToString());
        }

        /// <summary>
        /// NOTE: This is a test API only and should not be part of a production deployment
        /// Gets full list of all clawback items that have been completed and we took action on
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public ActionResult<string> ViewCompletedClawbackActionItems()
        {
            InitializeLoggingCv();
            var response = new StringBuilder("Getting ClawbackActionItems that are Completed:\n");

            using (var dbContext = ServerDBController.CreateDbContext(_config, _cV, _logger))
            {
                var completedItems = dbContext.ClawbackActionItems.Where(
                    b => b.State == ClawbackActionItemState.Completed
                    ).ToList();

                if (completedItems.Count > 0)
                {
                    response.Append(FormatResponseFromClawbackActionList(completedItems));
                }
                else
                {
                    response.Append("No ClawbackActionItems found");
                }
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
        private static string FormatResponseFromClawbackActionList(List<ClawbackActionItem> actionItems)
        {
            var response = new StringBuilder("");
            response.Append(
                    "| Action State | ProductId    | Quantity | LineItemState | Purchased Date         | Refunded Date          | Candidate Id     | Candidate Consume Date | LineItemId                           |\n" +
                    "|--------------------------------------------------------------------------------------------------------------------------------------------------------------------|\n");

            foreach (var item in actionItems)
            {
                var candidates = item.GetClawbackCandidates();
                var candidate = candidates.First();

                response.AppendFormat("| {0,-12} | {1,-12} | {2,-8} | {3,-13} | {4,-22} | {5,-22} | {6,-16} | {7,-22} | {8,-36} |\n",
                                      item.State,
                                      item.ProductId,
                                      item.Quantity,
                                      item.LineItemState,
                                      item.OrderPurchaseDate,
                                      item.OrderRefundedDate,
                                      candidate.UserId,
                                      candidate.ConsumeDate,
                                      item.LineItemId);

                //  if there are more candidates due to building or pending states, list them below the first entry
                for (int i = 1; i < candidates.Count; i++)
                {
                    response.AppendFormat(
                        "| {0,-12} | {0,-12} | {0,-8} | {0,-13} | {0,-22} | {0,-22} | {1,-16} | {2,-22} | {0,-36} |\n",
                        "",
                        candidates[i].UserId,
                        candidates[i].ConsumeDate);
                }

                response.AppendFormat(
                    "|--------------------------------------------------------------------------------------------------------------------------------------------------------------------|\n\n");
            }

            return response.ToString();
        }
    }
}
