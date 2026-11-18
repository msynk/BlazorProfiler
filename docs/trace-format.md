# Trace format (version 1, event schema 3)

A trace is a JSON document, stored either plainly (`.bptrace.json`) or gzip-compressed (`.bptrace`, recommended). Readers
detect compression by the gzip magic bytes (`1F 8B`). Property names are camelCase; enums are strings; `null` properties are
omitted.

```json
{
  "format": "blazor-profiler-trace",
  "formatVersion": 1,
  "eventSchemaVersion": 3,
  "metadata": { ... },
  "clock": { "frequency": 10000000, "anchorTimestamp": 26007650839, "anchorTime": "2026-11-04T10:04:47.1111266+00:00" },
  "collectors": [ ... ],
  "events": [ ... ],
  "summary": { ... },
  "findings": [ ... ]
}
```

## Header

| Property | Description |
|---|---|
| `format` | Must be `blazor-profiler-trace`. |
| `formatVersion` | Document layout version. Readers reject versions they do not know. |
| `eventSchemaVersion` | Event model version (`ProfilerEvent.SchemaVersion`). |
| `metadata.appName`, `appVersion`, `buildConfiguration`, `operatingSystem`, `processorCount` | The profiled application's entry assembly, informational version, `Debug`/`Release` build, OS, and processors (optional; kept when an imported trace is exported again). `blazor-profiler compare` warns when builds, hosting models, runtimes, or processor counts differ. |
| `metadata` | `sessionId`, `sessionName`, `circuitId` (if the session was circuit-scoped), `createdAt`, `exportedAt`, `durationMs`, `source` (`server`/`webassembly`), `runtime`, `profilerVersion`, `profile`, `samplingRates` (kind → rate; absent kinds were not sampled), `droppedEvents`, `evictedEvents`, `imported`. |
| `clock` | `frequency` (ticks per second), `anchorTimestamp` (monotonic ticks), `anchorTime` (wall clock at the anchor). |
| `collectors` | Collector descriptors and status at export time (capability, overhead, state, errors). |
| `summary` | Aggregates at export time, including events evicted before export. Optional. |
| `findings` | Diagnostic findings at export time. Optional. |
| `suppressions` | Suppressions at export time. Optional. |

## Time

Every event has `timestamp` (start, in monotonic clock ticks) and `duration` (ticks). Convert to wall-clock time with:

```
wallTime = anchorTime + (timestamp - anchorTimestamp) / frequency seconds
```

Timestamps are only comparable within one trace (one clock domain).

## Events

Every event has: `type` (discriminator), `id`, `timestamp`, `duration`, optional `circuitId`, `operationId`, `correlation`
(`None`, `Self`, `Explicit`, `ExecutionContext`, `Dispatcher`, `Temporal`), `parentId`, `outcome` (`Success`, `Error`,
`Canceled`, `Unknown`), optional `errorType`, `sampleWeight`, optional `traceId`/`spanId`.

