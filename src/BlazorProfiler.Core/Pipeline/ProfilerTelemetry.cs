using System.Diagnostics;
using System.Diagnostics.Metrics;
using BlazorProfiler.Analysis;
using BlazorProfiler.Events;

namespace BlazorProfiler.Pipeline;

/// <summary>
/// Publishes what the profiler measures, and the framework does not, as OpenTelemetry-compatible metrics and spans through
/// <see cref="System.Diagnostics.Metrics.Meter"/> and <see cref="System.Diagnostics.ActivitySource"/> named
/// <see cref="Name"/>. Add them to an OpenTelemetry pipeline (<c>.AddMeter("BlazorProfiler")</c>,
/// <c>.AddSource("BlazorProfiler")</c>) to see per-component render timing, lifecycle timing, JavaScript interop,
/// user-perceived interaction latency, long animation frames, Core Web Vitals, leaks, and frustration signals in the
/// Aspire dashboard or any other backend.
/// </summary>
/// <remarks>
/// <para>
/// HTTP requests, database commands, and Blazor's own <c>HandleEvent</c>/<c>Navigate</c> activities are not republished:
/// their libraries instrument them already. Spans are created after the fact (with the measured start and end times) on
/// the profiler's background consumer, as children of the trace that was active where the work ran when that trace was
/// captured (<see cref="ProfilerOptions.CaptureTraceContext"/>), so renders and interop calls appear inside the
/// <c>HandleEvent</c> span of the click that caused them.
/// </para>
/// <para>
/// Metrics record one measurement per event that reaches the pipeline. Under sampling, histogram counts are therefore
/// lower than the real counts; counters add each event's sample weight.
/// </para>
/// </remarks>
public sealed class ProfilerTelemetry : IProfilerEventProcessor, IDisposable
{
    /// <summary>The name of the meter and the activity source.</summary>
    public const string Name = "BlazorProfiler";

    private readonly TelemetryOptions _options;
    private readonly IProfilerClock _clock;
    private readonly Meter? _meter;
    private readonly ActivitySource? _source;
    private readonly Histogram<double>? _renderDuration;
    private readonly Histogram<double>? _lifecycleDuration;
    private readonly Histogram<double>? _interopDuration;
    private readonly Histogram<double>? _interactionDuration;
    private readonly Histogram<double>? _longFrameDuration;
    private readonly Histogram<double>? _lcp;
    private readonly Histogram<double>? _inp;
    private readonly Histogram<double>? _cls;
    private readonly Counter<double>? _retained;
    private readonly Counter<double>? _frustration;
    private readonly long _minimumSpanTicks;

    /// <summary>Creates the publisher.</summary>
    public ProfilerTelemetry(TelemetryOptions options, IProfilerClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _options = options;
        _clock = clock;
        _minimumSpanTicks = clock.FromMilliseconds(Math.Max(0, options.MinimumSpanDurationMs));
        var version = typeof(ProfilerTelemetry).Assembly.GetName().Version?.ToString();
        if (options.Metrics)
        {
            _meter = new Meter(Name, version);
            _renderDuration = _meter.CreateHistogram<double>("blazor_profiler.component.render.duration", "s", "Duration of a component's BuildRenderTree (instrumented components).");
            _lifecycleDuration = _meter.CreateHistogram<double>("blazor_profiler.component.lifecycle.duration", "s", "Duration of a component lifecycle method, including awaited work (instrumented components).");
            _interopDuration = _meter.CreateHistogram<double>("blazor_profiler.js_interop.duration", "s", "Duration of a JavaScript interop call.");
            _interactionDuration = _meter.CreateHistogram<double>("blazor_profiler.browser.interaction.duration", "s", "User-perceived interaction latency (Event Timing): from input to the next paint.");
            _longFrameDuration = _meter.CreateHistogram<double>("blazor_profiler.browser.long_animation_frame.duration", "s", "Duration of a long animation frame or long task on the browser's main thread.");
            _lcp = _meter.CreateHistogram<double>("blazor_profiler.browser.lcp", "s", "Largest Contentful Paint.");
            _inp = _meter.CreateHistogram<double>("blazor_profiler.browser.inp", "s", "Interaction to Next Paint (estimated per page).");
            _cls = _meter.CreateHistogram<double>("blazor_profiler.browser.cls", "1", "Cumulative Layout Shift.");
            _retained = _meter.CreateCounter<double>("blazor_profiler.component.retained", "{instance}", "Released component instances still reachable after full garbage collections (likely leaks).");
            _frustration = _meter.CreateCounter<double>("blazor_profiler.browser.frustration", "{signal}", "Rage clicks and dead clicks.");
        }

        if (options.Traces)
        {
            _source = new ActivitySource(Name, version);
        }
    }

