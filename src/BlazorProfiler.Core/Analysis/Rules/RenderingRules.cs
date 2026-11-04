using BlazorProfiler.Events;
using static BlazorProfiler.Analysis.DiagnosticContext;

namespace BlazorProfiler.Analysis.Rules;

/// <summary>BP1001: a component type renders at a sustained high frequency.</summary>
public sealed class HighFrequencyRenderingRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP1001",
        Title = "High-frequency rendering",
        Category = DiagnosticCategory.Rendering,
        Description = "Detects component types that render (or receive parameters) many times per second for a sustained period, and grades confidence by whether the renders produced changed output or received changed parameters.",
        RequiredSignals = [ProfilerEventKind.ComponentRender, ProfilerEventKind.ParametersSet],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var options = context.Options;
        var renders = context.OfType<ComponentRenderEvent>().GroupBy(e => e.ComponentType ?? "(unknown)").ToDictionary(g => g.Key, g => g.Cast<ProfilerEvent>().ToList());
        var parameters = context.OfType<ParametersSetEvent>().GroupBy(e => e.ComponentType ?? "(unknown)").ToDictionary(g => g.Key, g => g.Cast<ProfilerEvent>().ToList());
        var summaries = context.Summary.Components.ToDictionary(c => c.ComponentType, StringComparer.Ordinal);
        var second = context.Clock.Frequency;
        var findings = new List<(DiagnosticFinding Finding, double Peak)>();

        foreach (var type in renders.Keys.Union(parameters.Keys))
        {
            var instrumented = renders.TryGetValue(type, out var rendered);
            var events = instrumented ? rendered! : parameters[type];

            // Counts are weighted: under sampling each retained event stands for several renders.
            if (Weighted(events) < options.MinimumRenderSamples)
            {
                continue;
            }

            var sorted = SortedByTimestamp(events);
            var peak = PeakRate(sorted, second);
            var sustained = SecondsAtOrAbove(sorted, second, options.HighRenderRatePerSecond);
            if (peak < options.HighRenderRatePerSecond || (sustained < 2 && peak < options.HighRenderRatePerSecond * 3))
            {
                continue;
            }

            summaries.TryGetValue(type, out var summary);
            var unchangedOutputRatio = summary is { RendersWithOutputComparison: > 0 } ? summary.RendersWithUnchangedOutput / summary.RendersWithOutputComparison : (double?)null;
            var parameterSets = summary?.ParametersSets.Count ?? 0;
            var unchangedParameterRatio = parameterSets > 0 ? (summary!.ParametersUnchanged + summary.ParametersChangedByReferenceOnly) / parameterSets : (double?)null;
            var totalMs = context.Ms(events.Sum(e => e.Duration));

            var confidence = unchangedOutputRatio >= 0.5
                ? DiagnosticConfidence.High
                : unchangedParameterRatio >= 0.5 ? DiagnosticConfidence.Medium : DiagnosticConfidence.Low;

            var severity = Scale(peak, options.HighRenderRatePerSecond);
            if (confidence == DiagnosticConfidence.Low)
            {
                severity = Cap(severity, DiagnosticSeverity.Medium);
            }

            var what = instrumented ? "renders" : "parameter updates";
            var evidence = new List<FindingEvidence>
            {
                Evidence(instrumented ? "Renders" : "Parameter updates (render proxy)", FormatNumber(Weighted(events)), instrumented ? Core.ValueQualityFor(events) : ValueQuality.Inferred),
                Evidence("Peak rate", FormatNumber(peak) + " per second", Core.ValueQualityFor(events)),
                Evidence("Seconds at or above " + FormatNumber(options.HighRenderRatePerSecond) + "/s", FormatNumber(sustained), Core.ValueQualityFor(events)),
                Evidence(instrumented ? "Total render time (BuildRenderTree)" : "Total SetParametersAsync time", FormatMs(totalMs)),
            };

            if (unchangedOutputRatio is { } outputRatio)
            {
                evidence.Add(Evidence("Renders with identical output", FormatPercent(outputRatio)));
            }

            if (unchangedParameterRatio is { } parameterRatio)
            {
                evidence.Add(Evidence("Parameter updates without value changes", FormatPercent(parameterRatio)));
            }

            var explanation = confidence switch
            {
                DiagnosticConfidence.High => $"Most of these renders produced output identical to the previous render, so the work did not change the UI. Every render also costs diffing{(context.Events.Any(e => e is RenderBatchEvent { IncludesClientRoundTrip: true }) ? " and, on Blazor Server, network traffic" : string.Empty)}.",
                DiagnosticConfidence.Medium => "Most parameter updates carried the same values as before (or only new delegate/object instances), which suggests the component re-renders because its parent re-renders rather than because its own data changed.",
                _ => instrumented
                    ? "The component renders very frequently. This may be legitimate (animations, timers, streaming data), but sustained high-frequency rendering consumes UI-thread or circuit time."
                    : "The component receives parameters very frequently, which usually triggers a render each time. Without component instrumentation the profiler cannot tell whether the output changed.",
            };

            findings.Add((Create(
                $"{ShortType(type)} {what} {FormatNumber(peak)} times per second",
                severity,
                confidence,
                $"{ShortType(type)} had {FormatNumber(Weighted(events))} {what}, peaking at {FormatNumber(peak)} per second and staying at or above {FormatNumber(options.HighRenderRatePerSecond)} per second for {sustained} second(s).",
                explanation,
                "Reduce render triggers: override ShouldRender to skip renders when relevant state is unchanged, avoid calling StateHasChanged in loops or high-frequency timers (throttle to the display rate), pass primitive or immutable parameters, and move frequently changing state into a smaller child component so fewer components re-render.",
                evidence,
                new ImpactEstimate
                {
                    TotalMs = Math.Round(totalMs, 3),
                    Occurrences = Weighted(events),
                    Method = instrumented ? "Sum of measured BuildRenderTree durations (excludes diffing and DOM updates)." : "Sum of SetParametersAsync durations from framework metrics (includes awaited lifecycle work).",
                    Quality = instrumented ? ValueQuality.Measured : ValueQuality.Inferred,
                },
                componentType: type,
                subject: type,
                caveats:
                [
                    "Animations, progress indicators, timers, and streaming data can legitimately render frequently.",
                    instrumented ? "Output comparison ignores event-handler delegates; a render that only rebinds handlers is reported as identical." : "Parameter updates are used as a proxy for renders because per-component render events require ProfiledComponentBase.",
                ],
                related: events.Take(20),
                codeHint: $"Look for StateHasChanged calls, timers, or parent re-renders affecting {ShortType(type)}."), peak));
        }

        return findings.OrderByDescending(f => f.Finding.Severity).ThenByDescending(f => f.Peak).Select(f => f.Finding);
    }
}

