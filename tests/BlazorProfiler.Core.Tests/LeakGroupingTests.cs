using System.Text.Json;
using BlazorProfiler.Analysis;
using BlazorProfiler.Api;
using BlazorProfiler.Cli;
using BlazorProfiler.Events;
using BlazorProfiler.Export;
using BlazorProfiler.Sessions;

namespace BlazorProfiler.Core.Tests;

public sealed class LeakGroupingTests : IDisposable
{
    private const string Subscription = "AppState.OnChange → Counter.StateHasChanged — subscribed by the component and never removed; unsubscribe in Dispose";
    private const string Timer = ComponentRetainedEvent.TentativePrefix + "Counter._timer (Timer) — dispose it in Dispose";

    private readonly string _directory = Directory.CreateTempSubdirectory("bp-leaks-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static ComponentRetainedEvent Retained(string type, long instance, string? circuit = null, RetentionReason reason = RetentionReason.Disposed, params string[] retainers) => new()
    {
        ComponentType = type,
        ComponentInstanceId = instance,
        CircuitId = circuit,
        Reason = reason,
        ReleasedTimestamp = Ev.T(instance * 10),
        Timestamp = Ev.T(instance * 10 + 5_000),
        SurvivedCollections = (int)instance,
        RetainedBy = retainers.Length == 0 ? null : retainers,
        Source = InstrumentationSource.ComponentInstrumentation,
    };

    [Fact]
    public void SignatureDependsOnTheTypeAndTheRetainersOnly()
    {
        var a = LeakGrouping.Signature("App.Counter", [Subscription, Timer]);
        Assert.Equal(16, a.Length);
        Assert.Equal(a, LeakGrouping.Signature("App.Counter", [Timer, Subscription, Timer]));
        Assert.NotEqual(a, LeakGrouping.Signature("App.Counter", [Subscription]));
        Assert.NotEqual(a, LeakGrouping.Signature("App.Other", [Subscription, Timer]));
        Assert.Equal(LeakGrouping.Signature("App.Counter", null), LeakGrouping.Signature("App.Counter", []));
    }

    [Fact]
    public void GroupsLeaksAcrossSessionsAndCountsEachInstanceOnce()
    {
        var (host, first, _) = TestHost.WithSession();
        using var owner = host;

        // A second session records the same instances (for example a process-wide session next to a circuit's session).
        var second = host.StartSession(new ProfilerSessionOptions { Name = "second" });
        host.Emit(Retained("App.Counter", 1, "c1", RetentionReason.Disposed, Subscription));
        host.Emit(Retained("App.Counter", 2, "c2", RetentionReason.ScopeEnded, Subscription));
        host.Emit(Retained("App.Chart", 3, "c1"));
        second.Stop();
        host.Emit(Retained("App.Counter", 4, "c3", RetentionReason.Disposed, Subscription));

        var report = LeakGrouping.Group(host.GetSessions());
        Assert.Equal(2, report.SessionsAnalyzed);
        Assert.Equal(4, report.Instances);
        Assert.Equal(2, report.Groups.Count);

        var counter = report.Groups[0];
        Assert.Equal("App.Counter", counter.ComponentType);
        Assert.Equal(LeakGrouping.Signature("App.Counter", [Subscription]), counter.Signature);
        Assert.Equal([Subscription], counter.Retainers);
        Assert.True(counter.RetainerIdentified);
        Assert.Equal(3, counter.Instances);
        // The second session saw only instances the first one already counted: it adds nothing.
        Assert.Equal(1, counter.Sessions);
        Assert.Equal(3, counter.Circuits);
        Assert.Equal(2, counter.Disposed);
        Assert.Equal(1, counter.ScopeEnded);
        Assert.Equal(4, counter.MaxSurvivedCollections);
        Assert.Equal([first.Id], counter.SessionIds);
        Assert.True(counter.LastReportedAt > counter.FirstReleasedAt);

        var chart = report.Groups[1];
        Assert.Empty(chart.Retainers);
        Assert.False(chart.RetainerIdentified);
        Assert.Equal(1, chart.Instances);
    }

    [Fact]
    public void ImportedTracesHaveTheirOwnInstanceIdentifiers()
    {
        var (host, session, _) = TestHost.WithSession();
        using var owner = host;
        host.Emit(Retained("App.Counter", 1, null, RetentionReason.Disposed, Subscription));
        session.Stop();
        var document = TraceDocumentBuilder.Create(session, host.Options, host.Diagnostics.Analyze(session), host.GetHealth(), "test");
        host.AddImportedSession(document);

        // The imported copy reuses instance 1, but it comes from another run.
        var group = Assert.Single(LeakGrouping.Group(host.GetSessions()).Groups);
        Assert.Equal(2, group.Instances);
        Assert.Equal(2, group.Sessions);
    }

    [Fact]
    public void ApiRespectsSessionVisibility()
    {
        var (host, _, _) = TestHost.WithSession();
        using var owner = host;
        host.StartSession(new ProfilerSessionOptions { Name = "mine", CircuitId = "c1" });
        host.Emit(Retained("App.Counter", 1, "c1", RetentionReason.Disposed, Subscription));
        host.Emit(Retained("App.Chart", 2, "c2"));
        var api = new ProfilerApi(host, "test");

        Assert.Equal(2, api.GetLeaks(ProfilerApiAccess.Full).Groups.Count);
        var restricted = new ProfilerApiAccess { OwnedCircuits = new HashSet<string>(["c1"]) };
        var mine = Assert.Single(api.GetLeaks(restricted).Groups);
        Assert.Equal("App.Counter", mine.ComponentType);
        Assert.Equal(1, api.GetLeaks(restricted).SessionsAnalyzed);
    }

    [Fact]
    public async Task McpListsLeaksAcrossSessions()
    {
        var (host, _, _) = TestHost.WithSession();
        using var owner = host;
        host.Emit(Retained("App.Counter", 1, "c1", RetentionReason.Disposed, Subscription));
        var server = new ProfilerMcpServer(new ProfilerApi(host, "test"));
        var response = await server.HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_leaks","arguments":{}}}""",
            ProfilerApiAccess.Full);
        var text = JsonDocument.Parse(response!).RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains(LeakGrouping.Signature("App.Counter", [Subscription]), text, StringComparison.Ordinal);
        Assert.Contains("AppState.OnChange", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RetainedComponentsRuleListsTheLeakSignature()
    {
        var (host, session, _) = TestHost.WithSession();
        using var owner = host;
        host.Emit(Retained("App.Counter", 1, "c1", RetentionReason.Disposed, Subscription));
        var finding = Assert.Single(host.Diagnostics.Analyze(session).Findings, f => f.RuleId == "BP5004");
        Assert.Contains(finding.Evidence, e => e.Label == "Leak signature" && e.Value == LeakGrouping.Signature("App.Counter", [Subscription]));
    }

    [Fact]
    public async Task CliGroupsLeaksAcrossTraceFiles()
    {
        async Task<string> Write(string name, params ComponentRetainedEvent[] events)
        {
            var (host, session, _) = TestHost.WithSession();
            using var owner = host;
            foreach (var e in events)
            {
                host.Emit(e);
            }

            var path = Path.Combine(_directory, name);
            await using var stream = File.Create(path);
            await new CompressedTraceExporter().ExportAsync(TraceDocumentBuilder.Create(session, host.Options, host.Diagnostics.Analyze(session), host.GetHealth(), "test"), stream);
            return path;
        }

        var monday = await Write("monday.bptrace", Retained("App.Counter", 1, null, RetentionReason.Disposed, Subscription));
        var tuesday = await Write("tuesday.bptrace", Retained("App.Counter", 1, null, RetentionReason.Disposed, Subscription), Retained("App.Chart", 2));
        var clean = await Write("clean.bptrace");

        using var output = new StringWriter();
        Assert.Equal(TraceCli.Violations, await TraceCli.RunAsync(["leaks", monday, tuesday, "--fail-on-leaks"], output, TextWriter.Null));
        Assert.Contains("2 leak signature(s), 3 retained instance(s) in 2 session(s)", output.ToString(), StringComparison.Ordinal);

        using var json = new StringWriter();
        Assert.Equal(TraceCli.Success, await TraceCli.RunAsync(["leaks", monday, tuesday, "--format", "json"], json, TextWriter.Null));
        var counter = JsonDocument.Parse(json.ToString()).RootElement.GetProperty("groups")[0];
        Assert.Equal("App.Counter", counter.GetProperty("componentType").GetString());
        Assert.Equal(2, counter.GetProperty("sessions").GetInt32());
        Assert.Equal(["monday.bptrace", "tuesday.bptrace"], counter.GetProperty("sessionIds").EnumerateArray().Select(e => e.GetString()).Order().ToList());

        Assert.Equal(TraceCli.Success, await TraceCli.RunAsync(["leaks", clean, "--fail-on-leaks"], TextWriter.Null, TextWriter.Null));

        // Traces with one file name in different folders (CI artifacts) are different sessions, named by path.
        Directory.CreateDirectory(Path.Combine(_directory, "run1"));
        Directory.CreateDirectory(Path.Combine(_directory, "run2"));
        var run1 = await Write(Path.Combine("run1", "trace.bptrace"), Retained("App.Counter", 1, null, RetentionReason.Disposed, Subscription));
        var run2 = await Write(Path.Combine("run2", "trace.bptrace"), Retained("App.Counter", 1, null, RetentionReason.Disposed, Subscription));
        using var runs = new StringWriter();
        Assert.Equal(TraceCli.Success, await TraceCli.RunAsync(["leaks", run1, run2, monday, "--format", "json"], runs, TextWriter.Null));
        var group = JsonDocument.Parse(runs.ToString()).RootElement.GetProperty("groups")[0];
        Assert.Equal(3, group.GetProperty("sessions").GetInt32());
        Assert.Equal(new[] { run1, run2, "monday.bptrace" }.Order(StringComparer.Ordinal), group.GetProperty("sessionIds").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal));
    }
}
