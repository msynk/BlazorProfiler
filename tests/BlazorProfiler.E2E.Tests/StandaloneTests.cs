using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.Playwright;

namespace BlazorProfiler.E2E.Tests;

/// <summary>The standalone Blazor WebAssembly sample (no server project), served by the development server.</summary>
public sealed class StandaloneApp : IAsyncLifetime
{
    private Process? _process;

    public string BaseUrl { get; private set; } = "";

    public IPlaywright Playwright { get; private set; } = default!;

    public IBrowser Browser { get; private set; } = default!;

    public async ValueTask InitializeAsync()
    {
        var assembly = typeof(StandaloneApp).Assembly;
        string Metadata(string key) => assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;
        // No trailing separator: a backslash before the closing quote would escape it on Windows.
        var project = Path.GetFullPath(Metadata("StandaloneDirectory")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        var start = new ProcessStartInfo("dotnet", $"run --project \"{project}\" -c {Metadata("Configuration")} --no-build --no-launch-profile -- --urls {BaseUrl.TrimEnd('/')}")
        {
            WorkingDirectory = project,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        _process = Process.Start(start)!;
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(BaseUrl + "_framework/blazor.webassembly.js");
                if (response.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch (HttpRequestException)
            {
            }

            if (DateTime.UtcNow > deadline || _process.HasExited)
            {
                throw new InvalidOperationException("The standalone sample did not start.");
            }

            await Task.Delay(250);
        }

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        Playwright?.Dispose();
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

public sealed class StandaloneTests(StandaloneApp app) : IClassFixture<StandaloneApp>
{
    private static async Task<(string SessionId, List<JsonElement> Events)> EventsAsync(IPage page)
    {
        var info = await EndToEndTests.BridgeAsync(page, new { op = "info" });
        var sessionId = info.GetProperty("info").GetProperty("sessions")[0].Str("id");
        var all = new List<JsonElement>();
        long after = 0;
        while (true)
        {
            var result = (await EndToEndTests.BridgeAsync(page, new { op = "events", sessionId, after, max = 10_000 })).GetProperty("page");
            all.AddRange(result.GetProperty("events").EnumerateArray());
            after = result.GetProperty("cursor").GetInt64();
            if (!result.GetProperty("hasMore").GetBoolean())
            {
                return (sessionId, all);
            }
        }
    }

    private static async Task<List<JsonElement>> FindingsAsync(IPage page, string sessionId) =>
        [.. (await EndToEndTests.BridgeAsync(page, new { op = "findings", sessionId })).GetProperty("report").GetProperty("findings").EnumerateArray()];

    private async Task<IPage> OpenAsync()
    {
        var page = await app.Browser.NewPageAsync();
        await page.GotoAsync(app.BaseUrl);
        await EndToEndTests.ClickWhenInteractiveAsync(page, "#count", "Clicked 1", "#count");
        return page;
    }

    [Fact]
    public async Task InteractiveRouterNavigationsDriveTimerAndLeakDiagnostics()
    {
        var page = await OpenAsync();
        for (var i = 0; i < 7; i++)
        {
            await page.ClickAsync("#nav-leaky");
            await page.WaitForSelectorAsync("#leaky-status");
            await Task.Delay(1_200);
            await page.ClickAsync("#nav-home");
            await page.WaitForSelectorAsync("#count");
            await Task.Delay(1_200);
        }

        // Unlike enhanced navigation in a Blazor Web App, the interactive router reports every navigation.
        var (sessionId, events) = await EventsAsync(page);
        Assert.True(events.Count(e => e.Str("type") == "navigation") >= 14, "Navigations were not recorded.");
        // Only every other navigation (to the Leaky page) adds a timer, so the growth is found per Leaky instance (listed
        // among the candidates; types created with it on every visit correlate equally).
        var timerFinding = string.Empty;
        await Waits.UntilAsync(async () =>
        {
            var findings = await FindingsAsync(page, sessionId);
            var bp5007 = findings.Where(f => f.Str("ruleId") == "BP5007").ToList();
            timerFinding = string.Join(" | ", bp5007.Select(f => f.Str("title") + " " + f.GetProperty("evidence").GetRawText()));
            return bp5007.Any(f => f.GetProperty("evidence").GetRawText().Contains("Leaky:", StringComparison.Ordinal));
        }, 15_000, "BP5007 did not name the Leaky page.");
        Assert.Contains("Leaky", timerFinding, StringComparison.Ordinal);

        // The leak check reports the visited Leaky pages and the event subscription that holds them.
        var result = await EndToEndTests.BridgeAsync(page, new { op = "command", name = "check-leaks" });
        Assert.True(int.Parse(result.GetProperty("command").GetProperty("values").Str("retained"), System.Globalization.CultureInfo.InvariantCulture) >= 2);
        await Waits.UntilAsync(async () =>
        {
            var (_, latest) = await EventsAsync(page);
            return latest.Any(e => e.Str("type") == "componentRetained"
                && e.Str("componentType").EndsWith(".Leaky", StringComparison.Ordinal)
                && e.TryGetProperty("retainedBy", out var by)
                && by.EnumerateArray().Any(r => r.GetString()!.StartsWith("AppState.Changed → Leaky.OnChanged", StringComparison.Ordinal)));
        }, 20_000, "The retained Leaky pages or their retainer were not reported.");
        var leak = (await FindingsAsync(page, sessionId)).First(f => f.Str("ruleId") == "BP5004" && f.Str("componentType").EndsWith(".Leaky", StringComparison.Ordinal));
        Assert.Equal("High", leak.Str("confidence"));

        // Grouped across sessions by signature: the finding names the signature of the group.
        var leaks = (await EndToEndTests.BridgeAsync(page, new { op = "leaks" })).GetProperty("leaks");
        var group = leaks.GetProperty("groups").EnumerateArray().First(g => g.Str("componentType").EndsWith(".Leaky", StringComparison.Ordinal));
        Assert.True(group.GetProperty("instances").GetInt32() >= 2);
        Assert.Contains(group.GetProperty("retainers").EnumerateArray(), r => r.GetString()!.StartsWith("AppState.Changed → Leaky.OnChanged", StringComparison.Ordinal));
        Assert.Contains(leak.GetProperty("evidence").EnumerateArray(), e => e.Str("label").StartsWith("Leak signature", StringComparison.Ordinal) && e.Str("value").Contains(group.Str("signature"), StringComparison.Ordinal));
        await page.CloseAsync();
    }

    [Fact]
    public async Task PerRowRequestsAreAttributedAndReportedAsNPlusOne()
    {
        var page = await OpenAsync();
        await page.ClickAsync("#nav-products");
        await page.WaitForSelectorAsync("#products-status:has-text('Loaded')");

        await Waits.UntilAsync(async () =>
        {
            var (sessionId, events) = await EventsAsync(page);
            var details = events.Where(e => e.Str("type") == "http" && e.Str("originComponentType").EndsWith("ProductDetails", StringComparison.Ordinal)).ToList();
            if (details.Count < 12)
            {
                return false;
            }

            var findings = await FindingsAsync(page, sessionId);
            return findings.Any(f => f.Str("ruleId") is "BP3002" or "BP3006");
        }, 30_000, "The per-row requests were not attributed or not reported.");
        await page.CloseAsync();
    }

    [Fact]
    public async Task DashboardOpensThroughTheBridge()
    {
        // The dashboard talks to the app tab through a BroadcastChannel, so both pages share one browser context.
        await using var context = await app.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(app.BaseUrl);
        await EndToEndTests.ClickWhenInteractiveAsync(page, "#count", "Clicked 1", "#count");
        var dashboard = await context.NewPageAsync();
        await dashboard.GotoAsync(app.BaseUrl + "_content/BlazorProfiler/dashboard/index.html?source=bridge");
        await dashboard.WaitForSelectorAsync(".tile", new() { Timeout = 30_000 });
        await dashboard.ClickAsync("a[data-view=interactions]");
        await dashboard.WaitForSelectorAsync("table[aria-label=Operations] tbody tr", new() { Timeout = 20_000 });
        await dashboard.CloseAsync();
        await page.CloseAsync();
    }
}
