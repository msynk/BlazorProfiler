using System.Diagnostics;
using System.Threading.Channels;
using BlazorProfiler.Analysis;
using BlazorProfiler.Analysis.Rules;
using BlazorProfiler.Collectors;
using BlazorProfiler.Correlation;
using BlazorProfiler.Events;
using BlazorProfiler.Pipeline;
using BlazorProfiler.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorProfiler;

/// <summary>The profiler: entry point for collectors, sessions, and analysis.</summary>
public interface IProfiler
{
    /// <summary>Whether the profiler is enabled.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Whether at least one session is recording. Collectors check this before allocating events so that an idle profiler
    /// costs almost nothing.
    /// </summary>
    bool IsRecording { get; }

    /// <summary>The clock.</summary>
    IProfilerClock Clock { get; }

    /// <summary>The options.</summary>
    ProfilerOptions Options { get; }

    /// <summary>
    /// Submits an event. Never blocks and never throws. Returns <see langword="false"/> when the event was not accepted
    /// (no recording session, sampled out, or the queue was full).
    /// </summary>
    bool Emit(ProfilerEvent profilerEvent);

    /// <summary>Allocates a new operation identifier.</summary>
    long CreateOperationId();

    /// <summary>Creates and starts a session.</summary>
    /// <exception cref="InvalidOperationException">The profiler is disabled or the session limit was reached.</exception>
    IProfilerSession StartSession(ProfilerSessionOptions? options = null);

    /// <summary>Returns a session by identifier.</summary>
    IProfilerSession? GetSession(string id);

    /// <summary>Returns all sessions.</summary>
    IReadOnlyList<IProfilerSession> GetSessions();

    /// <summary>Disposes and removes a session.</summary>
    bool RemoveSession(string id);

    /// <summary>Registers a read-only session created from an imported trace.</summary>
    IProfilerSession AddImportedSession(Export.TraceDocument document);

    /// <summary>The diagnostic engine.</summary>
    DiagnosticEngine Diagnostics { get; }

    /// <summary>Returns a snapshot of the profiler's own health and overhead.</summary>
    ProfilerHealthSnapshot GetHealth();

    /// <summary>Waits until every event emitted before the call has been processed.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>A snapshot of the profiler's health and self-overhead.</summary>
public sealed class ProfilerHealthSnapshot
{
    /// <summary>Whether the profiler is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Whether any session is recording.</summary>
    public bool Recording { get; init; }

    /// <summary>The active profile.</summary>
    public ProfilerProfile Profile { get; init; }

    /// <summary>The queue capacity.</summary>
    public int QueueCapacity { get; init; }

    /// <summary>Events currently queued.</summary>
    public int QueueLength { get; init; }

    /// <summary>Events submitted while recording.</summary>
    public long Emitted { get; init; }

    /// <summary>Events removed by sampling.</summary>
    public long SampledOut { get; init; }

    /// <summary>Events dropped because the queue was full.</summary>
    public long DroppedQueueFull { get; init; }

    /// <summary>Events removed by filters or merged by processors.</summary>
    public long Filtered { get; init; }

    /// <summary>Events processed and offered to sessions.</summary>
    public long Processed { get; init; }

    /// <summary>Exceptions caught inside the profiler pipeline.</summary>
    public long InternalErrors { get; init; }

    /// <summary>Total time spent processing events on the consumer, in milliseconds.</summary>
    public double ProcessingMs { get; init; }

    /// <summary>Mean processing cost per event in microseconds.</summary>
    public double ProcessingMicrosecondsPerEvent { get; init; }

    /// <summary>
    /// Mean cost of <see cref="IProfiler.Emit"/> on the instrumented thread in microseconds, estimated from a 1-in-64 sample.
    /// </summary>
    public double EmitMicrosecondsPerEvent { get; init; }

    /// <summary>Collector statuses.</summary>
    public IReadOnlyList<CollectorStatus> Collectors { get; init; } = [];

    /// <summary>The number of sessions.</summary>
    public int Sessions { get; init; }
}

/// <summary>The default <see cref="IProfiler"/> implementation.</summary>
public sealed class ProfilerHost : IProfiler, IAsyncDisposable, IDisposable
{
    private const int EmitSampleMask = 63;

    private readonly object _gate = new();
    private readonly List<ProfilerSession> _sessions = [];
    private readonly IProfilerCollector[] _collectors;
    private readonly IProfilerCorrelationProvider[] _correlationProviders;
    private readonly IProfilerEventProcessor[] _processors;
    private readonly ProfilerCollectorContext _collectorContext;
    private readonly EventSampler _sampler;
    private readonly Channel<ProfilerEvent>? _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ILogger _logger;
    private readonly object _processLock = new();

