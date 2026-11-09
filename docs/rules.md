# Diagnostic rule reference

Rules are deterministic: the same session always yields the same findings. Each finding reports **severity** (how far the
measurement exceeds its threshold), **confidence** (how specific the evidence is), evidence values labelled
measured/estimated/inferred/unavailable, an impact estimate with its calculation method, a suggested fix, and the conditions
under which the advice may not apply. Thresholds are in `ProfilerOptions.Diagnostics`; any rule can be disabled or given a
fixed severity with `Diagnostics.Rules["BPxxxx"]`. Findings can be dismissed or marked expected in the dashboard; the
suppression key is `RuleId:Subject`, or `RuleId:*` for a whole rule.

Severity scale (value ÷ threshold): below 1× Info, 1–2× Low, 2–5× Medium, 5–20× High, 20× and above Critical. Rules whose
evidence is weak cap severity (noted below).

Sample demos (`demo/Server`) are listed for rules they reproduce.

## Rendering

<a id="bp1001"></a>
### BP1001 — High-frequency rendering

Component types that render (instrumented) or receive parameters (framework metrics, as a proxy) at least
`HighRenderRatePerSecond` (10) times per second, sustained for two seconds or peaking at three times the threshold, with at
least `MinimumRenderSamples` (30) observations. Confidence is **High** when most renders produced identical output,
**Medium** when most parameter updates carried no value changes, otherwise **Low** (severity capped at Medium) — animations,
timers, and streaming data render frequently by design. Demo: *Render storm*.

<a id="bp1002"></a>
### BP1002 — Large component subtree re-rendered

Render batches that diff at least `LargeBatchComponentCount` (50) components, grouped by the interaction whose execution
context produced them (High confidence) or uncorrelated (Medium). Fix by keeping state low in the tree, passing immutable
parameters, `ShouldRender`, `@key`, and `<Virtualize>`. Demo: *Large list*.

<a id="bp1003"></a>
### BP1003 — Re-renders caused by new parameter instances

Instrumented components where at least half of 20+ parameter updates had no value changes except new instances of
reference-type parameters (lambdas, `RenderFragment`, objects). Lists the parameter names. Demo: *Render storm* (rows get a
new lambda every render).

<a id="bp1004"></a>
### BP1004 — Renders without visible change

Instrumented components with `TrackRenderOutput` where at least 30% of 20+ compared renders produced identical output. The
impact is the share of `BuildRenderTree` time spent on identical renders (estimated; diffing is excluded). Demo: *Render storm*.

<a id="bp1005"></a>
### BP1005 — Slow lifecycle method

Synchronous lifecycle time or `BuildRenderTree` time at or above `SlowLifecycleMs` (50) for instrumented components (High
confidence: it blocks the dispatcher), and framework-measured `SetParametersAsync` durations at or above the threshold for
other components (Medium confidence, severity capped at Medium: the measurement includes awaited work). Demo: *Slow handlers*
(`SlowSection.OnParametersSet`).

<a id="bp1006"></a>
### BP1006 — Excessive component instances

Types with at least `ExcessiveComponentInstances` (500) instances created, or a fifth of that within one second. Demo:
*Large list*.

<a id="bp1007"></a>
### BP1007 — Slow render batches

Batches longer than `SlowLifecycleMs` that do not include a server round trip (WebAssembly and SSR). Impact is the time
beyond a 16.7 ms frame budget. Browser layout and paint are not included.

## Event handling

<a id="bp2001"></a>
### BP2001 — Slow event handler

Handlers with invocations at or above `SlowEventHandlerMs` (100). The explanation distinguishes synchronous blocking (High
confidence; requires an instrumented receiving component), time dominated by HTTP requests started in the same operation
(Medium), and other asynchronous time (Low unless the synchronous part was measured). Handlers at or above
`LongRunningHandlerMs` (1,000) are High severity. Handler duration is not user-perceived latency; see BP7001. Demo: *Slow handlers*.

<a id="bp2002"></a>
### BP2002 — Rapid event stream triggers rendering

