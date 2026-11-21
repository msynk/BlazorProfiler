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


<a id="bp1008"></a>
### BP1008 — Components recreated instead of reused

Within one operation, at least `ComponentChurnThreshold` (10) instances of one component type were disposed and new
instances of the same type created. Blazor reuses a child when it appears at the same position, or with the same `@key`, in
its parent's next render; it recreates it when the key differs (object instances recreated by reloading data, values
generated while rendering) or when the markup around it was replaced (a list rendered only when not loading). Recreation
reruns `OnInitialized`, loses component state, and rebuilds DOM. The impact estimate multiplies the recreated instances by
the type's mean initialization and render time. Requires `ProfiledComponentBase` (disposal events). High confidence when it
happens in two or more operations. Demo: *Rows recreated*.
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

<a id="bp3007"></a>
### BP3007 — Request repeated after prerendering

A GET request made by a component's initialization while prerendering (no circuit) and again, identically, when the same
component type became interactive on a Blazor Server circuit within 30 seconds. High confidence; BP3001 does not report
these pairs again. Recommends `[PersistentState]` / `PersistentComponentState`, or turning prerendering off for the
component. WebAssembly interactivity is not covered (the second request runs in the browser runtime, profiled
separately). Demo: *Request patterns* (verified end to end on the prerendered server page).

## JavaScript interop

<a id="bp4001"></a>
### BP4001 — Excessive JavaScript interop calls

Operations with at least `ExcessiveInteropCallsPerOperation` (50) .NET-to-JavaScript calls, and sustained rates of
`ExcessiveInteropCallsPerSecond` (60). Calls from JavaScript to .NET are reported by BP4007. Demo: *Interop chatter*.

<a id="bp4002"></a>
### BP4002 — Batchable interop calls

The same JavaScript identifier called 10+ times in one operation. Demo: *Interop chatter*.

<a id="bp4003"></a>
### BP4003 — Large interop payloads

Calls whose argument size is at least `LargeInteropPayloadBytes` (64 KB). On Blazor Server, calls from .NET to JavaScript
made through `IProfiledJSRuntime` are measured: the UTF-8 size of the arguments as the framework serialized them for the
circuit (see `ProfilerServerOptions.MeasureCircuitMessages`); such findings have no estimation caveat. Other sizes are
estimates and require `Collectors.JSInteropPayloadEstimation`. Calls from JavaScript to .NET on Blazor Server are reported
from `LargeServerJSToDotNetPayloadBytes` (24 KB, 75% of the hub's default `MaximumReceiveMessageSize`), because a larger
message closes the circuit: Medium severity, High from 32 KB. Their sizes are estimated in the browser with
`JSON.stringify` (byte arrays, which Blazor sends in binary, are overestimated). Demo: *Interop chatter* (one call with
20,000 points).

<a id="bp4004"></a>
### BP4004 — Slow interop calls

Identifiers whose p95 is at least `SlowInteropMs` (100), over the calls that JavaScript answered (a result or a
`JSException`). Calls without a response are left to BP4006, because their duration is the caller's wait (for example the
one-minute interop timeout), not JavaScript time. On the server the time includes the network round trip. Calls from
JavaScript to .NET are reported too, timed in the browser until .NET returned (on the server including the round trip
and the wait for the circuit's dispatcher). Demo: *Interop chatter* (slow JavaScript).

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

<a id="bp4007"></a>
### BP4007 — Frequent JavaScript-to-.NET calls

Calls from JavaScript into .NET (`DotNetObjectReference.invokeMethodAsync`/`invokeMethod` and
`DotNet.invokeMethodAsync`/`invokeMethod`, recorded by the browser module) peaking at `FrequentServerJSToDotNetCallsPerSecond`
(20) per second on Blazor Server, where each call is a message over the circuit queued on its dispatcher, or
`FrequentJSToDotNetCallsPerSecond` (60) in WebAssembly. The evidence lists the most frequent methods with their own peaks.
Typical sources are unthrottled `scroll`, `pointermove`, `resize`, and `input` listeners. Recommends throttling in
JavaScript (once per animation frame), keeping high-frequency logic in JavaScript, and batching updates into one call.
Blazor's own plumbing (event dispatch, root components) is not counted; calls made by framework components such as
`Virtualize` and `InputFile` are. Demo: *Interop chatter* (pointer moves one by one, and the batched fix).

## Memory

<a id="bp5001"></a>
### BP5001 — High garbage-collection pressure

GC pause share at least `HighGcPauseRatio` (5%) or generation 2 collections at least `HighGen2CollectionsPerMinute` (6),
over at least 5 s of samples. Demo: *Allocation spike* → *Allocate large buffers* (large object heap churn).

<a id="bp5002"></a>
### BP5002 — Allocation spike

Runtime samples allocating at least `AllocationSpikeFactor` (4×) the session median and at least
`AllocationSpikeMinimumBytesPerSecond` (20 MB/s). Lists operations that overlapped in time — temporal correlation only;
allocations are process-wide. Demo: *Allocation spike*.

<a id="bp5003"></a>
### BP5003 — Memory growth across navigation

Requires navigation events, which only the interactive router reports (not the enhanced navigation of apps with
per-page interactivity).

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
When an instance is reported, the profiler inspects what the instance itself references for the usual retainers and
records them in `ComponentRetainedEvent.RetainedBy` ("held by"):

- an event, delegate field, or delegate collection of a service the component received (`[Inject]`,
  `[CascadingParameter]`, or a constructor parameter whose type is registered in dependency injection) that still holds
  one of the component's methods or a lambda capturing it, for example `AppState.OnChange → Counter.StateHasChanged`.
  Objects the component created itself are not inspected: a subscription to them is a cycle the GC reclaims;
- a static event or delegate in the component's assembly that holds the component (types with a static constructor are
  skipped, so the inspection never runs application code);
