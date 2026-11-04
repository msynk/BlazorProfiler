using System.Diagnostics.Metrics;
using BlazorProfiler.Correlation;
using BlazorProfiler.Events;
using BlazorProfiler.Instrumentation;

namespace BlazorProfiler.Collectors;

/// <summary>
/// Collects Blazor's built-in component metrics (.NET 10+): parameter updates, render batches, event handlers, and
/// navigation. Each measurement is converted into an event on the thread that recorded it, so ambient correlation
/// (operation, circuit) is preserved.
/// </summary>
public sealed class ComponentMetricsCollector : ProfilerCollectorBase
{
    /// <summary>The framework meter for events and navigation.</summary>
    public const string ComponentsMeter = "Microsoft.AspNetCore.Components";

    /// <summary>The framework meter for rendering lifecycle.</summary>
    public const string LifecycleMeter = "Microsoft.AspNetCore.Components.Lifecycle";

    private const string ComponentTypeTag = "aspnetcore.components.type";
    private const string RouteTag = "aspnetcore.components.route";
    private const string AttributeTag = "aspnetcore.components.attribute.name";
    private const string MethodTag = "code.function.name";
    private const string ErrorTag = "error.type";

    // A render batch waits for its size, which the framework records right after the duration on the same thread. The
    // ambient correlation (synchronization context and operation frame) is captured with it, because a batch that never
    // receives a size is emitted later from whatever work runs next on the thread, possibly for another circuit.
    [ThreadStatic]
    private static RenderBatchEvent? t_pendingBatch;

    [ThreadStatic]
    private static SynchronizationContext? t_pendingContext;

    [ThreadStatic]
    private static OperationFrame? t_pendingFrame;

    private readonly ComponentInstanceRegistry _registry;
    private readonly ProfilerHostingInfo _hosting;
    private readonly IMeterFactory? _meterFactory;
    private MeterListener? _listener;

    /// <summary>Creates the collector.</summary>
    /// <param name="registry">The component instance registry.</param>
    /// <param name="hosting">Hosting information.</param>
    /// <param name="meterFactory">
    /// The application's meter factory. When provided, only meters created by this factory are observed, which isolates
    /// several applications hosted in one process (for example integration tests).
    /// </param>
    public ComponentMetricsCollector(ComponentInstanceRegistry registry, ProfilerHostingInfo hosting, IMeterFactory? meterFactory = null)
    {
        _registry = registry;
        _hosting = hosting;
        _meterFactory = meterFactory;
    }

    /// <inheritdoc />
    public override CollectorDescriptor Descriptor { get; } = new()
    {
        Name = "component-metrics",
        Description = "Parameter updates (per component type), render batches (duration and number of component diffs), event handler duration, and navigation.",
        Mechanism = "System.Diagnostics.Metrics MeterListener on the 'Microsoft.AspNetCore.Components' and 'Microsoft.AspNetCore.Components.Lifecycle' meters.",
        Capability = CapabilityLevel.Partial,
        Overhead = OverheadLevel.Low,
        Compatibility = ".NET 10+. Server and Blazor Web Apps register the metrics automatically; WebAssembly requires the profiler's WebAssembly integration (which registers them) and MetricsSupport enabled.",
        Degradation = "Framework metrics are aggregated by component type: no per-instance identity and no per-component render timing. Use ProfiledComponentBase for per-instance data.",
    };

    /// <inheritdoc />
    protected override bool IsEnabled(ProfilerOptions options) => options.Collectors.ComponentMetrics;

    /// <inheritdoc />
    protected override string? GetUnsupportedReason(ProfilerCollectorContext context) =>
        AppContext.TryGetSwitch("System.Diagnostics.Metrics.Meter.IsSupported", out var supported) && !supported
            ? "System.Diagnostics.Metrics is disabled in this application (MetricsSupport=false). Set <MetricsSupport>true</MetricsSupport> in the project file."
            : null;

