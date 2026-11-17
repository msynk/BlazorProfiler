using BlazorProfiler.Analysis;
using BlazorProfiler.Analysis.Rules;
using BlazorProfiler.Events;
using BlazorProfiler.Export;

namespace BlazorProfiler.Core.Tests;

/// <summary>
/// Rules added after the second competitive study: component churn (BP1008), forced layout (BP7006), frustration signals
/// (BP7007), long animation frame script attribution in BP7002, and identified retainers in BP5004.
/// </summary>
public class AttributionRuleTests
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

    private static ComponentCreatedEvent Created(string type, long instance, double atMs, long operation) =>
        new() { ComponentType = type, ComponentInstanceId = instance, Timestamp = Ev.T(atMs), OperationId = operation, Source = InstrumentationSource.ComponentActivator };

    private static ComponentDisposedEvent Disposed(string type, long instance, double atMs, long operation) =>
        new() { ComponentType = type, ComponentInstanceId = instance, Timestamp = Ev.T(atMs), OperationId = operation, Source = InstrumentationSource.ComponentInstrumentation };

    private static BrowserLongTaskEvent Frame(double atMs, double durationMs, double forcedMs = 0, params LongFrameScript[] scripts) => new()
    {
        EntryType = "long-animation-frame",
        Timestamp = Ev.T(atMs),
        Duration = Ev.D(durationMs),
        BlockingMs = Math.Max(0, durationMs - 50),
        ForcedStyleAndLayoutMs = forcedMs,
        Scripts = scripts.Length == 0 ? null : [.. scripts],
        Script = scripts.FirstOrDefault()?.SourceUrl,
    };

    private static LongFrameScript Script(string function, string url, double durationMs, double forcedMs = 0, string invokerType = "event-listener") => new()
    {
        Invoker = "DIV.onclick",
        InvokerType = invokerType,
        SourceUrl = url,
        SourceFunctionName = function,
        DurationMs = durationMs,
        ForcedStyleAndLayoutMs = forcedMs,
    };

    [Fact]
    public void BuiltInRulesIncludeTheNewRules()
    {
        var ids = BuiltInRules.Create().Select(r => r.Descriptor.Id).ToHashSet();
        Assert.Contains("BP1008", ids);
        Assert.Contains("BP7006", ids);
        Assert.Contains("BP7007", ids);
        Assert.Contains("BP7008", ids);
        Assert.Contains("BP3007", ids);
        Assert.Contains("BP5005", ids);
        Assert.Contains("BP5006", ids);
        Assert.Contains("BP5007", ids);
        Assert.Contains("BP7009", ids);
    }

    [Fact]
    public void BP1008_ReportsComponentsRecreatedWithinOperations()
    {
        var events = new List<ProfilerEvent>();
        long id = 1;
        for (var op = 1; op <= 2; op++)
        {
            events.Add(Ev.Handler("App.Pages.Orders", "Sort", op * 1000, 30, operation: op));
            for (var i = 0; i < 12; i++)
            {
                events.Add(Disposed("App.Shared.OrderRow", id, (op * 1000) + 5, op));
                events.Add(Created("App.Shared.OrderRow", id + 1000, (op * 1000) + 6, op));
                id++;
            }
        }

        var finding = Assert.Single(Findings(Analyze(events), "BP1008"));
        Assert.Equal("App.Shared.OrderRow", finding.ComponentType);
        Assert.Equal(DiagnosticConfidence.High, finding.Confidence);
        Assert.Contains(finding.Evidence, e => e.Label == "Instances recreated (total)" && e.Value == "24");
        Assert.Contains("@key", finding.Recommendation, StringComparison.Ordinal);
    }

    [Fact]
    public void BP1008_IgnoresSmallChurnAndCreationsWithoutDisposals()
    {
        var events = new List<ProfilerEvent> { Ev.Handler("App.Pages.Orders", "Add", 0, 10, operation: 1) };
        for (var i = 0; i < 4; i++)
        {
            events.Add(Disposed("App.Row", i + 1, 5, 1));
            events.Add(Created("App.Row", i + 100, 6, 1));
        }

        // Many creations, but nothing disposed: a list that grew, not churn.
        for (var i = 0; i < 50; i++)
        {
            events.Add(Created("App.Cell", i + 500, 10, 1));
        }

        Assert.Empty(Findings(Analyze(events), "BP1008"));
    }

    [Fact]
    public void BP7006_AttributesForcedLayoutToTheScriptThatCausedIt()
    {
        var events = new List<ProfilerEvent>
        {
            Frame(100, 180, 90, Script("measureRows", "https://localhost/js/grid.js?v=3", 120, 90), Script("dispatch", "https://localhost/_framework/blazor.web.js", 40)),
            Frame(400, 120, 45, Script("measureRows", "https://localhost/js/grid.js?v=3", 80, 45)),
            Frame(800, 90, 2, Script("other", "https://localhost/js/other.js", 60, 2)),
            Ev.Interop("grid.measureRows", 110, 100),
        };

        var finding = Assert.Single(Findings(Analyze(events), "BP7006"));
        Assert.Equal(DiagnosticConfidence.High, finding.Confidence);
        Assert.Contains("measureRows (grid.js)", finding.Title, StringComparison.Ordinal);
        Assert.Contains(finding.Evidence, e => e.Label == "Frames with forced layout" && e.Value == "2");
        Assert.Contains(finding.Evidence, e => e.Label == "JS interop calls running during these frames" && e.Value.Contains("grid.measureRows", StringComparison.Ordinal));
    }

    [Fact]
    public void BP7006_BlamesTheInteropCallWhenTheBrowserAttributesLayoutToBlazor()
    {
        // In WebAssembly the JavaScript called through interop runs inside Blazor's event listener, so the browser attributes
        // the forced layout to blazor.web.js; the interop call running during the frame is the actual culprit.
        var events = new List<ProfilerEvent>
        {
            Frame(100, 220, 200, new LongFrameScript { Invoker = "#document.onclick", InvokerType = "event-listener", SourceUrl = "https://localhost/_framework/blazor.web.js", SourceFunctionName = "onGlobalEvent", DurationMs = 215, ForcedStyleAndLayoutMs = 200 }),
            Ev.Interop("import", 90, 2),
            Ev.Interop("thrashLayout", 101, 210),
        };

        var finding = Assert.Single(Findings(Analyze(events), "BP7006"));
        Assert.StartsWith("JS interop call thrashLayout forces synchronous layout", finding.Title, StringComparison.Ordinal);
        Assert.Equal(DiagnosticConfidence.Medium, finding.Confidence);
        Assert.Equal("interop:thrashLayout", finding.Subject);
        Assert.Contains(finding.Evidence, e => e.Label == "Scripts that forced layout" && e.Value.Contains("[Blazor/.NET entry point]", StringComparison.Ordinal));
    }

    [Fact]
    public void BP7006_PrefersTheInteropCallOverAMinorApplicationScript()
    {
        // blazor.web.js forced 400 ms during an interop call; an analytics script forced 2 ms in the same frame.
        var events = new List<ProfilerEvent>
        {
            Frame(100, 450, 402,
                new LongFrameScript { Invoker = "#document.onclick", InvokerType = "event-listener", SourceUrl = "https://localhost/_framework/blazor.web.js", DurationMs = 420, ForcedStyleAndLayoutMs = 400 },
                Script("track", "https://cdn.example/analytics.js", 10, 2)),
            Ev.Interop("grid.measureAll", 101, 440),
        };

        var finding = Assert.Single(Findings(Analyze(events), "BP7006"));
        Assert.StartsWith("JS interop call grid.measureAll", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void BP7006_IgnoresForcedLayoutBelowTheThreshold()
    {
        var events = new List<ProfilerEvent> { Frame(100, 180, 10, Script("f", "https://localhost/a.js", 100, 10)) };
        Assert.Empty(Findings(Analyze(events), "BP7006"));
    }

    [Fact]
    public void BP7002_UsesScriptAttributionAndReportsTheFrameworkShare()
    {
        var events = new List<ProfilerEvent>();
        for (var i = 0; i < 4; i++)
        {
            events.Add(Frame(i * 1000, 200, 0, Script("invokeMethod", "https://localhost/_framework/dotnet.runtime.js", 150, invokerType: "user-callback"), Script("chart", "https://localhost/js/chart.js", 30)));
        }

        var finding = Assert.Single(Findings(Analyze(events), "BP7002"));
        Assert.Contains(finding.Evidence, e => e.Label == "Top scripts" && e.Value.StartsWith("invokeMethod (dotnet.runtime.js)", StringComparison.Ordinal) && e.Value.Contains("Blazor/.NET runtime", StringComparison.Ordinal));
        Assert.Contains(finding.Evidence, e => e.Label.StartsWith("Share of attributed script time", StringComparison.Ordinal) && e.Value == "83.3%");
    }

    [Fact]
    public void LongFrameScriptStats_AggregatesByLabelAndDetectsFrameworkScripts()
    {
        var frames = new[]
        {
            Frame(0, 100, 0, Script("a", "https://x/app.js", 60), Script("b", "https://x/_framework/blazor.web.js", 30)),
            Frame(200, 100, 0, Script("a", "https://x/app.js", 70)),
        };
        frames[0].Id = 1;
        frames[1].Id = 2;
        var stats = LongFrameScriptStats.Aggregate(frames);
        Assert.Equal("a (app.js)", stats[0].Label);
        Assert.Equal(2, stats[0].Frames);
        Assert.Equal(130, stats[0].TotalMs);
        Assert.False(stats[0].IsFramework);
        Assert.True(stats[1].IsFramework);
        Assert.Equal("(unknown script)", new LongFrameScript().Label);
        Assert.Equal("BUTTON.onclick (x.js)", new LongFrameScript { Invoker = "BUTTON.onclick", SourceUrl = "https://h/x.js#y" }.Label);
    }

    [Fact]
    public void BP7007_ReportsRageClicksWithTheSlowHandlerThatCausedThem()
    {
        var events = new List<ProfilerEvent>
        {
            Ev.Handler("App.Pages.Checkout", "PayAsync", 100, 2_500, operation: 3),
            new BrowserFrustrationEvent { Signal = FrustrationSignal.RageClick, ClickCount = 5, TargetTag = "button", Target = "button#pay.btn", Timestamp = Ev.T(400), Duration = Ev.D(600) },
        };

        var finding = Assert.Single(Findings(Analyze(events), "BP7007"));
        Assert.Equal(DiagnosticSeverity.High, finding.Severity);
        Assert.Contains("button#pay.btn", finding.Title, StringComparison.Ordinal);
        Assert.Contains("PayAsync", finding.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BP7007_ReportsRepeatedDeadClicksWithoutHandlersAndIgnoresSingleOnes()
    {
        var dead = new List<ProfilerEvent>
        {
            new BrowserFrustrationEvent { Signal = FrustrationSignal.DeadClick, TargetTag = "button", Target = "button.save", Timestamp = Ev.T(100) },
            new BrowserFrustrationEvent { Signal = FrustrationSignal.DeadClick, TargetTag = "button", Target = "button.save", Timestamp = Ev.T(5_000) },
            new BrowserFrustrationEvent { Signal = FrustrationSignal.DeadClick, TargetTag = "a", Target = "a.help", Timestamp = Ev.T(9_000) },
        };

        var finding = Assert.Single(Findings(Analyze(dead), "BP7007"));
        Assert.Equal(DiagnosticSeverity.Low, finding.Severity);
        Assert.Equal("button.save", finding.Subject);
        Assert.Contains("No .NET event handler ran", finding.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BP7008_ReportsInteractionsWhoseContentPaintsLongAfterTheFirstPaint()
    {
        var events = new List<ProfilerEvent>
        {
            new BrowserInteractionEvent { EventType = "click", TargetTag = "button", Timestamp = Ev.T(100), Duration = Ev.D(24), ContentPaintMs = 1_250 },
            new BrowserInteractionEvent { EventType = "click", TargetTag = "button", Timestamp = Ev.T(5_000), Duration = Ev.D(32), ContentPaintMs = 900 },
            Ev.Handler("App.Pages.Report", "LoadAsync", 101, 1_220, operation: 4),
            Ev.Http("https://localhost/api/report", 110, 1_150, operation: 4),

            // Fast content, or content as fast as the first paint, is not reported.
            new BrowserInteractionEvent { EventType = "keydown", TargetTag = "input", Timestamp = Ev.T(9_000), Duration = Ev.D(150), ContentPaintMs = 180 },
            new BrowserInteractionEvent { EventType = "click", TargetTag = "a", Timestamp = Ev.T(9_500), Duration = Ev.D(300), ContentPaintMs = 320 },
        };

        var finding = Assert.Single(Findings(Analyze(events), "BP7008"));
        Assert.Equal("click on <button>", finding.Subject);
        Assert.Contains("1.25 s", finding.Title, StringComparison.Ordinal);
        Assert.Contains("HTTP request(s) ran meanwhile", finding.Summary, StringComparison.Ordinal);
        Assert.Contains(finding.Evidence, e => e.Label == ".NET handlers during the wait" && e.Value.Contains("Report.LoadAsync", StringComparison.Ordinal));
        Assert.Empty(Findings(Analyze([new BrowserInteractionEvent { EventType = "click", Timestamp = Ev.T(0), Duration = Ev.D(400) }]), "BP7008"));
    }

    [Fact]
    public void BP3007_ReportsRequestsRepeatedAfterPrerenderingAndBP3001LeavesThemAlone()
    {
        static HttpRequestEvent Request(double at, string? circuit) => new()
        {
            Method = "GET",
            Url = "https://localhost/api/products",
            UrlHash = 42,
            Endpoint = "GET https://localhost/api/products",
            Timestamp = Ev.T(at),
            Duration = Ev.D(80),
            StatusCode = 200,
            CircuitId = circuit,
            OriginComponentType = "App.Pages.Products",
            OriginLifecycleMethod = LifecycleMethod.OnInitializedAsync,
            Source = InstrumentationSource.HttpMessageHandler,
        };

        var events = new List<ProfilerEvent> { Request(0, null), Request(900, "circuit-1") };
        var report = Analyze(events);
        var finding = Assert.Single(Findings(report, "BP3007"));
        Assert.Equal("App.Pages.Products", finding.ComponentType);
        Assert.Contains("PersistentState", finding.Recommendation, StringComparison.Ordinal);
        Assert.Empty(Findings(report, "BP3001"));

        // Two interactive requests are an ordinary duplicate, not a prerender double fetch.
        var interactive = Analyze([Request(0, "circuit-1"), Request(900, "circuit-1")]);
        Assert.Empty(Findings(interactive, "BP3007"));
        Assert.Single(Findings(interactive, "BP3001"));
    }

    [Fact]
    public void BP5005_ReportsAllocationHeavyRendering()
    {
        var events = Enumerable.Range(0, 12).Select(i => (ProfilerEvent)new ComponentRenderEvent
        {
            ComponentType = "App.Grid",
            ComponentInstanceId = 1,
            Timestamp = Ev.T(i * 100),
            Duration = Ev.D(3),
            AllocatedBytes = 120_000,
            Source = InstrumentationSource.ComponentInstrumentation,
        }).ToList();
        events.AddRange(Enumerable.Range(0, 12).Select(i => (ProfilerEvent)new ComponentRenderEvent { ComponentType = "App.Row", ComponentInstanceId = 2, Timestamp = Ev.T(i * 100), Duration = Ev.D(1), AllocatedBytes = 800, Source = InstrumentationSource.ComponentInstrumentation }));

        var finding = Assert.Single(Findings(Analyze(events), "BP5005"));
        Assert.Equal("App.Grid", finding.ComponentType);
        Assert.Contains("117.2 KB", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void BP5006_ReportsFrequentExceptionsWithTheirTypes()
    {
        var events = new List<ProfilerEvent>
        {
            Ev.Handler("App.Pages.Search", "OnInput", 100, 300, operation: 3),
            new RuntimeSampleEvent { Timestamp = Ev.T(0), Duration = Ev.D(1_000), Exceptions = 3 },
            new RuntimeSampleEvent { Timestamp = Ev.T(1_000), Duration = Ev.D(1_000), Exceptions = 2 },
        };
        events[1] = new RuntimeSampleEvent
        {
            Timestamp = Ev.T(0),
            Duration = Ev.D(1_000),
            Exceptions = 60,
            ExceptionTypes = new() { ["System.Threading.Tasks.TaskCanceledException"] = 55, ["System.FormatException"] = 5 },
        };

        var finding = Assert.Single(Findings(Analyze(events), "BP5006"));
        Assert.StartsWith("TaskCanceledException thrown up to 60 times per second", finding.Title, StringComparison.Ordinal);
        Assert.Contains("Most are cancellations", finding.Summary, StringComparison.Ordinal);
        Assert.Contains(finding.Evidence, e => e.Label == "Operations running at the time" && e.Value.Contains("Search.OnInput", StringComparison.Ordinal));

        // A few exceptions per second are not reported.
        Assert.Empty(Findings(Analyze([new RuntimeSampleEvent { Timestamp = Ev.T(0), Duration = Ev.D(1_000), Exceptions = 4 }]), "BP5006"));
    }

    [Fact]
    public void BP5007_ReportsActiveTimersGrowingWithNavigation()
    {
        var events = new List<ProfilerEvent>();
        for (var i = 0; i < 8; i++)
        {
            events.Add(new NavigationEvent { Route = "/leaky", Timestamp = Ev.T(i * 1_000) });
            events.Add(new RuntimeSampleEvent { Timestamp = Ev.T((i * 1_000) + 10), Duration = Ev.D(100), ActiveTimers = 12 + (i * 2) });
        }

        var finding = Assert.Single(Findings(Analyze(events), "BP5007"));
        Assert.Contains("about 2 per navigation", finding.Title, StringComparison.Ordinal);

        // A stable number of timers is not reported.
        var stable = new List<ProfilerEvent>();
        for (var i = 0; i < 8; i++)
        {
            stable.Add(new NavigationEvent { Route = "/ok", Timestamp = Ev.T(i * 1_000) });
            stable.Add(new RuntimeSampleEvent { Timestamp = Ev.T((i * 1_000) + 10), Duration = Ev.D(100), ActiveTimers = 12 + (i % 2) });
        }

        Assert.Empty(Findings(Analyze(stable), "BP5007"));
    }

    [Fact]
    public void BP5006_IgnoresASingleModestBurst()
    {
        // One sample at 15 per second (a startup burst) is not reported; two such samples are.
        Assert.Empty(Findings(Analyze([new RuntimeSampleEvent { Timestamp = Ev.T(0), Duration = Ev.D(1_000), Exceptions = 15 }]), "BP5006"));
        var repeated = Findings(Analyze(
        [
            new RuntimeSampleEvent { Timestamp = Ev.T(0), Duration = Ev.D(1_000), Exceptions = 15, ExceptionTypes = new() { ["(other types)"] = 10, ["System.FormatException"] = 5 } },
            new RuntimeSampleEvent { Timestamp = Ev.T(1_000), Duration = Ev.D(1_000), Exceptions = 15, ExceptionTypes = new() { ["(other types)"] = 10, ["System.FormatException"] = 5 } },
        ]), "BP5006");

        // "(other types)" is never named as the culprit.
        Assert.StartsWith("FormatException thrown", Assert.Single(repeated).Title, StringComparison.Ordinal);
    }

    [Fact]
    public void BP5007_FindsTheComponentWhoseInstancesAddTimersWithoutNavigationEvents()
    {
        // Enhanced navigation in a Blazor Web App reports no navigation events: each new LeakyWidget adds a timer.
        var events = new List<ProfilerEvent>();
        for (var i = 0; i < 7; i++)
        {
            events.Add(new ComponentCreatedEvent { ComponentType = "App.LeakyWidget", ComponentInstanceId = i + 1, Timestamp = Ev.T(i * 2_000), Source = InstrumentationSource.ComponentActivator });
            events.Add(new ComponentCreatedEvent { ComponentType = "App.Row", ComponentInstanceId = 100 + i, Timestamp = Ev.T((i * 2_000) + 1_000), Source = InstrumentationSource.ComponentActivator });
            events.Add(new RuntimeSampleEvent { Timestamp = Ev.T((i * 2_000) + 100), Duration = Ev.D(500), ActiveTimers = 10 + i });
            events.Add(new RuntimeSampleEvent { Timestamp = Ev.T((i * 2_000) + 1_100), Duration = Ev.D(500), ActiveTimers = 10 + i });
        }

        // Rows are created once per widget here too, so both types match the growth: they are listed as candidates and the
        // finding says the attribution is by time (Medium confidence).
        var finding = Assert.Single(Findings(Analyze(events), "BP5007"));
        var candidates = finding.Evidence.Single(e => e.Label == "Component types whose instances match the growth");
        Assert.Contains("LeakyWidget", candidates.Value, StringComparison.Ordinal);
        Assert.Equal(ValueQuality.Inferred, candidates.Quality);
        Assert.Equal(DiagnosticConfidence.Medium, finding.Confidence);

        // Components created while their circuit was starting (a page load) are not blamed for the circuit's own timers.
        var circuits = new List<ProfilerEvent>();
        for (var i = 0; i < 7; i++)
        {
            var circuit = "c" + i;
            circuits.Add(new CircuitEvent { Change = CircuitChange.Opened, CircuitId = circuit, Timestamp = Ev.T(i * 2_000) });
            circuits.Add(new ComponentCreatedEvent { ComponentType = "App.Routes", ComponentInstanceId = i + 1, CircuitId = circuit, Timestamp = Ev.T((i * 2_000) + 50), Source = InstrumentationSource.ComponentActivator });
            circuits.Add(new RuntimeSampleEvent { Timestamp = Ev.T((i * 2_000) + 100), Duration = Ev.D(500), ActiveTimers = 10 + (3 * i) });
        }

        Assert.Empty(Findings(Analyze(circuits), "BP5007"));
    }

    [Fact]
    public void BP7009_ReportsExcessiveDomSizeAndBP7005IgnoresIt()
    {
        var events = new List<ProfilerEvent>
        {
            new BrowserVitalEvent { Name = "DOM", Value = 600, Timestamp = Ev.T(0) },
            new BrowserVitalEvent { Name = "DOM", Value = 5_200, Timestamp = Ev.T(1_000) },
            new BrowserVitalEvent { Name = "DOM", Value = 900, Timestamp = Ev.T(2_000) },
        };
        for (var i = 0; i < 60; i++)
        {
            events.Add(new ComponentCreatedEvent { ComponentType = "App.GridRow", ComponentInstanceId = i + 1, Timestamp = Ev.T(900), Source = InstrumentationSource.ComponentActivator });
        }

        var (host, session, _) = TestHost.WithSession();
        foreach (var e in events)
        {
            host.Emit(e);
        }

        var report = host.Diagnostics.Analyze(session);
        var finding = Assert.Single(report.Findings, f => f.RuleId == "BP7009");
        Assert.Contains("5,200", finding.Title, StringComparison.Ordinal);
        Assert.Contains("GridRow ×60", finding.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Findings, f => f.RuleId == "BP7005");

        // The summary keeps the largest DOM size, not the latest.
        var dom = session.GetSummary().Vitals["DOM"];
        Assert.Equal(5_200, dom.Value);
        Assert.Equal("poor", dom.Rating);
        Assert.Equal(DiagnosticSeverity.Low, Assert.Single(Findings(Analyze([new BrowserVitalEvent { Name = "DOM", Value = 1_000, Timestamp = Ev.T(0) }]), "BP7009")).Severity);
        Assert.Empty(Findings(Analyze([new BrowserVitalEvent { Name = "DOM", Value = 700, Timestamp = Ev.T(0) }]), "BP7009"));
    }

    [Fact]
    public void BP5004_UsesIdentifiedRetainersAsEvidenceAndRaisesConfidence()
    {
        var events = new List<ProfilerEvent>
        {
            new ComponentRetainedEvent
            {
                ComponentType = "App.Pages.Counter",
                ComponentInstanceId = 5,
                ReleasedTimestamp = Ev.T(0),
                Timestamp = Ev.T(5_000),
                SurvivedCollections = 2,
                RetainedBy = ["AppState.OnChange → Counter.StateHasChanged — subscribed by the component and never removed; unsubscribe in Dispose"],
            },
        };

        var finding = Assert.Single(Findings(Analyze(events), "BP5004"));

        // A single instance would normally be bounded retention (Info), but an identified event subscription is a leak.
        Assert.NotEqual(DiagnosticSeverity.Info, finding.Severity);
        Assert.Equal(DiagnosticConfidence.High, finding.Confidence);
        Assert.Contains(finding.Evidence, e => e.Label == "Likely retained by" && e.Value.StartsWith("AppState.OnChange", StringComparison.Ordinal));
        Assert.StartsWith("AppState.OnChange", finding.CodeHint, StringComparison.Ordinal);
    }

    [Fact]
    public void BP5004_DoesNotTreatATentativeRetainerAsALeak()
    {
        var events = new List<ProfilerEvent>
        {
            new ComponentRetainedEvent
            {
                ComponentType = "App.Pages.Clock",
                ComponentInstanceId = 5,
                ReleasedTimestamp = Ev.T(0),
                Timestamp = Ev.T(5_000),
                SurvivedCollections = 2,
                RetainedBy = [ComponentRetainedEvent.TentativePrefix + "Clock._timer (Timer) — if it was not disposed, ..."],
            },
        };

        // A single retained child with only a possible retainer stays bounded retention, but the hint is shown.
        var finding = Assert.Single(Findings(Analyze(events), "BP5004"));
        Assert.Equal(DiagnosticSeverity.Info, finding.Severity);
        Assert.Contains(finding.Evidence, e => e.Label == "Likely retained by" && e.Value.StartsWith("Possibly: Clock._timer", StringComparison.Ordinal));
        Assert.True(ComponentRetainedEvent.IsTentative(finding.Evidence.First(e => e.Label == "Likely retained by").Value));
        Assert.False(ComponentRetainedEvent.IsTentative("AppState.OnChange → Clock.StateHasChanged"));
    }

    [Fact]
    public async Task NewEventsRoundTripThroughExportAndImport()
    {
        var (host, session, _) = TestHost.WithSession();
        host.Emit(Frame(0, 120, 40, Script("f", "https://localhost/a.js", 100, 40)));
        host.Emit(new BrowserFrustrationEvent { Signal = FrustrationSignal.RageClick, ClickCount = 4, TargetTag = "button", Target = "button#x", Timestamp = Ev.T(10) });
        host.Emit(new ComponentRetainedEvent { ComponentType = "App.C", ComponentInstanceId = 1, RetainedBy = ["A.B → C.D"], Timestamp = Ev.T(20) });

        var document = TraceDocumentBuilder.Create(session, host.Options, null, null, "test");
        using var stream = new MemoryStream();
        await new JsonTraceExporter().ExportAsync(document, stream);
        stream.Position = 0;
        var imported = await TraceImporter.ReadAsync(stream, new TraceImportOptions());

        var frame = Assert.Single(imported.Events.OfType<BrowserLongTaskEvent>());
        Assert.Equal(40, frame.ForcedStyleAndLayoutMs);
        Assert.Equal("f", Assert.Single(frame.Scripts!).SourceFunctionName);
        var rage = Assert.Single(imported.Events.OfType<BrowserFrustrationEvent>());
        Assert.Equal(FrustrationSignal.RageClick, rage.Signal);
        Assert.Equal(4, rage.ClickCount);
        Assert.Equal(["A.B → C.D"], Assert.Single(imported.Events.OfType<ComponentRetainedEvent>()).RetainedBy!);
    }

    [Fact]
    public void RedactionSanitizesScriptUrlsAndUrlInvokers()
    {
        var (host, session, _) = TestHost.WithSession();
        host.Emit(Frame(0, 120, 0, new LongFrameScript { Invoker = "https://localhost/js/app.js?token=secret", InvokerType = "classic-script", SourceUrl = "https://localhost/js/app.js?token=secret", DurationMs = 80 }));
        var frame = Assert.Single(session.CreateSnapshot().Events.OfType<BrowserLongTaskEvent>());
        var script = Assert.Single(frame.Scripts!);
        Assert.DoesNotContain("secret", script.SourceUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", script.Invoker, StringComparison.Ordinal);
    }
}