- a `DotNetObjectReference` the component created and did not dispose, as a *possible* retainer (the JavaScript runtime
  keeps it alive only once it was passed to JavaScript);
- a `System.Timers.Timer` the component owns that is still enabled; other timers it owns are listed as *possible*
  retainers, because whether they were disposed cannot be read through public APIs.

An instance with an identified (not merely possible) retainer is always treated as a likely leak, with High confidence, and the first retainer
becomes the finding's "where to look" hint. Other retaining paths (caches, closures stored elsewhere) are not found; use a
memory profiler or `dotnet-gcdump` for those. Demo: *Leaky component* (navigate away and back several times, then check
for leaks).

The evidence includes the **leak signature**: a hash of the component type and its retainers that is the same in every
session, process, and trace. The leaks report groups retained instances of all sessions by signature, so a leak that
occurs in many circuits is listed once with how many sessions, circuits, and instances it affected (Sessions view, `GET
/api/leaks`, the MCP tool `list_leaks`, and `blazor-profiler leaks` for trace files). An instance reported by two
overlapping sessions (process-wide and per circuit) counts once.

<a id="bp5005"></a>
### BP5005 — Allocation-heavy rendering

Instrumented component types whose renders allocate at least `RenderAllocationBytes` (32 KB) on average, or
`RenderAllocationTotalBytes` (64 MB) in total, over at least 10 measured renders. Allocations are measured per render on the
rendering thread (`GC.GetAllocatedBytesForCurrentThread` before and after `BuildRenderTree`; verified in WebAssembly as
well), so they are attributed exactly, unlike process-wide samples. The evidence includes the share of all process
allocations. BP5002 (allocation spikes) also lists the components whose renders allocated during each spike.

<a id="bp5006"></a>
### BP5006 — Frequent exceptions

Two or more runtime samples in which exceptions were thrown at `ExceptionsPerSecond` (10) or more, or one sample at five
times that rate (a single burst at startup is common and not reported). Exceptions are counted first-chance
(`AppDomain.FirstChanceException`), so exceptions that are caught and ignored count too, with their type names (never
messages). The summary adds advice for the most frequent type (cancellations from fast input or disposal,
`JSDisconnectedException` after the circuit dropped, `NavigationException` during static rendering, `HttpRequestException`,
parsing), taken from the busy samples rather than the whole session, and lists the operations running at the time
(temporal).

