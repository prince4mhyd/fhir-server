﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using AngleSharp.Io;
using EnsureThat;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features.Authentication;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Fhir.Api.Features.Bundle;
using Microsoft.Health.Fhir.Api.Features.ContentTypes;
using Microsoft.Health.Fhir.Api.Features.Headers;
using Microsoft.Health.Fhir.Api.Features.Routing;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Messages.Bundle;
using static Hl7.Fhir.Model.Bundle;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Api.Features.Resources.Bundle
{
    /// <summary>
    /// Handler for bundles of type transaction and batch.
    /// </summary>
    public partial class BundleHandler : IRequestHandler<BundleRequest, BundleResponse>
    {
        private readonly IFhirRequestContextAccessor _fhirRequestContextAccessor;
        private readonly FhirJsonSerializer _fhirJsonSerializer;
        private readonly FhirJsonParser _fhirJsonParser;
        private readonly Dictionary<Hl7.Fhir.Model.Bundle.HTTPVerb, List<(RouteContext, int)>> _requests;
        private readonly IHttpAuthenticationFeature _httpAuthenticationFeature;
        private readonly IRouter _router;
        private readonly IServiceProvider _requestServices;
        private readonly ITransactionHandler _transactionHandler;
        private readonly IBundleHttpContextAccessor _bundleHttpContextAccessor;
        private readonly ILogger<BundleHandler> _logger;
        private int _requestCount;
        private readonly Hl7.Fhir.Model.Bundle.HTTPVerb[] _verbExecutionOrder;
        private List<int> emptyRequestsOrder;
        private readonly TransactionValidator _transactionValidator;
        private BundleType? _bundleType;

        public BundleHandler(
            IHttpContextAccessor httpContextAccessor,
            IFhirRequestContextAccessor fhirRequestContextAccessor,
            FhirJsonSerializer fhirJsonSerializer,
            FhirJsonParser fhirJsonParser,
            ITransactionHandler transactionHandler,
            IBundleHttpContextAccessor bundleHttpContextAccessor,
            TransactionValidator transactionValidator,
            ILogger<BundleHandler> logger)
        : this()
        {
            EnsureArg.IsNotNull(httpContextAccessor, nameof(httpContextAccessor));
            EnsureArg.IsNotNull(fhirRequestContextAccessor, nameof(fhirRequestContextAccessor));
            EnsureArg.IsNotNull(fhirJsonSerializer, nameof(fhirJsonSerializer));
            EnsureArg.IsNotNull(fhirJsonParser, nameof(fhirJsonParser));
            EnsureArg.IsNotNull(transactionHandler, nameof(transactionHandler));
            EnsureArg.IsNotNull(bundleHttpContextAccessor, nameof(bundleHttpContextAccessor));
            EnsureArg.IsNotNull(transactionValidator, nameof(transactionValidator));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _fhirRequestContextAccessor = fhirRequestContextAccessor;
            _fhirJsonSerializer = fhirJsonSerializer;
            _fhirJsonParser = fhirJsonParser;
            _transactionHandler = transactionHandler;
            _bundleHttpContextAccessor = bundleHttpContextAccessor;
            _logger = logger;
            _transactionValidator = transactionValidator;

            // Not all versions support the same enum values, so do the dictionary creation in the version specific partial.
            _requests = _verbExecutionOrder.ToDictionary(verb => verb, _ => new List<(RouteContext, int)>());

            _httpAuthenticationFeature = httpContextAccessor.HttpContext.Features.Get<IHttpAuthenticationFeature>();
            _router = httpContextAccessor.HttpContext.GetRouteData().Routers.First();
            _requestServices = httpContextAccessor.HttpContext.RequestServices;
            emptyRequestsOrder = new List<int>();
        }

        private async Task ExecuteAllRequests(Hl7.Fhir.Model.Bundle responseBundle)
        {
            // List is not created initially since it doesn't create a list with _requestCount elements
            EntryComponent[] entryComponents = new EntryComponent[_requestCount];
            responseBundle.Entry = entryComponents.ToList();
            foreach (int emptyRequestOrder in emptyRequestsOrder)
            {
                var entryComponent = new Hl7.Fhir.Model.Bundle.EntryComponent();
                entryComponent.Response = new Hl7.Fhir.Model.Bundle.ResponseComponent
                {
                    Status = ((int)HttpStatusCode.BadRequest).ToString(),
                    Outcome = CreateOperationOutcome(
                            OperationOutcome.IssueSeverity.Error,
                            OperationOutcome.IssueType.Invalid,
                            "Request is empty"),
                };
                responseBundle.Entry[emptyRequestOrder] = entryComponent;
            }

            foreach (Hl7.Fhir.Model.Bundle.HTTPVerb verb in _verbExecutionOrder)
            {
                await ExecuteRequests(responseBundle, verb);
            }
        }

        public async Task<BundleResponse> Handle(BundleRequest bundleRequest, CancellationToken cancellationToken)
        {
            var bundleResource = bundleRequest.Bundle.ToPoco<Hl7.Fhir.Model.Bundle>();
            _bundleType = bundleResource.Type;

            // For resources within a transaction, we need to validate if they are referring to each other and throw an exception in such case.
            await _transactionValidator.ValidateBundle(bundleResource);

            await FillRequestLists(bundleResource.Entry);

            if (bundleResource.Type == Hl7.Fhir.Model.Bundle.BundleType.Batch)
            {
                var responseBundle = new Hl7.Fhir.Model.Bundle
                {
                    Type = Hl7.Fhir.Model.Bundle.BundleType.BatchResponse,
                };

                await ExecuteAllRequests(responseBundle);
                return new BundleResponse(responseBundle.ToResourceElement());
            }
            else if (bundleResource.Type == Hl7.Fhir.Model.Bundle.BundleType.Transaction)
            {
                var responseBundle = new Hl7.Fhir.Model.Bundle
                {
                    Type = Hl7.Fhir.Model.Bundle.BundleType.TransactionResponse,
                };

                return await ExecuteTransactionForAllRequests(responseBundle);
            }

            throw new MethodNotAllowedException(string.Format(Api.Resources.InvalidBundleType, bundleResource.Type));
        }

        private async Task<BundleResponse> ExecuteTransactionForAllRequests(Hl7.Fhir.Model.Bundle responseBundle)
        {
            try
            {
                using (var transaction = _transactionHandler.BeginTransaction())
                {
                    await ExecuteAllRequests(responseBundle);

                    transaction.Complete();
                }
            }
            catch (TransactionAbortedException)
            {
                _logger.LogError("Failed to commit a transaction. Throwing BadRequest as a default exception.");
                throw new TransactionFailedException(Api.Resources.GeneralTransactionFailedError, HttpStatusCode.BadRequest);
            }

            return new BundleResponse(responseBundle.ToResourceElement());
        }

        private async Task FillRequestLists(List<EntryComponent> bundleEntries)
        {
            int order = 0;
            _requestCount = bundleEntries.Count;

            var referenceIdDictionary = new Dictionary<string, string>();

            foreach (Hl7.Fhir.Model.Bundle.EntryComponent entry in bundleEntries)
            {
                if (entry.Request == null || entry.Request.Method == null)
                {
                    emptyRequestsOrder.Add(order++);
                    continue;
                }

                HttpContext httpContext = new DefaultHttpContext { RequestServices = _requestServices };

                // For resources within a transaction, we need to resolve the existing conditional references and update the search URI with the logical ID.
                if (_bundleType == BundleType.Transaction && entry.Resource != null)
                {
                    await ResolveBundleReferencesAsync(entry, referenceIdDictionary);
                }

                httpContext.Features[typeof(IHttpAuthenticationFeature)] = _httpAuthenticationFeature;
                httpContext.Response.Body = new MemoryStream();

                var requestUri = new Uri(_fhirRequestContextAccessor.FhirRequestContext.BaseUri, entry.Request.Url);
                httpContext.Request.Scheme = requestUri.Scheme;
                httpContext.Request.Host = new HostString(requestUri.Host, requestUri.Port);
                httpContext.Request.Path = requestUri.LocalPath;
                httpContext.Request.QueryString = new QueryString(requestUri.Query);
                httpContext.Request.Method = entry.Request.Method.ToString();

                AddHeaderIfNeeded(HeaderNames.IfMatch, entry.Request.IfMatch, httpContext);
                AddHeaderIfNeeded(HeaderNames.IfModifiedSince, entry.Request.IfModifiedSince?.ToString(), httpContext);
                AddHeaderIfNeeded(HeaderNames.IfNoneMatch, entry.Request.IfNoneMatch, httpContext);
                AddHeaderIfNeeded(KnownFhirHeaders.IfNoneExist, entry.Request.IfNoneExist, httpContext);

                if (entry.Request.Method == Hl7.Fhir.Model.Bundle.HTTPVerb.POST ||
                   entry.Request.Method == Hl7.Fhir.Model.Bundle.HTTPVerb.PUT)
                {
                    httpContext.Request.Headers.Add(HeaderNames.ContentType, new StringValues(KnownContentTypes.JsonContentType));

                    var memoryStream = new MemoryStream(_fhirJsonSerializer.SerializeToBytes(entry.Resource));
                    memoryStream.Seek(0, SeekOrigin.Begin);
                    httpContext.Request.Body = memoryStream;
                }

                var routeContext = new RouteContext(httpContext);

                await _router.RouteAsync(routeContext);

                httpContext.Features[typeof(IRoutingFeature)] = new RoutingFeature()
                {
                    RouteData = routeContext.RouteData,
                };

                _requests[entry.Request.Method.Value].Add((routeContext, order++));
            }
        }

        public async Task ResolveBundleReferencesAsync(EntryComponent entry, Dictionary<string, string> referenceIdDictionary)
        {
            IEnumerable<ResourceReference> references = entry.Resource.GetAllChildren<ResourceReference>();

            foreach (ResourceReference reference in references)
            {
                if (referenceIdDictionary.TryGetValue(reference.Reference, out var referenceId))
                {
                    reference.Reference = referenceId;
                }
                else
                {
                    if (reference.Reference.Contains("?", StringComparison.InvariantCulture))
                    {
                        string[] queries = reference.Reference.Split("?");
                        string resourceType = queries[0];
                        string conditionalQueries = queries[1];

                        SearchResult results = await _transactionValidator.GetExistingResourceId(entry.Request.Url, resourceType, conditionalQueries);

                        int? resultCount = results?.Results.Count();

                        if (resultCount == null || resultCount == 0 || resultCount > 1)
                        {
                            throw new RequestNotValidException(string.Format(Api.Resources.InvalidConditionalReference, reference.Reference));
                        }

                        string resourceId = $"{resourceType}/{results.Results.First().Resource.ResourceId}";

                        referenceIdDictionary.Add(reference.Reference, resourceId);

                        reference.Reference = resourceId;
                    }
                }
            }
        }

        private static void AddHeaderIfNeeded(string headerKey, string headerValue, HttpContext httpContext)
        {
            if (!string.IsNullOrWhiteSpace(headerValue))
            {
                httpContext.Request.Headers.Add(headerKey, new StringValues(headerValue));
            }
        }

        private async Task ExecuteRequests(Hl7.Fhir.Model.Bundle responseBundle, Hl7.Fhir.Model.Bundle.HTTPVerb httpVerb)
        {
            foreach ((RouteContext request, int entryIndex) in _requests[httpVerb])
            {
                var entryComponent = new Hl7.Fhir.Model.Bundle.EntryComponent();

                if (request.Handler != null)
                {
                    HttpContext httpContext = request.HttpContext;

                    IFhirRequestContext originalFhirRequestContext = _fhirRequestContextAccessor.FhirRequestContext;

                    request.RouteData.Values.TryGetValue(KnownActionParameterNames.ResourceType, out object resourceType);
                    var newFhirRequestContext = new FhirRequestContext(
                        httpContext.Request.Method,
                        httpContext.Request.GetDisplayUrl(),
                        originalFhirRequestContext.BaseUri.OriginalString,
                        originalFhirRequestContext.CorrelationId,
                        httpContext.Request.Headers,
                        httpContext.Response.Headers,
                        resourceType?.ToString())
                    {
                        Principal = originalFhirRequestContext.Principal,
                    };
                    _fhirRequestContextAccessor.FhirRequestContext = newFhirRequestContext;

                    _bundleHttpContextAccessor.HttpContext = httpContext;

                    await request.Handler.Invoke(httpContext);

                    httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
                    string bodyContent = new StreamReader(httpContext.Response.Body).ReadToEnd();

                    ResponseHeaders responseHeaders = httpContext.Response.GetTypedHeaders();
                    entryComponent.Response = new Hl7.Fhir.Model.Bundle.ResponseComponent
                    {
                        Status = httpContext.Response.StatusCode.ToString(),
                        Location = responseHeaders.Location?.OriginalString,
                        Etag = responseHeaders.ETag?.ToString(),
                        LastModified = responseHeaders.LastModified,
                    };

                    if (!string.IsNullOrWhiteSpace(bodyContent))
                    {
                        var entryComponentResource = _fhirJsonParser.Parse<Resource>(bodyContent);

                        if (entryComponentResource.ResourceType == ResourceType.OperationOutcome)
                        {
                            entryComponent.Response.Outcome = entryComponentResource;
                        }
                        else
                        {
                            entryComponent.Resource = entryComponentResource;
                        }
                    }
                    else
                    {
                        if (httpContext.Response.StatusCode == (int)HttpStatusCode.Forbidden)
                        {
                            entryComponent.Response.Outcome = CreateOperationOutcome(
                                OperationOutcome.IssueSeverity.Error,
                                OperationOutcome.IssueType.Forbidden,
                                Api.Resources.Forbidden);
                        }
                    }
                }
                else
                {
                    entryComponent.Response = new Hl7.Fhir.Model.Bundle.ResponseComponent
                    {
                        Status = ((int)HttpStatusCode.NotFound).ToString(),
                        Outcome = CreateOperationOutcome(
                            OperationOutcome.IssueSeverity.Error,
                            OperationOutcome.IssueType.NotFound,
                            string.Format(Api.Resources.BundleNotFound, $"{request.HttpContext.Request.Path}{request.HttpContext.Request.QueryString}")),
                    };
                }

                if (entryComponent.Response.Outcome != null && responseBundle.Type == Hl7.Fhir.Model.Bundle.BundleType.TransactionResponse)
                {
                    TransactionExceptionHandler.ThrowTransactionException(request.HttpContext.Request.Method, request.HttpContext.Request.Path, entryComponent.Response.Status, (OperationOutcome)entryComponent.Response.Outcome);
                }

                responseBundle.Entry[entryIndex] = entryComponent;
            }
        }

        private Resource CreateOperationOutcome(OperationOutcome.IssueSeverity issueSeverity, OperationOutcome.IssueType issueType, string diagnostics)
        {
            return new OperationOutcome
            {
                Issue = new List<OperationOutcome.IssueComponent>
                {
                    new OperationOutcome.IssueComponent
                    {
                        Severity = issueSeverity,
                        Code = issueType,
                        Diagnostics = diagnostics,
                    },
                },
            };
        }
    }
}