Handlers invoked at least `RapidEventsPerSecond` (8) times in a second (20+ invocations), reporting render batches and
component diffs per event. Demo: *Search as you type*.

<a id="bp2003"></a>
### BP2003 — Event handler issues duplicate requests

Operations started by a handler in which the same URL (by hash) was requested more than once. Demo: *Request patterns*
(Reload fetches settings twice).

<a id="bp2004"></a>
### BP2004 — Excessive rendering after an event

Handlers whose operations average five or more render batches (a handler normally causes one or two).

## Network

<a id="bp3001"></a>
### BP3001 — Duplicate or overlapping requests

Identical GET/HEAD URLs (by hash of the original URL, including query values) repeated within
`DuplicateRequestWindowMs` (2,000), including requests issued while an identical one was in flight.

<a id="bp3002"></a>
### BP3002 — N+1 request pattern

At least `NPlusOneThreshold` (5) distinct URLs of one endpoint template within one operation (High confidence) or within one
second when no operation is known (Medium). Demo: *Request patterns* (each row loads its own details).

<a id="bp3003"></a>
### BP3003 — Sequential requests

Chains of `SequentialRequestThreshold` (3) or more non-overlapping requests to different endpoints in one operation, each
starting within 50 ms of the previous one ending. Low confidence (data dependencies are invisible); severity capped at
Medium. Impact is an optimistic bound (sequential time minus the longest request). Demo: *Request patterns*.

<a id="bp3004"></a>
### BP3004 — Slow endpoint

Endpoints whose p95 is at least `SlowRequestMs` (1,000) over 3+ requests, or a single request at three times the threshold.
Includes recorded trace ids for server-side follow-up. Demo: *Failing endpoint* (slow endpoint).

<a id="bp3005"></a>
### BP3005 — Repeated request failures

Endpoints with at least `RepeatedFailureThreshold` (3) failures (exceptions, cancellations, or status ≥ 400), with retry and
re-render counts. Demo: *Failing endpoint*.

<a id="bp3006"></a>
### BP3006 — Requests repeated during component initialization

Endpoints requested from lifecycle methods of 3+ instances of one component type (5+ requests), or 3+ times by one instance
from `OnParametersSet(Async)`. Requires instrumentation for origin attribution. Demo: *Request patterns*.

## JavaScript interop

<a id="bp4001"></a>
### BP4001 — Excessive JavaScript interop calls

Operations with at least `ExcessiveInteropCallsPerOperation` (50) calls, and sustained rates of
`ExcessiveInteropCallsPerSecond` (60). Demo: *Interop chatter*.

<a id="bp4002"></a>
### BP4002 — Batchable interop calls

The same identifier called 10+ times in one operation. Demo: *Interop chatter*.

<a id="bp4003"></a>
### BP4003 — Large interop payloads

Calls whose estimated argument size is at least `LargeInteropPayloadBytes` (64 KB). Requires
`Collectors.JSInteropPayloadEstimation`; sizes are estimates. Demo: *Interop chatter* (one call with 20,000 points).

<a id="bp4004"></a>
### BP4004 — Slow interop calls

Identifiers whose p95 is at least `SlowInteropMs` (100), over the calls that JavaScript answered (a result or a
`JSException`). Calls without a response are left to BP4006, because their duration is the caller's wait (for example the
one-minute interop timeout), not JavaScript time. On the server the time includes the network round trip. Demo: *Interop
chatter* (slow JavaScript).

<a id="bp4005"></a>
### BP4005 — Interop after component disposal

Calls attributed to an instrumented component instance that started after its disposal. Demo: *Leaky component*.

<a id="bp4006"></a>
### BP4006 — Interop calls without a response

