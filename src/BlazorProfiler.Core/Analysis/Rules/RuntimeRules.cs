using BlazorProfiler.Events;
using static BlazorProfiler.Analysis.DiagnosticContext;

namespace BlazorProfiler.Analysis.Rules;

/// <summary>BP5001: high garbage-collection pressure.</summary>
public sealed class HighGcPressureRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP5001",
        Title = "High garbage-collection pressure",
        Category = DiagnosticCategory.Memory,
        Description = "Detects a high share of time paused for garbage collection or frequent generation 2 collections.",
        RequiredSignals = [ProfilerEventKind.RuntimeSample],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var runtime = context.Summary.Runtime;
        if (runtime.Samples < 5 || runtime.SampledMs < 5_000)
        {
            yield break;
        }

        // Without pause data (WebAssembly) only the generation 2 collection rate is evaluated.
        var pauseAvailable = !runtime.GcPauseUnavailable;
        var pauseRatio = pauseAvailable ? runtime.GcPauseMs / runtime.SampledMs : 0;
        var gen2PerMinute = runtime.Gen2Collections / (runtime.SampledMs / 60_000);
        var highPause = pauseRatio >= context.Options.HighGcPauseRatio;
        var highGen2 = gen2PerMinute >= context.Options.HighGen2CollectionsPerMinute;
        if (!highPause && !highGen2)
        {
            yield break;
        }

        var server = context.Events.Any(e => e.CircuitId is not null);
        yield return Create(
            highPause ? $"{FormatPercent(pauseRatio)} of time spent in GC pauses" : $"{gen2PerMinute:0.#} generation 2 collections per minute",
            highPause ? Scale(pauseRatio, context.Options.HighGcPauseRatio) : Scale(gen2PerMinute, context.Options.HighGen2CollectionsPerMinute),
            DiagnosticConfidence.High,
            pauseAvailable
                ? $"Over {FormatMs(runtime.SampledMs)} of samples the runtime paused {FormatMs(runtime.GcPauseMs)} for GC ({runtime.Gen0Collections}/{runtime.Gen1Collections}/{runtime.Gen2Collections} gen0/1/2 collections) and allocated {FormatBytes(runtime.AllocatedBytes)}."
                : $"Over {FormatMs(runtime.SampledMs)} of samples the runtime ran {runtime.Gen0Collections}/{runtime.Gen1Collections}/{runtime.Gen2Collections} gen0/1/2 collections and allocated {FormatBytes(runtime.AllocatedBytes)} (this runtime does not report GC pause time).",
            server
                ? "GC pauses stop all threads of the server process, delaying every circuit. Frequent generation 2 collections indicate many long-lived allocations or memory pressure."
                : "In WebAssembly, GC pauses block the browser's UI thread. Allocation-heavy rendering (large render trees, LINQ, string building) increases collection frequency.",
            "Reduce allocations on hot paths (avoid LINQ and string concatenation in markup, reuse buffers, cache derived data), avoid holding large object graphs per user, and check the allocation spike findings for when allocations happen.",
            [
                pauseAvailable ? Evidence("GC pause share", FormatPercent(pauseRatio)) : Evidence("GC pause share", "not reported by this runtime", ValueQuality.Unavailable),
                Evidence("Gen2 collections per minute", gen2PerMinute.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)),
                Evidence("Allocated", FormatBytes(runtime.AllocatedBytes)),
            ],
            new ImpactEstimate
            {
                TotalMs = pauseAvailable ? runtime.GcPauseMs : null,
                Occurrences = runtime.Gen0Collections + runtime.Gen1Collections + runtime.Gen2Collections,
                Method = pauseAvailable ? "Total GC pause time reported by the runtime during sampling." : "Garbage collections during sampling (this runtime does not report pause time).",
                Quality = pauseAvailable ? ValueQuality.Measured : ValueQuality.Unavailable,
            },
            subject: "gc",
            caveats: ["Server GC values cover the whole process, including work unrelated to Blazor."]);
    }
}

