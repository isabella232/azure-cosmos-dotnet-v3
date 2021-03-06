﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------
namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Diagnostics;
    using Microsoft.Azure.Cosmos.Resource.CosmosExceptions;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Collections;

    internal sealed class CosmosHttpClientCore : CosmosHttpClient
    {
        private readonly HttpClient httpClient;
        private readonly ICommunicationEventSource eventSource;

        private bool disposedValue;

        private CosmosHttpClientCore(
            HttpClient httpClient,
            HttpMessageHandler httpMessageHandler,
            ICommunicationEventSource eventSource)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            this.eventSource = eventSource ?? throw new ArgumentNullException(nameof(eventSource));
            this.HttpMessageHandler = httpMessageHandler;
        }

        public override HttpMessageHandler HttpMessageHandler { get; }

        public static CosmosHttpClient CreateWithConnectionPolicy(
            ApiType apiType,
            ICommunicationEventSource eventSource,
            ConnectionPolicy connectionPolicy,
            HttpMessageHandler httpMessageHandler,
            EventHandler<SendingRequestEventArgs> sendingRequestEventArgs,
            EventHandler<ReceivedResponseEventArgs> receivedResponseEventArgs)
        {
            if (connectionPolicy == null)
            {
                throw new ArgumentNullException(nameof(connectionPolicy));
            }

            Func<HttpClient> httpClientFactory = connectionPolicy.HttpClientFactory;
            if (httpClientFactory != null)
            {
                if (sendingRequestEventArgs != null &&
                    receivedResponseEventArgs != null)
                {
                    throw new InvalidOperationException($"{nameof(connectionPolicy.HttpClientFactory)} can not be set at the same time as {nameof(sendingRequestEventArgs)} or {nameof(ReceivedResponseEventArgs)}");
                }

                HttpClient userHttpClient = httpClientFactory.Invoke() ?? throw new ArgumentNullException($"{nameof(httpClientFactory)} returned null. {nameof(httpClientFactory)} must return a HttpClient instance.");
                return CosmosHttpClientCore.CreateHelper(
                    httpClient: userHttpClient,
                    httpMessageHandler: httpMessageHandler,
                    requestTimeout: connectionPolicy.RequestTimeout,
                    userAgentContainer: connectionPolicy.UserAgentContainer,
                    apiType: apiType,
                    eventSource: eventSource);
            }

            if (httpMessageHandler == null)
            {
                httpMessageHandler = CosmosHttpClientCore.CreateHttpClientHandler(
                        gatewayModeMaxConnectionLimit: connectionPolicy.MaxConnectionLimit,
                        webProxy: null);
            }

            if (sendingRequestEventArgs != null ||
                receivedResponseEventArgs != null)
            {
                httpMessageHandler = CosmosHttpClientCore.CreateHttpMessageHandler(
                    httpMessageHandler,
                    sendingRequestEventArgs,
                    receivedResponseEventArgs);
            }

            HttpClient httpClient = new HttpClient(httpMessageHandler);

            return CosmosHttpClientCore.CreateHelper(
                httpClient: httpClient,
                httpMessageHandler: httpMessageHandler,
                requestTimeout: connectionPolicy.RequestTimeout,
                userAgentContainer: connectionPolicy.UserAgentContainer,
                apiType: apiType,
                eventSource: eventSource);
        }

        public static HttpMessageHandler CreateHttpClientHandler(int gatewayModeMaxConnectionLimit, IWebProxy webProxy)
        {
            HttpClientHandler httpClientHandler = new HttpClientHandler();

            // Proxy is only set by users and can cause not supported exception on some platforms
            if (webProxy != null)
            {
                httpClientHandler.Proxy = webProxy;
            }

            // https://docs.microsoft.com/en-us/archive/blogs/timomta/controlling-the-number-of-outgoing-connections-from-httpclient-net-core-or-full-framework
            try
            {
                httpClientHandler.MaxConnectionsPerServer = gatewayModeMaxConnectionLimit;
            }
            // MaxConnectionsPerServer is not supported on some platforms.
            catch (PlatformNotSupportedException)
            {
            }

            return httpClientHandler;
        }

        private static HttpMessageHandler CreateHttpMessageHandler(
            HttpMessageHandler innerHandler,
            EventHandler<SendingRequestEventArgs> sendingRequestEventArgs,
            EventHandler<ReceivedResponseEventArgs> receivedResponseEventArgs)
        {
            return new HttpRequestMessageHandler(
                sendingRequestEventArgs,
                receivedResponseEventArgs,
                innerHandler);
        }

        private static CosmosHttpClient CreateHelper(
            HttpClient httpClient,
            HttpMessageHandler httpMessageHandler,
            TimeSpan requestTimeout,
            UserAgentContainer userAgentContainer,
            ApiType apiType,
            ICommunicationEventSource eventSource)
        {
            if (httpClient == null)
            {
                throw new ArgumentNullException(nameof(httpClient));
            }

            httpClient.Timeout = requestTimeout > CosmosHttpClientCore.GatewayRequestTimeout
                ? requestTimeout
                : CosmosHttpClientCore.GatewayRequestTimeout;
            httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

            httpClient.AddUserAgentHeader(userAgentContainer);
            httpClient.AddApiTypeHeader(apiType);

            // Set requested API version header that can be used for
            // version enforcement.
            httpClient.DefaultRequestHeaders.Add(HttpConstants.HttpHeaders.Version,
                HttpConstants.Versions.CurrentVersion);

            httpClient.DefaultRequestHeaders.Add(HttpConstants.HttpHeaders.Accept, RuntimeConstants.MediaTypes.Json);

            return new CosmosHttpClientCore(
                httpClient,
                httpMessageHandler,
                eventSource);
        }

        public override Task<HttpResponseMessage> GetAsync(
            Uri uri,
            INameValueCollection additionalHeaders,
            ResourceType resourceType,
            HttpTimeoutPolicy timeoutPolicy,
            CosmosDiagnosticsContext diagnosticsContext,
            CancellationToken cancellationToken)
        {
            if (uri == null)
            {
                throw new ArgumentNullException(nameof(uri));
            }

            // GetAsync doesn't let clients to pass in additional headers. So, we are
            // internally using SendAsync and add the additional headers to requestMessage. 
            ValueTask<HttpRequestMessage> CreateRequestMessage()
            {
                HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, uri);
                if (additionalHeaders != null)
                {
                    foreach (string header in additionalHeaders)
                    {
                        if (GatewayStoreClient.IsAllowedRequestHeader(header))
                        {
                            requestMessage.Headers.TryAddWithoutValidation(header, additionalHeaders[header]);
                        }
                    }
                }

                return new ValueTask<HttpRequestMessage>(requestMessage);
            }

            return this.SendHttpAsync(
                CreateRequestMessage,
                resourceType,
                timeoutPolicy,
                diagnosticsContext,
                cancellationToken);
        }

        public override Task<HttpResponseMessage> SendHttpAsync(
            Func<ValueTask<HttpRequestMessage>> createRequestMessageAsync,
            ResourceType resourceType,
            HttpTimeoutPolicy timeoutPolicy,
            CosmosDiagnosticsContext diagnosticsContext,
            CancellationToken cancellationToken)
        {
            if (createRequestMessageAsync == null)
            {
                throw new ArgumentNullException(nameof(createRequestMessageAsync));
            }

            return this.SendHttpHelperAsync(
                createRequestMessageAsync,
                resourceType,
                diagnosticsContext ?? new CosmosDiagnosticsContextCore(),
                timeoutPolicy,
                cancellationToken);
        }

        private async Task<HttpResponseMessage> SendHttpHelperAsync(
            Func<ValueTask<HttpRequestMessage>> createRequestMessageAsync,
            ResourceType resourceType,
            CosmosDiagnosticsContext diagnosticsContext,
            HttpTimeoutPolicy timeoutPolicy,
            CancellationToken cancellationToken)
        {
            bool isDefaultCancellationToken = cancellationToken == default;
            DateTime startDateTimeUtc = DateTime.UtcNow;
            IEnumerator<(TimeSpan requestTimeout, TimeSpan delayForNextRequest)> timeoutEnumerator = timeoutPolicy.TimeoutEnumerator;
            timeoutEnumerator.MoveNext();
            while (true)
            {
                (TimeSpan requestTimeout, TimeSpan delayForNextRequest) = timeoutEnumerator.Current;
                using (HttpRequestMessage requestMessage = await createRequestMessageAsync())
                {
                    // If the default cancellation token is passed then use the timeout policy
                    CancellationTokenSource cancellationTokenSource = null;
                    if (isDefaultCancellationToken)
                    {
                        cancellationTokenSource = new CancellationTokenSource();
                        cancellationTokenSource.CancelAfter(requestTimeout);
                        cancellationToken = cancellationTokenSource.Token;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        using (diagnosticsContext.CreateScope(nameof(CosmosHttpClientCore.SendHttpHelperAsync)))
                        {
                            return await this.ExecuteHttpHelperAsync(
                                requestMessage,
                                resourceType,
                                cancellationToken);
                        }
                    }
                    catch (Exception e)
                    {
                        // Log the error message
                        diagnosticsContext.AddDiagnosticsInternal(
                              new PointOperationStatistics(
                                  activityId: Trace.CorrelationManager.ActivityId.ToString(),
                                  statusCode: HttpStatusCode.ServiceUnavailable,
                                  subStatusCode: SubStatusCodes.Unknown,
                                  responseTimeUtc: DateTime.UtcNow,
                                  requestCharge: 0,
                                  errorMessage: e.ToString(),
                                  method: requestMessage.Method,
                                  requestUri: requestMessage.RequestUri.OriginalString,
                                  requestSessionToken: null,
                                  responseSessionToken: null));

                        bool isOutOfRetries = (DateTime.UtcNow - startDateTimeUtc) > timeoutPolicy.MaximumRetryTimeLimit || // Maximum of time for all retries
                            !timeoutEnumerator.MoveNext(); // No more retries are configured

                        switch (e)
                        {
                            case OperationCanceledException operationCanceledException:
                                // Throw if the user passed in cancellation was requested
                                if (!isDefaultCancellationToken && cancellationToken.IsCancellationRequested)
                                {
                                    throw;
                                }

                                // Convert OperationCanceledException to 408 when the HTTP client throws it. This makes it clear that the 
                                // the request timed out and was not user canceled operation.
                                if (isOutOfRetries || requestMessage.Method != HttpMethod.Get)
                                {
                                    // throw timeout if the cancellationToken is not canceled (i.e. httpClient timed out)
                                    string message =
                                        $"GatewayStoreClient Request Timeout. Start Time UTC:{startDateTimeUtc}; Total Duration:{(DateTime.UtcNow - startDateTimeUtc).TotalMilliseconds} Ms; Request Timeout {requestTimeout.TotalMilliseconds} Ms; Http Client Timeout:{this.httpClient.Timeout.TotalMilliseconds} Ms; Activity id: {Trace.CorrelationManager.ActivityId};";
                                    throw CosmosExceptionFactory.CreateRequestTimeoutException(
                                        message,
                                        innerException: operationCanceledException,
                                        diagnosticsContext: diagnosticsContext);
                                }

                                break;
                            case WebException webException:
                                if (isOutOfRetries || (requestMessage.Method != HttpMethod.Get && !WebExceptionUtility.IsWebExceptionRetriable(webException)))
                                {
                                    throw;
                                }

                                break;
                            case HttpRequestException httpRequestException:
                                if (isOutOfRetries || requestMessage.Method != HttpMethod.Get)
                                {
                                    throw;
                                }

                                break;
                            default:
                                throw;
                        }
                    }
                }

                if (delayForNextRequest != TimeSpan.Zero)
                {
                    using (diagnosticsContext.CreateScope($"HttpRetryDelay; Delay:{delayForNextRequest} seconds; Current request timeout {requestTimeout}; TimeoutPolicy: {timeoutPolicy.TimeoutPolicyName}"))
                    {
                        await Task.Delay(delayForNextRequest);
                    }
                }
            }
        }

        private async Task<HttpResponseMessage> ExecuteHttpHelperAsync(
            HttpRequestMessage requestMessage,
            ResourceType resourceType,
            CancellationToken cancellationToken)
        {
            DateTime sendTimeUtc = DateTime.UtcNow;
            Guid localGuid = Guid.NewGuid(); // For correlating HttpRequest and HttpResponse Traces

            Guid requestedActivityId = Trace.CorrelationManager.ActivityId;
            this.eventSource.Request(
                requestedActivityId,
                localGuid,
                requestMessage.RequestUri.ToString(),
                resourceType.ToResourceTypeString(),
                requestMessage.Headers);

            // Only read the header initially. The content gets copied into a memory stream later
            // if we read the content HTTP client will buffer the message and then it will get buffered
            // again when it is copied to the memory stream.
            HttpResponseMessage responseMessage = await this.httpClient.SendAsync(
                    requestMessage,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

            // WebAssembly HttpClient does not set the RequestMessage property on SendAsync
            if (responseMessage.RequestMessage == null)
            {
                responseMessage.RequestMessage = requestMessage;
            }

            DateTime receivedTimeUtc = DateTime.UtcNow;
            TimeSpan durationTimeSpan = receivedTimeUtc - sendTimeUtc;

            Guid activityId = Guid.Empty;
            if (responseMessage.Headers.TryGetValues(
                HttpConstants.HttpHeaders.ActivityId,
                out IEnumerable<string> headerValues) && headerValues.Any())
            {
                activityId = new Guid(headerValues.First());
            }

            this.eventSource.Response(
                activityId,
                localGuid,
                (short)responseMessage.StatusCode,
                durationTimeSpan.TotalMilliseconds,
                responseMessage.Headers);

            return responseMessage;
        }

        protected override void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                if (disposing)
                {
                    this.httpClient.Dispose();
                }

                this.disposedValue = true;
            }
        }

        public override void Dispose()
        {
            this.Dispose(true);
        }

        private class HttpRequestMessageHandler : DelegatingHandler
        {
            private readonly EventHandler<SendingRequestEventArgs> sendingRequest;
            private readonly EventHandler<ReceivedResponseEventArgs> receivedResponse;

            public HttpRequestMessageHandler(
                EventHandler<SendingRequestEventArgs> sendingRequest,
                EventHandler<ReceivedResponseEventArgs> receivedResponse,
                HttpMessageHandler innerHandler)
            {
                this.sendingRequest = sendingRequest;
                this.receivedResponse = receivedResponse;

                this.InnerHandler = innerHandler ?? throw new ArgumentNullException(
                    $"innerHandler is null. This required for .NET core to limit the http connection. See {nameof(CreateHttpClientHandler)} ");
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                this.sendingRequest?.Invoke(this, new SendingRequestEventArgs(request));
                HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
                this.receivedResponse?.Invoke(this, new ReceivedResponseEventArgs(request, response));
                return response;
            }
        }
    }
}
