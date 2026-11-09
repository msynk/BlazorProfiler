using BlazorProfiler.Events;
using static BlazorProfiler.Analysis.DiagnosticContext;

namespace BlazorProfiler.Analysis.Rules;

/// <summary>Shared helpers for database rules.</summary>
internal static class DatabaseRuleHelpers
{
    public static string Sql(DatabaseCommandEvent e, int max = 300) =>
        e.CommandText is null ? "(unknown)" : e.CommandText.Length <= max ? e.CommandText : e.CommandText[..max] + "…";

    public static string Origin(DatabaseCommandEvent e) =>
        e.OriginComponentType is null
            ? "outside a component"
            : $"{ShortType(e.OriginComponentType)}{(e.OriginLifecycleMethod is { } method ? "." + method : string.Empty)}";

    public static string Origins(IEnumerable<DatabaseCommandEvent> events) =>
        string.Join("; ", events.Select(Origin).Distinct().Take(3));

    public static bool IsInitialization(DatabaseCommandEvent e) =>
        e.OriginLifecycleMethod is LifecycleMethod.OnInitialized or LifecycleMethod.OnInitializedAsync or LifecycleMethod.SetParametersAsync or LifecycleMethod.OnParametersSet or LifecycleMethod.OnParametersSetAsync;
}

/// <summary>BP8001: the same query executed many times with different arguments in one operation (N+1).</summary>
public sealed class NPlusOneQueriesRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP8001",
        Title = "N+1 database queries",
        Category = DiagnosticCategory.Database,
        Description = "Detects the same query shape executed at least NPlusOneQueryThreshold times with different arguments within one operation, or by many instances of one component type, typically a query per item of a list.",
        RequiredSignals = [ProfilerEventKind.DatabaseCommand],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = Math.Max(2, context.Options.NPlusOneQueryThreshold);
        var handlers = HandlersByOperation(context);
        var commands = context.OfType<DatabaseCommandEvent>();
        var reported = new HashSet<long>();

        // Within one operation (strong evidence: the same interaction issued all of them).
        foreach (var group in commands.Where(c => c.OperationId != 0).GroupBy(c => (c.OperationId, c.CommandHash)).Where(g => g.Count() >= threshold).OrderByDescending(g => g.Count()))
        {
            var list = group.ToList();
            if (list.Select(c => c.ParameterHash).Distinct().Count() < 2 || !reported.Add(group.Key.CommandHash))
            {
                // Identical arguments are duplicates (BP8002), not N+1.
                continue;
            }

            handlers.TryGetValue(group.Key.OperationId, out var handler);
            yield return CreateFinding(context, list, handler is null ? $"operation #{group.Key.OperationId}" : HandlerName(handler), DiagnosticConfidence.High);
        }

        // Across instances of one component type outside any operation (for example the initial render of a list of rows).
        foreach (var group in commands.Where(c => c.OperationId == 0 && c.OriginComponentType is not null).GroupBy(c => (c.OriginComponentType, c.CommandHash)))
        {
            var list = SortedByTimestamp(group).Cast<DatabaseCommandEvent>().ToList();
            if (list.Select(c => c.OriginComponentInstanceId).Distinct().Count() < threshold || reported.Contains(group.Key.CommandHash))
            {
                continue;
            }

            var window = context.Ticks(2_000);
            var best = new List<DatabaseCommandEvent>();
            for (int start = 0, end = 0; end < list.Count; end++)
            {
                while (list[end].Timestamp - list[start].Timestamp > window)
                {
                    start++;
                }

                if (end - start + 1 > best.Count)
                {
                    best = list.GetRange(start, end - start + 1);
                }
            }

            if (best.Select(c => c.OriginComponentInstanceId).Distinct().Count() >= threshold && reported.Add(group.Key.CommandHash))
            {
                yield return CreateFinding(context, best, $"{FormatNumber(best.Select(c => c.OriginComponentInstanceId).Distinct().Count())} {ShortType(group.Key.OriginComponentType)} instances", DiagnosticConfidence.Medium);
            }
        }
    }

    private DiagnosticFinding CreateFinding(DiagnosticContext context, List<DatabaseCommandEvent> list, string scope, DiagnosticConfidence confidence)
    {
        var totalMs = list.Sum(c => context.Ms(c.Duration));
        var maxMs = list.Max(c => context.Ms(c.Duration));
        var perInstance = list.Select(c => c.OriginComponentInstanceId).Where(id => id != 0).Distinct().Count() >= list.Count / 2 && list.Any(DatabaseRuleHelpers.IsInitialization);
        return Create(
            $"N+1 queries: {FormatNumber(list.Count)} executions of the same query ({scope})",
            Scale(list.Count, context.Options.NPlusOneQueryThreshold),
            confidence,
            $"The same query ran {FormatNumber(list.Count)} times with different arguments ({scope}), taking {FormatMs(totalMs)} in total. Started from: {DatabaseRuleHelpers.Origins(list)}.",
            perInstance
                ? "Each component instance loads its own data while initializing, so rendering a list of N items issues N queries. Every query pays a database round trip, and on Blazor Server they also hold the circuit's interaction open."
                : "One query per item (often from a loop or lazy-loaded navigation properties) multiplies database round trips with the number of items.",
            perInstance
                ? "Load the data for all items in the parent with one query (for example `Where(x => ids.Contains(x.Id))` or `Include`) and pass each item's data to the child components as parameters."
                : "Fetch related data in one query: use `Include`/`ThenInclude` or a projection, or load all needed rows with `Where(x => ids.Contains(x.Id))` and match them in memory. Disable lazy loading where it causes per-item queries.",
            [
                Evidence("Executions", FormatNumber(list.Count)),
                Evidence("Distinct arguments", FormatNumber(list.Select(c => c.ParameterHash).Distinct().Count())),
                Evidence("Total time", FormatMs(totalMs)),
                Evidence("Started from", DatabaseRuleHelpers.Origins(list), list.Any(c => c.OriginComponentType is not null) ? ValueQuality.Measured : ValueQuality.Unavailable),
                Evidence("Query", DatabaseRuleHelpers.Sql(list[0])),
            ],
            new ImpactEstimate { TotalMs = Math.Round(Math.Max(0, totalMs - maxMs), 3), Occurrences = list.Count, Method = "Total time of the repeated executions minus the slowest one (optimistic: one batched query costs at least as much as one execution).", Quality = ValueQuality.Estimated },
            componentType: list.Select(c => c.OriginComponentType).FirstOrDefault(t => t is not null),
            subject: list[0].CommandHash.ToString(System.Globalization.CultureInfo.InvariantCulture),
            caveats: ["Commands that bypass Entity Framework Core are not observed.", "Small lookups served from the database cache can be cheap; the cost grows with network latency to the database."],
            related: list,
            codeHint: list.FirstOrDefault(c => c.OriginComponentType is not null) is { } origin ? DatabaseRuleHelpers.Origin(origin) : null);
    }
}

