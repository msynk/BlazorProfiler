# Security and privacy

## Defaults

- **Off outside Development.** `AddBlazorProfiler` enables the profiler only when the environment is Development, unless
  `ProfilerOptions.Enabled` is set explicitly. When disabled, no interception points are registered (no activator decoration,
  no HTTP handler, no listeners) and `MapBlazorProfiler` maps nothing.
- **No remote access without authorization.** In Development, requests from the local machine get full access unless an
  `AuthorizationPolicy` is configured. In every other environment, access requires the configured policy; if none is
  configured, the API is unreachable (the app logs a warning at startup). Unauthorized callers receive `404`, so the
  endpoints are not discoverable.
- **Cross-site request forgery.** Every state-changing request (start, pause, clear, remove, import, suppress) must carry the
  `X-Blazor-Profiler` header, which cross-site forms cannot add. Responses are `Cache-Control: no-store` and `nosniff`.

> **Warning:** the local-machine check uses the connection's remote address. Behind a reverse proxy on the same machine all
> requests may look local. This exemption only applies in Development; configure `AuthorizationPolicy` for any shared
> environment.

## Access levels and isolation

| Access | Granted by | Sees | Can |
|---|---|---|---|
| Full | `AuthorizationPolicy`, or local requests in Development | All sessions, all circuits, process-wide health and runtime | Everything, including import |
| Own circuits | `OwnCircuitPolicy` | Only sessions restricted to circuits the same user opened; process health without counters | Start sessions for own circuits, control them, suppress findings |
| None | — | Nothing (404) | Nothing |

Circuit ownership is determined when the circuit opens, from the circuit's `AuthenticationStateProvider` (name identifier
claim, else name). Anonymous circuits are visible only with full access. Owner keys are kept in memory for authorization and
are never written to events or traces. Sessions restricted to a circuit never receive events from other circuits; this is
enforced in the session itself and covered by tests.

The WebAssembly bridge uses a same-origin `BroadcastChannel`: only pages from the same origin as the app can query it, and
the data never leaves the browser unless the user exports it.

## What is never collected

- Request and response bodies, headers (other than `Content-Length`), cookies, authorization data.
- JS interop arguments and return values (only argument counts, and optional size estimates).
- Parameter values (only names, and whether values changed).
- Element content, input values, or text typed by users. Browser interaction entries record the event type and the target's
  tag name only.
- Exception messages or stack traces (only exception type names).
- Render output (identical-output detection hashes it in memory; the hash is process-local and not exported as content).

## Redaction

URLs are sanitized before storage: user information and fragments are removed, query values are replaced (keys are kept;
sensitive-looking keys are always redacted), and identifier-like path segments are templated. A salted, process-local hash
of the original URL is kept for duplicate detection; `string.GetHashCode` is randomized per process, so the hash cannot be
reversed or correlated across processes. Export can additionally pseudonymize circuit ids and remove trace ids
(`TraceExportOptions`).

## Resource limits (denial-of-service protection)

- Bounded queue (events beyond capacity are dropped and counted, never buffered without limit).
- Per-session limits on events, memory, and age; bounded aggregation keys and tracked instances.
- Bounded number of sessions.
- Browser batches are limited to 1,000 entries and 512 KB per call.
- Trace import: input size, decompressed size (gzip bomb protection), event count, string length, JSON depth; only the 20
  known event types are accepted; no type names from the input are resolved and no code is executed.

## Production use

For controlled production diagnostics: set `Enabled = true`, choose `ProfilerProfile.LowOverhead`, configure
`AuthorizationPolicy` (and optionally `OwnCircuitPolicy`), keep `AutoStartSession` off if you only want to record on demand,
and review retention limits. Exported traces may contain sanitized URLs, component and method names, and circuit ids; treat
them as internal diagnostics data.