/// <summary>BP5002: allocation spikes.</summary>
public sealed class AllocationSpikeRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP5002",
        Title = "Allocation spike",
        Category = DiagnosticCategory.Memory,
        Description = "Detects runtime samples whose allocation rate is far above the session median and lists operations that overlapped them (temporal correlation only).",
        RequiredSignals = [ProfilerEventKind.RuntimeSample],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var samples = context.OfType<RuntimeSampleEvent>().Where(s => s.AllocatedBytes is not null && s.Duration > 0).ToList();
        if (samples.Count < 10)
        {
            yield break;
        }

        double Rate(RuntimeSampleEvent s) => s.AllocatedBytes!.Value / (s.Duration / (double)context.Clock.Frequency);
        var rates = samples.Select(Rate).OrderBy(r => r).ToList();
        var median = rates[rates.Count / 2];
        var threshold = Math.Max(median * context.Options.AllocationSpikeFactor, context.Options.AllocationSpikeMinimumBytesPerSecond);
        var spikes = samples.Where(s => Rate(s) >= threshold).OrderByDescending(Rate).Take(5).ToList();
        if (spikes.Count == 0)
        {
            yield break;
        }

        var operations = context.Events
            .Where(e => e is EventHandlerEvent or NavigationEvent or OperationEvent)
            .Where(e => spikes.Any(s => e.Timestamp <= s.EndTimestamp && e.EndTimestamp >= s.Timestamp))
            .ToList();
        var names = operations.Select(e => e switch
        {
            EventHandlerEvent h => HandlerName(h),
            NavigationEvent n => "navigation to " + n.Route,
            OperationEvent o => o.Name ?? "operation",
            _ => e.Kind.ToString(),
        }).Distinct().Take(5).ToList();

        var peak = Rate(spikes[0]);
        yield return Create(
            $"Allocation rate spiked to {FormatBytes(peak)}/s",
            Scale(peak / Math.Max(median, 1), context.Options.AllocationSpikeFactor * 2),
            DiagnosticConfidence.Medium,
            $"{spikes.Count} sample(s) allocated at {FormatBytes(threshold)}/s or more (peak {FormatBytes(peak)}/s; session median {FormatBytes(median)}/s).",
            names.Count > 0
                ? $"These operations overlapped the spikes in time: {string.Join("; ", names)}. Overlap is temporal correlation only; allocations are measured process-wide and cannot be attributed to a component."
                : "No interaction or navigation overlapped the spikes; background work (timers, polling, SignalR traffic) may be responsible.",
            "Profile the overlapping operations with a memory profiler (dotnet-trace, Visual Studio, or browser tools for WebAssembly) and reduce allocations: avoid materializing large collections, reuse buffers, and avoid re-creating large render trees.",
            [
                Evidence("Peak allocation rate", FormatBytes(peak) + "/s"),
                Evidence("Median allocation rate", FormatBytes(median) + "/s"),
                Evidence("Overlapping operations", names.Count > 0 ? string.Join("; ", names) : "none", names.Count > 0 ? ValueQuality.Inferred : ValueQuality.Measured),
            ],
            new ImpactEstimate { Occurrences = spikes.Count, Method = "Runtime samples above the spike threshold.", Quality = ValueQuality.Measured },
            subject: "allocation-spike",
            caveats: ["Allocation counters are process-wide (per browser tab in WebAssembly). Per-component allocation attribution is not available."],
            related: spikes.Cast<ProfilerEvent>().Concat(operations.Take(10)));
    }
}

