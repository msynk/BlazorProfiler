using BlazorProfiler.Events;

namespace BlazorProfiler.Analysis.Rules;

/// <summary>Base class for diagnostic rules with helpers for building consistent findings.</summary>
public abstract class DiagnosticRule : IDiagnosticRule
{
    /// <inheritdoc />
    public abstract DiagnosticRuleDescriptor Descriptor { get; }

    /// <inheritdoc />
    public abstract IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context);

    /// <summary>
    /// Maps how far a value exceeds its threshold to a severity:
    /// below 1x Info, 1-2x Low, 2-5x Medium, 5-20x High, 20x and more Critical.
    /// </summary>
    protected static DiagnosticSeverity Scale(double value, double threshold)
    {
        if (threshold <= 0)
        {
            return DiagnosticSeverity.Medium;
        }

        var ratio = value / threshold;
        return ratio switch
        {
            < 1 => DiagnosticSeverity.Info,
            < 2 => DiagnosticSeverity.Low,
            < 5 => DiagnosticSeverity.Medium,
            < 20 => DiagnosticSeverity.High,
            _ => DiagnosticSeverity.Critical,
        };
    }

    /// <summary>Caps a severity.</summary>
    protected static DiagnosticSeverity Cap(DiagnosticSeverity severity, DiagnosticSeverity max) => severity > max ? max : severity;

    /// <summary>Creates an evidence entry.</summary>
    protected static FindingEvidence Evidence(string label, string value, ValueQuality quality = ValueQuality.Measured) =>
        new() { Label = label, Value = value, Quality = quality };

    /// <summary>Returns up to <paramref name="max"/> event identifiers.</summary>
    protected static long[] Ids(IEnumerable<ProfilerEvent> events, int max = 20) => events.Take(max).Select(e => e.Id).ToArray();

    /// <summary>Returns up to <paramref name="max"/> distinct non-zero operation identifiers.</summary>
    protected static long[] Operations(IEnumerable<ProfilerEvent> events, int max = 20) =>
        events.Where(e => e.OperationId != 0).Select(e => e.OperationId).Distinct().Take(max).ToArray();

    /// <summary>
    /// Events sorted by start timestamp, as required by <see cref="DiagnosticContext.PeakWeight"/> (the peak rate in a
    /// sliding window).
    /// </summary>
    protected static List<ProfilerEvent> SortedByTimestamp(IEnumerable<ProfilerEvent> events)
    {
        var list = events.ToList();
        list.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return list;
    }

    /// <summary>
    /// Counts the seconds in which the weighted number of events (see <see cref="DiagnosticContext.WeightOf"/>) reached
    /// <paramref name="threshold"/>.
    /// </summary>
    protected static int SecondsAtOrAbove(IReadOnlyList<ProfilerEvent> events, long frequency, double threshold)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return 0;
        }

        var weights = new Dictionary<long, double>();
        foreach (var e in events)
        {
            var second = e.Timestamp / frequency;
            weights[second] = weights.GetValueOrDefault(second) + DiagnosticContext.WeightOf(e);
        }

        // The tolerance absorbs floating-point residue from fractional weights.
        return weights.Values.Count(w => w >= threshold - 1e-9);
    }

    /// <summary>The weighted count of events: the number of occurrences they represent when sampled.</summary>
    protected static double Weighted(IEnumerable<ProfilerEvent> events) => events.Sum(DiagnosticContext.WeightOf);

    /// <summary>
    /// The weighted total duration of events in milliseconds: each duration counts for the occurrences its event represents
    /// when sampled, consistent with <see cref="Weighted"/>.
    /// </summary>
    protected static double WeightedMs(DiagnosticContext context, IEnumerable<ProfilerEvent> events, Func<ProfilerEvent, long>? duration = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        duration ??= static e => e.Duration;
        return events.Sum(e => context.Ms(duration(e)) * DiagnosticContext.WeightOf(e));
    }

    /// <summary>Estimates a percentile of event durations in ticks.</summary>
    protected static long PercentileTicks(IEnumerable<ProfilerEvent> events, double percentile)
    {
        var durations = events.Select(e => e.Duration).ToList();
        if (durations.Count == 0)
        {
            return 0;
        }

        durations.Sort();
        var rank = (int)Math.Ceiling(percentile / 100.0 * durations.Count) - 1;
        return durations[Math.Clamp(rank, 0, durations.Count - 1)];
    }

    /// <summary>
    /// Maps each operation to the first event handler recorded for it. Operation identifiers are normally unique per
    /// handler, but imported or merged traces can repeat them, so duplicates are tolerated instead of failing the rule.
    /// </summary>
    protected static Dictionary<long, EventHandlerEvent> HandlersByOperation(DiagnosticContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var handlers = new Dictionary<long, EventHandlerEvent>();
        foreach (var handler in context.OfType<EventHandlerEvent>())
        {
            if (handler.OperationId != 0)
            {
                handlers.TryAdd(handler.OperationId, handler);
            }
        }

        return handlers;
    }

    /// <summary>Formats a handler display name.</summary>
    protected static string HandlerName(EventHandlerEvent handler) =>
        $"{handler.AttributeName ?? "event"} → {DiagnosticContext.ShortType(handler.ComponentType)}.{handler.MethodName ?? "?"}";

    /// <summary>Creates a finding pre-populated from the descriptor.</summary>
    protected DiagnosticFinding Create(
        string title,
        DiagnosticSeverity severity,
        DiagnosticConfidence confidence,
        string summary,
        string explanation,
        string recommendation,
        IReadOnlyList<FindingEvidence> evidence,
        ImpactEstimate? impact = null,
        string? componentType = null,
        long? instanceId = null,
        string? subject = null,
        IReadOnlyList<string>? caveats = null,
        IEnumerable<ProfilerEvent>? related = null,
        string? codeHint = null)
    {
        var relatedList = related?.ToList();
        return new DiagnosticFinding
        {
            RuleId = Descriptor.Id,
            RuleVersion = Descriptor.Version,
            Category = Descriptor.Category,
            Title = title,
            Severity = severity,
            Confidence = confidence,
            Summary = summary,
            Explanation = explanation,
            Recommendation = recommendation,
            Evidence = evidence,
            Impact = impact,
            ComponentType = componentType,
            ComponentInstanceId = instanceId,
            Subject = subject,
            Caveats = caveats ?? [],
            RelatedEventIds = relatedList is null ? [] : Ids(relatedList),
            RelatedOperationIds = relatedList is null ? [] : Operations(relatedList),
            WindowStart = relatedList is { Count: > 0 } ? relatedList.Min(e => e.Timestamp) : null,
            WindowEnd = relatedList is { Count: > 0 } ? relatedList.Max(e => e.EndTimestamp) : null,
            CodeHint = codeHint,
        };
    }
}

