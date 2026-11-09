using System.Globalization;
using BlazorProfiler.Events;
using static BlazorProfiler.Analysis.DiagnosticContext;

namespace BlazorProfiler.Analysis.Rules;

/// <summary>BP7003: a large or slow Blazor framework download at startup.</summary>
public sealed class LargeFrameworkDownloadRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP7003",
        Title = "Large framework download",
        Category = DiagnosticCategory.Browser,
        Description = "Reports page loads that transferred at least LargeFrameworkDownloadBytes of _framework files, or spent at least SlowStartupMs downloading them, and flags large files served without compression.",
        RequiredSignals = [ProfilerEventKind.BrowserStartup],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var startup = context.OfType<BrowserStartupEvent>().OrderByDescending(s => s.FrameworkTransferBytes).FirstOrDefault();
        if (startup is null || startup.FrameworkFiles == 0)
        {
            yield break;
        }

        var options = context.Options;
        var large = startup.FrameworkTransferBytes >= options.LargeFrameworkDownloadBytes;
        var slow = startup.FrameworkTransferBytes > 0 && startup.FrameworkDownloadMs >= options.SlowStartupMs;
        if (!large && !slow && startup.FrameworkUncompressedFiles == 0)
        {
            yield break;
        }

        var severity = large ? Scale(startup.FrameworkTransferBytes, options.LargeFrameworkDownloadBytes) : slow ? Scale(startup.FrameworkDownloadMs, options.SlowStartupMs) : DiagnosticSeverity.Low;
        var largest = startup.LargestFiles.OrderByDescending(f => f.DecodedSize).Take(5).ToList();
        var largestText = largest.Count == 0 ? "not reported" : string.Join(", ", largest.Select(f => $"{f.Name} ({FormatBytes(f.DecodedSize)})"));
        var recommendations = new List<string>();
        if (startup.FrameworkUncompressedFiles > 0)
        {
            recommendations.Add("Serve the framework files compressed: `MapStaticAssets` serves precompressed Brotli/gzip files, and other hosts need Brotli or gzip enabled for .wasm, .dll/.webcil, .js, and .dat files.");
        }

        recommendations.Add("Publish in Release with trimming (the default for WebAssembly), consider `<InvariantGlobalization>true</InvariantGlobalization>` or loading only the ICU data you need, lazy-load assemblies used by rarely visited pages (`BlazorWebAssemblyLazyLoad`), and remove or replace the largest dependencies listed in the evidence.");
        recommendations.Add("AOT compilation makes the download larger; use it only for CPU-bound code. For a fast first visit, consider the Interactive Auto render mode (server first, WebAssembly once cached).");

        yield return Create(
            startup.FrameworkUncompressedFiles > 0 && !large && !slow
                ? $"{FormatNumber(startup.FrameworkUncompressedFiles)} large framework file(s) were served without compression"
                : $"The app downloads {FormatBytes(startup.FrameworkTransferBytes)} of framework files before it becomes interactive",
            severity,
            DiagnosticConfidence.High,
            $"{FormatNumber(startup.FrameworkFiles)} _framework files: {FormatBytes(startup.FrameworkTransferBytes)} transferred ({FormatBytes(startup.FrameworkDecodedBytes)} decoded), {FormatNumber(startup.FrameworkCachedFiles)} from the browser cache, downloaded over {FormatMs(startup.FrameworkDownloadMs)}. {FormatNumber(startup.FrameworkUncompressedFiles)} large file(s) arrived without compression.",
            "Until the .NET runtime and the app's assemblies are downloaded and started, WebAssembly components cannot respond. Every megabyte costs seconds on slow mobile connections, and first-time visitors always pay it.",
            string.Join(" ", recommendations),
            [
                Evidence("Transferred", FormatBytes(startup.FrameworkTransferBytes)),
                Evidence("Decoded", FormatBytes(startup.FrameworkDecodedBytes)),
                Evidence("Files (cached)", $"{FormatNumber(startup.FrameworkFiles)} ({FormatNumber(startup.FrameworkCachedFiles)})"),
                Evidence("Uncompressed large files", FormatNumber(startup.FrameworkUncompressedFiles)),
                Evidence("Download time", FormatMs(startup.FrameworkDownloadMs)),
                Evidence("Largest files", largestText, largest.Count > 0 ? ValueQuality.Measured : ValueQuality.Unavailable),
            ],
            new ImpactEstimate { TotalMs = Math.Round(startup.FrameworkDownloadMs, 3), Occurrences = 1, Method = "Time from the first to the last _framework download on this page load.", Quality = ValueQuality.Measured },
            subject: "framework-download",
            caveats:
            [
                "Development builds are neither trimmed nor compressed; measure a Release publish served the way production serves it.",
                "Download time depends on the network of the machine that loaded the page; local loads are much faster than real users' connections.",
            ],
            related: [startup]);
    }
}