/// <summary>BP5003: memory growth across repeated navigation.</summary>
public sealed class MemoryGrowthAcrossNavigationRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP5003",
        Title = "Memory growth across navigation",
        Category = DiagnosticCategory.Memory,
        Description = "Fits a linear trend to the managed heap size observed after each navigation and reports sustained growth, which may indicate retained components or caches.",
        RequiredSignals = [ProfilerEventKind.Navigation, ProfilerEventKind.RuntimeSample],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var navigations = context.OfType<NavigationEvent>().OrderBy(n => n.Timestamp).ToList();
        var samples = context.OfType<RuntimeSampleEvent>().Where(s => (s.HeapSizeBytes ?? s.TotalMemoryBytes) is not null).OrderBy(s => s.EndTimestamp).ToList();
        if (navigations.Count < context.Options.MemoryGrowthMinimumNavigations || samples.Count < 2)
        {
            yield break;
        }

        var points = new List<(double X, double Y)>();
        var gen2 = 0;
        foreach (var navigation in navigations)
        {
            var sample = samples.FirstOrDefault(s => s.EndTimestamp >= navigation.EndTimestamp);
            if (sample is null)
            {
                break;
            }

            points.Add((points.Count, (sample.HeapSizeBytes ?? sample.TotalMemoryBytes)!.Value));
        }

        gen2 = samples.Sum(s => s.Gen2Collections ?? 0);
        if (points.Count < context.Options.MemoryGrowthMinimumNavigations)
        {
            yield break;
        }

        var meanX = points.Average(p => p.X);
        var meanY = points.Average(p => p.Y);
        var sxx = points.Sum(p => (p.X - meanX) * (p.X - meanX));
        var sxy = points.Sum(p => (p.X - meanX) * (p.Y - meanY));
        var syy = points.Sum(p => (p.Y - meanY) * (p.Y - meanY));
        if (sxx <= 0 || syy <= 0)
        {
            yield break;
        }

        var slope = sxy / sxx;
        var r2 = (sxy * sxy) / (sxx * syy);
        if (slope < context.Options.MemoryGrowthBytesPerNavigation || r2 < 0.6)
        {
            yield break;
        }

        yield return Create(
            $"Heap grows about {FormatBytes(slope)} per navigation",
            Scale(slope, context.Options.MemoryGrowthBytesPerNavigation),
            gen2 > 0 ? DiagnosticConfidence.Medium : DiagnosticConfidence.Low,
            $"Across {points.Count} navigations the managed heap grew from {FormatBytes(points[0].Y)} to {FormatBytes(points[^1].Y)} (trend {FormatBytes(slope)} per navigation, R² {r2:0.00}).",
            "Memory that keeps growing as users navigate between pages often means disposed components are still referenced: event handlers subscribed to longer-lived services, timers, static or singleton caches, or DotNetObjectReference instances that were never disposed.",
            "Unsubscribe from service events and dispose timers in Dispose, dispose DotNetObjectReference/IJSObjectReference instances, bound caches, and take a heap snapshot after several navigations to find retaining paths.",
            [
                Evidence("Navigations analyzed", FormatNumber(points.Count)),
                Evidence("Growth per navigation", FormatBytes(slope), ValueQuality.Estimated),
                Evidence("Trend fit (R²)", r2.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)),
                Evidence("Gen2 collections observed", FormatNumber(gen2)),
            ],
            new ImpactEstimate { Occurrences = points.Count, Method = "Least-squares slope of heap size sampled after each navigation.", Quality = ValueQuality.Estimated },
            subject: "memory-growth",
            caveats:
            [
                "Heap size depends on when garbage collections run; growth without generation 2 collections may be reclaimed later.",
                "Caches that fill up on first visits grow legitimately until they reach their bound.",
            ],
            related: navigations.Take(20));
    }
}

