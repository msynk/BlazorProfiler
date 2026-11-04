using BlazorProfiler.Events;

namespace BlazorProfiler;

/// <summary>Predefined configuration profiles.</summary>
public enum ProfilerProfile
{
    /// <summary>
    /// Low-overhead monitoring: high-volume per-component events are sampled, render-output comparison and
    /// component-tree tracking are disabled, and runtime sampling is infrequent. Suitable for staging and
    /// controlled production diagnostics.
    /// </summary>
    LowOverhead = 0,

    /// <summary>Balanced defaults for day-to-day development.</summary>
    Development = 1,

    /// <summary>
    /// Detailed investigation: every supported signal is collected without sampling, including render-output
    /// comparison and payload size estimation. Highest overhead; use for short sessions.
    /// </summary>
    Detailed = 2,
}

/// <summary>Root configuration of the profiler.</summary>
public sealed class ProfilerOptions
{
    /// <summary>Creates options initialized with the <see cref="ProfilerProfile.Development"/> profile.</summary>
    public ProfilerOptions() => UseProfile(ProfilerProfile.Development);

    /// <summary>
    /// Whether the profiler is enabled. When <see langword="null"/> (the default) hosting integrations enable it only in the
    /// Development environment. Set to <see langword="true"/> explicitly to profile other environments; remote access to
    /// profiling data additionally requires the authorization configured by the hosting integration.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>The profile most recently applied with <see cref="UseProfile"/>.</summary>
    public ProfilerProfile Profile { get; private set; }

    /// <summary>Whether a session that records everything visible to the host starts automatically.</summary>
    public bool AutoStartSession { get; set; } = true;

    /// <summary>Whether to record the W3C trace and span identifiers of the ambient <see cref="System.Diagnostics.Activity"/>.</summary>
    public bool CaptureTraceContext { get; set; } = true;

    /// <summary>Processing pipeline options.</summary>
    public PipelineOptions Pipeline { get; } = new();

    /// <summary>Storage retention limits.</summary>
    public RetentionOptions Retention { get; } = new();

    /// <summary>Per-event-kind sampling.</summary>
    public SamplingOptions Sampling { get; } = new();

    /// <summary>Event filters.</summary>
    public FilterOptions Filters { get; } = new();

    /// <summary>Redaction of potentially sensitive data.</summary>
    public RedactionOptions Redaction { get; } = new();

    /// <summary>Collector selection.</summary>
    public CollectorOptions Collectors { get; } = new();

    /// <summary>Options for opt-in component instrumentation.</summary>
    public InstrumentationOptions Instrumentation { get; } = new();

    /// <summary>Diagnostic rule thresholds and overrides.</summary>
    public DiagnosticOptions Diagnostics { get; } = new();