/// <summary>BP8002: identical queries (same text and arguments) repeated, including the prerender-then-interactive double fetch.</summary>
public sealed class DuplicateQueriesRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP8002",
        Title = "Duplicate database queries",
        Category = DiagnosticCategory.Database,
        Description = "Detects identical queries (same text and argument values) executed more than once within one operation or within DuplicateRequestWindowMs, and queries that run once during prerendering and again when the component becomes interactive.",
        RequiredSignals = [ProfilerEventKind.DatabaseCommand],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var commands = context.OfType<DatabaseCommandEvent>().Where(c => c.Outcome == ProfilerOutcome.Success).ToList();
        var reported = new HashSet<long>();

        // Prerendering (no circuit) followed by the interactive render (a circuit) of the same component type.
        var window = context.Ticks(30_000);
        foreach (var group in commands.Where(c => c.OriginComponentType is not null && DatabaseRuleHelpers.IsInitialization(c)).GroupBy(c => (c.ParameterHash, c.OriginComponentType)))
        {
            var list = SortedByTimestamp(group).Cast<DatabaseCommandEvent>().ToList();
            var pairs = new List<DatabaseCommandEvent>();
            foreach (var prerender in list.Where(c => c.CircuitId is null))
            {
                var interactive = list.FirstOrDefault(c => c.CircuitId is not null && c.Timestamp > prerender.Timestamp && c.Timestamp - prerender.Timestamp <= window);
                if (interactive is not null)
                {
                    pairs.Add(prerender);
                    pairs.Add(interactive);
                }
            }

            if (pairs.Count == 0 || !reported.Add(group.Key.ParameterHash))
            {
                continue;
            }

            var shortType = ShortType(group.Key.OriginComponentType);
            var wastedMs = pairs.Where(c => c.CircuitId is not null).Sum(c => context.Ms(c.Duration));
            yield return Create(
                $"{shortType} runs the same query during prerendering and again when it becomes interactive",
                Scale(pairs.Count / 2, 1),
                DiagnosticConfidence.High,
                $"{FormatNumber(pairs.Count / 2)} time(s), {shortType} ran an identical query while prerendering and again after the circuit started. The second execution cost {FormatMs(wastedMs)} and its result was already sent to the browser once.",
                "With prerendering, components are initialized twice: once to produce the static HTML and once when they become interactive. Data loaded in OnInitializedAsync is fetched both times unless it is persisted across the two renders.",
                "Persist the prerendered state: mark the property with `[PersistentState]` (.NET 10) or use `PersistentComponentState`, or turn prerendering off for this component. Alternatively cache the result for the user's session.",
                [
                    Evidence("Double fetches", FormatNumber(pairs.Count / 2)),
                    Evidence("Wasted time (interactive executions)", FormatMs(wastedMs)),
                    Evidence("Query", DatabaseRuleHelpers.Sql(pairs[0])),
                ],
                new ImpactEstimate { TotalMs = Math.Round(wastedMs, 3), Occurrences = pairs.Count / 2, Method = "Duration of the repeated (interactive) executions.", Quality = ValueQuality.Measured },
                componentType: group.Key.OriginComponentType,
                subject: "prerender:" + group.Key.ParameterHash.ToString(System.Globalization.CultureInfo.InvariantCulture),
                caveats: ["The data may legitimately differ between the two renders (for example per-user data that changed in between)."],
                related: pairs,
                codeHint: $"{shortType}.OnInitializedAsync");
        }

        // Identical queries within one operation, or close together without an operation.
        var duplicateWindow = context.Ticks(context.Options.DuplicateRequestWindowMs);
        foreach (var group in commands.GroupBy(c => c.ParameterHash))
        {
            if (reported.Contains(group.Key))
            {
                continue;
            }

            var list = SortedByTimestamp(group).Cast<DatabaseCommandEvent>().ToList();
            var duplicates = new List<DatabaseCommandEvent>();
            for (var i = 1; i < list.Count; i++)
            {
                var previous = list[i - 1];
                var current = list[i];
                var sameOperation = current.OperationId != 0 && current.OperationId == previous.OperationId;
                var close = current.OperationId == 0 && previous.OperationId == 0 && current.Timestamp - previous.Timestamp <= duplicateWindow && current.CircuitId == previous.CircuitId;
                if (sameOperation || close)
                {
                    duplicates.Add(current);
                }
            }

            if (duplicates.Count == 0 || IsWrite(list[0]))
            {
                continue;
            }

            var wastedMs = duplicates.Sum(c => context.Ms(c.Duration));
            yield return Create(
                $"Identical query executed {FormatNumber(duplicates.Count + 1)} times",
                Scale(duplicates.Count, 1),
                duplicates.Any(c => c.OperationId != 0) ? DiagnosticConfidence.High : DiagnosticConfidence.Medium,
                $"A query with identical text and arguments ran {FormatNumber(duplicates.Count)} more time(s) {(duplicates.Any(c => c.OperationId != 0) ? "within the same operation" : $"within {FormatMs(context.Options.DuplicateRequestWindowMs)}")}, costing {FormatMs(wastedMs)}. Started from: {DatabaseRuleHelpers.Origins(duplicates)}.",
                "Repeating a query whose result cannot have changed adds database round trips. In Blazor this often happens when several components load the same data independently, or when a lifecycle method runs more often than expected.",
                "Load the data once and share it: pass it down as a parameter or cascading value, cache it in a scoped service, or move the query out of methods that run on every render or parameter update (OnParametersSetAsync, OnAfterRenderAsync).",
                [
                    Evidence("Executions", FormatNumber(duplicates.Count + 1)),
                    Evidence("Wasted time", FormatMs(wastedMs)),
                    Evidence("Started from", DatabaseRuleHelpers.Origins(list), list.Any(c => c.OriginComponentType is not null) ? ValueQuality.Measured : ValueQuality.Unavailable),
                    Evidence("Query", DatabaseRuleHelpers.Sql(list[0])),
                ],
                new ImpactEstimate { TotalMs = Math.Round(wastedMs, 3), Occurrences = duplicates.Count, Method = "Duration of the repeated executions.", Quality = ValueQuality.Measured },
                componentType: list.Select(c => c.OriginComponentType).FirstOrDefault(t => t is not null),
                subject: group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                caveats: ["Arguments are compared by hash; values are never stored.", "Queries reading data that other users change concurrently may need to be repeated."],
                related: list);
        }
    }

    private static bool IsWrite(DatabaseCommandEvent e) =>
        e.ExecuteMethod == "NonQuery" || (e.CommandText is { } text && (text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) || text.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) || text.StartsWith("SET ", StringComparison.OrdinalIgnoreCase)));
}

