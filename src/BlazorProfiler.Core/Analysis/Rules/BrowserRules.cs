using BlazorProfiler.Events;
using static BlazorProfiler.Analysis.DiagnosticContext;

namespace BlazorProfiler.Analysis.Rules;

/// <summary>A script of long animation frames, aggregated by its label (function and source file, or invoker).</summary>
public sealed class LongFrameScriptStats
{
    /// <summary>The display label (see <see cref="LongFrameScript.Label"/>).</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>The invoker of the longest occurrence.</summary>
    public string? Invoker { get; init; }

    /// <summary>The invoker type of the longest occurrence.</summary>
    public string? InvokerType { get; init; }

    /// <summary>The sanitized source URL, when known.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>The number of long frames the script ran in.</summary>
    public int Frames { get; init; }

    /// <summary>Total script duration in milliseconds.</summary>
    public double TotalMs { get; init; }

    /// <summary>Total forced style and layout in milliseconds.</summary>
    public double ForcedLayoutMs { get; init; }

    /// <summary>The longest single duration in milliseconds.</summary>
    public double MaxMs { get; init; }

    /// <summary>Whether the script belongs to the Blazor framework (<c>blazor.*.js</c>, <c>dotnet.*.js</c>).</summary>
    public bool IsFramework { get; init; }

    /// <summary>Aggregates the scripts of long animation frames, longest total first.</summary>
    public static List<LongFrameScriptStats> Aggregate(IEnumerable<BrowserLongTaskEvent> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var groups = new Dictionary<string, List<(LongFrameScript Script, long FrameId)>>(StringComparer.Ordinal);
        foreach (var frame in frames)
        {
            foreach (var script in frame.Scripts ?? [])
            {
                if (script is null)
                {
                    continue;
                }

                var label = script.Label;
                if (!groups.TryGetValue(label, out var list))
                {
                    groups[label] = list = [];
                }

                list.Add((script, frame.Id));
            }
        }

        return groups
            .Select(g =>
            {
                var longest = g.Value.MaxBy(s => s.Script.DurationMs).Script;
                return new LongFrameScriptStats
                {
                    Label = g.Key,
                    Invoker = longest.Invoker,
                    InvokerType = longest.InvokerType,
                    SourceUrl = longest.SourceUrl,
                    Frames = g.Value.Select(s => s.FrameId).Distinct().Count(),
                    TotalMs = Math.Round(g.Value.Sum(s => s.Script.DurationMs), 3),
                    ForcedLayoutMs = Math.Round(g.Value.Sum(s => s.Script.ForcedStyleAndLayoutMs), 3),
                    MaxMs = Math.Round(longest.DurationMs, 3),
                    IsFramework = IsFrameworkScript(longest.SourceUrl) || IsFrameworkScript(longest.Invoker),
                };
            })
            .OrderByDescending(s => s.TotalMs)
            .ToList();
    }