/// <summary>Factory for the built-in rule set.</summary>
public static class BuiltInRules
{
    /// <summary>Creates new instances of every built-in rule.</summary>
    public static IReadOnlyList<IDiagnosticRule> Create() =>
    [
        new HighFrequencyRenderingRule(),
        new LargeRenderBatchRule(),
        new ParameterReferenceChurnRule(),
        new UnchangedOutputRenderRule(),
        new SlowLifecycleRule(),
        new ExcessiveComponentInstancesRule(),
        new SlowRenderBatchRule(),
        new SlowEventHandlerRule(),
        new RapidEventStreamRule(),
        new DuplicateRequestsInHandlerRule(),
        new ExcessiveRenderingAfterEventRule(),
        new DuplicateRequestsRule(),
        new NPlusOneRequestsRule(),
        new SequentialRequestsRule(),
        new SlowEndpointRule(),
        new RepeatedFailuresRule(),
        new RequestsDuringInitializationRule(),
        new ExcessiveInteropRule(),
        new BatchableInteropRule(),
        new LargeInteropPayloadRule(),
        new SlowInteropRule(),
        new InteropAfterDisposeRule(),
        new HighGcPressureRule(),
        new AllocationSpikeRule(),
        new MemoryGrowthAcrossNavigationRule(),
        new SlowServerRenderRoundTripRule(),
        new CircuitInstabilityRule(),
        new SlowCircuitActivityRule(),
        new HighRenderBatchVolumeRule(),
        new ServerSaturationRule(),
        new SlowBrowserInteractionRule(),
        new LongTaskRule(),
    ];
}
