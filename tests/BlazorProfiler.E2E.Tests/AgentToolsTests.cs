using System.Text.Json;
using Microsoft.Playwright;

namespace BlazorProfiler.E2E.Tests;

/// <summary>
/// In-page agent tools through WebMCP (<c>document.modelContext</c>), with Chromium's own implementation (enabled by the
/// <c>WebMCPTesting</c> feature, as <c>chrome://flags/#enable-webmcp-testing</c> does): the profiler registers its read-only
/// tools, and an agent's call returns the profiler's answer.
/// </summary>
public sealed class AgentToolsTests(SampleApp app) : IClassFixture<SampleApp>
{
    [Theory]
    [InlineData("server/slow-handler")]
    [InlineData("wasm/slow-handler")]
    public async Task ProfilerToolsAreOfferedToAgentsInThePage(string path)
    {
        await using var browser = await app.Playwright.Chromium.LaunchAsync(new() { Headless = true, Args = ["--enable-features=WebMCPTesting"] });
        var page = await browser.NewPageAsync();
        await page.GotoAsync(app.BaseUrl + path);
        await EndToEndTests.ClickWhenInteractiveAsync(page, "#block", "Computed", "#slow-status");

        // The tools are registered once the profiler client has started.
        string[] names = [];
        await Waits.UntilAsync(
            async () =>
            {
                names = await page.EvaluateAsync<string[]>("async () => (await document.modelContext.getTools()).map(t => t.name)");
                return names.Contains("blazor_profiler_list_findings");
            },
            30_000,
            "The profiler's tools were not registered.");

        Assert.Contains("blazor_profiler_list_sessions", names);
        Assert.Contains("blazor_profiler_get_overview", names);

        // Read-only: tools that change the profiling state are never offered in the page.
        Assert.DoesNotContain("blazor_profiler_control_session", names);
        Assert.DoesNotContain("blazor_profiler_run_command", names);
        Assert.All(names, n => Assert.StartsWith("blazor_profiler_", n, StringComparison.Ordinal));

        var annotations = await page.EvaluateAsync<JsonElement>("async () => (await document.modelContext.getTools()).find(t => t.name === 'blazor_profiler_list_sessions').annotations ?? null");
        if (annotations.ValueKind == JsonValueKind.Object)
        {
            Assert.True(annotations.GetProperty("readOnlyHint").GetBoolean());
        }

        // An agent's call reaches the profiler and returns its answer.
        var answer = await page.EvaluateAsync<string>("""
            async () => {
              const tool = (await document.modelContext.getTools()).find(t => t.name === 'blazor_profiler_list_sessions');
              const result = await document.modelContext.executeTool(tool, {});
              return typeof result === 'string' ? result : JSON.stringify(result);
            }
            """);
        Assert.Contains("session", answer, StringComparison.OrdinalIgnoreCase);
        await page.CloseAsync();
    }
}
