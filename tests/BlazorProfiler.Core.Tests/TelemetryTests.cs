using System.Diagnostics;
using System.Diagnostics.Metrics;
using BlazorProfiler.Cli;
using BlazorProfiler.Events;
using BlazorProfiler.Export;
using BlazorProfiler.Pipeline;
using BlazorProfiler.Storage;

namespace BlazorProfiler.Core.Tests;

// The meter and activity source are process-wide names; these tests must not run in parallel with each other.
[Collection(nameof(TelemetryTests))]
public class TelemetryTests
{
    [Fact]
    public void NothingIsPublishedByDefault()
    {
        using var host = TestHost.Create();
        Assert.False(host.Options.Telemetry.Metrics);
        Assert.False(host.Options.Telemetry.Traces);
    }

    [Fact]
    public void MetricsAreRecordedForRendersInteropVitalsAndLeaks()
    {
        var measurements = new List<(string Name, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ProfilerTelemetry.Name)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            lock (measurements)
            {
                measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
            }
        });
        listener.Start();

        var (host, _, _) = TestHost.WithSession(o => o.Telemetry.Metrics = true);
        using (host)
        {
            host.Emit(new ComponentRenderEvent { ComponentType = "App.Row", ComponentInstanceId = 1, Timestamp = Ev.T(0), Duration = Ev.D(4), Reason = RenderReason.Parameters, Source = InstrumentationSource.ComponentInstrumentation });
            host.Emit(Ev.Interop("chart.draw", 10, 20));
            host.Emit(new BrowserVitalEvent { Name = "LCP", Value = 1800, Timestamp = Ev.T(20) });
            host.Emit(new ComponentRetainedEvent { ComponentType = "App.Dialog", ComponentInstanceId = 2, Timestamp = Ev.T(30) });
            host.Emit(new BrowserFrustrationEvent { Signal = FrustrationSignal.DeadClick, Timestamp = Ev.T(40) });
        }

        var render = Assert.Single(measurements, m => m.Name == "blazor_profiler.component.render.duration");
        Assert.Equal(0.004, render.Value, 6);
        Assert.Equal("App.Row", render.Tags["blazor_profiler.component.type"]);
        Assert.Equal("Parameters", render.Tags["blazor_profiler.render.reason"]);
        Assert.Equal(0.02, Assert.Single(measurements, m => m.Name == "blazor_profiler.js_interop.duration").Value, 6);
        Assert.Equal(1.8, Assert.Single(measurements, m => m.Name == "blazor_profiler.browser.lcp").Value, 6);
        Assert.Equal("App.Dialog", Assert.Single(measurements, m => m.Name == "blazor_profiler.component.retained").Tags["blazor_profiler.component.type"]);
        Assert.Equal("dead_click", Assert.Single(measurements, m => m.Name == "blazor_profiler.browser.frustration").Tags["blazor_profiler.browser.frustration_signal"]);
    }

    [Fact]
    public void SpansAreChildrenOfTheCapturedTraceWithMeasuredTimes()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == ProfilerTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (spans)
                {
                    spans.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        var spanId = ActivitySpanId.CreateRandom().ToHexString();
        var (host, _, _) = TestHost.WithSession(o => o.Telemetry.Traces = true);
        using (host)
        {
            // Traced work becomes a child span; work without a trace and very short work are skipped.
            host.Emit(new ComponentRenderEvent { ComponentType = "App.Row", ComponentInstanceId = 3, Timestamp = Ev.T(100), Duration = Ev.D(12), TraceId = traceId, SpanId = spanId, Source = InstrumentationSource.ComponentInstrumentation });
            host.Emit(new ComponentRenderEvent { ComponentType = "App.Untraced", Timestamp = Ev.T(100), Duration = Ev.D(12), Source = InstrumentationSource.ComponentInstrumentation });
            host.Emit(new JSInteropEvent { Identifier = "fast", Timestamp = Ev.T(100), Duration = Ev.D(0.1), TraceId = traceId, SpanId = spanId });
            host.Emit(new JSInteropEvent { Identifier = "fail", Timestamp = Ev.T(200), Duration = Ev.D(5), TraceId = traceId, SpanId = spanId, Outcome = ProfilerOutcome.Error, ErrorType = "JSException" });
        }

        Assert.Equal(2, spans.Count);
        var render = spans.Single(s => s.DisplayName == "Render Row");
        Assert.Equal(traceId, render.TraceId.ToHexString());
        Assert.Equal(spanId, render.ParentSpanId.ToHexString());
        Assert.Equal(12, render.Duration.TotalMilliseconds, 3);
        Assert.Equal(new FakeClock().AnchorTime.AddMilliseconds(1_000 + 100).UtcDateTime, render.StartTimeUtc);
        Assert.Equal("App.Row", render.GetTagItem("blazor_profiler.component.type"));
        var failed = spans.Single(s => s.DisplayName == "JS interop fail");
        Assert.Equal(ActivityStatusCode.Error, failed.Status);
    }

    [Fact]
    public void RendersAndInteropCaptureTheAmbientTraceWhenTracesAreOn()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is ProfilerTelemetry.Name or "Test.App",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (spans)
                {
                    spans.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("Test.App");

        var (host, _, _) = TestHost.WithSession(o => o.Telemetry.Traces = true);
        using (host)
        {
            using var request = source.StartActivity("HandleEvent");
            Assert.NotNull(request);

            // No trace ids set by hand: the correlation provider captures them because spans are published.
            host.Emit(new ComponentRenderEvent { ComponentType = "App.Row", ComponentInstanceId = 1, Timestamp = Ev.T(0), Duration = Ev.D(5), Source = InstrumentationSource.ComponentInstrumentation });
            host.Emit(new JSInteropEvent { Identifier = "chart.draw", Timestamp = Ev.T(1), Duration = Ev.D(3) });
            request.Stop();

            var render = spans.Single(s => s.DisplayName == "Render Row");
            Assert.Equal(request.TraceId, render.TraceId);
            Assert.Equal(request.SpanId, render.ParentSpanId);
            Assert.Equal(request.TraceId, spans.Single(s => s.DisplayName == "JS interop chart.draw").TraceId);
        }

        // Without Traces, renders do not pay for capturing the trace context.
        var (plain, session, _) = TestHost.WithSession();
        using (plain)
        {
            using var request = source.StartActivity("HandleEvent");
            plain.Emit(new ComponentRenderEvent { ComponentType = "App.Row", Timestamp = Ev.T(0), Duration = Ev.D(5) });
            Assert.Null(Assert.Single(session.CreateSnapshot().Events.OfType<ComponentRenderEvent>()).TraceId);
        }
    }

    [Fact]
    public void RootSpansNeverAdoptTheConsumerThreadsAmbientActivity()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is ProfilerTelemetry.Name or "Test.Ambient",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (spans)
                {
                    spans.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("Test.Ambient");

        var (host, _, _) = TestHost.WithSession(o =>
        {
            o.Telemetry.Traces = true;
            o.Telemetry.RootSpans = true;
            o.CaptureTraceContext = false;
        });
        using (host)
        {
            // Synchronous processing runs the publisher on this thread, where an unrelated activity is current.
            using var unrelated = source.StartActivity("Unrelated");
            host.Emit(new ComponentRenderEvent { ComponentType = "App.Row", Timestamp = Ev.T(0), Duration = Ev.D(5) });
            Assert.Same(unrelated, Activity.Current);
        }

        var render = spans.Single(s => s.DisplayName == "Render Row");
        Assert.Equal(default, render.ParentSpanId);
    }

    [Fact]
    public async Task TraceMetadataRecordsTheApplicationAndSurvivesReexport()
    {
        var (host, session, _) = TestHost.WithSession();
        using (host)
        {
            host.Emit(Ev.Render("App.Row", 0));
            var document = TraceDocumentBuilder.Create(session, host.Options, null, null, "test");
            Assert.False(string.IsNullOrEmpty(document.Metadata.AppName));
            Assert.Contains(document.Metadata.BuildConfiguration, new[] { "Debug", "Release" });
            Assert.Equal(Environment.ProcessorCount, document.Metadata.ProcessorCount);

            // An imported trace keeps the environment it was recorded in when exported again.
            document.Metadata.AppName = "Shop.Web";
            document.Metadata.BuildConfiguration = "Debug";
            document.Metadata.ProcessorCount = 64;
            using var stream = new MemoryStream();
            await new JsonTraceExporter().ExportAsync(document, stream);
            stream.Position = 0;
            var imported = host.AddImportedSession(await TraceImporter.ReadAsync(stream));
            var again = TraceDocumentBuilder.Create(imported, host.Options, null, null, "test");
            Assert.Equal("Shop.Web", again.Metadata.AppName);
            Assert.Equal("Debug", again.Metadata.BuildConfiguration);
            Assert.Equal(64, again.Metadata.ProcessorCount);
        }
    }

    [Fact]
    public void CompareWarnsWhenTheEnvironmentsDiffer()
    {
        var release = new TraceMetadata { BuildConfiguration = "Release", Source = "server", Runtime = ".NET 10.0.14", ProcessorCount = 8 };
        Assert.Empty(TraceCli.EnvironmentWarnings(release, new TraceMetadata { BuildConfiguration = "Release", Source = "server", Runtime = ".NET 10.0.14", ProcessorCount = 8 }));
        var warnings = TraceCli.EnvironmentWarnings(release, new TraceMetadata { BuildConfiguration = "Debug", Source = "webassembly", Runtime = ".NET 11.0.0", ProcessorCount = 2 });
        Assert.Equal(4, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("Debug builds are much slower", StringComparison.Ordinal));

        // Unknown values (older traces) never produce warnings.
        Assert.Empty(TraceCli.EnvironmentWarnings(new TraceMetadata(), release));
    }

    [Fact]
    public void CompareReportsPerComponentRegressions()
    {
        static SessionSummary Summary(double rowRenders, double rowP95, double cellRenders) => new()
        {
            Components =
            [
                new ComponentSummary { ComponentType = "App.Row", Instrumented = true, Renders = new DurationSummary { Count = rowRenders, P95Ms = rowP95 } },
                new ComponentSummary { ComponentType = "App.Cell", Instrumented = true, Renders = new DurationSummary { Count = cellRenders, P95Ms = 1 } },
            ],
        };

        var rows = TraceCli.CompareComponents(Summary(10, 2, 100), Summary(30, 5, 100), 0.1, includeTimings: true);
        Assert.Contains(rows, r => r.Name == "Row renders" && r.Regressed && r.Current == 30);
        Assert.Contains(rows, r => r.Name == "Row render p95" && r.Regressed);
        Assert.DoesNotContain(rows, r => r.Name.StartsWith("Cell", StringComparison.Ordinal));

        // Without --include-timings only deterministic counts are compared.
        Assert.DoesNotContain(TraceCli.CompareComponents(Summary(10, 2, 100), Summary(30, 5, 100), 0.1, includeTimings: false), r => r.Name.EndsWith("p95", StringComparison.Ordinal));

        // Allocations per render are compared without --include-timings (they are deterministic for the same data).
        var before = Summary(10, 2, 100);
        var after = Summary(10, 2, 100);
        before.Components[0].RenderAllocatedBytes = 10 * 4_096;
        before.Components[0].RendersWithAllocations = 10;
        after.Components[0].RenderAllocatedBytes = 10 * 40_960;
        after.Components[0].RendersWithAllocations = 10;
        Assert.Contains(TraceCli.CompareComponents(before, after, 0.1, includeTimings: false), r => r.Name == "Row alloc/render" && r.Regressed);

        // Fewer renders is an improvement, not a regression.
        Assert.Contains(TraceCli.CompareComponents(Summary(30, 2, 100), Summary(10, 2, 100), 0.1, false), r => r.Name == "Row renders" && r.Improved && !r.Regressed);
    }
}
