using System.Globalization;
using BlazorProfiler.Events;
using BlazorProfiler.Sessions;
using BlazorProfiler.Storage;

namespace BlazorProfiler.Analysis;

/// <summary>
/// Retained components (rule BP5004) grouped across sessions by leak signature, so the same leak seen in many circuits,
/// sessions, or traces is reported once with how often it occurred (as LeakCanary groups heap analyses by leak trace).
/// </summary>
public sealed class LeakReport
{
    /// <summary>The number of sessions (or traces) analyzed.</summary>
    public int SessionsAnalyzed { get; set; }

    /// <summary>The number of distinct retained instances across all groups.</summary>
    public int Instances { get; set; }

    /// <summary>The groups, the most widespread first (sessions, then instances).</summary>
    public List<LeakGroup> Groups { get; set; } = [];
}

/// <summary>Retained instances of one component type held by the same retainers.</summary>
public sealed class LeakGroup
{
    /// <summary>
    /// A stable identifier of the leak: a hash of the component type and its retainers. It is the same in every session,
    /// process, and trace, so it can be used to track a leak across runs.
    /// </summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>The retained component type.</summary>
    public string ComponentType { get; set; } = string.Empty;

    /// <summary>
    /// The likely retainers found for the instances (see <see cref="ComponentRetainedEvent.RetainedBy"/>), sorted. Empty when
    /// none was found, which does not prove there is none.
    /// </summary>
    public List<string> Retainers { get; set; } = [];

    /// <summary>Whether at least one retainer was identified with certainty (not only tentatively).</summary>
    public bool RetainerIdentified { get; set; }

    /// <summary>The number of distinct retained instances.</summary>
    public int Instances { get; set; }

    /// <summary>
    /// The number of sessions in which the leak was seen. A session whose instances were all counted already from an
    /// overlapping session (for example a circuit's session inside the process-wide one) does not count.
    /// </summary>
    public int Sessions { get; set; }

    /// <summary>The number of distinct circuits (Blazor Server) in which the leak was seen.</summary>
    public int Circuits { get; set; }

    /// <summary>Instances released by disposal.</summary>
    public int Disposed { get; set; }

    /// <summary>Instances released by the end of their circuit or request scope.</summary>
    public int ScopeEnded { get; set; }

    /// <summary>The most full garbage collections an instance survived after its release.</summary>
    public int MaxSurvivedCollections { get; set; }

    /// <summary>When the first instance was released (wall clock).</summary>
    public DateTimeOffset FirstReleasedAt { get; set; }

    /// <summary>When an instance was last reported as retained (wall clock).</summary>
    public DateTimeOffset LastReportedAt { get; set; }

    /// <summary>The sessions in which the leak was seen, the most recent first (at most 20).</summary>
    public List<string> SessionIds { get; set; } = [];
}

/// <summary>Groups retained components across sessions by leak signature.</summary>
public static class LeakGrouping
{
    private const int MaxSessionIds = 20;

    /// <summary>The kinds read from sessions.</summary>
    private static readonly HashSet<ProfilerEventKind> s_kinds = [ProfilerEventKind.ComponentRetained];

    /// <summary>
    /// Computes the signature of a leak: a 64-bit FNV-1a hash (16 hexadecimal digits) of the component type and the sorted,
    /// distinct retainers. Instance identifiers and times are not part of it.
    /// </summary>
    public static string Signature(string? componentType, IEnumerable<string>? retainers)
    {
        var text = string.Join('\n', new[] { componentType ?? string.Empty }.Concat(Normalize(retainers)));
        var hash = 14695981039346656037UL;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>Groups the retained components of live or imported sessions.</summary>
    /// <param name="sessions">The sessions to analyze.</param>
    /// <param name="maxGroups">The maximum number of groups returned.</param>
    public static LeakReport Group(IEnumerable<IProfilerSession> sessions, int maxGroups = 200)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var sources = new List<LeakSource>();
        foreach (var session in sessions)
        {
            var events = session.GetEvents(new EventQuery { Kinds = s_kinds }).OfType<ComponentRetainedEvent>().ToList();

            // Live sessions share the profiler's instance identifiers (a process-wide session and a circuit's session can
            // report the same instance); an imported trace has identifiers of its own.
            sources.Add(new LeakSource(session.Id, session.IsImported ? "trace:" + session.Id : "live", session.Clock, session.StartTimestamp, events));
        }

        return Group(sources, maxGroups);
    }

    /// <summary>Groups the retained components of several sources (sessions or traces).</summary>
    /// <param name="sources">The sources to analyze.</param>
    /// <param name="maxGroups">The maximum number of groups returned.</param>
    public static LeakReport Group(IEnumerable<LeakSource> sources, int maxGroups = 200)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var groups = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var instances = new HashSet<(string Domain, long Instance)>();
        var analyzed = 0;
        foreach (var source in sources.OrderBy(s => s.Clock.AnchorTime + TimeSpan.FromSeconds((double)(s.StartTimestamp - s.Clock.AnchorTimestamp) / s.Clock.Frequency)))
        {
            analyzed++;
            foreach (var e in source.Events)
            {
                var retainers = Normalize(e.RetainedBy);
                var signature = Signature(e.ComponentType, retainers);
                if (!groups.TryGetValue(signature, out var group))
                {
                    group = new Accumulator(signature, e.ComponentType ?? "(unknown)", retainers);
                    groups[signature] = group;
                }

                group.Add(source, e);
                instances.Add((source.Domain, e.ComponentInstanceId));
            }
        }

