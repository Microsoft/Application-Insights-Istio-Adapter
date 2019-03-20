﻿namespace Microsoft.IstioMixerPlugin.Library
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.Contracts;
    using System.Globalization;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Web;
    using System.Xml.Linq;
    using ApplicationInsights.Channel;
    using ApplicationInsights.DataContracts;
    using ApplicationInsights.Extensibility.Implementation;
    using Common;
    using Google.Protobuf;
    using Tracespan;
    using static System.FormattableString;

    internal class TelemetryGenerator
    {
        private readonly string targetNamspace;

        private static readonly Random rand = new Random();
        private static readonly object sync = new object();

        private static readonly uint[] lookup32 = CreateLookup32();

        private static readonly Regex requestContextAppIdRegex = new Regex("^appId=(?<appId>.+)$", RegexOptions.Compiled);

        public TelemetryGenerator(string targetNamespace)
        {
            this.targetNamspace = targetNamespace ?? string.Empty;
        }

        public IEnumerable<ITelemetry> Generate(params InstanceMsg[] instances)
        {
            return instances.SelectMany(this.Generate);
        }

        /// <summary>
        /// Generates a set of AI telemetry based on a Mixer instance
        /// </summary>
        /// <param name="instance"></param>
        /// <returns></returns>
        public IEnumerable<ITelemetry> Generate(InstanceMsg instance)
        {
            //!!! perf, consider removing in production
            var debugInfo = instance.ToString();
            
            var contextReporterUid = instance.SpanTags["context.reporter.uid"].StringValue;
            var contextReporterKind = instance.SpanTags["context.reporter.kind"].StringValue.ToLowerInvariant();

            var contextProtocol = instance.SpanTags["context.protocol"].StringValue.ToLowerInvariant();

            var connectionEvent = instance.SpanTags["connection.event"].StringValue.ToLowerInvariant();

            var sourceUid = instance.SpanTags["source.uid"].StringValue;
            var destinationUid = instance.SpanTags["destination.uid"].StringValue;

            var sourceWorkloadNamespace = instance.SpanTags["source.workload.namespace"].StringValue;
            var sourceWorkloadName = instance.SpanTags["source.workload.name"].StringValue;

            var destinationWorkloadNamespace = instance.SpanTags["destination.workload.namespace"].StringValue;
            var destinationWorkloadName = instance.SpanTags["destination.workload.name"].StringValue;

            var sourceAppInsightsMonitoringEnabled = instance.SpanTags["source.labels.appinsights.monitoring.enabled"].StringValue;
            var destinationAppInsightsMonitoringEnabled = instance.SpanTags["destination.labels.appinsights.monitoring.enabled"].StringValue;

            // sourceAppInsightsMonitoringIsGateway = strings.ToLower(instance.SpanTags["source.labels.appinsights.monitoring.isgateway"].StringValue;) == "true"
            var isSourceIstioIngressGateway = instance.SpanTags["source.labels.istio.isingressgateway"].BoolValue;

            var requestHeadersRequestId = instance.SpanTags["request.headers.request.id"].StringValue;
            var requestHeadersSyntheticTestRunId = instance.SpanTags["request.headers.synthetictest.runid"].StringValue;
            var requestHeadersSyntheticTestLocation = instance.SpanTags["request.headers.synthetictest.location"].StringValue;

            var requestHeadersRequestContext = instance.SpanTags["request.headers.request.context"].StringValue;
            var responseHeadersRequestContext = instance.SpanTags["response.headers.request.context"].StringValue;
            
            string requestHeadersRequestContextAppId = string.Empty;
            if (!string.IsNullOrWhiteSpace(requestHeadersRequestContext))
            {
                // Regex.Match is thread-safe
                Match requestHeadersRequestContextAppIdMatch = requestContextAppIdRegex.Match(requestHeadersRequestContext);
                Group group = requestHeadersRequestContextAppIdMatch.Groups["appId"];
                if (requestHeadersRequestContextAppIdMatch.Success && group.Success && group.Captures.Count == 1)
                {
                    requestHeadersRequestContextAppId = requestHeadersRequestContextAppIdMatch.Groups["appId"].Value;
                }
            }

            string responseHeadersRequestContextAppId = string.Empty;
            if (!string.IsNullOrWhiteSpace(responseHeadersRequestContext))
            {
                // Regex.Match is thread-safe
                Match responseHeadersRequestContextAppIdMatch = requestContextAppIdRegex.Match(responseHeadersRequestContext);
                Group group = responseHeadersRequestContextAppIdMatch.Groups["appId"];
                if (responseHeadersRequestContextAppIdMatch.Success && group.Success && group.Captures.Count == 1)
                {
                    responseHeadersRequestContextAppId = responseHeadersRequestContextAppIdMatch.Groups["appId"].Value;
                }
            }

            //!!! is this necessary? Also, perf
            var strippedSourceRoleName = instance.SpanTags["source.role.name"].StringValue.ToLowerInvariant().Replace("kubernetes://", string.Empty);
            var strippedSourceRoleInstance = instance.SpanTags["source.role.instance"].StringValue.ToLowerInvariant().Replace("kubernetes://", string.Empty);
            var strippedDestinationRoleName = instance.SpanTags["destination.role.name"].StringValue.ToLowerInvariant().Replace("kubernetes://", string.Empty);
            var strippedDestinationRoleInstance = instance.SpanTags["destination.role.instance"].StringValue.ToLowerInvariant().Replace("kubernetes://", string.Empty);

            var httpUserAgent = instance.SpanTags["http.useragent"].StringValue;
            var host = instance.SpanTags["host"].StringValue;
            var httpStatusCode = instance.SpanTags["http.status_code"].Int64Value;
            var httpPath = instance.SpanTags["http.path"].StringValue;
            var httpMethod = instance.SpanTags["http.method"].StringValue;

            var requestScheme = instance.SpanTags["request.scheme"].StringValue;
            
            var destinationPort = instance.SpanTags["destination.port"].Int64Value;
            
            // if source/desination information is insufficient, let's assume it's an external caller/dependency and use whatever little we know about them
            if (string.IsNullOrWhiteSpace(strippedSourceRoleName) || string.Equals(strippedSourceRoleName, "unknown", StringComparison.InvariantCultureIgnoreCase))
            {
                strippedSourceRoleName = httpUserAgent;
            }

            if (string.IsNullOrWhiteSpace(strippedSourceRoleInstance) || string.Equals(strippedSourceRoleInstance, "unknown", StringComparison.InvariantCultureIgnoreCase))
            {
                strippedSourceRoleInstance = httpUserAgent;
            }

            if (string.IsNullOrWhiteSpace(strippedDestinationRoleName) || string.Equals(strippedDestinationRoleName, "unknown", StringComparison.InvariantCultureIgnoreCase))
            {
                strippedDestinationRoleName = host;
            }

            if (string.IsNullOrWhiteSpace(strippedDestinationRoleInstance) || string.Equals(strippedDestinationRoleInstance, "unknown", StringComparison.InvariantCultureIgnoreCase))
            {
                strippedDestinationRoleInstance = host;
            }

            // if none of the workloads have anything to do with us, skip the instance completely
            this.DetermineInterest(contextReporterUid, sourceWorkloadNamespace, destinationWorkloadNamespace, sourceAppInsightsMonitoringEnabled, destinationAppInsightsMonitoringEnabled,
                out bool isInstanceInteresting, out bool isInstanceFullyWithinTargetArea, out bool isSourceWithinTargetArea, out bool isDestinationWithinTargetArea);

            Diagnostics.LogTrace($"source: {sourceWorkloadName}, destination: {destinationWorkloadName}, reporter: {contextReporterUid}, kind: {contextReporterKind}");

            if (contextProtocol == "tcp")
            {
                //!!!
                Diagnostics.LogTrace(Invariant($"TCP. {debugInfo}"));
            }

            //!!! do we want TCP?
            if (!isInstanceInteresting || (contextProtocol == "tcp" && connectionEvent != "open"))
            {
                Diagnostics.LogTrace("SKIPPED");

                yield break;
            }

            // Request-Id header format https://github.com/dotnet/corefx/blob/master/src/System.Diagnostics.DiagnosticSource/src/HierarchicalRequestId.md
            bool incomingRequestIdPresent = !string.IsNullOrWhiteSpace(requestHeadersRequestId);
            string incomingRequestId = incomingRequestIdPresent ? requestHeadersRequestId : Invariant($"|{RandomStringLong()}.");

            string url = Invariant(
                $@"{contextProtocol}{(string.IsNullOrWhiteSpace(contextProtocol) ? string.Empty : "://")}{strippedDestinationRoleInstance}{httpPath}{
                        (destinationPort > 0 ? $":{destinationPort}" : string.Empty)
                    }");

            long code = httpStatusCode == 0 ? 0 : (httpStatusCode >= 400 ? httpStatusCode : 0);

            // this instance signifies a dependency call (client span) for the source and a request (server span) for the destination
            // from the distributed tracing perspective, it's dependency call => request
            // we need to report both

            // each instance where both workloads are within our target area of interest will be reported twice - by the two proxies it passes through
            // for these calls, we'll arbitrarily choose to act only on the instance supplied by the inbound proxy to avoid duplication

            // each instance where the source is within our target area and the destination is not may be reported twice
            // for these calls, we'll only act on the outbound proxy

            // each instance where the source is not within our target area and the destination is may be reported twice
            // for these calls, we'll only act on the inbound proxy
            bool instanceIsActionable = isInstanceFullyWithinTargetArea && string.Equals(contextReporterKind, "inbound", StringComparison.InvariantCultureIgnoreCase) ||
                                        isSourceWithinTargetArea && !isDestinationWithinTargetArea && string.Equals(contextReporterKind, "outbound", StringComparison.InvariantCultureIgnoreCase) ||
                                        !isSourceWithinTargetArea && isDestinationWithinTargetArea && string.Equals(contextReporterKind, "inbound", StringComparison.InvariantCultureIgnoreCase);

            if (instanceIsActionable)
            {
                var debugData = new Dictionary<string, string>()
                {
                    {"aks.debug.context.reporter.kind", contextReporterKind},
                    {"aks.debug.context.reporter.uid", contextReporterUid},
                    {"aks.debug.context.source.uid", sourceUid},
                    {"aks.debug.context.destination.uid", destinationUid},
                    {"aks.debug.info", debugInfo}
                };

                DateTimeOffset? startTime = instance.StartTime?.Value?.ToDateTimeOffset();
                DateTimeOffset? endTime = instance.EndTime?.Value?.ToDateTimeOffset();

                string latestRequestId = incomingRequestId;

                // if the source is an Istio Ingress Gateway we want to generate an additional pair of dependency/request on its behalf
                // as if it's within our target area (we want to see it on the App Map)
                if (isSourceIstioIngressGateway)
                {
                    // we are generating a request/dependency pair for the Istio Ingress Gateway
                    // the instance represents the call: gateway -> pod (reported by inbound proxy of the pod)

                    // on behalf of the gateway, report server span (request)
                    // the request will represent the gateway's side of the call: internet -> gateway
                    string gatewayRequestId = incomingRequestIdPresent ? AcknowledgeRequest(incomingRequestId) : incomingRequestId;
                    yield return this.GenerateRequest(spanName: strippedDestinationRoleInstance,
                        requestId: gatewayRequestId,
                        parentRequestId: incomingRequestIdPresent ? incomingRequestId : string.Empty,
                        spanStatusCode: code, spanStatusMessage: string.Empty, startTime: startTime, endTime: endTime,
                        roleName: strippedSourceRoleName, 
                        roleInstance: strippedSourceRoleInstance, 
                        httpUrl: url, httpHost: host,
                        httpStatusCode: httpStatusCode.ToString(CultureInfo.InvariantCulture), httpPath: httpPath, httpMethod: httpMethod, httpPort: destinationPort, httpScheme: requestScheme,
                        httpUserAgent: httpUserAgent, httpRoute: string.Empty, requestHeadersRequestContextAppId: requestHeadersRequestContextAppId, requestHeadersSyntheticTestRunId: requestHeadersSyntheticTestRunId,
                        requestHeadersSyntheticTestLocation: requestHeadersSyntheticTestLocation, propagateSyntheticContext: true, debugData: debugData);
                    
                    // on behalf of the Gateway, report client span (dependency call)
                    // the dependency will represent the gateway's side of the call: gateway -> pod
                    string requestId = StartDependency(gatewayRequestId);
                    yield return this.GenerateDependency(spanName: strippedDestinationRoleInstance, 
                        requestId: requestId,
                        parentRequestId: gatewayRequestId,
                        spanStatusCode: code, spanStatusMessage: string.Empty, startTime: startTime,
                        endTime: endTime,
                        roleName: strippedSourceRoleName,
                        roleInstance: strippedSourceRoleInstance,
                        httpUrl: url,
                        httpHost: host, httpStatusCode: httpStatusCode.ToString(CultureInfo.InvariantCulture), httpPath: httpPath, httpMethod: httpMethod, httpPort: destinationPort, httpScheme: requestScheme, 
                        protocol: contextProtocol, responseHeadersRequestContextAppId: responseHeadersRequestContextAppId,
                        debugData: debugData);
                    
                    // advance latestRequestId so that the main dependency/request pair is stiched to the ingress gateway pair we've already created
                    latestRequestId = requestId;
                }

                // now, let's generate the regular pair of dependency/request

                // first, on behalf of the source, report client span (dependency call)
                bool isIngressRequest = !isSourceWithinTargetArea && isDestinationWithinTargetArea;
                if (!isIngressRequest)
                {
                    string requestId = StartDependency(latestRequestId);

                    //!!! Temporary support of app propagating Request-Id
                    bool isEgressRequest = isSourceWithinTargetArea && !isDestinationWithinTargetArea;
                    if (isEgressRequest && incomingRequestIdPresent)
                    {
                        requestId = incomingRequestId;
                    }
                    /////////// end of temporary support
                    
                    yield return this.GenerateDependency(spanName: strippedDestinationRoleInstance, 
                        requestId: requestId,
                        parentRequestId: latestRequestId,
                        spanStatusCode: code, spanStatusMessage: string.Empty, startTime: startTime, endTime: endTime, 
                        roleName: strippedSourceRoleName, 
                        roleInstance: strippedSourceRoleInstance, 
                        httpUrl: url,
                        httpHost: host, httpStatusCode: httpStatusCode.ToString(CultureInfo.InvariantCulture), httpPath: httpPath, httpMethod: httpMethod, httpPort: destinationPort, httpScheme: requestScheme, 
                        protocol: contextProtocol, responseHeadersRequestContextAppId: responseHeadersRequestContextAppId,
                        debugData: debugData);
                    
                    // advance latestRequestId
                    latestRequestId = requestId;
                }

                // now, on behalf of the destination, report server span (request), but only if the destination is within the target area
                // otherwise it's an external dependency and we don't want to generate requests for it
                if (isDestinationWithinTargetArea)
                {
                    // we only want to propagate synthetic context if this is a direct call from a synthetic probe (not through a gateway)
                    // if it's a synthetic call through a gateway, the gateway will be the one for which we've propagated synthetic context above
                    yield return this.GenerateRequest(spanName: strippedDestinationRoleInstance, 
                        requestId: AcknowledgeRequest(latestRequestId),
                        parentRequestId: latestRequestId,
                        spanStatusCode: code, spanStatusMessage: string.Empty, startTime: startTime, endTime: endTime,
                        roleName: strippedDestinationRoleName, 
                        roleInstance: strippedDestinationRoleInstance, 
                        httpUrl: url, httpHost: host,
                        httpStatusCode: httpStatusCode.ToString(CultureInfo.InvariantCulture), httpPath: httpPath, httpMethod: httpMethod, httpPort: destinationPort, httpScheme: requestScheme,
                        httpUserAgent: httpUserAgent, httpRoute: string.Empty, requestHeadersRequestContextAppId: requestHeadersRequestContextAppId, requestHeadersSyntheticTestRunId: requestHeadersSyntheticTestRunId,
                        requestHeadersSyntheticTestLocation: requestHeadersSyntheticTestLocation, propagateSyntheticContext: !isSourceIstioIngressGateway, debugData: debugData);
                }
            }
        }

        private DependencyTelemetry GenerateDependency(string spanName, string requestId, string parentRequestId,
            long spanStatusCode, string spanStatusMessage, DateTimeOffset? startTime, DateTimeOffset? endTime, string roleName, string roleInstance,
            string httpUrl, string httpHost, string httpStatusCode, string httpPath, string httpMethod, long httpPort, string httpScheme, string protocol, string responseHeadersRequestContextAppId, Dictionary<string, string> debugData)
        {
            string host = GetHost(httpUrl);

            if (IsApplicationInsightsUrl(host))
            {
                return null;
            }

            var dependency = new DependencyTelemetry();
            
            // https://github.com/Microsoft/ApplicationInsights-dotnet/issues/876
            dependency.Success = null;

            InitializeOperationTelemetry(dependency, spanName, requestId, parentRequestId, spanStatusCode, spanStatusMessage, startTime, endTime, roleName, roleInstance);
            // SetTracestate(span.Tracestate, dependency);

            dependency.ResultCode = httpStatusCode;

            foreach (var pair in debugData)
            {
                dependency.Properties.Add(pair);
            }

            if (httpUrl != null)
            {
                dependency.Data = httpUrl;

                if (Uri.TryCreate(httpUrl, UriKind.RelativeOrAbsolute, out var uri))
                {
                    dependency.Name = GetHttpTelemetryName(
                        httpMethod,
                        uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString,
                        null);
                }
            }

            if (string.IsNullOrWhiteSpace(responseHeadersRequestContextAppId))
            {
                dependency.Type = protocol;
                dependency.Target = host;
            }
            else
            {
                // cross-component correlation
                // http://apmtips.com/blog/2017/10/18/two-types-of-correlation/
                dependency.Type = "Http (tracked component)";
                dependency.Target = Invariant($"{host} | {responseHeadersRequestContextAppId}");
            }

            return dependency;
        }

        private RequestTelemetry GenerateRequest(string spanName, string requestId, string parentRequestId,
            long spanStatusCode, string spanStatusMessage, DateTimeOffset? startTime, DateTimeOffset? endTime, string roleName, string roleInstance,
            string httpUrl, string httpHost, string httpStatusCode, string httpPath, string httpMethod, long httpPort, string httpScheme, string httpUserAgent, string httpRoute, string requestHeadersRequestContextAppId,
            string requestHeadersSyntheticTestRunId, string requestHeadersSyntheticTestLocation, bool propagateSyntheticContext, Dictionary<string, string> debugData)
        {
            RequestTelemetry request = new RequestTelemetry();
            
            InitializeOperationTelemetry(request, spanName, requestId, parentRequestId, spanStatusCode, spanStatusMessage, startTime, endTime, roleName, roleInstance);
            // SetTracestate(span.Tracestate, request);

            request.ResponseCode = spanStatusCode.ToString();

            request.ResponseCode = httpStatusCode;
            request.Context.User.UserAgent = httpUserAgent;

            foreach (var pair in debugData)
            {
                request.Properties.Add(pair);
            }

            if (httpUrl != null && Uri.TryCreate(httpUrl, UriKind.RelativeOrAbsolute, out var requestUrl))
            {
                if (requestUrl.IsAbsoluteUri)
                {
                    request.Url = requestUrl;
                    request.Name = GetHttpTelemetryName(httpMethod, requestUrl.AbsolutePath, httpRoute);
                }
                else
                {
                    request.Name = GetHttpTelemetryName(httpMethod, requestUrl.OriginalString, httpRoute);
                }
            }
            else
            {
                request.Url = GetUrl(httpScheme, httpHost, httpPort, httpPath);
                request.Name = GetHttpTelemetryName(httpMethod, httpPath, httpRoute);
            }

            if (propagateSyntheticContext)
            {
                this.SetSyntheticContext(request, requestHeadersSyntheticTestRunId, requestHeadersSyntheticTestLocation);
            }

            if (!string.IsNullOrWhiteSpace(requestHeadersRequestContextAppId))
            {
                request.Source = Invariant($"{requestHeadersRequestContextAppId}");
            }

            return request;
        }

        private void DetermineInterest(string contextReporterUid, string sourceNamespace, string destinationNamespace, string sourceAppInsightsMonitoringEnabled,
            string destinationAppInsightsMonitoringEnabled, out bool isInstanceInteresting, out bool isInstanceFullyWithinTargetArea, out bool isSourceWithinTargetArea,
            out bool isDestinationWithinTargetArea)
        {
            // in order to be interesting to us, the instance must involve at least one workload that is within our target namespace
            // the user is also capable of opting a workload in or out of our area of interest by labeling it appropriately

            // if there is no target namespace, any workload is considered to be within the target namespace
            bool sourceIsWithinTargetNamespace = string.IsNullOrWhiteSpace(this.targetNamspace) || string.Equals(sourceNamespace, this.targetNamspace, StringComparison.InvariantCultureIgnoreCase);
            bool destinationIsWithinTargetNamespace =
                string.IsNullOrWhiteSpace(this.targetNamspace) || string.Equals(destinationNamespace, this.targetNamspace, StringComparison.InvariantCultureIgnoreCase);

            bool sourceIsPositivelyLabeled = bool.TryParse(sourceAppInsightsMonitoringEnabled, out bool sourceAppInsightsMonitoringEnabledParsed) && sourceAppInsightsMonitoringEnabledParsed;
            bool destinationIsPositivelyLabeled = bool.TryParse(destinationAppInsightsMonitoringEnabled, out bool destinationAppInsightsMonitoringEnabledParsed) &&
                                                  destinationAppInsightsMonitoringEnabledParsed;

            bool sourceIsNegativelyLabeled = bool.TryParse(sourceAppInsightsMonitoringEnabled, out sourceAppInsightsMonitoringEnabledParsed) && !sourceAppInsightsMonitoringEnabledParsed;
            bool destinationIsNegativelyLabeled =
                bool.TryParse(destinationAppInsightsMonitoringEnabled, out destinationAppInsightsMonitoringEnabledParsed) && !destinationAppInsightsMonitoringEnabledParsed;

            bool sourceIsInteresting = sourceIsPositivelyLabeled || sourceIsWithinTargetNamespace && !sourceIsNegativelyLabeled;
            Boolean destinationIsInteresting = destinationIsPositivelyLabeled || destinationIsWithinTargetNamespace && !destinationIsNegativelyLabeled;

            isInstanceInteresting = sourceIsInteresting || destinationIsInteresting;
            isInstanceFullyWithinTargetArea = sourceIsInteresting && destinationIsInteresting;

            isSourceWithinTargetArea = sourceIsInteresting;
            isDestinationWithinTargetArea = destinationIsInteresting;
        }
        #region Helpers

        private void SetSyntheticContext(RequestTelemetry telemetry, string requestHeadersSyntheticTestRunId, string requestHeadersSyntheticTestLocation)
        {
            // replicated behavior: https://github.com/Microsoft/ApplicationInsights-aspnetcore/blob/master/src/Microsoft.ApplicationInsights.AspNetCore/TelemetryInitializers/SyntheticTelemetryInitializer.cs

            if (!string.IsNullOrEmpty(requestHeadersSyntheticTestRunId) &&
                !string.IsNullOrEmpty(requestHeadersSyntheticTestLocation))
            {
                telemetry.Context.Operation.SyntheticSource = "Application Insights Availability Monitoring";

                telemetry.Context.User.Id = Invariant($"{requestHeadersSyntheticTestLocation}_{requestHeadersSyntheticTestRunId}");
                telemetry.Context.Session.Id = requestHeadersSyntheticTestRunId;
            }
        }

        private static string GetHost(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return uri.Host;
            }

            throw new ArgumentException($"Could not construct a URI. Input: {url}");
        }

        private static bool IsApplicationInsightsUrl(string host)
        {
            return host != null && (host.StartsWith("dc.services.visualstudio.com", StringComparison.InvariantCultureIgnoreCase)
                                    || host.StartsWith("rt.services.visualstudio.com", StringComparison.InvariantCultureIgnoreCase));
        }

        private static void SetOperationContext(string requestId, string parentRequestId, OperationTelemetry telemetry)
        {
            telemetry.Context.Operation.Id = requestId.TrimStart('|').Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

            telemetry.Id = requestId;
            telemetry.Context.Operation.ParentId = parentRequestId;
        }

        private static void InitializeOperationTelemetry(OperationTelemetry telemetry, string spanName, string requestId, string parentRequestId,
            long spanStatusCode, string spanStatusMessage, DateTimeOffset? startTime, DateTimeOffset? endTime, string roleName, string roleInstance)
        {
            telemetry.Name = spanName;

            var now = DateTimeOffset.UtcNow;
            telemetry.Timestamp = startTime ?? now;
            var endTimeAdjusted = endTime ?? now;
            telemetry.Duration = endTimeAdjusted - telemetry.Timestamp;

            SetOperationContext(requestId, parentRequestId, telemetry);
            
            telemetry.Success = spanStatusCode == 0;
            if (!string.IsNullOrEmpty(spanStatusMessage))
            {
                telemetry.Properties["statusDescription"] = spanStatusMessage;
            }

            SetPeerInfo(telemetry, roleName, roleInstance);
        }

        private static void SetPeerInfo(ITelemetry telemetry, string roleName, string roleInstance)
        {
            telemetry.Context.Cloud.RoleName = roleName;
            telemetry.Context.Cloud.RoleInstance = roleInstance;

            telemetry.Context.GetInternalContext().SdkVersion =
                string.Concat("aks_", "plugin", ":", "0.0.0");
        }

        //private static void SetTracestate(Span.Types.Tracestate tracestate, ISupportProperties telemetry)
        //{
        //    if (tracestate?.Entries != null)
        //    {
        //        foreach (var entry in tracestate.Entries)
        //        {
        //            if (!telemetry.Properties.ContainsKey(entry.Key))
        //            {
        //                telemetry.Properties[entry.Key] = entry.Value;
        //            }
        //        }
        //    }
        //}

        private static string GetHttpTelemetryName(string method, string path, string route)
        {
            if (method == null && path == null && route == null)
            {
                return null;
            }

            if (path == null && route == null)
            {
                return method;
            }

            if (method == null)
            {
                return route ?? path;
            }

            return method + " " + (route ?? path);
        }

        private static Uri GetUrl(string scheme, string host, long port, string path)
        {
            if (string.IsNullOrEmpty(host))
            {
                return null;
            }

            string slash = string.Empty;
            if (!string.IsNullOrEmpty(path) && !path.StartsWith("/"))
            {
                slash = "/";
            }

            string url;
            if (port <= 0 || port == 80 || port == 443)
            {
                url = $"{scheme}://{host}{slash}{path}";
            }
            else
            {
                // host already has :port
                url = $"{scheme}://{host}{slash}{path}";
            }

            try
            {
                return new Uri(url);
            }
            catch (Exception e)
            {
                throw new FormatException($"url: {url}", e);
            }
        }

        private static byte[] HexStringToBytes(string hex)
        {
            try
            {
                int numberChars = hex.Length;
                byte[] bytes = new byte[numberChars / 2];
                for (int i = 0; i < numberChars; i += 2)
                    bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);

                return bytes;
            }
            catch (Exception)
            {
                // something went wrong, probably not a well formated hex byte string
                return new byte[0];
            }
        }

        /// <summary>
        /// Converts protobuf ByteString to hex-encoded low string
        /// </summary>
        /// <returns>Hex string</returns>
        private static string BytesToHexString(byte[] bytes)
        {
            if (bytes == null)
            {
                return null;
            }

            // See https://stackoverflow.com/questions/311165/how-do-you-convert-a-byte-array-to-a-hexadecimal-string-and-vice-versa/24343727#24343727
            var result = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                var val = lookup32[bytes[i]];
                result[2 * i] = (char)val;
                result[(2 * i) + 1] = (char)(val >> 16);
            }

            return new string(result);
        }

        private static uint[] CreateLookup32()
        {
            // See https://stackoverflow.com/questions/311165/how-do-you-convert-a-byte-array-to-a-hexadecimal-string-and-vice-versa/24343727#24343727
            var result = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                string s = i.ToString("x2", CultureInfo.InvariantCulture);
                result[i] = s[0] + ((uint)s[1] << 16);
            }

            return result;
        }

        private static string RandomStringShort()
        {
            return RandomString(4);
        }

        private static string RandomStringLong()
        {
            return RandomString(16);
        }

        private static string RandomString(int lengthInBytes)
        {
            byte[] randomBytes = new byte[lengthInBytes];
            lock (sync)
            {
                rand.NextBytes(randomBytes);
            }

            return BytesToHexString(randomBytes);
        }

        private static string StartDependency(string requestId)
        {
            return Invariant($"{requestId}{RandomStringShort()}.");
        }

        private static string AcknowledgeRequest(string requestId)
        {
            return Invariant($"{requestId}{RandomStringShort()}_");
        }
        #endregion
    }
}