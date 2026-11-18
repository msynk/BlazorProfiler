using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BlazorProfiler.Analysis;
using BlazorProfiler.Events;
using BlazorProfiler.Export;
using BlazorProfiler.Sessions;
using BlazorProfiler.Storage;

namespace BlazorProfiler.Api;

/// <summary>Options of the <see cref="ProfilerMcpServer"/>.</summary>
public sealed class ProfilerMcpOptions
{
    /// <summary>
    /// Whether tools that change state are offered: starting, pausing, stopping, clearing, and bookmarking sessions, and
    /// running profiler commands (for example the leak check, which forces garbage collections). Defaults to false, so a
    /// coding agent can read profiling data but not change anything.
    /// </summary>
    public bool AllowControl { get; set; }

    /// <summary>The maximum length of a tool result in characters; longer results are cut. Defaults to 60,000.</summary>
    public int MaxResultLength { get; set; } = 60_000;
}

/// <summary>
/// A Model Context Protocol (MCP) server over <see cref="ProfilerApi"/>: coding agents list sessions, read an overview,
/// list findings and ask for the full explanation of one, inspect the slowest interactions and a component's render
/// causes, and (when <see cref="ProfilerMcpOptions.AllowControl"/> is set) control sessions and run commands. It handles
/// JSON-RPC messages and is transport independent: the server package maps it to a Streamable HTTP endpoint, and the CLI
/// serves exported traces over standard input and output.
/// </summary>
/// <remarks>
/// Replies are aggregated and compact first (lists with identifiers), with tools to drill into one item, so that an agent
/// reads little until it needs detail. Every call goes through <see cref="ProfilerApi"/> with the caller's
/// <see cref="ProfilerApiAccess"/>, so the access rules of the dashboard apply unchanged.
/// </remarks>
public sealed class ProfilerMcpServer
{
    /// <summary>The newest protocol version implemented.</summary>
    public const string LatestProtocolVersion = "2025-11-25";

    /// <summary>The protocol versions this server accepts.</summary>
    public static IReadOnlyList<string> SupportedProtocolVersions { get; } = [LatestProtocolVersion, "2025-06-18", "2025-03-26", "2024-11-05"];

    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;

    private readonly ProfilerApi _api;
    private readonly ProfilerMcpOptions _options;
    private readonly List<Tool> _tools;

    /// <summary>Creates the server.</summary>
    public ProfilerMcpServer(ProfilerApi api, ProfilerMcpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
        _options = options ?? new ProfilerMcpOptions();
        _tools = CreateTools();
    }

    /// <summary>The names of the tools offered to callers with control access.</summary>
    public IEnumerable<string> ToolNames => _tools.Select(t => t.Name);

    /// <summary>
    /// Handles one JSON-RPC message. Returns the serialized response, or null for notifications and responses (which
    /// receive no reply).
    /// </summary>
    public async Task<string?> HandleAsync(string message, ProfilerApiAccess access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message ?? string.Empty, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return Error(default, ParseError, "Parse error.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error(default, InvalidRequest, "Batches are not supported; send one JSON-RPC object.");
            }

            var hasId = root.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number;
            if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            {
                // A response from the client (we send no requests) or an invalid message.
                return hasId && (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _)) ? null : Error(hasId ? id : default, InvalidRequest, "Invalid request.");
            }

            var method = methodElement.GetString()!;
            if (!hasId)
            {
                // Notifications (initialized, cancelled, ...) need no reply.
                return null;
            }

