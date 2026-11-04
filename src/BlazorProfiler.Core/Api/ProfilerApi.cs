using System.Text.Json;
using BlazorProfiler.Analysis;
using BlazorProfiler.Collectors;
using BlazorProfiler.Events;
using BlazorProfiler.Export;
using BlazorProfiler.Sessions;
using BlazorProfiler.Storage;

namespace BlazorProfiler.Api;

/// <summary>Profiler information returned to dashboards.</summary>
public sealed class ProfilerInfo
{
    /// <summary>The runtime label (for example <c>server</c> or <c>webassembly</c>).</summary>
    public string? Source { get; set; }

    /// <summary>The profiler version.</summary>
    public string? Version { get; set; }

    /// <summary>The event schema version.</summary>
    public int EventSchemaVersion { get; set; } = ProfilerEvent.SchemaVersion;

    /// <summary>The clock frequency (ticks per second) of live sessions.</summary>
    public long ClockFrequency { get; set; }

    /// <summary>The monotonic clock anchor.</summary>
    public long ClockAnchorTimestamp { get; set; }

    /// <summary>The wall-clock anchor.</summary>
    public DateTimeOffset ClockAnchorTime { get; set; }

    /// <summary>The current monotonic timestamp.</summary>
    public long Now { get; set; }

    /// <summary>Health and overhead.</summary>
    public ProfilerHealthSnapshot? Health { get; set; }

    /// <summary>Sessions visible to the caller.</summary>
    public List<ProfilerSessionStatistics> Sessions { get; set; } = [];

    /// <summary>Registered diagnostic rules.</summary>
    public List<DiagnosticRuleDescriptor> Rules { get; set; } = [];

    /// <summary>Whether the caller may see process-wide information and all circuits.</summary>
    public bool CanViewAllCircuits { get; set; }
}

/// <summary>A page of events.</summary>
public sealed class EventPage
{
    /// <summary>The events.</summary>
    public List<ProfilerEvent> Events { get; set; } = [];

    /// <summary>The cursor to pass as <c>after</c> on the next request.</summary>
    public long Cursor { get; set; }

    /// <summary>
    /// Whether events between the requested cursor and the first returned event were evicted by retention limits, so the
    /// client should treat its view as incomplete.
    /// </summary>
    public bool Gap { get; set; }

    /// <summary>
    /// Whether the requested cursor predates a clear of the session (or was not issued by it). The client must discard the
    /// events it holds; the returned events start after the clear.
    /// </summary>
    public bool Reset { get; set; }

    /// <summary>Whether more events are available immediately.</summary>
    public bool HasMore { get; set; }
}

/// <summary>A request handled by <see cref="ProfilerApi.HandleAsync(ProfilerApiRequest, ProfilerApiAccess, CancellationToken)"/> (used by message-based transports).</summary>
public sealed class ProfilerApiRequest
{
    /// <summary>The operation: info, events, summary, findings, operation, control, start, remove, export, import, suppress, unsuppress.</summary>
    public string? Op { get; set; }

    /// <summary>The session identifier.</summary>
    public string? SessionId { get; set; }

    /// <summary>The event cursor.</summary>
    public long After { get; set; }

    /// <summary>The maximum number of events.</summary>
    public int Max { get; set; } = 2_000;

    /// <summary>The operation identifier.</summary>
    public long OperationId { get; set; }

    /// <summary>A control action (pause, resume, stop, clear, bookmark).</summary>
    public string? Action { get; set; }

    /// <summary>A name (bookmark or session name).</summary>
    public string? Name { get; set; }

    /// <summary>The export format (native, json, chrome).</summary>
    public string? Format { get; set; }

    /// <summary>Base64-encoded data (import).</summary>
    public string? Data { get; set; }

    /// <summary>A suppression fingerprint.</summary>
    public string? Fingerprint { get; set; }

    /// <summary>A suppression kind.</summary>
    public SuppressionKind? Kind { get; set; }

