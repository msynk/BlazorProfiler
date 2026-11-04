using BlazorProfiler.Events;
using static BlazorProfiler.Analysis.DiagnosticContext;

namespace BlazorProfiler.Analysis.Rules;

/// <summary>BP2001: slow or long-running event handlers.</summary>
public sealed class SlowEventHandlerRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP2001",
        Title = "Slow event handler",
        Category = DiagnosticCategory.EventHandling,
        Description = "Detects event handlers whose duration exceeds the response budget and explains whether the time was synchronous, spent awaiting HTTP requests, or other asynchronous work.",
        RequiredSignals = [ProfilerEventKind.EventHandler],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var options = context.Options;
        var threshold = context.Ticks(options.SlowEventHandlerMs);
        var results = new List<DiagnosticFinding>();
        foreach (var group in context.OfType<EventHandlerEvent>().GroupBy(HandlerName))
        {
            var all = group.ToList();
            var slow = all.Where(h => h.Duration >= threshold).ToList();
            if (slow.Count == 0)
            {
                continue;
            }

            var worst = slow.MaxBy(h => h.Duration)!;
            var worstMs = context.Ms(worst.Duration);
            var p95Ms = context.Ms(PercentileTicks(all, 95));
            var syncTicks = slow.Where(h => h.SynchronousDuration > 0).Select(h => h.SynchronousDuration).DefaultIfEmpty(0).Max();
            var syncMs = context.Ms(syncTicks);

            double httpMs = 0;
            var httpCount = 0;
            var interopCount = 0;
            foreach (var handler in slow)
            {
                if (handler.OperationId == 0 || !context.ByOperation.TryGetValue(handler.OperationId, out var operationEvents))
                {
                    continue;
                }

                var requests = operationEvents.OfType<HttpRequestEvent>().ToList();
                httpCount += requests.Count;
                interopCount += operationEvents.Count(e => e is JSInteropEvent);
                httpMs += context.Ms(UnionDuration(requests.Where(r => r.Timestamp >= handler.Timestamp && r.Timestamp <= handler.EndTimestamp)));
            }

            var slowTotalMs = slow.Sum(h => context.Ms(h.Duration));
            var httpShare = slowTotalMs > 0 ? httpMs / slowTotalMs : 0;
            var longRunning = worstMs >= options.LongRunningHandlerMs;

            string explanation;
            DiagnosticConfidence confidence;
            if (syncMs >= options.SlowEventHandlerMs)
            {
                explanation = $"The handler ran synchronously for up to {FormatMs(syncMs)}. Synchronous work blocks the UI thread (WebAssembly) or the circuit (Server), so the UI cannot update or respond until it finishes.";
                confidence = DiagnosticConfidence.High;
            }
            else if (httpShare >= 0.5)
            {
                explanation = $"About {FormatPercent(httpShare)} of the slow handlers' time overlapped with HTTP requests they started ({httpCount} request(s)), so the latency is dominated by awaited network calls rather than by .NET execution.";
                confidence = DiagnosticConfidence.Medium;
            }
            else
            {
                explanation = longRunning
                    ? "The handler's task ran for a long time. If the work is awaited asynchronously the UI stays responsive, but the result of the interaction is delayed and users may repeat the action."
                    : "The handler's task took longer than the response budget. Without component instrumentation the profiler cannot separate synchronous execution from awaited time.";
                confidence = syncTicks > 0 ? DiagnosticConfidence.High : DiagnosticConfidence.Low;
            }

            var evidence = new List<FindingEvidence>
            {
                Evidence("Invocations", FormatNumber(all.Count)),
                Evidence("Slow invocations (≥ " + FormatMs(options.SlowEventHandlerMs) + ")", FormatNumber(slow.Count)),
                Evidence("Slowest", FormatMs(worstMs)),
                Evidence("p95", FormatMs(p95Ms)),
                Evidence("Synchronous portion (max)", syncTicks > 0 ? FormatMs(syncMs) : "unavailable (requires ProfiledComponentBase)", syncTicks > 0 ? ValueQuality.Measured : ValueQuality.Unavailable),
                Evidence("Time overlapping its HTTP requests", FormatMs(httpMs), ValueQuality.Inferred),
            };
            if (interopCount > 0)
            {
                evidence.Add(Evidence("JS interop calls in slow invocations", FormatNumber(interopCount)));
            }

            results.Add(Create(
                $"{group.Key} takes up to {FormatMs(worstMs)}",
                longRunning ? DiagnosticSeverity.High : Scale(worstMs, options.SlowEventHandlerMs),
                confidence,
                $"{slow.Count} of {all.Count} invocations of {group.Key} took at least {FormatMs(options.SlowEventHandlerMs)} (slowest {FormatMs(worstMs)}).",
                explanation,
                syncMs >= options.SlowEventHandlerMs
                    ? "Move CPU-heavy work off the handler (precompute, cache, or chunk it with await Task.Yield()), avoid synchronous waits, and render intermediate state before long work."
                    : "Show progress immediately (the first render after the handler's synchronous part), cache or prefetch data, start independent requests concurrently with Task.WhenAll, and support cancellation for repeated interactions.",
                evidence,
                new ImpactEstimate { TotalMs = Math.Round(slowTotalMs, 3), Occurrences = slow.Count, Method = "Sum of measured handler durations above the threshold (handler task duration, not user-perceived latency).", Quality = ValueQuality.Measured },
                componentType: worst.ComponentType,
                subject: group.Key,
                caveats:
                [
                    "Handler duration is not user-perceived latency: browser input delay, network transport (Server), and paint are excluded. See browser interaction findings for perceived latency.",
                    "Overlap with HTTP requests is reported as overlap, not proof that every request was awaited.",
                ],
                related: slow.OrderByDescending(h => h.Duration),
                codeHint: $"{DiagnosticContext.ShortType(worst.ComponentType)}.{worst.MethodName}"));
        }

        return results.OrderByDescending(f => f.Severity).ThenByDescending(f => f.Impact?.TotalMs ?? 0);
    }

    /// <summary>Returns the length of the union of event intervals in ticks.</summary>
    internal static long UnionDuration(IEnumerable<ProfilerEvent> events)
    {
        long total = 0;
        long currentStart = 0;
        long currentEnd = long.MinValue;
        foreach (var e in events.OrderBy(e => e.Timestamp))
        {
            if (e.Timestamp > currentEnd)
            {
                if (currentEnd > long.MinValue)
                {
                    total += currentEnd - currentStart;
                }

                currentStart = e.Timestamp;
                currentEnd = e.EndTimestamp;
            }
            else if (e.EndTimestamp > currentEnd)
            {
                currentEnd = e.EndTimestamp;
            }
        }

        if (currentEnd > long.MinValue)
        {
            total += currentEnd - currentStart;
        }

        return total;
    }
}