/// <summary>Helpers shared by rules.</summary>
internal static class Core
{
    public static ValueQuality ValueQualityFor(IEnumerable<ProfilerEvent> events) =>
        events.Any(e => e.SampleWeight > 1) ? ValueQuality.Estimated : ValueQuality.Measured;
}

/// <summary>BP1002: a single operation causes very large render batches.</summary>
public sealed class LargeRenderBatchRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP1002",
        Title = "Large component subtree re-rendered",
        Category = DiagnosticCategory.Rendering,
        Description = "Detects render batches that diff many components, grouped by the interaction that caused them when correlation is available.",
        RequiredSignals = [ProfilerEventKind.RenderBatch],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Options.LargeBatchComponentCount;
        var large = context.OfType<RenderBatchEvent>().Where(b => b.UpdatedComponentCount >= threshold).ToList();
        if (large.Count == 0)
        {
            yield break;
        }

        var handlers = HandlersByOperation(context);
        var groups = large
            .GroupBy(b => b.OperationId != 0 && handlers.TryGetValue(b.OperationId, out var h) ? HandlerName(h) : "(no correlated interaction)")
            .Select(g => (Name: g.Key, Batches: g.ToList(), Max: g.Max(b => b.UpdatedComponentCount)))
            .OrderByDescending(g => g.Max);

        foreach (var (name, batches, max) in groups)
        {
            var correlated = name != "(no correlated interaction)";
            var totalMs = context.Ms(batches.Sum(b => b.Duration));
            var worst = batches.MaxBy(b => b.UpdatedComponentCount)!;
            yield return Create(
                correlated ? $"{name} re-renders up to {max} components" : $"Render batches update up to {max} components",
                Scale(max, threshold),
                correlated ? DiagnosticConfidence.High : DiagnosticConfidence.Medium,
                $"{batches.Count} render batch(es) diffed at least {threshold} components (largest: {max} components, {FormatMs(context.Ms(worst.Duration))}).",
                correlated
                    ? $"The batches ran in the execution context of '{name}' (correlation: {worst.Correlation}), so this interaction caused a large part of the component tree to re-render. Re-rendering a component re-renders every child whose parameters may have changed, which grows with tree size."
                    : "Large batches usually mean a component high in the tree re-rendered and its children received parameters the framework could not prove unchanged.",
                "Keep frequently changing state in the smallest component that needs it, give child components primitive or immutable parameters, override ShouldRender in expensive children, add @key to list items, and use <Virtualize> for long lists.",
                [
                    Evidence("Large batches", FormatNumber(batches.Count)),
                    Evidence("Largest batch", max + " components"),
                    Evidence("Total time of these batches", FormatMs(totalMs)),
                ],
                new ImpactEstimate { TotalMs = Math.Round(totalMs, 3), Occurrences = batches.Count, Method = "Sum of measured render batch durations.", Quality = ValueQuality.Measured },
                subject: name,
                caveats: ["Initial page renders and navigations legitimately produce large batches."],
                related: batches);
        }
    }
}

