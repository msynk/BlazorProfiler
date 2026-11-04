using BlazorProfiler.Events;

namespace BlazorProfiler.Pipeline;

/// <summary>
/// A processing step applied to every event on the pipeline consumer, after correlation and before storage.
/// </summary>
/// <remarks>
/// Processors run sequentially on a single consumer, so they do not need to be thread-safe with respect to each other.
/// They must not block and must not throw; exceptions are caught, counted, and the event is dropped.
/// </remarks>
public interface IProfilerEventProcessor
{
    /// <summary>Processes an event. Return <see langword="false"/> to drop it.</summary>
    bool Process(ProfilerEvent profilerEvent);
}

/// <summary>Deterministic per-kind head sampler.</summary>
internal sealed class EventSampler
{
    private readonly double[] _rates;
    private readonly long[] _counters;

    public EventSampler(SamplingOptions options)
    {
        var size = (int)ProfilerEventKind.Operation + 1;
        _rates = new double[size];
        _counters = new long[size];
        Array.Fill(_rates, 1.0);
        foreach (var (kind, rate) in options.Rates)
        {
            var index = (int)kind;
            if (index >= 0 && index < size)
            {
                _rates[index] = rate;
            }
        }
    }

    /// <summary>Returns whether to keep the event and sets its <see cref="ProfilerEvent.SampleWeight"/>.</summary>
    public bool ShouldKeep(ProfilerEvent profilerEvent)
    {
        var kind = profilerEvent.Kind;
        var index = (int)kind;
        if ((uint)index >= (uint)_rates.Length)
        {
            return true;
        }

        var rate = _rates[index];
        if (rate >= 1.0 || IsNeverSampled(profilerEvent))
        {
            return true;
        }

        if (rate <= 0)
        {
            return false;
        }

        // Keep an event whenever the cumulative expected count crosses an integer boundary.
        var n = Interlocked.Increment(ref _counters[index]) - 1;
        var keep = (long)((n + 1) * rate) > (long)(n * rate);
        if (keep)
        {
            profilerEvent.SampleWeight = 1.0 / rate;
        }

        return keep;
    }

    private static bool IsNeverSampled(ProfilerEvent profilerEvent) =>
        profilerEvent.Outcome == ProfilerOutcome.Error
        || profilerEvent.Kind is ProfilerEventKind.EventHandler
            or ProfilerEventKind.Navigation
            or ProfilerEventKind.Operation
            or ProfilerEventKind.Marker
            or ProfilerEventKind.Circuit
            or ProfilerEventKind.RenderBatch
            or ProfilerEventKind.HttpRequest
            or ProfilerEventKind.ComponentCreated
            or ProfilerEventKind.ComponentDisposed;
}

/// <summary>Applies <see cref="FilterOptions"/>.</summary>
internal sealed class FilterProcessor : IProfilerEventProcessor
{
    private readonly FilterOptions _options;

    public FilterProcessor(FilterOptions options) => _options = options;

    public bool Process(ProfilerEvent profilerEvent)
    {
        switch (profilerEvent)
        {
            case ComponentEvent component when IsExcludedComponent(component.ComponentType):
                return false;
            case HttpRequestEvent http when ContainsAny(http.Url, _options.ExcludedUrlFragments):
                return false;
            case HttpRequestEvent http when IsExcludedComponent(http.OriginComponentType):
                return false;
            case BrowserResourceEvent resource when ContainsAny(resource.Url, _options.ExcludedResourceFragments):
                return false;
            case JSInteropEvent js when StartsWithAny(js.Identifier, _options.ExcludedJSIdentifierPrefixes):
                return false;
            case JSInteropEvent js when IsExcludedComponent(js.OriginComponentType):
                return false;
        }

        return _options.Predicate is null || _options.Predicate(profilerEvent);
    }

    private bool IsExcludedComponent(string? type) => StartsWithAny(type, _options.ExcludedComponentTypePrefixes);

