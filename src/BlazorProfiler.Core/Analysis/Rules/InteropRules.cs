using BlazorProfiler.Events;
using static BlazorProfiler.Analysis.DiagnosticContext;

namespace BlazorProfiler.Analysis.Rules;

/// <summary>BP4001: excessive interop calls per operation or per second.</summary>
public sealed class ExcessiveInteropRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP4001",
        Title = "Excessive JavaScript interop calls",
        Category = DiagnosticCategory.Interop,
        Description = "Detects operations with many interop crossings and sustained high interop call rates.",
        RequiredSignals = [ProfilerEventKind.JSInterop],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var options = context.Options;
        var calls = context.OfType<JSInteropEvent>();
        if (calls.Count == 0)
        {
            yield break;
        }

        var server = calls.Any(c => c.CircuitId is not null);
        var handlers = HandlersByOperation(context);

        // Counts are weighted: under sampling each retained call stands for several calls.
        foreach (var operation in calls.Where(c => c.OperationId != 0).GroupBy(c => c.OperationId).Select(g => (g.Key, Calls: g.ToList(), Weight: Weighted(g))).Where(g => g.Weight >= options.ExcessiveInteropCallsPerOperation).OrderByDescending(g => g.Weight).Take(5))
        {
            var list = operation.Calls;
            var count = operation.Weight;
            var quality = Core.ValueQualityFor(list);
            var name = handlers.TryGetValue(operation.Key, out var handler) ? HandlerName(handler) : "operation " + operation.Key;
            var top = list.GroupBy(c => c.Identifier).Select(g => (g.Key, Weight: Weighted(g))).OrderByDescending(g => g.Weight).Take(3).Select(g => $"{g.Key} ×{FormatNumber(g.Weight)}");
            var totalMs = Math.Round(WeightedMs(context, list), 3);
            yield return Create(
                $"{name} makes {FormatNumber(count)} interop calls",
                Scale(count, options.ExcessiveInteropCallsPerOperation),
                DiagnosticConfidence.High,
                $"One invocation of {name} made {FormatNumber(count)} JavaScript interop calls ({string.Join(", ", top)}).",
                server
                    ? "On Blazor Server every interop call is a network round trip over the circuit, so many calls multiply latency."
                    : "Each interop crossing serializes arguments and switches between .NET and JavaScript.",
                "Combine calls into one JavaScript function that performs the whole operation, pass arrays instead of calling per item, and cache values that do not change.",
                [Evidence("Calls in the operation", FormatNumber(count), quality), Evidence("Interop time", FormatMs(totalMs), quality)],
                new ImpactEstimate { TotalMs = totalMs, Occurrences = count, Method = "Sum of measured interop call durations in the operation (weighted by sampling).", Quality = quality },
                subject: name,
                related: list);
        }

        var peak = PeakWeight(SortedByTimestamp(calls), context.Clock.Frequency);
        if (peak >= options.ExcessiveInteropCallsPerSecond)
        {
            var top = calls.GroupBy(c => c.Identifier).Select(g => (g.Key, Weight: Weighted(g))).OrderByDescending(g => g.Weight).Take(3).Select(g => $"{g.Key} ×{FormatNumber(g.Weight)}");
            var quality = Core.ValueQualityFor(calls);
            yield return Create(
                $"Interop calls peak at {FormatNumber(peak)} per second",
                Scale(peak, options.ExcessiveInteropCallsPerSecond),
                DiagnosticConfidence.High,
                $"{FormatNumber(Weighted(calls))} interop calls were made, peaking at {FormatNumber(peak)} per second (top: {string.Join(", ", top)}).",
                server ? "A sustained high rate of interop calls saturates the circuit's connection." : "A sustained high rate of interop calls adds serialization overhead on the UI thread.",
                "Throttle callers (timers, scroll and resize handlers), batch work into fewer calls, and keep high-frequency logic entirely in JavaScript.",
                [Evidence("Peak per second", FormatNumber(peak), quality), Evidence("Total calls", FormatNumber(Weighted(calls)), quality)],
                new ImpactEstimate { Occurrences = Weighted(calls), Method = "Peak calls in any one-second window.", Quality = quality },
                subject: "interop-rate",
                related: calls.Take(20));
        }
    }
}

