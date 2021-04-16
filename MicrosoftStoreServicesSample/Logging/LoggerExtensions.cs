//-----------------------------------------------------------------------------
// XstsLoggerExtensions.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
//-----------------------------------------------------------------------------

using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Text;

namespace MicrosoftStoreServicesSample
{
    //  This sample is using LoggerMessage as outlined in the following article:
    //  https://docs.microsoft.com/en-us/aspnet/core/fundamentals/logging/loggermessage?view=aspnetcore-2.1
    //
    //  As part of this, the format of the logging strings is pre-defined in the static class below rather
    //  than at each point in the code using mLogger.LogError or mLogger.LogInformation.  This provides better
    //  performance even if it saves a small amount and allows all of the logging formatting to be managed
    //  in a single file rather than hundreds of lines spread out throughout the code.
    public class LogEventIds
    {
        //  Server and services
        public const int Service            = 1000;
        public const int Startup            = 1010;
        public const int Collections        = 1020;
        public const int CollectionsQuery   = 1021;
        public const int CollectionsConsume = 1022;
        public const int CollectionsRetry   = 1024; 
        public const int Clawback           = 1030; 
        public const int TransactionDB      = 1040;
    }

    public static class LoggerExtensions
    {
        //  Service specific logging actions
        private static readonly Action<ILogger, string, string, Exception> _startupError;
        private static readonly Action<ILogger, string, string, Exception> _startupInfo;
        private static readonly Action<ILogger, string, string, Exception> _startupWarning;
        private static readonly Action<ILogger, string, string, Exception> _serviceError;
        private static readonly Action<ILogger, string, string, Exception> _serviceInfo;
        private static readonly Action<ILogger, string, string, Exception> _serviceWarning;
        private static readonly Action<ILogger, string, string, string, string, Exception> _collectionsInvalidRequest;
        private static readonly Action<ILogger, string, string, string, string, uint, Exception> _removePendingTransaction;
        private static readonly Action<ILogger, string, string, string, string, uint, Exception> _addPendingTransaction;
        private static readonly Action<ILogger, string, string, string, string, uint, Exception> _addConsumeToClawbackQueue;
        private static readonly Action<ILogger, string, string, string, Exception> _queryResponse;
        private static readonly Action<ILogger, string, string, string, Exception> _queryError;
        private static readonly Action<ILogger, string, string, string, Exception> _consumeResponse;
        private static readonly Action<ILogger, string, string, string, string, uint, string, Exception> _consumeError;
        private static readonly Action<ILogger, string, string, Exception> _retryPendingConsumesResponse;

        private static string SanitizeLineEndings(string str)
        {
            //  this is so the strings we are logging are json formatting compatible
            return str.Replace("\n", "\\\\n").Replace("\r", "\\\\r").Replace("\t", "\\\\t");
        }

        private static string SanitizeQuotes(string str)
        {
            //  Used for formatting json that will be included as a string
            //  in another json structure
            var a = str.Replace("\"", "\\\"").Replace("\'", "\\\'");
            return a;
        }

