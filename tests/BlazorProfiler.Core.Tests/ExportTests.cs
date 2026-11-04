using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BlazorProfiler.Events;
using BlazorProfiler.Export;
using BlazorProfiler.Sessions;
using BlazorProfiler.Storage;

namespace BlazorProfiler.Core.Tests;

public class ExportTests
{
    private static (ProfilerHost Host, IProfilerSession Session) SessionWithData()
    {
        var (host, session, _) = TestHost.WithSession();
        host.Emit(Ev.Handler("App.Counter", "Increment", 0, 5, 1));
        host.Emit(Ev.Batch(5, 2, 3, 1));
        host.Emit(Ev.Http("https://x.test/api/items/1?q=secret", 1, 50, 1));
        host.Emit(new CircuitEvent { CircuitId = "real-circuit-id", Change = CircuitChange.Opened, Timestamp = Ev.T(0) });
        host.Emit(new ComponentChildrenEvent { ComponentType = "App.Counter", ComponentInstanceId = 1, ChildInstanceIds = [2], ChildTypes = ["App.Child"], Timestamp = Ev.T(1) });
        host.Emit(Ev.Runtime(0, 1000, 1024, heap: 4096));
        session.AddBookmark("checkpoint");
        return (host, session);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundTripsNativeFormat(bool compressed)
    {
        var (host, session) = SessionWithData();
        var report = host.Diagnostics.Analyze(session);
        var document = TraceDocumentBuilder.Create(session, host.Options, report, host.GetHealth(), "test");
        IProfilerExporter exporter = compressed ? new CompressedTraceExporter() : new JsonTraceExporter();
        using var stream = new MemoryStream();
        await exporter.ExportAsync(document, stream);
        stream.Position = 0;
        if (compressed)
        {
            Assert.Equal(0x1f, stream.GetBuffer()[0]);
        }

        var imported = await TraceImporter.ReadAsync(stream);
        Assert.Equal(document.Events.Count, imported.Events.Count);
        Assert.Equal(document.Events.Select(e => e.GetType()), imported.Events.Select(e => e.GetType()));
        Assert.Equal(document.Events.Select(e => e.Id), imported.Events.Select(e => e.Id));
        Assert.Equal(document.Clock.Frequency, imported.Clock.Frequency);
        Assert.Equal("test", imported.Metadata.Source);
        var http = imported.Events.OfType<HttpRequestEvent>().Single();
        Assert.DoesNotContain("secret", http.Url, StringComparison.Ordinal);

        var importedSession = host.AddImportedSession(imported);
        Assert.True(importedSession.IsImported);
        Assert.Equal(ProfilerSessionState.Imported, importedSession.State);
        Assert.Equal(session.GetSummary().Handlers.Count, importedSession.GetSummary().Handlers.Count);
        Assert.Single(((ProfilerSession)importedSession).Bookmarks);
        Assert.Throws<InvalidOperationException>(importedSession.Pause);
        Assert.Empty(host.Diagnostics.Analyze(importedSession).RuleErrors);
    }

    [Fact]
    public void ExportRedactionPseudonymizesCircuitsWithoutTouchingLiveEvents()
    {
        var (host, session) = SessionWithData();
        var document = TraceDocumentBuilder.Create(session, host.Options, exportOptions: new TraceExportOptions { PseudonymizeCircuitIds = true, RemoveTraceContext = true });
        Assert.Equal("circuit-1", document.Events.OfType<CircuitEvent>().Single().CircuitId);
        Assert.Equal("real-circuit-id", session.GetEvents(new EventQuery()).OfType<CircuitEvent>().Single().CircuitId);
        Assert.All(document.Events, e => Assert.Null(e.TraceId));
    }

    [Fact]
    public async Task RejectsOversizedInput()
    {
        var (host, session) = SessionWithData();
        using var stream = new MemoryStream();
        await new JsonTraceExporter().ExportAsync(TraceDocumentBuilder.Create(session, host.Options), stream);
        stream.Position = 0;
        var ex = await Assert.ThrowsAsync<TraceImportException>(() => TraceImporter.ReadAsync(stream, new TraceImportOptions { MaxInputBytes = 100 }));
        Assert.Contains("maximum input size", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsDecompressionBombs()
    {
        using var stream = new MemoryStream();
        await using (var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true))
        {
            // A syntactically valid prefix followed by a huge string that compresses extremely well.
            await gzip.WriteAsync(Encoding.UTF8.GetBytes("{\"format\":\""));
            var filler = Encoding.UTF8.GetBytes(new string('a', 1024 * 1024));
            for (var i = 0; i < 20; i++)
            {
                await gzip.WriteAsync(filler);
            }
        }

        stream.Position = 0;
        var ex = await Assert.ThrowsAsync<TraceImportException>(() => TraceImporter.ReadAsync(stream, new TraceImportOptions { MaxDecompressedBytes = 1024 * 1024 }));
        Assert.Contains("decompressed", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"format":"other","formatVersion":1,"eventSchemaVersion":1,"clock":{"frequency":1}}""", "Unknown trace format")]
    [InlineData("""{"format":"blazor-profiler-trace","formatVersion":99,"eventSchemaVersion":1,"clock":{"frequency":1}}""", "format version")]
    [InlineData("""{"format":"blazor-profiler-trace","formatVersion":1,"eventSchemaVersion":1,"clock":{"frequency":0}}""", "clock")]
    [InlineData("""{"format":"blazor-profiler-trace","formatVersion":1,"eventSchemaVersion":1,"clock":{"frequency":1},"events":[{"type":"System.Diagnostics.Process","id":1}]}""", "not a valid")]
    [InlineData("""{"format":"blazor-profiler-trace","formatVersion":1,"eventSchemaVersion":1,"clock":{"frequency":1},"events":[{"type":"marker","id":1,"duration":-5}]}""", "negative duration")]
    [InlineData("""{"format":"blazor-profiler-trace","formatVersion":1,"eventSchemaVersion":1,"clock":{"frequency":1},"events":[{"type":"marker","id":1,"sampleWeight":0}]}""", "sample weight")]
    [InlineData("""not json""", "not a valid")]
    public async Task RejectsInvalidTraces(string json, string expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var ex = await Assert.ThrowsAsync<TraceImportException>(() => TraceImporter.ReadAsync(stream));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsTooManyEventsAndTruncatesLongStrings()
    {
        var events = string.Join(",", Enumerable.Range(1, 5).Select(i => $$"""{"type":"marker","id":{{i}},"name":"{{new string('x', 5000)}}"}"""));
        var json = $$"""{"format":"blazor-profiler-trace","formatVersion":1,"eventSchemaVersion":1,"clock":{"frequency":1000},"events":[{{events}}]}""";
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
        {
            await Assert.ThrowsAsync<TraceImportException>(() => TraceImporter.ReadAsync(stream, new TraceImportOptions { MaxEvents = 3 }));
        }

        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
        {
            var document = await TraceImporter.ReadAsync(stream);
            Assert.All(document.Events.OfType<MarkerEvent>(), m => Assert.Equal(256, m.Name!.Length));
        }
    }

    [Fact]
    public async Task ChromeExportProducesValidTraceEvents()
    {
        var (host, session) = SessionWithData();
        using var stream = new MemoryStream();
        await new ChromeTraceExporter().ExportAsync(TraceDocumentBuilder.Create(session, host.Options, source: "wasm"), stream);
        using var json = JsonDocument.Parse(stream.ToArray());
        var traceEvents = json.RootElement.GetProperty("traceEvents").EnumerateArray().ToList();
        Assert.Contains(traceEvents, e => e.GetProperty("ph").GetString() == "X");
        Assert.Contains(traceEvents, e => e.GetProperty("ph").GetString() == "C");
        Assert.Contains(traceEvents, e => e.GetProperty("ph").GetString() == "M");
        Assert.All(traceEvents.Where(e => e.TryGetProperty("ts", out _)), e => Assert.True(e.GetProperty("ts").GetDouble() >= 0));
    }
}