/// <summary>BP5004: released components that are still reachable (memory leaks).</summary>
/// <remarks>
/// Blazor's renderer keeps the previous render tree of every live component, and that buffer can still reference the
/// child most recently removed from a given position until the position is rendered again. Such retention is bounded (at
/// most one instance per parent position) and is not a leak. The rule therefore treats a retained instance as a likely
/// leak only when retention accumulates (two or more retained instances of the same type under the same parent), when the
/// parent is gone too, or when the whole circuit or request scope ended; a single retained child of a live parent is
/// reported at Info severity with low confidence.
/// </remarks>
public sealed class RetainedComponentsRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP5004",
        Title = "Components retained after disposal",
        Category = DiagnosticCategory.Memory,
        Description = "Reports component instances that were still reachable after several full garbage collections although they had been disposed, or their circuit or request scope had ended, and separates accumulating retention (leaks) from the bounded retention of Blazor's previous render tree.",
        RequiredSignals = [ProfilerEventKind.ComponentRetained],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var retainedEvents = context.OfType<ComponentRetainedEvent>().ToList();
        if (retainedEvents.Count == 0)
        {
            yield break;
        }

        // Parents are known for children of instrumented components (component tree tracking).
        var parentOf = new Dictionary<long, long>();
        foreach (var children in context.OfType<ComponentChildrenEvent>())
        {
            foreach (var child in children.ChildInstanceIds)
            {
                parentOf[child] = children.ComponentInstanceId;
            }
        }

        var gone = new HashSet<long>(context.OfType<ComponentDisposedEvent>().Select(e => e.ComponentInstanceId));
        gone.UnionWith(retainedEvents.Where(e => e.Reason == RetentionReason.ScopeEnded).Select(e => e.ComponentInstanceId));

        foreach (var group in retainedEvents.GroupBy(e => e.ComponentType ?? "(unknown)").OrderByDescending(g => g.Count()))
        {
            var retained = group.ToList();
            var accumulating = new List<ComponentRetainedEvent>();
            var single = new List<ComponentRetainedEvent>();
            var unknownParents = 0;
            foreach (var slot in retained.Where(e => e.Reason == RetentionReason.Disposed).GroupBy(e => parentOf.GetValueOrDefault(e.ComponentInstanceId)))
            {
                var instances = slot.ToList();
                if (slot.Key == 0)
                {
                    unknownParents += instances.Count;
                }

                if (instances.Count >= 2 || (slot.Key != 0 && gone.Contains(slot.Key)))
                {
                    accumulating.AddRange(instances);
                }
                else
                {
                    single.AddRange(instances);
                }
            }

            var scopeEnded = retained.Where(e => e.Reason == RetentionReason.ScopeEnded).ToList();
            var likely = accumulating.Count + scopeEnded.Count;
            var count = retained.Count;
            var circuits = retained.Select(e => e.CircuitId).Where(c => c is not null).Distinct().Count();
            var survived = retained.Max(e => e.SurvivedCollections);
            var forced = retained.Any(e => e.ForcedCollection);
            var oldestSeconds = retained.Max(e => context.Ms(e.Timestamp - e.ReleasedTimestamp)) / 1000;
            var shortType = ShortType(group.Key);
            var evidence = new List<FindingEvidence>
            {
                Evidence("Retained instances", FormatNumber(count)),
                Evidence("Likely leaked", FormatNumber(likely), ValueQuality.Inferred),
                Evidence("Released by", scopeEnded.Count == count ? "end of circuit/request scope" : scopeEnded.Count == 0 ? "disposal" : $"disposal ({FormatNumber(count - scopeEnded.Count)}), scope end ({FormatNumber(scopeEnded.Count)})"),
                Evidence("Full collections survived (max)", FormatNumber(survived)),
                Evidence("Circuits", circuits > 0 ? FormatNumber(circuits) : "none (WebAssembly or static rendering)", circuits > 0 ? ValueQuality.Measured : ValueQuality.Unavailable),
                Evidence("Retaining path", "not determined (use a memory profiler or dotnet-gcdump)", ValueQuality.Unavailable),
            };
            var caveats = new List<string>
            {
                "Blazor keeps the previous render tree of a live component; it can reference the child most recently removed from a position until that position renders again. That retention is bounded and is not counted as a likely leak.",
                "Work that legitimately outlives a component (for example an awaited request that completes after disposal) keeps it alive briefly; such instances are reported only after the configured number of full collections.",
                "Components that do not inherit ProfiledComponentBase are watched only when their circuit or request scope ends.",
            };

            if (likely == 0)
            {
                yield return Create(
                    $"{FormatNumber(count)} removed {shortType} instance(s) still referenced (possibly by the previous render tree)",
                    DiagnosticSeverity.Info,
                    DiagnosticConfidence.Low,
                    $"{FormatNumber(count)} disposed instance(s) of {shortType} were still reachable after {survived} full garbage collection(s), at most one per parent position{(unknownParents > 0 ? " (or with an unknown parent)" : string.Empty)}. This matches Blazor keeping the parent's previous render tree, which is bounded and harmless.",
                    "A real leak grows each time the component is shown and removed again; bounded retention by the renderer does not.",
                    "Repeat the action that shows and removes the component several times and run the leak check again. If the count grows, look for event subscriptions on longer-lived services, timers, DotNetObjectReference instances, or static caches that reference the component.",
                    evidence,
                    new ImpactEstimate { Occurrences = count, Method = "Number of released instances still reachable at the check; their memory size is not measured.", Quality = ValueQuality.Measured },
                    componentType: group.Key,
                    subject: group.Key,
                    caveats: caveats,
                    related: retained);
                continue;
            }

            var reason = scopeEnded.Count == likely
                ? "their circuit or request scope had ended"
                : scopeEnded.Count == 0 ? "they were disposed" : "they were disposed or their circuit or request scope had ended";
            var knownStructure = scopeEnded.Count > 0 || accumulating.Any(e => parentOf.ContainsKey(e.ComponentInstanceId));
            var confidence = (forced || survived >= 3) && knownStructure ? DiagnosticConfidence.High : DiagnosticConfidence.Medium;

            yield return Create(
                $"{FormatNumber(likely)} released {shortType} instance(s) are leaking",
                Scale(likely, 1),
                confidence,
                $"{FormatNumber(likely)} instance(s) of {shortType} were still reachable after {survived} full garbage collection(s){(forced ? " (including collections forced by a leak check)" : string.Empty)}, although {reason}. The retention accumulates, so it is not explained by the renderer's previous render tree. The oldest was released {oldestSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s before the check.",
                "Something with a longer lifetime still references these components, so they (and everything they reference: render trees, parameters, injected scoped services) cannot be collected. On Blazor Server this memory accumulates per user until the process restarts; in WebAssembly it accumulates in the browser tab.",
                "Unsubscribe from events of longer-lived services in Dispose (a frequent cause: `State.OnChange += StateHasChanged` without `-=`), dispose timers, CancellationTokenSources and DotNetObjectReference instances, and avoid static fields or caches that hold components, RenderFragments, or lambdas capturing `this`. Implement IDisposable/IAsyncDisposable in the component (`@implements IDisposable`) and release everything there.",
                evidence,
                new ImpactEstimate { Occurrences = likely, Method = "Number of released instances that accumulate in memory; their memory size is not measured.", Quality = ValueQuality.Measured },
                componentType: group.Key,
                subject: group.Key,
                caveats: caveats,
                related: accumulating.Concat(scopeEnded).Concat(single),
                codeHint: $"{shortType}: Dispose, event subscriptions, timers");
        }
    }
}