<a id="bp5007"></a>
### BP5007 — Active timers keep growing

Timers that components start and never dispose keep running and keep their targets alive. The rule fits
`Timer.ActiveCount` two ways and reports growth of `TimerGrowthPerNavigation` (0.9) or more per step:

- per navigation of the interactive router (at least `MemoryGrowthMinimumNavigations`, 5; R² ≥ 0.6);
- per new instance of each component type (5 to 2,000 instances; R² ≥ 0.8), which also names the likely owner. This is
  what works in Blazor Web Apps with per-page interactivity, whose enhanced navigation reports no navigation events
  (verified end to end with the sample app, where it names `LeakyComponent`).

Navigations and instances that happened while their circuit was starting (a page load) are left out, because every new
circuit adds timers of its own. Component types created together correlate equally; up to three are listed as
candidates (inferred), and confidence is Medium on Blazor Server or when several types match. Demo: *Leaky component*
(navigate away and back).

## Blazor Server

<a id="bp6001"></a>
### BP6001 — Slow server render round trip

Circuit batches whose p95 round trip (diff + transport + browser apply) is at least `SlowServerRenderMs` (250). When even
small batches are slow, the latency is attributed to the network (Medium confidence); otherwise Low. Demo: *Slow
handlers* (server) with the *Simulate latency* command at 300 ms.

<a id="bp6002"></a>
### BP6002 — Circuit connection instability

Circuits with at least `CircuitReconnectThreshold` (3) connection drops. Reproduced by the end-to-end tests, which close
the page's WebSocket three times; in a browser, switching the network off and on (DevTools → Network → Offline) for longer
than the server's keep-alive timeout does the same.

<a id="bp6003"></a>
### BP6003 — Slow circuit activity

Inbound circuit activities at or above `SlowCircuitActivityMs` (250), by hub method. In .NET 10 browser events arrive as
`BeginInvokeDotNetFromJS`. Activities include awaited work, so a slow activity is not necessarily blocking. Demo: *Slow
handlers* (server; *Compute (blocking)* takes 250 ms, more with simulated latency).

<a id="bp6004"></a>
### BP6004 — High render batch volume

Circuits sending at least `HighBatchRatePerSecond` (30) batches per second for 3+ seconds. When batch sizes are measured
(`ProfilerServerOptions.MeasureCircuitMessages`), the evidence includes the peak bytes per second. Demo: *Render storm*
(server).

<a id="bp6005"></a>
### BP6005 — Server thread-pool saturation

Two or more runtime samples with a thread-pool queue of at least `ThreadPoolQueueSaturation` (100); overlapping slow
operations are listed as temporal correlation. Demo: *Thread-pool saturation* (server).

<a id="bp6006"></a>
### BP6006 — Large circuit messages

Uses the measured size of circuit messages (`ProfilerServerOptions.MeasureCircuitMessages`, on by default), read from
Blazor's SignalR hub protocol. Reports, per circuit:

- Render batches of at least `LargeCircuitRenderBatchBytes` (64 KB) on the wire. Severity scales with the largest batch
  and is capped at High. Recommends `Virtualize` or paging for long lists, avoiding re-renders of unchanged subtrees, and
  serving large content from URLs.
- Messages from the browser of at least `LargeCircuitReceivedMessageBytes` (24 KB, 75% of the hub's default
  `MaximumReceiveMessageSize`), by hub method: Medium, High from 32 KB (only possible when the limit was raised). A
  larger message closes the circuit. The recommendation depends on the hub method: UI events and calls from JavaScript
  (`BeginInvokeDotNetFromJS`, `DispatchBrowserEvent`), or results of calls to JavaScript (`EndInvokeJSFromDotNet`). An
  input bound with `@bind` sends its whole value twice per event (as the event value and as the bound field value), so
  about 16,000 characters of plain text reach the default limit.

Chunks of streams from JavaScript (`ReceiveJSDataChunk`), which the framework sizes just below the limit, are not
reported: streaming is the remedy. Messages above the limit close the circuit before they are processed and are not
recorded; repeated drops are reported by BP6002. Demo: *Large list* (server: sort, and paste a long text into the notes box).