/// <summary>BP8003: slow database queries.</summary>
public sealed class SlowQueryRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP8003",
        Title = "Slow database query",
        Category = DiagnosticCategory.Database,
        Description = "Reports query shapes whose p95 duration (including reading the rows) is at least SlowQueryMs over three or more executions, or a single execution at three times the threshold.",
        RequiredSignals = [ProfilerEventKind.DatabaseCommand],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Options.SlowQueryMs;
        foreach (var group in context.OfType<DatabaseCommandEvent>().GroupBy(c => c.CommandHash))
        {
            var list = group.ToList();
            var p95 = context.Ms(PercentileTicks(list, 95));
            var max = context.Ms(list.Max(c => c.Duration));
            if (!(list.Count >= 3 && p95 >= threshold) && !(max >= threshold * 3))
            {
                continue;
            }

            var rows = list.Where(c => c.RowsRead is not null).Select(c => c.RowsRead!.Value).DefaultIfEmpty().Max();
            yield return Create(
                $"Slow query: p95 {FormatMs(p95)} over {FormatNumber(list.Count)} execution(s)",
                Scale(list.Count >= 3 ? p95 : max / 3, threshold),
                DiagnosticConfidence.High,
                $"A query took {FormatMs(p95)} at the 95th percentile (max {FormatMs(max)}) over {FormatNumber(list.Count)} execution(s){(rows > 0 ? $", reading up to {FormatNumber(rows)} rows" : string.Empty)}. Started from: {DatabaseRuleHelpers.Origins(list)}.",
                "Interactions that wait for this query cannot render their result sooner. On Blazor Server a slow query in a lifecycle method or handler also keeps the user's circuit busy.",
                rows > 1000
                    ? "The query reads many rows: filter and page on the server (`Where`, `Skip`/`Take`), project only the columns you need (`Select`), and use `<Virtualize>` with an items provider for long lists."
                    : "Inspect the execution plan, add an index for the filtered or sorted columns, project only the needed columns, and avoid client-side evaluation. Consider caching results that change rarely.",
                [
                    Evidence("Executions", FormatNumber(list.Count)),
                    Evidence("p95", FormatMs(p95)),
                    Evidence("Max", FormatMs(max)),
                    Evidence("Rows read (max)", rows > 0 ? FormatNumber(rows) : "unknown", rows > 0 ? ValueQuality.Measured : ValueQuality.Unavailable),
                    Evidence("Query", DatabaseRuleHelpers.Sql(list[0])),
                ],
                new ImpactEstimate { TotalMs = Math.Round(list.Sum(c => Math.Max(0, context.Ms(c.Duration) - threshold)), 3), Occurrences = list.Count, Method = "Time above the threshold summed over executions.", Quality = ValueQuality.Estimated },
                componentType: list.Select(c => c.OriginComponentType).FirstOrDefault(t => t is not null),
                subject: group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                caveats: ["Durations include connection and network time to the database and, for readers, the time to read the rows."],
                related: list.OrderByDescending(c => c.Duration));
        }
    }
}

