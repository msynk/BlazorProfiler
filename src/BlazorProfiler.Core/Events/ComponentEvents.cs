using System.Text.Json.Serialization;

namespace BlazorProfiler.Events;

/// <summary>Base type of events that are attributable to a component type and, optionally, a component instance.</summary>
public abstract class ComponentEvent : ProfilerEvent
{
    /// <summary>The full name of the component type.</summary>
    public string? ComponentType { get; set; }

    /// <summary>
    /// Stable, process-unique identifier of the component instance, or zero when instance identity is not available
    /// (for example when the event originates from framework metrics, which are aggregated by component type).
    /// </summary>
    public long ComponentInstanceId { get; set; }

    /// <summary>The mechanism that produced the event.</summary>
    public InstrumentationSource Source { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + 16;
}

/// <summary>A component instance was created by the renderer.</summary>
public sealed class ComponentCreatedEvent : ComponentEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.ComponentCreated;
}

/// <summary>A component instance was disposed by the renderer.</summary>
public sealed class ComponentDisposedEvent : ComponentEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.ComponentDisposed;

    /// <summary>The instance lifetime in clock ticks, when the creation time is known; otherwise zero.</summary>
    public long LifetimeTicks { get; set; }
}

/// <summary>Why a component instance was expected to become unreachable.</summary>
public enum RetentionReason
{
    /// <summary>The renderer disposed the instance (observed through <c>ProfiledComponentBase</c>).</summary>
    Disposed = 0,

    /// <summary>
    /// The dependency-injection scope that created the instance ended: the Blazor Server circuit closed, or the static
    /// rendering request completed. Every component of the scope has been disposed by then.
    /// </summary>
    ScopeEnded = 1,
}

/// <summary>
/// A component instance that should have become unreachable was still alive after several full (generation 2) garbage
/// collections: something (typically an event subscription on a longer-lived service, a timer, a static cache, or an
/// undisposed <c>DotNetObjectReference</c>) still references it. Reported once per instance.
/// </summary>
public sealed class ComponentRetainedEvent : ComponentEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.ComponentRetained;

    /// <summary>Why the instance was expected to be released.</summary>
    public RetentionReason Reason { get; set; }

    /// <summary>When the instance was disposed or its scope ended (clock ticks).</summary>
    public long ReleasedTimestamp { get; set; }

    /// <summary>The number of full garbage collections the instance survived after it was released.</summary>
    public int SurvivedCollections { get; set; }

    /// <summary>Whether the check that found the instance followed a collection forced by the profiler (an explicit leak check).</summary>
    public bool ForcedCollection { get; set; }
}

/// <summary>Describes how a component's incoming parameters compared with the previous set.</summary>
public enum ParameterChangeState
{
    /// <summary>Change detection was not performed.</summary>
    Unknown = 0,
    /// <summary>This was the first parameter set for the instance.</summary>
    Initial = 1,
    /// <summary>At least one parameter value changed.</summary>
    Changed = 2,
    /// <summary>
    /// All values are equal by value, except reference-type values (such as delegates, <c>RenderFragment</c> or objects)
    /// that are different instances. The framework treats such parameters as possibly changed.
    /// </summary>
    ChangedByReferenceOnly = 3,
    /// <summary>Every parameter value is equal to the previous value.</summary>
    Unchanged = 4,
}

/// <summary>A component received parameters (<c>SetParametersAsync</c>).</summary>
/// <remarks>
/// When produced from framework metrics (<see cref="InstrumentationSource.FrameworkMetrics"/>), <see cref="ProfilerEvent.Duration"/>
/// spans from the start of <c>SetParametersAsync</c> until its returned task completed, which includes asynchronous
/// lifecycle methods such as <c>OnInitializedAsync</c> and <c>OnParametersSetAsync</c>.
/// </remarks>
public sealed class ParametersSetEvent : ComponentEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.ParametersSet;

    /// <summary>The comparison result against the previous parameter set, when change detection is enabled.</summary>
    public ParameterChangeState ChangeState { get; set; }

    /// <summary>The number of parameters supplied.</summary>
    public int ParameterCount { get; set; }

    /// <summary>
    /// Names of parameters whose values changed (or changed only by reference). Parameter values are never recorded.
    /// </summary>
    public string[]? ChangedParameters { get; set; }

    /// <inheritdoc />
    public override int EstimateSize()
    {
        var size = base.EstimateSize() + 8;
        if (ChangedParameters is not null)
        {
            size += 24 + (ChangedParameters.Length * 8);
            foreach (var name in ChangedParameters)
            {
                size += StringSize(name);
            }
        }

        return size;
    }
}

