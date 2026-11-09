using System.Globalization;
using System.Text;
using BlazorProfiler.Analysis;
using BlazorProfiler.Sessions;
using BlazorProfiler.Storage;

namespace BlazorProfiler.Testing;

/// <summary>
/// Thrown by <see cref="ProfilerAssert"/> and <see cref="ProfilerBudget"/> when profiling data violates an expectation.
/// The message lists every violation, so a failing test explains itself.
/// </summary>
public sealed class ProfilerAssertionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ProfilerAssertionException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ProfilerAssertionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public ProfilerAssertionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The individual violations.</summary>
    public IReadOnlyList<string> Violations { get; init; } = [];
}

/// <summary>
/// Assertions for tests and CI: fail when the diagnostic rules report problems, in the spirit of N+1 detectors that raise in
/// test suites. Rules are deterministic, so the same interactions always produce the same findings.
/// </summary>
/// <example>
/// <code>
/// // In a bUnit or integration test with the profiler registered (Pipeline.SynchronousProcessing = true):
/// var session = profiler.StartSession();
/// cut.Find("button").Click();
/// await ProfilerAssert.NoFindingsAsync(profiler, session, DiagnosticSeverity.Medium, "BP1006");
/// </code>
/// </example>
public static class ProfilerAssert
{
    /// <summary>
    /// Flushes pending events, analyzes the session, and throws <see cref="ProfilerAssertionException"/> when a finding at or
    /// above <paramref name="minimumSeverity"/> is reported (dismissed or expected findings and <paramref name="ignoredRules"/>
    /// excepted).
    /// </summary>
    public static async Task NoFindingsAsync(IProfiler profiler, IProfilerSession session, DiagnosticSeverity minimumSeverity = DiagnosticSeverity.Low, params string[] ignoredRules)
    {
        ArgumentNullException.ThrowIfNull(profiler);
        ArgumentNullException.ThrowIfNull(session);
        await profiler.FlushAsync().ConfigureAwait(false);
        NoFindings(profiler.Diagnostics.Analyze(session), minimumSeverity, ignoredRules);
    }

    /// <summary>
    /// Throws <see cref="ProfilerAssertionException"/> when <paramref name="report"/> contains a finding at or above
    /// <paramref name="minimumSeverity"/> (dismissed or expected findings and <paramref name="ignoredRules"/> excepted), or
    /// when a rule failed to run.
    /// </summary>
    public static void NoFindings(DiagnosticReport report, DiagnosticSeverity minimumSeverity = DiagnosticSeverity.Low, params string[] ignoredRules)
    {
        ArgumentNullException.ThrowIfNull(report);
        var ignored = new HashSet<string>(ignoredRules ?? [], StringComparer.OrdinalIgnoreCase);
        var violations = report.Findings
            .Where(f => f.Suppression is null && f.Severity >= minimumSeverity && !ignored.Contains(f.RuleId))
            .Select(f => $"{f.RuleId} {f.Severity} ({f.Confidence} confidence): {f.Title}. {f.Summary} Fix: {f.Recommendation}")
            .Concat(report.RuleErrors.Select(e => $"{e.Key} failed to run: {e.Value}"))
            .ToList();
        Throw(violations, $"Profiler reported {violations.Count} problem(s) at or above {minimumSeverity}:");
    }

    internal static void Throw(List<string> violations, string heading)
    {
        if (violations.Count == 0)
        {
            return;
        }

        var message = new StringBuilder(heading);
        foreach (var violation in violations)
        {
            message.AppendLine().Append(" - ").Append(violation);
        }

        throw new ProfilerAssertionException(message.ToString()) { Violations = violations };
    }
}

/// <summary>
/// Limits on what a scenario may cost, checked against a session summary. Counts (renders, batches, requests, queries,
/// interop calls, retained components) are deterministic for a given scenario and make stable CI gates; durations vary with
/// the machine, so give them generous limits. Unset limits are not checked.
/// </summary>
/// <example>
/// <code>
/// new ProfilerBudget { MaxComponentRenders = 20, MaxDatabaseCommands = 2, MaxRetainedComponents = 0 }
///     .WithMaxRenders("MyApp.Components.ProductRow", 10)
///     .Assert(session.GetSummary());
/// </code>
/// </example>
public sealed class ProfilerBudget
{
    private readonly Dictionary<string, double> _maxRendersByComponent = new(StringComparer.Ordinal);

    /// <summary>The maximum number of render batches.</summary>
    public double? MaxRenderBatches { get; init; }

    /// <summary>The maximum number of instrumented component renders (all types).</summary>
    public double? MaxComponentRenders { get; init; }