    /// <summary>A suppression note.</summary>
    public string? Note { get; set; }
}

/// <summary>A response produced by <see cref="ProfilerApi.HandleAsync(ProfilerApiRequest, ProfilerApiAccess, CancellationToken)"/>.</summary>
public sealed class ProfilerApiResponse
{
    /// <summary>Whether the request succeeded.</summary>
    public bool Ok { get; set; }

    /// <summary>An error message.</summary>
    public string? Error { get; set; }

    /// <summary>Profiler information.</summary>
    public ProfilerInfo? Info { get; set; }

    /// <summary>An event page.</summary>
    public EventPage? Page { get; set; }

    /// <summary>A summary.</summary>
    public SessionSummary? Summary { get; set; }

    /// <summary>A diagnostic report.</summary>
    public DiagnosticReport? Report { get; set; }

    /// <summary>Operation events.</summary>
    public List<ProfilerEvent>? Events { get; set; }

    /// <summary>Session statistics.</summary>
    public ProfilerSessionStatistics? Session { get; set; }

    /// <summary>Base64-encoded data (export).</summary>
    public string? Data { get; set; }

    /// <summary>A suggested file name (export).</summary>
    public string? FileName { get; set; }

    /// <summary>A content type (export).</summary>
    public string? ContentType { get; set; }
}

/// <summary>Thrown for invalid API requests.</summary>
public sealed class ProfilerApiException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ProfilerApiException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public ProfilerApiException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ProfilerApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The suggested HTTP status code.</summary>
    public int StatusCode { get; init; } = 400;
}

/// <summary>Access rules applied by <see cref="ProfilerApi"/> for one caller.</summary>
public sealed class ProfilerApiAccess
{
    /// <summary>Full access to every session and circuit.</summary>
    public static ProfilerApiAccess Full { get; } = new() { CanViewAllCircuits = true };

    /// <summary>Whether the caller may see every circuit and process-wide sessions.</summary>
    public bool CanViewAllCircuits { get; init; }