    /// <summary>Applies the defaults of a predefined profile. Call before customizing individual values.</summary>
    public ProfilerOptions UseProfile(ProfilerProfile profile)
    {
        Profile = profile;
        Sampling.Rates.Clear();
        switch (profile)
        {
            case ProfilerProfile.LowOverhead:
                Retention.MaxEvents = 20_000;
                Retention.MaxMemoryBytes = 16L * 1024 * 1024;
                Retention.MaxAge = TimeSpan.FromMinutes(10);
                Sampling.Rates[ProfilerEventKind.ParametersSet] = 0.25;
                Sampling.Rates[ProfilerEventKind.ComponentRender] = 0.25;
                Sampling.Rates[ProfilerEventKind.RenderRequest] = 0.1;
                Sampling.Rates[ProfilerEventKind.LifecycleMethod] = 0.25;
                Sampling.Rates[ProfilerEventKind.JSInterop] = 0.5;
                Collectors.RuntimeSampleInterval = TimeSpan.FromSeconds(5);
                Collectors.JSInteropPayloadEstimation = false;
                Instrumentation.TrackParameterChanges = false;
                Instrumentation.TrackRenderOutput = false;
                Instrumentation.TrackComponentTree = false;
                Instrumentation.TrackRenderRequests = false;
                Instrumentation.MinimumLifecycleDuration = TimeSpan.FromMilliseconds(1);
                break;
            case ProfilerProfile.Detailed:
                Retention.MaxEvents = 250_000;
                Retention.MaxMemoryBytes = 128L * 1024 * 1024;
                Retention.MaxAge = TimeSpan.FromMinutes(30);
                Collectors.RuntimeSampleInterval = TimeSpan.FromMilliseconds(500);
                Collectors.JSInteropPayloadEstimation = true;
                Instrumentation.TrackParameterChanges = true;
                Instrumentation.TrackRenderOutput = true;
                Instrumentation.TrackComponentTree = true;
                Instrumentation.TrackRenderRequests = true;
                Instrumentation.MinimumLifecycleDuration = TimeSpan.Zero;
                break;
            default:
                Retention.MaxEvents = 100_000;
                Retention.MaxMemoryBytes = 64L * 1024 * 1024;
                Retention.MaxAge = TimeSpan.FromMinutes(15);
                Collectors.RuntimeSampleInterval = TimeSpan.FromSeconds(1);
                Collectors.JSInteropPayloadEstimation = false;
                Instrumentation.TrackParameterChanges = true;
                Instrumentation.TrackRenderOutput = false;
                Instrumentation.TrackComponentTree = true;
                Instrumentation.TrackRenderRequests = true;
                Instrumentation.MinimumLifecycleDuration = TimeSpan.FromMilliseconds(0.1);
                break;
        }

        return this;
    }

    /// <summary>Validates the options and throws <see cref="ArgumentException"/> for invalid values.</summary>
    public void Validate()
    {
        if (Pipeline.QueueCapacity < 16)
        {
            throw new ArgumentException("Pipeline.QueueCapacity must be at least 16.");
        }

        if (Retention.MaxEvents < 100)
        {
            throw new ArgumentException("Retention.MaxEvents must be at least 100.");
        }

        if (Retention.MaxMemoryBytes < 64 * 1024)
        {
            throw new ArgumentException("Retention.MaxMemoryBytes must be at least 64 KB.");
        }

        if (Retention.MaxAge <= TimeSpan.Zero)
        {
            throw new ArgumentException("Retention.MaxAge must be positive.");
        }

        if (Retention.MaxSessions < 1)
        {
            throw new ArgumentException("Retention.MaxSessions must be at least 1.");
        }

        foreach (var (kind, rate) in Sampling.Rates)
        {
            if (rate is < 0 or > 1 || double.IsNaN(rate))
            {
                throw new ArgumentException($"Sampling rate for {kind} must be between 0 and 1.");
            }
        }

        if (Collectors.RuntimeSampleInterval < TimeSpan.FromMilliseconds(100))
        {
            throw new ArgumentException("Collectors.RuntimeSampleInterval must be at least 100 ms.");
        }
    }
}

/// <summary>Options for the event processing pipeline.</summary>
public sealed class PipelineOptions
{
    /// <summary>
    /// The capacity of the bounded queue between collectors and processing. When the queue is full new events are
    /// dropped and counted; collectors are never blocked.
    /// </summary>
    public int QueueCapacity { get; set; } = 16_384;

    /// <summary>
    /// Processes events synchronously on the emitting thread instead of on a background consumer.
    /// Intended for deterministic tests; it adds the processing cost to the instrumented code path.
    /// </summary>
    public bool SynchronousProcessing { get; set; }

    /// <summary>The maximum number of events processed before the consumer yields.</summary>
    public int MaxBatchSize { get; set; } = 512;

    /// <summary>
    /// How long the consumer waits after draining the queue before processing again. Batching reduces the cost of
    /// <see cref="IProfiler.Emit"/> on instrumented threads at the price of this much additional latency before events
    /// become visible. Zero processes events as soon as they arrive.
    /// </summary>
    public TimeSpan ConsumerInterval { get; set; } = TimeSpan.FromMilliseconds(25);
}

/// <summary>Storage retention limits. Every limit is enforced per session.</summary>
public sealed class RetentionOptions
{
    /// <summary>The maximum number of raw events retained per session. Older events are evicted first.</summary>
    public int MaxEvents { get; set; } = 100_000;