/// <summary>BP1003: parameters change by reference only, forcing re-renders.</summary>
public sealed class ParameterReferenceChurnRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP1003",
        Title = "Re-renders caused by new parameter instances",
        Category = DiagnosticCategory.Rendering,
        Description = "Detects instrumented components whose parameter updates carry equal values except for reference-type parameters (delegates, RenderFragment, objects) that are new instances on every parent render.",
        RequiredSignals = [ProfilerEventKind.ParametersSet],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        foreach (var component in context.Summary.Components.OrderByDescending(c => c.ParametersChangedByReferenceOnly + c.ParametersUnchanged))
        {
            var total = component.ParametersSets.Count;
            var churn = component.ParametersChangedByReferenceOnly + component.ParametersUnchanged;
            if (!component.Instrumented || total < 20 || churn / total < 0.5)
            {
                continue;
            }

            var names = component.ReferenceOnlyParameters.OrderByDescending(p => p.Value).Take(5).Select(p => p.Key).ToList();
            yield return Create(
                $"{ShortType(component.ComponentType)} re-renders with unchanged parameter values",
                Scale(churn, 100),
                DiagnosticConfidence.High,
                $"{FormatPercent(churn / total)} of {FormatNumber(total)} parameter updates to {ShortType(component.ComponentType)} had no value changes{(names.Count > 0 ? " other than new instances of: " + string.Join(", ", names) : string.Empty)}.",
                "Blazor skips re-rendering a child only when every parameter is of a known immutable type and unchanged. A parameter of a reference type (a lambda, an EventCallback built from a lambda, a RenderFragment, or an object) that is re-created on each parent render makes the child render again even though nothing changed.",
                "Create delegates once (for example in a field or OnInitialized) instead of inline lambdas, pass primitive values or immutable records, and override ShouldRender to compare the values that matter. For RenderFragment child content, consider whether the child can skip rendering when its own data is unchanged.",
                [
                    Evidence("Parameter updates", FormatNumber(total)),
                    Evidence("Unchanged or reference-only changes", FormatNumber(churn)),
                    Evidence("Most frequent reference-only parameters", names.Count > 0 ? string.Join(", ", names) : "(none recorded)"),
                ],
                new ImpactEstimate { Occurrences = churn, Method = "Count of parameter updates with no value change, measured by ProfiledComponentBase.", Quality = ValueQuality.Measured },
                componentType: component.ComponentType,
                subject: component.ComponentType,
                caveats: ["Values are compared with Equals for primitives and by reference for other types; mutable objects mutated in place are reported as unchanged even though their content changed."],
                codeHint: names.Count > 0 ? $"Check how the parent passes {string.Join(", ", names)} to {ShortType(component.ComponentType)}." : null);
        }
    }
}

