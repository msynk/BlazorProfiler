using System.Diagnostics;
using BlazorProfiler.Events;

namespace BlazorProfiler.Correlation;

/// <summary>The kind of logical operation an <see cref="OperationFrame"/> represents.</summary>
public enum OperationKind
{
    /// <summary>A UI event dispatched by the renderer.</summary>
    Interaction = 0,
    /// <summary>A router navigation.</summary>
    Navigation = 1,
    /// <summary>A custom operation started through manual instrumentation.</summary>
    Custom = 2,
    /// <summary>A component lifecycle method (adds origin information, inherits the operation).</summary>
    ComponentScope = 3,
}

/// <summary>
/// Ambient correlation information that flows with the .NET execution context. Collectors read
/// <see cref="ProfilerContext.Current"/> on the emitting thread to attribute events to operations and components.
/// </summary>
public sealed class OperationFrame
{
    /// <summary>Creates a frame.</summary>
    public OperationFrame(long operationId, OperationKind kind, CorrelationKind correlation)
    {
        OperationId = operationId;
        Kind = kind;
        Correlation = correlation;
    }

    /// <summary>The operation identifier (zero when the frame only carries component origin information).</summary>
    public long OperationId { get; }

    /// <summary>The frame kind.</summary>
    public OperationKind Kind { get; }

    /// <summary>The correlation kind applied to events attributed through this frame.</summary>
    public CorrelationKind Correlation { get; }

    /// <summary>The circuit the operation runs on, when known.</summary>
    public string? CircuitId { get; init; }

    /// <summary>The component type that originated work in this frame, when known.</summary>
    public string? ComponentType { get; init; }

    /// <summary>The component instance that originated work in this frame, when known.</summary>
    public long ComponentInstanceId { get; init; }

    /// <summary>The lifecycle method that originated work in this frame, when known.</summary>
    public LifecycleMethod? LifecycleMethod { get; init; }

    /// <summary>The enclosing frame, when frames are nested.</summary>
    public OperationFrame? Parent { get; init; }

    /// <summary>The monotonic timestamp at which the frame was created.</summary>
    public long StartTimestamp { get; init; }

    /// <summary>
    /// For interaction frames: the synchronous duration of the event handler in ticks, recorded by component
    /// instrumentation when the receiving component is instrumented; otherwise zero.
    /// </summary>
    public long HandlerSynchronousTicks { get; set; }

    /// <summary>For interaction frames: the instance that handled the event, when known.</summary>
    public long HandlerInstanceId { get; set; }
}

/// <summary>Access to the ambient <see cref="OperationFrame"/>.</summary>
/// <remarks>
/// The ambient value is stored in an <see cref="AsyncLocal{T}"/>, so it flows into asynchronous continuations started
/// while it is set, and changes made inside an <see langword="async"/> method do not leak to its caller.
/// </remarks>
public static class ProfilerContext
{
    private static readonly AsyncLocal<OperationFrame?> s_current = new();

    /// <summary>Gets the ambient frame.</summary>
    public static OperationFrame? Current => s_current.Value;

    /// <summary>Sets the ambient frame and returns the previous one, which must be passed to <see cref="Restore"/>.</summary>
    public static OperationFrame? Push(OperationFrame frame)
    {
        var previous = s_current.Value;
        s_current.Value = frame;
        return previous;
    }

    /// <summary>Restores a frame returned by <see cref="Push"/>.</summary>
    public static void Restore(OperationFrame? previous) => s_current.Value = previous;

    /// <summary>
    /// Creates a component origin frame that inherits the ambient operation and records which component started the work.
    /// </summary>
    public static OperationFrame CreateComponentFrame(string? componentType, long instanceId, LifecycleMethod? method, long timestamp)
    {
        var parent = s_current.Value;
        return new OperationFrame(parent?.OperationId ?? 0, OperationKind.ComponentScope, parent?.Correlation ?? CorrelationKind.None)
        {
            CircuitId = parent?.CircuitId,
            ComponentType = componentType,
            ComponentInstanceId = instanceId,
            LifecycleMethod = method,
            Parent = parent,
            StartTimestamp = timestamp,
        };
    }
}

