using System.Text.Json;
using BlazorProfiler.Cli;
using BlazorProfiler.Events;
using BlazorProfiler.Export;

namespace BlazorProfiler.Core.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bp-cli-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private async Task<string> WriteTraceAsync(string name, int queries, int renders)
    {
        var (host, session, _) = TestHost.WithSession();
        host.Emit(Ev.Handler("App.Orders", "Load", 0, 50, operation: 1));
        for (var i = 0; i < queries; i++)
        {
            host.Emit(Ev.Db("SELECT * FROM Lines WHERE OrderId = @p0", 1 + i, 2, operation: 1, arguments: i, origin: "App.Orders"));
        }

        for (var i = 0; i < renders; i++)
        {
            host.Emit(Ev.Render("App.Row", 100 + i, instance: i + 1));
        }

        var document = TraceDocumentBuilder.Create(session, host.Options, host.Diagnostics.Analyze(session), host.GetHealth(), "test");
        var path = Path.Combine(_directory, name);
        await using var stream = File.Create(path);
        await new CompressedTraceExporter().ExportAsync(document, stream);
        return path;
    }

    private static async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await TraceCli.RunAsync(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task CheckFailsOnFindingsAtOrAboveTheThresholdAndPassesOtherwise()
    {
        var trace = await WriteTraceAsync("n-plus-one.bptrace", queries: 8, renders: 1);

        var (code, output, _) = await Run("check", trace, "--fail-on", "low");
        Assert.Equal(TraceCli.Violations, code);
        Assert.Contains("FAIL", output, StringComparison.Ordinal);
        Assert.Contains("BP8001", output, StringComparison.Ordinal);

        Assert.Equal(TraceCli.Success, (await Run("check", trace, "--fail-on", "critical")).Code);
        Assert.Equal(TraceCli.Success, (await Run("check", trace, "--fail-on", "low", "--ignore", "BP8001")).Code);
    }

    [Fact]
    public async Task CheckWritesSarifAndGitHubAnnotations()
    {
        var trace = await WriteTraceAsync("sarif.bptrace", queries: 8, renders: 1);
        var sarifPath = Path.Combine(_directory, "out.sarif");

        var (code, _, _) = await Run("check", trace, "--fail-on", "low", "--format", "sarif", "--output", sarifPath);
        Assert.Equal(TraceCli.Violations, code);
        using var sarif = JsonDocument.Parse(await File.ReadAllTextAsync(sarifPath));
        Assert.Equal("2.1.0", sarif.RootElement.GetProperty("version").GetString());
        var run = sarif.RootElement.GetProperty("runs")[0];
        Assert.Equal("BlazorProfiler", run.GetProperty("tool").GetProperty("driver").GetProperty("name").GetString());
        Assert.Contains(run.GetProperty("results").EnumerateArray(), r => r.GetProperty("ruleId").GetString() == "BP8001" && r.GetProperty("level").GetString() != "note");

        var (_, annotations, _) = await Run("check", trace, "--fail-on", "low", "--format", "github");
        Assert.Contains("title=BP8001", annotations, StringComparison.Ordinal);
        Assert.Matches("^::(warning|error) ", annotations.Split('\n').First(l => l.Contains("BP8001", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CheckEnforcesBudgets()
    {
        var trace = await WriteTraceAsync("budget.bptrace", queries: 0, renders: 12);
        var budget = Path.Combine(_directory, "budget.json");
        await File.WriteAllTextAsync(budget, """{ "maxComponentRenders": 10, "maxRendersByComponent": { "App.Row": 20 } }""");

        var (code, output, _) = await Run("check", trace, "--budget", budget);
        Assert.Equal(TraceCli.Violations, code);
        Assert.Contains("Component renders: 12 exceeds the budget of 10", output, StringComparison.Ordinal);

        await File.WriteAllTextAsync(budget, """{ "maxComponentRenders": 12 }""");
        Assert.Equal(TraceCli.Success, (await Run("check", trace, "--budget", budget)).Code);
    }

    [Fact]
    public async Task CompareFlagsCountRegressionsOnly()
    {
        var baseline = await WriteTraceAsync("baseline.bptrace", queries: 2, renders: 10);
        var current = await WriteTraceAsync("current.bptrace", queries: 2, renders: 20);

        var (code, output, _) = await Run("compare", baseline, current);
        Assert.Equal(TraceCli.Violations, code);
        Assert.Contains("REGRESSION", output, StringComparison.Ordinal);
        Assert.Contains("Component renders", output, StringComparison.Ordinal);

        Assert.Equal(TraceCli.Success, (await Run("compare", baseline, current, "--threshold", "150")).Code);
        Assert.Equal(TraceCli.Success, (await Run("compare", current, baseline)).Code);
    }

    [Fact]
    public async Task ReportWritesMarkdown()
    {
        var trace = await WriteTraceAsync("report.bptrace", queries: 8, renders: 1);
        var (code, output, _) = await Run("report", trace);
        Assert.Equal(TraceCli.Success, code);
        Assert.StartsWith("# Blazor Profiler report", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("check")]
    [InlineData("check", "missing.bptrace")]
    [InlineData("check", "x", "--fail-on", "urgent")]
    public async Task InvalidUsageReturnsTheUsageErrorCode(params string[] args)
    {
        var (code, _, error) = await Run(args);
        Assert.Equal(TraceCli.UsageError, code);
        Assert.StartsWith("error:", error, StringComparison.Ordinal);
    }
}