        return new LeakReport
        {
            SessionsAnalyzed = analyzed,
            Instances = instances.Count,
            Groups = [.. groups.Values
                .Select(g => g.ToGroup())
                .OrderByDescending(g => g.Sessions)
                .ThenByDescending(g => g.Instances)
                .ThenBy(g => g.ComponentType, StringComparer.Ordinal)
                .Take(Math.Max(1, maxGroups))],
        };
    }

    /// <summary>Formats a report as Markdown (used by the MCP tool and the command-line tool).</summary>
    /// <param name="report">The report.</param>
    /// <param name="max">The maximum number of groups listed.</param>
    public static string ToMarkdown(LeakReport report, int max = 20)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new System.Text.StringBuilder();
        if (report.Groups.Count == 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"No retained components in {report.SessionsAnalyzed} session(s). Leaks are reported after full garbage collections; run the check-leaks command for an immediate answer.");
            return sb.ToString();
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{report.Groups.Count} leak signature(s), {report.Instances} retained instance(s) in {report.SessionsAnalyzed} session(s), the most widespread first.");
        sb.AppendLine();
        sb.AppendLine("| signature | component | retained by | instances | sessions | circuits | released by | last seen |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---|---|");
        foreach (var g in report.Groups.Take(Math.Max(1, max)))
        {
            var retainers = g.Retainers.Count == 0 ? "not determined (use dotnet-gcdump)" : string.Join("; ", g.Retainers);
            var released = g.ScopeEnded == 0 ? "disposal" : g.Disposed == 0 ? "scope end" : $"disposal ({g.Disposed}), scope end ({g.ScopeEnded})";
            sb.Append("| ").Append(g.Signature).Append(" | ").Append(Cell(g.ComponentType)).Append(" | ").Append(Cell(retainers)).Append(" | ")
                .Append(g.Instances.ToString(CultureInfo.InvariantCulture)).Append(" | ").Append(g.Sessions.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(g.Circuits.ToString(CultureInfo.InvariantCulture)).Append(" | ").Append(released).Append(" | ")
                .Append(g.LastReportedAt.ToString("u", CultureInfo.InvariantCulture)).AppendLine(" |");
        }

        if (report.Groups.Count > max)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"\n{report.Groups.Count - max} more signature(s) not listed.");
        }

        return sb.ToString();

        static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ');
    }

    private static List<string> Normalize(IEnumerable<string>? retainers) =>
        retainers is null ? [] : [.. retainers.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static DateTimeOffset WallTime(IProfilerClock clock, long timestamp) =>
        clock.AnchorTime + TimeSpan.FromSeconds((double)(timestamp - clock.AnchorTimestamp) / clock.Frequency);

    private sealed class Accumulator(string signature, string componentType, List<string> retainers)
    {
        private readonly HashSet<(string Domain, long Instance)> _instances = [];
        private readonly HashSet<string> _sessions = new(StringComparer.Ordinal);
        private readonly HashSet<string> _circuits = new(StringComparer.Ordinal);
        private readonly List<string> _sessionOrder = [];
        private int _disposed;
        private int _scopeEnded;
        private int _survived;
        private DateTimeOffset? _first;
        private DateTimeOffset? _last;

        public void Add(LeakSource source, ComponentRetainedEvent e)
        {
            if (e.CircuitId is not null)
            {
                _circuits.Add(e.CircuitId);
            }

            _survived = Math.Max(_survived, e.SurvivedCollections);
            var released = WallTime(source.Clock, e.ReleasedTimestamp);
            var reported = WallTime(source.Clock, e.Timestamp);
            _first = _first is null || released < _first ? released : _first;
            _last = _last is null || reported > _last ? reported : _last;
            if (!_instances.Add((source.Domain, e.ComponentInstanceId)))
            {
                // The same instance reported by an overlapping session (for example process-wide and per circuit): neither
                // the instance nor the session counts again, so overlapping sessions do not make a leak look widespread.
                return;
            }

            if (_sessions.Add(source.Id))
            {
                _sessionOrder.Add(source.Id);
            }

            if (e.Reason == RetentionReason.ScopeEnded)
            {
                _scopeEnded++;
            }
            else
            {
                _disposed++;
            }
        }

        public LeakGroup ToGroup() => new()
        {
            Signature = signature,
            ComponentType = componentType,
            Retainers = retainers,
            RetainerIdentified = retainers.Any(r => !ComponentRetainedEvent.IsTentative(r)),
            Instances = _instances.Count,
            Sessions = _sessions.Count,
            Circuits = _circuits.Count,
            Disposed = _disposed,
            ScopeEnded = _scopeEnded,
            MaxSurvivedCollections = _survived,
            FirstReleasedAt = _first ?? default,
            LastReportedAt = _last ?? default,
            SessionIds = [.. Enumerable.Reverse(_sessionOrder).Take(MaxSessionIds)],
        };
    }
}

/// <summary>Retained-component events of one session or trace, for <see cref="LeakGrouping"/>.</summary>
/// <param name="Id">The session identifier (or trace name).</param>
/// <param name="Domain">
/// The instance-identifier domain: sources with the same domain share component instance identifiers (live sessions of
/// one profiler), so an instance they both report is counted once.
/// </param>
/// <param name="Clock">The clock of the source's timestamps.</param>
/// <param name="StartTimestamp">When the source started (orders sources from oldest to newest).</param>
/// <param name="Events">The retained-component events.</param>
public sealed record LeakSource(string Id, string Domain, IProfilerClock Clock, long StartTimestamp, IReadOnlyList<ComponentRetainedEvent> Events);