    /// <summary>Whether a script URL belongs to Blazor or the .NET WebAssembly runtime.</summary>
    public static bool IsFrameworkScript(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        var file = url[(url.LastIndexOf('/') + 1)..];
        return file.StartsWith("blazor.", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("dotnet.", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/_framework/", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>BP7006: forced synchronous layout (layout thrashing) inside long animation frames.</summary>
public sealed class ForcedLayoutRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP7006",
        Title = "Forced synchronous layout",
        Category = DiagnosticCategory.Browser,
        Description = "Detects long animation frames in which scripts forced the browser to compute style and layout synchronously (reading element geometry after changing the DOM), and attributes the time to the scripts that caused it.",
        RequiredSignals = [ProfilerEventKind.BrowserLongTask],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Options.ForcedLayoutMs;
        var frames = context.OfType<BrowserLongTaskEvent>().Where(f => f.ForcedStyleAndLayoutMs >= threshold).ToList();
        if (frames.Count == 0)
        {
            yield break;
        }

        var total = frames.Sum(f => f.ForcedStyleAndLayoutMs);
        var max = frames.Max(f => f.ForcedStyleAndLayoutMs);
        var allScripts = LongFrameScriptStats.Aggregate(frames).Where(s => s.ForcedLayoutMs > 0).OrderByDescending(s => s.ForcedLayoutMs).ToList();
        var scripts = allScripts.Take(3).ToList();
        var interop = context.OfType<JSInteropEvent>()
            .Where(j => j.Direction == InteropDirection.DotNetToJS && frames.Any(f => j.Timestamp <= f.EndTimestamp && j.EndTimestamp >= f.Timestamp))
            .ToList();

        // Blazor runs .NET code (and the JavaScript it calls through interop) inside its own event listeners and promise
        // callbacks, so the browser often attributes the forced layout to a Blazor framework script. The interop call that
        // ran during the frame is then the real culprit; each frame's forced layout is attributed to the overlapping call
        // that overlapped it the longest.
        var byInterop = new Dictionary<string, (double ForcedMs, int Frames)>(StringComparer.Ordinal);
        foreach (var frame in frames)
        {
            var call = interop
                .Where(j => j.Identifier is not null && j.Timestamp <= frame.EndTimestamp && j.EndTimestamp >= frame.Timestamp)
                .MaxBy(j => Math.Min(j.EndTimestamp, frame.EndTimestamp) - Math.Max(j.Timestamp, frame.Timestamp));
            if (call is not null)
            {
                var current = byInterop.GetValueOrDefault(call.Identifier!);
                byInterop[call.Identifier!] = (current.ForcedMs + frame.ForcedStyleAndLayoutMs, current.Frames + 1);
            }
        }

        var interopCulprit = byInterop.OrderByDescending(p => p.Value.ForcedMs).Select(p => (Identifier: p.Key, p.Value.ForcedMs, p.Value.Frames)).FirstOrDefault();
        // An application script is the culprit when it caused at least as much forced layout as Blazor's own entry points;
        // otherwise the framework attribution hides the real cause, which is then the interop call running in the frame.
        var frameworkForced = allScripts.Where(s => s.IsFramework).Sum(s => s.ForcedLayoutMs);
        var appScript = scripts.FirstOrDefault(s => !s.IsFramework && (s.ForcedLayoutMs >= frameworkForced || byInterop.Count == 0));
        string title;
        string subject;
        DiagnosticConfidence confidence;
        string? codeHint;
        if (appScript is not null)
        {
            title = $"{appScript.Label} forces synchronous layout ({FormatMs(appScript.ForcedLayoutMs)})";
            subject = appScript.Label;
            confidence = DiagnosticConfidence.High;
            codeHint = appScript.SourceUrl is { } url ? $"See {appScript.Label} in {url}." : null;
        }
        else if (interopCulprit.Identifier is not null)
        {
            title = $"JS interop call {interopCulprit.Identifier} forces synchronous layout ({FormatMs(interopCulprit.ForcedMs)})";
            subject = "interop:" + interopCulprit.Identifier;
            confidence = DiagnosticConfidence.Medium;
            codeHint = $"The JavaScript function behind {interopCulprit.Identifier}: separate DOM writes from geometry reads.";
        }
        else
        {
            title = scripts.Count > 0
                ? $"{scripts[0].Label} forces synchronous layout ({FormatMs(scripts[0].ForcedLayoutMs)})"
                : $"Forced synchronous layout cost {FormatMs(total)} in {frames.Count} frame(s)";
            subject = scripts.FirstOrDefault()?.Label ?? "forced-layout";
            confidence = scripts.Count > 0 ? DiagnosticConfidence.Medium : DiagnosticConfidence.Low;
            codeHint = null;
        }

        var evidence = new List<FindingEvidence>
        {
            Evidence("Frames with forced layout", FormatNumber(frames.Count)),
            Evidence("Forced style and layout (total)", FormatMs(total)),
            Evidence("Forced style and layout (worst frame)", FormatMs(max)),
            Evidence(
                "Scripts that forced layout",
                scripts.Count > 0 ? string.Join("; ", scripts.Select(s => $"{s.Label}{(s.IsFramework ? " [Blazor/.NET entry point]" : string.Empty)}: {FormatMs(s.ForcedLayoutMs)}")) : "not attributed by the browser",
                scripts.Count > 0 ? ValueQuality.Measured : ValueQuality.Unavailable),
            Evidence(
                "JS interop calls running during these frames",
                byInterop.Count > 0 ? string.Join("; ", byInterop.OrderByDescending(p => p.Value.ForcedMs).Take(3).Select(p => $"{p.Key}: {FormatMs(p.Value.ForcedMs)} in {p.Value.Frames} frame(s)")) : "none",
                ValueQuality.Inferred),
        };

        var caveats = new List<string>
        {
            "Only frames longer than 50 ms are reported by the browser; short frames with forced layout are not counted.",
            "Script attribution requires the Long Animation Frames API (Chromium).",
        };
        if (appScript is null && interopCulprit.Identifier is not null)
        {
            caveats.Add("The browser attributed the layout to Blazor's own entry point; the interop call is identified because it ran during the same frame (temporal correlation).");
        }

        yield return Create(
            title,
            Scale(max, threshold),
            confidence,
            $"{frames.Count} long animation frame(s) spent at least {FormatMs(threshold)} in forced style and layout (total {FormatMs(total)}, worst {FormatMs(max)}).",
            "A script that changes the DOM and then reads geometry (offsetHeight, getBoundingClientRect, scrollTop, getComputedStyle) forces the browser to recompute style and layout synchronously. Doing that in a loop (read, write, read, write) is layout thrashing. In Blazor this typically happens in JavaScript called through interop right after a render batch patched the DOM, or in OnAfterRenderAsync measurements of many elements.",
            "Batch DOM reads before writes, measure once per frame (requestAnimationFrame), replace polling of sizes with ResizeObserver or IntersectionObserver, and call measuring interop once for many elements instead of once per element. Prefer CSS (container queries, position: sticky) over measuring in script.",
            evidence,
            new ImpactEstimate { TotalMs = Math.Round(total, 3), Occurrences = frames.Count, Method = "Sum of forcedStyleAndLayoutDuration of long animation frame scripts.", Quality = ValueQuality.Measured },
            subject: subject,
            caveats: caveats,
            related: frames.OrderByDescending(f => f.ForcedStyleAndLayoutMs).Cast<ProfilerEvent>().Concat(interop.Take(10)),
            codeHint: codeHint);
    }
}

/// <summary>BP7007: rage clicks and dead clicks.</summary>
public sealed class FrustrationSignalRule : DiagnosticRule
{
    private const double CorrelationWindowMs = 1_000;

    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP7007",
        Title = "Rage clicks or dead clicks",
        Category = DiagnosticCategory.Browser,
        Description = "Reports elements users clicked repeatedly in quick succession (rage clicks) or clicked without the page changing (dead clicks), with the .NET work that was running at that moment.",
        RequiredSignals = [ProfilerEventKind.BrowserFrustration],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var signals = context.OfType<BrowserFrustrationEvent>();
        if (signals.Count == 0)
        {
            yield break;
        }

        var window = context.Ticks(CorrelationWindowMs);
        var busy = context.Events
            .Where(e => e is EventHandlerEvent or RenderBatchEvent or HttpRequestEvent or BrowserLongTaskEvent or CircuitActivityEvent)
            .ToList();

        foreach (var group in signals.GroupBy(s => s.Target ?? s.TargetTag ?? "(element)").OrderByDescending(g => g.Count()))
        {
            var list = group.ToList();
            var rage = list.Where(s => s.Signal == FrustrationSignal.RageClick).ToList();
            var dead = list.Where(s => s.Signal == FrustrationSignal.DeadClick).ToList();
            if (rage.Count == 0 && dead.Count < context.Options.FrustrationThreshold)
            {
                continue;
            }

            var overlapping = busy
                .Where(e => list.Any(s => e.Timestamp <= s.EndTimestamp + window && e.EndTimestamp >= s.Timestamp - window))
                .ToList();
            var slowHandlers = overlapping.OfType<EventHandlerEvent>().Where(h => context.Ms(h.Duration) >= context.Options.SlowEventHandlerMs).ToList();
            var requests = overlapping.OfType<HttpRequestEvent>().ToList();
            var longFrames = overlapping.OfType<BrowserLongTaskEvent>().ToList();
            var handlers = overlapping.OfType<EventHandlerEvent>().ToList();
            var maxClicks = rage.Count > 0 ? rage.Max(r => r.ClickCount) : 0;

            string cause;
            if (slowHandlers.Count > 0)
            {
                var slowest = slowHandlers.MaxBy(h => h.Duration)!;
                cause = $"A slow handler was running: {HandlerName(slowest)} took {FormatMs(context.Ms(slowest.Duration))} without visible feedback.";
            }
            else if (longFrames.Count > 0)
            {
                cause = $"The main thread was blocked by {longFrames.Count} long frame(s) (max {FormatMs(context.Ms(longFrames.Max(f => f.Duration)))}), so the clicks could not be handled.";
            }
            else if (requests.Count > 0)
            {
                cause = $"{requests.Count} HTTP request(s) were in flight; the page likely waited for data without showing progress.";
            }
            else if (handlers.Count == 0 && dead.Count > 0)
            {
                cause = "No .NET event handler ran for these clicks: the element may have no @onclick handler, the component may not be interactive yet (prerendered or static rendering), or the handler may be attached to another element.";
            }
            else
            {
                cause = "Handlers ran but did not change the page.";
            }

            var target = group.Key;
            var title = rage.Count > 0
                ? $"Rage clicks on {target} (up to {maxClicks} clicks)"
                : $"{dead.Count} dead click(s) on {target}";
            yield return Create(
                title,
                rage.Count > 0 ? (slowHandlers.Count > 0 || longFrames.Count > 0 ? DiagnosticSeverity.High : DiagnosticSeverity.Medium) : DiagnosticSeverity.Low,
                overlapping.Count > 0 || handlers.Count == 0 ? DiagnosticConfidence.Medium : DiagnosticConfidence.Low,
                $"Users clicked {target} {FormatNumber(rage.Sum(r => r.ClickCount) + dead.Count)} time(s) in {list.Count} frustration signal(s): {rage.Count} rage click burst(s) and {dead.Count} dead click(s). {cause}",
                "Repeated clicks on the same spot and clicks that change nothing are the strongest signals that the UI felt unresponsive. In Blazor the usual causes are an awaited handler that shows no progress, a WebAssembly handler that blocks the main thread, a Blazor Server circuit with high latency, or a button rendered by static or prerendered markup before the component became interactive.",
                "Give immediate feedback: disable the button and show progress before awaiting (render a busy state, then await), keep the main thread free (await Task.Yield() in long WebAssembly work), make sure the element is rendered by an interactive component, and guard handlers against re-entrance so repeated clicks do not start duplicate work.",
                [
                    Evidence("Rage click bursts", FormatNumber(rage.Count)),
                    Evidence("Dead clicks", FormatNumber(dead.Count)),
                    Evidence("Most clicks in one burst", maxClicks > 0 ? FormatNumber(maxClicks) : "n/a"),
                    Evidence("Handlers within 1 s", FormatNumber(handlers.Count), ValueQuality.Inferred),
                    Evidence("Slow handlers within 1 s", FormatNumber(slowHandlers.Count), ValueQuality.Inferred),
                    Evidence("Long frames within 1 s", FormatNumber(longFrames.Count), ValueQuality.Inferred),
                ],
                new ImpactEstimate { Occurrences = list.Count, Method = "Number of rage-click bursts and dead clicks detected by the browser module.", Quality = ValueQuality.Measured },
                subject: target,
                caveats:
                [
                    "Dead clicks are detected from the absence of DOM changes within one second; clicks that intentionally change nothing visible (for example copying to the clipboard) are counted too.",
                    "The connection to .NET work is temporal (within one second of the clicks).",
                ],
                related: list.Cast<ProfilerEvent>().Concat(overlapping.Take(10)));
        }
    }
}

/// <summary>BP7008: interaction results that appear late (interaction contentful paint).</summary>
public sealed class LateInteractionResultRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP7008",
        Title = "Interaction results appear late",
        Category = DiagnosticCategory.Browser,
        Description = "Detects interactions whose changed content painted long after the input (interaction contentful paint), although the first paint after the input was quick. That latency is invisible to INP: it comes from asynchronous work such as an awaited handler loading data.",
        RequiredSignals = [ProfilerEventKind.BrowserInteraction],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Options.SlowInteractionMs;
        var late = context.OfType<BrowserInteractionEvent>()
            .Where(i => i.ContentPaintMs is { } cp && cp >= threshold && cp >= 2 * context.Ms(i.Duration))
            .ToList();
        // One click reports pointerdown, pointerup, and click entries; they are grouped as one kind of interaction.
        static string KindOf(string? type) => type switch
        {
            "pointerdown" or "pointerup" or "mousedown" or "mouseup" or "click" or "dblclick" or "contextmenu" or "auxclick" => "click",
            "keydown" or "keyup" or "keypress" => "key press",
            null => "interaction",
            _ => type,
        };

