using BlazorProfiler.Events;
using static BlazorProfiler.Analysis.DiagnosticContext;

namespace BlazorProfiler.Analysis.Rules;

/// <summary>BP3001: identical requests repeated within a short window.</summary>
public sealed class DuplicateRequestsRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP3001",
        Title = "Duplicate or overlapping requests",
        Category = DiagnosticCategory.Network,
        Description = "Detects identical GET/HEAD requests (same URL, compared by a process-local hash) repeated within the duplicate window, including requests issued while an identical request was still in flight.",
        RequiredSignals = [ProfilerEventKind.HttpRequest],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var window = context.Ticks(context.Options.DuplicateRequestWindowMs);
        var results = new List<(DiagnosticFinding Finding, int Count)>();
        foreach (var group in context.OfType<HttpRequestEvent>()
                     .Where(r => r.UrlHash != 0 && r.Method is "GET" or "HEAD")
                     .GroupBy(r => r.UrlHash))
        {
            var requests = group.OrderBy(r => r.Timestamp).ToList();
            if (requests.Count < 2)
            {
                continue;
            }

            var duplicates = new List<HttpRequestEvent>();
            var overlapping = 0;
            for (var i = 1; i < requests.Count; i++)
            {
                var previous = requests[i - 1];
                if (requests[i].Timestamp - previous.Timestamp <= window)
                {
                    duplicates.Add(requests[i]);
                    if (requests[i].Timestamp < previous.EndTimestamp)
                    {
                        overlapping++;
                    }
                }
            }

            if (duplicates.Count == 0)
            {
                continue;
            }

            var wastedMs = duplicates.Sum(r => context.Ms(r.Duration));
            var origins = duplicates.Select(r => r.OriginComponentType).Where(t => t is not null).Distinct().Select(ShortType).Take(4).ToList();
            var url = requests[0].Url ?? requests[0].Endpoint ?? "(unknown)";
            results.Add((Create(
                $"{url} requested {duplicates.Count + 1} times within {FormatMs(context.Options.DuplicateRequestWindowMs)}",
                Scale(duplicates.Count, 2),
                DiagnosticConfidence.High,
                $"The same URL was requested {requests.Count} times; {duplicates.Count} request(s) followed an identical one within {FormatMs(context.Options.DuplicateRequestWindowMs)}, {overlapping} of them while the previous request was still in flight.",
                "Repeated identical reads cost network round trips and server work without new information. They are typically caused by several components fetching the same data, lifecycle methods that run more than once (prerendering followed by interactive rendering, OnParametersSetAsync), or re-renders that restart loading.",
                "Cache the response (in a scoped or singleton service, or with HTTP caching headers), share in-flight requests (store the Task, not just the result), use persistent component state to reuse prerendered data, and load data in OnInitializedAsync rather than OnParametersSetAsync unless it depends on parameters.",
                [
                    Evidence("Requests", FormatNumber(requests.Count)),
                    Evidence("Duplicates within window", FormatNumber(duplicates.Count)),
                    Evidence("Overlapping (in-flight) duplicates", FormatNumber(overlapping)),
                    Evidence("Originating components", origins.Count > 0 ? string.Join(", ", origins) : "unknown", origins.Count > 0 ? ValueQuality.Measured : ValueQuality.Unavailable),
                ],
                new ImpactEstimate { TotalMs = Math.Round(wastedMs, 3), Occurrences = duplicates.Count, Method = "Sum of durations of requests that repeated an identical request within the window.", Quality = ValueQuality.Measured },
                subject: requests[0].Endpoint ?? url,
                caveats: ["Polling and intentionally refreshed data are expected to repeat.", "URLs are compared by a hash of the full original URL, including query values."],
                related: duplicates), duplicates.Count));
        }

        return results.OrderByDescending(r => r.Count).Select(r => r.Finding);
    }
}

