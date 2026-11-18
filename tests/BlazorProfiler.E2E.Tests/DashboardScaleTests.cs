using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace BlazorProfiler.E2E.Tests;

/// <summary>
/// Measures the dashboard with a large imported trace: about 19,000 component instances (near the default limit of tracked
/// instances, with sections of 1,200 children, above the tree's per-level limit) and about 58,000 events. Budgets are
/// several times the times measured on a developer machine, so they catch regressions in complexity (for example a view
/// that became quadratic) rather than noise. The test also checks that the first load of a large session does not reset
/// what the user did meanwhile (an expanded tree, a filter).
/// </summary>
public sealed class DashboardScaleTests(SampleApp app, ITestOutputHelper output) : IClassFixture<SampleApp>
{
    private const int Sections = 16;
    private const int RowsPerSection = 1_200;
    private const int TypeCount = 60;

    [Fact]
    public async Task LargeComponentTreeStaysResponsive()
    {
        var (json, events) = BuildTrace();
        var sessionId = await ImportAsync(json);
        try
        {
            await using var context = await app.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1400, Height = 900 } });

            // Records the longest animation frame of the dashboard (a frame delayed by work on the main thread).
            await context.AddInitScriptAsync("""
                window.__longest = 0;
                new PerformanceObserver(list => { for (const e of list.getEntries()) { window.__longest = Math.max(window.__longest, e.duration); } })
                  .observe({ type: 'long-animation-frame', buffered: true });
                """);
            var page = await context.NewPageAsync();

            // The dashboard loads events in pages over several polls; the last page says there are no more.
            var allLoaded = new TaskCompletionSource();
            page.Response += async (_, response) =>
            {
                if (response.Url.Contains($"/sessions/{sessionId}/events", StringComparison.Ordinal))
                {
                    try
                    {
                        if ((await response.TextAsync()).Contains("\"hasMore\":false", StringComparison.Ordinal))
                        {
                            allLoaded.TrySetResult();
                        }
                    }
                    catch (PlaywrightException)
                    {
                    }
                }
            };
            await page.GotoAsync(app.BaseUrl + "_blazor-profiler/");
            await page.WaitForSelectorAsync(".tile", new() { Timeout = 30_000 });

            var timings = new List<(string Step, long Ms, long Budget)>();
            async Task Step(string name, long budgetMs, Func<Task> action)
            {
                var watch = Stopwatch.StartNew();
                await action();
                timings.Add((name, watch.ElapsedMilliseconds, budgetMs));
            }

            await Step("select session (overview)", 10_000, async () =>
            {
                await page.SelectOptionAsync("#session-select", sessionId);
                await page.WaitForSelectorAsync($"text={events.ToString("N0", CultureInfo.InvariantCulture)} events stored", new() { Timeout = 30_000 });
            });

            await Step("components view", 4_000, async () =>
            {
                await page.ClickAsync("a[data-view=components]");
                await page.WaitForSelectorAsync("[role=tree] [role=treeitem]", new() { Timeout = 30_000 });
            });

            await Step("expand root", 1_500, async () =>
            {
                await page.ClickAsync("[role=tree] > [role=treeitem] > button");
                await page.WaitForSelectorAsync("[role=tree] > [role=treeitem] > [role=group] > [role=treeitem]");
            });

            await Step("expand a section of 1,200 rows", 2_000, async () =>
            {
                await page.ClickAsync("[role=tree] > [role=treeitem] > [role=group] > [role=treeitem] >> nth=0 >> button >> nth=0");
                await page.WaitForSelectorAsync("text=700 more not shown");
            });

            Assert.Equal(500, await page.Locator("[role=tree] > [role=treeitem] > [role=group] > [role=treeitem] >> nth=0 >> [role=group] > [role=treeitem]").CountAsync());

            await Step("filter component types", 2_000, async () =>
            {
                await page.FillAsync("input[aria-label='Filter components']", "Type07");
                await page.WaitForFunctionAsync("() => document.querySelectorAll('table[aria-label=Components] tbody tr').length === 1");
            });

            await Step("load all events", 15_000, () => allLoaded.Task.WaitAsync(TimeSpan.FromSeconds(30)));

            await Step("timeline view (all events)", 6_000, async () =>
            {
                await page.ClickAsync("a[data-view=timeline]");
                await page.WaitForSelectorAsync(".timeline-wrap canvas");
            });

            await Step("diagnostics view", 4_000, async () =>
            {
                await page.ClickAsync("a[data-view=diagnostics]");
                await page.WaitForSelectorAsync("details.finding", new() { Timeout = 30_000 });
            });

            var longest = await page.EvaluateAsync<double>("() => window.__longest");
            foreach (var (step, ms, budget) in timings)
            {
                output.WriteLine($"{step}: {ms} ms (budget {budget} ms)");
            }

            output.WriteLine($"longest animation frame above 50 ms: {longest:0} ms");
            Assert.All(timings, t => Assert.True(t.Ms <= t.Budget, $"{t.Step} took {t.Ms} ms; the budget is {t.Budget} ms."));
            Assert.True(longest <= 1_000, $"A dashboard frame was delayed for {longest:0} ms.");
        }
        finally
        {
            await app.ApiAsync($"/sessions/{sessionId}", HttpMethod.Delete);
        }
    }

    private async Task<string> ImportAsync(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "_blazor-profiler/api/import") { Content = content };
        request.Headers.Add("X-Blazor-Profiler", "1");
        using var response = await app.Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.Str("id");
    }

    /// <summary>
    /// Builds a trace: a root with <see cref="Sections"/> sections of <see cref="RowsPerSection"/> rows, each instance created
    /// and rendered twice, with render batches and event handlers over one minute.
    /// </summary>
    private static (string Json, int Events) BuildTrace()
    {
        const long frequency = 10_000_000;
        const long start = 1_000_000_000;
        var events = new List<object>();
        long id = 0;
        long instanceId = 0;
        long Ticks(double ms) => (long)(ms * frequency / 1000);
        string TypeOf(long instance) => $"Demo.Scale.Type{instance % TypeCount:00}";

        void Created(long instance, string type, long at)
        {
            events.Add(new { type = "componentCreated", id = ++id, timestamp = at, duration = 0L, componentType = type, componentInstanceId = instance, source = "ComponentInstrumentation", sampleWeight = 1.0 });
        }

        void Render(long instance, string type, long at, int number)
        {
            events.Add(new { type = "componentRender", id = ++id, timestamp = at, duration = Ticks(0.05 + (instance % 7) * 0.02), componentType = type, componentInstanceId = instance, source = "ComponentInstrumentation", frameCount = 12, reason = number == 1 ? "FirstRender" : "Parameters", renderNumber = number, sampleWeight = 1.0 });
        }

        void Children(long parent, string type, long at, long[] children, string[] types)
        {
            events.Add(new { type = "componentChildren", id = ++id, timestamp = at, duration = 0L, componentType = type, componentInstanceId = parent, source = "ComponentInstrumentation", childInstanceIds = children, childTypes = types, sampleWeight = 1.0 });
        }

        var root = ++instanceId;
        Created(root, "Demo.Scale.Root", start);
        Render(root, "Demo.Scale.Root", start, 1);
        var sectionIds = new long[Sections];
        for (var s = 0; s < Sections; s++)
        {
            var section = sectionIds[s] = ++instanceId;
            var at = start + Ticks(s * 3_000);
            Created(section, "Demo.Scale.Section", at);
            Render(section, "Demo.Scale.Section", at, 1);
            var rows = new long[RowsPerSection];
            var rowTypes = new string[RowsPerSection];
            for (var r = 0; r < RowsPerSection; r++)
            {
                var row = rows[r] = ++instanceId;
                rowTypes[r] = TypeOf(row);
                var rowAt = at + Ticks(r * 0.5);
                Created(row, rowTypes[r], rowAt);
                Render(row, rowTypes[r], rowAt, 1);
                Render(row, rowTypes[r], rowAt + Ticks(1_000), 2);
            }

            Children(section, "Demo.Scale.Section", at + Ticks(700), rows, rowTypes);
            events.Add(new { type = "renderBatch", id = ++id, timestamp = at, duration = Ticks(40), updatedComponentCount = RowsPerSection + 1, includesClientRoundTrip = false, source = "FrameworkMetrics", sampleWeight = 1.0 });
            events.Add(new { type = "eventHandler", id = ++id, timestamp = at + Ticks(1_000), duration = Ticks(30), componentType = "Demo.Scale.Section", componentInstanceId = section, source = "FrameworkMetrics", methodName = "Refresh", attributeName = "onclick", operationId = (long)(s + 1), sampleWeight = 1.0 });
        }

        Children(root, "Demo.Scale.Root", start + Ticks(50_000), sectionIds, [.. sectionIds.Select(_ => "Demo.Scale.Section")]);

        var document = new
        {
            format = "blazor-profiler-trace",
            formatVersion = 1,
            eventSchemaVersion = 3,
            metadata = new { sessionId = "scale", sessionName = "Dashboard scale", createdAt = DateTimeOffset.UtcNow, exportedAt = DateTimeOffset.UtcNow, durationMs = 60_000.0, source = "webassembly" },
            clock = new { frequency, anchorTimestamp = start, anchorTime = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
            collectors = Array.Empty<object>(),
            events,
        };
        return (JsonSerializer.Serialize(document), events.Count);
    }
}
