using BlazorProfiler.Events;
using BlazorProfiler.Storage;

namespace BlazorProfiler.Sessions;

/// <summary>The lifecycle state of a session.</summary>
public enum ProfilerSessionState
{
    /// <summary>Created but not started.</summary>
    Created = 0,
    /// <summary>Recording events.</summary>
    Recording = 1,
    /// <summary>Paused: events are not recorded until resumed.</summary>
    Paused = 2,
    /// <summary>Stopped: retained data can be read and exported but no new events are recorded.</summary>
    Stopped = 3,
    /// <summary>Disposed: all data has been released.</summary>
    Disposed = 4,
    /// <summary>Created from an imported trace; read-only.</summary>
    Imported = 5,
}

/// <summary>Options of a session.</summary>
public sealed class ProfilerSessionOptions
{
    /// <summary>A display name.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Restricts the session to events of one Blazor Server circuit. Events of other circuits are never added to the session.
    /// </summary>
    public string? CircuitId { get; set; }

    /// <summary>
    /// When <see cref="CircuitId"/> is set, also include process-wide runtime samples, which contain no per-user data.
    /// </summary>
    public bool IncludeProcessMetrics { get; set; } = true;

    /// <summary>An opaque key identifying the principal that owns the session; used by hosting integrations for authorization.</summary>
    public string? OwnerKey { get; set; }

    /// <summary>Restricts the session to these event kinds.</summary>
    public IReadOnlySet<ProfilerEventKind>? Kinds { get; set; }
}

/// <summary>Statistics of a session.</summary>
public sealed class ProfilerSessionStatistics
{
    /// <summary>The session identifier.</summary>
    public required string Id { get; init; }

    /// <summary>The session name.</summary>
    public string? Name { get; init; }

    /// <summary>The session state.</summary>
    public ProfilerSessionState State { get; init; }

    /// <summary>The circuit the session is restricted to, if any.</summary>
    public string? CircuitId { get; init; }

    /// <summary>The wall-clock time the session was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The recorded duration in milliseconds (excluding nothing; paused periods are included).</summary>
    public double DurationMs { get; init; }

    /// <summary>Retained raw events.</summary>
    public int StoredEvents { get; init; }

    /// <summary>Approximate memory retained by raw events.</summary>
    public long StoredBytes { get; init; }

    /// <summary>Events accepted since creation or the last clear.</summary>
    public long AcceptedEvents { get; init; }

    /// <summary>Events evicted by retention limits.</summary>
    public long EvictedEvents { get; init; }

    /// <summary>The sequence number of the most recent event (cursor for incremental reads).</summary>
    public long LastSequence { get; init; }

    /// <summary>Whether the session is backed by an imported trace.</summary>
    public bool IsImported { get; init; }

    /// <summary>The monotonic timestamp at which the session started.</summary>
    public long StartTimestamp { get; init; }

    /// <summary>The frequency (ticks per second) of the session's clock.</summary>
    public long ClockFrequency { get; init; }

    /// <summary>The monotonic anchor of the session's clock.</summary>
    public long ClockAnchorTimestamp { get; init; }

    /// <summary>The wall-clock anchor of the session's clock.</summary>
    public DateTimeOffset ClockAnchorTime { get; init; }
}

/// <summary>A profiling session: an isolated recording window with its own bounded storage and aggregates.</summary>
public interface IProfilerSession : IDisposable
{
    /// <summary>The unique session identifier.</summary>
    string Id { get; }

    /// <summary>The session name.</summary>
    string Name { get; }

    /// <summary>The session state.</summary>
    ProfilerSessionState State { get; }

    /// <summary>The session options.</summary>
    ProfilerSessionOptions Options { get; }

    /// <summary>The clock used for the session's timestamps.</summary>
    IProfilerClock Clock { get; }

    /// <summary>The monotonic timestamp at which the session was created.</summary>
    long StartTimestamp { get; }

    /// <summary>The monotonic timestamp at which the session stopped, if it has.</summary>
    long? StopTimestamp { get; }

    /// <summary>Whether the session was created from an imported trace.</summary>
    bool IsImported { get; }

    /// <summary>Starts recording.</summary>
    void Start();

    /// <summary>Pauses recording.</summary>
    void Pause();

    /// <summary>Resumes recording after <see cref="Pause"/>.</summary>
    void Resume();

    /// <summary>Stops recording permanently. Data remains readable.</summary>
    void Stop();

    /// <summary>Removes all retained events and aggregates.</summary>
    void Clear();

    /// <summary>Adds a bookmark at the current time.</summary>
    MarkerEvent AddBookmark(string name);

    /// <summary>Returns retained events matching a query.</summary>
    List<ProfilerEvent> GetEvents(EventQuery query);

    /// <summary>Returns retained events after a sequence cursor.</summary>
    List<ProfilerEvent> GetEventsAfter(long afterSequence, int max, out long lastSequence);

    /// <summary>Returns the retained events of one operation.</summary>
    IReadOnlyList<ProfilerEvent> GetOperationEvents(long operationId);