/// <summary>BP3002: N+1 request pattern.</summary>
public sealed class NPlusOneRequestsRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP3002",
        Title = "N+1 request pattern",
        Category = DiagnosticCategory.Network,
        Description = "Detects many requests to the same endpoint template with different identifiers within one operation (or within one second when no operation is known).",
        RequiredSignals = [ProfilerEventKind.HttpRequest],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Options.NPlusOneThreshold;
        var groups = new List<(string Endpoint, List<HttpRequestEvent> Requests, bool Correlated)>();
        foreach (var operation in context.OfType<HttpRequestEvent>().Where(r => r.OperationId != 0 && r.Endpoint is not null).GroupBy(r => (r.OperationId, r.Endpoint)))
        {
            var requests = operation.ToList();
            if (requests.Select(r => r.UrlHash).Distinct().Count() >= threshold)
            {
                groups.Add((operation.Key.Endpoint!, requests, true));
            }
        }

        var second = context.Clock.Frequency;
        foreach (var endpoint in context.OfType<HttpRequestEvent>().Where(r => r.OperationId == 0 && r.Endpoint is not null).GroupBy(r => r.Endpoint!))
        {
            var requests = endpoint.OrderBy(r => r.Timestamp).ToList();
            var start = 0;
            for (var end = 0; end < requests.Count; end++)
            {
                while (requests[end].Timestamp - requests[start].Timestamp >= second)
                {
                    start++;
                }

                var window = requests.GetRange(start, end - start + 1);
                if (window.Select(r => r.UrlHash).Distinct().Count() >= threshold)
                {
                    groups.Add((endpoint.Key, window, false));
                    break;
                }
            }
        }

        foreach (var (endpoint, requests, correlated) in groups.GroupBy(g => g.Endpoint).Select(g => g.MaxBy(x => x.Requests.Count)).OrderByDescending(g => g.Requests.Count))
        {
            var totalMs = requests.Sum(r => context.Ms(r.Duration));
            var origins = requests.Select(r => r.OriginComponentType).Where(t => t is not null).Distinct().Select(ShortType).Take(3).ToList();
            yield return Create(
                $"{requests.Count} requests to {endpoint}",
                Scale(requests.Count, threshold),
                correlated ? DiagnosticConfidence.High : DiagnosticConfidence.Medium,
                $"{requests.Count} requests to {endpoint} with {requests.Select(r => r.UrlHash).Distinct().Count()} distinct URLs were issued {(correlated ? "in one operation" : "within one second")}.",
                "Fetching items one at a time (for example one request per list row) multiplies latency and server load. Each request pays a full round trip even when they run concurrently.",
                "Add a batch endpoint that returns all needed items, include related data in the parent request, or load data once in the parent component and pass it to children as parameters.",
                [
                    Evidence("Requests", FormatNumber(requests.Count)),
                    Evidence("Total request time", FormatMs(totalMs)),
                    Evidence("Originating components", origins.Count > 0 ? string.Join(", ", origins) : "unknown", origins.Count > 0 ? ValueQuality.Measured : ValueQuality.Unavailable),
                    Evidence("Grouping", correlated ? "same operation (execution-context correlation)" : "same one-second window (temporal)", correlated ? ValueQuality.Measured : ValueQuality.Inferred),
                ],
                new ImpactEstimate { TotalMs = Math.Round(totalMs, 3), Occurrences = requests.Count, Method = "Sum of request durations in the group.", Quality = ValueQuality.Measured },
                subject: endpoint,
                caveats: ["Some APIs only support single-item retrieval; then consider caching or a backend-for-frontend aggregation endpoint."],
                related: requests);
        }
    }
}