/// <summary>BP1004: renders that produced identical output.</summary>
public sealed class UnchangedOutputRenderRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP1004",
        Title = "Renders without visible change",
        Category = DiagnosticCategory.Rendering,
        Description = "Detects instrumented components where a large share of renders produced output identical to the previous render (requires Instrumentation.TrackRenderOutput).",
        RequiredSignals = [ProfilerEventKind.ComponentRender],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        foreach (var component in context.Summary.Components.OrderByDescending(c => c.RendersWithUnchangedOutput))
        {
            if (component.RendersWithOutputComparison < 20)
            {
                continue;
            }

            var ratio = component.RendersWithUnchangedOutput / component.RendersWithOutputComparison;
            if (ratio < 0.3)
            {
                continue;
            }

            var wastedMs = component.Renders.TotalMs * ratio;
            yield return Create(
                $"{ShortType(component.ComponentType)}: {FormatPercent(ratio)} of renders changed nothing",
                Scale(component.RendersWithUnchangedOutput, 50),
                DiagnosticConfidence.High,
                $"{FormatNumber(component.RendersWithUnchangedOutput)} of {FormatNumber(component.RendersWithOutputComparison)} compared renders of {ShortType(component.ComponentType)} produced a render tree identical to the previous one.",
                "These renders executed BuildRenderTree and were diffed by the renderer, but produced no UI change.",
                "Override ShouldRender to return false when the data the component displays has not changed, or stop the source of the render requests (unneeded StateHasChanged calls, parent re-renders).",
                [
                    Evidence("Compared renders", FormatNumber(component.RendersWithOutputComparison)),
                    Evidence("Identical output", FormatNumber(component.RendersWithUnchangedOutput)),
                    Evidence("Estimated wasted render time", FormatMs(wastedMs), ValueQuality.Estimated),
                ],
                new ImpactEstimate { TotalMs = Math.Round(wastedMs, 3), Occurrences = component.RendersWithUnchangedOutput, Method = "Total BuildRenderTree time multiplied by the share of identical renders (excludes diffing).", Quality = ValueQuality.Estimated },
                componentType: component.ComponentType,
                subject: component.ComponentType,
                caveats: ["The comparison hashes element names, attributes, text, and child component types; event-handler delegates and child component parameters are compared by type only."]);
        }
    }
}