/// <summary>BP8004: synchronous database calls on the renderer's dispatcher.</summary>
public sealed class SynchronousQueryOnRendererRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP8004",
        Title = "Synchronous database call blocks the renderer",
        Category = DiagnosticCategory.Database,
        Description = "Detects database commands executed synchronously (ToList, First, SaveChanges instead of their Async versions) on a renderer's dispatcher, which blocks the circuit for their whole duration.",
        RequiredSignals = [ProfilerEventKind.DatabaseCommand],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        foreach (var group in context.OfType<DatabaseCommandEvent>().Where(c => !c.IsAsync && c.OnRendererThread == true).GroupBy(c => c.OriginComponentType ?? "(unknown)"))
        {
            var list = group.ToList();
            var totalMs = list.Sum(c => context.Ms(c.Duration));
            var maxMs = list.Max(c => context.Ms(c.Duration));
            var shortType = ShortType(group.Key);
            yield return Create(
                $"{FormatNumber(list.Count)} synchronous database call(s) block the renderer ({shortType})",
                Scale(maxMs, Math.Max(1, context.Options.SlowLifecycleMs / 5)),
                DiagnosticConfidence.High,
                $"{FormatNumber(list.Count)} command(s) ran synchronously on the renderer's dispatcher (total {FormatMs(totalMs)}, max {FormatMs(maxMs)}). Started from: {DatabaseRuleHelpers.Origins(list)}.",
                "While a synchronous call waits for the database, the dispatcher can do nothing else: the user's circuit stops processing events and rendering, and a thread-pool thread is blocked (which limits server scalability).",
                "Use the asynchronous APIs (`ToListAsync`, `FirstOrDefaultAsync`, `SaveChangesAsync`, `FindAsync`) and await them, typically in OnInitializedAsync or an async event handler.",
                [
                    Evidence("Synchronous calls", FormatNumber(list.Count)),
                    Evidence("Total blocking time", FormatMs(totalMs)),
                    Evidence("Longest", FormatMs(maxMs)),
                    Evidence("Started from", DatabaseRuleHelpers.Origins(list), list.Any(c => c.OriginComponentType is not null) ? ValueQuality.Measured : ValueQuality.Unavailable),
                    Evidence("Query", DatabaseRuleHelpers.Sql(list[0])),
                ],
                new ImpactEstimate { TotalMs = Math.Round(totalMs, 3), Occurrences = list.Count, Method = "Sum of the synchronous command durations (time the dispatcher was blocked).", Quality = ValueQuality.Measured },
                componentType: group.Key == "(unknown)" ? null : group.Key,
                subject: group.Key,
                related: list.OrderByDescending(c => c.Duration),
                codeHint: list.FirstOrDefault(c => c.OriginComponentType is not null) is { } origin ? DatabaseRuleHelpers.Origin(origin) : null);
        }
    }
}