    // Serializes collector start/stop. Held while collectors start or stop (never while holding _gate), so the decision
    // and the transition happen atomically with respect to other session state changes.
    private readonly object _collectorGate = new();

    private ProfilerSession[] _recording = [];

    // The sessions offered the event currently being processed (consumer only, or under _processLock).
    private ProfilerSession[] _processingSessions = [];
    private Task? _consumer;
    private bool _collectorsRunning;
    private bool _started;
    private volatile bool _disposed;
    private long _nextEventId;
    private long _nextOperationId;
    private long _emitted;
    private long _sampledOut;
    private long _dropped;
    private long _droppedReported;
    private long _filtered;
    private long _processed;
    private long _internalErrors;
    private long _processingTicks;
    private long _emitSampleTicks;
    private long _emitSamples;

    /// <summary>Creates a profiler host.</summary>
    public ProfilerHost(
        ProfilerOptions options,
        IProfilerClock? clock = null,
        IEnumerable<IProfilerCollector>? collectors = null,
        IEnumerable<IProfilerCorrelationProvider>? correlationProviders = null,
        IEnumerable<IProfilerEventProcessor>? processors = null,
        IProfilerRedactionPolicy? redactionPolicy = null,
        IEnumerable<IDiagnosticRule>? rules = null,
        IEnumerable<IDiagnosticRecommendationProvider>? recommendationProviders = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        Clock = clock ?? StopwatchProfilerClock.Instance;
        loggerFactory ??= NullLoggerFactory.Instance;
        _logger = loggerFactory.CreateLogger<ProfilerHost>();
        _collectors = collectors?.ToArray() ?? [];
        _correlationProviders = [new AmbientCorrelationProvider(options.CaptureTraceContext), .. correlationProviders ?? []];
        _sampler = new EventSampler(options.Sampling);
        RedactionPolicy = redactionPolicy ?? new DefaultRedactionPolicy(options.Redaction);
        _processors =
        [
            new FilterProcessor(options.Filters),
            new RedactionProcessor(RedactionPolicy),
            new BrowserResourceMergeProcessor(Clock, () => _processingSessions, ReplaceStoredEvent),
            .. processors ?? [],
        ];
        Diagnostics = new DiagnosticEngine(options.Diagnostics, rules ?? BuiltInRules.Create(), recommendationProviders);
        _collectorContext = new ProfilerCollectorContext(this, loggerFactory);

        if (!options.Pipeline.SynchronousProcessing)
        {
            _channel = Channel.CreateBounded<ProfilerEvent>(new BoundedChannelOptions(options.Pipeline.QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
        }
    }

    /// <inheritdoc />
    public bool IsEnabled => Options.Enabled != false;

    /// <inheritdoc />
    public bool IsRecording => _recording.Length > 0;

    /// <inheritdoc />
    public IProfilerClock Clock { get; }

    /// <inheritdoc />
    public ProfilerOptions Options { get; }

    /// <summary>The redaction policy.</summary>
    public IProfilerRedactionPolicy RedactionPolicy { get; }

    /// <inheritdoc />
    public DiagnosticEngine Diagnostics { get; }

    /// <summary>The registered collectors.</summary>
    public IReadOnlyList<IProfilerCollector> Collectors => _collectors;

    /// <summary>
    /// Starts the profiler: starts the processing consumer and, when <see cref="ProfilerOptions.AutoStartSession"/> is set,
    /// a default session. Idempotent. Does nothing when the profiler is disabled.
    /// </summary>
    public void Start()
    {
        if (!IsEnabled)
        {
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            if (_channel is not null)
            {
                _consumer = Task.Run(() => ConsumeAsync(_shutdown.Token));
            }
        }

        if (Options.AutoStartSession)
        {
            StartSession(new ProfilerSessionOptions { Name = "Default session" });
        }
    }

    /// <inheritdoc />
    public bool Emit(ProfilerEvent profilerEvent)
    {
        if (_recording.Length == 0 || profilerEvent is null)
        {
            return false;
        }

        var sampleThisEmit = (Interlocked.Increment(ref _emitted) & EmitSampleMask) == 0;
        var start = sampleThisEmit ? Stopwatch.GetTimestamp() : 0;
        try
        {
            if (profilerEvent.Id == 0)
            {
                profilerEvent.Id = Interlocked.Increment(ref _nextEventId);
            }

            foreach (var provider in _correlationProviders)
            {
                provider.Enrich(profilerEvent);
            }

            if (!_sampler.ShouldKeep(profilerEvent))
            {
                Interlocked.Increment(ref _sampledOut);
                return false;
            }

            if (_channel is null)
            {
                lock (_processLock)
                {
                    Process(profilerEvent);
                }

                return true;
            }

            if (!_channel.Writer.TryWrite(profilerEvent))
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _internalErrors);
            _logger.LogDebug(ex, "Profiler failed to accept an event.");
            return false;
        }
        finally
        {
            if (sampleThisEmit)
            {
                Interlocked.Add(ref _emitSampleTicks, Stopwatch.GetTimestamp() - start);
                Interlocked.Increment(ref _emitSamples);
            }
        }
    }

    /// <inheritdoc />
    public long CreateOperationId() => Interlocked.Increment(ref _nextOperationId);

    /// <inheritdoc />
    public IProfilerSession StartSession(ProfilerSessionOptions? options = null)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("The profiler is disabled.");
        }