/// <summary>BP4002: many small calls to the same identifier that could be batched.</summary>
public sealed class BatchableInteropRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP4002",
        Title = "Batchable interop calls",
        Category = DiagnosticCategory.Interop,
        Description = "Detects repeated calls to the same JavaScript function within one operation.",
        RequiredSignals = [ProfilerEventKind.JSInterop],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        // Counts are weighted: under sampling each retained call stands for several calls.
        var groups = context.OfType<JSInteropEvent>()
            .Where(c => c.OperationId != 0 && c.Identifier is not null)
            .GroupBy(c => (c.OperationId, c.Identifier))
            .Select(g => (g.Key.Identifier, Calls: g.ToList(), Weight: Weighted(g)))
            .Where(g => g.Weight >= 10)
            .GroupBy(g => g.Identifier)
            .OrderByDescending(g => g.Sum(x => x.Weight));

        foreach (var identifier in groups)
        {
            var calls = identifier.SelectMany(g => g.Calls).ToList();
            var total = identifier.Sum(g => g.Weight);
            var perOperation = identifier.Max(g => g.Weight);
            var quality = Core.ValueQualityFor(calls);
            var meanMs = calls.Average(c => context.Ms(c.Duration));
            yield return Create(
                $"{identifier.Key} called up to {FormatNumber(perOperation)} times per operation",
                Scale(perOperation, 10),
                DiagnosticConfidence.Medium,
                $"{identifier.Key} was called {FormatNumber(total)} times across {identifier.Count()} operation(s), up to {FormatNumber(perOperation)} times in one operation, averaging {FormatMs(meanMs)} per call.",
                "Repeated calls to the same function in one operation usually iterate over items one at a time. Each call pays the interop crossing cost.",
                "Send all items in one call (an array argument) and loop in JavaScript, or move the loop entirely into JavaScript.",
                [Evidence("Calls", FormatNumber(total), quality), Evidence("Max per operation", FormatNumber(perOperation), quality), Evidence("Mean duration", FormatMs(meanMs))],
                new ImpactEstimate { TotalMs = Math.Round(WeightedMs(context, calls), 3), Occurrences = total, Method = "Sum of measured durations of the repeated calls (weighted by sampling).", Quality = quality },
                subject: identifier.Key,
                caveats: ["Calls whose order or intermediate results matter may not be batchable."],
                related: calls);
        }
    }
}

/// <summary>BP4003: large estimated interop payloads.</summary>
public sealed class LargeInteropPayloadRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP4003",
        Title = "Large interop payloads",
        Category = DiagnosticCategory.Interop,
        Description = "Detects interop calls whose estimated serialized arguments exceed the size threshold (requires Collectors.JSInteropPayloadEstimation).",
        RequiredSignals = [ProfilerEventKind.JSInterop],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Options.LargeInteropPayloadBytes;
        foreach (var group in context.OfType<JSInteropEvent>().Where(c => c.PayloadBytes >= threshold).GroupBy(c => c.Identifier).OrderByDescending(g => g.Max(c => c.PayloadBytes)))
        {
            var calls = group.ToList();
            var max = calls.Max(c => c.PayloadBytes ?? 0);
            yield return Create(
                $"{group.Key} sends up to {FormatBytes(max)} (estimated)",
                Scale(max, threshold),
                DiagnosticConfidence.Medium,
                $"{calls.Count} call(s) to {group.Key} passed arguments estimated at {FormatBytes(threshold)} or more (max {FormatBytes(max)}).",
                "Large payloads are serialized to JSON on every call; on Blazor Server they are also sent over the circuit, where messages above the SignalR maximum message size fail.",
                "Send only the data JavaScript needs, use streaming interop (DotNetStreamReference / IJSStreamReference) for large binary data, or keep large data on one side and exchange identifiers.",
                [Evidence("Calls", FormatNumber(calls.Count)), Evidence("Largest payload", FormatBytes(max), ValueQuality.Estimated)],
                new ImpactEstimate { Occurrences = calls.Count, Method = "Estimated JSON size of the arguments (serialized separately by the profiler; not the framework's exact wire size).", Quality = ValueQuality.Estimated },
                subject: group.Key,
                caveats: ["Sizes are estimates computed with System.Text.Json default options and may differ from the framework's serialization."],
                related: calls);
        }
    }
}