/// <summary>BP6001: slow server render round trips.</summary>
public sealed class SlowServerRenderRoundTripRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP6001",
        Title = "Slow server render round trip",
        Category = DiagnosticCategory.Server,
        Description = "Detects Blazor Server render batches whose time from diff generation until the browser's acknowledgement exceeds the threshold, and estimates whether network latency dominates.",
        RequiredSignals = [ProfilerEventKind.RenderBatch],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Options.SlowServerRenderMs;
        foreach (var circuit in context.OfType<RenderBatchEvent>().Where(b => b.IncludesClientRoundTrip == true).GroupBy(b => b.CircuitId ?? "(unknown)"))
        {
            var batches = circuit.ToList();
            var p95 = context.Ms(PercentileTicks(batches, 95));
            if (batches.Count < 3 || p95 < threshold)
            {
                continue;
            }

            var small = batches.Where(b => b.UpdatedComponentCount <= 3).ToList();
            var smallMedian = small.Count >= 3 ? context.Ms(PercentileTicks(small, 50)) : (double?)null;
            var networkBound = smallMedian >= threshold * 0.5;
            yield return Create(
                $"Render round trips on circuit {circuit.Key} p95 {FormatMs(p95)}",
                Scale(p95, threshold),
                networkBound ? DiagnosticConfidence.Medium : DiagnosticConfidence.Low,
                $"{batches.Count} render batches on circuit {circuit.Key}: p95 {FormatMs(p95)} from diff generation until the browser acknowledged the update.",
                networkBound
                    ? $"Even batches that updated three or fewer components took a median of {FormatMs(smallMedian!.Value)}, which points to network latency (or a busy browser) rather than server rendering."
                    : "The measured time combines server diffing, serialization, network transport, and browser-side DOM updates; the profiler cannot measure them separately on the server.",
                "If latency is network-bound, deploy closer to users, enable WebSocket transport, and reduce interactions that need a server round trip (handle purely visual interactions in JavaScript or CSS). If batches are large, reduce re-rendered components.",
                [
                    Evidence("Batches", FormatNumber(batches.Count)),
                    Evidence("p95 round trip", FormatMs(p95)),
                    Evidence("Median of small batches", smallMedian is { } m ? FormatMs(m) : "insufficient data", smallMedian is null ? ValueQuality.Unavailable : ValueQuality.Measured),
                ],
                new ImpactEstimate { TotalMs = Math.Round(batches.Sum(b => context.Ms(b.Duration)), 3), Occurrences = batches.Count, Method = "Sum of measured batch round trips (includes transport).", Quality = ValueQuality.Measured },
                subject: circuit.Key,
                caveats: ["Round-trip time is not render time: transport latency, diff generation, and browser paint are distinct and are not separated by this measurement."],
                related: batches.OrderByDescending(b => b.Duration));
        }
    }
}

/// <summary>BP6002: circuit connection instability.</summary>
public sealed class CircuitInstabilityRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP6002",
        Title = "Circuit connection instability",
        Category = DiagnosticCategory.Server,
        Description = "Detects circuits whose connection dropped repeatedly.",
        RequiredSignals = [ProfilerEventKind.Circuit],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        foreach (var circuit in context.Summary.Circuits.Where(c => c.ConnectionDowns >= context.Options.CircuitReconnectThreshold).OrderByDescending(c => c.ConnectionDowns))
        {
            yield return Create(
                $"Circuit {circuit.CircuitId} disconnected {circuit.ConnectionDowns} times",
                Scale(circuit.ConnectionDowns, context.Options.CircuitReconnectThreshold),
                DiagnosticConfidence.High,
                $"The connection of circuit {circuit.CircuitId} went down {circuit.ConnectionDowns} times and came back {circuit.ConnectionUps} times.",
                "Each disconnection shows the reconnection UI, buffers render batches on the server, and loses the circuit if reconnection fails within the retention period.",
                "Check proxies and load balancers (WebSocket support, idle timeouts, sticky sessions), keep-alive and timeout settings (HubOptions.KeepAliveInterval, ClientTimeoutInterval), and client network stability.",
                [Evidence("Connection drops", FormatNumber(circuit.ConnectionDowns)), Evidence("Reconnections", FormatNumber(circuit.ConnectionUps))],
                new ImpactEstimate { Occurrences = circuit.ConnectionDowns, Method = "Count of connection-down notifications from the circuit handler.", Quality = ValueQuality.Measured },
                subject: circuit.CircuitId,
                caveats: ["Mobile clients that switch networks or sleep disconnect legitimately."]);
        }
    }
}