        options ??= new ProfilerSessionOptions();
        ProfilerSession session;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started)
            {
                _started = true;
                if (_channel is not null)
                {
                    _consumer = Task.Run(() => ConsumeAsync(_shutdown.Token));
                }
            }

            if (_sessions.Count >= Options.Retention.MaxSessions)
            {
                throw new InvalidOperationException($"The maximum number of sessions ({Options.Retention.MaxSessions}) has been reached. Remove a session first.");
            }

            session = new ProfilerSession(Guid.NewGuid().ToString("N"), options, Clock, Options.Retention, Clock.GetTimestamp(), imported: false, OnSessionStateChanged);
            _sessions.Add(session);
        }

        session.Start();
        return session;
    }

    /// <inheritdoc />
    public IProfilerSession AddImportedSession(Export.TraceDocument document)
    {
        var session = Export.TraceImporter.CreateSession(document, Options);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.Count >= Options.Retention.MaxSessions)
            {
                session.Dispose();
                throw new InvalidOperationException($"The maximum number of sessions ({Options.Retention.MaxSessions}) has been reached. Remove a session first.");
            }

            _sessions.Add(session);
        }

        return session;
    }

    /// <inheritdoc />
    public IProfilerSession? GetSession(string id)
    {
        lock (_gate)
        {
            return _sessions.Find(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IProfilerSession> GetSessions()
    {
        lock (_gate)
        {
            return [.. _sessions];
        }
    }

    /// <inheritdoc />
    public bool RemoveSession(string id)
    {
        ProfilerSession? session;
        lock (_gate)
        {
            session = _sessions.Find(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            if (session is null)
            {
                return false;
            }

            _sessions.Remove(session);
        }

        session.Dispose();
        return true;
    }

    /// <inheritdoc />
    public ProfilerHealthSnapshot GetHealth()
    {
        var processed = Interlocked.Read(ref _processed);
        var processingTicks = Interlocked.Read(ref _processingTicks);
        var emitSamples = Interlocked.Read(ref _emitSamples);
        int sessions;
        lock (_gate)
        {
            sessions = _sessions.Count;
        }

        return new ProfilerHealthSnapshot
        {
            Enabled = IsEnabled,
            Recording = IsRecording,
            Profile = Options.Profile,
            QueueCapacity = Options.Pipeline.QueueCapacity,
            QueueLength = _channel?.Reader.CanCount == true ? _channel.Reader.Count : 0,
            Emitted = Interlocked.Read(ref _emitted),
            SampledOut = Interlocked.Read(ref _sampledOut),
            DroppedQueueFull = Interlocked.Read(ref _dropped),
            Filtered = Interlocked.Read(ref _filtered),
            Processed = processed,
            InternalErrors = Interlocked.Read(ref _internalErrors),
            ProcessingMs = Math.Round(processingTicks * 1000.0 / Stopwatch.Frequency, 3),
            ProcessingMicrosecondsPerEvent = processed == 0 ? 0 : Math.Round(processingTicks * 1_000_000.0 / Stopwatch.Frequency / processed, 3),
            EmitMicrosecondsPerEvent = emitSamples == 0 ? 0 : Math.Round(Interlocked.Read(ref _emitSampleTicks) * 1_000_000.0 / Stopwatch.Frequency / emitSamples, 3),
            Collectors = [.. _collectors.Select(c => c.GetStatus())],
            Sessions = sessions,
        };
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var consumer = _consumer;
        if (_channel is null || consumer is null)
        {
            return;
        }

        var request = new FlushRequest();
        while (!_channel.Writer.TryWrite(request))
        {
            // The writer fails permanently once the channel is completed (disposal), and the consumer no longer drains
            // the queue once it has stopped; nothing can be flushed in either case.
            if (_disposed || consumer.IsCompleted)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }

        // A request queued just before the consumer stops (disposal or an unexpected failure) is never processed, so
        // also complete when the consumer completes.
        await Task.WhenAny(request.Completion.Task, consumer).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        List<ProfilerSession> sessions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            sessions = [.. _sessions];
            _sessions.Clear();
            _recording = [];
        }

        lock (_collectorGate)
        {
            _collectorsRunning = false;
            StopCollectors();
        }

        _channel?.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_consumer is not null)
        {
            try
            {
                await _consumer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var session in sessions)
        {
            session.Dispose();
        }

        _shutdown.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private void OnSessionStateChanged(ProfilerSession session)
    {
        lock (_gate)
        {
            if (session.State == ProfilerSessionState.Disposed)
            {
                _sessions.Remove(session);
            }

            _recording = [.. _sessions.Where(s => s.State == ProfilerSessionState.Recording)];
        }

        SynchronizeCollectors();
    }

    /// <summary>
    /// Starts or stops collectors so that they run exactly when a session is recording. The desired state is re-read
    /// under the collector gate, so concurrent session changes cannot apply their transitions out of order (for example a
    /// stop computed before another session started, applied after it).
    /// </summary>
    private void SynchronizeCollectors()
    {
        lock (_collectorGate)
        {
            bool shouldRun;
            lock (_gate)
            {
                shouldRun = !_disposed && _recording.Length > 0;
            }

            if (shouldRun == _collectorsRunning)
            {
                return;
            }

            _collectorsRunning = shouldRun;
            if (shouldRun)
            {
                StartCollectors();
            }
            else
            {
                StopCollectors();
            }
        }
    }

    private void StartCollectors()
    {
        foreach (var collector in _collectors)
        {
            try
            {
                collector.Start(_collectorContext);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _internalErrors);
                _logger.LogWarning(ex, "Profiler collector {Collector} failed to start.", collector.Descriptor.Name);
            }
        }
    }

    private void StopCollectors()
    {
        foreach (var collector in _collectors)
        {
            try
            {
                collector.Stop();
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _internalErrors);
                _logger.LogWarning(ex, "Profiler collector {Collector} failed to stop.", collector.Descriptor.Name);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        var reader = _channel!.Reader;
        var maxBatch = Math.Max(1, Options.Pipeline.MaxBatchSize);
        var interval = Options.Pipeline.ConsumerInterval;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var processed = 0;
                while (reader.TryRead(out var profilerEvent))
                {
                    Process(profilerEvent);
                    if (++processed % maxBatch == 0)
                    {
                        // Yield so a single-threaded runtime (WebAssembly) can run application work between batches.
                        await Task.Yield();
                    }
                }

                if (interval > TimeSpan.Zero)
                {
                    // Draining in intervals means writers rarely find a waiting reader, so Emit does not pay for waking
                    // the consumer on every event.
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _internalErrors);
            _logger.LogError(ex, "Profiler event consumer stopped unexpectedly.");
        }
    }

    private void Process(ProfilerEvent profilerEvent)
    {
        if (profilerEvent is FlushRequest flush)
        {
            flush.Completion.TrySetResult();
            return;
        }

        var start = Stopwatch.GetTimestamp();
        try
        {
            var sessions = _recording;
            _processingSessions = sessions;
            ReportDroppedEvents(sessions);

            foreach (var processor in _processors)
            {
                if (!processor.Process(profilerEvent))
                {
                    Interlocked.Increment(ref _filtered);
                    return;
                }
            }

            foreach (var session in sessions)
            {
                session.TryAppend(profilerEvent);
            }

            Interlocked.Increment(ref _processed);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _internalErrors);
            _logger.LogDebug(ex, "Profiler failed to process an event.");
        }
        finally
        {
            Interlocked.Add(ref _processingTicks, Stopwatch.GetTimestamp() - start);
        }
    }

    /// <summary>
    /// Replaces an event that sessions already stored with an enriched copy (runs on the consumer, inside processing).
    /// </summary>
    /// <param name="original">The stored event.</param>
    /// <param name="replacement">The enriched copy.</param>
    /// <param name="recipients">
    /// The sessions the original was offered to; no other session can hold it. They may have been paused, stopped, or
    /// removed since, and are searched regardless of their current state (removed sessions ignore the request).
    /// </param>
    private static void ReplaceStoredEvent(HttpRequestEvent original, HttpRequestEvent replacement, ProfilerSession[] recipients)
    {
        foreach (var session in recipients)
        {
            session.TryReplace(original, replacement);
        }
    }

    private void ReportDroppedEvents(ProfilerSession[] sessions)
    {
        var dropped = Interlocked.Read(ref _dropped);
        var delta = dropped - _droppedReported;
        if (delta <= 0)
        {
            return;
        }

        _droppedReported = dropped;
        var marker = new MarkerEvent
        {
            Id = Interlocked.Increment(ref _nextEventId),
            MarkerType = MarkerType.EventsDropped,
            Name = "Events dropped (queue full)",
            Count = delta,
            Timestamp = Clock.GetTimestamp(),
        };

        foreach (var session in sessions)
        {
            session.AppendSystemMarker(marker);
        }
    }

    private sealed class FlushRequest : ProfilerEvent
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ProfilerEventKind Kind => ProfilerEventKind.Marker;
    }
}
