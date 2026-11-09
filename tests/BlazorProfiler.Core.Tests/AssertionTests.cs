using BlazorProfiler.Analysis;
using BlazorProfiler.Events;
using BlazorProfiler.Testing;

namespace BlazorProfiler.Core.Tests;

public class AssertionTests
{
    [Fact]
    public async Task NoFindingsPassesForACleanSessionAndFailsWithEveryViolationListed()
    {
        var (host, session, _) = TestHost.WithSession();
        await ProfilerAssert.NoFindingsAsync(host, session);

        host.Emit(Ev.Handler("App.Orders", "Load", 0, 50, operation: 1));
        for (var i = 0; i < 6; i++)
        {
            host.Emit(Ev.Db("SELECT * FROM Lines WHERE OrderId = @p0", 1 + i, 2, operation: 1, arguments: i));
        }

        var error = await Assert.ThrowsAsync<ProfilerAssertionException>(() => ProfilerAssert.NoFindingsAsync(host, session));
        Assert.Contains(error.Violations, v => v.StartsWith("BP8001", StringComparison.Ordinal));
        Assert.Contains("BP8001", error.Message, StringComparison.Ordinal);

        // Ignored rules and higher thresholds pass.
        await ProfilerAssert.NoFindingsAsync(host, session, DiagnosticSeverity.Low, "BP8001");
        await ProfilerAssert.NoFindingsAsync(host, session, DiagnosticSeverity.Critical);
    }

    [Fact]
    public void NoFindingsIgnoresSuppressedFindings()
    {
        var report = new DiagnosticReport
        {
            Findings =
            [
                new DiagnosticFinding
                {
                    RuleId = "BP1001", RuleVersion = 1, Category = DiagnosticCategory.Rendering, Title = "t", Summary = "s", Explanation = "e", Recommendation = "r",
                    Severity = DiagnosticSeverity.High, Confidence = DiagnosticConfidence.High, Evidence = [],
                    Suppression = new FindingSuppression { Fingerprint = "x", Kind = SuppressionKind.Expected },
                },
            ],
        };

        ProfilerAssert.NoFindings(report);
    }

    [Fact]
    public void BudgetReportsEveryExceededLimit()
    {
        var (host, session, _) = TestHost.WithSession();
        for (var i = 0; i < 5; i++)
        {
            host.Emit(Ev.Render("App.Row", i, instance: i + 1));
            host.Emit(Ev.Http($"https://x.test/items/{i}", i, 10));
        }

        host.Emit(new ComponentRetainedEvent { ComponentType = "App.Row", ComponentInstanceId = 1, Timestamp = Ev.T(10) });
        var summary = session.GetSummary();

        var budget = new ProfilerBudget { MaxComponentRenders = 10, MaxHttpRequests = 3, MaxRetainedComponents = 0, MaxDatabaseCommands = 0 }
            .WithMaxRenders("App.Row", 4);
        var violations = budget.Check(summary);
        Assert.Equal(3, violations.Count);
        Assert.Contains(violations, v => v.StartsWith("HTTP requests: 5", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.StartsWith("Retained components: 1", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.StartsWith("Row renders: 5", StringComparison.Ordinal));

        var error = Assert.Throws<ProfilerAssertionException>(() => budget.Assert(summary));
        Assert.Equal(3, error.Violations.Count);
        new ProfilerBudget { MaxComponentRenders = 5, MaxHttpRequests = 5 }.Assert(summary);
    }
}
