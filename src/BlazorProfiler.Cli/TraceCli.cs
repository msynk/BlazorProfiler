using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorProfiler.Analysis;
using BlazorProfiler.Export;
using BlazorProfiler.Storage;
using BlazorProfiler.Testing;

namespace BlazorProfiler.Cli;

/// <summary>The command-line interface. Exit codes: 0 success, 1 violations found, 2 invalid usage or input.</summary>
internal static class TraceCli
{
    public const int Success = 0;
    public const int Violations = 1;
    public const int UsageError = 2;

    public const string Usage = """
        blazor-profiler: analyze BlazorProfiler traces (.bptrace or .json exports).

        Usage:
          blazor-profiler check <trace> [--fail-on <severity>] [--ignore <ids>] [--budget <file>]
                                        [--format text|markdown|sarif|github] [--output <file>]
              Re-analyzes the trace with the current rules. Fails (exit code 1) when a finding at or above the
              --fail-on severity (default: medium) remains, or when a budget limit is exceeded.
          blazor-profiler report <trace> [--output <file>]
              Writes a Markdown report.
          blazor-profiler compare <baseline> <current> [--threshold <percent>] [--include-timings]
                                  [--format text|markdown] [--output <file>]
              Compares key metrics. Fails when a count (renders, batches, requests, queries, interop calls, retained
              components) grows by more than --threshold percent (default 10). Durations fail only with --include-timings.

        Severities: info, low, medium, high, critical. Rule ids: comma-separated, for example BP1006,BP3003.
        Budget file (JSON): { "maxComponentRenders": 50, "maxDatabaseCommands": 3, "maxRetainedComponents": 0,
                              "maxRendersByComponent": { "MyApp.Components.Row": 20 } }
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                await output.WriteLineAsync(Usage).ConfigureAwait(false);
                return args.Length == 0 ? UsageError : Success;
            }

            var options = Options.Parse(args.Skip(1).ToArray());
            return args[0] switch
            {
                "check" => await CheckAsync(options, output, cancellationToken).ConfigureAwait(false),
                "report" => await ReportAsync(options, output, cancellationToken).ConfigureAwait(false),
                "compare" => await CompareAsync(options, output, cancellationToken).ConfigureAwait(false),
                _ => throw new CliException($"Unknown command '{args[0]}'."),
            };
        }
        catch (CliException ex)
        {
            await error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            await error.WriteLineAsync("Run 'blazor-profiler --help' for usage.").ConfigureAwait(false);
            return UsageError;
        }
        catch (Exception ex) when (ex is TraceImportException or IOException or UnauthorizedAccessException or JsonException)
        {
            await error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return UsageError;
        }
    }

    private static async Task<int> CheckAsync(Options options, TextWriter output, CancellationToken cancellationToken)
    {
        var path = options.Positional(0, "trace");
        var (document, summary, report) = await AnalyzeAsync(path, cancellationToken).ConfigureAwait(false);
        var failOn = options.Severity("--fail-on", DiagnosticSeverity.Medium);
        var ignored = new HashSet<string>(options.List("--ignore"), StringComparer.OrdinalIgnoreCase);
        var failing = report.Findings.Where(f => f.Suppression is null && f.Severity >= failOn && !ignored.Contains(f.RuleId)).ToList();
        var budgetViolations = options.Value("--budget") is { } budgetPath ? LoadBudget(budgetPath).Check(summary) : [];

        var format = options.Value("--format") ?? "text";
        var text = format switch
        {
            "text" => CheckText(document, report, failing, budgetViolations, failOn),
            "markdown" or "md" => MarkdownReportExporter.Render(WithFindings(document, report)) + BudgetMarkdown(budgetViolations),
            "sarif" => Sarif(report, failing, ignored),
            "github" => GitHubAnnotations(report, failing, budgetViolations, ignored),
            _ => throw new CliException($"Unknown format '{format}'."),
        };

        await WriteAsync(options, output, text, cancellationToken).ConfigureAwait(false);
        return failing.Count > 0 || budgetViolations.Count > 0 ? Violations : Success;
    }

    private static async Task<int> ReportAsync(Options options, TextWriter output, CancellationToken cancellationToken)
    {
        var (document, _, report) = await AnalyzeAsync(options.Positional(0, "trace"), cancellationToken).ConfigureAwait(false);
        await WriteAsync(options, output, MarkdownReportExporter.Render(WithFindings(document, report)), cancellationToken).ConfigureAwait(false);
        return Success;
    }

    private static async Task<int> CompareAsync(Options options, TextWriter output, CancellationToken cancellationToken)
    {
        var (_, baseline, _) = await AnalyzeAsync(options.Positional(0, "baseline"), cancellationToken).ConfigureAwait(false);
        var (_, current, _) = await AnalyzeAsync(options.Positional(1, "current"), cancellationToken).ConfigureAwait(false);
        var threshold = options.Number("--threshold", 10) / 100;
        var includeTimings = options.Flag("--include-timings");
        var rows = Compare(baseline, current, threshold, includeTimings);
        var markdown = options.Value("--format") is "markdown" or "md";
        var text = new StringBuilder();
        if (markdown)
        {
            text.AppendLine("| Metric | Baseline | Current | Change | |").AppendLine("|---|---:|---:|---:|---|");
        }

        foreach (var row in rows)
        {
            var change = row.Baseline == 0 ? (row.Current == 0 ? "0%" : "new") : ((row.Current - row.Baseline) / row.Baseline).ToString("+0%;-0%;0%", CultureInfo.InvariantCulture);
            var status = row.Regressed ? "REGRESSION" : row.Improved ? "improved" : string.Empty;
            text.AppendLine(markdown
                ? $"| {row.Name} | {row.Format(row.Baseline)} | {row.Format(row.Current)} | {change} | {status} |"
                : $"{row.Name,-28} {row.Format(row.Baseline),12} {row.Format(row.Current),12} {change,8}  {status}");
        }

        var regressions = rows.Count(r => r.Regressed);
        text.AppendLine().AppendLine(regressions == 0 ? "No regressions." : $"{regressions} regression(s) above {threshold * 100:0.#}%.");
        await WriteAsync(options, output, text.ToString(), cancellationToken).ConfigureAwait(false);
        return regressions > 0 ? Violations : Success;
    }

    internal static List<ComparisonRow> Compare(SessionSummary baseline, SessionSummary current, double threshold, bool includeTimings)
    {
        static string Count(double v) => DiagnosticContext.FormatNumber(v);
        static string Ms(double v) => DiagnosticContext.FormatMs(v);
        static string Bytes(double v) => DiagnosticContext.FormatBytes(v);
        var metrics = new (string Name, Func<SessionSummary, double> Value, Func<double, string> Format, bool Deterministic)[]
        {
            ("Render batches", s => s.RenderBatches.Durations.Count, Count, true),
            ("Components updated", s => s.RenderBatches.TotalUpdatedComponents, Count, true),
            ("Component renders", s => s.Components.Sum(c => c.Renders.Count), Count, true),
            ("Parameter updates", s => s.Components.Sum(c => c.ParametersSets.Count), Count, true),
            ("HTTP requests", s => s.Endpoints.Sum(e => e.Durations.Count), Count, true),
            ("Database commands", s => s.Queries.Sum(q => q.Durations.Count), Count, true),
            ("JS interop calls", s => s.Interop.Sum(i => i.Durations.Count), Count, true),
            ("Retained components", s => s.Components.Sum(c => c.Retained), Count, true),
            ("Event handler p95", s => s.Handlers.Select(h => h.Durations.P95Ms).DefaultIfEmpty().Max(), Ms, false),
            ("Render batch p95", s => s.RenderBatches.Durations.P95Ms, Ms, false),
            ("Browser interaction p95", s => s.BrowserInteractions.P95Ms, Ms, false),
            ("Allocated", s => s.Runtime.AllocatedBytes, Bytes, false),
        };

        var rows = new List<ComparisonRow>();
        foreach (var (name, value, format, deterministic) in metrics)
        {
            var a = value(baseline);
            var b = value(current);
            var grew = b > a * (1 + threshold) && b - a >= (deterministic ? 1 : 0.001);
            var shrank = b < a * (1 - threshold);
            rows.Add(new ComparisonRow(name, a, b, format, grew && (deterministic || includeTimings), shrank));
        }

        return rows;
    }

    private static async Task<(TraceDocument Document, SessionSummary Summary, DiagnosticReport Report)> AnalyzeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new CliException($"File not found: {path}");
        }

        TraceDocument document;
        await using (var stream = File.OpenRead(path))
        {
            document = await TraceImporter.ReadAsync(stream, new TraceImportOptions { MaxInputBytes = 1L << 31, MaxDecompressedBytes = 4L << 30, MaxEvents = 10_000_000 }, cancellationToken).ConfigureAwait(false);
        }

        var options = new ProfilerOptions { Enabled = true, AutoStartSession = false };
        using var host = new ProfilerHost(options);
        var session = host.AddImportedSession(document);
        return (document, session.GetSummary(), host.Diagnostics.Analyze(session));
    }

    private static TraceDocument WithFindings(TraceDocument document, DiagnosticReport report)
    {
        document.Findings = [.. report.Findings];
        return document;
    }

    private static string CheckText(TraceDocument document, DiagnosticReport report, List<DiagnosticFinding> failing, IReadOnlyList<string> budgetViolations, DiagnosticSeverity failOn)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{document.Metadata.SessionName ?? "trace"}: {report.AnalyzedEvents} events, {report.Findings.Count} finding(s), {failing.Count} at or above {failOn}.").AppendLine();
        foreach (var f in report.Findings.OrderByDescending(f => f.Severity))
        {
            var marker = failing.Contains(f) ? "FAIL" : f.Suppression is null ? "    " : "skip";
            text.Append(CultureInfo.InvariantCulture, $"{marker} [{f.Severity}] {f.RuleId} {f.Title}").AppendLine();
            text.Append("       ").AppendLine(f.Summary);
            if (failing.Contains(f))
            {
                text.Append("       Fix: ").AppendLine(f.Recommendation);
            }
        }

        foreach (var violation in budgetViolations)
        {
            text.Append("FAIL [Budget] ").AppendLine(violation);
        }

        foreach (var (rule, message) in report.RuleErrors)
        {
            text.Append(CultureInfo.InvariantCulture, $"warn rule {rule} failed: {message}").AppendLine();
        }

        return text.ToString();
    }

    private static string BudgetMarkdown(IReadOnlyList<string> violations) =>
        violations.Count == 0 ? string.Empty : Environment.NewLine + "## Budget violations" + Environment.NewLine + Environment.NewLine + string.Concat(violations.Select(v => "- " + v + Environment.NewLine));

    internal static string Sarif(DiagnosticReport report, List<DiagnosticFinding> failing, HashSet<string> ignored)
    {
        var rules = new JsonArray();
        foreach (var rule in report.Rules.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            rules.Add(new JsonObject
            {
                ["id"] = rule.Id,
                ["name"] = string.Concat(rule.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..])).Replace("-", string.Empty, StringComparison.Ordinal),
                ["shortDescription"] = new JsonObject { ["text"] = rule.Title },
                ["fullDescription"] = new JsonObject { ["text"] = rule.Description },
                ["helpUri"] = DocumentationRecommendationProvider.BaseUrl + rule.Id.ToLowerInvariant(),
            });
        }

        var results = new JsonArray();
        foreach (var f in report.Findings.Where(f => f.Suppression is null && !ignored.Contains(f.RuleId)))
        {
            var result = new JsonObject
            {
                ["ruleId"] = f.RuleId,
                ["level"] = !failing.Contains(f) ? "note" : f.Severity >= DiagnosticSeverity.High ? "error" : "warning",
                ["message"] = new JsonObject { ["text"] = $"{f.Title}. {f.Summary} Suggested fix: {f.Recommendation}" },
                ["partialFingerprints"] = new JsonObject { ["blazorProfiler/v1"] = f.Fingerprint },
                ["properties"] = new JsonObject { ["severity"] = f.Severity.ToString(), ["confidence"] = f.Confidence.ToString(), ["category"] = f.Category.ToString() },
            };
            var location = f.ComponentType ?? f.Subject;
            if (location is not null)
            {
                result["locations"] = new JsonArray(new JsonObject
                {
                    ["logicalLocations"] = new JsonArray(new JsonObject { ["fullyQualifiedName"] = location, ["kind"] = f.ComponentType is null ? "member" : "type" }),
                });
            }

            results.Add(result);
        }

        var sarif = new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(new JsonObject
            {
                ["tool"] = new JsonObject
                {
                    ["driver"] = new JsonObject
                    {
                        ["name"] = "BlazorProfiler",
                        ["version"] = TraceDocumentBuilder.ProfilerVersion,
                        ["informationUri"] = "https://github.com/msynk/BlazorProfiler",
                        ["rules"] = rules,
                    },
                },
                ["results"] = results,
            }),
        };
        return sarif.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    internal static string GitHubAnnotations(DiagnosticReport report, List<DiagnosticFinding> failing, IReadOnlyList<string> budgetViolations, HashSet<string> ignored)
    {
        static string Escape(string value) => value.Replace("%", "%25", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);
        static string EscapeProperty(string value) => Escape(value).Replace(":", "%3A", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal);
        var text = new StringBuilder();
        foreach (var f in report.Findings.Where(f => f.Suppression is null && !ignored.Contains(f.RuleId)))
        {
            var level = !failing.Contains(f) ? "notice" : f.Severity >= DiagnosticSeverity.High ? "error" : "warning";
            text.Append("::").Append(level).Append(" title=").Append(EscapeProperty($"{f.RuleId} {f.Title}")).Append("::").AppendLine(Escape($"{f.Summary} Suggested fix: {f.Recommendation}"));
        }

        foreach (var violation in budgetViolations)
        {
            text.Append("::error title=Profiler budget::").AppendLine(Escape(violation));
        }

        return text.ToString();
    }

    private static ProfilerBudget LoadBudget(string path)
    {
        if (!File.Exists(path))
        {
            throw new CliException($"Budget file not found: {path}");
        }

        var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new CliException("The budget file must contain a JSON object.");
        double? Get(string name) => node.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value?.GetValue<double>();
        var budget = new ProfilerBudget
        {
            MaxRenderBatches = Get("maxRenderBatches"),
            MaxComponentRenders = Get("maxComponentRenders"),
            MaxUpdatedComponents = Get("maxUpdatedComponents"),
            MaxHttpRequests = Get("maxHttpRequests"),
            MaxFailedHttpRequests = Get("maxFailedHttpRequests"),
            MaxDatabaseCommands = Get("maxDatabaseCommands"),
            MaxInteropCalls = Get("maxInteropCalls"),
            MaxRetainedComponents = Get("maxRetainedComponents"),
            MaxHandlerP95Ms = Get("maxHandlerP95Ms"),
            MaxRenderBatchP95Ms = Get("maxRenderBatchP95Ms"),
            MaxAllocatedBytes = Get("maxAllocatedBytes"),
        };
        if (node.FirstOrDefault(p => string.Equals(p.Key, "maxRendersByComponent", StringComparison.OrdinalIgnoreCase)).Value is JsonObject perComponent)
        {
            foreach (var (type, value) in perComponent)
            {
                budget.WithMaxRenders(type, value?.GetValue<double>() ?? throw new CliException($"maxRendersByComponent['{type}'] must be a number."));
            }
        }

        return budget;
    }

    private static async Task WriteAsync(Options options, TextWriter output, string text, CancellationToken cancellationToken)
    {
        if (options.Value("--output") is { } file)
        {
            await File.WriteAllTextAsync(file, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await output.WriteAsync(text).ConfigureAwait(false);
        }
    }

    internal sealed record ComparisonRow(string Name, double Baseline, double Current, Func<double, string> Format, bool Regressed, bool Improved);

    private sealed class CliException(string message) : Exception(message);

    /// <summary>Parsed arguments: positional values, <c>--name value</c> options, and <c>--flag</c> switches.</summary>
    private sealed class Options
    {
        private static readonly HashSet<string> s_flags = new(StringComparer.Ordinal) { "--include-timings" };
        private readonly List<string> _positional = [];
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _switches = new(StringComparer.Ordinal);

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    options._positional.Add(arg);
                }
                else if (s_flags.Contains(arg))
                {
                    options._switches.Add(arg);
                }
                else if (i + 1 < args.Length)
                {
                    options._values[arg] = args[++i];
                }
                else
                {
                    throw new CliException($"Option {arg} requires a value.");
                }
            }

            return options;
        }

        public string Positional(int index, string name) =>
            index < _positional.Count ? _positional[index] : throw new CliException($"Missing argument <{name}>.");

        public string? Value(string name) => _values.GetValueOrDefault(name);

        public bool Flag(string name) => _switches.Contains(name);

        public IEnumerable<string> List(string name) =>
            Value(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        public double Number(string name, double fallback) =>
            Value(name) is not { } text ? fallback
            : double.TryParse(text.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value
            : throw new CliException($"{name} must be a non-negative number.");

        public DiagnosticSeverity Severity(string name, DiagnosticSeverity fallback) =>
            Value(name) is not { } text ? fallback
            : Enum.TryParse<DiagnosticSeverity>(text, ignoreCase: true, out var severity) && Enum.IsDefined(severity) ? severity
            : throw new CliException($"{name} must be one of info, low, medium, high, critical.");
    }
}
