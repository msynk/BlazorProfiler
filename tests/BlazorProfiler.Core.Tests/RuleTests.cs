using BlazorProfiler.Analysis;
using BlazorProfiler.Analysis.Rules;
using BlazorProfiler.Events;

namespace BlazorProfiler.Core.Tests;

public class RuleTests
{
    private static DiagnosticReport Analyze(IEnumerable<ProfilerEvent> events, Action<ProfilerOptions>? configure = null)
    {
        var (host, session, _) = TestHost.WithSession(configure);
        foreach (var e in events)
        {
            host.Emit(e);
        }

        return host.Diagnostics.Analyze(session);
    }

    private static List<DiagnosticFinding> Findings(DiagnosticReport report, string ruleId) =>
        report.Findings.Where(f => f.RuleId == ruleId).ToList();

    [Fact]
    public void AllBuiltInRulesHaveUniqueIdsAndRunWithoutErrorsOnEmptySessions()
    {
        var rules = BuiltInRules.Create();
        Assert.Equal(rules.Count, rules.Select(r => r.Descriptor.Id).Distinct().Count());
        var report = Analyze([]);
        Assert.Empty(report.RuleErrors);
        Assert.Empty(report.Findings);
        Assert.Equal(rules.Count, report.Rules.Count);
    }