Identifiers with at least `InteropWithoutResponseThreshold` (3) .NET-to-JavaScript calls that ended without a response:
canceled or timed out (`TaskCanceledException`), failed with `JSDisconnectedException` (the circuit's browser was gone), or
failed with `InvalidOperationException` (interop unavailable, for example from a prerendered instance). On Blazor Server,
calls made while the circuit is disconnected never reach the browser and wait for the interop timeout; the finding counts
the timeouts that started while the circuit was disconnected or closed. Severity capped at High. Timeouts and deliberate
cancellations cannot be told apart. Demo: *Leaky component* (leave the page or close the tab).

## Memory

<a id="bp5001"></a>
### BP5001 — High garbage-collection pressure

GC pause share at least `HighGcPauseRatio` (5%) or generation 2 collections at least `HighGen2CollectionsPerMinute` (6),
over at least 5 s of samples.

<a id="bp5002"></a>
### BP5002 — Allocation spike

Runtime samples allocating at least `AllocationSpikeFactor` (4×) the session median and at least
`AllocationSpikeMinimumBytesPerSecond` (20 MB/s). Lists operations that overlapped in time — temporal correlation only;
allocations are process-wide. Demo: *Allocation spike*.

<a id="bp5003"></a>
### BP5003 — Memory growth across navigation

A least-squares trend of heap size sampled after each navigation (≥ `MemoryGrowthMinimumNavigations`, 5) with slope at least
`MemoryGrowthBytesPerNavigation` (512 KB) and R² ≥ 0.6. Confidence is Low without generation 2 collections in the window,
Medium with them. Demo: *Leaky component* (navigate back and forth).

<a id="bp5004"></a>
### BP5004 — Components retained after disposal

Component instances still reachable after `Collectors.RetentionGcCycles` (2) full garbage collections and at least
`Collectors.RetentionMinimumAge` (2 s) after they were released: disposed `ProfiledComponentBase` components, and every
component of a closed Blazor Server circuit or a finished static rendering request (the component activator watches its
scope). Detection waits for collections that happen anyway; the **Check for leaks** command (Components view, or
`POST /api/commands/check-leaks`) forces full collections and checks immediately.

Blazor's renderer keeps the previous render tree of every live component, and that buffer can still reference the child
most recently removed from a position until the position renders again. That retention is bounded, so the rule reports a
**likely leak** (severity by count, High confidence after a forced check) only when retention accumulates: two or more
retained instances of one type under the same parent, a retained child whose parent is gone, or components of an ended
scope. A single retained child of a live parent is reported at Info severity with Low confidence; repeat the action and
check again to tell the two apart. The retaining path is not determined (use a memory profiler or `dotnet-gcdump`).
Demo: *Leaky component* (navigate away and back several times, then check for leaks).

## Blazor Server

<a id="bp6001"></a>
### BP6001 — Slow server render round trip

Circuit batches whose p95 round trip (diff + transport + browser apply) is at least `SlowServerRenderMs` (250). When even
small batches are slow, the latency is attributed to the network (Medium confidence); otherwise Low.

<a id="bp6002"></a>
### BP6002 — Circuit connection instability

Circuits with at least `CircuitReconnectThreshold` (3) connection drops.

<a id="bp6003"></a>
### BP6003 — Slow circuit activity

Inbound circuit activities at or above `SlowCircuitActivityMs` (250), by hub method. In .NET 10 browser events arrive as
`BeginInvokeDotNetFromJS`. Activities include awaited work, so a slow activity is not necessarily blocking.

<a id="bp6004"></a>
### BP6004 — High render batch volume

Circuits sending at least `HighBatchRatePerSecond` (30) batches per second for 3+ seconds. Demo: *Render storm* (server).

<a id="bp6005"></a>
### BP6005 — Server thread-pool saturation

Two or more runtime samples with a thread-pool queue of at least `ThreadPoolQueueSaturation` (100); overlapping slow
operations are listed as temporal correlation.

## Browser

<a id="bp7001"></a>
### BP7001 — Slow user interaction

Event Timing interactions at or above `SlowInteractionMs` (200), with the dominant phase (input delay, processing,
presentation). On Blazor Server the .NET handler runs after the browser's processing phase and is not part of this
measurement. Demo: *Slow handlers* (WebAssembly).

<a id="bp7002"></a>
### BP7002 — Main thread blocked by long tasks

Three or more long animation frames / long tasks at or above `LongTaskMs` (150).

<a id="bp7003"></a>
### BP7003 — Large framework download

Page loads that transferred at least `LargeFrameworkDownloadBytes` (5 MB) of `_framework` files or spent at least
`SlowStartupMs` (3,000) downloading them, and large framework files served without compression. Evidence lists the
largest files. Development builds are neither trimmed nor compressed: judge a Release publish. Requires
`BlazorProfilerClient` (startup is reported once per page load).

<a id="bp7004"></a>
### BP7004 — Slow interactivity after first paint

At least `InteractivityGapMs` (2,000) between First Contentful Paint and the moment the page became interactive: users see
(often prerendered) content that ignores their input. Interactivity is estimated from the first interactive render of
`BlazorProfilerClient` (Medium confidence); render it early in the layout.

<a id="bp7005"></a>
### BP7005 — Core Web Vitals need improvement

Largest Contentful Paint, Cumulative Layout Shift (web-vitals session windows), and estimated Interaction to Next Paint
measured in the browser above the "good" thresholds (2.5 s, 0.1, 200 ms). "Needs improvement" is Low severity; "poor" is
scaled. The advice is specific to Blazor (prerender the LCP content, keep prerendered and interactive layouts identical,
reserve space for data loaded in `OnInitializedAsync`). Values are lab data from the profiling browser.

## Database

Database rules use commands captured from Entity Framework Core's `DiagnosticListener` (`Collectors.Database`). Command
text is normalized (literals replaced with `?`); argument values are compared by hash and never stored.

<a id="bp8001"></a>
### BP8001 — N+1 database queries

The same query shape executed at least `NPlusOneQueryThreshold` (5) times with different arguments within one operation
(High confidence), or by that many instances of one component type within two seconds outside any operation (Medium; the
typical "each row loads its own data in `OnInitializedAsync`" pattern, where the fix is to load in the parent). Impact is
an optimistic bound: total time minus the slowest execution.

<a id="bp8002"></a>
### BP8002 — Duplicate database queries

Identical queries (same text and argument hash) repeated within one operation or within `DuplicateRequestWindowMs`
(writes excluded), and the **prerender double fetch**: a component that ran the same query while prerendering (no
circuit) and again after it became interactive. The fix for the latter is `[PersistentState]` /
`PersistentComponentState`.

<a id="bp8003"></a>
### BP8003 — Slow database query

Query shapes whose p95 (including reading the rows) is at least `SlowQueryMs` (100) over 3+ executions, or a single
execution at three times the threshold. Advice changes when many rows were read (paging, projection, `<Virtualize>`).

<a id="bp8004"></a>
### BP8004 — Synchronous database call blocks the renderer

Commands executed synchronously (`ToList`, `First`, `SaveChanges`) on a renderer's dispatcher: the circuit cannot
process events or render until the database answers. High confidence.

<a id="bp8005"></a>
### BP8005 — DbContext shared across interactions

One `DbContext` instance used by three or more operations over at least 30 seconds in a circuit: a scoped context lives
as long as the circuit. Recommends `IDbContextFactory<TContext>` and short-lived contexts. Severity capped at Medium.

## Writing a rule

```csharp
public sealed class MyRule : DiagnosticRule
{
    public override DiagnosticRuleDescriptor Descriptor { get; } = new()
    {
        Id = "ACME001", Title = "...", Category = DiagnosticCategory.Network, Description = "...",
    };

    public override IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
    {
        foreach (var request in context.OfType<HttpRequestEvent>().Where(r => r.Url!.Contains("/legacy/")))
        {
            yield return Create("Legacy endpoint used", DiagnosticSeverity.Low, DiagnosticConfidence.High,
                summary: "...", explanation: "...", recommendation: "...",
                evidence: [Evidence("URL", request.Url!)], related: [request], subject: request.Endpoint);
        }
    }
}

builder.AddBlazorProfiler().AddRule<MyRule>();
```
