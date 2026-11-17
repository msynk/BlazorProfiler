using System.Diagnostics;
using BlazorProfiler.Api;
using BlazorProfiler.Events;
using BlazorProfiler.Export;

namespace BlazorProfiler.Core.Tests;

/// <summary>
/// A session recorded for two simulated hours with small retention limits: storage, aggregates, the time series, diagnostics,
/// the API, and export must stay bounded and consistent long after the first events were evicted.
/// </summary>
public class LongSessionTests
{
    private const int Minutes = 120;
    private const int OperationsPerMinute = 60;

    [Fact]
    public async Task TwoHourSessionStaysBoundedAndKeepsItsAggregates()
    {
        var clock = new FakeClock();
        using var host = TestHost.Create(clock, o =>
        {
            o.Retention.MaxEvents = 5_000;
            o.Retention.MaxAge = TimeSpan.FromMinutes(10);
            o.Retention.MaxMemoryBytes = 8L * 1024 * 1024;
            o.Retention.MaxAggregateKeys = 500;
            o.Retention.TimeSeriesBuckets = 300;
        });
        var session = host.StartSession();

        long operation = 0;
        var emitted = 0;
        for (var minute = 0; minute < Minutes; minute++)
        {
            for (var i = 0; i < OperationsPerMinute; i++)
            {
                operation++;
                var at = clock.Now;
                host.Emit(new EventHandlerEvent { ComponentType = "App.Pages.Orders", MethodName = "Refresh", AttributeName = "onclick", Timestamp = at, Duration = clock.Ms(30), OperationId = operation, Correlation = CorrelationKind.Self });
                host.Emit(new ComponentRenderEvent { ComponentType = "App.Shared.Row", ComponentInstanceId = 1 + (i % 50), Timestamp = at + clock.Ms(1), Duration = clock.Ms(2), OperationId = operation, Source = InstrumentationSource.ComponentInstrumentation });

                // A growing set of endpoints and component types exercises the aggregate key limit.
                host.Emit(new HttpRequestEvent { Method = "GET", Url = $"https://api.test/items/{operation % 900}/v{operation % 7}", Timestamp = at + clock.Ms(3), Duration = clock.Ms(20), StatusCode = 200, OperationId = operation, Source = InstrumentationSource.HttpMessageHandler });
                host.Emit(new ComponentRenderEvent { ComponentType = $"App.Generated.C{operation % 1_000}", ComponentInstanceId = 100_000 + operation, Timestamp = at + clock.Ms(4), Duration = clock.Ms(1), OperationId = operation, Source = InstrumentationSource.ComponentInstrumentation });
                emitted += 4;
                clock.AdvanceMs(1_000);
            }
        }

        var statistics = session.GetStatistics();
        Assert.True(statistics.StoredEvents <= 5_000, $"{statistics.StoredEvents} events stored");
        Assert.True(statistics.StoredBytes <= 8L * 1024 * 1024);
        Assert.Equal(emitted, statistics.AcceptedEvents);
        Assert.Equal(emitted - statistics.StoredEvents, statistics.EvictedEvents);

        // Age: nothing older than ten minutes is kept.
        var snapshot = session.CreateSnapshot();
        Assert.True(snapshot.Events.Min(e => e.Timestamp) >= clock.Now - clock.Ms(10 * 60 * 1000) - clock.Ms(1_000));

        // Aggregates survive eviction: every render and handler of the two hours is counted.
        var summary = session.GetSummary();
        var row = Assert.Single(summary.Components, c => c.ComponentType == "App.Shared.Row");
        Assert.Equal(Minutes * OperationsPerMinute, row.Renders.Count);
        Assert.Equal(Minutes * OperationsPerMinute, summary.Handlers.Single().Durations.Count);
        Assert.True(summary.Components.Count <= 500 + 1, $"{summary.Components.Count} component aggregates");
        Assert.True(summary.Endpoints.Count <= 500 + 1, $"{summary.Endpoints.Count} endpoint aggregates");
        Assert.True(summary.TimeSeries.Count <= 300, $"{summary.TimeSeries.Count} time series points");

        // Analysis, the API, MCP, and export work on the long session and stay fast.
        var watch = Stopwatch.StartNew();
        var report = host.Diagnostics.Analyze(session);
        Assert.Empty(report.RuleErrors);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"analysis took {watch.Elapsed}");

        var api = new ProfilerApi(host, "test");
        var page = api.GetEvents(ProfilerApiAccess.Full, session.Id, 0, 100);
        Assert.True(page.Gap, "Reading from the start of an evicted session must report a gap.");
        var mcp = new ProfilerMcpServer(api);
        var overview = await mcp.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_overview","arguments":{}}}""", ProfilerApiAccess.Full);
        Assert.Contains("Event handlers: 7,200", overview!, StringComparison.Ordinal);

        var document = TraceDocumentBuilder.Create(session, host.Options, report, null, "test");
        using var stream = new MemoryStream();
        await new CompressedTraceExporter().ExportAsync(document, stream);
        stream.Position = 0;
        var imported = await TraceImporter.ReadAsync(stream);
        Assert.Equal(statistics.StoredEvents, imported.Events.Count);
        Assert.Equal(Minutes * OperationsPerMinute, imported.Summary!.Components.Single(c => c.ComponentType == "App.Shared.Row").Renders.Count);
    }
}
