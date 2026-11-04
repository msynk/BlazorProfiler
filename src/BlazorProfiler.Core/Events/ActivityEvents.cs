using System.Text.Json.Serialization;

namespace BlazorProfiler.Events;

/// <summary>A render batch was produced by a renderer and applied to the display.</summary>
/// <remarks>
/// <para>
/// When produced from framework metrics, <see cref="ProfilerEvent.Duration"/> spans from the start of the batch
/// (rendering queued components and computing diffs) until the renderer's display update completed:
/// </para>
/// <list type="bullet">
/// <item><description>Blazor WebAssembly: the DOM update is applied synchronously, so the duration includes diff generation and
/// DOM patching, but not browser layout or paint.</description></item>
/// <item><description>Blazor Server: the display update completes when the browser acknowledges the batch, so the duration
/// includes diff generation, serialization, network transport, and browser-side application. See
/// <see cref="IncludesClientRoundTrip"/>.</description></item>
/// </list>
/// </remarks>
public sealed class RenderBatchEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.RenderBatch;

    /// <summary>The number of components whose output was diffed in this batch.</summary>
    public int UpdatedComponentCount { get; set; }

    /// <summary>
    /// Whether <see cref="ProfilerEvent.Duration"/> includes a client acknowledgement round trip (Blazor Server).
    /// <see langword="null"/> when the hosting model is unknown.
    /// </summary>
    public bool? IncludesClientRoundTrip { get; set; }

    /// <summary>The mechanism that produced the event.</summary>
    public InstrumentationSource Source { get; set; }
}

/// <summary>A UI event handler (for example <c>@onclick</c>) was dispatched by the renderer.</summary>
/// <remarks>
/// When produced from framework metrics, <see cref="ProfilerEvent.Duration"/> spans from the start of the dispatch until the
/// task returned by the handler completed. For asynchronous handlers on <c>ComponentBase</c> this includes the render
/// requested after the handler completed; for synchronous handlers the subsequent render batch is reported separately.
/// It never includes browser input delay or paint; see <see cref="BrowserInteractionEvent"/> for user-perceived latency.
/// </remarks>
public sealed class EventHandlerEvent : ComponentEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.EventHandler;

    /// <summary>The handler method name (for example <c>IncrementCount</c>). Lambdas have compiler-generated names.</summary>
    public string? MethodName { get; set; }

    /// <summary>The event attribute name (for example <c>onclick</c>).</summary>
    public string? AttributeName { get; set; }

    /// <summary>
    /// The synchronous portion of the handler in ticks, when measured by component instrumentation; otherwise zero.
    /// </summary>
    public long SynchronousDuration { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + StringSize(MethodName) + StringSize(AttributeName) + 8;
}

/// <summary>The router navigated to a page component.</summary>
public sealed class NavigationEvent : ComponentEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.Navigation;

    /// <summary>The matched route template (not the concrete URL).</summary>
    public string? Route { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + StringSize(Route);
}

/// <summary>A custom operation recorded through manual instrumentation.</summary>
public sealed class OperationEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.Operation;

    /// <summary>The operation name.</summary>
    public string? Name { get; set; }

    /// <summary>An optional category used for grouping.</summary>
    public string? Category { get; set; }

    /// <summary>The component type that started the operation, when known.</summary>
    public string? ComponentType { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + StringSize(Name) + StringSize(Category) + StringSize(ComponentType);
}

/// <summary>Kinds of <see cref="MarkerEvent"/>.</summary>
public enum MarkerType
{
    /// <summary>A user-created bookmark.</summary>
    Bookmark = 0,
    /// <summary>The session was paused.</summary>
    SessionPaused = 1,
    /// <summary>The session was resumed.</summary>
    SessionResumed = 2,
    /// <summary>Events were dropped because a queue or limit was exceeded.</summary>
    EventsDropped = 3,
    /// <summary>A collector failed.</summary>
    CollectorFailed = 4,
    /// <summary>A custom application mark.</summary>
    Custom = 5,
}

/// <summary>An instantaneous marker on the timeline.</summary>
public sealed class MarkerEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.Marker;

    /// <summary>The marker type.</summary>
    public MarkerType MarkerType { get; set; }

    /// <summary>A short label.</summary>
    public string? Name { get; set; }

    /// <summary>An associated count (for example the number of dropped events).</summary>
    public long Count { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + StringSize(Name) + 12;
}
