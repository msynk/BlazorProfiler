using BlazorProfiler.Events;

namespace BlazorProfiler.Analysis;

/// <summary>Severity of a finding.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Informational.</summary>
    Info = 0,
    /// <summary>Low impact.</summary>
    Low = 1,
    /// <summary>Medium impact.</summary>
    Medium = 2,
    /// <summary>High impact.</summary>
    High = 3,
    /// <summary>Critical impact.</summary>
    Critical = 4,
}

/// <summary>How strongly the evidence supports the finding's explanation.</summary>
public enum DiagnosticConfidence
{
    /// <summary>Plausible explanation based on limited or temporal evidence.</summary>
    Low = 0,
    /// <summary>Supported by direct measurements, but alternative explanations remain.</summary>
    Medium = 1,
    /// <summary>Supported by direct, specific measurements.</summary>
    High = 2,
}

/// <summary>Rule categories.</summary>
public enum DiagnosticCategory
{
    /// <summary>Rendering efficiency.</summary>
    Rendering = 0,
    /// <summary>Event handling.</summary>
    EventHandling = 1,
    /// <summary>HTTP and network.</summary>
    Network = 2,
    /// <summary>JavaScript interop.</summary>
    Interop = 3,
    /// <summary>Memory and garbage collection.</summary>
    Memory = 4,
    /// <summary>Blazor Server and circuits.</summary>
    Server = 5,
    /// <summary>Browser responsiveness.</summary>
    Browser = 6,
}

/// <summary>Static description of a rule.</summary>
public sealed class DiagnosticRuleDescriptor
{
    /// <summary>The unique rule identifier (for example <c>BP1001</c>).</summary>
    public required string Id { get; init; }

    /// <summary>The rule version; incremented whenever the rule's logic changes.</summary>
    public int Version { get; init; } = 1;

    /// <summary>A short title.</summary>
    public required string Title { get; init; }

    /// <summary>The category.</summary>
    public DiagnosticCategory Category { get; init; }

    /// <summary>What the rule detects and which signals it needs.</summary>
    public required string Description { get; init; }

    /// <summary>The signals the rule depends on (used to explain why a rule produced no result).</summary>
    public IReadOnlyList<ProfilerEventKind> RequiredSignals { get; init; } = [];
}

/// <summary>A piece of evidence supporting a finding.</summary>
public sealed class FindingEvidence
{
    /// <summary>A label.</summary>
    public required string Label { get; init; }

    /// <summary>The formatted value.</summary>
    public required string Value { get; init; }

    /// <summary>Whether the value was measured, estimated, or inferred.</summary>
    public ValueQuality Quality { get; init; } = ValueQuality.Measured;
}

/// <summary>The estimated impact of a finding.</summary>
public sealed class ImpactEstimate
{
    /// <summary>The estimated aggregate time attributable to the issue, in milliseconds, when quantifiable.</summary>
    public double? TotalMs { get; init; }

    /// <summary>The number of occurrences.</summary>
    public double Occurrences { get; init; }

    /// <summary>How the estimate was calculated.</summary>
    public required string Method { get; init; }

    /// <summary>Whether the estimate is measured or estimated.</summary>
    public ValueQuality Quality { get; init; } = ValueQuality.Estimated;
}

/// <summary>A diagnostic finding produced by a rule.</summary>
public sealed class DiagnosticFinding
{
    /// <summary>The rule identifier.</summary>
    public required string RuleId { get; init; }

    /// <summary>The rule version that produced the finding.</summary>
    public int RuleVersion { get; init; }

    /// <summary>The rule category.</summary>
    public DiagnosticCategory Category { get; init; }

    /// <summary>A short title.</summary>
    public required string Title { get; init; }

    /// <summary>The severity.</summary>
    public DiagnosticSeverity Severity { get; set; }

    /// <summary>The confidence.</summary>
    public DiagnosticConfidence Confidence { get; init; }

    /// <summary>What was observed.</summary>
    public required string Summary { get; init; }

    /// <summary>Why it matters and the most likely explanation.</summary>
    public required string Explanation { get; init; }

    /// <summary>The suggested remediation.</summary>
    public required string Recommendation { get; init; }

    /// <summary>Conditions under which the recommendation may not apply.</summary>
    public IReadOnlyList<string> Caveats { get; init; } = [];

    /// <summary>Supporting evidence.</summary>
    public IReadOnlyList<FindingEvidence> Evidence { get; init; } = [];