/// <summary>BP1005: slow lifecycle methods or render tree building.</summary>
public sealed class SlowLifecycleRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP1005",
        Title = "Slow lifecycle method",
        Category = DiagnosticCategory.Rendering,
        Description = "Detects lifecycle methods, BuildRenderTree, and parameter updates whose duration exceeds the threshold, distinguishing synchronous (blocking) time from awaited time.",
        RequiredSignals = [ProfilerEventKind.LifecycleMethod, ProfilerEventKind.ComponentRender, ProfilerEventKind.ParametersSet],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Ticks(context.Options.SlowLifecycleMs);
        var results = new List<DiagnosticFinding>();

        // Synchronous lifecycle time (instrumented) blocks the renderer's dispatcher.
        foreach (var group in context.OfType<LifecycleMethodEvent>().Where(e => e.SynchronousDuration >= threshold).GroupBy(e => (e.ComponentType, e.Method)))
        {
            var events = group.ToList();
            var maxMs = context.Ms(events.Max(e => e.SynchronousDuration));
            var totalMs = context.Ms(events.Sum(e => e.SynchronousDuration));
            results.Add(Create(
                $"{ShortType(group.Key.ComponentType)}.{group.Key.Method} blocks for up to {FormatMs(maxMs)}",
                Scale(maxMs, context.Options.SlowLifecycleMs),
                DiagnosticConfidence.High,
                $"{events.Count} call(s) of {group.Key.Method} spent at least {FormatMs(context.Options.SlowLifecycleMs)} running synchronously (max {FormatMs(maxMs)}).",
                "Synchronous lifecycle code runs on the renderer's dispatcher: in WebAssembly it blocks the browser's UI thread, and on Blazor Server it blocks the circuit so no other events or renders for that user can be processed.",
                "Move expensive computation out of lifecycle methods (precompute, cache, or compute lazily), make I/O asynchronous, and avoid synchronous waits such as .Result or .Wait().",
                [
                    Evidence("Slow calls", FormatNumber(events.Count)),
                    Evidence("Maximum synchronous time", FormatMs(maxMs)),
                    Evidence("Total synchronous time", FormatMs(totalMs)),
                ],
                new ImpactEstimate { TotalMs = Math.Round(totalMs, 3), Occurrences = events.Count, Method = "Sum of measured synchronous durations.", Quality = ValueQuality.Measured },
                componentType: group.Key.ComponentType,
                subject: $"{group.Key.ComponentType}.{group.Key.Method}",
                related: events));
        }

        foreach (var group in context.OfType<ComponentRenderEvent>().Where(e => e.Duration >= threshold).GroupBy(e => e.ComponentType))
        {
            var events = group.ToList();
            var maxMs = context.Ms(events.Max(e => e.Duration));
            var totalMs = context.Ms(events.Sum(e => e.Duration));
            results.Add(Create(
                $"{ShortType(group.Key)} BuildRenderTree takes up to {FormatMs(maxMs)}",
                Scale(maxMs, context.Options.SlowLifecycleMs),
                DiagnosticConfidence.High,
                $"{events.Count} render(s) of {ShortType(group.Key)} spent at least {FormatMs(context.Options.SlowLifecycleMs)} building the render tree.",
                "Building the render tree is synchronous. Slow renders usually come from expensive expressions in markup (LINQ over large collections, formatting, sorting) or very large markup.",
                "Compute derived data once when inputs change (OnParametersSet) instead of in markup, split large markup into child components with ShouldRender, and virtualize long lists.",
                [Evidence("Slow renders", FormatNumber(events.Count)), Evidence("Maximum", FormatMs(maxMs)), Evidence("Total", FormatMs(totalMs))],
                new ImpactEstimate { TotalMs = Math.Round(totalMs, 3), Occurrences = events.Count, Method = "Sum of measured BuildRenderTree durations.", Quality = ValueQuality.Measured },
                componentType: group.Key,
                subject: group.Key + ".BuildRenderTree",
                related: events));
        }

        // Framework metrics: SetParametersAsync duration includes awaited work; report with lower confidence.
        var instrumentedTypes = context.Summary.Components.Where(c => c.Instrumented).Select(c => c.ComponentType).ToHashSet(StringComparer.Ordinal);
        foreach (var group in context.OfType<ParametersSetEvent>()
                     .Where(e => e.Source == InstrumentationSource.FrameworkMetrics && e.Duration >= threshold && !instrumentedTypes.Contains(e.ComponentType ?? string.Empty))
                     .GroupBy(e => e.ComponentType))
        {
            var events = group.ToList();
            var maxMs = context.Ms(events.Max(e => e.Duration));
            results.Add(Create(
                $"{ShortType(group.Key)} parameter updates take up to {FormatMs(maxMs)}",
                Cap(Scale(maxMs, context.Options.SlowLifecycleMs), DiagnosticSeverity.Medium),
                DiagnosticConfidence.Medium,
                $"{events.Count} SetParametersAsync call(s) of {ShortType(group.Key)} took at least {FormatMs(context.Options.SlowLifecycleMs)} until their task completed (max {FormatMs(maxMs)}).",
                "Framework metrics measure until SetParametersAsync's task completes, which includes awaited work in OnInitializedAsync and OnParametersSetAsync (for example HTTP calls). Awaited I/O does not block the UI, but the component shows incomplete data until it finishes. Synchronous time cannot be separated without instrumentation.",
                "Inherit ProfiledComponentBase (for example with @inherits in _Imports.razor) to separate synchronous from awaited time. Show a loading state for slow data, cache results, and start independent requests concurrently.",
                [Evidence("Slow parameter updates", FormatNumber(events.Count)), Evidence("Maximum (including awaited work)", FormatMs(maxMs))],
                new ImpactEstimate { Occurrences = events.Count, Method = "Count of SetParametersAsync durations above the threshold.", Quality = ValueQuality.Measured },
                componentType: group.Key,
                subject: group.Key + ".SetParametersAsync",
                caveats: ["The duration includes asynchronous waiting and is not necessarily blocking time."],
                related: events));
        }

        return results.OrderByDescending(f => f.Severity).ThenByDescending(f => f.Impact?.TotalMs ?? 0);
    }
}

