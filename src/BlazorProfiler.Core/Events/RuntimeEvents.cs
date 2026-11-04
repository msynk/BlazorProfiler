using System.Text.Json.Serialization;

namespace BlazorProfiler.Events;

/// <summary>
/// A periodic sample of managed runtime state. Deltas cover the interval
/// [<see cref="ProfilerEvent.Timestamp"/>, <see cref="ProfilerEvent.EndTimestamp"/>].
/// Values that are not available on the current platform are <see langword="null"/>.
/// </summary>
/// <remarks>
/// In Blazor WebAssembly these values describe the .NET runtime inside the browser tab. They are not the browser's
/// JavaScript heap and not server-process metrics. In Blazor Server they describe the whole server process, shared by
/// every circuit.
/// </remarks>
public sealed class RuntimeSampleEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.RuntimeSample;

    /// <summary>Bytes allocated by the process during the interval (<c>GC.GetTotalAllocatedBytes(precise: true)</c>).</summary>
    public long? AllocatedBytes { get; set; }

    /// <summary>The managed heap size reported by the most recent garbage collection.</summary>
    public long? HeapSizeBytes { get; set; }

    /// <summary>The managed memory currently known to the GC (<c>GC.GetTotalMemory(false)</c>).</summary>
    public long? TotalMemoryBytes { get; set; }

    /// <summary>Generation 0 collections during the interval.</summary>
    public int? Gen0Collections { get; set; }

    /// <summary>Generation 1 collections during the interval.</summary>
    public int? Gen1Collections { get; set; }

    /// <summary>Generation 2 collections during the interval.</summary>
    public int? Gen2Collections { get; set; }

    /// <summary>Time the runtime was paused for garbage collection during the interval, in clock ticks.</summary>
    public long? GcPauseTicks { get; set; }

    /// <summary>Process CPU time consumed during the interval, in clock ticks (server only).</summary>
    public long? CpuTicks { get; set; }

    /// <summary>The number of logical processors available to the process.</summary>
    public int? ProcessorCount { get; set; }

    /// <summary>The process working set (server only).</summary>
    public long? WorkingSetBytes { get; set; }

    /// <summary>The number of thread-pool threads (server only).</summary>
    public int? ThreadPoolThreads { get; set; }

    /// <summary>The number of queued thread-pool work items (server only).</summary>
    public long? ThreadPoolQueueLength { get; set; }

    /// <summary>Monitor lock contentions during the interval (server only).</summary>
    public long? LockContentions { get; set; }

    /// <summary>Exceptions thrown during the interval.</summary>
    public long? Exceptions { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + 160;
}

/// <summary>A circuit lifecycle change.</summary>
public enum CircuitChange
{
    /// <summary>The circuit was opened.</summary>
    Opened = 0,
    /// <summary>The circuit's connection was established or re-established.</summary>
    ConnectionUp = 1,
    /// <summary>The circuit's connection was lost.</summary>
    ConnectionDown = 2,
    /// <summary>The circuit was closed.</summary>
    Closed = 3,
}

/// <summary>A Blazor Server circuit lifecycle change.</summary>
public sealed class CircuitEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.Circuit;

    /// <summary>The change.</summary>
    public CircuitChange Change { get; set; }
}

/// <summary>
/// A message received by a Blazor Server circuit and processed as an inbound activity, such as a browser event,
/// a render-batch acknowledgement, or a JavaScript interop callback.
/// </summary>
public sealed class CircuitActivityEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.CircuitActivity;

    /// <summary>
    /// The SignalR hub method that delivered the activity (for example <c>DispatchBrowserEvent</c> or
    /// <c>OnRenderCompleted</c>), or <see langword="null"/> when it could not be determined.
    /// </summary>
    public string? ActivityName { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + StringSize(ActivityName);
}

/// <summary>A user interaction observed by the browser's Event Timing API.</summary>
/// <remarks>
/// <see cref="ProfilerEvent.Duration"/> is the browser-reported interaction duration: from the input event's timestamp until
/// the next paint after its handlers ran. It reflects user-perceived latency, including work outside .NET.
/// </remarks>
public sealed class BrowserInteractionEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.BrowserInteraction;

    /// <summary>The DOM event type (for example <c>click</c> or <c>keydown</c>).</summary>
    public string? EventType { get; set; }

    /// <summary>The lower-case tag name of the event target, when available. Element content is never captured.</summary>
    public string? TargetTag { get; set; }

    /// <summary>Time between the input and the start of event processing, in milliseconds.</summary>
    public double InputDelayMs { get; set; }

    /// <summary>Time spent running event handlers, in milliseconds.</summary>
    public double ProcessingMs { get; set; }

    /// <summary>Time from the end of processing until the next frame was presented, in milliseconds.</summary>
    public double PresentationDelayMs { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + StringSize(EventType) + StringSize(TargetTag) + 24;
}

/// <summary>A long task or long animation frame observed by the browser.</summary>
public sealed class BrowserLongTaskEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.BrowserLongTask;

    /// <summary>The entry type (<c>long-animation-frame</c> or <c>longtask</c>).</summary>
    public string? EntryType { get; set; }

    /// <summary>The blocking duration in milliseconds, when reported.</summary>
    public double BlockingMs { get; set; }

    /// <summary>The sanitized source of the longest script, when reported (long animation frames only).</summary>
    public string? Script { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + StringSize(EntryType) + StringSize(Script) + 8;
}

/// <summary>
/// A browser Resource Timing entry for a request that was not matched to an <see cref="HttpRequestEvent"/>
/// (for example images, scripts, or fetches that do not use <c>HttpClient</c>).
/// </summary>
public sealed class BrowserResourceEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.BrowserResource;

    /// <summary>The sanitized URL.</summary>
    public string? Url { get; set; }

    /// <summary>The resource initiator type (for example <c>fetch</c>, <c>img</c>, or <c>script</c>).</summary>
    public string? InitiatorType { get; set; }

    /// <summary>The transferred size in bytes (zero for cache hits or cross-origin resources without Timing-Allow-Origin).</summary>
    public long TransferSize { get; set; }

    /// <summary>The decoded body size in bytes.</summary>
    public long DecodedBodySize { get; set; }

    /// <summary>Whether the response appears to have been served from the browser cache.</summary>
    public bool? FromCache { get; set; }

    /// <summary>The HTTP response status when reported by the browser; otherwise <see langword="null"/>.</summary>
    public int? StatusCode { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + StringSize(Url) + StringSize(InitiatorType) + 32;
}