    /// <summary>Returns the session's aggregated summary.</summary>
    SessionSummary GetSummary();

    /// <summary>Returns session statistics.</summary>
    ProfilerSessionStatistics GetStatistics();

    /// <summary>Returns a consistent snapshot of retained events and aggregates.</summary>
    SessionSnapshot CreateSnapshot();
}

/// <summary>A consistent, immutable snapshot of a session.</summary>
public sealed class SessionSnapshot
{
    /// <summary>Creates a snapshot.</summary>
    public SessionSnapshot(IProfilerSession session, ProfilerEvent[] events, SessionSummary summary, ProfilerSessionStatistics statistics)
    {
        Session = session;
        Events = events;
        Summary = summary;
        Statistics = statistics;
    }

    /// <summary>The session.</summary>
    public IProfilerSession Session { get; }

    /// <summary>Retained events in storage order.</summary>
    public ProfilerEvent[] Events { get; }

    /// <summary>The aggregated summary.</summary>
    public SessionSummary Summary { get; }

    /// <summary>The statistics.</summary>
    public ProfilerSessionStatistics Statistics { get; }
}

/// <summary>The default <see cref="IProfilerSession"/> implementation.</summary>
public sealed class ProfilerSession : IProfilerSession
{
    private readonly object _lock = new();
    private readonly Action<ProfilerSession>? _stateChanged;
    private readonly RetentionOptions _retention;
    private BoundedEventStore _store;
    private SessionAggregates _aggregates;
    private ProfilerSessionState _state;
    private long? _stopTimestamp;

    internal ProfilerSession(
        string id,
        ProfilerSessionOptions options,
        IProfilerClock clock,
        RetentionOptions retention,
        long startTimestamp,
        bool imported,
        Action<ProfilerSession>? stateChanged)
    {
        Id = id;
        Options = options;
        Clock = clock;
        _retention = retention;
        StartTimestamp = startTimestamp;
        IsImported = imported;
        _stateChanged = stateChanged;
        Name = options.Name ?? (imported ? "Imported trace" : "Session " + id[..6]);
        _store = CreateStore();
        _aggregates = new SessionAggregates(clock, retention, startTimestamp);
        _state = imported ? ProfilerSessionState.Imported : ProfilerSessionState.Created;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ProfilerSessionState State => _state;

    /// <inheritdoc />
    public ProfilerSessionOptions Options { get; }

    /// <inheritdoc />
    public IProfilerClock Clock { get; }

    /// <inheritdoc />
    public long StartTimestamp { get; }

    /// <inheritdoc />
    public long? StopTimestamp => _stopTimestamp;

    /// <inheritdoc />
    public bool IsImported { get; }

    /// <summary>Bookmarks added to the session.</summary>
    public IReadOnlyList<MarkerEvent> Bookmarks
    {
        get
        {
            lock (_lock)
            {
                return [.. _bookmarks];
            }
        }
    }

    private readonly List<MarkerEvent> _bookmarks = [];

    /// <inheritdoc />
    public void Start() => Transition(ProfilerSessionState.Created, ProfilerSessionState.Recording, null);

    /// <inheritdoc />
    public void Pause() => Transition(ProfilerSessionState.Recording, ProfilerSessionState.Paused, MarkerType.SessionPaused);

    /// <inheritdoc />
    public void Resume() => Transition(ProfilerSessionState.Paused, ProfilerSessionState.Recording, MarkerType.SessionResumed);

    /// <inheritdoc />
    public void Stop()
    {
        lock (_lock)
        {
            if (_state is not (ProfilerSessionState.Recording or ProfilerSessionState.Paused or ProfilerSessionState.Created))
            {
                return;
            }

            _state = ProfilerSessionState.Stopped;
            _stopTimestamp = Clock.GetTimestamp();
        }

        _stateChanged?.Invoke(this);
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            _store.Clear();
            _aggregates = new SessionAggregates(Clock, _retention, StartTimestamp);
            _bookmarks.Clear();
        }
    }

    /// <inheritdoc />
    public MarkerEvent AddBookmark(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var marker = new MarkerEvent
        {
            MarkerType = MarkerType.Bookmark,
            Name = name.Length > 128 ? name[..128] : name,
            Timestamp = Clock.GetTimestamp(),
        };

        lock (_lock)
        {
            ThrowIfDisposed();
            _bookmarks.Add(marker);
            AppendCore(marker);
        }

        return marker;
    }

    /// <summary>Returns whether the session accepts an event (state and scope).</summary>
    public bool Accepts(ProfilerEvent profilerEvent)
    {
        if (_state != ProfilerSessionState.Recording)
        {
            return false;
        }

        if (Options.CircuitId is { } circuitId && !string.Equals(profilerEvent.CircuitId, circuitId, StringComparison.Ordinal))
        {
            var processMetric = profilerEvent.CircuitId is null && Options.IncludeProcessMetrics && profilerEvent.Kind == ProfilerEventKind.RuntimeSample;
            if (!processMetric)
            {
                return false;
            }
        }

        return Options.Kinds is null || Options.Kinds.Contains(profilerEvent.Kind);
    }

