using System.Diagnostics;
using System.Globalization;
using BlazorProfiler.Events;
using BlazorProfiler.Sessions;
using BlazorProfiler.Storage;

namespace BlazorProfiler.Analysis;

/// <summary>A deterministic diagnostic rule.</summary>
/// <remarks>
/// Rules must be pure functions of the <see cref="DiagnosticContext"/>: identical input yields identical findings.
/// They must report speculative explanations with an appropriate <see cref="DiagnosticConfidence"/> and never present
/// temporal coincidence as causation.
/// </remarks>
public interface IDiagnosticRule
{
    /// <summary>The rule description.</summary>
    DiagnosticRuleDescriptor Descriptor { get; }

    /// <summary>Evaluates the rule.</summary>
    IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context);
}

/// <summary>Augments findings with recommendations or documentation, for example organization-specific guidance.</summary>
public interface IDiagnosticRecommendationProvider
{
    /// <summary>
    /// Augments a finding. Implementations may set <see cref="DiagnosticFinding.DocumentationUrl"/> to an absolute
    /// <c>http</c>/<c>https</c> URL or a same-origin path (<c>/wiki/perf/bp1001</c>); other links are replaced with the
    /// built-in rule reference.
    /// </summary>
    void Augment(DiagnosticFinding finding);
}

/// <summary>Links findings to the built-in rule reference.</summary>
public sealed class DocumentationRecommendationProvider : IDiagnosticRecommendationProvider
{
    /// <summary>The base URL of the rule reference.</summary>
    public const string BaseUrl = "https://github.com/msynk/BlazorProfiler/blob/main/docs/rules.md#";

    /// <inheritdoc />
    public void Augment(DiagnosticFinding finding) =>
        finding.DocumentationUrl ??= BaseUrl + finding.RuleId.ToLowerInvariant();
}

/// <summary>Input to diagnostic rules: a session snapshot plus derived indexes.</summary>
public sealed class DiagnosticContext
{
    private readonly Dictionary<Type, object> _byType = [];
    private Dictionary<long, List<ProfilerEvent>>? _byOperation;

    /// <summary>Creates a context.</summary>
    public DiagnosticContext(SessionSnapshot snapshot, DiagnosticOptions options)
    {
        Snapshot = snapshot;
        Options = options;
        Clock = snapshot.Session.Clock;
    }

    /// <summary>The snapshot.</summary>
    public SessionSnapshot Snapshot { get; }

    /// <summary>Retained events.</summary>
    public ProfilerEvent[] Events => Snapshot.Events;

    /// <summary>Aggregates (covering evicted events as well).</summary>
    public SessionSummary Summary => Snapshot.Summary;

    /// <summary>The options.</summary>
    public DiagnosticOptions Options { get; }

    /// <summary>The clock.</summary>
    public IProfilerClock Clock { get; }

    /// <summary>Returns retained events of a type, cached.</summary>
    public IReadOnlyList<T> OfType<T>()
        where T : ProfilerEvent
    {
        if (!_byType.TryGetValue(typeof(T), out var cached))
        {
            var list = new List<T>();
            foreach (var e in Events)
            {
                if (e is T typed)
                {
                    list.Add(typed);
                }
            }

            cached = list;
            _byType[typeof(T)] = cached;
        }

        return (List<T>)cached;
    }

    /// <summary>Returns retained events grouped by operation.</summary>
    public IReadOnlyDictionary<long, List<ProfilerEvent>> ByOperation
    {
        get
        {
            if (_byOperation is null)
            {
                _byOperation = [];
                foreach (var e in Events)
                {
                    if (e.OperationId == 0)
                    {
                        continue;
                    }

                    if (!_byOperation.TryGetValue(e.OperationId, out var list))
                    {
                        list = [];
                        _byOperation[e.OperationId] = list;
                    }

                    list.Add(e);
                }
            }

            return _byOperation;
        }
    }

    /// <summary>Converts ticks to milliseconds.</summary>
    public double Ms(long ticks) => Clock.ToMilliseconds(ticks);

    /// <summary>Converts milliseconds to ticks.</summary>
    public long Ticks(double milliseconds) => Clock.FromMilliseconds(milliseconds);

