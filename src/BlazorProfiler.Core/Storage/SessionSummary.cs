using BlazorProfiler.Events;

namespace BlazorProfiler.Storage;

/// <summary>
/// Aggregated view of a session in display units (milliseconds, bytes, counts). Counts are weighted by sample weight and
/// may be fractional when <see cref="ContainsSampledData"/> is <see langword="true"/>.
/// </summary>
public sealed class SessionSummary
{
    /// <summary>Whether any aggregated value is an estimate derived from sampled events.</summary>
    public bool ContainsSampledData { get; set; }

    /// <summary>The timestamp of the earliest event, if any.</summary>
    public long? FirstEventTimestamp { get; set; }

    /// <summary>The end timestamp of the latest event, if any.</summary>
    public long? LastEventTimestamp { get; set; }

    /// <summary>The number of events with an error outcome.</summary>
    public long Errors { get; set; }

    /// <summary>The number of dropped events reported through markers.</summary>
    public long DroppedEventsReported { get; set; }

    /// <summary>Event counts by kind.</summary>
    public Dictionary<string, double> EventCounts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Per-component-type aggregates.</summary>
    public List<ComponentSummary> Components { get; set; } = [];

    /// <summary>Per-instance aggregates (instrumented components and created instances).</summary>
    public List<ComponentInstanceSummary> Instances { get; set; } = [];

    /// <summary>Per-event-handler aggregates.</summary>
    public List<HandlerSummary> Handlers { get; set; } = [];

    /// <summary>Per-endpoint HTTP aggregates.</summary>
    public List<EndpointSummary> Endpoints { get; set; } = [];

    /// <summary>Per-identifier JavaScript interop aggregates.</summary>
    public List<InteropSummary> Interop { get; set; } = [];

    /// <summary>Navigation counts by route.</summary>
    public List<NavigationSummary> Navigations { get; set; } = [];

    /// <summary>Per-circuit aggregates.</summary>
    public List<CircuitSummary> Circuits { get; set; } = [];

    /// <summary>Render batch aggregates.</summary>
    public RenderBatchSummary RenderBatches { get; set; } = new();

    /// <summary>Browser-observed interaction latency.</summary>
    public DurationSummary BrowserInteractions { get; set; } = new();

    /// <summary>Browser long tasks and long animation frames.</summary>
    public DurationSummary BrowserLongTasks { get; set; } = new();

    /// <summary>Runtime aggregates.</summary>
    public RuntimeSummary Runtime { get; set; } = new();

    /// <summary>Per-second time series (most recent window).</summary>
    public List<TimeSeriesPoint> TimeSeries { get; set; } = [];
}

/// <summary>Aggregates for one component type.</summary>
public sealed class ComponentSummary
{
    /// <summary>The component type.</summary>
    public string ComponentType { get; set; } = string.Empty;

    /// <summary>Whether instances of the type are instrumented (per-component render data available).</summary>
    public bool Instrumented { get; set; }

    /// <summary>Instances created.</summary>
    public double Created { get; set; }

    /// <summary>Instances disposed (instrumented components only).</summary>
    public double Disposed { get; set; }

    /// <summary>Render durations (<c>BuildRenderTree</c>); instrumented components only.</summary>
    public DurationSummary Renders { get; set; } = new();

    /// <summary>Renders whose output was identical to the previous render.</summary>
    public double RendersWithUnchangedOutput { get; set; }

    /// <summary>Renders for which output comparison was performed.</summary>
    public double RendersWithOutputComparison { get; set; }

    /// <summary>Parameter updates.</summary>
    public DurationSummary ParametersSets { get; set; } = new();

    /// <summary>Parameter updates where every value was equal to the previous value.</summary>
    public double ParametersUnchanged { get; set; }

    /// <summary>Parameter updates where values differed only by reference identity.</summary>
    public double ParametersChangedByReferenceOnly { get; set; }