/// <summary>BP4004: slow interop calls.</summary>
public sealed class SlowInteropRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP4004",
        Title = "Slow interop calls",
        Category = DiagnosticCategory.Interop,
        Description = "Detects interop identifiers whose answered calls exceed the slow-interop threshold. Calls without a response are reported by BP4006.",
        RequiredSignals = [ProfilerEventKind.JSInterop],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Options.SlowInteropMs;

        // Only calls that JavaScript answered measure JavaScript: a call without a response (timed out, canceled, or made
        // while the browser was unreachable) measures how long the caller waited, which BP4006 reports instead, and a call
        // that failed before reaching JavaScript measures nothing.
        foreach (var interop in context.Summary.Interop.OrderByDescending(i => i.ResponseDurations.P95Ms))
        {
            var durations = interop.ResponseDurations;
            var slow = durations.Count >= 3 ? durations.P95Ms >= threshold : durations.Count > 0 && durations.MaxMs >= threshold * 3;
            if (!slow)
            {
                continue;
            }

            var related = context.OfType<JSInteropEvent>().Where(c => c.Identifier == interop.Identifier && c.Direction == interop.Direction && c.Answered).OrderByDescending(c => c.Duration).Take(20).ToList();
            var server = related.Any(c => c.CircuitId is not null);
            yield return Create(
                $"{interop.Identifier} p95 is {FormatMs(durations.P95Ms)}",
                Scale(durations.P95Ms, threshold),
                DiagnosticConfidence.Medium,
                $"{FormatNumber(durations.Count)} answered call(s) to {interop.Identifier}: p95 {FormatMs(durations.P95Ms)}, max {FormatMs(durations.MaxMs)}.",
                server
                    ? "On Blazor Server the measured time includes the network round trip to the browser and the JavaScript execution."
                    : "The measured time includes JavaScript execution and, for asynchronous calls, any awaited JavaScript promises (for example user prompts or network calls).",
                "Profile the JavaScript function in the browser developer tools; avoid awaiting long-running JavaScript work in UI paths, and cache results where possible.",
                [Evidence("Answered calls", FormatNumber(durations.Count)), Evidence("p95", FormatMs(durations.P95Ms)), Evidence("Maximum", FormatMs(durations.MaxMs))],
                new ImpactEstimate { TotalMs = durations.TotalMs, Occurrences = durations.Count, Method = "Total measured time of the answered calls.", Quality = ValueQuality.Measured },
                subject: interop.Identifier,
                caveats: ["Calls that intentionally wait for user input (dialogs, permission prompts) are expected to be slow."],
                related: related);
        }
    }
}