/// <summary>BP6003: slow circuit inbound activities.</summary>
public sealed class SlowCircuitActivityRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP6003",
        Title = "Slow circuit activity",
        Category = DiagnosticCategory.Server,
        Description = "Detects slow inbound circuit activities (browser events, interop callbacks, render acknowledgements) grouped by hub method.",
        RequiredSignals = [ProfilerEventKind.CircuitActivity],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Ticks(context.Options.SlowCircuitActivityMs);
        foreach (var group in context.OfType<CircuitActivityEvent>().Where(a => a.Duration >= threshold).GroupBy(a => a.ActivityName ?? "(unknown)").OrderByDescending(g => g.Max(a => a.Duration)))
        {
            var activities = group.ToList();
            var maxMs = context.Ms(activities.Max(a => a.Duration));
            yield return Create(
                $"{group.Key} circuit activities take up to {FormatMs(maxMs)}",
                Scale(maxMs, context.Options.SlowCircuitActivityMs),
                DiagnosticConfidence.Medium,
                $"{activities.Count} '{group.Key}' inbound activit(ies) took at least {FormatMs(context.Options.SlowCircuitActivityMs)} (max {FormatMs(maxMs)}) across {activities.Select(a => a.CircuitId).Distinct().Count()} circuit(s).",
                "Inbound activities include awaited work (for example an async event handler awaiting an HTTP call), so a long activity does not necessarily block the circuit. Synchronous parts do block the circuit's dispatcher, delaying the user's other events and renders.",
                "Cross-check slow event handlers and lifecycle findings for the same period, keep synchronous work short, and offload CPU-heavy work.",
                [Evidence("Slow activities", FormatNumber(activities.Count)), Evidence("Maximum", FormatMs(maxMs))],
                new ImpactEstimate { TotalMs = Math.Round(activities.Sum(a => context.Ms(a.Duration)), 3), Occurrences = activities.Count, Method = "Sum of measured inbound activity durations.", Quality = ValueQuality.Measured },
                subject: group.Key,
                related: activities.OrderByDescending(a => a.Duration));
        }
    }
}

/// <summary>BP6004: high render batch volume per circuit.</summary>
public sealed class HighRenderBatchVolumeRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP6004",
        Title = "High render batch volume",
        Category = DiagnosticCategory.Server,
        Description = "Detects circuits that send render batches to the browser at a sustained high rate.",
        RequiredSignals = [ProfilerEventKind.RenderBatch],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Options.HighBatchRatePerSecond;
        foreach (var circuit in context.OfType<RenderBatchEvent>().Where(b => b.CircuitId is not null).GroupBy(b => b.CircuitId!))
        {
            var sorted = SortedByTimestamp(circuit);
            var peak = PeakWeight(sorted, context.Clock.Frequency);
            var sustained = SecondsAtOrAbove(sorted, context.Clock.Frequency, threshold);
            if (peak < threshold || sustained < 3)
            {
                continue;
            }

            var total = Weighted(sorted);
            yield return Create(
                $"Circuit {circuit.Key} sends up to {FormatNumber(peak)} render batches per second",
                Scale(peak, threshold),
                DiagnosticConfidence.High,
                $"Circuit {circuit.Key} produced {FormatNumber(total)} render batches, at or above {FormatNumber(threshold)} per second for {sustained} second(s).",
                "Each batch is serialized and sent to the browser, which must acknowledge it. A sustained high rate consumes server CPU and bandwidth per user and limits how many users a server can host.",
                "Throttle UI updates from timers and streams (for example to 10 per second), avoid StateHasChanged in loops, and coalesce updates.",
                [Evidence("Peak batches per second", FormatNumber(peak)), Evidence("Seconds above threshold", FormatNumber(sustained))],
                new ImpactEstimate { Occurrences = total, Method = "Count of render batches on the circuit.", Quality = ValueQuality.Measured },
                subject: circuit.Key,
                related: circuit.Take(20));
        }
    }
}