    private static bool StartsWithAny(string? value, List<string> prefixes)
    {
        if (value is null)
        {
            return false;
        }

        foreach (var prefix in prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAny(string? value, List<string> fragments)
    {
        if (value is null)
        {
            return false;
        }

        foreach (var fragment in fragments)
        {
            if (value.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Applies the <see cref="IProfilerRedactionPolicy"/> to URLs and identifiers.</summary>
internal sealed class RedactionProcessor : IProfilerEventProcessor
{
    private readonly IProfilerRedactionPolicy _policy;

    public RedactionProcessor(IProfilerRedactionPolicy policy) => _policy = policy;

    public bool Process(ProfilerEvent profilerEvent)
    {
        switch (profilerEvent)
        {
            case HttpRequestEvent http when http.Url is not null:
                // The original URL is only available here; hash it before it is replaced.
                if (http.UrlHash == 0)
                {
                    http.UrlHash = ComputeUrlHash(http.Method, http.Url);
                }

                http.Endpoint ??= _policy.GetEndpointKey(http.Method, http.Url);
                http.Url = _policy.SanitizeUrl(http.Url);
                break;
            case BrowserResourceEvent resource when resource.Url is not null:
                resource.Url = _policy.SanitizeUrl(resource.Url);
                break;
            case JSInteropEvent js when js.Identifier is not null:
                js.Identifier = _policy.SanitizeIdentifier(js.Identifier);
                break;
            case BrowserLongTaskEvent longTask when longTask.Script is not null:
                longTask.Script = _policy.SanitizeUrl(longTask.Script);
                break;
        }

        return true;
    }

    /// <summary>
    /// Computes a process-local hash. <see cref="string.GetHashCode()"/> is randomized per process, so the value cannot be
    /// reversed or correlated across processes.
    /// </summary>
    internal static long ComputeUrlHash(string? method, string url)
    {
        var high = (long)StringComparer.Ordinal.GetHashCode(url);
        var low = (long)(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(method ?? string.Empty);
        var hash = (high << 32) ^ low ^ url.Length;
        return hash == 0 ? 1 : hash;
    }
}

/// <summary>
/// Merges browser Resource Timing entries into matching <see cref="HttpRequestEvent"/>s observed in the same runtime,
/// so one logical request is reported once, enriched with transfer size and cache information.
/// </summary>
/// <remarks>
/// Matching only applies to events without a circuit identifier (WebAssembly), where browser and .NET timestamps share the
/// same machine clock. A browser entry matches when the sanitized URL is equal and the start times differ by less than
/// <see cref="MatchToleranceMs"/>.
/// </remarks>
internal sealed class BrowserResourceMergeProcessor : IProfilerEventProcessor
{
    public const double MatchToleranceMs = 150;
    private const int Capacity = 512;

    private readonly IProfilerClock _clock;
    private readonly HttpRequestEvent?[] _recent = new HttpRequestEvent?[Capacity];
    private int _next;

    public BrowserResourceMergeProcessor(IProfilerClock clock) => _clock = clock;

    public bool Process(ProfilerEvent profilerEvent)
    {
        if (profilerEvent is HttpRequestEvent http && http.CircuitId is null)
        {
            _recent[_next] = http;
            _next = (_next + 1) % Capacity;
            return true;
        }

        if (profilerEvent is BrowserResourceEvent resource && resource.CircuitId is null && resource.InitiatorType is "fetch" or "xmlhttprequest")
        {
            var tolerance = _clock.FromMilliseconds(MatchToleranceMs);
            HttpRequestEvent? best = null;
            var bestDelta = long.MaxValue;
            foreach (var candidate in _recent)
            {
                if (candidate is null || candidate.ResponseBytesQuality == ValueQuality.Measured && candidate.FromCache is not null)
                {
                    continue;
                }

                if (!string.Equals(candidate.Url, resource.Url, StringComparison.Ordinal))
                {
                    continue;
                }

                var delta = Math.Abs(candidate.Timestamp - resource.Timestamp);
                if (delta <= tolerance && delta < bestDelta)
                {
                    best = candidate;
                    bestDelta = delta;
                }
            }

            if (best is not null)
            {
                if (resource.TransferSize > 0)
                {
                    best.ResponseBytes = resource.TransferSize;
                    best.ResponseBytesQuality = ValueQuality.Measured;
                }

                best.FromCache = resource.FromCache;
                return false;
            }
        }

        return true;
    }
}