    /// <summary>The estimated impact.</summary>
    public ImpactEstimate? Impact { get; init; }

    /// <summary>The affected component type, when applicable.</summary>
    public string? ComponentType { get; init; }

    /// <summary>The affected component instance, when applicable.</summary>
    public long? ComponentInstanceId { get; init; }

    /// <summary>The affected subject (endpoint, interop identifier, handler, circuit), when applicable.</summary>
    public string? Subject { get; init; }

    /// <summary>The start of the measurement window (monotonic timestamp).</summary>
    public long? WindowStart { get; init; }

    /// <summary>The end of the measurement window (monotonic timestamp).</summary>
    public long? WindowEnd { get; init; }

    /// <summary>Identifiers of representative related events (bounded).</summary>
    public IReadOnlyList<long> RelatedEventIds { get; init; } = [];

    /// <summary>Identifiers of related operations (bounded).</summary>
    public IReadOnlyList<long> RelatedOperationIds { get; init; } = [];

    /// <summary>A documentation link.</summary>
    public string? DocumentationUrl { get; set; }

    /// <summary>A hint pointing at the likely code location.</summary>
    public string? CodeHint { get; init; }

    /// <summary>A stable key used to suppress the finding across sessions (rule plus subject).</summary>
    public string Fingerprint => string.Concat(RuleId, ":", Subject ?? ComponentType ?? string.Empty);

    /// <summary>The suppression applied to this finding, if any.</summary>
    public FindingSuppression? Suppression { get; set; }
}

/// <summary>The kind of suppression.</summary>
public enum SuppressionKind
{
    /// <summary>The finding was dismissed.</summary>
    Dismissed = 0,
    /// <summary>The behavior is expected (for example an animation that renders frequently by design).</summary>
    Expected = 1,
}

/// <summary>A suppression entry.</summary>
public sealed class FindingSuppression
{
    /// <summary>The fingerprint of suppressed findings (<c>RuleId:Subject</c>), or <c>RuleId:*</c> to suppress a whole rule.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The kind of suppression.</summary>
    public SuppressionKind Kind { get; init; }

    /// <summary>An optional note.</summary>
    public string? Note { get; init; }

    /// <summary>When the suppression was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A thread-safe list of suppressions.</summary>
public sealed class SuppressionList
{
    private readonly Dictionary<string, FindingSuppression> _entries = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>Adds or replaces a suppression.</summary>
    public void Add(FindingSuppression suppression)
    {
        ArgumentNullException.ThrowIfNull(suppression);
        if (suppression.Fingerprint.Length > 1024)
        {
            throw new ArgumentException("The fingerprint is too long.", nameof(suppression));
        }

        lock (_lock)
        {
            if (_entries.Count >= 10_000 && !_entries.ContainsKey(suppression.Fingerprint))
            {
                throw new InvalidOperationException("Too many suppressions.");
            }

            _entries[suppression.Fingerprint] = suppression;
        }
    }

    /// <summary>Removes a suppression.</summary>
    public bool Remove(string fingerprint)
    {
        lock (_lock)
        {
            return _entries.Remove(fingerprint);
        }
    }

    /// <summary>Returns all suppressions.</summary>
    public IReadOnlyList<FindingSuppression> GetAll()
    {
        lock (_lock)
        {
            return [.. _entries.Values];
        }
    }

    /// <summary>Returns the suppression matching a finding, if any.</summary>
    public FindingSuppression? Match(DiagnosticFinding finding)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(finding.Fingerprint, out var exact))
            {
                return exact;
            }

            return _entries.TryGetValue(finding.RuleId + ":*", out var rule) ? rule : null;
        }
    }
}

/// <summary>The result of running the diagnostic engine on a session.</summary>
public sealed class DiagnosticReport
{
    /// <summary>Findings ordered by severity and impact.</summary>
    public IReadOnlyList<DiagnosticFinding> Findings { get; init; } = [];

    /// <summary>Rules that were evaluated.</summary>
    public IReadOnlyList<DiagnosticRuleDescriptor> Rules { get; init; } = [];

    /// <summary>Rules that failed, with the error message.</summary>
    public IReadOnlyDictionary<string, string> RuleErrors { get; init; } = new Dictionary<string, string>();

    /// <summary>The number of events analyzed.</summary>
    public int AnalyzedEvents { get; init; }

    /// <summary>The time taken by the analysis in milliseconds.</summary>
    public double AnalysisMs { get; init; }
}