    /// <summary>Formats milliseconds for display.</summary>
    public static string FormatMs(double milliseconds) =>
        milliseconds >= 1000
            ? (milliseconds / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " s"
            : milliseconds.ToString(milliseconds < 10 ? "0.##" : "0", CultureInfo.InvariantCulture) + " ms";

    /// <summary>Formats a byte count for display.</summary>
    public static string FormatBytes(double bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => (bytes / (1024 * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
        >= 1024 * 1024 => (bytes / (1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString("0", CultureInfo.InvariantCulture) + " B",
    };

    /// <summary>Formats a number for display.</summary>
    public static string FormatNumber(double value) =>
        value.ToString(value == Math.Floor(value) ? "N0" : "N1", CultureInfo.InvariantCulture);

    /// <summary>Formats a ratio as a percentage.</summary>
    public static string FormatPercent(double ratio) => (ratio * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    /// <summary>Shortens a full type name to its simple name for titles.</summary>
    public static string ShortType(string? type)
    {
        if (string.IsNullOrEmpty(type))
        {
            return "(unknown)";
        }

        var generic = type.IndexOf('`', StringComparison.Ordinal);
        var name = generic > 0 ? type[..generic] : type;
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    /// <summary>
    /// Computes the maximum weighted number of events (the sum of <see cref="ProfilerEvent.SampleWeight"/>) in any half-open
    /// sliding window of <paramref name="windowTicks"/>, given events sorted by <see cref="ProfilerEvent.Timestamp"/>.
    /// Sampled events count for the occurrences they represent, so the peak rate is not under-estimated under sampling.
    /// </summary>
    public static double PeakWeight(IReadOnlyList<ProfilerEvent> sortedEvents, long windowTicks)
    {
        ArgumentNullException.ThrowIfNull(sortedEvents);
        double peak = 0;
        double sum = 0;
        var start = 0;
        for (var end = 0; end < sortedEvents.Count; end++)
        {
            sum += WeightOf(sortedEvents[end]);
            while (sortedEvents[end].Timestamp - sortedEvents[start].Timestamp >= windowTicks)
            {
                sum -= WeightOf(sortedEvents[start]);
                start++;
            }

            peak = Math.Max(peak, sum);
        }

        // Rounding removes floating-point residue from adding and subtracting fractional weights.
        return Math.Round(peak, 6);
    }

    /// <summary>The number of occurrences an event represents: its sample weight, or one when no weight is recorded.</summary>
    public static double WeightOf(ProfilerEvent profilerEvent)
    {
        ArgumentNullException.ThrowIfNull(profilerEvent);
        return profilerEvent.Weight;
    }
}

/// <summary>Runs diagnostic rules over sessions.</summary>
public sealed class DiagnosticEngine
{
    private readonly DiagnosticOptions _options;
    private readonly IDiagnosticRule[] _rules;
    private static readonly DocumentationRecommendationProvider s_defaultDocumentation = new();

    // Custom providers; the built-in rule reference is applied after them, to findings left without a valid link.
    private readonly IDiagnosticRecommendationProvider[] _recommendationProviders;

    /// <summary>Creates an engine.</summary>
    public DiagnosticEngine(DiagnosticOptions options, IEnumerable<IDiagnosticRule> rules, IEnumerable<IDiagnosticRecommendationProvider>? recommendationProviders = null)
    {
        _options = options;
        _rules = rules.ToArray();
        var duplicate = _rules.GroupBy(r => r.Descriptor.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate diagnostic rule identifier '{duplicate.Key}'.", nameof(rules));
        }

        _recommendationProviders = [.. recommendationProviders ?? []];
    }

    /// <summary>The suppressions applied to findings.</summary>
    public SuppressionList Suppressions { get; } = new();

    /// <summary>The registered rules.</summary>
    public IReadOnlyList<IDiagnosticRule> Rules => _rules;

    /// <summary>Analyzes a session.</summary>
    public DiagnosticReport Analyze(IProfilerSession session) => Analyze(session.CreateSnapshot());

    /// <summary>Analyzes a snapshot.</summary>
    public DiagnosticReport Analyze(SessionSnapshot snapshot)
    {
        var start = Stopwatch.GetTimestamp();
        var context = new DiagnosticContext(snapshot, _options);
        var findings = new List<DiagnosticFinding>();
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var evaluated = new List<DiagnosticRuleDescriptor>();

        foreach (var rule in _rules)
        {
            var descriptor = rule.Descriptor;
            if (!_options.IsRuleEnabled(descriptor.Id))
            {
                continue;
            }

            evaluated.Add(descriptor);
            try
            {
                var count = 0;
                foreach (var finding in rule.Evaluate(context))
                {
                    if (++count > Math.Max(1, _options.MaxFindingsPerRule))
                    {
                        break;
                    }

                    if (_options.Rules.TryGetValue(descriptor.Id, out var settings) && settings.Severity is { } severity)
                    {
                        finding.Severity = severity;
                    }

                    foreach (var provider in _recommendationProviders)
                    {
                        provider.Augment(finding);
                    }

                    // Dashboards render the URL as a link, so only web URLs and same-origin paths are kept (no javascript:,
                    // data:, and so on). A rejected link falls back to the built-in rule reference, like a missing one.
                    if (!IsSafeDocumentationUrl(finding.DocumentationUrl))
                    {
                        finding.DocumentationUrl = null;
                    }

                    s_defaultDocumentation.Augment(finding);
                    finding.Suppression = Suppressions.Match(finding);
                    findings.Add(finding);
                }
            }
            catch (Exception ex)
            {
                errors[descriptor.Id] = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        findings.Sort(static (a, b) =>
        {
            var bySeverity = b.Severity.CompareTo(a.Severity);
            if (bySeverity != 0)
            {
                return bySeverity;
            }

            var byConfidence = b.Confidence.CompareTo(a.Confidence);
            if (byConfidence != 0)
            {
                return byConfidence;
            }

            return (b.Impact?.TotalMs ?? 0).CompareTo(a.Impact?.TotalMs ?? 0);
        });

        return new DiagnosticReport
        {
            Findings = findings,
            Rules = evaluated,
            RuleErrors = errors,
            AnalyzedEvents = snapshot.Events.Length,
            AnalysisMs = Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 3),
        };
    }

    /// <summary>
    /// Returns whether a documentation link is safe to render: an absolute <c>http</c>/<c>https</c> URL, or a path on the
    /// dashboard's own origin (<c>/wiki/perf</c>; not protocol-relative). Whitespace, control characters, and backslashes,
    /// which browsers normalize in surprising ways, are never accepted.
    /// </summary>
    internal static bool IsSafeDocumentationUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\'))
        {
            return false;
        }

        if (url[0] == '/')
        {
            return url.Length == 1 || url[1] != '/';
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }
}
