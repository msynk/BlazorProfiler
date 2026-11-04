using System.Diagnostics;
using BlazorProfiler.Correlation;
using BlazorProfiler.Events;

namespace BlazorProfiler.Collectors;

/// <summary>
/// Establishes operation context for user interactions and navigations by observing Blazor's built-in activities
/// (<c>Microsoft.AspNetCore.Components.HandleEvent</c> and <c>Microsoft.AspNetCore.Components.Navigate</c>).
/// </summary>
/// <remarks>
/// <para>
/// When an activity starts, the collector creates an operation identifier and places an <see cref="OperationFrame"/> in the
/// ambient execution context. Everything that runs in that context afterwards (the handler, its asynchronous
/// continuations, HTTP and interop calls it starts, and the render batch processed right after it) is attributed to the
/// operation with <see cref="CorrelationKind.ExecutionContext"/> confidence.
/// </para>
/// <para>
/// The collector requests activities with propagation data only. When no other listener (for example OpenTelemetry)
/// requested full data, the collector removes the activity from <see cref="Activity.Current"/> again, so outgoing HTTP
/// requests do not start carrying trace headers just because the profiler is running.
/// </para>
/// </remarks>
public sealed class ComponentActivityCollector : ProfilerCollectorBase
{
    /// <summary>The framework activity source.</summary>
    public const string SourceName = "Microsoft.AspNetCore.Components";

    private const string HandleEventName = "Microsoft.AspNetCore.Components.HandleEvent";
    private const string NavigateName = "Microsoft.AspNetCore.Components.Navigate";

    private ActivityListener? _listener;

    /// <inheritdoc />
    public override CollectorDescriptor Descriptor { get; } = new()
    {
        Name = "component-activities",
        Description = "Interaction and navigation operation context used to correlate handlers, render batches, HTTP, and interop calls.",
        Mechanism = "System.Diagnostics.ActivityListener on the 'Microsoft.AspNetCore.Components' activity source (HandleEvent, Navigate).",
        Capability = CapabilityLevel.Full,
        Overhead = OverheadLevel.Low,
        Compatibility = ".NET 10+. Server and Blazor Web Apps register component tracing automatically; WebAssembly requires the profiler's WebAssembly integration.",
        Degradation = "Without activities, events are recorded but not grouped into interactions; rules fall back to temporal correlation with lower confidence.",
    };

    /// <inheritdoc />
    protected override bool IsEnabled(ProfilerOptions options) => options.Collectors.ComponentActivities;

    /// <inheritdoc />
    protected override void OnStart(ProfilerCollectorContext context)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Name is HandleEventName or NavigateName ? ActivitySamplingResult.PropagationData : ActivitySamplingResult.None,
            ActivityStarted = OnActivityStarted,
        };

        ActivitySource.AddActivityListener(listener);
        _listener = listener;
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        _listener?.Dispose();
        _listener = null;
    }

    private void OnActivityStarted(Activity activity)
    {
        var profiler = Profiler;
        if (profiler is null || !profiler.IsRecording)
        {
            return;
        }

        try
        {
            var kind = activity.OperationName switch
            {
                HandleEventName => OperationKind.Interaction,
                NavigateName => OperationKind.Navigation,
                _ => (OperationKind?)null,
            };

            if (kind is null)
            {
                return;
            }

            if (!activity.IsAllDataRequested && ReferenceEquals(Activity.Current, activity))
            {
                // Only the profiler asked for this activity: keep the ambient Activity unchanged for the application.
                Activity.Current = null;
            }

            // The change is made synchronously inside Activity.Start(), so it flows into the framework's dispatch frame
            // and is reverted automatically by the enclosing asynchronous method when that frame completes.
            ProfilerContext.Push(new OperationFrame(profiler.CreateOperationId(), kind.Value, CorrelationKind.ExecutionContext)
            {
                CircuitId = ProfilerContext.Current?.CircuitId,
                StartTimestamp = profiler.Clock.GetTimestamp(),
            });
        }
        catch (Exception ex)
        {
            RecordError(ex);
        }
    }
}