/// <summary>BP6005: thread-pool saturation coinciding with slow server work.</summary>
public sealed class ServerSaturationRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP6005",
        Title = "Server thread-pool saturation",
        Category = DiagnosticCategory.Server,
        Description = "Detects runtime samples with a long thread-pool queue and lists slow operations that overlapped them (temporal correlation only).",
        RequiredSignals = [ProfilerEventKind.RuntimeSample],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var saturated = context.OfType<RuntimeSampleEvent>().Where(s => s.ThreadPoolQueueLength >= context.Options.ThreadPoolQueueSaturation).ToList();
        if (saturated.Count < 2)
        {
            yield break;
        }

        var slowThreshold = context.Ticks(context.Options.SlowEventHandlerMs);
        var overlapping = context.Events
            .Where(e => e is EventHandlerEvent or CircuitActivityEvent or RenderBatchEvent && e.Duration >= slowThreshold)
            .Where(e => saturated.Any(s => e.Timestamp <= s.EndTimestamp && e.EndTimestamp >= s.Timestamp))
            .ToList();
        var maxQueue = saturated.Max(s => s.ThreadPoolQueueLength ?? 0);
        yield return Create(
            $"Thread-pool queue reached {FormatNumber(maxQueue)} work items",
            Scale(maxQueue, context.Options.ThreadPoolQueueSaturation),
            overlapping.Count > 0 ? DiagnosticConfidence.Medium : DiagnosticConfidence.Low,
            $"{saturated.Count} runtime sample(s) observed at least {FormatNumber(context.Options.ThreadPoolQueueSaturation)} queued thread-pool work items; {overlapping.Count} slow operation(s) overlapped them.",
            "A long thread-pool queue means work waits for a thread, which increases the latency of every circuit on the server. It is commonly caused by blocking calls (.Result, .Wait(), synchronous I/O) or by CPU-bound work.",
            "Remove sync-over-async blocking, make I/O asynchronous, offload CPU-heavy work, and scale out if the load is legitimate.",
            [Evidence("Max queue length", FormatNumber(maxQueue)), Evidence("Saturated samples", FormatNumber(saturated.Count)), Evidence("Overlapping slow operations", FormatNumber(overlapping.Count), ValueQuality.Inferred)],
            new ImpactEstimate { Occurrences = saturated.Count, Method = "Runtime samples above the queue threshold.", Quality = ValueQuality.Measured },
            subject: "threadpool",
            caveats: ["Overlap with slow operations is temporal correlation, not proof of causation."],
            related: saturated.Cast<ProfilerEvent>().Concat(overlapping.Take(10)));
    }
}

/// <summary>BP7001: slow browser interactions (user-perceived latency).</summary>
public sealed class SlowBrowserInteractionRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP7001",
        Title = "Slow user interaction",
        Category = DiagnosticCategory.Browser,
        Description = "Detects browser-measured interactions (Event Timing API) slower than the threshold and breaks them down into input delay, processing, and presentation delay.",
        RequiredSignals = [ProfilerEventKind.BrowserInteraction],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Ticks(context.Options.SlowInteractionMs);
        foreach (var group in context.OfType<BrowserInteractionEvent>().Where(i => i.Duration >= threshold).GroupBy(i => i.EventType ?? "interaction"))
        {
            var interactions = group.ToList();
            var worstMs = context.Ms(interactions.Max(i => i.Duration));
            var inputDelay = interactions.Average(i => i.InputDelayMs);
            var processing = interactions.Average(i => i.ProcessingMs);
            var presentation = interactions.Average(i => i.PresentationDelayMs);
            var dominant = new[] { ("input delay", inputDelay), ("event processing", processing), ("presentation delay", presentation) }.MaxBy(p => p.Item2);
            var server = context.Events.Any(e => e.CircuitId is not null);
            var overlappingHandlers = context.OfType<EventHandlerEvent>()
                .Where(h => interactions.Any(i => h.Timestamp <= i.EndTimestamp && h.EndTimestamp >= i.Timestamp))
                .Select(HandlerName).Distinct().Take(3).ToList();

            var explanation = dominant.Item1 switch
            {
                "input delay" => "Most of the latency happened before the browser could start processing the input: the main thread was busy with other work (long tasks, rendering, or .NET code in WebAssembly).",
                "event processing" => server
                    ? "Most of the latency was spent in browser event handlers. On Blazor Server the .NET handler runs on the server after this, so its time is not part of this measurement."
                    : "Most of the latency was spent processing the event. In WebAssembly, .NET event handlers and the render that follows run inside this phase.",
                _ => "Most of the latency came after the event handlers finished: style, layout, and paint of the updated DOM took long, often because large DOM subtrees changed.",
            };

            yield return Create(
                $"'{group.Key}' interactions take up to {FormatMs(worstMs)} to paint",
                Scale(worstMs, context.Options.SlowInteractionMs),
                DiagnosticConfidence.High,
                $"{interactions.Count} '{group.Key}' interaction(s) took at least {FormatMs(context.Options.SlowInteractionMs)} from input to next paint (worst {FormatMs(worstMs)}). Average breakdown: input delay {FormatMs(inputDelay)}, processing {FormatMs(processing)}, presentation {FormatMs(presentation)}.",
                explanation,
                "Reduce work on the main thread before and during the interaction, render less per interaction, and avoid large synchronous DOM changes. In WebAssembly, keep event handlers short and yield (await Task.Yield()) before long work so the browser can paint.",
                [
                    Evidence("Slow interactions", FormatNumber(interactions.Count)),
                    Evidence("Worst", FormatMs(worstMs)),
                    Evidence("Dominant phase", dominant.Item1),
                    Evidence(".NET handlers overlapping in time", overlappingHandlers.Count > 0 ? string.Join("; ", overlappingHandlers) : "none", overlappingHandlers.Count > 0 ? ValueQuality.Inferred : ValueQuality.Measured),
                ],
                new ImpactEstimate { TotalMs = Math.Round(interactions.Sum(i => context.Ms(i.Duration)), 3), Occurrences = interactions.Count, Method = "Sum of browser-reported interaction durations.", Quality = ValueQuality.Measured },
                subject: group.Key,
                caveats: ["Overlapping .NET handlers are matched by time only; the Event Timing API does not identify which handler ran."],
                related: interactions.OrderByDescending(i => i.Duration));
        }
    }
}