/// <summary>The reason a component rendered.</summary>
public enum RenderReason
{
    /// <summary>Unknown.</summary>
    Unknown = 0,
    /// <summary>The first render of the instance.</summary>
    FirstRender = 1,
    /// <summary>The render followed a parameter update.</summary>
    Parameters = 2,
    /// <summary>The render followed an event handled by this component.</summary>
    EventHandler = 3,
    /// <summary>The render was requested through <c>StateHasChanged</c> outside of parameters or events.</summary>
    StateHasChanged = 4,
    /// <summary>The render followed the completion of an asynchronous lifecycle method.</summary>
    AsyncLifecycleCompletion = 5,
}

/// <summary>
/// A component produced its render tree. Only emitted by opt-in component instrumentation; framework metrics do not
/// expose per-component render timing.
/// </summary>
/// <remarks>
/// <see cref="ProfilerEvent.Duration"/> covers the component's <c>BuildRenderTree</c> execution only. It excludes the
/// renderer's diffing, child component rendering, transport to the browser, and DOM updates.
/// </remarks>
public sealed class ComponentRenderEvent : ComponentEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.ComponentRender;

    /// <summary>The number of render tree frames produced, or -1 when not measured.</summary>
    public int FrameCount { get; set; } = -1;

    /// <summary>
    /// Whether the produced render output was structurally identical to the previous render.
    /// <see langword="null"/> when output comparison is disabled or not possible.
    /// </summary>
    public bool? OutputUnchanged { get; set; }

    /// <summary>Why the component rendered.</summary>
    public RenderReason Reason { get; set; }

    /// <summary>Sequential render number of the instance, starting at 1.</summary>
    public int RenderNumber { get; set; }
}

/// <summary>The result of a render request (<c>StateHasChanged</c>).</summary>
public enum RenderRequestResult
{
    /// <summary>The render was queued.</summary>
    Queued = 0,
    /// <summary>The request was ignored because a render was already queued.</summary>
    Coalesced = 1,
    /// <summary>The request was ignored because <c>ShouldRender</c> returned <see langword="false"/>.</summary>
    SuppressedByShouldRender = 2,
}

/// <summary>
/// A component's render request did not lead to a render (it was coalesced with a pending render or suppressed by
/// <c>ShouldRender</c>). Requests that were queued are not recorded individually because each one produces a
/// <see cref="ComponentRenderEvent"/>. Only emitted by opt-in component instrumentation.
/// </summary>
public sealed class RenderRequestEvent : ComponentEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.RenderRequest;

    /// <summary>The result of the request.</summary>
    public RenderRequestResult Result { get; set; }
}

/// <summary>A component lifecycle method.</summary>
public enum LifecycleMethod
{
    /// <summary><c>SetParametersAsync</c>.</summary>
    SetParametersAsync = 0,
    /// <summary><c>OnInitialized</c>.</summary>
    OnInitialized = 1,
    /// <summary><c>OnInitializedAsync</c>.</summary>
    OnInitializedAsync = 2,
    /// <summary><c>OnParametersSet</c>.</summary>
    OnParametersSet = 3,
    /// <summary><c>OnParametersSetAsync</c>.</summary>
    OnParametersSetAsync = 4,
    /// <summary><c>OnAfterRender</c>.</summary>
    OnAfterRender = 5,
    /// <summary><c>OnAfterRenderAsync</c>.</summary>
    OnAfterRenderAsync = 6,
    /// <summary><c>ShouldRender</c>.</summary>
    ShouldRender = 7,
    /// <summary><c>Dispose</c>.</summary>
    Dispose = 8,
}

/// <summary>A component lifecycle method ran. Only emitted by opt-in component instrumentation.</summary>
/// <remarks>
/// For asynchronous methods <see cref="ProfilerEvent.Duration"/> spans until the returned task completed and
/// <see cref="SynchronousDuration"/> covers only the part that ran before the method returned its task.
/// </remarks>
public sealed class LifecycleMethodEvent : ComponentEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.LifecycleMethod;

    /// <summary>The lifecycle method.</summary>
    public LifecycleMethod Method { get; set; }

    /// <summary>The synchronous portion of the method in ticks.</summary>
    public long SynchronousDuration { get; set; }

    /// <summary>Whether the method returned an incomplete task.</summary>
    public bool CompletedAsynchronously { get; set; }
}

/// <summary>
/// The set of child components rendered by an instrumented component changed. Emitted by opt-in component instrumentation
/// after a render whose child set differs from the previous one; used to build the component tree.
/// </summary>
public sealed class ComponentChildrenEvent : ComponentEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.ComponentChildren;

    /// <summary>Instance identifiers of the direct child components, in render order.</summary>
    public long[] ChildInstanceIds { get; set; } = [];

    /// <summary>Full type names of the direct child components, parallel to <see cref="ChildInstanceIds"/>.</summary>
    public string[] ChildTypes { get; set; } = [];

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + 48 + (ChildInstanceIds.Length * 16);
}