    /// <summary>The maximum number of components updated across all render batches (renders plus framework-only updates).</summary>
    public double? MaxUpdatedComponents { get; init; }

    /// <summary>The maximum number of HTTP requests.</summary>
    public double? MaxHttpRequests { get; init; }

    /// <summary>The maximum number of failed HTTP requests.</summary>
    public double? MaxFailedHttpRequests { get; init; }

    /// <summary>The maximum number of database commands.</summary>
    public double? MaxDatabaseCommands { get; init; }

    /// <summary>The maximum number of JavaScript interop calls.</summary>
    public double? MaxInteropCalls { get; init; }

    /// <summary>The maximum number of component instances retained after release (possible leaks).</summary>
    public double? MaxRetainedComponents { get; init; }

    /// <summary>The maximum p95 event-handler duration in milliseconds (machine dependent).</summary>
    public double? MaxHandlerP95Ms { get; init; }

    /// <summary>The maximum p95 render-batch duration in milliseconds (machine dependent).</summary>
    public double? MaxRenderBatchP95Ms { get; init; }

    /// <summary>The maximum bytes allocated during the session (process-wide; machine and runtime dependent).</summary>
    public double? MaxAllocatedBytes { get; init; }

    /// <summary>Limits the renders (or, for components that are not instrumented, parameter updates) of one component type.</summary>
    /// <param name="componentType">The full type name.</param>
    /// <param name="maxRenders">The maximum count.</param>
    public ProfilerBudget WithMaxRenders(string componentType, double maxRenders)
    {
        ArgumentException.ThrowIfNullOrEmpty(componentType);
        _maxRendersByComponent[componentType] = maxRenders;
        return this;
    }

    /// <summary>Returns the violated limits, or an empty list.</summary>
    public IReadOnlyList<string> Check(SessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var violations = new List<string>();
        void Limit(string name, double? max, double actual, Func<double, string>? format = null)
        {
            if (max is { } limit && actual > limit + 1e-9)
            {
                format ??= v => v.ToString("0.##", CultureInfo.InvariantCulture);
                violations.Add($"{name}: {format(actual)} exceeds the budget of {format(limit)}");
            }
        }

        static string Ms(double v) => DiagnosticContext.FormatMs(v);
        Limit("Render batches", MaxRenderBatches, summary.RenderBatches.Durations.Count);
        Limit("Component renders", MaxComponentRenders, summary.Components.Sum(c => c.Renders.Count));
        Limit("Updated components", MaxUpdatedComponents, summary.RenderBatches.TotalUpdatedComponents);
        Limit("HTTP requests", MaxHttpRequests, summary.Endpoints.Sum(e => e.Durations.Count));
        Limit("Failed HTTP requests", MaxFailedHttpRequests, summary.Endpoints.Sum(e => e.Failures));
        Limit("Database commands", MaxDatabaseCommands, summary.Queries.Sum(q => q.Durations.Count));
        Limit("JS interop calls", MaxInteropCalls, summary.Interop.Sum(i => i.Durations.Count));
        Limit("Retained components", MaxRetainedComponents, summary.Components.Sum(c => c.Retained));
        Limit("Event handler p95", MaxHandlerP95Ms, summary.Handlers.Select(h => h.Durations.P95Ms).DefaultIfEmpty().Max(), Ms);
        Limit("Render batch p95", MaxRenderBatchP95Ms, summary.RenderBatches.Durations.P95Ms, Ms);
        Limit("Allocated bytes", MaxAllocatedBytes, summary.Runtime.AllocatedBytes, v => DiagnosticContext.FormatBytes(v));
        foreach (var (type, max) in _maxRendersByComponent)
        {
            var component = summary.Components.FirstOrDefault(c => c.ComponentType == type);
            var actual = component is null ? 0 : component.Instrumented ? component.Renders.Count : component.ParametersSets.Count;
            Limit($"{DiagnosticContext.ShortType(type)} renders", max, actual);
        }

        return violations;
    }

    /// <summary>Throws <see cref="ProfilerAssertionException"/> when a limit is exceeded.</summary>
    public void Assert(SessionSummary summary) => ProfilerAssert.Throw([.. Check(summary)], "The scenario exceeded its profiling budget:");

    /// <summary>Flushes pending events and checks the session's summary; throws when a limit is exceeded.</summary>
    public async Task AssertAsync(IProfiler profiler, IProfilerSession session)
    {
        ArgumentNullException.ThrowIfNull(profiler);
        ArgumentNullException.ThrowIfNull(session);
        await profiler.FlushAsync().ConfigureAwait(false);
        Assert(session.GetSummary());
    }
}
