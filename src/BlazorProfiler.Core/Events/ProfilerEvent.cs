using System.Text.Json.Serialization;

namespace BlazorProfiler.Events;

/// <summary>
/// Base type of every profiler event. Events are immutable once they have been accepted by the
/// processing pipeline; collectors populate them before calling <see cref="IProfiler.Emit(ProfilerEvent)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Timestamps are monotonic ticks produced by <see cref="IProfilerClock"/> and must only be compared with
/// timestamps from the same clock domain. Use <see cref="ProfilerClockExtensions.ToDateTimeOffset"/>
/// to convert a timestamp to wall-clock time for display or cross-system correlation.
/// </para>
/// <para>
/// The schema version of the event model is <see cref="SchemaVersion"/>. It is written once into the
/// header of every exported trace instead of on each event.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(MarkerEvent), "marker")]
[JsonDerivedType(typeof(ComponentCreatedEvent), "componentCreated")]
[JsonDerivedType(typeof(ComponentDisposedEvent), "componentDisposed")]
[JsonDerivedType(typeof(ParametersSetEvent), "parametersSet")]
[JsonDerivedType(typeof(ComponentRenderEvent), "componentRender")]
[JsonDerivedType(typeof(RenderRequestEvent), "renderRequest")]
[JsonDerivedType(typeof(LifecycleMethodEvent), "lifecycle")]
[JsonDerivedType(typeof(ComponentChildrenEvent), "componentChildren")]
[JsonDerivedType(typeof(RenderBatchEvent), "renderBatch")]
[JsonDerivedType(typeof(EventHandlerEvent), "eventHandler")]
[JsonDerivedType(typeof(NavigationEvent), "navigation")]
[JsonDerivedType(typeof(HttpRequestEvent), "http")]
[JsonDerivedType(typeof(JSInteropEvent), "jsInterop")]
[JsonDerivedType(typeof(RuntimeSampleEvent), "runtime")]
[JsonDerivedType(typeof(CircuitEvent), "circuit")]
[JsonDerivedType(typeof(CircuitActivityEvent), "circuitActivity")]
[JsonDerivedType(typeof(BrowserInteractionEvent), "browserInteraction")]
[JsonDerivedType(typeof(BrowserLongTaskEvent), "browserLongTask")]
[JsonDerivedType(typeof(BrowserResourceEvent), "browserResource")]
[JsonDerivedType(typeof(OperationEvent), "operation")]
public abstract class ProfilerEvent
{
    /// <summary>The version of the event schema implemented by this assembly.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// Process-unique, stable identifier assigned when the event is emitted.
    /// Identifiers increase monotonically in emission order within one process.
    /// </summary>
    public long Id { get; set; }

    /// <summary>The kind of event. Derived from the concrete event type.</summary>
    [JsonIgnore]
    public abstract ProfilerEventKind Kind { get; }

    /// <summary>The monotonic timestamp (in <see cref="IProfilerClock.Frequency"/> ticks) at which the operation started.</summary>
    public long Timestamp { get; set; }

    /// <summary>Duration of the operation in clock ticks. Zero for instantaneous events.</summary>
    public long Duration { get; set; }

    /// <summary>The Blazor Server circuit that produced the event, when known.</summary>
    public string? CircuitId { get; set; }

    /// <summary>
    /// Identifier of the logical operation (user interaction, navigation, or custom operation) this event belongs to.
    /// Zero when the event could not be attributed to an operation.
    /// </summary>
    public long OperationId { get; set; }

    /// <summary>How the <see cref="OperationId"/> and <see cref="ParentId"/> relationships were established.</summary>
    public CorrelationKind Correlation { get; set; }

    /// <summary>Identifier of an explicitly known parent event, or zero.</summary>
    public long ParentId { get; set; }

    /// <summary>The outcome of the operation.</summary>
    public ProfilerOutcome Outcome { get; set; }

    /// <summary>The exception type name when <see cref="Outcome"/> is <see cref="ProfilerOutcome.Error"/>. Never contains exception messages.</summary>
    public string? ErrorType { get; set; }

    /// <summary>
    /// Statistical weight of this event. A value greater than one indicates the event represents
    /// several sampled-out occurrences (for example 4 when one in four events is kept).
    /// </summary>
    public double SampleWeight { get; set; } = 1;

    /// <summary>
    /// The number of occurrences the event represents: <see cref="SampleWeight"/>, or one when no valid weight is recorded.
    /// Every count derived from events (aggregates, diagnostics) uses this, so they agree under sampling.
    /// </summary>
    internal double Weight => SampleWeight > 0 ? SampleWeight : 1;

    /// <summary>W3C trace identifier of the ambient <see cref="System.Diagnostics.Activity"/>, when captured.</summary>
    public string? TraceId { get; set; }

    /// <summary>W3C span identifier of the ambient <see cref="System.Diagnostics.Activity"/>, when captured.</summary>
    public string? SpanId { get; set; }

    /// <summary>The timestamp at which the operation ended.</summary>
    [JsonIgnore]
    public long EndTimestamp => Timestamp + Duration;

    /// <summary>Estimates the managed memory retained by this event, used to enforce storage limits.</summary>
    public virtual int EstimateSize() =>
        112 + StringSize(CircuitId) + StringSize(ErrorType) + StringSize(TraceId) + StringSize(SpanId);

    /// <summary>Estimates the size of a string instance.</summary>
    protected static int StringSize(string? value) => value is null ? 0 : 22 + (value.Length * 2);