## Browser

<a id="bp7001"></a>
### BP7001 — Slow user interaction

Event Timing interactions at or above `SlowInteractionMs` (200), with the dominant phase (input delay, processing,
presentation). On Blazor Server the .NET handler runs after the browser's processing phase and is not part of this
measurement. Demo: *Slow handlers* (WebAssembly).

<a id="bp7002"></a>
### BP7002 — Main thread blocked by long tasks

Three or more long animation frames / long tasks at or above `LongTaskMs` (150). With the Long Animation Frames API
(Chromium) the evidence lists the scripts that ran longest (function and file, invoker type) and the share of script time
spent in Blazor or the .NET WebAssembly runtime, which is where .NET handlers and rendering appear. Demo: *Layout
thrashing* (*Block the main thread*, three times).

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

<a id="bp7006"></a>
### BP7006 — Forced synchronous layout

Long animation frames whose scripts forced at least `ForcedLayoutMs` (30) of synchronous style and layout
(`forcedStyleAndLayoutDuration`): DOM writes followed by geometry reads (`offsetHeight`, `getBoundingClientRect`), usually
in JavaScript called through interop after a render batch, or measurements in `OnAfterRenderAsync`. The finding names the
application script that caused it (High confidence) when that script forced at least as much layout as Blazor's own entry
points. When the browser attributes the time to Blazor's own entry point
(which happens whenever .NET calls JavaScript synchronously from an event handler, as in WebAssembly), the finding names
the JS interop call that ran during the frame instead (Medium confidence, temporal). Requires Chromium. Demo: *Layout
thrashing*.

<a id="bp7007"></a>
### BP7007 — Rage clicks or dead clicks

Frustration signals detected by the browser module: **rage clicks** (3 or more clicks within one second on the same spot;
keyboard activations are not counted) and **dead clicks** (a click on a button, link, or other interactive element after which the DOM and URL did not change
within one second). Signals are grouped by element (tag, id, classes; never text). A rage click is Medium severity, High
when a slow handler or a long frame ran at that moment; dead clicks are reported from `FrustrationThreshold` (2) per
element at Low severity. The summary names the .NET work running within one second: a slow handler without feedback, a
blocked main thread, requests in flight, or no handler at all (the element has no `@onclick`, or the component was not
interactive yet). Exclude elements that intentionally change nothing with `Collectors.FrustrationIgnoreSelector`; turn
detection off with `Collectors.FrustrationSignals = false`. Demo: *Rage and dead clicks*.

<a id="bp7008"></a>
### BP7008 — Interaction results appear late

Interactions whose changed content painted at least `SlowInteractionMs` (200) after the input and at least twice as late as
the first paint (Chromium's `interaction-contentful-paint`). INP ends at the first paint after the event handlers, so an
asynchronous handler that renders a busy state and then the result looks fast to INP while users wait; this rule measures
the wait. The summary names the .NET handlers, HTTP requests, and database commands that ran meanwhile (temporal).
Verified behavior (Chromium 156, end to end): in WebAssembly the browser attributes content painted after awaited
JavaScript interop calls to the interaction; content painted after an `HttpClient` response is never attributed, and after
.NET timers (`Task.Delay`) only sometimes, because the runtime resumes those continuations from its own (shared) timer or
fetch callbacks. Blazor Server reports nothing, because updates arrive over the circuit. BP2001 and BP6001 cover those
cases. Demo: *Interop chatter* → *Slow JavaScript* in WebAssembly.

<a id="bp7009"></a>
### BP7009 — Excessive DOM size

The number of elements in the document (sampled with every browser batch and reported when it changes by 10% or more)
reached `DomElementsWarning` (800, Low severity) or `LargeDomElements` (1,400, scaled from Medium), Lighthouse's thresholds.
The evidence lists component types created in large numbers, the usual source of large Blazor DOMs. Recommends
`<Virtualize>`, paging, rendering hidden sections with `@if` instead of hiding them with CSS, and flatter row markup.
Demo: *Large list*.

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