    /// <summary>Circuits the caller owns (visible even without <see cref="CanViewAllCircuits"/>).</summary>
    public IReadOnlySet<string> OwnedCircuits { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Whether the caller may modify sessions (start, stop, clear). Process-wide changes (process-wide sessions, import,
    /// suppressions) additionally require <see cref="CanViewAllCircuits"/>.
    /// </summary>
    public bool CanControl { get; init; } = true;

    /// <summary>Returns whether a session is visible to the caller.</summary>
    public bool CanView(IProfilerSession session) =>
        CanViewAllCircuits || (session.Options.CircuitId is { } circuit && OwnedCircuits.Contains(circuit));
}

/// <summary>
/// Transport-independent query and control API used by the HTTP endpoints (server) and the browser bridge (WebAssembly).
/// </summary>
public sealed class ProfilerApi
{
    /// <summary>The maximum events returned per page.</summary>
    public const int MaxPageSize = 10_000;

    private readonly IProfiler _profiler;
    private readonly string _source;
    private readonly TraceImportOptions _importOptions;

    /// <summary>Creates the API.</summary>
    public ProfilerApi(IProfiler profiler, string source, TraceImportOptions? importOptions = null)
    {
        _profiler = profiler;
        _source = source;
        _importOptions = importOptions ?? new TraceImportOptions();
    }

    /// <summary>The profiler.</summary>
    public IProfiler Profiler => _profiler;

    /// <summary>Returns profiler information.</summary>
    public ProfilerInfo GetInfo(ProfilerApiAccess access)
    {
        var clock = _profiler.Clock;
        return new ProfilerInfo
        {
            Source = _source,
            Version = TraceDocumentBuilder.ProfilerVersion,
            ClockFrequency = clock.Frequency,
            ClockAnchorTimestamp = clock.AnchorTimestamp,
            ClockAnchorTime = clock.AnchorTime,
            Now = clock.GetTimestamp(),
            Health = access.CanViewAllCircuits ? _profiler.GetHealth() : RestrictHealth(_profiler.GetHealth()),
            Sessions = [.. _profiler.GetSessions().Where(access.CanView).Select(s => s.GetStatistics())],
            Rules = [.. _profiler.Diagnostics.Rules.Select(r => r.Descriptor)],
            CanViewAllCircuits = access.CanViewAllCircuits,
        };
    }

    /// <summary>Returns events after a cursor.</summary>
    public EventPage GetEvents(ProfilerApiAccess access, string sessionId, long after, int max)
    {
        var session = GetSession(access, sessionId);
        max = Math.Clamp(max, 1, MaxPageSize);
        var events = session.GetEventsAfter(after, max + 1, out var cursor);
        var statistics = session.GetStatistics();
        var hasMore = events.Count > max;
        if (hasMore)
        {
            events.RemoveAt(events.Count - 1);
            cursor--;
        }

        // Sequence numbers are never reused, so a cursor below the last clear (or ahead of the session) means the caller's
        // data no longer exists. A fresh reader (cursor 0) has nothing to discard.
        var reset = after > statistics.LastSequence || (after > 0 && after < statistics.ClearedSequence);
        var effectiveAfter = reset || after < statistics.ClearedSequence ? statistics.ClearedSequence : after;
        var oldest = statistics.LastSequence - statistics.StoredEvents + 1;
        return new EventPage
        {
            Events = events,
            Cursor = cursor,
            Gap = effectiveAfter + 1 < oldest && statistics.StoredEvents > 0,
            Reset = reset,
            HasMore = hasMore,
        };
    }

    /// <summary>Returns a session summary.</summary>
    public SessionSummary GetSummary(ProfilerApiAccess access, string sessionId) => GetSession(access, sessionId).GetSummary();

    /// <summary>Runs diagnostics on a session.</summary>
    public DiagnosticReport GetFindings(ProfilerApiAccess access, string sessionId) => _profiler.Diagnostics.Analyze(GetSession(access, sessionId));

    /// <summary>Returns the retained events of one operation.</summary>
    public List<ProfilerEvent> GetOperation(ProfilerApiAccess access, string sessionId, long operationId) =>
        [.. GetSession(access, sessionId).GetOperationEvents(operationId)];

    /// <summary>Applies a control action.</summary>
    public ProfilerSessionStatistics Control(ProfilerApiAccess access, string sessionId, string? action, string? name)
    {
        RequireControl(access);
        var session = GetSession(access, sessionId);
        try
        {
            switch (action?.ToLowerInvariant())
            {
                case "pause":
                    session.Pause();
                    break;
                case "resume":
                    session.Resume();
                    break;
                case "stop":
                    session.Stop();
                    break;
                case "clear":
                    session.Clear();
                    break;
                case "bookmark":
                    session.AddBookmark(string.IsNullOrWhiteSpace(name) ? "Bookmark" : name);
                    break;
                default:
                    throw new ProfilerApiException($"Unknown action '{action}'.");
            }
        }
        catch (InvalidOperationException ex)
        {
            throw new ProfilerApiException(ex.Message) { StatusCode = 409 };
        }

        return session.GetStatistics();
    }

    /// <summary>Starts a new session.</summary>
    public ProfilerSessionStatistics StartSession(ProfilerApiAccess access, string? name, string? circuitId)
    {
        RequireControl(access);
        if (circuitId is null && !access.CanViewAllCircuits)
        {
            throw new ProfilerApiException("Only callers that may view all circuits can start process-wide sessions.") { StatusCode = 403 };
        }

        if (circuitId is not null && !access.CanViewAllCircuits && !access.OwnedCircuits.Contains(circuitId))
        {
            throw new ProfilerApiException("The circuit is not accessible.") { StatusCode = 404 };
        }

        try
        {
            var session = _profiler.StartSession(new ProfilerSessionOptions { Name = Truncate(name, 128), CircuitId = circuitId });
            return session.GetStatistics();
        }
        catch (InvalidOperationException ex)
        {
            throw new ProfilerApiException(ex.Message) { StatusCode = 409 };
        }
    }

    /// <summary>Removes a session.</summary>
    public void RemoveSession(ProfilerApiAccess access, string sessionId)
    {
        RequireControl(access);
        _ = GetSession(access, sessionId);
        _profiler.RemoveSession(sessionId);
    }

    /// <summary>Exports a session.</summary>
    public async Task<(byte[] Data, string FileName, string ContentType)> ExportAsync(ProfilerApiAccess access, string sessionId, string? format, CancellationToken cancellationToken = default)
    {
        var session = GetSession(access, sessionId);
        IProfilerExporter exporter = format?.ToLowerInvariant() switch
        {
            "json" => new JsonTraceExporter(),
            "chrome" => new ChromeTraceExporter(),
            null or "" or "native" => new CompressedTraceExporter(),
            _ => throw new ProfilerApiException($"Unknown export format '{format}'."),
        };

        var report = _profiler.Diagnostics.Analyze(session);
        var document = TraceDocumentBuilder.Create(
            session,
            _profiler.Options,
            report,
            access.CanViewAllCircuits ? _profiler.GetHealth() : null,
            _source);
        using var stream = new MemoryStream();
        await exporter.ExportAsync(document, stream, cancellationToken).ConfigureAwait(false);
        var safeName = new string(session.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return (stream.ToArray(), $"blazor-profile-{(safeName.Length == 0 ? "session" : safeName)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}{exporter.FileExtension}", exporter.ContentType);
    }

    /// <summary>Imports a trace as a read-only session.</summary>
    public async Task<ProfilerSessionStatistics> ImportAsync(ProfilerApiAccess access, Stream input, CancellationToken cancellationToken = default)
    {
        RequireControl(access);
        if (!access.CanViewAllCircuits)
        {
            throw new ProfilerApiException("Importing traces requires access to all circuits.") { StatusCode = 403 };
        }

        try
        {
            var document = await TraceImporter.ReadAsync(input, _importOptions, cancellationToken).ConfigureAwait(false);
            return _profiler.AddImportedSession(document).GetStatistics();
        }
        catch (TraceImportException ex)
        {
            throw new ProfilerApiException(ex.Message, ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new ProfilerApiException(ex.Message) { StatusCode = 409 };
        }
    }

    /// <summary>Adds a suppression.</summary>
    /// <remarks>
    /// Suppressions are process-wide (they apply to every session and every caller's reports), so only callers that may
    /// view all circuits can change them.
    /// </remarks>
    public void Suppress(ProfilerApiAccess access, string? fingerprint, SuppressionKind kind, string? note)
    {
        RequireSuppressionControl(access);
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length > 1024)
        {
            throw new ProfilerApiException("A valid fingerprint is required.");
        }

        _profiler.Diagnostics.Suppressions.Add(new FindingSuppression { Fingerprint = fingerprint, Kind = kind, Note = Truncate(note, 512), CreatedAt = DateTimeOffset.UtcNow });
    }

    /// <summary>Removes a suppression.</summary>
    /// <remarks>Like <see cref="Suppress"/>, requires access to all circuits.</remarks>
    public void Unsuppress(ProfilerApiAccess access, string? fingerprint)
    {
        RequireSuppressionControl(access);
        _profiler.Diagnostics.Suppressions.Remove(fingerprint ?? string.Empty);
    }

    /// <summary>Handles a serialized request (message-based transports such as the WebAssembly bridge).</summary>
    public async Task<string> HandleAsync(string requestJson, ProfilerApiAccess access, CancellationToken cancellationToken = default)
    {
        ProfilerApiResponse response;
        try
        {
            if (requestJson.Length > 96 * 1024 * 1024)
            {
                throw new ProfilerApiException("The request is too large.");
            }

            var request = JsonSerializer.Deserialize(requestJson, ProfilerJsonContext.Default.ProfilerApiRequest)
                ?? throw new ProfilerApiException("Empty request.");
            response = await HandleAsync(request, access, cancellationToken).ConfigureAwait(false);
        }
        catch (ProfilerApiException ex)
        {
            response = new ProfilerApiResponse { Ok = false, Error = ex.Message };
        }
        catch (JsonException)
        {
            response = new ProfilerApiResponse { Ok = false, Error = "Invalid request." };
        }

        return JsonSerializer.Serialize(response, ProfilerJsonContext.Default.ProfilerApiResponse);
    }

    /// <summary>Handles a request.</summary>
    public async Task<ProfilerApiResponse> HandleAsync(ProfilerApiRequest request, ProfilerApiAccess access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = request.SessionId ?? string.Empty;
        switch (request.Op?.ToLowerInvariant())
        {
            case "info":
                return new ProfilerApiResponse { Ok = true, Info = GetInfo(access) };
            case "events":
                return new ProfilerApiResponse { Ok = true, Page = GetEvents(access, id, request.After, request.Max) };
            case "summary":
                return new ProfilerApiResponse { Ok = true, Summary = GetSummary(access, id) };
            case "findings":
                return new ProfilerApiResponse { Ok = true, Report = GetFindings(access, id) };
            case "operation":
                return new ProfilerApiResponse { Ok = true, Events = GetOperation(access, id, request.OperationId) };
            case "control":
                return new ProfilerApiResponse { Ok = true, Session = Control(access, id, request.Action, request.Name) };
            case "start":
                return new ProfilerApiResponse { Ok = true, Session = StartSession(access, request.Name, null) };
            case "remove":
                RemoveSession(access, id);
                return new ProfilerApiResponse { Ok = true };
            case "export":
                {
                    var (data, fileName, contentType) = await ExportAsync(access, id, request.Format, cancellationToken).ConfigureAwait(false);
                    return new ProfilerApiResponse { Ok = true, Data = Convert.ToBase64String(data), FileName = fileName, ContentType = contentType };
                }

            case "import":
                {
                    byte[] bytes;
                    try
                    {
                        bytes = Convert.FromBase64String(request.Data ?? string.Empty);
                    }
                    catch (FormatException)
                    {
                        throw new ProfilerApiException("The import data is not valid base64.");
                    }

                    using var stream = new MemoryStream(bytes);
                    return new ProfilerApiResponse { Ok = true, Session = await ImportAsync(access, stream, cancellationToken).ConfigureAwait(false) };
                }

            case "suppress":
                Suppress(access, request.Fingerprint, request.Kind ?? SuppressionKind.Dismissed, request.Note);
                return new ProfilerApiResponse { Ok = true };
            case "unsuppress":
                Unsuppress(access, request.Fingerprint);
                return new ProfilerApiResponse { Ok = true };
            default:
                throw new ProfilerApiException($"Unknown operation '{request.Op}'.");
        }
    }

    private IProfilerSession GetSession(ProfilerApiAccess access, string sessionId)
    {
        var session = _profiler.GetSession(sessionId);
        if (session is null || !access.CanView(session))
        {
            throw new ProfilerApiException("Session not found.") { StatusCode = 404 };
        }

        return session;
    }

    private static void RequireControl(ProfilerApiAccess access)
    {
        if (!access.CanControl)
        {
            throw new ProfilerApiException("The caller is not allowed to control profiling.") { StatusCode = 403 };
        }
    }

    private static void RequireSuppressionControl(ProfilerApiAccess access)
    {
        RequireControl(access);
        if (!access.CanViewAllCircuits)
        {
            throw new ProfilerApiException("Suppressions apply to every user; changing them requires access to all circuits.") { StatusCode = 403 };
        }
    }

    private static ProfilerHealthSnapshot RestrictHealth(ProfilerHealthSnapshot health) => new()
    {
        Enabled = health.Enabled,
        Recording = health.Recording,
        Profile = health.Profile,
        Collectors = health.Collectors.Select(c => new CollectorStatus { Descriptor = c.Descriptor, State = c.State }).ToList(),
    };

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
