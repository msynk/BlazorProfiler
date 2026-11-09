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

> **Warning:** a request only counts as local when all of the following hold:
>
> - it arrives over a loopback connection (or from the address the connection arrived on);
> - it addresses the app by a local host name (`localhost`, the machine name or its fully qualified name, a loopback or
>   local address);
> - it does not use HTTP/1.0, which browsers never send but nginx's default `proxy_pass` does;
> - every client address reported in forwarding headers (`Forwarded`, `X-Forwarded-For`, `X-Original-For`, `X-Real-IP`,
>   `X-Cluster-Client-IP`, and similar) is this machine, every reported host is a local name, and a relayed request
>   (`Via`, `X-Forwarded-Server`, `X-Forwarded-Proto`, ...) reports its client at all. Local hops such as IIS or IIS
>   Express out-of-process hosting therefore still qualify;
> - when sent by a browser, its `Origin` and (for same-origin requests) `Referer` name a local host.
>
> This rejects remote clients relayed by Dev Tunnels, ngrok, and reverse proxies that report the client, and remote
> browsers behind any relay. It cannot reject a non-browser client behind a relay that speaks HTTP/1.1, adds no forwarding
> headers, and rewrites `Host` to a local name (for example nginx with `proxy_http_version 1.1` and no
> `proxy_set_header` lines): such a request is indistinguishable from a local one. The exemption only applies in
> Development; never expose a Development app through such a relay, and configure `AuthorizationPolicy` whenever the app is
> reachable from other machines.

## Access levels and isolation

| Access | Granted by | Sees | Can |
|---|---|---|---|
| Full | `AuthorizationPolicy`, or local requests in Development | All sessions, all circuits, process-wide health and runtime | Everything, including import |
| Own circuits | `OwnCircuitPolicy` | Only sessions restricted to circuits the same user opened (including retained sessions of their closed circuits); process health without counters | Start sessions for own circuits and control them. Not import, and not suppressions, which apply to every user |
| None | — | Nothing (404) | Nothing |

Circuit ownership is determined when the circuit opens, from the circuit's `AuthenticationStateProvider` (name identifier
claim, else name). Anonymous circuits are visible only with full access. Owner keys are kept in memory for authorization and
are never written to events or traces. Sessions restricted to a circuit never receive events from other circuits; this is
enforced in the session itself and covered by tests.

The WebAssembly bridge uses a same-origin `BroadcastChannel`: only pages from the same origin as the app can query it, and
the data never leaves the browser unless the user exports it. On Blazor Server the bridge is off by default; when enabled
(`EnableDashboardBridge="true"`), each request is authorized for the circuit's user with `AuthorizationPolicy` and
`OwnCircuitPolicy`, exactly like the HTTP API, except that the Development loopback grant never applies (a circuit cannot
prove where a request comes from). Anonymous users get no access through the bridge. Access is checked before a streamed
request (a trace import larger than one circuit message) is read, and the request's work runs off the circuit's dispatcher.

The dashboard only loads data from its own origin (`?api=` must be a same-origin path) and only renders `http`/`https` links
and same-origin paths from findings, so a crafted link or trace file cannot inject script links into the app's origin.
Recommendation providers may link to absolute `http`/`https` URLs or same-origin paths; any other link is replaced with the
built-in rule reference.

## What is never collected

- Request and response bodies, headers (other than `Content-Length`), cookies, authorization data.
- JS interop arguments and return values (only argument counts, and optional size estimates).
- Parameter values (only names, and whether values changed).
- Element content, input values, or text typed by users. Browser interaction entries record the event type and the target's
  tag name only.
- Exception messages or stack traces (only exception type names).
- Render output (identical-output detection hashes it in memory; the hash is process-local and not exported as content).
- Database parameter values and literals. SQL text is normalized before it leaves the collector (string and numeric
  literals become `?`); argument values are reduced to a 64-bit hash used only to recognize identical queries.
- Page content in the in-page toolbar and update highlighting: they read element geometry and mutation counts only, run
  entirely in the browser, and send nothing to the server.

## Redaction

URLs are sanitized before storage: user information and fragments are removed, query values are replaced (keys are kept;
sensitive-looking keys are always redacted), and identifier-like path segments are templated. A salted, process-local hash
of the original URL is kept for duplicate detection; `string.GetHashCode` is randomized per process, so the hash cannot be
reversed or correlated across processes. Export can additionally pseudonymize circuit ids and remove trace ids
(`TraceExportOptions`).

## Commands and development-only features

- Commands (`POST /api/commands/{name}`, bridge `command`) require control access and the CSRF header like every other
  mutation; process-wide commands (`check-leaks`, `simulate-latency`) also require access to all circuits, so per-user
  (`OwnCircuitPolicy`) callers cannot run them and do not see them listed.
- `check-leaks` forces full blocking garbage collections, which pause the whole process briefly.
- Latency simulation is never active in the Production environment, whatever the configuration or command says.
- DevTools tracks on Blazor Server send only the current circuit's events to that circuit's browser; when the circuit
  cannot be determined, nothing is sent.
- Retention tracking holds weak references only; it never keeps a component alive.

## Resource limits (denial-of-service protection)

- Bounded queue (events beyond capacity are dropped and counted, never buffered without limit).
- Per-session limits on events, memory, and age; bounded aggregation keys and tracked instances.
- Bounded number of sessions.
- Browser batches are limited to 1,000 entries and 512 KB per call; larger batches are discarded and counted as collector
  errors. On Blazor Server the browser module keeps each batch under 16,000 UTF-8 bytes (measured as escaped for the
  interop call), so it stays within SignalR's default 32 KB message limit even when the JSON hub protocol escapes it again.
  Dashboard bridge requests above that size (trace imports) are streamed in chunks instead of sent as one message, up to
  96 MB.
- Trace import: input size, decompressed size (gzip bomb protection), event count, string length, JSON depth; only the 24
  known event types are accepted; no type names from the input are resolved and no code is executed.

## Production use

For controlled production diagnostics: set `Enabled = true`, choose `ProfilerProfile.LowOverhead`, configure
`AuthorizationPolicy` (and optionally `OwnCircuitPolicy`), keep `AutoStartSession` off if you only want to record on demand,
and review retention limits. Exported traces may contain sanitized URLs, component and method names, and circuit ids; treat
them as internal diagnostics data.