    /// <summary>Names of parameters that most often changed by reference only, with counts.</summary>
    public Dictionary<string, double> ReferenceOnlyParameters { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Render requests ignored because a render was already pending.</summary>
    public double RenderRequestsCoalesced { get; set; }

    /// <summary>Render requests suppressed by <c>ShouldRender</c>.</summary>
    public double RenderRequestsSuppressed { get; set; }

    /// <summary>Event handlers dispatched to this component type.</summary>
    public DurationSummary EventHandlers { get; set; } = new();

    /// <summary>Lifecycle method durations by method name.</summary>
    public Dictionary<string, DurationSummary> Lifecycle { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Errors observed for this component type.</summary>
    public long Errors { get; set; }
}

/// <summary>Aggregates for one component instance.</summary>
public sealed class ComponentInstanceSummary
{
    /// <summary>The instance identifier.</summary>
    public long InstanceId { get; set; }

    /// <summary>The component type.</summary>
    public string? ComponentType { get; set; }

    /// <summary>The parent instance identifier, or zero when unknown.</summary>
    public long ParentInstanceId { get; set; }

    /// <summary>The creation timestamp, when observed.</summary>
    public long? CreatedTimestamp { get; set; }

    /// <summary>The disposal timestamp, when observed.</summary>
    public long? DisposedTimestamp { get; set; }

    /// <summary>Renders observed.</summary>
    public int Renders { get; set; }

    /// <summary>Total render time in milliseconds.</summary>
    public double RenderMs { get; set; }

    /// <summary>Renders whose output was identical to the previous render.</summary>
    public int RendersWithUnchangedOutput { get; set; }

    /// <summary>Parameter updates observed.</summary>
    public int ParametersSets { get; set; }

    /// <summary>Parameter updates that were unchanged or changed by reference only.</summary>
    public int UnchangedParametersSets { get; set; }

    /// <summary>The most recent render timestamp.</summary>
    public long? LastRenderTimestamp { get; set; }
}

/// <summary>Aggregates for one event handler.</summary>
public sealed class HandlerSummary
{
    /// <summary>The component type.</summary>
    public string? ComponentType { get; set; }

    /// <summary>The handler method name.</summary>
    public string? MethodName { get; set; }

    /// <summary>The event attribute name.</summary>
    public string? AttributeName { get; set; }

    /// <summary>Handler durations.</summary>
    public DurationSummary Durations { get; set; } = new();

    /// <summary>Failed invocations.</summary>
    public long Errors { get; set; }
}

/// <summary>Aggregates for one HTTP endpoint.</summary>
public sealed class EndpointSummary
{
    /// <summary>The endpoint key (method, host, templated path).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The HTTP method.</summary>
    public string? Method { get; set; }

    /// <summary>Request durations.</summary>
    public DurationSummary Durations { get; set; } = new();

    /// <summary>Failed requests (exceptions, cancellations, or status codes &gt;= 400).</summary>
    public double Failures { get; set; }

    /// <summary>Requests served from the browser cache, when known.</summary>
    public double CacheHits { get; set; }

    /// <summary>Total response bytes, when known.</summary>
    public long ResponseBytes { get; set; }

    /// <summary>Counts by status code.</summary>
    public Dictionary<string, double> StatusCodes { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Aggregates for one interop identifier.</summary>
public sealed class InteropSummary
{
    /// <summary>The call direction.</summary>
    public InteropDirection Direction { get; set; }

    /// <summary>The identifier.</summary>
    public string? Identifier { get; set; }

    /// <summary>Durations of all calls; <see cref="DurationSummary.Count"/> is the number of calls.</summary>
    public DurationSummary Durations { get; set; } = new();

    /// <summary>
    /// Durations of the calls that JavaScript answered (a result or a JavaScript exception). Latency analysis uses these: a
    /// call without a response (see <see cref="NoResponse"/>) measures how long the caller waited, for example the interop
    /// timeout, and a call that failed before reaching JavaScript measures nothing.
    /// </summary>
    public DurationSummary ResponseDurations { get; set; } = new();

    /// <summary>Failed or canceled calls.</summary>
    public double Failures { get; set; }

    /// <summary>
    /// Calls that ended without a response from JavaScript: they timed out, were canceled, were made after the connection to
    /// the browser was lost, or were made while interop was unavailable (for example during prerendering). Included in
    /// <see cref="Failures"/>.
    /// </summary>
    public double NoResponse { get; set; }

    /// <summary>Total estimated payload bytes.</summary>
    public long PayloadBytes { get; set; }

    /// <summary>The largest estimated payload.</summary>
    public long MaxPayloadBytes { get; set; }
}

/// <summary>Navigation count for one route.</summary>
public sealed class NavigationSummary
{
    /// <summary>The route template.</summary>
    public string Route { get; set; } = string.Empty;

    /// <summary>The number of navigations.</summary>
    public double Count { get; set; }
}

/// <summary>Aggregates for one circuit.</summary>
public sealed class CircuitSummary
{
    /// <summary>The circuit identifier.</summary>
    public string CircuitId { get; set; } = string.Empty;

    /// <summary>Whether the circuit is currently connected.</summary>
    public bool Connected { get; set; }

    /// <summary>Whether the circuit has been closed.</summary>
    public bool Closed { get; set; }

    /// <summary>Connection drops.</summary>
    public int ConnectionDowns { get; set; }

    /// <summary>Connection establishments.</summary>
    public int ConnectionUps { get; set; }

    /// <summary>Inbound activity durations.</summary>
    public DurationSummary InboundActivities { get; set; } = new();

    /// <summary>Render batch durations (including client acknowledgement).</summary>
    public DurationSummary RenderBatches { get; set; } = new();

    /// <summary>The time the circuit opened.</summary>
    public long? OpenedTimestamp { get; set; }

    /// <summary>The time the circuit closed.</summary>
    public long? ClosedTimestamp { get; set; }
}

/// <summary>Render batch aggregates.</summary>
public sealed class RenderBatchSummary
{
    /// <summary>Batch durations.</summary>
    public DurationSummary Durations { get; set; } = new();

    /// <summary>The total number of component diffs across batches.</summary>
    public double TotalUpdatedComponents { get; set; }

    /// <summary>The largest number of component diffs in one batch.</summary>
    public int MaxUpdatedComponents { get; set; }
}

/// <summary>Runtime aggregates.</summary>
public sealed class RuntimeSummary
{
    /// <summary>Runtime samples collected.</summary>
    public long Samples { get; set; }

    /// <summary>Total bytes allocated across samples.</summary>
    public long AllocatedBytes { get; set; }

    /// <summary>Generation 0 collections.</summary>
    public long Gen0Collections { get; set; }

    /// <summary>Generation 1 collections.</summary>
    public long Gen1Collections { get; set; }

    /// <summary>Generation 2 collections.</summary>
    public long Gen2Collections { get; set; }

    /// <summary>Total GC pause time in milliseconds.</summary>
    public double GcPauseMs { get; set; }

    /// <summary>
    /// Whether samples exist but none reported GC pause time (the WebAssembly runtime does not track it), so
    /// <see cref="GcPauseMs"/> is not a measurement.
    /// </summary>
    public bool GcPauseUnavailable { get; set; }

    /// <summary>Total time covered by samples in milliseconds.</summary>
    public double SampledMs { get; set; }

    /// <summary>The most recent heap size.</summary>
    public long? HeapSizeBytes { get; set; }

    /// <summary>The most recent total managed memory.</summary>
    public long? TotalMemoryBytes { get; set; }

    /// <summary>The most recent working set.</summary>
    public long? WorkingSetBytes { get; set; }

    /// <summary>The most recent thread-pool thread count.</summary>
    public int? ThreadPoolThreads { get; set; }

    /// <summary>The most recent thread-pool queue length.</summary>
    public long? ThreadPoolQueueLength { get; set; }

    /// <summary>The processor count.</summary>
    public int? ProcessorCount { get; set; }
}

/// <summary>One second of aggregated activity.</summary>
public sealed class TimeSeriesPoint
{
    /// <summary>Seconds since the session started.</summary>
    public long Second { get; set; }

    /// <summary>Render batches.</summary>
    public double RenderBatches { get; set; }

    /// <summary>Component diffs across batches.</summary>
    public double UpdatedComponents { get; set; }

    /// <summary>Total render batch time in milliseconds.</summary>
    public double RenderBatchMs { get; set; }

    /// <summary>Instrumented component renders.</summary>
    public double ComponentRenders { get; set; }

    /// <summary>Parameter updates.</summary>
    public double ParametersSets { get; set; }

    /// <summary>Event handlers.</summary>
    public double EventHandlers { get; set; }

    /// <summary>Total event handler time in milliseconds.</summary>
    public double EventHandlerMs { get; set; }

    /// <summary>HTTP requests.</summary>
    public double HttpRequests { get; set; }

    /// <summary>Interop calls.</summary>
    public double InteropCalls { get; set; }

    /// <summary>Bytes allocated.</summary>
    public long AllocatedBytes { get; set; }

    /// <summary>Garbage collections.</summary>
    public int GcCollections { get; set; }

    /// <summary>The heap size at the end of the second, when sampled.</summary>
    public long? HeapBytes { get; set; }

    /// <summary>Errors.</summary>
    public int Errors { get; set; }
}