    /// <summary>Sets <see cref="Timestamp"/> and <see cref="Duration"/> from an end timestamp and a duration.</summary>
    public void SetTimingFromEnd(long endTimestamp, long duration)
    {
        if (duration < 0)
        {
            duration = 0;
        }

        Timestamp = endTimestamp - duration;
        Duration = duration;
    }
}

/// <summary>Identifies the concrete type of a <see cref="ProfilerEvent"/>.</summary>
public enum ProfilerEventKind
{
    /// <summary>A marker such as a bookmark or a collection-health notice.</summary>
    Marker = 0,
    /// <summary>A component instance was created.</summary>
    ComponentCreated = 10,
    /// <summary>A component instance was disposed.</summary>
    ComponentDisposed = 11,
    /// <summary>A component received parameters.</summary>
    ParametersSet = 12,
    /// <summary>A component produced its render tree.</summary>
    ComponentRender = 13,
    /// <summary>A component requested a render.</summary>
    RenderRequest = 14,
    /// <summary>A component lifecycle method ran.</summary>
    LifecycleMethod = 15,
    /// <summary>The children of a component changed.</summary>
    ComponentChildren = 16,
    /// <summary>A render batch was produced and applied.</summary>
    RenderBatch = 20,
    /// <summary>A UI event handler ran.</summary>
    EventHandler = 30,
    /// <summary>The router navigated to a page.</summary>
    Navigation = 31,
    /// <summary>An HTTP request was sent.</summary>
    HttpRequest = 40,
    /// <summary>A JavaScript interop call ran.</summary>
    JSInterop = 50,
    /// <summary>A periodic runtime sample.</summary>
    RuntimeSample = 60,
    /// <summary>A circuit lifecycle change.</summary>
    Circuit = 70,
    /// <summary>A circuit inbound activity (an incoming SignalR message processed by the circuit).</summary>
    CircuitActivity = 71,
    /// <summary>A browser-observed user interaction (Event Timing API).</summary>
    BrowserInteraction = 80,
    /// <summary>A browser long task or long animation frame.</summary>
    BrowserLongTask = 81,
    /// <summary>A browser resource timing entry.</summary>
    BrowserResource = 82,
    /// <summary>A custom application operation.</summary>
    Operation = 90,
}

/// <summary>The outcome of a profiled operation.</summary>
public enum ProfilerOutcome
{
    /// <summary>The operation completed successfully.</summary>
    Success = 0,
    /// <summary>The operation failed with an exception or an error status.</summary>
    Error = 1,
    /// <summary>The operation was canceled.</summary>
    Canceled = 2,
    /// <summary>The outcome is not known.</summary>
    Unknown = 3,
}

/// <summary>
/// Describes how a relationship between an event and its operation or parent was established.
/// Ordered from strongest to weakest evidence.
/// </summary>
public enum CorrelationKind
{
    /// <summary>The event is not correlated with any operation.</summary>
    None = 0,
    /// <summary>The event is the operation itself.</summary>
    Self = 1,
    /// <summary>The relationship is explicit, for example a parent identifier recorded by instrumentation.</summary>
    Explicit = 2,
    /// <summary>
    /// The relationship was established through ambient context that flowed with the .NET execution context
    /// (for example an operation scope captured by an asynchronous continuation). Strong evidence.
    /// </summary>
    ExecutionContext = 3,
    /// <summary>
    /// The relationship was established because the work ran sequentially on the same renderer dispatcher
    /// immediately after the operation. Good evidence, but asynchronous work can interleave.
    /// </summary>
    Dispatcher = 4,
    /// <summary>
    /// The relationship is based only on temporal proximity. Weak evidence; never proof of causation.
    /// </summary>
    Temporal = 5,
}

/// <summary>Describes whether a reported value was measured, estimated, inferred, or is unavailable.</summary>
public enum ValueQuality
{
    /// <summary>The value is not available on this platform or with the current configuration.</summary>
    Unavailable = 0,
    /// <summary>The value was measured directly.</summary>
    Measured = 1,
    /// <summary>The value was estimated (for example from serialized size heuristics or sampling).</summary>
    Estimated = 2,
    /// <summary>The value was inferred from other evidence.</summary>
    Inferred = 3,
}

/// <summary>Identifies the mechanism that produced an event.</summary>
public enum InstrumentationSource
{
    /// <summary>Unknown source.</summary>
    Unknown = 0,
    /// <summary>Built-in ASP.NET Core Blazor metrics (System.Diagnostics.Metrics).</summary>
    FrameworkMetrics = 1,
    /// <summary>Built-in ASP.NET Core Blazor activities (System.Diagnostics.ActivitySource).</summary>
    FrameworkActivity = 2,
    /// <summary>Opt-in component instrumentation (<c>ProfiledComponentBase</c>).</summary>
    ComponentInstrumentation = 3,
    /// <summary>The profiler's component activator.</summary>
    ComponentActivator = 4,
    /// <summary>A delegating HTTP message handler.</summary>
    HttpMessageHandler = 5,
    /// <summary>The System.Net.Http diagnostic activity.</summary>
    HttpDiagnosticActivity = 6,
    /// <summary>A JavaScript interop runtime decorator.</summary>
    JSRuntimeDecorator = 7,
    /// <summary>Browser performance APIs.</summary>
    Browser = 8,
    /// <summary>Blazor Server circuit handler and hub filter.</summary>
    CircuitHandler = 9,
    /// <summary>Runtime APIs such as <see cref="GC"/>.</summary>
    Runtime = 10,
    /// <summary>Manual application instrumentation.</summary>
    Manual = 11,
}
