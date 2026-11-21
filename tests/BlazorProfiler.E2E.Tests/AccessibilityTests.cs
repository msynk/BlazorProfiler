using System.Text;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace BlazorProfiler.E2E.Tests;

/// <summary>
/// Audits the dashboard with axe-core against WCAG 2.1 A and AA: every view with recorded data, the event drawer, an open
/// finding, and the in-page toolbar, in light and dark color schemes. An automated audit finds about half of the issues a
/// screen-reader user meets (names, roles, states, contrast, structure); it does not replace testing with a screen reader.
/// </summary>
public sealed class AccessibilityTests(SampleApp app, ITestOutputHelper output) : IClassFixture<SampleApp>
{
    private static readonly string[] Views =
    [
        "overview", "interactions", "components", "timeline", "responsiveness", "network", "database", "interop", "runtime",
        "circuits", "diagnostics", "sessions", "health",
    ];

    private static readonly AxeRunOptions Wcag21AA = new()
    {
        RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"] },
    };

    [Theory]
    [InlineData(ColorScheme.Light)]
    [InlineData(ColorScheme.Dark)]
    public async Task DashboardMeetsWcag21AA(ColorScheme scheme)
    {
        var session = await app.StartSessionAsync($"accessibility-{scheme}");
        await RecordActivityAsync();

        await using var context = await app.Browser.NewContextAsync(new() { ColorScheme = scheme, ViewportSize = new() { Width = 1400, Height = 900 } });
        var dashboard = await context.NewPageAsync();
        await dashboard.GotoAsync(app.BaseUrl + "_blazor-profiler/");
        await dashboard.WaitForSelectorAsync(".tile", new() { Timeout = 20_000 });
        await dashboard.SelectOptionAsync("#session-select", session);
        await Waits.UntilAsync(async () => (await app.FindingsAsync(session)).Count > 0, 30_000, "No findings were recorded.");

        var report = new StringBuilder();
        foreach (var view in Views)
        {
            await dashboard.ClickAsync($"a[data-view={view}]");
            await dashboard.WaitForSelectorAsync($"#view:not(:has(.loading))", new() { Timeout = 20_000 });
            await dashboard.WaitForTimeoutAsync(300);
            await AuditAsync(dashboard, view, report);
        }

        // An open finding with its actions, and the drawer with an event's details.
        await dashboard.ClickAsync("a[data-view=diagnostics]");
        await dashboard.Locator("details.finding summary").First.ClickAsync();
        await AuditAsync(dashboard, "diagnostics (finding open)", report);

        await dashboard.ClickAsync("a[data-view=interactions]");
        await dashboard.WaitForSelectorAsync("table[aria-label=Operations] tbody tr", new() { Timeout = 20_000 });
        await dashboard.ClickAsync("table[aria-label=Operations] tbody tr");
        await dashboard.WaitForSelectorAsync("#drawer .waterfall", new() { State = WaitForSelectorState.Visible });
        await AuditAsync(dashboard, "interactions (drawer open)", report);

        // The in-page toolbar is injected into the app's own page.
        var page = await context.NewPageAsync();
        await page.GotoAsync(app.BaseUrl + "server/render-storm");
        await page.WaitForSelectorAsync("[data-blazor-profiler=toolbar]", new() { State = WaitForSelectorState.Attached, Timeout = 30_000 });
        await page.WaitForTimeoutAsync(1_000);
        await AuditAsync(page, "toolbar", report, ToolbarContext);

        output.WriteLine(report.Length == 0 ? "No violations." : report.ToString());
        Assert.True(report.Length == 0, report.ToString());
    }

    private const string ToolbarContext = "[data-blazor-profiler=toolbar]";

    private async Task RecordActivityAsync()
    {
        var page = await app.Browser.NewPageAsync();
        await page.GotoAsync(app.BaseUrl + "server/requests");
        await EndToEndTests.ClickWhenInteractiveAsync(page, "#reload", "Reloaded", "#requests-status");
        await page.GotoAsync(app.BaseUrl + "server/slow-handler");
        await EndToEndTests.ClickWhenInteractiveAsync(page, "#block", "Computed", "#slow-status");
        await page.CloseAsync();
    }

    private static async Task AuditAsync(IPage page, string name, StringBuilder report, string? selector = null)
    {
        var results = selector is null
            ? await page.RunAxe(Wcag21AA)
            : await page.RunAxe(new AxeRunContext { Include = [new AxeSelector(selector)] }, Wcag21AA);
        foreach (var violation in results.Violations)
        {
            report.Append(name).Append(": ").Append(violation.Id).Append(" (").Append(violation.Impact).Append(") ").AppendLine(violation.Help);
            foreach (var node in violation.Nodes.Take(5))
            {
                var html = node.Html.Length > 160 ? node.Html[..160] + "…" : node.Html;
                var messages = string.Join("; ", (node.Any ?? []).Concat(node.All ?? []).Concat(node.None ?? []).Select(c => c.Message).Distinct());
                report.Append("    ").Append(node.Target).Append(' ').Append(html).Append(" — ").AppendLine(messages);
            }
        }
    }
}