/// <summary>BP2002: rapid event streams triggering work.</summary>
public sealed class RapidEventStreamRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP2002",
        Title = "Rapid event stream triggers rendering",
        Category = DiagnosticCategory.EventHandling,
        Description = "Detects handlers invoked many times per second (input, pointer, scroll, key events), each followed by rendering work.",
        RequiredSignals = [ProfilerEventKind.EventHandler, ProfilerEventKind.RenderBatch],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var options = context.Options;
        var batchesByOperation = context.OfType<RenderBatchEvent>().Where(b => b.OperationId != 0).GroupBy(b => b.OperationId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var group in context.OfType<EventHandlerEvent>().GroupBy(HandlerName).OrderByDescending(g => g.Count()))
        {
            var events = group.ToList();
            if (events.Count < 20)
            {
                continue;
            }

            var peak = PeakCount(SortedTimestamps(events), context.Clock.Frequency);
            if (peak < options.RapidEventsPerSecond)
            {
                continue;
            }

            var batches = events.Sum(e => batchesByOperation.TryGetValue(e.OperationId, out var list) ? list.Count : 0);
            var components = events.Sum(e => batchesByOperation.TryGetValue(e.OperationId, out var list) ? list.Sum(b => b.UpdatedComponentCount) : 0);
            var handlerMs = events.Sum(e => context.Ms(e.Duration));
            var batchMs = events.Sum(e => batchesByOperation.TryGetValue(e.OperationId, out var list) ? list.Sum(b => context.Ms(b.Duration)) : 0);
            var rendersPerEvent = (double)batches / events.Count;
            var attribute = events[0].AttributeName ?? "event";

            yield return Create(
                $"{group.Key} fires up to {FormatNumber(peak)} times per second",
                Scale(peak * Math.Max(rendersPerEvent, 0.25), options.RapidEventsPerSecond),
                rendersPerEvent >= 0.5 ? DiagnosticConfidence.High : DiagnosticConfidence.Medium,
                $"{group.Key} ran {events.Count} times, peaking at {FormatNumber(peak)} per second, with {rendersPerEvent:0.##} render batch(es) and {(double)components / events.Count:0.#} component diffs per event.",
                $"Every '{attribute}' event dispatched to .NET runs the handler and then re-renders the receiving component. High-frequency events (typing, pointer movement, scrolling) multiply that work{(events.Any(e => e.CircuitId is not null) ? " and, on Blazor Server, send a message per event over the network" : string.Empty)}.",
                "Debounce or throttle the handler, bind to 'onchange' instead of 'oninput' when per-keystroke updates are not needed (@bind:event), use @bind:after with a debounce, or handle high-frequency pointer and scroll events in JavaScript and only notify .NET of meaningful changes.",
                [
                    Evidence("Invocations", FormatNumber(events.Count)),
                    Evidence("Peak per second", FormatNumber(peak)),
                    Evidence("Render batches correlated to these events", FormatNumber(batches)),
                    Evidence("Handler time", FormatMs(handlerMs)),
                    Evidence("Render batch time", FormatMs(batchMs)),
                ],
                new ImpactEstimate { TotalMs = Math.Round(handlerMs + batchMs, 3), Occurrences = events.Count, Method = "Handler time plus the duration of render batches correlated to the same operations.", Quality = ValueQuality.Measured },
                componentType: events[0].ComponentType,
                subject: group.Key,
                caveats: ["Some UIs (live search, drawing) need per-event updates; prefer throttling over removing them."],
                related: events.Take(20));
        }
    }
}

