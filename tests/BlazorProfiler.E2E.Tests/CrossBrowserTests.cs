using Microsoft.Playwright;

namespace BlazorProfiler.E2E.Tests;

/// <summary>
/// The browser module, the dashboard bridge, and server collection in Firefox and WebKit. Chromium-only APIs (Long Animation
/// Frames) are feature-detected, so the rest of the profiler must work there and must never break the page.
/// </summary>
public sealed class CrossBrowserTests(SampleApp app) : IClassFixture<SampleApp>
{
    private async Task<IBrowser> LaunchAsync(string name)
    {
        var type = name switch
        {
            "firefox" => app.Playwright.Firefox,
            "webkit" => app.Playwright.Webkit,
            _ => app.Playwright.Chromium,
        };
        return await type.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    private static List<string> CollectErrors(IPage page)
    {
        var errors = new List<string>();
        page.PageError += (_, error) =>
        {
            lock (errors)
            {
                errors.Add(error);
            }
        };
        return errors;
    }

    [Theory]
    [InlineData("firefox")]
    [InlineData("webkit")]
    public async Task WebAssemblyCollectsEventsAndAnswersTheBridge(string browserName)
    {
        await using var browser = await LaunchAsync(browserName);
        var page = await browser.NewPageAsync();
        var errors = CollectErrors(page);
        await page.GotoAsync(app.BaseUrl + "wasm/slow-handler");
        await EndToEndTests.ClickWhenInteractiveAsync(page, "#block", "Computed", "#slow-status");

        await Waits.UntilAsync(async () =>
        {
            var info = await EndToEndTests.BridgeAsync(page, new { op = "info" });
            var sessionId = info.GetProperty("info").GetProperty("sessions")[0].Str("id");
            var events = (await EndToEndTests.BridgeAsync(page, new { op = "events", sessionId, after = 0, max = 10000 }))
                .GetProperty("page").GetProperty("events").EnumerateArray().ToList();
            return events.Any(e => e.Str("type") == "eventHandler" && e.Str("methodName") == "Block")
                && events.Any(e => e.Str("type") == "componentRender")
                && events.Any(e => e.Str("type") == "browserStartup");
        }, 60_000, $"{browserName}: the WebAssembly profiler did not report handlers, renders, and startup.");

        // Frustration detection runs in every browser.
        await page.ClickAsync("#block");
        await page.ClickAsync("#block");
        await page.ClickAsync("#block");
        await Waits.UntilAsync(async () =>
        {
            var info = await EndToEndTests.BridgeAsync(page, new { op = "info" });
            var sessionId = info.GetProperty("info").GetProperty("sessions")[0].Str("id");
            var events = await EndToEndTests.BridgeAsync(page, new { op = "events", sessionId, after = 0, max = 10000 });
            return events.GetProperty("page").GetProperty("events").EnumerateArray().Any(e => e.Str("type") == "browserFrustration" && e.Str("signal") == "RageClick");
        }, 45_000, $"{browserName}: no rage click was reported.");

        Assert.Empty(errors);
        await page.CloseAsync();
    }

    [Theory]
    [InlineData("firefox")]
    [InlineData("webkit")]
    public async Task ServerCollectsCircuitAndBrowserDataAndTheDashboardLoads(string browserName)
    {
        var session = await app.StartSessionAsync($"cross-browser {browserName}");
        await using var browser = await LaunchAsync(browserName);
        var page = await browser.NewPageAsync();
        var errors = CollectErrors(page);
        await page.GotoAsync(app.BaseUrl + "server/slow-handler");
        await EndToEndTests.ClickWhenInteractiveAsync(page, "#block", "Computed", "#slow-status");

        await Waits.UntilAsync(async () =>
        {
            var events = await app.EventsAsync(session);
            return events.Any(e => e.Str("type") == "eventHandler" && e.Str("methodName") == "Block" && e.Str("circuitId").Length > 0)
                && events.Any(e => e.Str("type") == "browserStartup");
        }, 60_000, $"{browserName}: server collection or the browser module failed."); // Firefox and WebKit start slowly under load

        var dashboard = await browser.NewPageAsync();
        var dashboardErrors = CollectErrors(dashboard);
        await dashboard.GotoAsync(app.BaseUrl + "_blazor-profiler/");
        await dashboard.WaitForSelectorAsync(".tile", new() { Timeout = 45_000 });
        await dashboard.SelectOptionAsync("#session-select", session);
        foreach (var view in new[] { "interactions", "responsiveness", "timeline", "diagnostics" })
        {
            await dashboard.ClickAsync($"a[data-view={view}]");
            await dashboard.WaitForSelectorAsync("#view h1", new() { Timeout = 10_000 });
        }

        Assert.Empty(errors);
        Assert.Empty(dashboardErrors);
        await dashboard.CloseAsync();
        await page.CloseAsync();
    }
}