/// <summary>BP3003: sequential requests that may be parallelizable.</summary>
public sealed class SequentialRequestsRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP3003",
        Title = "Sequential requests",
        Category = DiagnosticCategory.Network,
        Description = "Detects chains of non-overlapping requests to different endpoints within one operation, where each starts shortly after the previous one ends.",
        RequiredSignals = [ProfilerEventKind.HttpRequest],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Options.SequentialRequestThreshold;
        var maxGap = context.Ticks(50);
        var chains = new List<List<HttpRequestEvent>>();
        foreach (var operation in context.OfType<HttpRequestEvent>().Where(r => r.OperationId != 0).GroupBy(r => r.OperationId))
        {
            var requests = operation.OrderBy(r => r.Timestamp).ToList();
            var chain = new List<HttpRequestEvent>();
            foreach (var request in requests)
            {
                if (chain.Count > 0)
                {
                    var previous = chain[^1];
                    var gap = request.Timestamp - previous.EndTimestamp;
                    if (gap < 0 || gap > maxGap || request.Endpoint == previous.Endpoint)
                    {
                        if (chain.Count >= threshold)
                        {
                            chains.Add(chain);
                        }

                        chain = [];
                    }
                }

                chain.Add(request);
            }

            if (chain.Count >= threshold)
            {
                chains.Add(chain);
            }
        }

        foreach (var group in chains.GroupBy(c => string.Join(" → ", c.Select(r => r.Endpoint))).OrderByDescending(g => g.Sum(c => c.Count)))
        {
            var chain = group.First();
            var totalMs = chain.Sum(r => context.Ms(r.Duration));
            var parallelMs = chain.Max(r => context.Ms(r.Duration));
            yield return Create(
                $"{chain.Count} sequential requests in one operation",
                Cap(Scale(totalMs - parallelMs, 200), DiagnosticSeverity.Medium),
                DiagnosticConfidence.Low,
                $"{chain.Count} requests to different endpoints ran one after another ({group.Key}), {group.Count()} time(s).",
                "Each request started only after the previous one completed. If the requests do not depend on each other's results, the total latency is the sum of their durations instead of the longest one.",
                "If the requests are independent, start them together and await Task.WhenAll; otherwise consider a single endpoint that returns the combined data.",
                [
                    Evidence("Chain length", FormatNumber(chain.Count)),
                    Evidence("Sequential time", FormatMs(totalMs)),
                    Evidence("Lower bound if parallel", FormatMs(parallelMs), ValueQuality.Estimated),
                    Evidence("Occurrences", FormatNumber(group.Count())),
                ],
                new ImpactEstimate { TotalMs = Math.Round((totalMs - parallelMs) * group.Count(), 3), Occurrences = group.Count(), Method = "Sequential time minus the longest request, an optimistic bound for parallel execution.", Quality = ValueQuality.Estimated },
                subject: group.Key,
                caveats: ["The profiler cannot see data dependencies; requests that need a previous response must stay sequential."],
                related: chain);
        }
    }
}

/// <summary>BP3004: slow endpoints.</summary>
public sealed class SlowEndpointRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP3004",
        Title = "Slow endpoint",
        Category = DiagnosticCategory.Network,
        Description = "Detects endpoints whose 95th-percentile duration exceeds the slow-request threshold.",
        RequiredSignals = [ProfilerEventKind.HttpRequest],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Options.SlowRequestMs;
        var median = context.Summary.Endpoints.Count > 0 ? context.Summary.Endpoints.Select(e => e.Durations.P50Ms).OrderBy(v => v).ElementAt(context.Summary.Endpoints.Count / 2) : 0;
        foreach (var endpoint in context.Summary.Endpoints.OrderByDescending(e => e.Durations.P95Ms))
        {
            var slow = endpoint.Durations.Count >= 3 ? endpoint.Durations.P95Ms >= threshold : endpoint.Durations.MaxMs >= threshold * 3;
            if (!slow)
            {
                continue;
            }

            var related = context.OfType<HttpRequestEvent>().Where(r => r.Endpoint == endpoint.Endpoint).OrderByDescending(r => r.Duration).Take(20).ToList();
            var traceIds = related.Select(r => r.TraceId).Where(t => t is not null).Distinct().Take(3).ToList();
            yield return Create(
                $"{endpoint.Endpoint} p95 is {FormatMs(endpoint.Durations.P95Ms)}",
                Scale(endpoint.Durations.P95Ms, threshold),
                DiagnosticConfidence.High,
                $"{FormatNumber(endpoint.Durations.Count)} request(s) to {endpoint.Endpoint}: median {FormatMs(endpoint.Durations.P50Ms)}, p95 {FormatMs(endpoint.Durations.P95Ms)}, max {FormatMs(endpoint.Durations.MaxMs)}.",
                $"The client waited at least {FormatMs(threshold)} for this endpoint at the 95th percentile{(median > 0 ? $" (the median endpoint in this session takes {FormatMs(median)})" : string.Empty)}. Client-side timing includes network transfer and server processing; it cannot tell them apart.",
                "Investigate the server side using the recorded trace identifiers, reduce payload size, add caching, and show progressive UI while waiting.",
                [
                    Evidence("Requests", FormatNumber(endpoint.Durations.Count)),
                    Evidence("p95", FormatMs(endpoint.Durations.P95Ms)),
                    Evidence("Maximum", FormatMs(endpoint.Durations.MaxMs)),
                    Evidence("Trace identifiers", traceIds.Count > 0 ? string.Join(", ", traceIds) : "not captured", traceIds.Count > 0 ? ValueQuality.Measured : ValueQuality.Unavailable),
                ],
                new ImpactEstimate { TotalMs = endpoint.Durations.TotalMs, Occurrences = endpoint.Durations.Count, Method = "Total measured request time for the endpoint.", Quality = ValueQuality.Measured },
                subject: endpoint.Endpoint,
                caveats: ["Cold starts and first-request JIT can make the first call slow; check whether only the first request is affected."],
                related: related);
        }
    }
}