/// <summary>BP2003: an event handler issues the same request more than once.</summary>
public sealed class DuplicateRequestsInHandlerRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP2003",
        Title = "Event handler issues duplicate requests",
        Category = DiagnosticCategory.EventHandling,
        Description = "Detects operations in which the same URL was requested more than once, attributed to the handler that started the operation.",
        RequiredSignals = [ProfilerEventKind.EventHandler, ProfilerEventKind.HttpRequest],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var handlers = context.OfType<EventHandlerEvent>().Where(h => h.OperationId != 0).ToDictionary(h => h.OperationId);
        var byHandler = new Dictionary<string, (EventHandlerEvent Handler, int Operations, int Duplicates, double WastedMs, List<HttpRequestEvent> Requests)>(StringComparer.Ordinal);
        foreach (var (operationId, events) in context.ByOperation)
        {
            if (!handlers.TryGetValue(operationId, out var handler))
            {
                continue;
            }

            var duplicates = events.OfType<HttpRequestEvent>().GroupBy(r => r.UrlHash).Where(g => g.Count() > 1).ToList();
            if (duplicates.Count == 0)
            {
                continue;
            }

            var key = HandlerName(handler);
            byHandler.TryGetValue(key, out var entry);
            var extra = duplicates.SelectMany(g => g.OrderBy(r => r.Timestamp).Skip(1)).ToList();
            byHandler[key] = (
                handler,
                entry.Operations + 1,
                entry.Duplicates + extra.Count,
                entry.WastedMs + extra.Sum(r => context.Ms(r.Duration)),
                [.. entry.Requests ?? [], .. extra]);
        }

        foreach (var (key, entry) in byHandler.OrderByDescending(p => p.Value.Duplicates))
        {
            var endpoints = entry.Requests.Select(r => r.Endpoint).Distinct().Take(3).ToList();
            yield return Create(
                $"{key} requests the same URL more than once",
                Scale(entry.Duplicates, 2),
                DiagnosticConfidence.High,
                $"In {entry.Operations} invocation(s) of {key}, {entry.Duplicates} request(s) repeated a URL already requested in the same interaction ({string.Join(", ", endpoints)}).",
                "The repeated requests ran in the same interaction's execution context. Duplicate requests usually come from several components loading the same data independently, or from a handler and a lifecycle method both fetching it.",
                "Load the data once and share it (a scoped state service, a cascading value, or a parameter), or deduplicate in-flight requests with a cache keyed by URL.",
                [
                    Evidence("Affected invocations", FormatNumber(entry.Operations)),
                    Evidence("Duplicate requests", FormatNumber(entry.Duplicates)),
                    Evidence("Time spent in duplicates", FormatMs(entry.WastedMs)),
                ],
                new ImpactEstimate { TotalMs = Math.Round(entry.WastedMs, 3), Occurrences = entry.Duplicates, Method = "Sum of durations of repeated requests (first request of each URL excluded).", Quality = ValueQuality.Measured },
                componentType: entry.Handler.ComponentType,
                subject: key,
                caveats: ["Requests for data that legitimately changes between calls (polling, POST commands) are not duplicates."],
                related: entry.Requests);
        }
    }
}