        //  This is where we define the static format for each of our logging APIs
        static LoggerExtensions()
        {
            _startupInfo = LoggerMessage.Define<string, string>(
                LogLevel.Information,
                new EventId(LogEventIds.Startup, nameof(StartupInfo)),
                "{{\"cV\":\"{cV}\",\"info\":\"{info}\"}}");

            _startupError = LoggerMessage.Define<string, string>(
                LogLevel.Critical,
                new EventId(LogEventIds.Startup, nameof(StartupError)),
                "{{\"cV\":\"{cV}\",\"error\":\"{error}\"}}");

            _startupWarning = LoggerMessage.Define<string, string>(
                LogLevel.Warning,
                new EventId(LogEventIds.Startup, nameof(StartupError)),
                "{{\"cV\":\"{cV}\",\"error\":\"{error}\"}}");

            _serviceInfo = LoggerMessage.Define<string, string>(
                LogLevel.Information,
                new EventId(LogEventIds.Service, nameof(StartupInfo)),
                "{{\"cV\":\"{cV}\",\"info\":\"{info}\"}}");

            _serviceError = LoggerMessage.Define<string, string>(
                LogLevel.Critical,
                new EventId(LogEventIds.Service, nameof(StartupError)),
                "{{\"cV\":\"{cV}\",\"error\":\"{error}\"}}");

            _serviceWarning = LoggerMessage.Define<string, string>(
                LogLevel.Warning,
                new EventId(LogEventIds.Service, nameof(StartupError)),
                "{{\"cV\":\"{cV}\",\"error\":\"{error}\"}}");

            _collectionsInvalidRequest = LoggerMessage.Define<string, string, string, string>(
                LogLevel.Warning,
                new EventId(LogEventIds.Collections, nameof(CollectionsInvalidRequest)),
                "{{\"cV\":\"{cV}\",\"userId\":\"{userId}\",\"error\":\"{error}\",\"urlPath\":\"{urlPath}\"}}");

            _removePendingTransaction = LoggerMessage.Define<string, string, string, string, uint>(
                LogLevel.Information,
                new EventId(LogEventIds.TransactionDB, nameof(RemovePendingTransaction)),
                "{{\"cV\":\"{cV}\",\"userId\":\"{userId}\",\"transaction\":\"{transaction}\",\"product\":\"{product}\",\"quantity\":{quantity},\"status\":\"removed\"}}");

            _addPendingTransaction = LoggerMessage.Define<string, string, string, string, uint>(
                LogLevel.Information,
                new EventId(LogEventIds.TransactionDB, nameof(AddPendingTransaction)),
                "{{\"cV\":\"{cV}\",\"userId\":\"{userId}\",\"transaction\":\"{transaction}\",\"product\":\"{product}\",\"quantity\":{quantity},\"status\":\"pending\"}}");

            _addConsumeToClawbackQueue = LoggerMessage.Define<string, string, string, string, uint>(
                LogLevel.Information,
                new EventId(LogEventIds.Clawback, nameof(AddConsumeToClawbackQueue)),
                "{{\"cV\":\"{cV}\",\"userId\":\"{userId}\",\"transaction\":\"{transaction}\",\"product\":\"{product}\",\"quantity\":{quantity},\"status\":\"queued\"}}");

            _queryResponse = LoggerMessage.Define<string, string, string>(
                LogLevel.Information,
                new EventId(LogEventIds.CollectionsQuery, nameof(QueryResponse)),
                "{{\"cV\":\"{cV}\",\"userId\":\"{userId}\",\"response\":\"{response}\"}}");

            _queryError = LoggerMessage.Define<string, string, string>(
                LogLevel.Error,
                new EventId(LogEventIds.CollectionsQuery, nameof(QueryError)),
                "{{\"cV\":\"{cV}\",\"userId\":\"{userId}\",\"status\":\"{message}\"}}");

            _consumeResponse = LoggerMessage.Define<string, string, string>(
                LogLevel.Information,
                new EventId(LogEventIds.CollectionsConsume, nameof(ConsumeResponse)),
                "{{\"cV\":\"{cV}\",\"userId\":\"{userId}\",\"response\":\"{response}\"}}");

            _consumeError = LoggerMessage.Define<string, string, string, string, uint, string>(
                LogLevel.Error,
                new EventId(LogEventIds.CollectionsConsume, nameof(ConsumeError)),
                "{{\"cV\":\"{cV}\",\"userId\":\"{userId}\",\"transaction\":\"{transaction}\",\"product\":\"{product}\",\"quantity\":{quantity},\"status\":\"{message}\"}}");

            _retryPendingConsumesResponse = LoggerMessage.Define<string, string>(
                LogLevel.Information,
                new EventId(LogEventIds.CollectionsRetry, nameof(RetryPendingConsumesResponse)),
                "{{\"cV\":\"{cV}\",\"response\":\"{response}\"}}");
        }

