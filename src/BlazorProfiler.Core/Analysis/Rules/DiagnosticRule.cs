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

    /// <summary>Sorted start timestamps.</summary>
    protected static List<long> SortedTimestamps(IEnumerable<ProfilerEvent> events)
    {
        var list = events.Select(e => e.Timestamp).ToList();
        list.Sort();
        return list;
    }

    /// <summary>Counts the seconds in which at least <paramref name="threshold"/> events occurred.</summary>
    protected static int SecondsAtOrAbove(IReadOnlyList<long> sortedTimestamps, long frequency, double threshold)
    {
        if (sortedTimestamps.Count == 0)
        {
            return 0;
        }

        var counts = new Dictionary<long, int>();
        foreach (var ts in sortedTimestamps)
        {
            var second = ts / frequency;
            counts[second] = counts.TryGetValue(second, out var c) ? c + 1 : 1;
        }

        return counts.Values.Count(c => c >= threshold);
    }

    /// <summary>The weighted count of events.</summary>
    protected static double Weighted(IEnumerable<ProfilerEvent> events) => events.Sum(e => e.SampleWeight);

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