    /// <summary>The approximate maximum memory retained by raw events per session.</summary>
    public long MaxMemoryBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>The maximum age of retained raw events.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The maximum number of concurrently existing sessions per profiler host.</summary>
    public int MaxSessions { get; set; } = 16;

    /// <summary>
    /// The maximum number of distinct aggregation keys (component types, handlers, endpoints, interop identifiers)
    /// tracked per category. Additional keys are folded into an overflow bucket.
    /// </summary>
    public int MaxAggregateKeys { get; set; } = 2_000;

    /// <summary>The maximum number of component instances tracked per session.</summary>
    public int MaxTrackedInstances { get; set; } = 20_000;

    /// <summary>The number of one-second buckets kept for time-series charts.</summary>
    public int TimeSeriesBuckets { get; set; } = 900;
}

/// <summary>Per-event-kind head sampling.</summary>
/// <remarks>
/// Sampled events carry a <see cref="ProfilerEvent.SampleWeight"/> so aggregates remain unbiased estimates. Operations that
/// anchor correlation (event handlers, navigation, custom operations), errors, and markers are never sampled out.
/// </remarks>
public sealed class SamplingOptions
{
    /// <summary>The fraction (0..1) of events of each kind to keep. Kinds not listed are always kept.</summary>
    public Dictionary<ProfilerEventKind, double> Rates { get; } = [];
}

/// <summary>Event filters.</summary>
public sealed class FilterOptions
{
    /// <summary>
    /// Component types whose full name starts with one of these prefixes are excluded. By default the profiler's own
    /// components are excluded so the profiler does not measure itself.
    /// </summary>
    public List<string> ExcludedComponentTypePrefixes { get; } = ["BlazorProfiler.Browser."];

    /// <summary>
    /// HTTP requests whose URL contains one of these fragments (case-insensitive) are excluded.
    /// </summary>
    public List<string> ExcludedUrlFragments { get; } = ["/_blazor-profiler"];

    /// <summary>
    /// Browser resource entries whose URL contains one of these fragments are excluded (framework assets by default).
    /// </summary>
    public List<string> ExcludedResourceFragments { get; } = ["/_framework/", "/_content/", "/_blazor", "/_blazor-profiler"];

    /// <summary>JavaScript interop identifiers starting with one of these prefixes are excluded.</summary>
    public List<string> ExcludedJSIdentifierPrefixes { get; } = ["BlazorProfiler.", "./_content/BlazorProfiler/"];

    /// <summary>An optional custom filter. Return <see langword="false"/> to drop an event.</summary>
    public Func<ProfilerEvent, bool>? Predicate { get; set; }
}

/// <summary>Redaction options used by the default <see cref="Pipeline.IProfilerRedactionPolicy"/>.</summary>
public sealed class RedactionOptions
{
    /// <summary>Whether query-string values are replaced with a placeholder. Keys are kept.</summary>
    public bool RedactQueryValues { get; set; } = true;