        public static string FormatResponseForLogs(HttpResponseMessage response, string responseBody)
        {
            var responseString = new StringBuilder();
            responseString.AppendFormat("{0}: {1}", (int)response.StatusCode, response.ReasonPhrase);
            responseString.AppendLine();
            responseString.Append(response.Headers.ToString());
            responseString.AppendLine();
            responseString.Append(SanitizeQuotes(responseBody));
            return responseString.ToString();
        }

        //  Creates a string that can be used in Fiddler's Scratch pad to replay the exact same call for debugging
        public static string FormatRequestForLogs(HttpRequestMessage request, string requestBody)
        {
            var requestString = new StringBuilder();
            requestString.AppendFormat("{0} {1} HTTP/1.2", request.Method, request.RequestUri.AbsoluteUri);
            requestString.AppendLine();
            requestString.Append(request.Headers.ToString());
            if(request.Content != null)
            {
                requestString.Append(request.Content.Headers.ToString());
            }
            if(request.Method != HttpMethod.Get)
            { 
                requestString.AppendLine();
                requestString.Append(SanitizeQuotes(requestBody));     // Had to do this for now as the request.Content object has already been disposed
                                                                       // at this point.  otherwise we would use Content.ReadAsStringAsync() as above
            }
            return requestString.ToString();
        }

        //  Controller / service specific logging functions
        public static void StartupInfo(this ILogger logger, string cV, string info)
        {
            _startupInfo(logger, cV, info, null);
        }

        public static void StartupError(this ILogger logger, string cV, string info, Exception ex)
        {
            _startupError(logger, cV, info, ex);
        }
        public static void StartupWarning(this ILogger logger, string cV, string info, Exception ex)
        {
            _startupWarning(logger, cV, info, ex);
        }

        public static void ServiceWarning(this ILogger logger, string cV, string info, Exception ex)
        {
            _serviceWarning(logger, cV, info, ex);
        }

        public static void ServiceInfo(this ILogger logger, string cV, string info)
        {
            _serviceInfo(logger, cV, info, null);
        }

        public static void ServiceError(this ILogger logger, string cV, string info, Exception ex)
        {
            _serviceError(logger, cV, info, ex);
        }

        public static void CollectionsInvalidRequest(this ILogger logger, string cV, string userId, string error, string urlPath)
        {
            _collectionsInvalidRequest(logger, cV, userId, error, urlPath, null);
        }

        public static void AddPendingTransaction(this ILogger logger, string cV, string userId, string transactionId, string productId, uint quantity )
        {
            _addPendingTransaction(logger, cV, userId, transactionId, productId, quantity, null);
        }

        public static void AddConsumeToClawbackQueue(this ILogger logger, string cV, string userId, string transactionId, string productId, uint quantity)
        {
            _addConsumeToClawbackQueue(logger, cV, userId, transactionId, productId, quantity, null);
        }

        public static void RemovePendingTransaction(this ILogger logger, string cV, string userId, string transactionId, string productId, uint quantity)
        {
            _removePendingTransaction(logger, cV, userId, transactionId, productId, quantity, null);
        }

        public static void QueryResponse(this ILogger logger, string cV, string userId, string response)
        {
            _queryResponse(logger, cV, userId, SanitizeLineEndings(response), null);
        }

        public static void QueryError(this ILogger logger, string cV, string userId, string response, Exception ex)
        {
            _queryError(logger, cV, userId, SanitizeLineEndings(response), ex);
        }

        public static void ConsumeResponse(this ILogger logger, string cV, string userId, string response)
        {
            _queryResponse(logger, cV, userId, SanitizeLineEndings(response), null);
        }

        public static void ConsumeError(this ILogger logger, string cV, string userId, string transactionId, string productId, uint quantity, string message, Exception ex)
        {
            _consumeError(logger, cV, userId, transactionId, productId, quantity, SanitizeLineEndings(message), ex);
        }

        public static void RetryPendingConsumesResponse(this ILogger logger, string cV, string response)
        {
            _retryPendingConsumesResponse(logger, cV, SanitizeLineEndings(response), null);
        }
    }
}