/// <summary>BP8005: one DbContext instance used across many operations over a long time (a circuit-lifetime DbContext).</summary>
public sealed class LongLivedDbContextRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP8005",
        Title = "DbContext shared across interactions",
        Category = DiagnosticCategory.Database,
        Description = "Detects a DbContext instance used by three or more operations over at least 30 seconds within a circuit: a scoped DbContext lives as long as the circuit on Blazor Server.",
        RequiredSignals = [ProfilerEventKind.DatabaseCommand],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var minimumSpan = context.Ticks(30_000);
        var groups = context.OfType<DatabaseCommandEvent>()
            .Where(c => c.ContextInstanceId != 0 && c.CircuitId is not null)
            .GroupBy(c => c.ContextType ?? "(unknown)");
        foreach (var byType in groups)
        {
            var instances = byType.GroupBy(c => c.ContextInstanceId)
                .Select(g => g.ToList())
                .Where(list => list.Select(c => c.OperationId).Where(id => id != 0).Distinct().Count() >= 3 && list.Max(c => c.Timestamp) - list.Min(c => c.Timestamp) >= minimumSpan)
                .ToList();
            if (instances.Count == 0)
            {
                continue;
            }

            var operations = instances.Max(list => list.Select(c => c.OperationId).Distinct().Count());
            var spanSeconds = instances.Max(list => context.Ms(list.Max(c => c.Timestamp) - list.Min(c => c.Timestamp))) / 1000;
            var shortType = ShortType(byType.Key);
            yield return Create(
                $"{shortType} instances live across many interactions",
                Cap(Scale(operations, 10), DiagnosticSeverity.Medium),
                DiagnosticConfidence.Medium,
                $"{FormatNumber(instances.Count)} {shortType} instance(s) were used by up to {FormatNumber(operations)} operations over {spanSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} s in the same circuit.",
                "On Blazor Server a DbContext registered as scoped lives as long as the circuit. Its change tracker grows with every query (slowing later queries and holding memory), it returns stale cached entities, and concurrent use from overlapping async work throws \"A second operation was started on this context\".",
                "Inject `IDbContextFactory<TContext>` and create a short-lived context per operation (`await using var db = await factory.CreateDbContextAsync();`), or use `OwningComponentBase` to scope it to a component. Use `AsNoTracking()` for read-only queries.",
                [
                    Evidence("Long-lived instances", FormatNumber(instances.Count)),
                    Evidence("Operations per instance (max)", FormatNumber(operations)),
                    Evidence("Lifetime observed (max)", $"{spanSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} s"),
                ],
                null,
                subject: byType.Key,
                caveats: ["A deliberately long-lived, no-tracking context is valid when it is never used concurrently."],
                related: instances.SelectMany(list => list).Take(20));
        }
    }
}