/// <summary>BP1006: very many instances of one component type.</summary>
public sealed class ExcessiveComponentInstancesRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP1006",
        Title = "Excessive component instances",
        Category = DiagnosticCategory.Rendering,
        Description = "Detects component types instantiated in very large numbers, in total or in bursts, typically rows or cells of large lists and grids.",
        RequiredSignals = [ProfilerEventKind.ComponentCreated],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Options.ExcessiveComponentInstances;
        var burstThreshold = Math.Max(20, threshold / 5);
        var created = context.OfType<ComponentCreatedEvent>().GroupBy(e => e.ComponentType ?? "(unknown)").ToDictionary(g => g.Key, g => g.ToList());

        foreach (var component in context.Summary.Components.OrderByDescending(c => c.Created))
        {
            created.TryGetValue(component.ComponentType, out var events);
            var peak = events is null ? 0 : PeakRate(SortedByTimestamp(events), context.Clock.Frequency);
            if (component.Created < threshold && peak < burstThreshold)
            {
                continue;
            }

            yield return Create(
                $"{FormatNumber(component.Created)} instances of {ShortType(component.ComponentType)} created",
                Scale(Math.Max(component.Created / threshold, (double)peak / burstThreshold), 1),
                DiagnosticConfidence.Medium,
                $"{FormatNumber(component.Created)} instances of {ShortType(component.ComponentType)} were created during the session, up to {FormatNumber(peak)} within one second.",
                "Every component instance costs memory, lifecycle calls, and diffing. Large lists and grids that render one component per row or cell multiply that cost, and creating many instances at once delays the first paint.",
                "Use <Virtualize> to render only visible items, paginate, render simple rows as markup instead of components, and add @key so list updates reuse instances instead of recreating them.",
                [
                    Evidence("Instances created", FormatNumber(component.Created)),
                    Evidence("Peak creations per second", FormatNumber(peak)),
                    Evidence("Instances disposed", component.Instrumented ? FormatNumber(component.Disposed) : "unavailable (requires ProfiledComponentBase)", component.Instrumented ? ValueQuality.Measured : ValueQuality.Unavailable),
                ],
                new ImpactEstimate { Occurrences = component.Created, Method = "Count of component activations observed by the component activator.", Quality = ValueQuality.Measured },
                componentType: component.ComponentType,
                subject: component.ComponentType,
                caveats: ["Large data displays may legitimately need many instances; Virtualize requires predictable item sizes."],
                related: events?.Take(20));
        }
    }
}

/// <summary>BP1007: slow render batches (excluding server round trips).</summary>
public sealed class SlowRenderBatchRule : DiagnosticRule
{
    private const double FrameBudgetMs = 16.7;

    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP1007",
        Title = "Slow render batches",
        Category = DiagnosticCategory.Rendering,
        Description = "Detects render batches (diff generation and, in WebAssembly, DOM patching) that exceed the slow-render threshold. Server batches, whose duration includes a network round trip, are covered by BP6001.",
        RequiredSignals = [ProfilerEventKind.RenderBatch],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Options.SlowLifecycleMs;
        var batches = context.OfType<RenderBatchEvent>().Where(b => b.IncludesClientRoundTrip != true).ToList();
        var slow = batches.Where(b => context.Ms(b.Duration) >= threshold).ToList();
        if (slow.Count == 0)
        {
            yield break;
        }

        var p95 = context.Ms(PercentileTicks(batches, 95));
        var maxMs = context.Ms(slow.Max(b => b.Duration));
        var overBudgetMs = slow.Sum(b => Math.Max(0, context.Ms(b.Duration) - FrameBudgetMs));
        yield return Create(
            $"{slow.Count} render batch(es) took longer than {FormatMs(threshold)}",
            Scale(maxMs, threshold),
            DiagnosticConfidence.High,
            $"{slow.Count} of {batches.Count} render batches exceeded {FormatMs(threshold)} (max {FormatMs(maxMs)}, p95 {FormatMs(p95)}).",
            "A render batch renders every queued component, computes diffs, and (in WebAssembly) applies them to the DOM synchronously. Batches longer than a frame (about 16 ms) delay paint and input handling.",
            "Find the components that render in these batches (see related interactions and the component view), reduce the number of components re-rendered per change, and move expensive computations out of markup.",
            [
                Evidence("Slow batches", FormatNumber(slow.Count)),
                Evidence("Maximum", FormatMs(maxMs)),
                Evidence("p95 (all batches)", FormatMs(p95)),
                Evidence("Time over a 16.7 ms frame budget", FormatMs(overBudgetMs), ValueQuality.Estimated),
            ],
            new ImpactEstimate { TotalMs = Math.Round(overBudgetMs, 3), Occurrences = slow.Count, Method = "Sum of batch time beyond a 60 Hz frame budget.", Quality = ValueQuality.Estimated },
            subject: "render-batches",
            caveats: ["Browser layout and paint after the DOM update are not included; see browser long tasks for those."],
            related: slow.OrderByDescending(b => b.Duration));
    }
}