    /// <summary>Query parameters whose values may be kept as-is when <see cref="RedactQueryValues"/> is enabled.</summary>
    public HashSet<string> AllowedQueryParameters { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether path segments that look like identifiers or secrets (numbers, GUIDs, long hexadecimal or base64 tokens,
    /// e-mail addresses) are replaced with placeholders such as <c>{id}</c>. This also groups requests by endpoint.
    /// </summary>
    public bool TemplatePathIdentifiers { get; set; } = true;

    /// <summary>JavaScript interop identifiers starting with one of these prefixes are replaced with <c>[redacted]</c>.</summary>
    public List<string> RedactedJSIdentifierPrefixes { get; } = [];
}

/// <summary>Selects which collectors run.</summary>
public sealed class CollectorOptions
{
    /// <summary>Collects framework component metrics: parameter updates, render batches, event handlers, navigation.</summary>
    public bool ComponentMetrics { get; set; } = true;

    /// <summary>Uses framework activities to establish interaction context for correlation.</summary>
    public bool ComponentActivities { get; set; } = true;

    /// <summary>Tracks component creation through a decorating <c>IComponentActivator</c>.</summary>
    public bool ComponentActivator { get; set; } = true;

    /// <summary>Collects HTTP requests.</summary>
    public bool Http { get; set; } = true;

    /// <summary>Collects JavaScript interop calls where a supported interception point exists.</summary>
    public bool JSInterop { get; set; } = true;

    /// <summary>
    /// Wraps <c>IJSObjectReference</c> instances returned by interop calls so calls on imported modules are recorded.
    /// Disabled by default: a wrapped reference cannot be passed back to JavaScript inside a nested argument object.
    /// </summary>
    public bool JSObjectReferences { get; set; }

    /// <summary>Estimates serialized interop argument sizes. Adds serialization cost to every call.</summary>
    public bool JSInteropPayloadEstimation { get; set; }

    /// <summary>Collects periodic runtime and garbage-collection samples.</summary>
    public bool Runtime { get; set; } = true;

    /// <summary>The runtime sampling interval.</summary>
    public TimeSpan RuntimeSampleInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Collects Blazor Server circuit lifecycle and inbound activity.</summary>
    public bool Circuits { get; set; } = true;

    /// <summary>Collects browser performance entries (Event Timing, long animation frames, resource timing).</summary>
    public bool Browser { get; set; } = true;

    /// <summary>The minimum interaction duration reported by the browser Event Timing observer.</summary>
    public int BrowserInteractionThresholdMs { get; set; } = 40;
}

/// <summary>Options for opt-in component instrumentation (<c>ProfiledComponentBase</c>).</summary>
public sealed class InstrumentationOptions
{
    /// <summary>Records lifecycle method durations.</summary>
    public bool TrackLifecycle { get; set; } = true;

    /// <summary>Records <c>StateHasChanged</c> requests, including coalesced and suppressed requests.</summary>
    public bool TrackRenderRequests { get; set; } = true;

    /// <summary>Compares incoming parameters with the previous set (values are compared, never stored in events).</summary>
    public bool TrackParameterChanges { get; set; } = true;

    /// <summary>
    /// Computes a structural hash of each render's output to detect renders that produced identical output.
    /// Proportional to the size of the component's render tree.
    /// </summary>
    public bool TrackRenderOutput { get; set; }

    /// <summary>Discovers parent/child relationships from rendered output.</summary>
    public bool TrackComponentTree { get; set; } = true;

    /// <summary>
    /// Lifecycle method calls that complete synchronously faster than this are not recorded as individual events (they
    /// still contribute to render and parameter timings). Methods a component does not override are never recorded.
    /// Calls that complete asynchronously are always recorded.
    /// </summary>
    public TimeSpan MinimumLifecycleDuration { get; set; } = TimeSpan.FromMilliseconds(0.1);
}

/// <summary>Per-rule settings.</summary>
public sealed class RuleSettings
{
    /// <summary>Whether the rule runs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>An optional severity override.</summary>
    public Analysis.DiagnosticSeverity? Severity { get; set; }
}

/// <summary>Thresholds used by the built-in diagnostic rules. All durations are in milliseconds.</summary>
/// <remarks>
/// Rules combine these absolute thresholds with relative baselines computed from the session, so a value above a
/// threshold is necessary but not always sufficient for a finding.
/// </remarks>
public sealed class DiagnosticOptions
{
    /// <summary>Per-rule settings keyed by rule identifier (for example <c>BP1001</c>).</summary>
    public Dictionary<string, RuleSettings> Rules { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The maximum number of findings reported per rule.</summary>
    public int MaxFindingsPerRule { get; set; } = 10;

    /// <summary>Sustained renders per second of one component type considered high frequency.</summary>
    public double HighRenderRatePerSecond { get; set; } = 10;

    /// <summary>Minimum number of observations before rate-based rendering rules report.</summary>
    public int MinimumRenderSamples { get; set; } = 30;

    /// <summary>Updated components in one render batch considered large.</summary>
    public int LargeBatchComponentCount { get; set; } = 50;

    /// <summary>Lifecycle or parameter-update duration considered slow.</summary>
    public double SlowLifecycleMs { get; set; } = 50;

    /// <summary>Component instances of one type considered excessive.</summary>
    public int ExcessiveComponentInstances { get; set; } = 500;

    /// <summary>Event handler duration considered slow (the RAIL model's 100 ms response budget).</summary>
    public double SlowEventHandlerMs { get; set; } = 100;

    /// <summary>Event handler duration considered long-running.</summary>
    public double LongRunningHandlerMs { get; set; } = 1_000;

    /// <summary>Events per second of one handler considered a rapid event stream.</summary>
    public double RapidEventsPerSecond { get; set; } = 8;

    /// <summary>Window within which identical requests are considered duplicates.</summary>
    public double DuplicateRequestWindowMs { get; set; } = 2_000;

    /// <summary>Requests to one endpoint template within one operation that suggest an N+1 pattern.</summary>
    public int NPlusOneThreshold { get; set; } = 5;

    /// <summary>Request duration considered slow.</summary>
    public double SlowRequestMs { get; set; } = 1_000;

    /// <summary>Sequential non-overlapping requests within one operation that may be parallelizable.</summary>
    public int SequentialRequestThreshold { get; set; } = 3;

    /// <summary>Failures of one endpoint within the session considered excessive.</summary>
    public int RepeatedFailureThreshold { get; set; } = 3;

    /// <summary>Interop calls within one operation considered excessive.</summary>
    public int ExcessiveInteropCallsPerOperation { get; set; } = 50;

    /// <summary>Sustained interop calls per second considered excessive.</summary>
    public double ExcessiveInteropCallsPerSecond { get; set; } = 60;

    /// <summary>Estimated interop payload size considered large.</summary>
    public long LargeInteropPayloadBytes { get; set; } = 64 * 1024;

    /// <summary>Interop call duration considered slow.</summary>
    public double SlowInteropMs { get; set; } = 100;

    /// <summary>Fraction of wall time spent in GC pauses considered high pressure.</summary>
    public double HighGcPauseRatio { get; set; } = 0.05;

    /// <summary>Generation 2 collections per minute considered high pressure.</summary>
    public double HighGen2CollectionsPerMinute { get; set; } = 6;

    /// <summary>Allocation rate relative to the session median that is considered a spike.</summary>
    public double AllocationSpikeFactor { get; set; } = 4;

    /// <summary>Minimum allocation rate (bytes per second) for a spike to be reported.</summary>
    public long AllocationSpikeMinimumBytesPerSecond { get; set; } = 20L * 1024 * 1024;

    /// <summary>Minimum navigations before memory-growth analysis runs.</summary>
    public int MemoryGrowthMinimumNavigations { get; set; } = 5;

    /// <summary>Heap growth per navigation considered suspicious.</summary>
    public long MemoryGrowthBytesPerNavigation { get; set; } = 512 * 1024;

    /// <summary>Server render round trip (diff + transport + client apply) considered slow.</summary>
    public double SlowServerRenderMs { get; set; } = 250;

    /// <summary>Connection drops per circuit considered unstable.</summary>
    public int CircuitReconnectThreshold { get; set; } = 3;

    /// <summary>Circuit inbound activity duration considered slow.</summary>
    public double SlowCircuitActivityMs { get; set; } = 250;

    /// <summary>Render batches per second on one circuit considered high volume.</summary>
    public double HighBatchRatePerSecond { get; set; } = 30;

    /// <summary>Thread-pool queue length considered saturated.</summary>
    public long ThreadPoolQueueSaturation { get; set; } = 100;

    /// <summary>Browser interaction latency considered slow (the INP "needs improvement" boundary).</summary>
    public double SlowInteractionMs { get; set; } = 200;

    /// <summary>Browser long animation frame duration considered a blocking task.</summary>
    public double LongTaskMs { get; set; } = 150;

    /// <summary>Returns whether a rule is enabled.</summary>
    public bool IsRuleEnabled(string ruleId) => !Rules.TryGetValue(ruleId, out var settings) || settings.Enabled;
}
