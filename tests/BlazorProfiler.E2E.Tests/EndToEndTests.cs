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

    private static async Task<JsonElement> BridgeAsync(IPage page, object payload)
    {
        var json = await page.EvaluateAsync<string>(BridgeCall, new { payload });
        var response = JsonDocument.Parse(json).RootElement;
        Assert.True(response.GetProperty("ok").GetBoolean(), response.Str("error"));
        return response;
    }

    private static async Task ClickWhenInteractiveAsync(IPage page, string selector, string expectedText, string resultSelector)
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
