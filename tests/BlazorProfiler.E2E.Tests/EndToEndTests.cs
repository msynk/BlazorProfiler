using System.Net;
using System.Text.Json;
using Microsoft.Playwright;

namespace BlazorProfiler.E2E.Tests;

public sealed class EndToEndTests(SampleApp app) : IClassFixture<SampleApp>
{
    private const string BridgeCall = """
        async ({ payload }) => {
          const channel = new BroadcastChannel('blazor-profiler');
          try {
            const clientId = await new Promise((resolve, reject) => {
              channel.onmessage = e => { if (e.data && e.data.type === 'announce') resolve(e.data.clientId); };
              channel.postMessage({ type: 'discover' });
              setTimeout(() => reject(new Error('No profiler client answered.')), 5000);
            });
            const requestId = Math.random();
            return await new Promise((resolve, reject) => {
              channel.onmessage = e => { if (e.data && e.data.type === 'response' && e.data.requestId === requestId) resolve(e.data.payload); };
              channel.postMessage({ type: 'request', clientId, requestId, payload });
              setTimeout(() => reject(new Error('Bridge request timed out.')), 20000);
            });
          } finally {
            channel.close();
          }
        }
        """;

    private async Task<IPage> OpenAsync(string path)
    {
        var page = await app.Browser.NewPageAsync();
        await page.GotoAsync(app.BaseUrl + path);
        return page;
    }

    internal static async Task<JsonElement> BridgeAsync(IPage page, object payload)
    {
        var json = await page.EvaluateAsync<string>(BridgeCall, new { payload });
        var response = JsonDocument.Parse(json).RootElement;
        Assert.True(response.GetProperty("ok").GetBoolean(), response.Str("error"));
        return response;
    }