/// <summary>BP7002: browser long tasks / long animation frames.</summary>
public sealed class LongTaskRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP7002",
        Title = "Main thread blocked by long tasks",
        Category = DiagnosticCategory.Browser,
        Description = "Detects repeated long animation frames or long tasks on the browser's main thread.",
        RequiredSignals = [ProfilerEventKind.BrowserLongTask],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Ticks(context.Options.LongTaskMs);
        var tasks = context.OfType<BrowserLongTaskEvent>().Where(t => t.Duration >= threshold).ToList();
        if (tasks.Count < 3)
        {
            yield break;
        }

        var totalBlocking = tasks.Sum(t => t.BlockingMs);
        var maxMs = context.Ms(tasks.Max(t => t.Duration));
        var scripts = tasks.Select(t => t.Script).Where(s => !string.IsNullOrEmpty(s)).GroupBy(s => s).OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key!).ToList();
        var overlapping = context.Events
            .Where(e => e is RenderBatchEvent or EventHandlerEvent)
            .Where(e => tasks.Any(t => e.Timestamp <= t.EndTimestamp && e.EndTimestamp >= t.Timestamp))
            .ToList();
        var wasm = context.Events.Any(e => e is RenderBatchEvent { IncludesClientRoundTrip: false });

        yield return Create(
            $"{tasks.Count} long frames blocked the main thread (max {FormatMs(maxMs)})",
            Scale(maxMs, context.Options.LongTaskMs),
            DiagnosticConfidence.High,
            $"{tasks.Count} long tasks or animation frames of at least {FormatMs(context.Options.LongTaskMs)} were observed (max {FormatMs(maxMs)}, total blocking {FormatMs(totalBlocking)}). {overlapping.Count} render batch(es) or handler(s) overlapped them.",
            wasm
                ? "In Blazor WebAssembly, .NET code (event handlers, rendering, JSON deserialization) runs on the browser's main thread, so long .NET work appears as long tasks and delays input and paint."
                : "Long tasks delay input handling and paint; they come from JavaScript, layout, or applying large render batches.",
            "Break long work into smaller chunks (await Task.Yield() in .NET, scheduler.yield() in JavaScript), reduce the size of render batches, and defer non-urgent work.",
            [
                Evidence("Long frames", FormatNumber(tasks.Count)),
                Evidence("Maximum", FormatMs(maxMs)),
                Evidence("Top scripts", scripts.Count > 0 ? string.Join(", ", scripts) : "not reported", scripts.Count > 0 ? ValueQuality.Measured : ValueQuality.Unavailable),
                Evidence("Overlapping .NET work", FormatNumber(overlapping.Count), ValueQuality.Inferred),
            ],
            new ImpactEstimate { TotalMs = Math.Round(totalBlocking, 3), Occurrences = tasks.Count, Method = "Sum of browser-reported blocking durations.", Quality = ValueQuality.Measured },
            subject: "long-tasks",
            caveats: ["Overlap with .NET work is temporal correlation."],
            related: tasks.Cast<ProfilerEvent>().Concat(overlapping.Take(10)));
    }
}