/// <summary>BP4006: interop calls that ended without a response from JavaScript.</summary>
public sealed class InteropWithoutResponseRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP4006",
        Title = "Interop calls without a response",
        Category = DiagnosticCategory.Interop,
        Description = "Detects .NET-to-JavaScript calls that timed out, were canceled, were made after the connection to the browser was lost, or were made while interop was unavailable (prerendering).",
        RequiredSignals = [ProfilerEventKind.JSInterop],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var calls = context.OfType<JSInteropEvent>().Where(c => c.Direction == InteropDirection.DotNetToJS && c.EndedWithoutResponse).ToList();
        if (calls.Count == 0)
        {
            yield break;
        }

        var offline = OfflinePeriods(context);
        var groups = calls
            .GroupBy(c => c.Identifier)
            .Select(g => (Identifier: g.Key, Calls: g.ToList(), Weight: Weighted(g)))
            .Where(g => g.Weight >= context.Options.InteropWithoutResponseThreshold)
            .OrderByDescending(g => g.Weight);

        foreach (var group in groups)
        {
            var list = group.Calls;
            var count = group.Weight;
            var quality = Core.ValueQualityFor(list);
            var canceled = list.Where(c => c.Outcome == ProfilerOutcome.Canceled).ToList();
            var timedOut = Weighted(canceled);
            var timedOutOffline = Weighted(canceled.Where(c => StartedOffline(offline, c)));
            var disconnected = Weighted(list.Where(c => c.Outcome == ProfilerOutcome.Error && c.ErrorType == JSInteropEvent.DisconnectedErrorType));
            var unavailable = Weighted(list.Where(c => c.Outcome == ProfilerOutcome.Error && c.ErrorType == JSInteropEvent.UnavailableErrorType));
            var waitedMs = Math.Round(WeightedMs(context, list), 3);
            var maxMs = context.Ms(list.Max(c => c.Duration));

            // Without a browser: calls that failed as disconnected or unavailable, and calls that timed out while their
            // circuit was disconnected (they were never delivered).
            var withoutBrowser = disconnected + unavailable + timedOutOffline >= count / 2;

            var evidence = new List<FindingEvidence> { Evidence("Calls without a response", FormatNumber(count), quality) };
            var reasons = new List<string>();
            var explanation = new List<string>();
            if (timedOut > 0)
            {
                evidence.Add(Evidence("Timed out or canceled", FormatNumber(timedOut), quality));
                reasons.Add($"{FormatNumber(timedOut)} timed out or were canceled" + (timedOutOffline > 0 ? $" ({FormatNumber(timedOutOffline)} of them sent while the circuit was disconnected)" : string.Empty));
                explanation.Add("A call that times out leaves its caller waiting for the interop timeout (one minute by default on Blazor Server). Calls sent while a circuit's connection is down never reach the browser, so they always time out; other causes are JavaScript promises that never settle.");
            }

            if (timedOutOffline > 0)
            {
                evidence.Add(Evidence("Timed out while the circuit was disconnected", FormatNumber(timedOutOffline), quality));
            }

            if (disconnected > 0)
            {
                evidence.Add(Evidence("Failed as disconnected (JSDisconnectedException)", FormatNumber(disconnected), quality));
                reasons.Add($"{FormatNumber(disconnected)} failed because the browser had disconnected");
                explanation.Add("Calls made after the circuit's connection to the browser is gone fail with JSDisconnectedException.");
            }

            if (unavailable > 0)
            {
                evidence.Add(Evidence("Failed as unavailable (InvalidOperationException)", FormatNumber(unavailable), quality));
                reasons.Add($"{FormatNumber(unavailable)} failed because interop was unavailable");
                explanation.Add("Calls made while JavaScript interop is unavailable, for example from a prerendered (statically rendered) component instance, fail with InvalidOperationException.");
            }

            evidence.Add(Evidence("Longest wait", FormatMs(maxMs)));
            if (withoutBrowser)
            {
                explanation.Add("Work that keeps calling JavaScript when no browser is attached usually comes from timers, event subscriptions, or loops that are not stopped when the component is disposed, including instances created during prerendering.");
            }

            yield return Create(
                withoutBrowser
                    ? $"{group.Identifier} called {FormatNumber(count)} times without a connected browser"
                    : $"{FormatNumber(count)} call(s) to {group.Identifier} got no response",
                Cap(Scale(count, context.Options.InteropWithoutResponseThreshold), DiagnosticSeverity.High),
                DiagnosticConfidence.High,
                $"{FormatNumber(count)} call(s) to {group.Identifier} ended without a response from JavaScript: {string.Join("; ", reasons)}. Callers waited up to {FormatMs(maxMs)}.",
                string.Join(" ", explanation),
                "Stop timers, unsubscribe events, and cancel background work in Dispose/DisposeAsync; pass a CancellationToken (or a short timeout) to InvokeAsync for calls that may not complete; make sure every JavaScript promise the call awaits resolves or rejects; and on Blazor Server avoid interop from background work, or pause it while the circuit is disconnected (CircuitHandler).",
                evidence,
                new ImpactEstimate { TotalMs = waitedMs, Occurrences = count, Method = "Sum of the time callers waited for calls that ended without a response.", Quality = quality },
                subject: group.Identifier,
                caveats: ["Calls canceled on purpose (for example when the user navigates away) are expected; timeouts and cancellations are not distinguished."],
                related: list.OrderByDescending(c => c.Duration));
        }
    }

    // Per circuit, the periods in which interop calls cannot reach the browser: from each connection loss to the next
    // reconnection, and from the circuit's closing on.
    private static Dictionary<string, List<(long Start, long End)>> OfflinePeriods(DiagnosticContext context)
    {
        var periods = new Dictionary<string, List<(long Start, long End)>>(StringComparer.Ordinal);
        foreach (var circuit in context.OfType<CircuitEvent>().Where(c => c.CircuitId is not null).GroupBy(c => c.CircuitId!))
        {
            var list = new List<(long Start, long End)>();
            long? downSince = null;
            foreach (var change in circuit.OrderBy(c => c.Timestamp))
            {
                switch (change.Change)
                {
                    case CircuitChange.ConnectionDown:
                        downSince ??= change.Timestamp;
                        break;
                    case CircuitChange.ConnectionUp or CircuitChange.Opened when downSince is { } since:
                        list.Add((since, change.Timestamp));
                        downSince = null;
                        break;
                    case CircuitChange.Closed:
                        downSince ??= change.Timestamp;
                        break;
                }
            }

            if (downSince is { } open)
            {
                list.Add((open, long.MaxValue));
            }

            if (list.Count > 0)
            {
                periods[circuit.Key] = list;
            }
        }

        return periods;
    }

    private static bool StartedOffline(Dictionary<string, List<(long Start, long End)>> offline, JSInteropEvent call) =>
        call.CircuitId is not null && offline.TryGetValue(call.CircuitId, out var periods) && periods.Any(p => call.Timestamp >= p.Start && call.Timestamp < p.End);
}