        foreach (var group in late.GroupBy(i => KindOf(i.EventType) + (i.TargetTag is null ? string.Empty : " on <" + i.TargetTag + ">")).OrderByDescending(g => g.Max(i => i.ContentPaintMs)))
        {
            var interactions = group.ToList();
            var worst = interactions.Max(i => i.ContentPaintMs!.Value);
            var firstPaint = interactions.Average(i => context.Ms(i.Duration));
            var contentPaint = interactions.Average(i => i.ContentPaintMs!.Value);

            // What ran between the input and the content paint (temporal correlation).
            bool During(ProfilerEvent e, BrowserInteractionEvent i) => e.Timestamp <= i.Timestamp + context.Ticks(i.ContentPaintMs!.Value) && e.EndTimestamp >= i.Timestamp;
            var handlers = context.OfType<EventHandlerEvent>().Where(h => interactions.Any(i => During(h, i))).ToList();
            var requests = context.OfType<HttpRequestEvent>().Where(r => interactions.Any(i => During(r, i))).ToList();
            var queries = context.OfType<DatabaseCommandEvent>().Where(q => interactions.Any(i => During(q, i))).ToList();
            var handlerNames = handlers.Select(HandlerName).Distinct().Take(3).ToList();
            var requestMs = requests.Count > 0 ? requests.Max(r => context.Ms(r.Duration)) : 0;

            var cause = requests.Count > 0
                ? $"{requests.Count} HTTP request(s) ran meanwhile (longest {FormatMs(requestMs)}, matched by time), so the content probably waited for data."
                : handlers.Count > 0
                    ? $"A .NET handler ran during the wait ({handlerNames[0]}, matched by time); awaited work in it probably delayed the render that showed the result."
                    : "No .NET work was matched by time; the delay may come from JavaScript or timers.";

            yield return Create(
                $"Results of '{group.Key}' appear after {FormatMs(worst)}",
                Scale(worst, threshold),
                DiagnosticConfidence.High,
                $"{interactions.Count} '{group.Key}' interaction(s) painted their changed content {FormatMs(contentPaint)} after the input on average (worst {FormatMs(worst)}), while the first paint came after {FormatMs(firstPaint)}. {cause}",
                "Interaction to Next Paint ends at the first paint after the event handlers, so an asynchronous handler looks fast even when users wait much longer for its result. In Blazor WebAssembly, an awaited handler renders once before the await (often unchanged) and again when the data arrives; the second render is what users wait for.",
                "Show feedback immediately (render a busy state before the await), then make the awaited work faster: cache or prefetch data, run independent requests in parallel, render partial results as they arrive, and move work that is not needed for the result after it. If the work is unavoidable, make the wait explicit with progress.",
                [
                    Evidence("Interactions with late results", FormatNumber(interactions.Count)),
                    Evidence("Input to content paint (worst)", FormatMs(worst)),
                    Evidence("Input to first paint (average)", FormatMs(firstPaint)),
                    Evidence(".NET handlers during the wait", handlerNames.Count > 0 ? string.Join("; ", handlerNames) : "none", ValueQuality.Inferred),
                    Evidence("HTTP requests during the wait", FormatNumber(requests.Count), ValueQuality.Inferred),
                    Evidence("Database commands during the wait", FormatNumber(queries.Count), ValueQuality.Inferred),
                ],
                new ImpactEstimate { TotalMs = Math.Round(interactions.Sum(i => i.ContentPaintMs!.Value - context.Ms(i.Duration)), 3), Occurrences = interactions.Count, Method = "Sum of the time between the first paint after each input and the paint of its content.", Quality = ValueQuality.Measured },
                subject: group.Key,
                caveats:
                [
                    "Interaction contentful paint is reported by Chromium only, and not for Blazor Server, whose updates arrive over the circuit outside the interaction (see BP6001 for server round trips).",
                    "The browser attributes content painted after JavaScript interop promises to the interaction, but not reliably after .NET timers (Task.Delay) or HttpClient responses, whose continuations the .NET runtime resumes outside the interaction's task chain; for those, BP2001 reports the slow handler instead.",
                    "Handlers and requests are matched to interactions by time.",
                ],
                related: interactions.Cast<ProfilerEvent>().Concat(handlers.Take(5)).Concat(requests.Take(5)));
        }
    }
}