    /// <inheritdoc />
    protected override void OnStart(ProfilerCollectorContext context)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name is ComponentsMeter or LifecycleMeter
                    && (_meterFactory is null || ReferenceEquals(instrument.Meter.Scope, _meterFactory)))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<double>(OnDouble);
        listener.SetMeasurementEventCallback<int>(OnInt);
        listener.SetMeasurementEventCallback<long>(OnLong);
        listener.Start();
        _listener = listener;
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        _listener?.Dispose();
        _listener = null;
        ClearPendingBatch();
    }

    private void OnDouble(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var profiler = Profiler;
        if (profiler is null || !profiler.IsRecording)
        {
            return;
        }

        try
        {
            var end = profiler.Clock.GetTimestamp();
            var duration = profiler.Clock.FromSeconds(measurement);
            switch (instrument.Name)
            {
                case "aspnetcore.components.update_parameters.duration":
                    {
                        var type = GetTag(tags, ComponentTypeTag);
                        if (_registry.IsInstrumented(type))
                        {
                            // ProfiledComponentBase reports this parameter update with more detail.
                            return;
                        }

                        var e = new ParametersSetEvent { ComponentType = type, Source = InstrumentationSource.FrameworkMetrics };
                        e.SetTimingFromEnd(end, duration);
                        ApplyError(e, tags);
                        Emit(e);
                        break;
                    }

                case "aspnetcore.components.render_diff.duration":
                    {
                        FlushPendingBatch();
                        var e = new RenderBatchEvent
                        {
                            Source = InstrumentationSource.FrameworkMetrics,
                            UpdatedComponentCount = -1,
                            IncludesClientRoundTrip = _hosting.IsWebAssembly ? false : null,
                        };
                        e.SetTimingFromEnd(end, duration);
                        ApplyError(e, tags);

                        // The batch size is recorded immediately after the duration on the same thread.
                        t_pendingBatch = e;
                        t_pendingContext = SynchronizationContext.Current;
                        t_pendingFrame = ProfilerContext.Current;
                        break;
                    }

                case "aspnetcore.components.handle_event.duration":
                    {
                        var frame = ProfilerContext.Current;
                        var e = new EventHandlerEvent
                        {
                            ComponentType = GetTag(tags, ComponentTypeTag),
                            MethodName = GetTag(tags, MethodTag),
                            AttributeName = GetTag(tags, AttributeTag),
                            Source = InstrumentationSource.FrameworkMetrics,
                        };
                        e.SetTimingFromEnd(end, duration);
                        ApplyError(e, tags);
                        if (frame is { Kind: OperationKind.Interaction })
                        {
                            e.OperationId = frame.OperationId;
                            e.Correlation = CorrelationKind.Self;
                            e.SynchronousDuration = frame.HandlerSynchronousTicks;
                            e.ComponentInstanceId = frame.HandlerInstanceId;
                        }

                        Emit(e);
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            RecordError(ex);
        }
    }

    private void OnInt(Instrument instrument, int measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (instrument.Name != "aspnetcore.components.render_diff.size")
        {
            return;
        }

        var pending = t_pendingBatch;
        var context = t_pendingContext;
        var frame = t_pendingFrame;
        ClearPendingBatch();
        if (pending is null || Profiler is not { IsRecording: true })
        {
            return;
        }

        try
        {
            pending.UpdatedComponentCount = measurement;
            EmitInContext(pending, context, frame);
        }
        catch (Exception ex)
        {
            RecordError(ex);
        }
    }

    private void OnLong(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var profiler = Profiler;
        if (instrument.Name != "aspnetcore.components.navigate" || profiler is null || !profiler.IsRecording)
        {
            return;
        }

        try
        {
            var frame = ProfilerContext.Current;
            var e = new NavigationEvent
            {
                ComponentType = GetTag(tags, ComponentTypeTag),
                Route = GetTag(tags, RouteTag),
                Timestamp = profiler.Clock.GetTimestamp(),
                Source = InstrumentationSource.FrameworkMetrics,
            };
            if (frame is { Kind: OperationKind.Navigation })
            {
                e.OperationId = frame.OperationId;
                e.Correlation = CorrelationKind.Self;
            }

            Emit(e);
        }
        catch (Exception ex)
        {
            RecordError(ex);
        }
    }

    private void FlushPendingBatch()
    {
        // A batch whose size was never reported is emitted without one, attributed to the context it was recorded in.
        var pending = t_pendingBatch;
        if (pending is not null)
        {
            var context = t_pendingContext;
            var frame = t_pendingFrame;
            ClearPendingBatch();
            EmitInContext(pending, context, frame);
        }
    }

    private static void ClearPendingBatch()
    {
        t_pendingBatch = null;
        t_pendingContext = null;
        t_pendingFrame = null;
    }

    /// <summary>
    /// Emits an event with the ambient correlation it was recorded under. Correlation providers read the ambient
    /// synchronization context and operation frame during <see cref="IProfiler.Emit"/>, so both are restored temporarily
    /// when they differ from the current ones.
    /// </summary>
    private void EmitInContext(ProfilerEvent e, SynchronizationContext? context, OperationFrame? frame)
    {
        var currentContext = SynchronizationContext.Current;
        var currentFrame = ProfilerContext.Current;
        if (ReferenceEquals(currentContext, context) && ReferenceEquals(currentFrame, frame))
        {
            Emit(e);
            return;
        }

        SynchronizationContext.SetSynchronizationContext(context);
        ProfilerContext.Restore(frame);
        try
        {
            Emit(e);
        }
        finally
        {
            ProfilerContext.Restore(currentFrame);
            SynchronizationContext.SetSynchronizationContext(currentContext);
        }
    }

    private static void ApplyError(ProfilerEvent e, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var error = GetTag(tags, ErrorTag);
        if (error is not null)
        {
            e.Outcome = error.Contains("OperationCanceled", StringComparison.Ordinal) || error.Contains("TaskCanceled", StringComparison.Ordinal)
                ? ProfilerOutcome.Canceled
                : ProfilerOutcome.Error;
            e.ErrorType = error;
        }
    }

    private static string? GetTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal))
            {
                return tag.Value as string;
            }
        }

        return null;
    }
}