/// <summary>BP7004: a long gap between first paint and interactivity (prerendered content that does not respond yet).</summary>
public sealed class SlowInteractivityRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP7004",
        Title = "Slow interactivity after first paint",
        Category = DiagnosticCategory.Browser,
        Description = "Reports page loads where at least InteractivityGapMs passed between First Contentful Paint and the moment the page became interactive: users see content (often prerendered) that does not respond to input yet.",
        RequiredSignals = [ProfilerEventKind.BrowserStartup],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        var threshold = context.Options.InteractivityGapMs;
        var startup = context.OfType<BrowserStartupEvent>()
            .Where(s => s.FirstContentfulPaintMs > 0 && s.InteractiveMs > s.FirstContentfulPaintMs)
            .OrderByDescending(s => s.InteractiveMs - s.FirstContentfulPaintMs)
            .FirstOrDefault();
        if (startup is null)
        {
            yield break;
        }

        var gap = startup.InteractiveMs - startup.FirstContentfulPaintMs;
        if (gap < threshold)
        {
            yield break;
        }

        var webAssembly = startup.RuntimeKind?.Contains("webassembly", StringComparison.Ordinal) == true;
        yield return Create(
            $"The page responds {FormatMs(gap)} after it first appears",
            Scale(gap, threshold),
            DiagnosticConfidence.Medium,
            $"First Contentful Paint at {FormatMs(startup.FirstContentfulPaintMs)}, interactive at {FormatMs(startup.InteractiveMs)} (time to first byte {FormatMs(startup.TimeToFirstByteMs)}). For {FormatMs(gap)} users could see the page but clicks and typing did nothing.",
            webAssembly
                ? "With prerendering, the HTML arrives early but WebAssembly components only respond after the runtime and assemblies are downloaded and started. Clicks during that window are lost (\"dead clicks\")."
                : "With prerendering, the HTML arrives early but interactive components only respond after the circuit (or runtime) starts. Clicks during that window are lost (\"dead clicks\").",
            webAssembly
                ? "Shrink the WebAssembly download (see BP7003), use the Interactive Auto render mode so first visits start on the server, show a clear loading state on controls that are not interactive yet, and keep static SSR for content that does not need interactivity."
                : "Reduce work before the first interactive render (heavy OnInitializedAsync in layout components, large root components), keep static SSR for content that does not need interactivity, and indicate when controls are not interactive yet.",
            [
                Evidence("First Contentful Paint", FormatMs(startup.FirstContentfulPaintMs)),
                Evidence("Interactive", FormatMs(startup.InteractiveMs), ValueQuality.Estimated),
                Evidence("Gap", FormatMs(gap), ValueQuality.Estimated),
                Evidence("Runtime", startup.RuntimeKind ?? "unknown"),
            ],
            new ImpactEstimate { TotalMs = Math.Round(gap - threshold, 3), Occurrences = 1, Method = "Time between first paint and interactivity beyond the threshold.", Quality = ValueQuality.Estimated },
            subject: "interactivity-gap",
            caveats: ["Interactivity is estimated from the first interactive render of BlazorProfilerClient; render it early in the layout (for example in MainLayout) so it becomes interactive with the rest of the page."],
            related: [startup]);
    }
}

/// <summary>BP7005: Core Web Vitals (LCP, CLS, INP) that are not rated good.</summary>
public sealed class CoreWebVitalsRule : DiagnosticRule
{
    /// <inheritdoc />
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "BP7005",
        Title = "Core Web Vitals need improvement",
        Category = DiagnosticCategory.Browser,
        Description = "Reports Largest Contentful Paint, Cumulative Layout Shift, and Interaction to Next Paint values that the browser measured above the Web Vitals \"good\" thresholds (2.5 s, 0.1, 200 ms).",
        RequiredSignals = [ProfilerEventKind.BrowserVital],
    };

    /// <inheritdoc />
    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        foreach (var group in context.OfType<BrowserVitalEvent>().Where(v => v.Name is not null).GroupBy(v => v.Name!))
        {
            var worst = group.MaxBy(v => v.Value)!;
            if (worst.Rating is null or "good")
            {
                continue;
            }

            var (good, unit, label) = group.Key switch
            {
                "LCP" => (2500d, "ms", "Largest Contentful Paint"),
                "CLS" => (0.1d, string.Empty, "Cumulative Layout Shift"),
                _ => (200d, "ms", "Interaction to Next Paint (estimated)"),
            };
            var value = unit == "ms" ? FormatMs(worst.Value) : worst.Value.ToString("0.###", CultureInfo.InvariantCulture);
            var (explanation, recommendation) = group.Key switch
            {
                "LCP" => (
                    "The largest visible element appeared late. In Blazor this happens when the main content renders only after WebAssembly starts or after OnInitializedAsync loads its data.",
                    "Prerender (or statically render) the main content so it is in the initial HTML, use streaming rendering with placeholders for slow data, preload the LCP image or font, and avoid render-blocking scripts and styles."),
                "CLS" => (
                    "Visible content moved after it was shown. Typical Blazor causes: the interactive render differs from the prerendered HTML, data loaded in OnInitializedAsync replaces a smaller placeholder, or images without dimensions.",
                    "Reserve space for content that loads later (fixed-size skeletons), render the same layout during prerendering and interactively, give images width and height, and avoid inserting content above existing content."),
                _ => (
                    "Interactions take long to produce the next frame. See BP7001 for the slowest interaction types and their input delay, processing, and presentation phases.",
                    "Keep event handlers short, render less per interaction (ShouldRender, @key, Virtualize), and move long work off the UI thread or yield before it."),
            };

            yield return Create(
                $"{label} is {value} ({worst.Rating})",
                worst.Rating == "poor" ? Cap(Scale(worst.Value, good), DiagnosticSeverity.High) : DiagnosticSeverity.Low,
                DiagnosticConfidence.High,
                $"The browser measured {label} at {value}{(worst.TargetTag is null ? string.Empty : $" (element: <{worst.TargetTag}>)")}; good is {(unit == "ms" ? FormatMs(good) : good.ToString(CultureInfo.InvariantCulture))} or less.",
                explanation,
                recommendation,
                [
                    Evidence(label, value),
                    Evidence("Rating", worst.Rating),
                    Evidence("Element", worst.TargetTag ?? "not reported", worst.TargetTag is null ? ValueQuality.Unavailable : ValueQuality.Measured),
                ],
                null,
                subject: group.Key,
                caveats: ["Values come from this browser session (lab data); real users' devices and networks differ. INP is estimated from the interactions observed by the profiler."],
                related: [worst]);
        }
    }
}