/// <summary>Enriches events with correlation information on the thread that emits them.</summary>
/// <remarks>
/// Providers run synchronously inside <see cref="IProfiler.Emit(ProfilerEvent)"/> before the event is queued, because
/// ambient context such as <see cref="AsyncLocal{T}"/> values and <see cref="SynchronizationContext.Current"/> is only
/// observable on the emitting thread. Implementations must be fast, allocation-free where possible, and must not throw.
/// </remarks>
public interface IProfilerCorrelationProvider
{
    /// <summary>Adds correlation information to <paramref name="profilerEvent"/>. Existing values must not be overwritten.</summary>
    void Enrich(ProfilerEvent profilerEvent);
}

/// <summary>
/// Correlates events with the ambient <see cref="OperationFrame"/> and, optionally, the ambient <see cref="Activity"/>.
/// </summary>
public sealed class AmbientCorrelationProvider : IProfilerCorrelationProvider
{
    private readonly bool _captureTraceContext;

    /// <summary>Creates the provider.</summary>
    public AmbientCorrelationProvider(bool captureTraceContext) => _captureTraceContext = captureTraceContext;

    /// <inheritdoc />
    public void Enrich(ProfilerEvent profilerEvent)
    {
        var frame = ProfilerContext.Current;
        if (frame is not null)
        {
            if (profilerEvent.OperationId == 0 && frame.OperationId != 0)
            {
                profilerEvent.OperationId = frame.OperationId;
                if (profilerEvent.Correlation == CorrelationKind.None)
                {
                    profilerEvent.Correlation = frame.Correlation;
                }
            }

            profilerEvent.CircuitId ??= frame.CircuitId;
            ApplyOrigin(profilerEvent, frame);
        }

        if (_captureTraceContext && profilerEvent.TraceId is null && WantsTraceContext(profilerEvent.Kind))
        {
            var activity = Activity.Current;
            if (activity is not null && activity.IdFormat == ActivityIdFormat.W3C)
            {
                profilerEvent.TraceId = activity.TraceId.ToHexString();
                profilerEvent.SpanId = activity.SpanId.ToHexString();
            }
        }
    }

    private static bool WantsTraceContext(ProfilerEventKind kind) =>
        kind is ProfilerEventKind.HttpRequest or ProfilerEventKind.DatabaseCommand or ProfilerEventKind.EventHandler or ProfilerEventKind.Operation or ProfilerEventKind.Navigation;

    private static void ApplyOrigin(ProfilerEvent profilerEvent, OperationFrame frame)
    {
        // Walk to the nearest frame that carries component origin information.
        var origin = frame;
        while (origin is not null && origin.ComponentType is null)
        {
            origin = origin.Parent;
        }

        if (origin is null)
        {
            return;
        }

        switch (profilerEvent)
        {
            case HttpRequestEvent http when http.OriginComponentType is null:
                http.OriginComponentType = origin.ComponentType;
                http.OriginComponentInstanceId = origin.ComponentInstanceId;
                http.OriginLifecycleMethod = origin.LifecycleMethod;
                break;
            case JSInteropEvent js when js.OriginComponentType is null:
                js.OriginComponentType = origin.ComponentType;
                js.OriginComponentInstanceId = origin.ComponentInstanceId;
                break;
            case DatabaseCommandEvent db when db.OriginComponentType is null:
                db.OriginComponentType = origin.ComponentType;
                db.OriginComponentInstanceId = origin.ComponentInstanceId;
                db.OriginLifecycleMethod = origin.LifecycleMethod;
                break;
            case OperationEvent operation when operation.ComponentType is null:
                operation.ComponentType = origin.ComponentType;
                break;
        }
    }
}