/// <summary>BP3005: repeated failures and retries.</summary>
public sealed class RepeatedFailuresRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP3005",
        Title = "Repeated request failures",
        Category = DiagnosticCategory.Network,
        Description = "Detects endpoints that fail repeatedly, retries of failed requests, and failed requests followed by re-rendering.",
        RequiredSignals = [ProfilerEventKind.HttpRequest],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var batchesByOperation = context.OfType<RenderBatchEvent>().Where(b => b.OperationId != 0).GroupBy(b => b.OperationId).ToDictionary(g => g.Key, g => g.Count());
        var retryWindow = context.Ticks(5_000);
        foreach (var group in context.OfType<HttpRequestEvent>().GroupBy(r => r.Endpoint ?? r.Url ?? "(unknown)"))
        {
            var requests = group.OrderBy(r => r.Timestamp).ToList();
            var failures = requests.Where(r => r.Outcome != ProfilerOutcome.Success || r.StatusCode >= 400).ToList();
            if (failures.Count < context.Options.RepeatedFailureThreshold)
            {
                continue;
            }

            var retries = 0;
            foreach (var failure in failures)
            {
                if (requests.Any(r => r.UrlHash == failure.UrlHash && r.Timestamp > failure.Timestamp && r.Timestamp - failure.EndTimestamp <= retryWindow))
                {
                    retries++;
                }
            }

            var rendersAfterFailures = failures.Sum(f => f.OperationId != 0 && batchesByOperation.TryGetValue(f.OperationId, out var c) ? c : 0);
            var statuses = failures.GroupBy(f => f.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? f.ErrorType ?? f.Outcome.ToString()).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} ×{g.Count()}");
            var ratio = (double)failures.Count / requests.Count;
            yield return Create(
                $"{group.Key} failed {failures.Count} times",
                Scale(failures.Count * Math.Max(ratio, 0.2), context.Options.RepeatedFailureThreshold * 0.5),
                DiagnosticConfidence.High,
                $"{failures.Count} of {requests.Count} requests to {group.Key} failed ({string.Join(", ", statuses)}); {retries} were followed by a request to the same URL within 5 s.",
                "Repeated failures waste round trips and often trigger retry loops or error re-renders. Failed requests in the same operation were followed by " + rendersAfterFailures + " render batch(es).",
                "Fix the failing calls, use bounded exponential backoff for transient errors, avoid retrying non-transient errors (4xx), and surface the error state once instead of re-rendering repeatedly.",
                [
                    Evidence("Failures", FormatNumber(failures.Count)),
                    Evidence("Failure ratio", FormatPercent(ratio)),
                    Evidence("Retried within 5 s", FormatNumber(retries)),
                    Evidence("Render batches in failing operations", FormatNumber(rendersAfterFailures)),
                ],
                new ImpactEstimate { TotalMs = Math.Round(failures.Sum(f => context.Ms(f.Duration)), 3), Occurrences = failures.Count, Method = "Sum of failed request durations.", Quality = ValueQuality.Measured },
                subject: group.Key,
                caveats: ["Expected 404 responses (existence checks) may not be problems."],
                related: failures);
        }
    }
}