/// <summary>BP2004: an interaction causes many render batches.</summary>
public sealed class ExcessiveRenderingAfterEventRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP2004",
        Title = "Excessive rendering after an event",
        Category = DiagnosticCategory.EventHandling,
        Description = "Detects handlers whose interactions are followed by many render batches, typically from StateHasChanged inside loops or progress callbacks.",
        RequiredSignals = [ProfilerEventKind.EventHandler, ProfilerEventKind.RenderBatch],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var batchesByOperation = context.OfType<RenderBatchEvent>().Where(b => b.OperationId != 0).GroupBy(b => b.OperationId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var group in context.OfType<EventHandlerEvent>().Where(h => h.OperationId != 0).GroupBy(HandlerName))
        {
            var handlers = group.ToList();
            var counts = handlers.Select(h => batchesByOperation.TryGetValue(h.OperationId, out var list) ? list.Count : 0).ToList();
            var average = counts.Average();
            if (average < 5 || handlers.Count < 1)
            {
                continue;
            }

            var batches = handlers.SelectMany(h => batchesByOperation.TryGetValue(h.OperationId, out var list) ? list : []).ToList();
            var totalMs = batches.Sum(b => context.Ms(b.Duration));
            yield return Create(
                $"{group.Key} triggers {average:0.#} render batches per invocation",
                Scale(average, 5),
                DiagnosticConfidence.Medium,
                $"Each of {handlers.Count} invocation(s) of {group.Key} was followed by {average:0.#} render batches on average (max {counts.Max()}).",
                "A handler normally causes one render when its synchronous part completes and one when its task completes. Many more batches indicate StateHasChanged calls inside loops, progress callbacks, or timers started by the handler.",
                "Batch state changes and call StateHasChanged once per meaningful update, throttle progress updates (for example at most every 100 ms), or update progress in a small dedicated child component.",
                [Evidence("Invocations", FormatNumber(handlers.Count)), Evidence("Render batches", FormatNumber(batches.Count)), Evidence("Render batch time", FormatMs(totalMs))],
                new ImpactEstimate { TotalMs = Math.Round(totalMs, 3), Occurrences = batches.Count, Method = "Sum of render batch durations correlated to the handler's operations.", Quality = ValueQuality.Measured },
                componentType: handlers[0].ComponentType,
                subject: group.Key,
                caveats: ["Progress reporting for long operations legitimately renders several times."],
                related: batches.Take(20));
        }
    }
}