    /// <inheritdoc />
    public bool Process(ProfilerEvent profilerEvent)
    {
        try
        {
            Record(profilerEvent);
            if (_source is { } source && source.HasListeners())
            {
                Trace(source, profilerEvent);
            }
        }
        catch (Exception)
        {
            // Publishing telemetry must never affect profiling.
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _meter?.Dispose();
        _source?.Dispose();
    }

    private void Record(ProfilerEvent e)
    {
        if (_meter is null)
        {
            return;
        }

        var seconds = _clock.ToMilliseconds(e.Duration) / 1000;
        switch (e)
        {
            case ComponentRenderEvent render when _renderDuration!.Enabled:
                _renderDuration.Record(seconds, new KeyValuePair<string, object?>("blazor_profiler.component.type", render.ComponentType), new KeyValuePair<string, object?>("blazor_profiler.render.reason", render.Reason.ToString()));
                break;
            case LifecycleMethodEvent lifecycle when _lifecycleDuration!.Enabled:
                _lifecycleDuration.Record(seconds, new KeyValuePair<string, object?>("blazor_profiler.component.type", lifecycle.ComponentType), new KeyValuePair<string, object?>("blazor_profiler.lifecycle.method", lifecycle.Method.ToString()), Error(e));
                break;
            case JSInteropEvent interop when _interopDuration!.Enabled:
                _interopDuration.Record(seconds, new KeyValuePair<string, object?>("blazor_profiler.js_interop.identifier", interop.Identifier), new KeyValuePair<string, object?>("blazor_profiler.js_interop.direction", interop.Direction.ToString()), Error(e));
                break;
            case BrowserInteractionEvent interaction when _interactionDuration!.Enabled:
                _interactionDuration.Record(seconds, new KeyValuePair<string, object?>("blazor_profiler.browser.event_type", interaction.EventType));
                break;
            case BrowserLongTaskEvent frame when _longFrameDuration!.Enabled:
                _longFrameDuration.Record(seconds, new KeyValuePair<string, object?>("blazor_profiler.browser.entry_type", frame.EntryType));
                break;
            case BrowserVitalEvent { Name: "LCP" } vital:
                _lcp!.Record(vital.Value / 1000);
                break;
            case BrowserVitalEvent { Name: "INP" } vital:
                _inp!.Record(vital.Value / 1000);
                break;
            case BrowserVitalEvent { Name: "CLS" } vital:
                _cls!.Record(vital.Value);
                break;
            case ComponentRetainedEvent retained:
                _retained!.Add(e.Weight, new KeyValuePair<string, object?>("blazor_profiler.component.type", retained.ComponentType));
                break;
            case BrowserFrustrationEvent frustration:
                _frustration!.Add(e.Weight, new KeyValuePair<string, object?>("blazor_profiler.browser.frustration_signal", frustration.Signal == FrustrationSignal.RageClick ? "rage_click" : "dead_click"));
                break;
        }
    }

    private static KeyValuePair<string, object?> Error(ProfilerEvent e) => new("error.type", e.Outcome == ProfilerOutcome.Error ? e.ErrorType ?? "error" : null);

    private void Trace(ActivitySource source, ProfilerEvent e)
    {
        if (e.Duration < _minimumSpanTicks)
        {
            return;
        }

        var (name, isSpan) = e switch
        {
            ComponentRenderEvent r => ($"Render {DiagnosticContext.ShortType(r.ComponentType)}", true),
            LifecycleMethodEvent l => ($"{l.Method} {DiagnosticContext.ShortType(l.ComponentType)}", true),
            JSInteropEvent j => ($"JS interop {j.Identifier}", j.Source != InstrumentationSource.Browser || _options.BrowserSpans),
            BrowserInteractionEvent b => ($"Browser interaction {b.EventType}", _options.BrowserSpans),
            BrowserLongTaskEvent => ("Long animation frame", _options.BrowserSpans),
            _ => (string.Empty, false),
        };

        if (!isSpan)
        {
            return;
        }

        var parent = default(ActivityContext);
        if (e.TraceId is { Length: 32 } traceId && e.SpanId is { Length: 16 } spanId)
        {
            try
            {
                // Follow the parent's sampling decision, so parent-based samplers keep or drop the whole trace together.
                parent = new ActivityContext(ActivityTraceId.CreateFromString(traceId), ActivitySpanId.CreateFromString(spanId), e.TraceRecorded ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None, isRemote: false);
            }
            catch (ArgumentException)
            {
                parent = default;
            }
        }
        else if (!_options.RootSpans)
        {
            // Without a captured trace the span would start a trace of its own; skip it unless asked for.
            return;
        }

        var start = _clock.ToDateTimeOffset(e.Timestamp);

        // A default parent would make the source pick up whatever activity is current on this thread.
        var ambient = Activity.Current;
        Activity.Current = null;
        try
        {
            Publish(source, e, name, parent, start);
        }
        finally
        {
            Activity.Current = ambient;
        }
    }

    private void Publish(ActivitySource source, ProfilerEvent e, string name, ActivityContext parent, DateTimeOffset start)
    {
        using var activity = source.StartActivity(name, ActivityKind.Internal, parent, startTime: start);
        if (activity is null)
        {
            return;
        }

        switch (e)
        {
            case ComponentRenderEvent r:
                activity.SetTag("blazor_profiler.component.type", r.ComponentType);
                activity.SetTag("blazor_profiler.component.instance_id", r.ComponentInstanceId);
                activity.SetTag("blazor_profiler.render.reason", r.Reason.ToString());
                if (r.OutputUnchanged is { } unchanged)
                {
                    activity.SetTag("blazor_profiler.render.output_unchanged", unchanged);
                }

                break;
            case LifecycleMethodEvent l:
                activity.SetTag("blazor_profiler.component.type", l.ComponentType);
                activity.SetTag("blazor_profiler.component.instance_id", l.ComponentInstanceId);
                activity.SetTag("blazor_profiler.lifecycle.synchronous_ms", Math.Round(_clock.ToMilliseconds(l.SynchronousDuration), 3));
                break;
            case JSInteropEvent j:
                activity.SetTag("blazor_profiler.js_interop.identifier", j.Identifier);
                activity.SetTag("blazor_profiler.js_interop.direction", j.Direction.ToString());
                if (j.PayloadBytes is { } bytes)
                {
                    activity.SetTag("blazor_profiler.js_interop.payload_bytes", bytes);
                }

                break;
            case BrowserInteractionEvent b:
                activity.SetTag("blazor_profiler.browser.event_type", b.EventType);
                activity.SetTag("blazor_profiler.browser.input_delay_ms", b.InputDelayMs);
                activity.SetTag("blazor_profiler.browser.processing_ms", b.ProcessingMs);
                activity.SetTag("blazor_profiler.browser.presentation_delay_ms", b.PresentationDelayMs);
                if (b.ContentPaintMs is { } contentPaint)
                {
                    activity.SetTag("blazor_profiler.browser.content_paint_ms", contentPaint);
                }
                break;
            case BrowserLongTaskEvent l:
                activity.SetTag("blazor_profiler.browser.blocking_ms", l.BlockingMs);
                activity.SetTag("blazor_profiler.browser.forced_style_and_layout_ms", l.ForcedStyleAndLayoutMs);
                if (l.Scripts is { Count: > 0 } scripts)
                {
                    activity.SetTag("blazor_profiler.browser.longest_script", scripts[0].Label);
                }

                break;
        }

        if (e.Outcome == ProfilerOutcome.Error)
        {
            activity.SetStatus(ActivityStatusCode.Error, e.ErrorType);
            activity.SetTag("error.type", e.ErrorType ?? "error");
        }

        activity.SetEndTime((start + TimeSpan.FromMilliseconds(_clock.ToMilliseconds(e.Duration))).UtcDateTime);
    }
}
