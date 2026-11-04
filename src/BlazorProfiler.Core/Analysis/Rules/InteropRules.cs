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
        var handlers = context.OfType<EventHandlerEvent>().Where(h => h.OperationId != 0).ToDictionary(h => h.OperationId);
        foreach (var operation in calls.Where(c => c.OperationId != 0).GroupBy(c => c.OperationId).Where(g => g.Count() >= options.ExcessiveInteropCallsPerOperation).OrderByDescending(g => g.Count()).Take(5))
        {
            var list = operation.ToList();
            var name = handlers.TryGetValue(operation.Key, out var handler) ? HandlerName(handler) : "operation " + operation.Key;
            var top = list.GroupBy(c => c.Identifier).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} ×{g.Count()}");
            yield return Create(
                $"{name} makes {list.Count} interop calls",
                Scale(list.Count, options.ExcessiveInteropCallsPerOperation),
                DiagnosticConfidence.High,
                $"One invocation of {name} made {list.Count} JavaScript interop calls ({string.Join(", ", top)}).",
                server
                    ? "On Blazor Server every interop call is a network round trip over the circuit, so many calls multiply latency."
                    : "Each interop crossing serializes arguments and switches between .NET and JavaScript.",
                "Combine calls into one JavaScript function that performs the whole operation, pass arrays instead of calling per item, and cache values that do not change.",
                [Evidence("Calls in the operation", FormatNumber(list.Count)), Evidence("Interop time", FormatMs(list.Sum(c => context.Ms(c.Duration))))],
                new ImpactEstimate { TotalMs = Math.Round(list.Sum(c => context.Ms(c.Duration)), 3), Occurrences = list.Count, Method = "Sum of measured interop call durations in the operation.", Quality = ValueQuality.Measured },
                subject: name,
                related: list);
        }

        var peak = PeakCount(SortedTimestamps(calls), context.Clock.Frequency);
        if (peak >= options.ExcessiveInteropCallsPerSecond)
        {
            var top = calls.GroupBy(c => c.Identifier).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} ×{g.Count()}");
            yield return Create(
                $"Interop calls peak at {FormatNumber(peak)} per second",
                Scale(peak, options.ExcessiveInteropCallsPerSecond),
                DiagnosticConfidence.High,
                $"{FormatNumber(Weighted(calls))} interop calls were made, peaking at {FormatNumber(peak)} per second (top: {string.Join(", ", top)}).",
                server ? "A sustained high rate of interop calls saturates the circuit's connection." : "A sustained high rate of interop calls adds serialization overhead on the UI thread.",
                "Throttle callers (timers, scroll and resize handlers), batch work into fewer calls, and keep high-frequency logic entirely in JavaScript.",
                [Evidence("Peak per second", FormatNumber(peak)), Evidence("Total calls", FormatNumber(Weighted(calls)), Core.ValueQualityFor(calls))],
                new ImpactEstimate { Occurrences = Weighted(calls), Method = "Peak calls in any one-second window.", Quality = ValueQuality.Measured },
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
        var groups = context.OfType<JSInteropEvent>()
            .Where(c => c.OperationId != 0 && c.Identifier is not null)
            .GroupBy(c => (c.OperationId, c.Identifier))
            .Where(g => g.Count() >= 10)
            .GroupBy(g => g.Key.Identifier)
            .OrderByDescending(g => g.Sum(x => x.Count()));

        foreach (var identifier in groups)
        {
            var calls = identifier.SelectMany(g => g).ToList();
            var perOperation = identifier.Max(g => g.Count());
            var meanMs = calls.Average(c => context.Ms(c.Duration));
            yield return Create(
                $"{identifier.Key} called up to {perOperation} times per operation",
                Scale(perOperation, 10),
                DiagnosticConfidence.Medium,
                $"{identifier.Key} was called {calls.Count} times across {identifier.Count()} operation(s), up to {perOperation} times in one operation, averaging {FormatMs(meanMs)} per call.",
                "Repeated calls to the same function in one operation usually iterate over items one at a time. Each call pays the interop crossing cost.",
                "Send all items in one call (an array argument) and loop in JavaScript, or move the loop entirely into JavaScript.",
                [Evidence("Calls", FormatNumber(calls.Count)), Evidence("Max per operation", FormatNumber(perOperation)), Evidence("Mean duration", FormatMs(meanMs))],
                new ImpactEstimate { TotalMs = Math.Round(calls.Sum(c => context.Ms(c.Duration)), 3), Occurrences = calls.Count, Method = "Sum of measured durations of the repeated calls.", Quality = ValueQuality.Measured },
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
        Description = "Detects interop identifiers whose calls exceed the slow-interop threshold.",
        RequiredSignals = [ProfilerEventKind.JSInterop],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Options.SlowInteropMs;
        foreach (var interop in context.Summary.Interop.OrderByDescending(i => i.Durations.P95Ms))
        {
            var slow = interop.Durations.Count >= 3 ? interop.Durations.P95Ms >= threshold : interop.Durations.MaxMs >= threshold * 3;
            if (!slow)
            {
                continue;
            }

            var related = context.OfType<JSInteropEvent>().Where(c => c.Identifier == interop.Identifier && c.Direction == interop.Direction).OrderByDescending(c => c.Duration).Take(20).ToList();
            var server = related.Any(c => c.CircuitId is not null);
            yield return Create(
                $"{interop.Identifier} p95 is {FormatMs(interop.Durations.P95Ms)}",
                Scale(interop.Durations.P95Ms, threshold),
                DiagnosticConfidence.Medium,
                $"{FormatNumber(interop.Durations.Count)} call(s) to {interop.Identifier}: p95 {FormatMs(interop.Durations.P95Ms)}, max {FormatMs(interop.Durations.MaxMs)}.",
                server
                    ? "On Blazor Server the measured time includes the network round trip to the browser and the JavaScript execution."
                    : "The measured time includes JavaScript execution and, for asynchronous calls, any awaited JavaScript promises (for example user prompts or network calls).",
                "Profile the JavaScript function in the browser developer tools; avoid awaiting long-running JavaScript work in UI paths, and cache results where possible.",
                [Evidence("Calls", FormatNumber(interop.Durations.Count)), Evidence("p95", FormatMs(interop.Durations.P95Ms)), Evidence("Maximum", FormatMs(interop.Durations.MaxMs))],
                new ImpactEstimate { TotalMs = interop.Durations.TotalMs, Occurrences = interop.Durations.Count, Method = "Total measured call time.", Quality = ValueQuality.Measured },
                subject: interop.Identifier,
                caveats: ["Calls that intentionally wait for user input (dialogs, permission prompts) are expected to be slow."],
                related: related);
        }
    }
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