    internal static async Task ClickWhenInteractiveAsync(IPage page, string selector, string expectedText, string resultSelector)
    {
        // Interactive rendering starts after the page loads: click, wait for the result, and retry only if the click was
        // made before the component became interactive.
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            await page.ClickAsync(selector);
            var attemptEnd = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < attemptEnd)
            {
                if ((await page.InnerTextAsync(resultSelector)).Contains(expectedText, StringComparison.Ordinal))
                {
                    return;
                }

                await Task.Delay(150);
            }
        }

        throw new TimeoutException($"'{selector}' did not produce '{expectedText}'.");
    }

    [Fact]
    public async Task ServerRenderStormIsDiagnosedWithCircuitCorrelation()
    {
        var session = await app.StartSessionAsync("render storm");
        var page = await OpenAsync("server/render-storm");
        await ClickWhenInteractiveAsync(page, "#storm-toggle", "Stop", "#storm-toggle");
        await Task.Delay(3500);
        await page.ClickAsync("#storm-toggle");

        await Waits.UntilAsync(async () => (await app.FindingsAsync(session)).Any(f => f.Str("ruleId") == "BP1001"), 20_000, "BP1001 was not reported.");
        var findings = await app.FindingsAsync(session);
        Assert.Contains(findings, f => f.Str("ruleId") == "BP1003" && f.Str("componentType").EndsWith("StormRow", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Str("ruleId") == "BP1004" && f.Str("componentType").EndsWith("RenderStorm", StringComparison.Ordinal));

        var events = await app.EventsAsync(session);
        Assert.Contains(events, e => e.Str("type") == "renderBatch" && e.Str("circuitId").Length > 0 && e.GetProperty("includesClientRoundTrip").GetBoolean());
        var handler = events.First(e => e.Str("type") == "eventHandler" && e.Str("methodName") == "Toggle");
        var operation = handler.GetProperty("operationId").GetInt64();
        Assert.True(operation > 0);
        Assert.True(handler.GetProperty("synchronousDuration").GetInt64() > 0);

        // In .NET 10, browser events reach the circuit as JS-to-.NET interop calls; the hub filter names the activity.
        Assert.Contains(events, e => e.Str("type") == "circuitActivity" && e.Str("activityName") == "BeginInvokeDotNetFromJS" && e.GetProperty("operationId").GetInt64() == operation);
        Assert.Contains(events, e => e.Str("type") == "circuitActivity" && e.Str("activityName") == "OnRenderCompleted");
        Assert.Contains(events, e => e.Str("type") == "componentChildren");
        await page.CloseAsync();
    }

    [Fact]
    public async Task ServerRequestPatternsAreDetectedAndAttributedToComponents()
    {
        var session = await app.StartSessionAsync("requests");
        var page = await OpenAsync("server/requests");
        await ClickWhenInteractiveAsync(page, "#reload", "Reloaded", "#requests-status");

        await Waits.UntilAsync(async () =>
        {
            var ids = (await app.FindingsAsync(session)).Select(f => f.Str("ruleId")).ToHashSet();
            return ids.IsSupersetOf(["BP3002", "BP3006", "BP2003"]);
        }, 20_000, "Request findings were not reported.");

        var events = await app.EventsAsync(session);
        var detail = events.First(e => e.Str("type") == "http" && e.Str("endpoint").EndsWith("/api/products/{id}", StringComparison.Ordinal));
        Assert.EndsWith("ProductRow", detail.Str("originComponentType"), StringComparison.Ordinal);
        Assert.Equal("OnInitializedAsync", detail.Str("originLifecycleMethod"));
        Assert.Contains(await app.FindingsAsync(session), f => f.Str("ruleId") == "BP3003");

        // The page is prerendered: OnInitializedAsync loads the products once without a circuit and again on the circuit.
        Assert.Contains(await app.FindingsAsync(session), f => f.Str("ruleId") == "BP3007" && f.Str("componentType").EndsWith("RequestPatterns", StringComparison.Ordinal));
        await page.CloseAsync();
    }

    [Fact]
    public async Task ServerSlowHandlerReportsSynchronousBlocking()
    {
        var session = await app.StartSessionAsync("slow handler");
        var page = await OpenAsync("server/slow-handler");
        await ClickWhenInteractiveAsync(page, "#block", "Computed", "#slow-status");

        await Waits.UntilAsync(async () => (await app.FindingsAsync(session)).Any(f => f.Str("ruleId") == "BP2001"), 20_000);
        var findings = await app.FindingsAsync(session);
        var slow = findings.First(f => f.Str("ruleId") == "BP2001" && f.Str("subject").Contains("SlowHandler.Block", StringComparison.Ordinal));
        Assert.Equal("High", slow.Str("confidence"));
        Assert.Contains("synchronously", slow.Str("explanation"), StringComparison.Ordinal);
        Assert.Contains(findings, f => f.Str("ruleId") == "BP1005" && f.Str("subject").Contains("SlowSection", StringComparison.Ordinal));
        await page.CloseAsync();
    }

    [Fact]
    public async Task ServerDashboardExplainsFindingsWithTheConfiguredAssistant()
    {
        var session = await app.StartSessionAsync("explain");
        var demo = await OpenAsync("server/flaky");
        await ClickWhenInteractiveAsync(demo, "#flaky", "Attempt 4", "#flaky-status");
        await Waits.UntilAsync(async () => (await app.FindingsAsync(session)).Any(f => f.Str("ruleId") == "BP3005"));

        var info = await app.ApiAsync("/info");
        Assert.True(info.GetProperty("canExplainFindings").GetBoolean());
        Assert.Equal("demo echo assistant", info.Str("findingExplainer"));

        var dashboard = await OpenAsync("_blazor-profiler/");
        await dashboard.WaitForSelectorAsync(".tile", new() { Timeout = 20_000 });
        await dashboard.SelectOptionAsync("#session-select", session);
        await dashboard.ClickAsync("a[data-view=diagnostics]");
        var finding = dashboard.Locator("details.finding", new() { HasText = "BP3005" }).First;
        await finding.Locator("summary").ClickAsync();
        await finding.Locator("button", new() { HasText = "Explain with AI" }).ClickAsync();

        // The answer is shown as text with where it came from; the prompt carries the hosting model and the finding.
        var text = finding.Locator(".explanation-text");
        await text.WaitForAsync(new() { Timeout = 20_000 });
        var answer = await text.InnerTextAsync();
        Assert.Contains("This demo assistant repeats", answer, StringComparison.Ordinal);
        Assert.Contains("Blazor Server", answer, StringComparison.Ordinal);
        Assert.Contains("BP3005", answer, StringComparison.Ordinal);
        Assert.Contains("Generated by demo echo assistant", await finding.InnerTextAsync(), StringComparison.Ordinal);

        // The answer is kept when the findings are rendered again, so it is not asked (and paid) for twice.
        await dashboard.ClickAsync("a[data-view=overview]");
        await dashboard.ClickAsync("a[data-view=diagnostics]");
        finding = dashboard.Locator("details.finding", new() { HasText = "BP3005" }).First;
        await finding.Locator("summary").ClickAsync();
        Assert.Contains("This demo assistant repeats", await finding.Locator(".explanation-text").InnerTextAsync(), StringComparison.Ordinal);
        Assert.Equal(1, await finding.Locator("button", new() { HasText = "Explain again" }).CountAsync());

        // The API requires the CSRF header and a known finding.
        using var missingHeader = await app.Http.PostAsync($"_blazor-profiler/api/sessions/{session}/findings/explain?fingerprint=x", null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, missingHeader.StatusCode);
        using var unknown = new HttpRequestMessage(HttpMethod.Post, $"_blazor-profiler/api/sessions/{session}/findings/explain?fingerprint=unknown");
        unknown.Headers.Add("X-Blazor-Profiler", "1");
        using var notFound = await app.Http.SendAsync(unknown);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, notFound.StatusCode);
        await demo.CloseAsync();
        await dashboard.CloseAsync();
    }

    [Fact]
    public async Task ServerDashboardRendersFindingsAndExportsTraces()
    {
        var session = await app.StartSessionAsync("dashboard");
        var demo = await OpenAsync("server/flaky");
        await ClickWhenInteractiveAsync(demo, "#flaky", "Attempt 4", "#flaky-status");
        await Waits.UntilAsync(async () => (await app.FindingsAsync(session)).Any(f => f.Str("ruleId") == "BP3005"));

        var dashboard = await OpenAsync("_blazor-profiler/");
        await dashboard.WaitForSelectorAsync(".tile", new() { Timeout = 20_000 });
        Assert.Contains("Render batches", await dashboard.InnerTextAsync("#view"), StringComparison.Ordinal);
        await dashboard.ClickAsync("a[data-view=diagnostics]");
        await dashboard.WaitForSelectorAsync(".finding", new() { Timeout = 20_000 });
        Assert.Contains("failed", await dashboard.InnerTextAsync("#view"), StringComparison.Ordinal);
        await dashboard.ClickAsync("a[data-view=timeline]");
        await dashboard.WaitForSelectorAsync(".timeline-wrap canvas");
        await dashboard.ClickAsync("a[data-view=health]");
        await dashboard.WaitForSelectorAsync("text=component-metrics");
        await dashboard.ClickAsync("a[data-view=sessions]");
        await dashboard.WaitForSelectorAsync("text=Leaks across sessions");

        // The panel loads the grouped leaks of every session: a table, or an empty state when nothing was retained.
        await dashboard.WaitForFunctionAsync("() => !!document.querySelector(\"table[aria-label='Leaks across sessions']\") || document.body.innerText.includes('No retained components')");

        using var native = await app.Http.GetAsync($"_blazor-profiler/api/sessions/{session}/export?format=native");
        native.EnsureSuccessStatusCode();
        var bytes = await native.Content.ReadAsByteArrayAsync();
        Assert.Equal(0x1f, bytes[0]);
        using var chrome = await app.Http.GetAsync($"_blazor-profiler/api/sessions/{session}/export?format=chrome");
        Assert.Contains("traceEvents", await chrome.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Re-import the exported trace and confirm it is analyzable.
        using var upload = new ByteArrayContent(bytes);
        upload.Headers.ContentType = new("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Post, "_blazor-profiler/api/import") { Content = upload };
        request.Headers.Add("X-Blazor-Profiler", "1");
        using var imported = await app.Http.SendAsync(request);
        imported.EnsureSuccessStatusCode();
        var importedId = JsonDocument.Parse(await imported.Content.ReadAsStringAsync()).RootElement.Str("id");
        Assert.Contains(await app.FindingsAsync(importedId), f => f.Str("ruleId") == "BP3005");
        await app.ApiAsync($"/sessions/{importedId}", HttpMethod.Delete);
        await demo.CloseAsync();
        await dashboard.CloseAsync();
    }

    [Fact]
    public async Task ServerDashboardDetailsDrawerOpensAndCloses()
    {
        var session = await app.StartSessionAsync("drawer");
        var demo = await OpenAsync("server/requests");
        await ClickWhenInteractiveAsync(demo, "#reload", "Reloaded", "#requests-status");
        await Waits.UntilAsync(async () => (await app.EventsAsync(session)).Any(e => e.Str("type") == "http"), 20_000, "No request was recorded.");

        var dashboard = await OpenAsync("_blazor-profiler/");
        await dashboard.WaitForSelectorAsync(".tile", new() { Timeout = 20_000 });
        Assert.False(await dashboard.IsVisibleAsync("#drawer"));

        // Close button and Escape both hide the details drawer.
        await dashboard.ClickAsync("a[data-view=components]");
        await dashboard.ClickAsync("table[aria-label=Components] tbody tr");
        await dashboard.WaitForSelectorAsync("#drawer", new() { State = WaitForSelectorState.Visible });
        await dashboard.ClickAsync("#drawer-close");
        await dashboard.WaitForSelectorAsync("#drawer", new() { State = WaitForSelectorState.Hidden });
        await dashboard.ClickAsync("table[aria-label=Components] tbody tr");
        await dashboard.WaitForSelectorAsync("#drawer", new() { State = WaitForSelectorState.Visible });
        await dashboard.Keyboard.PressAsync("Escape");
        await dashboard.WaitForSelectorAsync("#drawer", new() { State = WaitForSelectorState.Hidden });

        // Request rows start directly under the list header and open event details.
        await dashboard.ClickAsync("a[data-view=network]");
        var firstRow = await dashboard.WaitForSelectorAsync("[aria-label=Requests] .vrow[data-index='0']");
        var gap = await dashboard.EvaluateAsync<double>("""
            () => {
              const list = document.querySelector('[aria-label=Requests]');
              return list.querySelector('.vrow[data-index="0"]').getBoundingClientRect().top - list.querySelector('.vrow.header').getBoundingClientRect().bottom;
            }
            """);
        Assert.InRange(gap, -1, 1);
        await firstRow!.ClickAsync();
        await dashboard.WaitForSelectorAsync("#drawer", new() { State = WaitForSelectorState.Visible });
        Assert.Contains("http", await dashboard.InnerTextAsync("#drawer-body"), StringComparison.Ordinal);
        await dashboard.ClickAsync("#drawer-close");
        await dashboard.WaitForSelectorAsync("#drawer", new() { State = WaitForSelectorState.Hidden });
        await demo.CloseAsync();
        await dashboard.CloseAsync();
    }

    [Fact]
    public async Task StateChangingRequestsRequireTheProfilerHeader()
    {
        using var response = await app.Http.PostAsync("_blazor-profiler/api/sessions", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WebAssemblyCollectsMetricsAndAnswersTheDashboardBridge()
    {
        var page = await OpenAsync("wasm/requests");
        await ClickWhenInteractiveAsync(page, "#reload", "Reloaded", "#requests-status");

        await Waits.UntilAsync(async () =>
        {
            var info = await BridgeAsync(page, new { op = "info" });
            var id = info.GetProperty("info").GetProperty("sessions")[0].Str("id");
            var report = await BridgeAsync(page, new { op = "findings", sessionId = id });
            return report.GetProperty("report").GetProperty("findings").EnumerateArray().Any(f => f.Str("ruleId") == "BP3002");
        }, 30_000, "BP3002 was not reported in WebAssembly.");

        var infoResponse = await BridgeAsync(page, new { op = "info" });
        var info = infoResponse.GetProperty("info");
        Assert.Equal("webassembly", info.Str("source"));
        var collectors = info.GetProperty("health").GetProperty("collectors").EnumerateArray().ToDictionary(c => c.GetProperty("descriptor").Str("name"), c => c.Str("state"));
        Assert.Equal("Running", collectors["component-metrics"]);
        Assert.Equal("Running", collectors["js-interop"]);

        var sessionId = info.GetProperty("sessions")[0].Str("id");
        var page1 = await BridgeAsync(page, new { op = "events", sessionId, after = 0, max = 10000 });
        var events = page1.GetProperty("page").GetProperty("events").EnumerateArray().ToList();
        Assert.Contains(events, e => e.Str("type") == "renderBatch" && !e.GetProperty("includesClientRoundTrip").GetBoolean());
        Assert.Contains(events, e => e.Str("type") == "parametersSet" && e.Str("source") == "ComponentInstrumentation");
        Assert.Contains(events, e => e.Str("type") == "http" && e.Str("originComponentType").EndsWith("ProductRow", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Str("type") == "componentCreated");
        await page.CloseAsync();
    }

    private static void AssertJavaScriptToDotNetCalls(List<JsonElement> events)
    {
        var calls = events.Where(e => e.Str("type") == "jsInterop" && e.Str("direction") == "JSToDotNet").ToList();
        var moves = calls.Where(c => c.Str("identifier") == "OnPointerMove").ToList();
        Assert.True(moves.Count >= 150, $"Expected 150 OnPointerMove calls, got {moves.Count}.");
        Assert.All(moves, c => Assert.True(c.GetProperty("viaObjectReference").GetBoolean()));
        Assert.All(moves, c => Assert.Equal(2, c.GetProperty("argumentCount").GetInt32()));
        Assert.Contains(calls, c => c.Str("identifier") == "DemoPing (BlazorProfiler.Demo.Client)" && !c.GetProperty("viaObjectReference").GetBoolean());

        // Blazor's own plumbing (event dispatch, root components) and the profiler's own calls are not recorded.
        Assert.DoesNotContain(calls, c => c.Str("identifier") is "DispatchEventAsync" or "AddRootComponent" or "SetRootComponentParameters" or "RemoveRootComponent");
        Assert.DoesNotContain(calls, c => c.Str("identifier").StartsWith("BlazorProfiler.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServerRecordsJavaScriptToDotNetCallsAndReportsFrequentOnes()
    {
        var session = await app.StartSessionAsync("js-to-dotnet");
        var page = await OpenAsync("server/interop");
        await ClickWhenInteractiveAsync(page, "#stream-to-dotnet", "150 calls", "#interop-status");

        await Waits.UntilAsync(async () => (await app.FindingsAsync(session)).Any(f => f.Str("ruleId") == "BP4007"), 30_000, "BP4007 was not reported.");
        AssertJavaScriptToDotNetCalls(await app.EventsAsync(session));
        var finding = (await app.FindingsAsync(session)).First(f => f.Str("ruleId") == "BP4007");
        Assert.Contains("OnPointerMove", finding.Str("summary"), StringComparison.Ordinal);

        // The fix sends the same moves in one call.
        await page.ClickAsync("#stream-batched");
        await page.WaitForSelectorAsync("text=Sent 150 pointer moves to .NET with one call", new() { Timeout = 20_000 });
        await Waits.UntilAsync(async () => (await app.EventsAsync(session)).Any(e => e.Str("type") == "jsInterop" && e.Str("identifier") == "OnPointerMoves"), 20_000, "The batched call was not recorded.");
        await page.CloseAsync();
    }

    [Fact]
    public async Task ServerMeasuresCircuitMessageSizes()
    {
        var session = await app.StartSessionAsync("circuit messages");
        var page = await OpenAsync("server/interop");
        await ClickWhenInteractiveAsync(page, "#batched", "20000 points", "#interop-status");
        await page.ClickAsync("#stream-batched");
        await page.WaitForSelectorAsync("text=Sent 150 pointer moves to .NET with one call", new() { Timeout = 20_000 });

        List<JsonElement> events = [];
        await Waits.UntilAsync(
            async () =>
            {
                events = await app.EventsAsync(session);
                return events.Any(e => e.Str("type") == "jsInterop" && e.Str("identifier") == "plotMany");
            },
            20_000,
            "The large call was not recorded.");

        static long? Bytes(JsonElement e, string property) =>
            e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

        // .NET-to-JavaScript arguments are measured from the JSON the framework sent: 20,000 points of about 20 bytes each.
        var plotMany = events.First(e => e.Str("type") == "jsInterop" && e.Str("identifier") == "plotMany");
        Assert.Equal("Measured", plotMany.Str("payloadBytesQuality"));
        Assert.InRange(Bytes(plotMany, "payloadBytes") ?? 0, 200_000, 1_000_000);

        // Render batches carry the size of the message that delivered them, and inbound messages their received size: the
        // batched pointer moves (150 pairs of numbers) arrive in one message.
        Assert.Contains(events, e => e.Str("type") == "renderBatch" && e.Str("circuitId").Length > 0 && Bytes(e, "transportBytes") > 0);
        Assert.Contains(events, e => e.Str("type") == "circuitActivity" && e.Str("activityName") == "OnRenderCompleted" && Bytes(e, "receivedBytes") > 0);
        Assert.Contains(events, e => e.Str("type") == "circuitActivity" && e.Str("activityName") == "BeginInvokeDotNetFromJS" && Bytes(e, "receivedBytes") > 1_000);

        var summary = await app.ApiAsync($"/sessions/{session}/summary");
        var circuit = summary.GetProperty("circuits").EnumerateArray().Single(c => c.GetProperty("renderBatches").GetProperty("count").GetDouble() > 0);
        Assert.True(circuit.GetProperty("sentBytes").GetInt64() > 0);
        Assert.True(circuit.GetProperty("receivedBytes").GetInt64() > 1_000);
        await page.CloseAsync();
    }

    [Fact]
    public async Task ServerReportsLargeRenderBatchesAndMessagesNearTheHubLimit()
    {
        var session = await app.StartSessionAsync("large circuit messages");
        var page = await OpenAsync("server/large-list");
        await ClickWhenInteractiveAsync(page, "#sort", "(ascending)", "#sort");

        // One input event carries the whole text twice (as the event's value and as the bound field's value): 12,500
        // characters are about 25 KB, above 75% of the hub's 32 KB receive limit.
        await page.FillAsync("#notes", new string('x', 12_500));
        await page.WaitForSelectorAsync("text=12500 characters", new() { Timeout = 20_000 });

        await Waits.UntilAsync(
            async () => (await app.FindingsAsync(session)).Count(f => f.Str("ruleId") == "BP6006") >= 2,
            20_000,
            "BP6006 was not reported for both directions.");
        var findings = (await app.FindingsAsync(session)).Where(f => f.Str("ruleId") == "BP6006").ToList();
        Assert.Contains(findings, f => f.Str("subject").StartsWith("batches|", StringComparison.Ordinal));
        var received = findings.Single(f => f.Str("subject").StartsWith("received|", StringComparison.Ordinal));
        Assert.EndsWith("|BeginInvokeDotNetFromJS", received.Str("subject"), StringComparison.Ordinal);
        Assert.Contains("@bind", received.Str("recommendation"), StringComparison.Ordinal);
        await page.CloseAsync();
    }

    [Fact]
    public async Task WebAssemblyRecordsJavaScriptToDotNetCallsAndReportsFrequentOnes()
    {
        var page = await app.Browser.NewPageAsync();
        await page.GotoAsync(app.BaseUrl + "wasm/interop");
        await ClickWhenInteractiveAsync(page, "#stream-to-dotnet", "150 calls", "#interop-status");

        string? sessionId = null;
        await Waits.UntilAsync(async () =>
        {
            var info = await BridgeAsync(page, new { op = "info" });
            sessionId = info.GetProperty("info").GetProperty("sessions")[0].Str("id");
            var report = await BridgeAsync(page, new { op = "findings", sessionId });
            return report.GetProperty("report").GetProperty("findings").EnumerateArray().Any(f => f.Str("ruleId") == "BP4007");
        }, 30_000, "BP4007 was not reported in WebAssembly.");

        var events = (await BridgeAsync(page, new { op = "events", sessionId, after = 0, max = 10000 })).GetProperty("page").GetProperty("events").EnumerateArray().ToList();
        AssertJavaScriptToDotNetCalls(events);
        await page.CloseAsync();
    }

    private const string StreamedStatus = "Streamed 200000 bytes to JavaScript, 200000 bytes to .NET, 200000 bytes back";

    private static void AssertStreamSizes(List<JsonElement> events)
    {
        static long? Streamed(JsonElement e) =>
            e.TryGetProperty("streamBytes", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

        JsonElement Call(string identifier) => events.First(e => e.Str("type") == "jsInterop" && e.Str("identifier") == identifier);

        // A DotNetStreamReference argument, a JavaScript stream reference passed to .NET, and an IJSStreamReference result.
        Assert.Equal(200_000, Streamed(Call("receiveStream")));
        Assert.Equal(200_000, Streamed(Call("ReceiveStream")));
        Assert.Equal(200_000, Streamed(Call("makeStream")));
        Assert.Null(Streamed(Call("sendStream")));
    }

    [Fact]
    public async Task ServerRecordsInteropStreamSizes()
    {
        var session = await app.StartSessionAsync("streams");
        var page = await OpenAsync("server/interop");
        await ClickWhenInteractiveAsync(page, "#stream-data", StreamedStatus, "#interop-status");

        List<JsonElement> events = [];
        await Waits.UntilAsync(
            async () =>
            {
                events = await app.EventsAsync(session);
                return events.Any(e => e.Str("type") == "jsInterop" && e.Str("identifier") == "ReceiveStream")
                    && events.Any(e => e.Str("type") == "jsInterop" && e.Str("identifier") == "receiveBytes");
            },
            20_000,
            "The streaming calls were not recorded.");
        AssertStreamSizes(events);

        // A byte[] argument is sent in a message of its own before the call; it counts in the measured size.
        var bytes = events.Single(e => e.Str("type") == "jsInterop" && e.Str("identifier") == "receiveBytes");
        Assert.Equal("Measured", bytes.Str("payloadBytesQuality"));
        Assert.InRange(bytes.GetProperty("payloadBytes").GetInt64(), 50_000, 50_200);

        // The framework sends streams from JavaScript in chunks below the hub's receive limit: streaming is the fix for large
        // messages, not a large message itself.
        Assert.DoesNotContain(await app.FindingsAsync(session), f => f.Str("ruleId") == "BP6006");
        await page.CloseAsync();
    }

    [Fact]
    public async Task WebAssemblyRecordsInteropStreamSizes()
    {
        var page = await app.Browser.NewPageAsync();
        await page.GotoAsync(app.BaseUrl + "wasm/interop");
        await ClickWhenInteractiveAsync(page, "#stream-data", StreamedStatus, "#interop-status");

        List<JsonElement> events = [];
        await Waits.UntilAsync(async () =>
        {
            var info = await BridgeAsync(page, new { op = "info" });
            var sessionId = info.GetProperty("info").GetProperty("sessions")[0].Str("id");
            events = [.. (await BridgeAsync(page, new { op = "events", sessionId, after = 0, max = 10000 })).GetProperty("page").GetProperty("events").EnumerateArray()];
            return events.Any(e => e.Str("type") == "jsInterop" && e.Str("identifier") == "ReceiveStream")
                && events.Any(e => e.Str("type") == "jsInterop" && e.Str("identifier") == "makeStream");
        }, 30_000, "The streaming calls were not recorded in WebAssembly.");
        AssertStreamSizes(events);
        await page.CloseAsync();
    }

    [Fact]
    public async Task WebAssemblyDashboardShowsInteropFindingsThroughTheBridge()
    {
        var context = await app.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(app.BaseUrl + "wasm/interop");
        await ClickWhenInteractiveAsync(page, "#chatter", "120 calls", "#interop-status");

        var dashboard = await context.NewPageAsync();
        await dashboard.GotoAsync(app.BaseUrl + "_content/BlazorProfiler/dashboard/index.html?source=bridge");
        await dashboard.WaitForSelectorAsync("#source-picker select", new() { Timeout = 20_000 });
        Assert.Contains("webassembly", await dashboard.InnerTextAsync("#source-picker"), StringComparison.Ordinal);
        await dashboard.ClickAsync("a[data-view=interop]");
        await dashboard.WaitForSelectorAsync("text=plot", new() { Timeout = 20_000 });
        await dashboard.ClickAsync("a[data-view=diagnostics]");
        await Waits.UntilAsync(async () =>
        {
            await dashboard.ClickAsync("a[data-view=overview]");
            await dashboard.ClickAsync("a[data-view=diagnostics]");
            return (await dashboard.InnerTextAsync("#view")).Contains("BP4002", StringComparison.Ordinal);
        }, 30_000, "BP4002 was not shown in the dashboard.");
        await context.CloseAsync();
    }

    [Fact]
    public async Task ToolbarHighlightingStartupAndNewDashboardViewsWork()
    {
        var session = await app.StartSessionAsync("toolbar");
        var page = await OpenAsync("server/requests");

        // The in-page toolbar (browser-only) appears once the profiler client is interactive.
        await page.WaitForSelectorAsync("[data-blazor-profiler=toolbar]", new() { State = WaitForSelectorState.Attached, Timeout = 30_000 });
        var toolbarText = await page.EvaluateAsync<string>("() => document.querySelector('[data-blazor-profiler=toolbar]').shadowRoot.textContent");
        Assert.Contains("INP", toolbarText, StringComparison.Ordinal);
        Assert.Contains("Highlight updates", toolbarText, StringComparison.Ordinal);

        // Alt+Shift+H toggles update highlighting: an overlay that outlines DOM mutations.
        await page.Keyboard.PressAsync("Alt+Shift+H");
        await page.WaitForSelectorAsync("[data-blazor-profiler=highlight]", new() { State = WaitForSelectorState.Attached });
        await ClickWhenInteractiveAsync(page, "#reload", "Reloaded", "#requests-status");
        await page.Keyboard.PressAsync("Alt+Shift+H");
        await page.WaitForSelectorAsync("[data-blazor-profiler=highlight]", new() { State = WaitForSelectorState.Detached });

        // Startup is reported once per page load, a few seconds after the client starts.
        await Waits.UntilAsync(async () =>
        {
            var events = await app.ApiAsync($"/sessions/{session}/events?after=0&max=10000");
            return events.GetProperty("events").EnumerateArray().Any(e => e.Str("type") == "browserStartup");
        }, 30_000, "No startup was reported.");

        // The browser's conditions are recorded with startup (Chromium reports cores and memory).
        var startup = (await app.ApiAsync($"/sessions/{session}/events?after=0&max=10000")).GetProperty("events").EnumerateArray().First(e => e.Str("type") == "browserStartup");
        Assert.True(startup.GetProperty("hardwareConcurrency").GetInt32() > 0);
        Assert.True(startup.GetProperty("deviceMemoryGb").GetDouble() > 0);

        var dashboard = await OpenAsync("_blazor-profiler/");
        await dashboard.WaitForSelectorAsync(".tile", new() { Timeout = 20_000 });
        await dashboard.SelectOptionAsync("#session-select", session);
        await dashboard.ClickAsync("a[data-view=interactions]");
        await dashboard.WaitForSelectorAsync("table[aria-label=Operations] tbody tr", new() { Timeout = 20_000 });
        await dashboard.ClickAsync("table[aria-label=Operations] tbody tr");
        await dashboard.WaitForSelectorAsync("#drawer .waterfall", new() { State = WaitForSelectorState.Visible });
        await dashboard.ClickAsync("#drawer-close");
        await dashboard.ClickAsync("a[data-view=network]");
        await dashboard.WaitForSelectorAsync("text=Framework files", new() { Timeout = 20_000 });
        await dashboard.ClickAsync("a[data-view=database]");
        await dashboard.WaitForSelectorAsync("h1:has-text('Database')");
        await dashboard.ClickAsync("a[data-view=circuits]");
        await dashboard.WaitForSelectorAsync("text=Latency simulation");
        Assert.DoesNotContain("failed to render", await dashboard.InnerTextAsync("#view"), StringComparison.Ordinal);
        await page.CloseAsync();
        await dashboard.CloseAsync();
    }

    [Theory]
    [InlineData("server/requests")]
    [InlineData("wasm/requests")]
    public async Task DevToolsTracksWriteBlazorEntriesIntoThePerformanceTimeline(string path)
    {
        await app.StartSessionAsync("devtools");
        var page = await app.Browser.NewPageAsync();

        // The module clears its entries right after writing them (DevTools has captured them by then), so record the calls.
        await page.AddInitScriptAsync("""
            window.__bpMeasures = [];
            const original = performance.measure.bind(performance);
            performance.measure = (name, options) => {
              const devtools = options && options.detail && options.detail.devtools;
              if (devtools) { window.__bpMeasures.push({ name, track: devtools.track, group: devtools.trackGroup }); }
              return original(name, options);
            };
            """);
        await page.GotoAsync(app.BaseUrl + path);
        await ClickWhenInteractiveAsync(page, "#reload", "Reloaded", "#requests-status");

        await Waits.UntilAsync(async () =>
        {
            var tracks = await page.EvaluateAsync<string[]>("() => window.__bpMeasures.filter(m => m.group === 'Blazor').map(m => m.track)");
            return tracks.Contains("HTTP (.NET)") && tracks.Contains("Render batches");
        }, 30_000, "No Blazor DevTools track entries were written.");
        await page.CloseAsync();
    }

    [Theory]
    [InlineData("server/requests")]
    [InlineData("wasm/requests")]
    public async Task DevToolsTracksReachTheBrowsersPerformanceTrace(string path)
    {
        // The Performance panel builds custom tracks from the trace Chrome records: user timing events whose detail carries
        // a "devtools" object. Recording a trace over the protocol checks what the panel receives (how it draws the tracks
        // is Chrome's part).
        await app.StartSessionAsync("devtools-trace");
        var page = await app.Browser.NewPageAsync();
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var traceEvents = new List<JsonElement>();
        var complete = new TaskCompletionSource();
        cdp.Event("Tracing.dataCollected").OnEvent += (_, e) =>
        {
            lock (traceEvents)
            {
                traceEvents.AddRange(e!.Value.GetProperty("value").EnumerateArray().Select(v => v.Clone()));
            }
        };
        cdp.Event("Tracing.tracingComplete").OnEvent += (_, _) => complete.TrySetResult();
        await cdp.SendAsync("Tracing.start", new Dictionary<string, object>
        {
            ["transferMode"] = "ReportEvents",
            ["traceConfig"] = new Dictionary<string, object> { ["includedCategories"] = new[] { "blink.user_timing", "devtools.timeline" } },
        });

        await page.GotoAsync(app.BaseUrl + path);
        await ClickWhenInteractiveAsync(page, "#reload", "Reloaded", "#requests-status");
        await page.WaitForTimeoutAsync(2_000);
        await cdp.SendAsync("Tracing.end");
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(30));

        List<(string Name, string Track, string? Group)> entries = [];
        lock (traceEvents)
        {
            foreach (var e in traceEvents.Where(e => e.Str("cat")?.Contains("blink.user_timing", StringComparison.Ordinal) == true))
            {
                if (!e.TryGetProperty("args", out var args) || !args.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                using var parsed = JsonDocument.Parse(detail.GetString()!);
                if (parsed.RootElement.TryGetProperty("devtools", out var devtools) && devtools.Str("track") is { } track)
                {
                    entries.Add((e.Str("name")!, track, devtools.Str("trackGroup")));
                }
            }
        }

        Assert.Contains(entries, e => e.Group == "Blazor" && e.Track == "HTTP (.NET)");
        Assert.Contains(entries, e => e.Group == "Blazor" && e.Track == "Render batches");
        await page.CloseAsync();
    }

    [Fact]
    public async Task WebAssemblyBrowserCollectorReportsUserPerceivedLatency()
    {
        var page = await OpenAsync("wasm/slow-handler");
        await ClickWhenInteractiveAsync(page, "#block", "Computed", "#slow-status");
        await page.ClickAsync("#block");

        await Waits.UntilAsync(async () =>
        {
            var info = await BridgeAsync(page, new { op = "info" });
            var sessionId = info.GetProperty("info").GetProperty("sessions")[0].Str("id");
            var events = await BridgeAsync(page, new { op = "events", sessionId, after = 0, max = 10000 });
            return events.GetProperty("page").GetProperty("events").EnumerateArray().Any(e => e.Str("type") == "browserInteraction" && e.GetProperty("duration").GetInt64() > 0);
        }, 30_000, "No browser interaction was reported.");
        await page.CloseAsync();
    }

    [Fact]
    public async Task ServerRageAndDeadClicksAreDetectedWithTheSlowHandler()
    {
        var session = await app.StartSessionAsync("frustration");
        var page = await OpenAsync("server/frustration");
        await ClickWhenInteractiveAsync(page, "#pay-feedback", "Paid once", "#frustration-status");

        await Task.Delay(3200);

        // Two clicks on a button without a handler, then a burst of clicks on a button whose handler gives no feedback.
        await page.ClickAsync("#help");
        await Task.Delay(1500);
        await page.ClickAsync("#help");
        await Task.Delay(1500);
        for (var i = 0; i < 4; i++)
        {
            await page.ClickAsync("#pay");
            await Task.Delay(80);
        }

        await Waits.UntilAsync(async () => (await app.EventsAsync(session)).Count(e => e.Str("type") == "browserFrustration") >= 3, 30_000, "Frustration signals were not reported.");
        var events = await app.EventsAsync(session);
        Assert.Contains(events, e => e.Str("type") == "browserFrustration" && e.Str("signal") == "RageClick" && e.Str("target") == "button#pay" && e.GetProperty("clickCount").GetInt32() >= 3);
        Assert.Contains(events, e => e.Str("type") == "browserFrustration" && e.Str("signal") == "DeadClick" && e.Str("target") == "button#help");

        // The handler event is recorded when PayAsync completes, three seconds after the first click.
        await Waits.UntilAsync(
            async () => (await app.FindingsAsync(session)).Any(f => f.Str("ruleId") == "BP7007" && f.Str("subject") == "button#pay" && f.Str("summary").Contains("PayAsync", StringComparison.Ordinal)),
            20_000,
            "BP7007 did not name the slow handler.");
        var findings = await app.FindingsAsync(session);
        Assert.Contains(findings, f => f.Str("ruleId") == "BP7007" && f.Str("subject") == "button#help");

        // Steady clicking (one click every 700 ms) and keyboard activation are not rage clicks; each steady click on the
        // handler-less button is a dead click.
        var deadBefore = (await app.EventsAsync(session)).Count(e => e.Str("type") == "browserFrustration" && e.Str("signal") == "DeadClick");
        for (var i = 0; i < 3; i++)
        {
            await page.ClickAsync("#help");
            await Task.Delay(700);
        }

        await page.FocusAsync("#help");
        for (var i = 0; i < 4; i++)
        {
            await page.Keyboard.PressAsync("Enter");
        }

        await Waits.UntilAsync(
            async () => (await app.EventsAsync(session)).Count(e => e.Str("type") == "browserFrustration" && e.Str("signal") == "DeadClick") >= deadBefore + 3,
            20_000,
            "Steady clicks were not reported as dead clicks.");
        await Task.Delay(2500);
        Assert.DoesNotContain(await app.EventsAsync(session), e => e.Str("type") == "browserFrustration" && e.Str("signal") == "RageClick" && e.Str("target") == "button#help");
        await page.CloseAsync();
    }

    [Fact]
    public async Task ServerRowsRecreatedOnReloadAreReportedButIdKeyedRowsAreNot()
    {
        var session = await app.StartSessionAsync("churn");
        var page = await OpenAsync("server/churn");
        await ClickWhenInteractiveAsync(page, "#reload-id-key", "Reloaded (IdKey)", "#reload-status");

        // The first reload switched from object keys to id keys (every row recreated once); later id-keyed reloads reuse rows.
        await app.ApiAsync($"/sessions/{session}/clear", HttpMethod.Post);
        await page.ClickAsync("#reload-id-key");
        await Task.Delay(300);
        await page.ClickAsync("#reload-id-key");
        await Task.Delay(1000);
        Assert.DoesNotContain(await app.FindingsAsync(session), f => f.Str("ruleId") == "BP1008");

        await ClickWhenInteractiveAsync(page, "#reload-object-key", "Reloaded (ObjectKey)", "#reload-status");
        await ClickWhenInteractiveAsync(page, "#reload-object-key", "Reloaded (ObjectKey)", "#reload-status");
        await Waits.UntilAsync(async () => (await app.FindingsAsync(session)).Any(f => f.Str("ruleId") == "BP1008"), 20_000, "BP1008 was not reported.");
        var finding = (await app.FindingsAsync(session)).First(f => f.Str("ruleId") == "BP1008");
        Assert.EndsWith("ReloadRow", finding.Str("componentType"), StringComparison.Ordinal);
        await page.CloseAsync();
    }

    [Fact]
    public async Task WebAssemblyLayoutThrashingIsAttributedToTheScript()
    {
        var page = await OpenAsync("wasm/layout");
        await ClickWhenInteractiveAsync(page, "#thrash", "one by one", "#layout-status");
        await page.ClickAsync("#thrash");

        await Waits.UntilAsync(async () =>
        {
            var info = await BridgeAsync(page, new { op = "info" });
            var sessionId = info.GetProperty("info").GetProperty("sessions")[0].Str("id");
            var report = await BridgeAsync(page, new { op = "findings", sessionId });
            return report.GetProperty("report").GetProperty("findings").EnumerateArray().Any(f => f.Str("ruleId") == "BP7006" && f.Str("title").Contains("thrashLayout", StringComparison.Ordinal));
        }, 30_000, "BP7006 did not attribute the forced layout to thrashLayout.");

        // Three 200 ms tasks on the main thread are three long frames (BP7002), with the script that ran them.
        for (var i = 1; i <= 3; i++)
        {
            await page.ClickAsync("#block-main");
            await page.WaitForSelectorAsync($"#layout-status:has-text('Blocked the main thread {i} time')");
        }

        await Waits.UntilAsync(async () =>
        {
            var info = await BridgeAsync(page, new { op = "info" });
            var sessionId = info.GetProperty("info").GetProperty("sessions")[0].Str("id");
            var report = await BridgeAsync(page, new { op = "findings", sessionId });
            return report.GetProperty("report").GetProperty("findings").EnumerateArray().Any(f => f.Str("ruleId") == "BP7002");
        }, 30_000, "BP7002 did not report the long frames.");
        await page.CloseAsync();
    }

    [Fact]
    public async Task ServerInfrastructureRulesAreReproducedByTheDemos()
    {
        var session = await app.StartSessionAsync("infrastructure");
        async Task<bool> HasFinding(string ruleId) => (await app.FindingsAsync(session)).Any(f => f.Str("ruleId") == ruleId);

        // BP6001 and BP6003: with simulated latency, every browser message (events and render acknowledgements) waits
        // 300 ms before the circuit handles it, as on a slow connection.
        var page = await OpenAsync("server/slow-handler");
        await ClickWhenInteractiveAsync(page, "#block", "Computed", "#slow-status");
        await app.ApiAsync("/commands/simulate-latency?argument=300", HttpMethod.Post);
        try
        {
            var lastClick = DateTime.MinValue;
            await Waits.UntilAsync(
                async () =>
                {
                    if (DateTime.UtcNow - lastClick > TimeSpan.FromSeconds(1.5))
                    {
                        await page.ClickAsync("#block");
                        lastClick = DateTime.UtcNow;
                    }

                    return await HasFinding("BP6001") && await HasFinding("BP6003");
                },
                45_000,
                "Simulated latency did not produce BP6001 and BP6003.");
        }
        finally
        {
            await app.ApiAsync("/commands/simulate-latency?argument=0", HttpMethod.Post);
        }

        await page.CloseAsync();

        // BP6005: thousands of blocking work items queue up on the thread pool for about three seconds.
        page = await OpenAsync("server/thread-pool");
        await ClickWhenInteractiveAsync(page, "#saturate", "Queued", "#pool-status");
        await Waits.UntilAsync(() => HasFinding("BP6005"), 30_000, "Thread-pool saturation was not reported.");
        await page.CloseAsync();

        // BP5001: large buffers make the garbage collector run full collections again and again.
        page = await OpenAsync("server/allocations");
        await ClickWhenInteractiveAsync(page, "#allocate-large", "full collections", "#alloc-status");
        await Waits.UntilAsync(() => HasFinding("BP5001"), 30_000, "Garbage-collection pressure was not reported.");
        await page.CloseAsync();
    }

    [Fact]
    public async Task ServerCircuitReconnectionIsRecordedAndCollectionResumes()
    {
        var session = await app.StartSessionAsync("reconnect");
        await using var context = await app.Browser.NewContextAsync();

        // Keep the page's WebSockets reachable, so the test can drop the circuit's connection at once (network emulation
        // only takes effect when the server's keep-alive times out, about 30 seconds later).
        await context.AddInitScriptAsync("""
            window.WebSocket = new Proxy(window.WebSocket, {
              construct(target, args) {
                const socket = new target(...args);
                (window.__sockets ||= []).push(socket);
                return socket;
              },
            });
            """);
        var page = await context.NewPageAsync();
        await page.GotoAsync(app.BaseUrl + "server/slow-handler");
        await ClickWhenInteractiveAsync(page, "#block", "Computed", "#slow-status");

        // The session is process-wide: circuits of pages from earlier tests can still disconnect during this one, so the
        // page's own circuit is identified by its first handler, and only its events are considered.
        var circuit = string.Empty;
        await Waits.UntilAsync(
            async () =>
            {
                circuit = (await app.EventsAsync(session)).Where(e => e.Str("type") == "eventHandler" && e.Str("methodName") == "Block").Select(e => e.Str("circuitId")).FirstOrDefault() ?? string.Empty;
                return circuit.Length > 0;
            },
            20_000,
            "The first handler was not recorded.");
        int Count(List<JsonElement> events, string change) =>
            events.Count(e => e.Str("type") == "circuit" && e.Str("change") == change && e.Str("circuitId") == circuit);

        // Dropping the connection makes Blazor reconnect to the same circuit (the first ConnectionUp was recorded when the
        // page opened, after the session started). Three drops make an unstable connection (BP6002).
        for (var drop = 1; drop <= 3; drop++)
        {
            await page.EvaluateAsync("() => { for (const s of window.__sockets || []) { s.close(); } }");
            await Waits.UntilAsync(async () => Count(await app.EventsAsync(session), "ConnectionDown") >= drop, 30_000, $"Connection drop {drop} was not recorded.");
            await Waits.UntilAsync(async () => Count(await app.EventsAsync(session), "ConnectionUp") >= drop + 1, 45_000, $"The circuit did not reconnect after drop {drop}.");
        }

        var instability = Assert.Single(await app.FindingsAsync(session), f => f.Str("ruleId") == "BP6002" && f.Str("title").Contains(circuit, StringComparison.Ordinal));
        Assert.Contains("disconnected 3 times", instability.Str("title"), StringComparison.Ordinal);

        // The same circuit keeps being profiled after the reconnection. A click made while the reconnected circuit is still
        // resuming can be lost, so it is repeated until one is recorded.
        var lastClick = DateTime.MinValue;
        await Waits.UntilAsync(
            async () =>
            {
                if (DateTime.UtcNow - lastClick > TimeSpan.FromSeconds(5))
                {
                    await page.ClickAsync("#block");
                    lastClick = DateTime.UtcNow;
                }

                return (await app.EventsAsync(session)).Count(e => e.Str("type") == "eventHandler" && e.Str("methodName") == "Block" && e.Str("circuitId") == circuit) >= 2;
            },
            30_000,
            "Handlers were not recorded after the reconnection.");
        await page.CloseAsync();
    }

    [Fact]
    public async Task DeepCaptureIsOfferedOnlyInDiagnosticsBuilds()
    {
        // The sample is built without <EnableDiagnostics>, so the runtime's capture functions are null: no buttons, and a
        // programmatic capture explains what is missing instead of failing obscurely.
        var page = await OpenAsync("wasm/slow-handler");
        await ClickWhenInteractiveAsync(page, "#block", "Computed", "#slow-status");
        var available = await page.EvaluateAsync<string>("async () => JSON.stringify((await import('./_content/BlazorProfiler/blazor-profiler.js')).availableCaptures())");
        Assert.Equal("[]", available);
        var buttons = await page.EvaluateAsync<string>("() => [...document.querySelector('[data-blazor-profiler=toolbar]').shadowRoot.querySelectorAll('button')].map(b => b.textContent).join('|')");
        Assert.DoesNotContain("GC dump", buttons, StringComparison.Ordinal);
        var error = await page.EvaluateAsync<string>("async () => { try { await (await import('./_content/BlazorProfiler/blazor-profiler.js')).captureDiagnostics('gc'); return 'no error'; } catch (e) { return e.message; } }");
        Assert.Contains("EnableDiagnostics", error, StringComparison.Ordinal);
        await page.CloseAsync();
    }

    [Fact]
    public async Task TimelineComparesTwoWindowsOfOneSession()
    {
        var session = await app.StartSessionAsync("windows");
        var page = await OpenAsync("server/slow-handler");
        await ClickWhenInteractiveAsync(page, "#block", "Computed", "#slow-status");
        await Task.Delay(1500);
        await page.ClickAsync("#block");
        await page.ClickAsync("#block");
        await Waits.UntilAsync(async () => (await app.EventsAsync(session)).Count(e => e.Str("type") == "eventHandler") >= 3, 20_000);

        var dashboard = await OpenAsync("_blazor-profiler/");
        await dashboard.WaitForSelectorAsync(".tile", new() { Timeout = 20_000 });
        await dashboard.SelectOptionAsync("#session-select", session);
        await dashboard.ClickAsync("a[data-view=timeline]");
        var canvas = dashboard.Locator(".timeline-wrap canvas");
        await canvas.WaitForAsync();
        async Task SelectAsync(double from, double to)
        {
            // Clicking the selection panel's buttons can scroll the page, so the canvas is located again each time.
            await canvas.ScrollIntoViewIfNeededAsync();
            var box = (await canvas.BoundingBoxAsync())!;
            // The canvas can be taller than the viewport; press near its bottom, which scrolling into view keeps visible.
            var y = box.Y + box.Height - 30;
            await dashboard.Keyboard.DownAsync("Shift");
            await dashboard.Mouse.MoveAsync((float)(box.X + (box.Width * from)), (float)y);
            await dashboard.Mouse.DownAsync();
            await dashboard.Mouse.MoveAsync((float)(box.X + (box.Width * to)), (float)y, new() { Steps = 5 });
            await dashboard.Mouse.UpAsync();
            await dashboard.Keyboard.UpAsync("Shift");
        }

        await SelectAsync(0.15, 0.5);
        await dashboard.ClickAsync("#window-baseline");
        await SelectAsync(0.5, 0.98);
        await dashboard.ClickAsync("#window-compare");
        await dashboard.WaitForSelectorAsync("table[aria-label='Metric comparison']", new() { Timeout = 10_000 });
        Assert.Contains("Baseline", await dashboard.InnerTextAsync("#view"), StringComparison.Ordinal);
        await dashboard.CloseAsync();
        await page.CloseAsync();
    }

    [Fact]
    public async Task WebAssemblyAsyncResultsAreMeasuredByInteractionContentfulPaint()
    {
        // "Slow JavaScript" awaits three JavaScript promises before it renders its result: the first paint after the click is
        // fast, the result paints about half a second later. The browser attributes that paint to the click because the
        // .NET continuation resumes from a JavaScript promise of the click's task chain. (Continuations resumed by .NET
        // timers, such as Task.Delay, are attributed only sometimes, because the runtime multiplexes its timers onto one
        // JavaScript timer.) Each attempt checks what the browser reported, with an independent observer, and then that
        // the profiler recorded it and BP7008 reported it.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await using var context = await app.Browser.NewContextAsync();
            await context.AddInitScriptAsync("""
                window.__latePaint = 0;
                new PerformanceObserver(l => { for (const e of l.getEntries()) window.__latePaint = Math.max(window.__latePaint, e.duration); })
                  .observe({ type: 'interaction-contentful-paint', buffered: true });
                """);
            var page = await context.NewPageAsync();
            await page.GotoAsync(app.BaseUrl + "wasm/interop");
            await ClickWhenInteractiveAsync(page, "#slow-js", "JavaScript", "#interop-status");
            await Task.Delay(500);
            if (await page.EvaluateAsync<double>("() => window.__latePaint") < 400)
            {
                continue;
            }

            await Waits.UntilAsync(async () =>
            {
                var info = await BridgeAsync(page, new { op = "info" });
                var sessionId = info.GetProperty("info").GetProperty("sessions")[0].Str("id");
                var events = (await BridgeAsync(page, new { op = "events", sessionId, after = 0, max = 10000 })).GetProperty("page").GetProperty("events").EnumerateArray();
                if (!events.Any(e => e.Str("type") == "browserInteraction" && e.TryGetProperty("contentPaintMs", out var cp) && cp.GetDouble() >= 400))
                {
                    return false;
                }

                var report = await BridgeAsync(page, new { op = "findings", sessionId });
                return report.GetProperty("report").GetProperty("findings").EnumerateArray().Any(f => f.Str("ruleId") == "BP7008");
            }, 20_000, "The browser reported a late content paint, but the profiler did not record it or BP7008 did not report it.");
            return;
        }

        Assert.Skip("The browser did not attribute the late paint to the click in any attempt; nothing to verify.");
    }

    [Fact]
    public async Task LatencyHeatmapExplainsTheSlowestOperations()
    {
        var session = await app.StartSessionAsync("heatmap");
        var page = await OpenAsync("server/slow-handler");
        await ClickWhenInteractiveAsync(page, "#block", "Computed", "#slow-status");
        for (var i = 0; i < 5; i++)
        {
            await page.ClickAsync("#block");
            await Task.Delay(800); // spaced out: clicks queued behind a blocked circuit would lengthen each other
        }

        await page.ClickAsync("#slow-load");
        await Waits.UntilAsync(async () => (await app.EventsAsync(session)).Any(e => e.Str("type") == "eventHandler" && e.Str("methodName") == "LoadAsync"), 20_000);

        var dashboard = await OpenAsync("_blazor-profiler/");
        await dashboard.WaitForSelectorAsync(".tile", new() { Timeout = 20_000 });
        await dashboard.SelectOptionAsync("#session-select", session);
        await dashboard.ClickAsync("a[data-view=interactions]");
        await dashboard.ClickAsync("#heat-slowest");
        var table = dashboard.Locator("table[aria-label='Attributes over-represented in the selection']");
        await table.WaitForAsync(new() { Timeout = 10_000 });

        // The slowest operation awaited the slow endpoint; no other operation called it.
        var text = await table.InnerTextAsync();
        Assert.True(text.Contains("api/slow", StringComparison.Ordinal), text + Environment.NewLine + await dashboard.InnerTextAsync("table[aria-label=Operations]"));
        await dashboard.CloseAsync();
        await page.CloseAsync();
    }

    [Fact]
    public async Task WebAssemblyMeasuresRenderAllocations()
    {
        // GC.GetAllocatedBytesForCurrentThread must work in the browser runtime as well, or the allocation columns lie.
        var page = await OpenAsync("wasm/large-list");
        await ClickWhenInteractiveAsync(page, "#sort", "(ascending)", "#sort");
        await Waits.UntilAsync(async () =>
        {
            var info = await BridgeAsync(page, new { op = "info" });
            var sessionId = info.GetProperty("info").GetProperty("sessions")[0].Str("id");
            var events = (await BridgeAsync(page, new { op = "events", sessionId, after = 0, max = 10000 })).GetProperty("page").GetProperty("events").EnumerateArray();
            return events.Any(e => e.Str("type") == "componentRender" && e.TryGetProperty("allocatedBytes", out var bytes) && bytes.GetInt64() > 0);
        }, 30_000, "No render reported allocated bytes in WebAssembly.");

        // The 1,000-row list also makes the DOM excessive (BP7009).
        await Waits.UntilAsync(async () =>
        {
            var info = await BridgeAsync(page, new { op = "info" });
            var sessionId = info.GetProperty("info").GetProperty("sessions")[0].Str("id");
            var report = await BridgeAsync(page, new { op = "findings", sessionId });
            return report.GetProperty("report").GetProperty("findings").EnumerateArray().Any(f => f.Str("ruleId") == "BP7009");
        }, 20_000, "BP7009 was not reported for the 1,000-row list.");
        await page.CloseAsync();
    }

    [Fact]
    public async Task WebAssemblyTimerLeakIsReportedFromActiveTimerGrowth()
    {
        // Each visit to the leaky demo starts a timer that is never disposed; navigating back and forth accumulates them.
        var page = await OpenAsync("wasm/leaky");
        await page.WaitForSelectorAsync("#leaky-status", new() { Timeout = 30_000 });
        await Task.Delay(1500);
        for (var i = 0; i < 7; i++)
        {
            await page.ClickAsync("a[href='wasm/large-list']");
            await page.WaitForSelectorAsync("#sort", new() { Timeout = 10_000 });
            await page.ClickAsync("a[href='wasm/leaky']");
            await page.WaitForSelectorAsync("#leaky-status", new() { Timeout = 10_000 });
            await Task.Delay(1200);
        }

        // This app uses per-page interactivity with enhanced navigation, which reports no navigation events: the rule finds
        // the growth through the instances of the component that owns the timers.
        string? evidence = null;
        await Waits.UntilAsync(async () =>
        {
            var info = await BridgeAsync(page, new { op = "info" });
            var sessionId = info.GetProperty("info").GetProperty("sessions")[0].Str("id");
            var report = await BridgeAsync(page, new { op = "findings", sessionId });
            var finding = report.GetProperty("report").GetProperty("findings").EnumerateArray().FirstOrDefault(f => f.Str("ruleId") == "BP5007");
            evidence = finding.ValueKind == System.Text.Json.JsonValueKind.Undefined ? null : finding.GetProperty("evidence").GetRawText();
            return evidence is not null;
        }, 20_000, "BP5007 was not reported.");
        Assert.Contains("LeakyComponent", evidence, StringComparison.Ordinal);
        await page.CloseAsync();
    }

    [Fact]
    public async Task McpEndpointServesFindingsToAgents()
    {
        var session = await app.StartSessionAsync("mcp");
        var page = await OpenAsync("server/requests");
        await ClickWhenInteractiveAsync(page, "#reload", "Reloaded", "#requests-status");
        await Waits.UntilAsync(async () => (await app.FindingsAsync(session)).Any(f => f.Str("ruleId") == "BP3002"), 20_000);

        static System.Net.Http.StringContent Body(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
        using var init = await app.Http.PostAsync("_blazor-profiler/mcp", Body("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}"""));
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        var call = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_findings","arguments":{"session":"SESSION"}}}""".Replace("SESSION", session, StringComparison.Ordinal);
        using var response = await app.Http.PostAsync("_blazor-profiler/mcp", Body(call));
        var text = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("result").GetProperty("content")[0].Str("text");
        Assert.Contains("BP3002", text, StringComparison.Ordinal);
        await page.CloseAsync();
    }

    [Fact]
    public async Task ResponsivenessViewAndRenderTreeFlameGraphRender()
    {
        var session = await app.StartSessionAsync("responsiveness view");
        var page = await OpenAsync("server/churn");
        await ClickWhenInteractiveAsync(page, "#reload-object-key", "Reloaded (ObjectKey)", "#reload-status");
        await Waits.UntilAsync(async () => (await app.EventsAsync(session)).Any(e => e.Str("type") == "componentRender" && e.Str("componentType").EndsWith("ReloadRow", StringComparison.Ordinal)), 20_000);

        var dashboard = await OpenAsync("_blazor-profiler/");
        await dashboard.WaitForSelectorAsync(".tile", new() { Timeout = 20_000 });
        await dashboard.SelectOptionAsync("#session-select", session);
        await dashboard.ClickAsync("a[data-view=responsiveness]");
        await dashboard.WaitForSelectorAsync("#view h1:has-text('Responsiveness')");

        await dashboard.ClickAsync("a[data-view=interactions]");
        await dashboard.WaitForSelectorAsync("table[aria-label=Operations] tbody tr", new() { Timeout = 20_000 });
        await dashboard.ClickAsync("table[aria-label=Operations] tbody tr:has-text('KeyedReload')");
        await dashboard.WaitForSelectorAsync(".flame .flame-node", new PageWaitForSelectorOptions { Timeout = 15_000 });
        Assert.True(await dashboard.Locator(".flame .flame-node").CountAsync() > 1);
        await dashboard.CloseAsync();
        await page.CloseAsync();
    }
}

public sealed class ProductionTests(ProductionSampleApp app) : IClassFixture<ProductionSampleApp>
{
    [Fact]
    public async Task ProfilerEndpointsAreNotExposedOutsideDevelopment()
    {
        using var info = await app.Http.GetAsync("_blazor-profiler/api/info");
        Assert.Equal(HttpStatusCode.NotFound, info.StatusCode);
        using var dashboard = await app.Http.GetAsync("_blazor-profiler/");
        Assert.Equal(HttpStatusCode.NotFound, dashboard.StatusCode);
    }
}