    /// <summary>Appends an event if the session accepts it.</summary>
    internal bool TryAppend(ProfilerEvent profilerEvent)
    {
        if (!Accepts(profilerEvent))
        {
            return false;
        }

        lock (_lock)
        {
            if (_state != ProfilerSessionState.Recording)
            {
                return false;
            }

            AppendCore(profilerEvent);
            return true;
        }
    }

    /// <summary>Appends a system marker (for example dropped-event notices) regardless of scope.</summary>
    internal void AppendSystemMarker(MarkerEvent marker)
    {
        lock (_lock)
        {
            if (_state == ProfilerSessionState.Recording)
            {
                AppendCore(marker);
            }
        }
    }

    /// <summary>Appends an event to an imported session.</summary>
    internal void AppendImported(ProfilerEvent profilerEvent)
    {
        lock (_lock)
        {
            if (profilerEvent is MarkerEvent { MarkerType: MarkerType.Bookmark } bookmark)
            {
                _bookmarks.Add(bookmark);
            }

            _store.Append(profilerEvent, profilerEvent.EndTimestamp);
            _aggregates.Add(profilerEvent);
        }
    }

    /// <inheritdoc />
    public List<ProfilerEvent> GetEvents(EventQuery query)
    {
        lock (_lock)
        {
            return _store.Query(query);
        }
    }

    /// <inheritdoc />
    public List<ProfilerEvent> GetEventsAfter(long afterSequence, int max, out long lastSequence)
    {
        lock (_lock)
        {
            return _store.GetAfter(afterSequence, max, out lastSequence);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ProfilerEvent> GetOperationEvents(long operationId)
    {
        lock (_lock)
        {
            return _store.GetOperation(operationId);
        }
    }

    /// <inheritdoc />
    public SessionSummary GetSummary()
    {
        lock (_lock)
        {
            return _aggregates.CreateSummary();
        }
    }

    /// <inheritdoc />
    public ProfilerSessionStatistics GetStatistics()
    {
        lock (_lock)
        {
            return CreateStatistics();
        }
    }

    /// <inheritdoc />
    public SessionSnapshot CreateSnapshot()
    {
        lock (_lock)
        {
            return new SessionSnapshot(this, _store.ToArray(), _aggregates.CreateSummary(), CreateStatistics());
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            if (_state == ProfilerSessionState.Disposed)
            {
                return;
            }

            _state = ProfilerSessionState.Disposed;
            _stopTimestamp ??= Clock.GetTimestamp();
            _store.Clear();
            _store = CreateStore();
            _aggregates = new SessionAggregates(Clock, _retention, StartTimestamp);
            _bookmarks.Clear();
        }

        _stateChanged?.Invoke(this);
    }

    private ProfilerSessionStatistics CreateStatistics()
    {
        var end = _stopTimestamp ?? (IsImported ? (_aggregates.CreateSummary().LastEventTimestamp ?? StartTimestamp) : Clock.GetTimestamp());
        return new ProfilerSessionStatistics
        {
            Id = Id,
            Name = Name,
            State = _state,
            CircuitId = Options.CircuitId,
            CreatedAt = Clock.ToDateTimeOffset(StartTimestamp),
            DurationMs = Math.Round(Clock.ToMilliseconds(Math.Max(0, end - StartTimestamp)), 3),
            StoredEvents = _store.Count,
            StoredBytes = _store.EstimatedBytes,
            AcceptedEvents = _store.TotalAppended,
            EvictedEvents = _store.EvictedCount,
            LastSequence = _store.TotalAppended,
            IsImported = IsImported,
            StartTimestamp = StartTimestamp,
            ClockFrequency = Clock.Frequency,
            ClockAnchorTimestamp = Clock.AnchorTimestamp,
            ClockAnchorTime = Clock.AnchorTime,
        };
    }

    private void AppendCore(ProfilerEvent profilerEvent)
    {
        _store.Append(profilerEvent, Clock.GetTimestamp());
        _aggregates.Add(profilerEvent);
    }

    private void Transition(ProfilerSessionState from, ProfilerSessionState to, MarkerType? marker)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_state == to)
            {
                return;
            }

            if (_state != from)
            {
                throw new InvalidOperationException($"Cannot change session state from {_state} to {to}.");
            }

            _state = to;
            if (marker is { } markerType)
            {
                AppendCore(new MarkerEvent { MarkerType = markerType, Name = markerType.ToString(), Timestamp = Clock.GetTimestamp() });
            }
        }

        _stateChanged?.Invoke(this);
    }

    private void ThrowIfDisposed()
    {
        if (_state == ProfilerSessionState.Disposed)
        {
            throw new ObjectDisposedException(nameof(ProfilerSession));
        }
    }

    private BoundedEventStore CreateStore() =>
        new(_retention.MaxEvents, _retention.MaxMemoryBytes, Math.Max(1, Clock.FromTimeSpan(_retention.MaxAge)));
}