/// <summary>BP7009: excessive DOM size.</summary>
public sealed class ExcessiveDomSizeRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP7009",
        Title = "Excessive DOM size",
        Category = DiagnosticCategory.Browser,
        Description = "Reports pages whose document reached DomElementsWarning (Low severity) or LargeDomElements elements or more, with the component types created in large numbers that likely produced them.",
        RequiredSignals = [ProfilerEventKind.BrowserVital],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Options.LargeDomElements;
        var samples = context.OfType<BrowserVitalEvent>().Where(v => v.Name == "DOM").ToList();
        if (samples.Count == 0)
        {
            yield break;
        }

        var largest = samples.MaxBy(v => v.Value)!;
        if (largest.Value < context.Options.DomElementsWarning)
        {
            yield break;
        }

        var severity = largest.Value < threshold ? DiagnosticSeverity.Low : (DiagnosticSeverity)Math.Max((int)DiagnosticSeverity.Medium, (int)Scale(largest.Value, threshold / 2.0));

        // Components created in large numbers are the usual source of large Blazor DOMs (rows and cells of lists and grids).
        var many = context.Summary.Components.Where(c => c.Created >= 50).OrderByDescending(c => c.Created).Take(3).ToList();
        var server = context.Events.Any(e => e.CircuitId is not null);
        yield return Create(
            $"The page reached {FormatNumber(largest.Value)} DOM elements",
            severity,
            DiagnosticConfidence.High,
            $"The document grew to {FormatNumber(largest.Value)} elements (Lighthouse warns from {FormatNumber(context.Options.DomElementsWarning)} and fails from {FormatNumber(threshold)}).{(many.Count > 0 ? $" Component types created in large numbers: {string.Join(", ", many.Select(c => $"{ShortType(c.ComponentType)} ×{FormatNumber(c.Created)}"))}." : string.Empty)}",
            "A large DOM makes every style recalculation and layout slower, raises memory use, and makes interactions slower to paint. In Blazor it also means more render tree frames to diff and, " +
            (server ? "on Blazor Server, larger initial render batches sent over the circuit." : "in WebAssembly, more DOM patching on the UI thread."),
            "Render only what is visible: use <Virtualize> for long lists and grids, paginate, render collapsed sections (tabs, accordions, dialogs) conditionally with @if instead of hiding them with CSS, and simplify per-row markup (fewer wrapper elements).",
            [
                Evidence("Largest DOM size", FormatNumber(largest.Value) + " elements"),
                Evidence("Thresholds", $"{FormatNumber(context.Options.DomElementsWarning)} (warning) / {FormatNumber(threshold)} (excessive) elements"),
                many.Count > 0
                    ? Evidence("Component types created in large numbers", string.Join("; ", many.Select(c => $"{ShortType(c.ComponentType)} ×{FormatNumber(c.Created)}")), ValueQuality.Inferred)
                    : Evidence("Component types created in large numbers", "none (the elements come from markup)", ValueQuality.Measured),
            ],
            new ImpactEstimate { Occurrences = largest.Value, Method = "Elements in the document (document.getElementsByTagName('*')), the largest value reported.", Quality = ValueQuality.Measured },
            subject: "dom-size",
            caveats:
            [
                "Lighthouse's thresholds (800 warn, 1,400 fail) are guidelines; very simple elements cost less than deep, styled trees.",
                "The DOM size is sampled every few seconds and reported when it changes by 10% or more, so short peaks can be missed.",
            ],
            related: samples.OrderByDescending(v => v.Value).Take(5));
    }
}
