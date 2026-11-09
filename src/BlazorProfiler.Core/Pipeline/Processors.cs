using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using BlazorProfiler.Events;
using BlazorProfiler.Sessions;

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

/// <summary>
/// Deterministic head sampler. For each kind it keeps one event in every 1/rate, counted separately per subject (the
/// component type, and the method for lifecycle events, or the interop identifier), so every subject is sampled at the
/// configured rate however its events interleave with others.
/// </summary>
/// <remarks>
/// A single counter per kind aliases with periodic patterns: when a list renders B, B, B, A repeatedly with a rate of 0.25,
/// every kept render is A's, so A would be over-counted four times and B never counted. Per-subject counters keep each
/// subject's weighted counts and rates within one sampling interval of the real values.
/// </remarks>
internal sealed class EventSampler
{
    /// <summary>The maximum number of per-subject counters; subjects beyond it share their kind's counter.</summary>
    internal const int MaxSubjects = 4096;

    private readonly double[] _rates;
    private readonly long[] _counters;
    private readonly ConcurrentDictionary<(ProfilerEventKind Kind, string Subject, int Detail), StrongBox<long>> _subjectCounters = new();
    private int _subjectCount;

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

        // Keep an event whenever the subject's cumulative expected count crosses an integer boundary.
        var n = Interlocked.Increment(ref CounterFor(index, profilerEvent)) - 1;
        var keep = (long)((n + 1) * rate) > (long)(n * rate);
        if (keep)
        {
            profilerEvent.SampleWeight = 1.0 / rate;
        }

        return keep;
    }

    private ref long CounterFor(int index, ProfilerEvent profilerEvent)
    {
        var (subject, detail) = profilerEvent switch
        {
            LifecycleMethodEvent lifecycle => (lifecycle.ComponentType, (int)lifecycle.Method),
            ComponentEvent component => (component.ComponentType, 0),
            JSInteropEvent interop => (interop.Identifier, 0),
            _ => (null, 0),
        };

        if (subject is null)
        {
            return ref _counters[index];
        }

        var key = (profilerEvent.Kind, subject, detail);
        if (!_subjectCounters.TryGetValue(key, out var counter))
        {
            if (Volatile.Read(ref _subjectCount) >= MaxSubjects)
            {
                return ref _counters[index];
            }

            var created = new StrongBox<long>();
            if (_subjectCounters.TryAdd(key, created))
            {
                Interlocked.Increment(ref _subjectCount);
                counter = created;
            }
            else
            {
                counter = _subjectCounters[key];
            }
        }

        return ref counter.Value;
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
            or ProfilerEventKind.DatabaseCommand
            or ProfilerEventKind.ComponentCreated
            or ProfilerEventKind.ComponentDisposed
            or ProfilerEventKind.ComponentRetained
            or ProfilerEventKind.BrowserStartup
            or ProfilerEventKind.BrowserVital;
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
/// <para>
/// Matching only applies to events without a circuit identifier (WebAssembly), where browser and .NET timestamps share the
/// same machine clock. A browser entry matches when the sanitized URL is equal and the start times differ by less than
/// <see cref="MatchToleranceMs"/>.
/// </para>
/// <para>
/// The matching request has usually been stored and aggregated already, and readers may be serializing it, so it is never
/// mutated: an enriched copy is passed to the replacement callback, which swaps it into the sessions that hold the original
/// and updates their aggregates. Only the sessions the original was offered to are searched.
/// </para>
/// </remarks>
internal sealed class BrowserResourceMergeProcessor : IProfilerEventProcessor
{
    public const double MatchToleranceMs = 150;
    private const int Capacity = 512;

    private readonly IProfilerClock _clock;
    private readonly Func<ProfilerSession[]> _recipients;
    private readonly Action<HttpRequestEvent, HttpRequestEvent, ProfilerSession[]> _replace;
    private readonly HttpRequestEvent?[] _recent = new HttpRequestEvent?[Capacity];
    private readonly ProfilerSession[]?[] _recentRecipients = new ProfilerSession[]?[Capacity];
    private int _next;

    /// <summary>Creates the processor.</summary>
    /// <param name="clock">The clock.</param>
    /// <param name="recipients">Returns the sessions that the event being processed is offered to.</param>
    /// <param name="replace">
    /// Called when a browser entry matches, with the original request, its enriched copy, and the sessions the original was
    /// offered to.
    /// </param>
    public BrowserResourceMergeProcessor(
        IProfilerClock clock,
        Func<ProfilerSession[]> recipients,
        Action<HttpRequestEvent, HttpRequestEvent, ProfilerSession[]> replace)
    {
        _clock = clock;
        _recipients = recipients;
        _replace = replace;
    }

    public bool Process(ProfilerEvent profilerEvent)
    {
        if (profilerEvent is HttpRequestEvent http && http.CircuitId is null)
        {
            // Remember which sessions the request is offered to, so a later replacement only searches those: never
            // imported sessions, sessions created afterwards, or sessions that were not recording.
            _recent[_next] = http;
            _recentRecipients[_next] = _recipients();
            _next = (_next + 1) % Capacity;
            return true;
        }

        if (profilerEvent is BrowserResourceEvent resource && resource.CircuitId is null && resource.InitiatorType is "fetch" or "xmlhttprequest")
        {
            var tolerance = _clock.FromMilliseconds(MatchToleranceMs);
            var best = -1;
            var bestDelta = long.MaxValue;
            for (var i = 0; i < _recent.Length; i++)
            {
                var candidate = _recent[i];
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
                    best = i;
                    bestDelta = delta;
                }
            }

            if (best >= 0)
            {
                var original = _recent[best]!;
                var enriched = original.WithBrowserTiming(
                    resource.TransferSize > 0 ? resource.TransferSize : null,
                    resource.FromCache);
                _recent[best] = enriched;
                _replace(original, enriched, _recentRecipients[best] ?? []);
                return false;
            }
        }

        return true;
    }
}