| `type` | Additional properties |
|---|---|
| `marker` | `markerType` (`Bookmark`, `SessionPaused`, `SessionResumed`, `EventsDropped`, `CollectorFailed`, `Custom`), `name`, `count` |
| `componentCreated`, `componentDisposed` | `componentType`, `componentInstanceId`, `source`; disposed: `lifetimeTicks` |
| `parametersSet` | component fields, `changeState` (`Unknown`, `Initial`, `Changed`, `ChangedByReferenceOnly`, `Unchanged`), `parameterCount`, `changedParameters` (names only) |
| `componentRender` | component fields, `frameCount`, `outputUnchanged`, `reason`, `renderNumber`; schema 3: `allocatedBytes` |
| `renderRequest` | component fields, `result` (`Coalesced`, `SuppressedByShouldRender`) |
| `lifecycle` | component fields, `method`, `synchronousDuration`, `completedAsynchronously` |
| `componentChildren` | component fields, `childInstanceIds`, `childTypes` |
| `renderBatch` | `updatedComponentCount`, `includesClientRoundTrip`, `transportBytes` (Blazor Server: size of the message that carried the batch), `source` |
| `eventHandler` | component fields, `methodName`, `attributeName`, `synchronousDuration` |
| `navigation` | component fields, `route` (template) |
| `http` | `method`, `url` (sanitized), `endpoint`, `urlHash`, `statusCode`, `requestBytes`, `responseBytes`, `responseBytesQuality`, `fromCache`, `retryAttempt`, `source`, `originComponentType`, `originComponentInstanceId`, `originLifecycleMethod` |
| `jsInterop` | `direction`, `identifier`, `synchronous`, `viaObjectReference`, `argumentCount`, `payloadBytes`, `payloadBytesQuality`, `source`, `originComponentType`, `originComponentInstanceId`. Calls from JavaScript to .NET (`direction` `JSToDotNet`, `source` `Browser`) use the method name as `identifier` for instance methods and `Method (Assembly)` for static ones; a failed call has `errorType` `Microsoft.JSInterop.DotNetInvocationFailure`. `payloadBytesQuality` is `Measured` for .NET-to-JavaScript calls on Blazor Server whose circuit message was observed, `Estimated` otherwise. `streamBytes`: bytes passed through stream references (`DotNetStreamReference` arguments over seekable streams, an `IJSStreamReference` result, `DotNet.createJSStreamReference` arguments to .NET). |
| `runtime` | `allocatedBytes`, `heapSizeBytes`, `totalMemoryBytes`, `gen0Collections`, `gen1Collections`, `gen2Collections`, `gcPauseTicks`, `cpuTicks`, `processorCount`, `workingSetBytes`, `threadPoolThreads`, `threadPoolQueueLength`, `lockContentions`, `exceptions` (unavailable values are omitted); schema 3: `exceptionTypes` (type name → count, at most 5), `activeTimers` |
| `circuit` | `change` (`Opened`, `ConnectionUp`, `ConnectionDown`, `Closed`) |
| `circuitActivity` | `activityName` (hub method), `receivedBytes` (size of the message from the browser) |
| `browserInteraction` | `eventType`, `targetTag`, `inputDelayMs`, `processingMs`, `presentationDelayMs`; schema 3: `contentPaintMs` (input to interaction contentful paint, when reported; attached within 5 s of the input), `contentPaintOnly` (recorded only because the content painted late; excluded from interaction statistics) |
| `browserLongTask` | `entryType`, `blockingMs`, `script`; schema 3: `forcedStyleAndLayoutMs`, `renderMs`, `styleAndLayoutMs`, `scripts` (at most 5: `invoker`, `invokerType`, `sourceUrl` (sanitized), `sourceFunctionName`, `sourceCharPosition`, `durationMs`, `forcedStyleAndLayoutMs`, `pauseMs`) |
| `browserResource` | `url`, `initiatorType`, `transferSize`, `decodedBodySize`, `fromCache`, `statusCode` |
| `operation` | `name`, `category`, `componentType` |
| `componentRetained` (schema 2) | component fields, `reason` (`Disposed`, `ScopeEnded`), `releasedTimestamp`, `survivedCollections`, `forcedCollection` |
| `dbCommand` (schema 2) | `commandText` (normalized: literals replaced with `?`), `commandHash`, `parameterHash` (hash of text and argument values; values are never stored), `parameterCount`, `executeMethod` (`Reader`, `NonQuery`, `Scalar`), `isAsync`, `rowsRead`, `contextType`, `contextInstanceId`, `provider`, `originComponentType`, `originComponentInstanceId`, `originLifecycleMethod`, `onRendererThread`, `source` |
| `browserStartup` (schema 2) | `navigationType`, `runtimeKind`, `timeToFirstByteMs`, `firstContentfulPaintMs`, `domContentLoadedMs`, `loadMs`, `interactiveMs`, `frameworkFiles`, `frameworkTransferBytes`, `frameworkDecodedBytes`, `frameworkCachedFiles`, `frameworkUncompressedFiles`, `frameworkDownloadMs`, `largestFiles` (`name`, `transferSize`, `decodedSize`, `durationMs`, `cached`); schema 3: `hardwareConcurrency`, `deviceMemoryGb`, `cpuPerformance`, `effectiveConnectionType` |
| `browserVital` (schema 2) | `name` (`LCP`, `CLS`, `INP`; schema 3: `DOM`, the number of elements), `value`, `targetTag`, `rating` (computed; ignored on import) |
| `browserFrustration` (schema 3) | `signal` (`RageClick`, `DeadClick`), `clickCount`, `targetTag`, `target` (tag, id, and up to two classes; never text) |

`componentRetained` gained `retainedBy` in schema 3: short descriptions of likely retainers (at most 16).

"component fields" = `componentType`, `componentInstanceId`, `source`.

## Validation on import

Importers must treat traces as untrusted. The reference importer (`TraceImporter`):

- limits input size (64 MB) and decompressed size (256 MB), event count (1,000,000), JSON depth (32);
- rejects unknown `format`, unsupported `formatVersion`/`eventSchemaVersion`, missing or non-positive clock frequency,
  unknown event types, negative durations, and invalid sample weights;
- truncates overlong strings (2,048 characters by default; shorter limits for names and identifiers);
- never resolves type names or executes anything from the input.

## Compatibility

New optional properties may be added without changing `formatVersion`; readers ignore unknown properties. Removing or
changing the meaning of a property, or adding an event type, increments `eventSchemaVersion` and is listed in
[migration notes](migration.md).

## Chrome Trace Event export

`ChromeTraceExporter` writes the Trace Event format (`traceEvents` array; complete `X` events, instant `i` events, counter
`C` events for runtime samples, `M` metadata naming processes per circuit/runtime and threads per category) for Perfetto or
`chrome://tracing`. It is lossy; use the native format for re-import.