            root.TryGetProperty("params", out var parameters);
            try
            {
                return method switch
                {
                    "initialize" => Result(id, w => WriteInitialize(w, parameters)),
                    "ping" => Result(id, w => { w.WriteStartObject(); w.WriteEndObject(); }),
                    "tools/list" => Result(id, w => WriteToolList(w, access)),
                    "tools/call" => await CallToolAsync(id, parameters, access, cancellationToken).ConfigureAwait(false),
                    "resources/list" => Result(id, w => { w.WriteStartObject(); w.WriteStartArray("resources"); w.WriteEndArray(); w.WriteEndObject(); }),
                    "prompts/list" => Result(id, w => { w.WriteStartObject(); w.WriteStartArray("prompts"); w.WriteEndArray(); w.WriteEndObject(); }),
                    _ => Error(id, MethodNotFound, $"Method '{Truncate(method, 64)}' is not supported."),
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return Error(id, InternalError, "The request failed.");
            }
        }
    }

    private void WriteInitialize(Utf8JsonWriter w, JsonElement parameters)
    {
        var requested = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var version = requested is not null && SupportedProtocolVersions.Contains(requested) ? requested : LatestProtocolVersion;
        w.WriteStartObject();
        w.WriteString("protocolVersion", version);
        w.WriteStartObject("capabilities");
        w.WriteStartObject("tools");
        w.WriteBoolean("listChanged", false);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteStartObject("serverInfo");
        w.WriteString("name", "blazor-profiler");
        w.WriteString("title", "Blazor Profiler");
        w.WriteString("version", TraceDocumentBuilder.ProfilerVersion);
        w.WriteEndObject();
        w.WriteString(
            "instructions",
            "Blazor Profiler measures a running Blazor app (renders, event handlers, HTTP, database, JS interop, memory, circuits, browser responsiveness) and explains problems with deterministic rules. " +
            "Start with get_overview, then list_findings and explain_finding for detail. list_interactions and get_interaction show what one click or navigation did; get_component shows why a component rendered. " +
            "Every value is labelled measured, estimated, or inferred; do not present inferred correlations as proof. " +
            (_options.AllowControl ? "control_session and run_command change the profiled app's profiling state; use them only when asked." : "This server is read-only."));
        w.WriteEndObject();
    }

    private void WriteToolList(Utf8JsonWriter w, ProfilerApiAccess access)
    {
        w.WriteStartObject();
        w.WriteStartArray("tools");
        foreach (var tool in _tools.Where(t => IsAvailable(t, access)))
        {
            w.WriteStartObject();
            w.WriteString("name", tool.Name);
            w.WriteString("title", tool.Title);
            w.WriteString("description", tool.Description);
            w.WriteStartObject("inputSchema");
            w.WriteString("type", "object");
            w.WriteStartObject("properties");
            foreach (var p in tool.Parameters)
            {
                w.WriteStartObject(p.Name);
                w.WriteString("type", p.Type);
                w.WriteString("description", p.Description);
                if (p.Values is { } values)
                {
                    w.WriteStartArray("enum");
                    foreach (var value in values)
                    {
                        w.WriteStringValue(value);
                    }

                    w.WriteEndArray();
                }

                w.WriteEndObject();
            }

            w.WriteEndObject();
            w.WriteStartArray("required");
            foreach (var p in tool.Parameters.Where(p => p.Required))
            {
                w.WriteStringValue(p.Name);
            }

            w.WriteEndArray();
            w.WriteBoolean("additionalProperties", false);
            w.WriteEndObject();
            w.WriteStartObject("annotations");
            w.WriteBoolean("readOnlyHint", !tool.Control);
            w.WriteBoolean("destructiveHint", tool.Destructive);
            w.WriteBoolean("idempotentHint", !tool.Control);
            w.WriteBoolean("openWorldHint", false);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private bool IsAvailable(Tool tool, ProfilerApiAccess access) => !tool.Control || (_options.AllowControl && access.CanControl);

    private async Task<string> CallToolAsync(JsonElement id, JsonElement parameters, ProfilerApiAccess access, CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            return Error(id, InvalidParams, "A tool name is required.");
        }

        var name = nameElement.GetString();
        var tool = _tools.FirstOrDefault(t => t.Name == name);
        if (tool is null || !IsAvailable(tool, access))
        {
            return Error(id, InvalidParams, $"Unknown tool '{Truncate(name, 64)}'.");
        }

        var arguments = parameters.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? new Arguments(a) : new Arguments(default);
        string text;
        var isError = false;
        try
        {
            text = await tool.Run(arguments, access, cancellationToken).ConfigureAwait(false);
        }
        catch (ProfilerApiException ex)
        {
            text = ex.Message;
            isError = true;
        }
        catch (ArgumentException ex)
        {
            text = ex.Message;
            isError = true;
        }

        if (text.Length > _options.MaxResultLength)
        {
            text = text[.._options.MaxResultLength] + "\n\n[Result truncated. Narrow the request (limit, session, or a specific item).]";
        }

        return Result(id, w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("content");
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", text);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteBoolean("isError", isError);
            w.WriteEndObject();
        });
    }

    private List<Tool> CreateTools()
    {
        var session = new Parameter("session", "string", "Session id from list_sessions. Defaults to the most recent recording session (or the most recent session).");
        var limit = new Parameter("limit", "integer", "Maximum number of rows (default 20, at most 200).");
        return
        [
            new Tool("list_sessions", "List sessions", "Lists profiling sessions (id, name, state, duration, event count). Use the id as `session` in other tools.", [], (_, access, _) => Task.FromResult(ListSessions(access))),
            new Tool("get_overview", "Session overview", "Key metrics of a session (render batches, renders, handlers, HTTP, database, interop, browser responsiveness, memory) and the number of findings by severity and rule. Start here.", [session], (args, access, _) => Task.FromResult(Overview(access, args))),
            new Tool(
                "list_findings",
                "List findings",
                "Lists diagnostic findings (performance problems detected by deterministic rules), most severe first, with an id to pass to explain_finding.",
                [session, new Parameter("minSeverity", "string", "Lowest severity to include.", ["Info", "Low", "Medium", "High", "Critical"]), new Parameter("category", "string", "Only this category.", Enum.GetNames<DiagnosticCategory>()), limit],
                (args, access, _) => Task.FromResult(ListFindings(access, args))),
            new Tool("explain_finding", "Explain a finding", "The full explanation of one finding: summary, why it matters, suggested fix, where to look, evidence (each value labelled measured/estimated/inferred), impact, and when the advice may not apply.", [session, new Parameter("id", "string", "The finding id from list_findings (rule and subject, for example BP1001:MyApp.Counter).", Required: true)], (args, access, _) => Task.FromResult(ExplainFinding(access, args))),
            new Tool("list_interactions", "List interactions", "Lists user interactions, navigations, and custom operations with their duration and what they triggered (renders, HTTP, database, interop), slowest first.", [session, limit], (args, access, _) => Task.FromResult(ListInteractions(access, args))),
            new Tool("get_interaction", "Interaction details", "Everything one interaction did, in order: handler, renders and why, render batches, HTTP, database commands, interop calls, browser timing, with offsets from its start.", [session, new Parameter("operationId", "integer", "The operation id from list_interactions.", Required: true), limit], (args, access, _) => Task.FromResult(GetInteraction(access, args))),
            new Tool("list_components", "List components", "Lists component types by render cost: renders, render time, parameter updates, renders with unchanged output, instances, and retained (leaked) instances.", [session, limit], (args, access, _) => Task.FromResult(ListComponents(access, args))),
            new Tool("get_component", "Component details", "Why one component type rendered (render causes, changed and reference-only parameters), its lifecycle timings, handlers, and instances.", [session, new Parameter("component", "string", "The component type (full or short name).", Required: true)], (args, access, _) => Task.FromResult(GetComponent(access, args))),
            new Tool("list_leaks", "List leaks across sessions", "Components still referenced after they were disposed or their circuit ended (BP5004), across every visible session, grouped by leak signature (component type and likely retainers), the most widespread first. The same signature in many sessions or circuits is one leak.", [limit], (args, access, _) => Task.FromResult(LeakGrouping.ToMarkdown(_api.GetLeaks(access), args.Limit(20)))),
            new Tool("get_report", "Markdown report", "The complete Markdown report of a session: metrics, every finding with evidence and fix, and the most expensive components, handlers, endpoints, queries, and interop calls.", [session], async (args, access, ct) => await GetReportAsync(access, args, ct).ConfigureAwait(false)),
            new Tool(
                "control_session",
                "Control a session",
                "Starts a new process-wide session, or pauses, resumes, stops, clears, or bookmarks one.",
                [session, new Parameter("action", "string", "What to do.", ["start", "pause", "resume", "stop", "clear", "bookmark"], Required: true), new Parameter("name", "string", "Name of a new session or a bookmark.")],
                (args, access, _) => Task.FromResult(ControlSession(access, args)),
                Control: true,
                Destructive: true),
            new Tool(
                "run_command",
                "Run a profiler command",
                "Runs a command in the profiled app, for example check-leaks (forces garbage collections and reports retained components) or simulate-latency (Blazor Server). list_sessions shows the available commands.",
                [new Parameter("name", "string", "The command name.", Required: true), new Parameter("argument", "string", "The command argument, if it takes one.")],
                async (args, access, ct) => await RunCommandAsync(access, args, ct).ConfigureAwait(false),
                Control: true),
        ];
    }

    private string ListSessions(ProfilerApiAccess access)
    {
        var info = _api.GetInfo(access);
        var sb = new StringBuilder();
        if (info.Sessions.Count == 0)
        {
            sb.AppendLine("No sessions. A session starts when the app records (see the profiler's AutoStartSession option or the dashboard).");
        }
        else
        {
            sb.AppendLine("| id | name | state | circuit | duration | events |").AppendLine("|---|---|---|---|---:|---:|");
            foreach (var s in info.Sessions.OrderByDescending(s => s.CreatedAt))
            {
                sb.Append("| ").Append(s.Id).Append(" | ").Append(Cell(s.Name)).Append(" | ").Append(s.State).Append(" | ").Append(s.CircuitId ?? "all").Append(" | ")
                    .Append(Ms(s.DurationMs)).Append(" | ").Append(Num(s.StoredEvents)).AppendLine(" |");
            }
        }

        if (info.Commands.Count > 0 && _options.AllowControl)
        {
            sb.AppendLine().AppendLine("Commands (run_command):");
            foreach (var c in info.Commands)
            {
                sb.Append("- `").Append(c.Name).Append("`: ").Append(c.Description).Append(c.ArgumentHint is { } hint ? $" Argument: {hint}." : string.Empty).Append(c.State is { } state ? $" Now: {state}." : string.Empty).AppendLine();
            }
        }

        return sb.ToString();
    }

    private IProfilerSession ResolveSession(ProfilerApiAccess access, Arguments args)
    {
        var id = args.String("session");
        if (!string.IsNullOrEmpty(id))
        {
            return _api.GetSession(access, id);
        }

        var sessions = _api.GetInfo(access).Sessions;
        var chosen = sessions.Where(s => s.State == ProfilerSessionState.Recording).OrderByDescending(s => s.CreatedAt).FirstOrDefault()
            ?? sessions.OrderByDescending(s => s.CreatedAt).FirstOrDefault()
            ?? throw new ProfilerApiException("No sessions are available.");
        return _api.GetSession(access, chosen.Id);
    }

    private string Overview(ProfilerApiAccess access, Arguments args)
    {
        var session = ResolveSession(access, args);
        var summary = session.GetSummary();
        var report = _api.GetFindings(access, session.Id);
        var stats = session.GetStatistics();
        var sb = new StringBuilder();
        sb.Append("# ").Append(session.Name).Append(" (").Append(session.Id).Append(", ").Append(stats.State).Append(", ").Append(Ms(stats.DurationMs)).AppendLine(")").AppendLine();
        if (summary.ContainsSampledData)
        {
            sb.AppendLine("Some event kinds were sampled; counts are weighted estimates.").AppendLine();
        }

        sb.AppendLine("## Metrics");
        Line(sb, "Render batches", $"{Num(summary.RenderBatches.Durations.Count)} (p95 {Ms(summary.RenderBatches.Durations.P95Ms)}, max {Ms(summary.RenderBatches.Durations.MaxMs)}, largest {Num(summary.RenderBatches.MaxUpdatedComponents)} components)");
        Line(sb, "Component renders (instrumented)", Num(summary.Components.Sum(c => c.Renders.Count)));
        Line(sb, "Event handlers", $"{Num(summary.Handlers.Sum(h => h.Durations.Count))} (slowest p95 {Ms(summary.Handlers.Select(h => h.Durations.P95Ms).DefaultIfEmpty().Max())})");
        Line(sb, "HTTP requests", $"{Num(summary.Endpoints.Sum(e => e.Durations.Count))} to {summary.Endpoints.Count} endpoint(s), {Num(summary.Endpoints.Sum(e => e.Failures))} failed");
        Line(sb, "Database commands", $"{Num(summary.Queries.Sum(q => q.Durations.Count))} ({summary.Queries.Count} distinct, {Ms(summary.Queries.Sum(q => q.Durations.TotalMs))} total)");
        Line(sb, "JS interop calls", Num(summary.Interop.Sum(i => i.Durations.Count)));
        if (summary.BrowserInteractions.Count > 0)
        {
            Line(sb, "Browser interactions", $"{Num(summary.BrowserInteractions.Count)} (p95 {Ms(summary.BrowserInteractions.P95Ms)}, max {Ms(summary.BrowserInteractions.MaxMs)})");
        }

        if (summary.BrowserLongTasks.Count > 0)
        {
            Line(sb, "Long animation frames", $"{Num(summary.BrowserLongTasks.Count)} (max {Ms(summary.BrowserLongTasks.MaxMs)})");
        }

        foreach (var (name, vital) in summary.Vitals.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            var text = name switch
            {
                "CLS" => vital.Value.ToString("0.###", CultureInfo.InvariantCulture),
                "DOM" => Num(vital.Value) + " elements (largest)",
                _ => Ms(vital.Value),
            };
            Line(sb, name, $"{text} ({vital.Rating})");
        }

        if (summary.Startup is { } startup)
        {
            Line(sb, "Startup", $"interactive at {Ms(startup.InteractiveMs)}, framework {DiagnosticContext.FormatBytes(startup.FrameworkTransferBytes)} transferred in {startup.FrameworkFiles} file(s)");
            var device = new List<string>();
            if (startup.HardwareConcurrency is { } cores)
            {
                device.Add($"{cores} cores");
            }

            if (startup.DeviceMemoryGb is { } memory)
            {
                device.Add($"~{memory.ToString(CultureInfo.InvariantCulture)} GB memory");
            }

            if (startup.CpuPerformance is { } tier and > 0)
            {
                device.Add($"CPU tier {tier} of 4");
            }

            if (startup.EffectiveConnectionType is { } connection)
            {
                device.Add($"{connection} connection");
            }

            if (device.Count > 0)
            {
                Line(sb, "Browser device", string.Join(", ", device));
            }
        }

        if (summary.Runtime.Samples > 0)
        {
            Line(sb, "Allocated", $"{DiagnosticContext.FormatBytes(summary.Runtime.AllocatedBytes)}, GCs {summary.Runtime.Gen0Collections}/{summary.Runtime.Gen1Collections}/{summary.Runtime.Gen2Collections} (gen0/1/2)");
        }

        var retained = summary.Components.Sum(c => c.Retained);
        if (retained > 0)
        {
            Line(sb, "Retained component instances", Num(retained));
        }

        var active = report.Findings.Where(f => f.Suppression is null).ToList();
        sb.AppendLine().Append("## Findings: ").Append(active.Count).AppendLine();
        foreach (var group in active.GroupBy(f => f.Severity).OrderByDescending(g => g.Key))
        {
            sb.Append("- ").Append(group.Key).Append(": ").Append(group.Count()).Append(" (").Append(string.Join(", ", group.GroupBy(f => f.RuleId).Select(r => r.Count() > 1 ? $"{r.Key}×{r.Count()}" : r.Key))).AppendLine(")");
        }

        if (active.Count > 0)
        {
            sb.AppendLine().AppendLine("Use list_findings for the list and explain_finding for detail.");
        }

        return sb.ToString();
    }

    private string ListFindings(ProfilerApiAccess access, Arguments args)
    {
        var session = ResolveSession(access, args);
        var report = _api.GetFindings(access, session.Id);
        var minSeverity = args.Enum<DiagnosticSeverity>("minSeverity") ?? DiagnosticSeverity.Info;
        var category = args.Enum<DiagnosticCategory>("category");
        var max = args.Limit(20);
        var list = report.Findings
            .Where(f => f.Suppression is null && f.Severity >= minSeverity && (category is null || f.Category == category))
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.Impact?.TotalMs ?? 0)
            .ToList();
        if (list.Count == 0)
        {
            return "No findings match. Rules only report when the evidence is sufficient.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("| id | severity | confidence | title | impact |").AppendLine("|---|---|---|---|---|");
        foreach (var f in list.Take(max))
        {
            sb.Append("| ").Append(Cell(f.Fingerprint)).Append(" | ").Append(f.Severity).Append(" | ").Append(f.Confidence).Append(" | ").Append(Cell(f.Title)).Append(" | ")
                .Append(f.Impact is { } impact ? Cell($"{(impact.TotalMs is { } ms ? Ms(ms) + ", " : string.Empty)}{Num(impact.Occurrences)}×") : string.Empty).AppendLine(" |");
        }

        if (list.Count > max)
        {
            sb.Append('\n').Append(list.Count - max).AppendLine(" more; raise limit or filter by minSeverity or category.");
        }

        return sb.ToString();
    }

    private string ExplainFinding(ProfilerApiAccess access, Arguments args)
    {
        var id = args.String("id") ?? throw new ArgumentException("The finding id is required.");
        var session = ResolveSession(access, args);
        var report = _api.GetFindings(access, session.Id);
        var finding = report.Findings.FirstOrDefault(f => string.Equals(f.Fingerprint, id, StringComparison.Ordinal))
            ?? report.Findings.FirstOrDefault(f => f.Fingerprint.Contains(id, StringComparison.OrdinalIgnoreCase) || string.Equals(f.RuleId, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No finding '{Truncate(id, 128)}' in session {session.Id}. Use list_findings for ids.");
        var sb = new StringBuilder();
        MarkdownReportExporter.AppendFinding(sb, finding, "#");
        if (finding.RelatedOperationIds.Count > 0)
        {
            sb.Append("Related interactions (get_interaction): ").AppendLine(string.Join(", ", finding.RelatedOperationIds.Take(10)));
        }

        return sb.ToString();
    }

    private string ListInteractions(ProfilerApiAccess access, Arguments args)
    {
        var session = ResolveSession(access, args);
        var snapshot = session.CreateSnapshot();
        var clock = session.Clock;
        var operations = OperationSummary.Build(snapshot.Events, clock);
        if (operations.Count == 0)
        {
            return "No interactions were recorded in this session.";
        }

        var max = args.Limit(20);
        var sb = new StringBuilder();
        sb.AppendLine("| operationId | kind | name | duration | renders | batches | HTTP | DB | interop | errors |").AppendLine("|---:|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var op in operations.OrderByDescending(o => o.DurationMs).Take(max))
        {
            sb.Append("| ").Append(op.Id).Append(" | ").Append(op.Kind).Append(" | ").Append(Cell(op.Name)).Append(" | ").Append(Ms(op.DurationMs)).Append(" | ")
                .Append(op.Renders).Append(" | ").Append(op.Batches).Append(" | ").Append(op.Http).Append(" | ").Append(op.Database).Append(" | ").Append(op.Interop).Append(" | ").Append(op.Errors).AppendLine(" |");
        }

        if (operations.Count > max)
        {
            sb.Append('\n').Append(operations.Count - max).AppendLine(" more (raise limit).");
        }

        return sb.ToString();
    }

    private string GetInteraction(ProfilerApiAccess access, Arguments args)
    {
        var session = ResolveSession(access, args);
        var operationId = args.Long("operationId") ?? throw new ArgumentException("operationId is required.");
        var events = _api.GetOperation(access, session.Id, operationId);
        if (events.Count == 0)
        {
            return $"Operation {operationId} has no retained events (it may have been evicted or belongs to another session).";
        }

        var clock = session.Clock;
        events.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
        var start = events[0].Timestamp;
        var end = events.Max(e => e.EndTimestamp);
        var max = args.Limit(100);
        var sb = new StringBuilder();
        sb.Append("Operation ").Append(operationId).Append(": ").Append(events.Count).Append(" event(s) over ").Append(Ms(clock.ToMilliseconds(end - start))).AppendLine(". Offsets are from the first event.").AppendLine();
        sb.AppendLine("| at | duration | event | correlation |").AppendLine("|---:|---:|---|---|");
        foreach (var e in events.Take(max))
        {
            sb.Append("| +").Append(Ms(clock.ToMilliseconds(e.Timestamp - start))).Append(" | ").Append(e.Duration > 0 ? Ms(clock.ToMilliseconds(e.Duration)) : "—").Append(" | ")
                .Append(Cell(Describe(e))).Append(" | ").Append(e.Correlation).AppendLine(" |");
        }

        if (events.Count > max)
        {
            sb.Append('\n').Append(events.Count - max).AppendLine(" more (raise limit).");
        }

        return sb.ToString();
    }

    private string ListComponents(ProfilerApiAccess access, Arguments args)
    {
        var session = ResolveSession(access, args);
        var summary = session.GetSummary();
        var max = args.Limit(20);
        var rows = summary.Components
            .OrderByDescending(c => c.Renders.TotalMs > 0 ? c.Renders.TotalMs : c.ParametersSets.TotalMs)
            .ThenByDescending(c => c.Created)
            .Take(max)
            .ToList();
        if (rows.Count == 0)
        {
            return "No component activity was recorded.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("| component | instrumented | renders | render time | render p95 | alloc/render | unchanged output | parameter updates | created | retained |").AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var c in rows)
        {
            sb.Append("| ").Append(Cell(c.ComponentType)).Append(" | ").Append(c.Instrumented ? "yes" : "no").Append(" | ")
                .Append(Num(c.Renders.Count)).Append(" | ").Append(Ms(c.Renders.TotalMs)).Append(" | ").Append(Ms(c.Renders.P95Ms)).Append(" | ")
                .Append(c.MeanRenderAllocatedBytes is { } allocated ? DiagnosticContext.FormatBytes(allocated) : "n/a").Append(" | ")
                .Append(c.RendersWithOutputComparison > 0 ? DiagnosticContext.FormatPercent(c.RendersWithUnchangedOutput / c.RendersWithOutputComparison) : "n/a").Append(" | ")
                .Append(Num(c.ParametersSets.Count)).Append(" | ").Append(Num(c.Created)).Append(" | ").Append(Num(c.Retained)).AppendLine(" |");
        }

        return sb.ToString();
    }

    private string GetComponent(ProfilerApiAccess access, Arguments args)
    {
        var name = args.String("component") ?? throw new ArgumentException("component is required.");
        var session = ResolveSession(access, args);
        var summary = session.GetSummary();
        var matches = summary.Components.Where(c => string.Equals(c.ComponentType, name, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
        {
            matches = summary.Components.Where(c => string.Equals(DiagnosticContext.ShortType(c.ComponentType), name, StringComparison.OrdinalIgnoreCase)
                || c.ComponentType.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (matches.Count == 0)
        {
            throw new ArgumentException($"No component '{Truncate(name, 128)}' in session {session.Id}. Use list_components.");
        }

        if (matches.Count > 1)
        {
            return "Several components match; use a full name:\n" + string.Join("\n", matches.Select(m => "- " + m.ComponentType));
        }

        var c = matches[0];
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(c.ComponentType).AppendLine();
        Line(sb, "Instrumented (ProfiledComponentBase)", c.Instrumented ? "yes" : "no — per-instance render timing and render causes need ProfiledComponentBase");
        Line(sb, "Instances", $"{Num(c.Created)} created, {Num(c.Disposed)} disposed, {Num(c.Retained)} retained after disposal");
        if (c.Instrumented)
        {
            Line(sb, "Renders", $"{Num(c.Renders.Count)}, total {Ms(c.Renders.TotalMs)}, mean {Ms(c.Renders.MeanMs)}, p50 {Ms(c.Renders.P50Ms)}, p95 {Ms(c.Renders.P95Ms)}, max {Ms(c.Renders.MaxMs)}");
            if (c.MeanRenderAllocatedBytes is { } allocated)
            {
                Line(sb, "Allocations per render", $"{DiagnosticContext.FormatBytes(allocated)} on average, {DiagnosticContext.FormatBytes(c.MaxRenderAllocatedBytes)} at most, {DiagnosticContext.FormatBytes(c.RenderAllocatedBytes)} in total");
            }
            if (c.RendersWithOutputComparison > 0)
            {
                Line(sb, "Renders with identical output", $"{Num(c.RendersWithUnchangedOutput)} of {Num(c.RendersWithOutputComparison)} compared");
            }
        }

        Line(sb, "Parameter updates", $"{Num(c.ParametersSets.Count)} ({Num(c.ParametersUnchanged)} unchanged, {Num(c.ParametersChangedByReferenceOnly)} changed by reference only)");
        Line(sb, "Render requests", $"{Num(c.RenderRequestsCoalesced)} coalesced, {Num(c.RenderRequestsSuppressed)} suppressed by ShouldRender");
        if (c.RenderReasons.Count > 0)
        {
            sb.AppendLine().AppendLine("## Why it rendered");
            foreach (var (reason, count) in c.RenderReasons.OrderByDescending(r => r.Value))
            {
                sb.Append("- ").Append(reason).Append(": ").AppendLine(Num(count));
            }
        }

        if (c.ChangedParameters.Count > 0)
        {
            sb.AppendLine().AppendLine("## Parameters whose values changed");
            foreach (var (parameter, count) in c.ChangedParameters.OrderByDescending(r => r.Value).Take(20))
            {
                sb.Append("- ").Append(parameter).Append(": ").AppendLine(Num(count));
            }
        }

        if (c.ReferenceOnlyParameters.Count > 0)
        {
            sb.AppendLine().AppendLine("## Parameters that were new instances with equal values (cause re-renders)");
            foreach (var (parameter, count) in c.ReferenceOnlyParameters.OrderByDescending(r => r.Value).Take(20))
            {
                sb.Append("- ").Append(parameter).Append(": ").AppendLine(Num(count));
            }
        }

        if (c.Lifecycle.Count > 0)
        {
            sb.AppendLine().AppendLine("## Lifecycle").AppendLine("| method | calls | mean | p95 | max |").AppendLine("|---|---:|---:|---:|---:|");
            foreach (var (method, d) in c.Lifecycle.OrderByDescending(l => l.Value.TotalMs))
            {
                sb.Append("| ").Append(method).Append(" | ").Append(Num(d.Count)).Append(" | ").Append(Ms(d.MeanMs)).Append(" | ").Append(Ms(d.P95Ms)).Append(" | ").Append(Ms(d.MaxMs)).AppendLine(" |");
            }
        }

        var handlers = summary.Handlers.Where(h => h.ComponentType == c.ComponentType).ToList();
        if (handlers.Count > 0)
        {
            sb.AppendLine().AppendLine("## Event handlers");
            foreach (var h in handlers.OrderByDescending(h => h.Durations.TotalMs))
            {
                sb.Append("- ").Append(h.AttributeName ?? "event").Append(" → ").Append(h.MethodName).Append(": ").Append(Num(h.Durations.Count)).Append(" call(s), p95 ").AppendLine(Ms(h.Durations.P95Ms));
            }
        }

        var findings = _api.GetFindings(access, session.Id).Findings.Where(f => f.Suppression is null && f.ComponentType == c.ComponentType).ToList();
        if (findings.Count > 0)
        {
            sb.AppendLine().AppendLine("## Findings");
            foreach (var f in findings)
            {
                sb.Append("- ").Append(f.Severity).Append(" `").Append(f.Fingerprint).Append("`: ").AppendLine(f.Title);
            }
        }

        return sb.ToString();
    }

    private async Task<string> GetReportAsync(ProfilerApiAccess access, Arguments args, CancellationToken cancellationToken)
    {
        var session = ResolveSession(access, args);
        var (data, _, _) = await _api.ExportAsync(access, session.Id, "markdown", cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(data);
    }

    private string ControlSession(ProfilerApiAccess access, Arguments args)
    {
        var action = args.String("action")?.ToLowerInvariant() ?? throw new ArgumentException("action is required.");
        ProfilerSessionStatistics statistics;
        if (action == "start")
        {
            statistics = _api.StartSession(access, args.String("name") ?? "Agent session", null);
        }
        else
        {
            var session = ResolveSession(access, args);
            statistics = _api.Control(access, session.Id, action, args.String("name"));
        }

        return $"Session {statistics.Id} ({statistics.Name}) is {statistics.State}.";
    }

    private async Task<string> RunCommandAsync(ProfilerApiAccess access, Arguments args, CancellationToken cancellationToken)
    {
        var result = await _api.ExecuteCommandAsync(access, args.String("name"), args.String("argument"), cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder(result.Message);
        foreach (var (key, value) in result.Values)
        {
            sb.Append('\n').Append("- ").Append(key).Append(": ").Append(value);
        }

        return sb.ToString();
    }

    private static string Describe(ProfilerEvent e) => e switch
    {
        EventHandlerEvent h => $"handler {h.AttributeName ?? "event"} → {DiagnosticContext.ShortType(h.ComponentType)}.{h.MethodName}",
        NavigationEvent n => $"navigation {n.Route} → {DiagnosticContext.ShortType(n.ComponentType)}",
        OperationEvent o => $"operation {o.Name}",
        ComponentRenderEvent r => $"render {DiagnosticContext.ShortType(r.ComponentType)} #{r.ComponentInstanceId} ({r.Reason}{(r.OutputUnchanged == true ? ", output unchanged" : string.Empty)})",
        ParametersSetEvent p => $"parameters {DiagnosticContext.ShortType(p.ComponentType)} ({p.ChangeState}{(p.ChangedParameters is { Length: > 0 } changed ? ": " + string.Join(", ", changed.Take(5)) : string.Empty)})",
        LifecycleMethodEvent l => $"{l.Method} {DiagnosticContext.ShortType(l.ComponentType)}",
        ComponentCreatedEvent c => $"created {DiagnosticContext.ShortType(c.ComponentType)} #{c.ComponentInstanceId}",
        ComponentDisposedEvent d => $"disposed {DiagnosticContext.ShortType(d.ComponentType)} #{d.ComponentInstanceId}",
        RenderBatchEvent b => $"render batch ({b.UpdatedComponentCount} component(s){(b.IncludesClientRoundTrip == true ? ", incl. browser round trip" : string.Empty)}{(b.TransportBytes is { } sent ? $", {DiagnosticContext.FormatBytes(sent)} sent" : string.Empty)})",
        HttpRequestEvent h => $"HTTP {h.Method} {h.Url} → {h.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? h.Outcome.ToString()}",
        DatabaseCommandEvent d => $"SQL {(d.CommandText is { Length: > 160 } text ? text[..160] + "…" : d.CommandText)}{(d.RowsRead is { } rows ? $" ({rows} rows)" : string.Empty)}{(d.IsAsync ? string.Empty : " [sync]")}",
        JSInteropEvent j => $"interop {j.Direction} {j.Identifier}{(j.PayloadBytes is { } bytes ? $" ({DiagnosticContext.FormatBytes(bytes)})" : string.Empty)}{(j.StreamBytes is { } streamed ? $" (+{DiagnosticContext.FormatBytes(streamed)} streamed)" : string.Empty)}",
        BrowserInteractionEvent b => $"browser {b.EventType} (input delay {Ms(b.InputDelayMs)}, processing {Ms(b.ProcessingMs)}, presentation {Ms(b.PresentationDelayMs)}{(b.ContentPaintMs is { } cp ? $", content painted after {Ms(cp)}" : string.Empty)})",
        BrowserLongTaskEvent l => $"long frame (blocking {Ms(l.BlockingMs)}{(l.ForcedStyleAndLayoutMs > 0 ? $", forced layout {Ms(l.ForcedStyleAndLayoutMs)}" : string.Empty)})",
        CircuitActivityEvent a => $"circuit {a.ActivityName}{(a.ReceivedBytes is { } received ? $" ({DiagnosticContext.FormatBytes(received)} received)" : string.Empty)}",
        MarkerEvent m => $"marker {m.Name ?? m.MarkerType.ToString()}",
        _ => e.Kind.ToString(),
    } + (e.Outcome == ProfilerOutcome.Error ? $" [error {e.ErrorType}]" : string.Empty);

    private static string Result(JsonElement id, Action<Utf8JsonWriter> writeResult) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("jsonrpc", "2.0");
        w.WritePropertyName("id");
        id.WriteTo(w);
        w.WritePropertyName("result");
        writeResult(w);
        w.WriteEndObject();
    });

    private static string Error(JsonElement id, int code, string message) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("jsonrpc", "2.0");
        w.WritePropertyName("id");
        if (id.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            id.WriteTo(w);
        }
        else
        {
            w.WriteNullValue();
        }

        w.WriteStartObject("error");
        w.WriteNumber("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
        w.WriteEndObject();
    });

    private static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Line(StringBuilder sb, string label, string value) => sb.Append("- ").Append(label).Append(": ").AppendLine(value);

    private static string Cell(string? value) =>
        (value ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);

    private static string Ms(double ms) => DiagnosticContext.FormatMs(ms);

    private static string Num(double value) => DiagnosticContext.FormatNumber(value);

    private static string Truncate(string? value, int max) => value is null || value.Length <= max ? value ?? string.Empty : value[..max];

    private sealed record Parameter(string Name, string Type, string Description, string[]? Values = null, bool Required = false);

    private sealed record Tool(
        string Name,
        string Title,
        string Description,
        Parameter[] Parameters,
        Func<Arguments, ProfilerApiAccess, CancellationToken, Task<string>> Run,
        bool Control = false,
        bool Destructive = false);

    private readonly struct Arguments(JsonElement element)
    {
        public string? String(string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
                ? v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetRawText(),
                    _ => null,
                }
                : null;

        public long? Long(string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
                ? v.ValueKind switch
                {
                    JsonValueKind.Number when v.TryGetInt64(out var n) => n,
                    JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
                    _ => throw new ArgumentException($"{name} must be an integer."),
                }
                : null;

        public int Limit(int defaultValue) => (int)Math.Clamp(Long("limit") ?? defaultValue, 1, 200);

        public T? Enum<T>(string name)
            where T : struct, System.Enum
        {
            var value = String(name);
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            return System.Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && System.Enum.IsDefined(parsed)
                ? parsed
                : throw new ArgumentException($"Invalid {name} '{Truncate(value, 32)}'.");
        }
    }
}

/// <summary>An interaction, navigation, or custom operation summarized from its events.</summary>
internal sealed class OperationSummary
{
    public long Id { get; init; }

    public string Kind { get; init; } = "operation";

    public string Name { get; init; } = string.Empty;

    public double DurationMs { get; init; }

    public int Renders { get; init; }

    public int Batches { get; init; }

    public int Http { get; init; }

    public int Database { get; init; }

    public int Interop { get; init; }

    public int Errors { get; init; }

    public static List<OperationSummary> Build(IEnumerable<ProfilerEvent> events, IProfilerClock clock)
    {
        var result = new List<OperationSummary>();
        foreach (var group in events.Where(e => e.OperationId != 0).GroupBy(e => e.OperationId))
        {
            var list = group.ToList();
            var root = list.FirstOrDefault(e => e is EventHandlerEvent or NavigationEvent or OperationEvent);
            if (root is null)
            {
                continue;
            }

            var (kind, name) = root switch
            {
                EventHandlerEvent h => ("interaction", $"{h.AttributeName ?? "event"} → {DiagnosticContext.ShortType(h.ComponentType)}.{h.MethodName}"),
                NavigationEvent n => ("navigation", n.Route ?? DiagnosticContext.ShortType(n.ComponentType)),
                OperationEvent o => ("custom", o.Name ?? "operation"),
                _ => ("operation", string.Empty),
            };

            result.Add(new OperationSummary
            {
                Id = group.Key,
                Kind = kind,
                Name = name,
                DurationMs = clock.ToMilliseconds(list.Max(e => e.EndTimestamp) - list.Min(e => e.Timestamp)),
                Renders = list.Count(e => e is ComponentRenderEvent),
                Batches = list.Count(e => e is RenderBatchEvent),
                Http = list.Count(e => e is HttpRequestEvent),
                Database = list.Count(e => e is DatabaseCommandEvent),
                Interop = list.Count(e => e is JSInteropEvent),
                Errors = list.Count(e => e.Outcome == ProfilerOutcome.Error),
            });
        }

        return result;
    }
}