/// <summary>BP3006: requests repeated during component initialization or parameter updates.</summary>
public sealed class RequestsDuringInitializationRule : DiagnosticRule
{
    private static readonly HashSet<LifecycleMethod> s_initialization =
    [
        LifecycleMethod.OnInitialized, LifecycleMethod.OnInitializedAsync, LifecycleMethod.SetParametersAsync,
        LifecycleMethod.OnParametersSet, LifecycleMethod.OnParametersSetAsync,
    ];

    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP3006",
        Title = "Requests repeated during component initialization",
        Category = DiagnosticCategory.Network,
        Description = "Detects endpoints requested from the lifecycle methods of many instances of one component type, or repeatedly by one instance on every parameter update (requires ProfiledComponentBase for origin attribution).",
        RequiredSignals = [ProfilerEventKind.HttpRequest, ProfilerEventKind.LifecycleMethod],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        foreach (var group in context.OfType<HttpRequestEvent>()
                     .Where(r => r.OriginComponentType is not null && r.OriginLifecycleMethod is { } method && s_initialization.Contains(method))
                     .GroupBy(r => (r.OriginComponentType, r.Endpoint))
                     .OrderByDescending(g => g.Count()))
        {
            var requests = group.ToList();
            var instances = requests.Select(r => r.OriginComponentInstanceId).Distinct().Count();
            var perInstance = requests.GroupBy(r => r.OriginComponentInstanceId).Max(g => g.Count());
            var manyInstances = instances >= 3 && requests.Count >= 5;
            var repeatedPerInstance = perInstance >= 3 && requests.Any(r => r.OriginLifecycleMethod is LifecycleMethod.OnParametersSet or LifecycleMethod.OnParametersSetAsync);
            if (!manyInstances && !repeatedPerInstance)
            {
                continue;
            }

            var component = ShortType(group.Key.OriginComponentType);
            var totalMs = requests.Sum(r => context.Ms(r.Duration));
            yield return Create(
                manyInstances ? $"Each {component} instance requests {group.Key.Endpoint}" : $"{component} requests {group.Key.Endpoint} on every parameter update",
                Scale(requests.Count, 5),
                DiagnosticConfidence.High,
                manyInstances
                    ? $"{instances} instances of {component} issued {requests.Count} requests to {group.Key.Endpoint} from their lifecycle methods."
                    : $"One instance of {component} requested {group.Key.Endpoint} up to {perInstance} times from OnParametersSet(Async).",
                manyInstances
                    ? "When every instance of a component loads its own data, rendering a list of N items issues N requests."
                    : "OnParametersSet runs every time the parent re-renders, not only when parameter values change, so loading data there repeats requests.",
                manyInstances
                    ? "Load the data once in the parent (or a shared service) and pass it to each instance as a parameter."
                    : "Track the parameter values used for the last load and only fetch when they actually change, or move the load to OnInitializedAsync if it does not depend on parameters.",
                [
                    Evidence("Requests", FormatNumber(requests.Count)),
                    Evidence("Distinct instances", FormatNumber(instances)),
                    Evidence("Most requests by one instance", FormatNumber(perInstance)),
                    Evidence("Total request time", FormatMs(totalMs)),
                ],
                new ImpactEstimate { TotalMs = Math.Round(totalMs, 3), Occurrences = requests.Count, Method = "Sum of request durations attributed to the component's lifecycle methods.", Quality = ValueQuality.Measured },
                componentType: group.Key.OriginComponentType,
                subject: $"{group.Key.OriginComponentType}|{group.Key.Endpoint}",
                caveats: ["Prerendering runs lifecycle methods once on the server and again when the component becomes interactive; use persistent component state to avoid that duplicate."],
                related: requests);
        }
    }
}
