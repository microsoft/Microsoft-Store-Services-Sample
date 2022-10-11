//-----------------------------------------------------------------------------
// CollectionsController.cs
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
using Microsoft.StoreServices.Collections;
using Microsoft.StoreServices.Collections.V8;
using MicrosoftStoreServicesSample.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace MicrosoftStoreServicesSample.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class CollectionsController : ServiceControllerBase
    {
        private readonly IConfiguration _config;
        public CollectionsController(IConfiguration config,
                                     IStoreServicesClientFactory storeServicesClientFactory,
                                     ILogger<CollectionsController> logger) : 
            base(storeServicesClientFactory, logger)
        {
            _config = config;
        }

        /// <summary>
        /// Sends the access tokens to the client that will be needed to create the
        /// required UserCollectionsId and UserPurchaseId for functional calls
        /// made to the store services.
        /// 
        /// TODO: You will want to likely incorporate this flow into your authorization
        ///       handshake with the client ranter than having a dedicated endpoint to hand
        ///       these out.
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<ActionResult<string>> RetrieveAccessTokens()
        {
            InitializeLoggingCv();
            var response = new ClientAccessTokensResponse
            {
                //  TODO: Replace this code obtaining and noting the UserId with your own
                //        authentication ID system for each user.
                UserID = GetUserId()
            };

            try
            {
                response.AccessTokens = await GetAccessTokens();
            }
            catch (Exception e)
            {
                _logger.ServiceError(_cV.Value, "Error retrieving the access tokens", e);
                FinalizeLoggingCv();
                return "Error retrieving the access tokens";
            }

            //  Send these access tokens to the client for them to then
            //  get the UserCollectionsId and UserPurchaseId and return
            //  them to us
            FinalizeLoggingCv();
            return new OkObjectResult(JsonConvert.SerializeObject(response));
        }

        /// <summary>
        /// Gets the user's current Collections data
        /// </summary>
        /// <param name="clientRequest">Requires at least the UserCollectionsId</param>
        /// <returns>Custom formatted text of the user's collections data</returns>
        [HttpPost]
        public async Task<ActionResult<string>> Query([FromBody] ClientCollectionsQueryRequest clientRequest)
        {
            InitializeLoggingCv();
            var response = new StringBuilder("");
            var trialData = new StringBuilder("");
            bool includeTrialData = false;
            bool err = false;

            //  TODO: Replace this code obtaining and noting the UserId with your own
            //        authentication ID system for each user or have the client just
            //        put the ID you will understand into the API as the UserPartnerID
            if (string.IsNullOrEmpty(GetUserId()))
            {
                response.AppendFormat("Missing {{UserId}} from Authorization header\n");
                err = true;
            }

            //  Check that we have the other parameters for this operation
            if (string.IsNullOrEmpty(clientRequest.UserCollectionsId))
            {
                response.AppendFormat("Request body missing CollectionsId. ex: {\"CollectionsId\": \"...\"}");
                err = true;
            }

            if (err)
            {
                //  We had a bad request so exit here
                Response.StatusCode = (int)HttpStatusCode.BadRequest;
                _logger.QueryError(_cV.Value, GetUserId(), response.ToString(), null);
                return response.ToString();
            }

            bool includeJson = false;
            if (Request.Headers.ContainsKey("User-Agent"))
            {
                string userAgent = Request.Headers["User-Agent"];
                if (!string.IsNullOrEmpty(userAgent) && userAgent == "Microsoft.StoreServicesClientSample")
                {
                    //  This call is from the Client sample that is tied to this sample, so include the added JSON
                    //  in the response body so that it can use those values to update the UI.
                    includeJson = true;
                }
            }
            
            //  Build our query request parameters to the Collections Service
            var queryRequest = new CollectionsV8QueryRequest();

            //  First, add the beneficiary value in the response body that
            //  uses the UserCollectionsId to scope the results to the user
            //  signed into the store on the client.
            var beneficiary = new CollectionsRequestBeneficiary
            {
                Identitytype = "b2b",
                UserCollectionsId = clientRequest.UserCollectionsId,
                LocalTicketReference = ""
            };
            queryRequest.Beneficiaries.Add(beneficiary);

            if (!string.IsNullOrEmpty(clientRequest.Sbx))
            {
                queryRequest.SandboxId = clientRequest.Sbx;
            }

            if (clientRequest.EntitlementFilters != null &&
                clientRequest.EntitlementFilters.Count > 0)
            {
                queryRequest.EntitlementFilters = clientRequest.EntitlementFilters;
            }
            else
            {
                queryRequest.EntitlementFilters.Append(EntitlementFilterTypes.Pass);
            }

            //  TODO: Add any other request filtering that your service requires
            //        For example, filtering to specific ProductIds or product types
            //          
            //        For this sample we will just ask for all Game, Consumable, and
            //        Durable products as configured by default in the QueryRequest()
            //        constructor

            //  Send the request to the Collections service using a StoreServicesClient
            //  This is wrapped in a try/catch to log any exceptions and to format
            //  the response to the client to remove call stack info.
            try
            {
                var collectionsResponse = new CollectionsV8QueryResponse();
                var usersCollection = new List<CollectionsV8Item>();
                using (var storeClient = _storeServicesClientFactory.CreateClient())
                {
                    do
                    {
                        collectionsResponse = await storeClient.CollectionsQueryAsync(queryRequest);

                        //  If there was a continuation token add it to the next cycle.
                        queryRequest.ContinuationToken = collectionsResponse.ContinuationToken; 
                        
                        //  Append the results to our collections list before possibly doing
                        //  another request to get the rest.
                        usersCollection.Concat(collectionsResponse.Items);  
                    
                    } while (collectionsResponse.ContinuationToken != null);
                }

                //  TODO: Operate on the results with your custom logic
                //        For this sample we just iterate through the results, format them to
                //        a readable string and send it back to the client as proof of flow.
                response.Append(
                    "| ProductId    | Qty | Product Kind | Acquisition | IsTrial | Satisfied By |\n" +
                    "|--------------------------------------------------------------------------|\n");

                foreach (var item in usersCollection)
                {

                    //  Some Durable types have a quantity of 1, but for the output we will only show the
                    //  quantity if this is a consumable type product
                    string quantityToDisplay = "";
                    if( item.ProductKind == "UnmanagedConsumable" ||
                        item.ProductKind == "Consumable")
                    {
                        quantityToDisplay = item.Quantity.ToString();
                    }

                    string formattedType = item.ProductKind;

                    if(item.ProductKind == "UnmanagedConsumable")
                    {
                        formattedType = "U.Consumable";
                    }

                    response.AppendFormat("| {0,-12} | {1,-3} | {2,-12} | {3,-11} | {4,-7} ",
                                            item.ProductId,
                                            quantityToDisplay,
                                            formattedType,
                                            item.AcquisitionType,
                                            item.TrialData.IsTrial);

                    //  Check if this is enabled because of a satisfying entitlement from a bundle or subscription
                    //  format to add those to the output on their own lines.
                    if (item.SatisfiedByProductIds.Any())
                    {
                        bool isFirstEntitlement = true;
                        foreach (var parent in item.SatisfiedByProductIds)
                        {
                            if (isFirstEntitlement)
                            {
                                isFirstEntitlement = false;
                                response.AppendFormat("| {0,-12} |\n",
                                                      parent);
                            }
                            else
                            {
                                response.AppendFormat("|                                                   {0,-12} |\n",
                                                      parent);
                            }
                        }
                    }
                    else
                    {
                        response.AppendFormat("|              |\n");
                    }

                    if (item.TrialData.IsTrial)
                    {
                        if(!includeTrialData)
                        {
                            includeTrialData = true;
                            trialData.Append(
                            "| ProductId    | IsInTrialPeriod | Remaining (DD.HH:MM:SS)        |\n" +
                            "|-----------------------------------------------------------------|\n");
                        }

                        string remainingTrialTimeText = string.Format("{0}.{1}:{2}:{3}",
                                                                     item.TrialData.TrialTimeRemaining.Days,
                                                                     item.TrialData.TrialTimeRemaining.Hours,
                                                                     item.TrialData.TrialTimeRemaining.Minutes,
                                                                     item.TrialData.TrialTimeRemaining.Seconds);

                        trialData.AppendFormat("| {0,-12} | {1,-15} | {2,-30} |\n",
                                               item.ProductId,
                                               item.TrialData.IsInTrialPeriod,
                                               remainingTrialTimeText);
                    }
                }

                if(includeTrialData)
                {
                    response.AppendLine("");
                    response.Append(trialData);
                }

                //  If this is from the Client sample, include the JSON so that it can display the items in the UI
                //  properly
                if (includeJson)
                {
                    response.AppendLine("");
                    response.Append("RawResponse: ");
                    response.Append(JsonConvert.SerializeObject(collectionsResponse));
                }

            }
            catch (Exception ex)
            {
                _logger.QueryError(_cV.Value, GetUserId(), "Error querying collections.", ex);
                Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                response.AppendFormat("Unexpected error while querying the collections.  See logs for CV {0}", _cV.Value);
            }

            FinalizeLoggingCv();
            return new OkObjectResult(response.ToString());
        }

        /// <summary>
        /// Consumes a specified quantity of the consumable productID provided
        /// </summary>
        /// <param name="clientRequest">Requires ProductId, Quantity, UserCollectionsId (for Consume request), and UserPurchaseId (for Clawback validation)</param>
        /// <returns>Custom formatted text indicating the result of the consume request</returns>
        [HttpPost]
        public async Task<ActionResult<string>> Consume([FromBody] ClientConsumeRequest clientRequest)
        {
            //  Must call this to get the cV for this call flow
            InitializeLoggingCv();
            var response = new StringBuilder("");

            PendingConsumeRequest pendingRequest;
            var consumeManager = new ConsumableManager(_config, _storeServicesClientFactory, _logger);

            try
            {
                //  TODO: Replace this code obtaining and noting the UserId with your own
                //        authentication ID system for each user or have the client just
                //        put the ID you will understand into the API as the UserPartnerID
                if (string.IsNullOrEmpty(GetUserId()))
                {
                    throw new ArgumentException("No UserId in request header", nameof(clientRequest));
                }
                
                pendingRequest = ConsumableManager.CreateAndVerifyPendingConsumeRequest(clientRequest);
            }
            catch (ArgumentException ex)
            {
                //  We had a bad request so exit here
                Response.StatusCode = (int)HttpStatusCode.BadRequest;
                _logger.ConsumeError(_cV.Value, GetUserId(), "", "", 0, ex.Message, ex);
                return ex.Message;
            }

            //  call our helper function to manage the call controller
            response.Append(await consumeManager.ConsumeAsync(pendingRequest, _cV));

            FinalizeLoggingCv();
            return new OkObjectResult(response.ToString());
        }

        ////////////////////////////////////////////////////////////////////
        //  Testing Endpoints - Not for RETAIL release
        ////////////////////////////////////////////////////////////////////
        //  TODO: Remove these APIs if you are using this as a framework to
        //        build your service from.  These are only test endpoints to
        //        help demonstrate how the service handles pending consumes
        //        and clawback searches.
        ////////////////////////////////////////////////////////////////////

        /// <summary>
        /// NOTE: This is a test API only and should not be part of a production deployment
        /// 
        /// Adds a pending consume to the cache to simulate that a request was made but a response
        /// was not received and we need to replay the request to see what the result was.
        /// </summary>
        /// <param name="clientRequest">Requires ProductId, Quantity, UserCollectionsId (for Consume request), and UserPurchaseId (for Clawback validation)</param>
        /// <returns>Custom formatted text indicating the result of the consume request</returns>
        [HttpPost]
        public async Task<ActionResult<string>> AddPendingConsume([FromBody] ClientConsumeRequest clientRequest)
        {
            //  Must call this to get the cV for this call flow
            InitializeLoggingCv();
            var response = new StringBuilder("");
            var pendingRequest = new PendingConsumeRequest();

            try
            {
                //  TODO: Replace this code obtaining and noting the UserId with your own
                //        authentication ID system for each user or have the client just
                //        put the ID you will understand into the API as the UserPartnerID
                if (string.IsNullOrEmpty(GetUserId()))
                {
                    throw new ArgumentException("No UserId in request header", nameof(clientRequest));
                }

                pendingRequest = ConsumableManager.CreateAndVerifyPendingConsumeRequest(clientRequest);
            }
            catch (ArgumentException ex)
            {
                //  We had a bad request so exit here
                Response.StatusCode = (int)HttpStatusCode.BadRequest;
                _logger.ConsumeError(_cV.Value,
                                     GetUserId(),
                                     pendingRequest.TrackingId,
                                     pendingRequest.ProductId,
                                     pendingRequest.RemoveQuantity,
                                     ex.Message,
                                     ex);
                return new OkObjectResult("Error consuming the request");
            }

            //  This is only a test function here so that we can cache
            //  some pending consumes as if we tried but did not get a
            //  response back.  You can then call the RetryPendingConsumes
            //  endpoint to validate how the service would handle this
            //  scenario.
            var consumeManager = new ConsumableManager(_config, _storeServicesClientFactory, _logger);
            await consumeManager.TrackPendingConsumeAsync(pendingRequest, _cV);

            response.Append("Consume added to Pending cache, but not sent to Collections\n");
            response.AppendFormat("TrackingId:{0}\nUser:{1}\nConsumable:{2}\nQuantity:{3}\nSandboxId:{4}",
                                  pendingRequest.TrackingId,
                                  pendingRequest.UserId,
                                  pendingRequest.ProductId,
                                  pendingRequest.RemoveQuantity,
                                  pendingRequest.SandboxId);

            FinalizeLoggingCv();
            return new OkObjectResult(response.ToString());
        }

        /// <summary>
        /// NOTE: This is a test API only and should not be part of a production deployment
        /// 
        /// Returns to the caller all of the current balances of consumed products based
        /// on the UserIds the server is tracking
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public ActionResult<string> ViewPendingConsumes()
        {
            InitializeLoggingCv();

            var response = new StringBuilder("Pending consume requests:\n");

            var consumeManager = new ConsumableManager(_config, _storeServicesClientFactory, _logger);
            var pendingConsumes = consumeManager.GetAllPendingRequests(_cV);

            foreach (var consumeRequest in pendingConsumes)
            {
                response.AppendFormat("TrackingId {0} for UserId {1} on product {2} quantity of {3} in {4}\n",
                                      consumeRequest.TrackingId,
                                      consumeRequest.UserId,
                                      consumeRequest.ProductId,
                                      consumeRequest.RemoveQuantity,
                                      consumeRequest.SandboxId);
            }

            FinalizeLoggingCv();
            return new OkObjectResult(response.ToString());
        }

        /// <summary>
        /// NOTE: This is a test API only and should not be part of a production deployment
        /// Looks for any pending transactions of consumables that have not completed and attempts to
        /// retry each of them.  This is a test API and would not be exposed in an actual Service
        /// deployment.
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<ActionResult<string>> RetryPendingConsumes()
        {
            //  Must call this to get the cV for this call flow
            InitializeLoggingCv();

            var response = new StringBuilder("");

            response.AppendFormat("Finding all pending consume calls...\n");

            var consumeManager = new ConsumableManager(_config, _storeServicesClientFactory, _logger);
            List<PendingConsumeRequest> pendingUserConsumeRequests = consumeManager.GetAllPendingRequests(_cV);

            response.AppendFormat("Found {0} pending consume request(s) to complete or verify...\n", pendingUserConsumeRequests.Count);
            foreach (var currentRequest in pendingUserConsumeRequests)
            {
                //  Make the actual consume call
                response.Append(await consumeManager.ConsumeAsync(currentRequest, _cV));
            }

            var finalResponse = response.ToString();
            _logger.RetryPendingConsumesResponse(_cV.Increment(), finalResponse);

            FinalizeLoggingCv();
            return new OkObjectResult(finalResponse);
        }

        /// <summary>
        /// NOTE: This is a test API only and should not be part of a production deployment
        /// 
        /// Returns to the caller all of the current balances of consumed products based
        /// on the UserIds the server is tracking
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public ActionResult<string> ViewUserBalances()
        {
            InitializeLoggingCv();

            var consumeManager = new ConsumableManager(_config, _storeServicesClientFactory, _logger);
            var userBalances = consumeManager.GetAllUserBalances(_cV);
            var response = new StringBuilder("User balances from consumed items:\n");

            foreach (var userBalance in userBalances)
            {
                response.AppendFormat("User {0}'s balance of {1} is {2}\n",
                                      userBalance.UserId,
                                      userBalance.ProductId,
                                      userBalance.Quantity);
            }

            FinalizeLoggingCv();
            return new OkObjectResult(response.ToString());
        }
    }
}