    [Fact]
    public void BP1001_ReportsHighFrequencyRenderingWithHighConfidenceWhenOutputUnchanged()
    {
        var events = Enumerable.Range(0, 60).Select(i => (ProfilerEvent)Ev.Render("App.Clock", i * 50, 0.5, unchanged: true)).ToList();
        events.Add(Ev.Render("App.Rare", 0));
        var finding = Assert.Single(Findings(Analyze(events, o => o.Instrumentation.TrackRenderOutput = true), "BP1001"));
        Assert.Equal("App.Clock", finding.ComponentType);
        Assert.Equal(DiagnosticConfidence.High, finding.Confidence);
        Assert.Contains("20", finding.Title, StringComparison.Ordinal);
        Assert.NotEmpty(finding.Caveats);
        Assert.StartsWith(DocumentationRecommendationProvider.BaseUrl, finding.DocumentationUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void BP1001_UsesParameterUpdatesAsLowConfidenceProxyWithoutInstrumentation()
    {
        var events = Enumerable.Range(0, 60).Select(i => (ProfilerEvent)Ev.Parameters("App.Row", i * 50)).ToList();
        var finding = Assert.Single(Findings(Analyze(events), "BP1001"));
        Assert.Equal(DiagnosticConfidence.Low, finding.Confidence);
        Assert.True(finding.Severity <= DiagnosticSeverity.Medium);
        Assert.Contains(finding.Evidence, e => e.Quality == ValueQuality.Inferred);
    }

    [Fact]
    public void BP1001_WeightsSampledRendersWhenEstimatingRates()
    {
        // 30 renders per second for three seconds, sampled at 0.25: only about 7.5 events per second are retained (each
        // with weight 4), and fewer than MinimumRenderSamples in total. The weighted rate is still 30 per second.
        var events = Enumerable.Range(0, 90).Select(i => (ProfilerEvent)Ev.Render("App.Ticker", i * (1000.0 / 30), 0.2)).ToList();
        var report = Analyze(events, o => o.Sampling.Rates[ProfilerEventKind.ComponentRender] = 0.25);
        Assert.Empty(report.RuleErrors);
        var finding = Assert.Single(Findings(report, "BP1001"));
        Assert.Equal("App.Ticker", finding.ComponentType);
        Assert.Contains(finding.Evidence, e => e.Label == "Peak rate" && e.Quality == ValueQuality.Estimated);
    }

    [Fact]
    public void BP4001_WeightsSampledInteropCalls()
    {
        // 60 calls in one operation, sampled at 0.5: 30 retained calls stand for 60.
        var events = new List<ProfilerEvent> { Ev.Handler("App.Chart", "Draw", 0, 50, 1) };
        events.AddRange(Enumerable.Range(0, 60).Select(i => (ProfilerEvent)Ev.Interop("chart.point", i * 20, 0.1, operation: 1)));
        var findings = Findings(Analyze(events, o => o.Sampling.Rates[ProfilerEventKind.JSInterop] = 0.5), "BP4001");
        var finding = Assert.Single(findings, f => f.Subject!.Contains("Chart.Draw", StringComparison.Ordinal));
        Assert.Contains("60", finding.Title, StringComparison.Ordinal);
        Assert.Equal(ValueQuality.Estimated, finding.Impact!.Quality);
    }

    [Fact]
    public void HandlerRulesTolerateDuplicateOperationIds()
    {
        // Imported or merged traces can contain several handlers with the same operation identifier.
        var events = new List<ProfilerEvent>
        {
            Ev.Handler("App.Form", "Save", 0, 5, 1),
            Ev.Handler("App.Form", "Save", 1, 5, 1),
            Ev.Http("https://x.test/api/a", 2, 1, operation: 1),
            Ev.Http("https://x.test/api/a", 3, 1, operation: 1),
        };
        events.AddRange(Enumerable.Range(0, 60).Select(i => (ProfilerEvent)Ev.Interop("form.field", 4 + (i * 0.1), 0.05, operation: 1)));
        var report = Analyze(events);
        Assert.Empty(report.RuleErrors);
        Assert.Single(Findings(report, "BP2003"));
        Assert.Contains(Findings(report, "BP4001"), f => f.Subject!.Contains("Form.Save", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("data:text/html,x", null)]
    [InlineData("//evil.test/x", null)]
    [InlineData("/\\evil.test/x", null)]
    [InlineData(" https://x.test", null)]
    [InlineData("https://wiki.example.com/perf/bp2001", "https://wiki.example.com/perf/bp2001")]
    [InlineData("/wiki/perf/bp2001", "/wiki/perf/bp2001")]
    public void DocumentationLinksMustBeWebUrlsOrSameOriginPaths(string link, string? expected)
    {
        // A rejected link falls back to the built-in rule reference, like a missing one.
        var (host, session, _) = TestHost.WithSession(rules: [new SlowEndpointRule()]);
        var engine = new DiagnosticEngine(new DiagnosticOptions(), [new SlowEndpointRule()], [new LinkProvider(link)]);
        host.Emit(Ev.Http("https://x.test/slow", 0, 5000));
        var finding = Assert.Single(engine.Analyze(session).Findings);
        Assert.Equal(expected ?? DocumentationRecommendationProvider.BaseUrl + "bp3004", finding.DocumentationUrl);
    }

    [Fact]
    public void BP1001_DoesNotOverCountComponentsThatAlternateWithOthersUnderSampling()
    {
        // A list renders B, B, B, A three times per second for 20 seconds: A renders 3 times per second. Sampled at 0.25 with
        // one counter per kind, every retained render would be A's (weight 4), reporting A at 12 per second.
        var events = new List<ProfilerEvent>();
        for (var cycle = 0; cycle < 60; cycle++)
        {
            for (var i = 0; i < 4; i++)
            {
                events.Add(Ev.Render(i == 3 ? "App.A" : "App.B", (cycle * 1000.0 / 3) + (i * 10), 0.2));
            }
        }

        var report = Analyze(events, o => o.Sampling.Rates[ProfilerEventKind.ComponentRender] = 0.25);
        Assert.Empty(report.RuleErrors);
        Assert.DoesNotContain(Findings(report, "BP1001"), f => f.ComponentType == "App.A");
    }

    [Fact]
    public void BP1001_WeightsRenderTimeLikeTheRenderCount()
    {
        // 400 renders of 2 ms at 30 per second, sampled at 0.25: 100 retained renders stand for 400 renders and 800 ms.
        var events = Enumerable.Range(0, 400).Select(i => (ProfilerEvent)Ev.Render("App.Ticker", i * (1000.0 / 30), 2)).ToList();
        var finding = Assert.Single(Findings(Analyze(events, o => o.Sampling.Rates[ProfilerEventKind.ComponentRender] = 0.25), "BP1001"));
        Assert.Equal(400, finding.Impact!.Occurrences);
        Assert.Equal(800, finding.Impact.TotalMs!.Value, 3);
        Assert.Equal(ValueQuality.Estimated, finding.Impact.Quality);
    }

    [Fact]
    public void BP1001_IgnoresInfrequentRendering()
    {
        var events = Enumerable.Range(0, 60).Select(i => (ProfilerEvent)Ev.Render("App.Slow", i * 1000)).ToList();
        Assert.Empty(Findings(Analyze(events), "BP1001"));
    }

    [Fact]
    public void BP1002_AttributesLargeBatchToInteraction()
    {
        var finding = Assert.Single(Findings(Analyze([Ev.Handler("App.Grid", "Sort", 0, 5, 1), Ev.Batch(5, 30, 400, 1), Ev.Batch(100, 1, 3)]), "BP1002"));
        Assert.Contains("Grid.Sort", finding.Title, StringComparison.Ordinal);
        Assert.Equal(DiagnosticConfidence.High, finding.Confidence);
        Assert.Equal(DiagnosticSeverity.High, finding.Severity);
    }

    [Fact]
    public void BP1003_ReportsReferenceOnlyParameterChurn()
    {
        var events = Enumerable.Range(0, 30).Select(i => (ProfilerEvent)Ev.Parameters("App.Item", i, 0.1, ParameterChangeState.ChangedByReferenceOnly, InstrumentationSource.ComponentInstrumentation, ["OnSelect"])).ToList();
        var finding = Assert.Single(Findings(Analyze(events), "BP1003"));
        Assert.Contains("OnSelect", finding.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BP1004_ReportsRendersWithoutVisibleChange()
    {
        var events = Enumerable.Range(0, 40).Select(i => (ProfilerEvent)Ev.Render("App.Badge", i * 1000, 1, unchanged: i % 2 == 0)).ToList();
        var finding = Assert.Single(Findings(Analyze(events), "BP1004"));
        Assert.Equal(DiagnosticConfidence.High, finding.Confidence);
        Assert.Equal(20, finding.Impact!.TotalMs!.Value, 3);
    }

    [Fact]
    public void BP1005_DistinguishesSynchronousLifecycleFromFrameworkMetrics()
    {
        var report = Analyze(
        [
            new LifecycleMethodEvent { ComponentType = "App.Report", Method = LifecycleMethod.OnInitialized, Duration = Ev.D(120), SynchronousDuration = Ev.D(120), Timestamp = Ev.T(0) },
            Ev.Parameters("App.Remote", 0, 300),
        ]);
        var findings = Findings(report, "BP1005");
        Assert.Equal(2, findings.Count);
        Assert.Equal(DiagnosticConfidence.High, findings.Single(f => f.ComponentType == "App.Report").Confidence);
        var metrics = findings.Single(f => f.ComponentType == "App.Remote");
        Assert.Equal(DiagnosticConfidence.Medium, metrics.Confidence);
        Assert.True(metrics.Severity <= DiagnosticSeverity.Medium);
    }

    [Fact]
    public void BP1006_ReportsBurstOfComponentInstances()
    {
        var events = Enumerable.Range(0, 150).Select(i => (ProfilerEvent)new ComponentCreatedEvent { ComponentType = "App.Cell", ComponentInstanceId = i + 1, Timestamp = Ev.T(i) }).ToList();
        var finding = Assert.Single(Findings(Analyze(events), "BP1006"));
        Assert.Equal("App.Cell", finding.ComponentType);
    }

    [Fact]
    public void BP1007_ReportsSlowBatchesButNotServerRoundTrips()
    {
        var report = Analyze([Ev.Batch(0, 80, 5), Ev.Batch(100, 400, 5, roundTrip: true, circuit: "c")]);
        var finding = Assert.Single(Findings(report, "BP1007"));
        Assert.Single(finding.RelatedEventIds);
    }

    [Fact]
    public void BP2001_ExplainsHttpDominatedHandlers()
    {
        var report = Analyze([Ev.Handler("App.Orders", "Load", 0, 800, 1), Ev.Http("https://x.test/orders", 10, 700, operation: 1)]);
        var finding = Assert.Single(Findings(report, "BP2001"));
        Assert.Equal(DiagnosticConfidence.Medium, finding.Confidence);
        Assert.Contains("HTTP", finding.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void BP2001_ReportsSynchronousBlockingWithHighConfidence()
    {
        var finding = Assert.Single(Findings(Analyze([Ev.Handler("App.Calc", "Run", 0, 300, 1, syncMs: 290)]), "BP2001"));
        Assert.Equal(DiagnosticConfidence.High, finding.Confidence);
        Assert.Contains("synchronously", finding.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void BP2001_LongRunningHandlerIsHighSeverity()
    {
        var finding = Assert.Single(Findings(Analyze([Ev.Handler("App.Export", "Run", 0, 2500, 1)]), "BP2001"));
        Assert.Equal(DiagnosticSeverity.High, finding.Severity);
    }

    [Fact]
    public void BP2002_ReportsRapidInputEvents()
    {
        var events = new List<ProfilerEvent>();
        for (var i = 0; i < 40; i++)
        {
            events.Add(Ev.Handler("App.Search", "OnInput", i * 50, 1, i + 1, "oninput"));
            events.Add(Ev.Batch((i * 50) + 1, 1, 3, i + 1));
        }

        var finding = Assert.Single(Findings(Analyze(events), "BP2002"));
        Assert.Equal(DiagnosticConfidence.High, finding.Confidence);
    }

    [Fact]
    public void BP2003_ReportsDuplicateRequestsWithinOneInteraction()
    {
        var report = Analyze(
        [
            Ev.Handler("App.Page", "Refresh", 0, 100, 1),
            Ev.Http("https://x.test/api/user", 1, 20, operation: 1),
            Ev.Http("https://x.test/api/user", 2, 20, operation: 1),
            Ev.Http("https://x.test/api/other", 3, 20, operation: 1),
        ]);
        var finding = Assert.Single(Findings(report, "BP2003"));
        Assert.Equal(DiagnosticConfidence.High, finding.Confidence);
    }

    [Fact]
    public void BP2004_ReportsManyBatchesPerInteraction()
    {
        var events = new List<ProfilerEvent> { Ev.Handler("App.Import", "Run", 0, 500, 1) };
        events.AddRange(Enumerable.Range(0, 20).Select(i => (ProfilerEvent)Ev.Batch(i * 20, 1, 1, 1)));
        Assert.Single(Findings(Analyze(events), "BP2004"));
    }

    [Fact]
    public void BP3001_ReportsDuplicateAndOverlappingGets()
    {
        var finding = Assert.Single(Findings(Analyze([Ev.Http("https://x.test/a", 0, 100), Ev.Http("https://x.test/a", 50, 100), Ev.Http("https://x.test/a", 5000, 100)]), "BP3001"));
        Assert.Contains("1 of them while", finding.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BP3001_IgnoresPostAndDistantRequests()
    {
        var report = Analyze([Ev.Http("https://x.test/a", 0, 10, method: "POST"), Ev.Http("https://x.test/a", 10, 10, method: "POST"), Ev.Http("https://x.test/b", 0, 10), Ev.Http("https://x.test/b", 10_000, 10)]);
        Assert.Empty(Findings(report, "BP3001"));
    }

    [Fact]
    public void BP3001_ReportsDistinctUrlsOfOneEndpointAsOneFinding()
    {
        // /items/1 and /items/2 share the endpoint template (the finding's subject and fingerprint), so separate findings
        // would be indistinguishable and suppressing one would suppress the other.
        var events = new List<ProfilerEvent>();
        foreach (var id in new[] { 1, 2 })
        {
            events.Add(Ev.Http($"https://x.test/items/{id}", id * 1000, 10));
            events.Add(Ev.Http($"https://x.test/items/{id}", (id * 1000) + 100, 10));
        }

        var report = Analyze(events);
        var finding = Assert.Single(Findings(report, "BP3001"));
        Assert.Equal("GET https://x.test/items/{id}", finding.Subject);
        Assert.StartsWith("2 URLs of", finding.Title, StringComparison.Ordinal);
        Assert.Equal(2, finding.Impact!.Occurrences);
        Assert.Equal(report.Findings.Count, report.Findings.Select(f => f.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void BP3002_ReportsNPlusOneWithinOperation()
    {
        var events = Enumerable.Range(1, 8).Select(i => (ProfilerEvent)Ev.Http($"https://x.test/api/products/{i}", i, 10, operation: 3)).ToList();
        var finding = Assert.Single(Findings(Analyze(events), "BP3002"));
        Assert.Equal(DiagnosticConfidence.High, finding.Confidence);
        Assert.Equal("GET https://x.test/api/products/{id}", finding.Subject);
    }

    [Fact]
    public void BP3002_FallsBackToTemporalGroupingWithMediumConfidence()
    {
        var events = Enumerable.Range(1, 6).Select(i => (ProfilerEvent)Ev.Http($"https://x.test/api/products/{i}", i * 10, 10)).ToList();
        Assert.Equal(DiagnosticConfidence.Medium, Assert.Single(Findings(Analyze(events), "BP3002")).Confidence);
    }

    [Fact]
    public void BP3003_ReportsSequentialChainsWithLowConfidence()
    {
        var finding = Assert.Single(Findings(Analyze([Ev.Http("https://x.test/a", 0, 100, 1), Ev.Http("https://x.test/b", 101, 100, 1), Ev.Http("https://x.test/c", 202, 100, 1)]), "BP3003"));
        Assert.Equal(DiagnosticConfidence.Low, finding.Confidence);
        Assert.Equal(200, finding.Impact!.TotalMs!.Value, 0);
    }

    [Fact]
    public void BP3004_ReportsSlowEndpoint()
    {
        var events = Enumerable.Range(0, 5).Select(i => (ProfilerEvent)Ev.Http("https://x.test/slow", i * 5000, 1500)).ToList();
        events.Add(Ev.Http("https://x.test/fast", 0, 10));
        var finding = Assert.Single(Findings(Analyze(events), "BP3004"));
        Assert.Equal("GET https://x.test/slow", finding.Subject);
    }

    [Fact]
    public void BP3005_ReportsRepeatedFailuresAndRetries()
    {
        var events = Enumerable.Range(0, 4).Select(i => (ProfilerEvent)Ev.Http("https://x.test/flaky", i * 100, 10, status: 503)).ToList();
        var finding = Assert.Single(Findings(Analyze(events), "BP3005"));
        Assert.Contains("503", finding.Summary, StringComparison.Ordinal);
        Assert.Contains("3 were followed", finding.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BP3006_ReportsPerInstanceInitializationRequests()
    {
        var events = Enumerable.Range(1, 6).Select(i => (ProfilerEvent)Ev.Http("https://x.test/api/settings", i, 10, origin: "App.Row", lifecycle: LifecycleMethod.OnInitializedAsync, instance: i)).ToList();
        var finding = Assert.Single(Findings(Analyze(events), "BP3006"));
        Assert.Equal("App.Row", finding.ComponentType);
    }

    [Fact]
    public void BP4001_ReportsExcessiveInteropPerOperation()
    {
        var events = new List<ProfilerEvent> { Ev.Handler("App.Chart", "Draw", 0, 50, 1) };
        events.AddRange(Enumerable.Range(0, 60).Select(i => (ProfilerEvent)Ev.Interop("chart.point", i * 20, 0.1, operation: 1)));
        var findings = Findings(Analyze(events), "BP4001");
        Assert.Contains(findings, f => f.Subject!.Contains("Chart.Draw", StringComparison.Ordinal));
        Assert.Single(Findings(Analyze(events), "BP4002"));
    }

    [Fact]
    public void BP4003_ReportsLargePayloadsAsEstimates()
    {
        var finding = Assert.Single(Findings(Analyze([Ev.Interop("grid.load", 0, 5, payload: 500_000)]), "BP4003"));
        Assert.Contains(finding.Evidence, e => e.Quality == ValueQuality.Estimated);
    }

    [Fact]
    public void BP4004_ReportsSlowInterop()
    {
        var events = Enumerable.Range(0, 5).Select(i => (ProfilerEvent)Ev.Interop("map.render", i * 1000, 300)).ToList();
        Assert.Single(Findings(Analyze(events), "BP4004"));
    }

    [Fact]
    public void BP4005_ReportsInteropAfterDisposal()
    {
        var report = Analyze(
        [
            new ComponentDisposedEvent { ComponentType = "App.Ticker", ComponentInstanceId = 9, Timestamp = Ev.T(100) },
            Ev.Interop("ticker.update", 50, instance: 9, origin: "App.Ticker"),
            Ev.Interop("ticker.update", 200, instance: 9, origin: "App.Ticker"),
        ]);
        var finding = Assert.Single(Findings(report, "BP4005"));
        Assert.Single(finding.RelatedEventIds);
    }

    [Fact]
    public void BP5001_ReportsHighGcPauseRatio()
    {
        var events = Enumerable.Range(0, 10).Select(i => (ProfilerEvent)Ev.Runtime(i * 1000, 1000, 1_000_000, gen0: 5, gen2: 1, pauseMs: 100)).ToList();
        var finding = Assert.Single(Findings(Analyze(events), "BP5001"));
        Assert.Contains("10%", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void BP5001_DoesNotPresentMissingPauseDataAsMeasured()
    {
        // WebAssembly samples carry no pause time; only the generation 2 rate is evaluated.
        var events = Enumerable.Range(0, 10).Select(i =>
        {
            var sample = Ev.Runtime(i * 1000, 1000, 1_000_000, gen0: 5, gen2: 1);
            sample.GcPauseTicks = null;
            return (ProfilerEvent)sample;
        }).ToList();
        var report = Analyze(events);
        var finding = Assert.Single(Findings(report, "BP5001"));
        Assert.Contains("generation 2", finding.Title, StringComparison.Ordinal);
        Assert.Null(finding.Impact!.TotalMs);
        Assert.Equal(ValueQuality.Unavailable, finding.Evidence.Single(e => e.Label == "GC pause share").Quality);
    }

    [Fact]
    public void BP5002_ReportsAllocationSpikesWithTemporalOverlapOnly()
    {
        var events = Enumerable.Range(0, 20).Select(i => (ProfilerEvent)Ev.Runtime(i * 1000, 1000, i == 10 ? 200_000_000 : 1_000_000)).ToList();
        events.Add(Ev.Handler("App.Report", "Generate", 10_200, 300, 1));
        var finding = Assert.Single(Findings(Analyze(events), "BP5002"));
        Assert.Contains("Report.Generate", finding.Explanation, StringComparison.Ordinal);
        Assert.Contains("temporal", finding.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DiagnosticConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void BP5003_ReportsHeapGrowthAcrossNavigations()
    {
        var events = new List<ProfilerEvent>();
        for (var i = 0; i < 8; i++)
        {
            events.Add(new NavigationEvent { Route = i % 2 == 0 ? "/a" : "/b", ComponentType = "App.Page", Timestamp = Ev.T(i * 1000), Duration = Ev.D(5) });
            events.Add(Ev.Runtime((i * 1000) + 10, 100, 1000, heap: 10_000_000 + (i * 2_000_000), gen2: 1));
        }

        var finding = Assert.Single(Findings(Analyze(events), "BP5003"));
        Assert.Equal(DiagnosticConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void BP5003_IgnoresFlatHeap()
    {
        var events = new List<ProfilerEvent>();
        for (var i = 0; i < 8; i++)
        {
            events.Add(new NavigationEvent { Route = "/a", Timestamp = Ev.T(i * 1000) });
            events.Add(Ev.Runtime((i * 1000) + 10, 100, 1000, heap: 10_000_000 + ((i % 2) * 100_000)));
        }

        Assert.Empty(Findings(Analyze(events), "BP5003"));
    }

    [Fact]
    public void BP6001_IdentifiesNetworkBoundRoundTrips()
    {
        var events = Enumerable.Range(0, 10).Select(i => (ProfilerEvent)Ev.Batch(i * 1000, 400, 1, roundTrip: true, circuit: "c1")).ToList();
        var finding = Assert.Single(Findings(Analyze(events), "BP6001"));
        Assert.Contains("network", finding.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Findings(Analyze(events), "BP1007"));
    }

    [Fact]
    public void BP6002_ReportsUnstableCircuits()
    {
        var events = new List<ProfilerEvent>();
        for (var i = 0; i < 3; i++)
        {
            events.Add(new CircuitEvent { CircuitId = "c1", Change = CircuitChange.ConnectionDown, Timestamp = Ev.T(i * 10) });
            events.Add(new CircuitEvent { CircuitId = "c1", Change = CircuitChange.ConnectionUp, Timestamp = Ev.T((i * 10) + 5) });
        }

        Assert.Single(Findings(Analyze(events), "BP6002"));
    }

    [Fact]
    public void BP6003_ReportsSlowCircuitActivities()
    {
        Assert.Single(Findings(Analyze([new CircuitActivityEvent { CircuitId = "c", ActivityName = "DispatchBrowserEvent", Timestamp = Ev.T(0), Duration = Ev.D(800) }]), "BP6003"));
    }

    [Fact]
    public void BP6004_ReportsHighBatchVolume()
    {
        var events = Enumerable.Range(0, 200).Select(i => (ProfilerEvent)Ev.Batch(i * 20, 1, 1, roundTrip: true, circuit: "c1")).ToList();
        Assert.Single(Findings(Analyze(events), "BP6004"));
    }

    [Fact]
    public void BP6005_ReportsThreadPoolSaturation()
    {
        var events = Enumerable.Range(0, 3).Select(i => (ProfilerEvent)Ev.Runtime(i * 1000, 1000, 10, queue: 500)).ToList();
        events.Add(Ev.Handler("App.X", "Y", 500, 400, 1));
        var finding = Assert.Single(Findings(Analyze(events), "BP6005"));
        Assert.Equal(DiagnosticConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void BP7001_BreaksDownSlowInteractions()
    {
        var finding = Assert.Single(Findings(Analyze([new BrowserInteractionEvent { EventType = "click", Timestamp = Ev.T(0), Duration = Ev.D(400), InputDelayMs = 10, ProcessingMs = 350, PresentationDelayMs = 40 }]), "BP7001"));
        Assert.Contains("processing", finding.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BP7002_ReportsRepeatedLongTasks()
    {
        var events = Enumerable.Range(0, 4).Select(i => (ProfilerEvent)new BrowserLongTaskEvent { EntryType = "long-animation-frame", Timestamp = Ev.T(i * 1000), Duration = Ev.D(300), BlockingMs = 250 }).ToList();
        Assert.Single(Findings(Analyze(events), "BP7002"));
    }

    [Fact]
    public void SuppressionsAndSeverityOverridesAreApplied()
    {
        var (host, session, _) = TestHost.WithSession(o => o.Diagnostics.Rules["BP3004"] = new RuleSettings { Severity = DiagnosticSeverity.Info });
        host.Emit(Ev.Http("https://x.test/slow", 0, 5000));
        host.Diagnostics.Suppressions.Add(new FindingSuppression { Fingerprint = "BP3004:GET https://x.test/slow", Kind = SuppressionKind.Expected });
        var finding = Assert.Single(host.Diagnostics.Analyze(session).Findings);
        Assert.Equal(DiagnosticSeverity.Info, finding.Severity);
        Assert.Equal(SuppressionKind.Expected, finding.Suppression!.Kind);
    }

    [Fact]
    public void DisabledRulesDoNotRun()
    {
        var (host, session, _) = TestHost.WithSession(o => o.Diagnostics.Rules["BP3004"] = new RuleSettings { Enabled = false });
        host.Emit(Ev.Http("https://x.test/slow", 0, 5000));
        var report = host.Diagnostics.Analyze(session);
        Assert.Empty(report.Findings);
        Assert.DoesNotContain(report.Rules, r => r.Id == "BP3004");
    }

    [Fact]
    public void FailingRuleIsIsolated()
    {
        var (host, session, _) = TestHost.WithSession(rules: [new ThrowingRule(), new SlowEndpointRule()]);
        host.Emit(Ev.Http("https://x.test/slow", 0, 5000));
        var report = host.Diagnostics.Analyze(session);
        Assert.Single(report.Findings);
        Assert.Contains("TEST1", report.RuleErrors.Keys);
    }

    [Fact]
    public void AnalysisIsDeterministic()
    {
        var events = Enumerable.Range(1, 8).Select(i => (ProfilerEvent)Ev.Http($"https://x.test/api/p/{i}", i, 10, operation: 1)).ToList();
        var a = Analyze(events).Findings.Select(f => f.Fingerprint + f.Summary);
        var b = Analyze(events).Findings.Select(f => f.Fingerprint + f.Summary);
        Assert.Equal(a, b);
    }

    private sealed class LinkProvider(string link) : IDiagnosticRecommendationProvider
    {
        public void Augment(DiagnosticFinding finding) => finding.DocumentationUrl = link;
    }

    private sealed class ThrowingRule : IDiagnosticRule
    {
        public DiagnosticRuleDescriptor Descriptor { get; } = new() { Id = "TEST1", Title = "t", Description = "t" };

        public IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context) => throw new InvalidOperationException("rule failure");
    }
}