/// <summary>BP4005: interop calls from a component after it was disposed.</summary>
public sealed class InteropAfterDisposeRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP4005",
        Title = "Interop after component disposal",
        Category = DiagnosticCategory.Interop,
        Description = "Detects interop calls attributed to a component instance that started after the instance was disposed (requires ProfiledComponentBase).",
        RequiredSignals = [ProfilerEventKind.JSInterop, ProfilerEventKind.ComponentDisposed],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var disposed = context.OfType<ComponentDisposedEvent>().Where(d => d.ComponentInstanceId != 0).GroupBy(d => d.ComponentInstanceId).ToDictionary(g => g.Key, g => g.Min(d => d.Timestamp));
        if (disposed.Count == 0)
        {
            yield break;
        }

        var late = context.OfType<JSInteropEvent>()
            .Where(c => c.OriginComponentInstanceId != 0 && disposed.TryGetValue(c.OriginComponentInstanceId, out var at) && c.Timestamp > at)
            .GroupBy(c => (c.OriginComponentType, c.Identifier));

        foreach (var group in late.OrderByDescending(g => g.Count()))
        {
            var calls = group.ToList();
            yield return Create(
                $"{ShortType(group.Key.OriginComponentType)} calls {group.Key.Identifier} after disposal",
                calls.Count >= 10 ? DiagnosticSeverity.High : DiagnosticSeverity.Medium,
                DiagnosticConfidence.High,
                $"{calls.Count} interop call(s) to {group.Key.Identifier} were started by {calls.Select(c => c.OriginComponentInstanceId).Distinct().Count()} instance(s) of {ShortType(group.Key.OriginComponentType)} after those instances were disposed.",
                "Work continuing after disposal usually comes from timers, event subscriptions, or async operations that are not canceled. It wastes resources, may throw JSDisconnectedException or ObjectDisposedException, and can keep the component in memory.",
                "Implement IDisposable/IAsyncDisposable to stop timers, unsubscribe events, cancel a CancellationTokenSource passed to async work, and dispose IJSObjectReference/DotNetObjectReference instances.",
                [Evidence("Late calls", FormatNumber(calls.Count))],
                new ImpactEstimate { Occurrences = calls.Count, Method = "Count of calls whose start followed the originating instance's disposal.", Quality = ValueQuality.Measured },
                componentType: group.Key.OriginComponentType,
                subject: $"{group.Key.OriginComponentType}|{group.Key.Identifier}",
                related: calls);
        }
    }
}
