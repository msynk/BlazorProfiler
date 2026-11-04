using BlazorProfiler.Events;
using Microsoft.Extensions.Logging;

namespace BlazorProfiler.Collectors;

/// <summary>How completely a collector can observe its signals on the current platform.</summary>
public enum CapabilityLevel
{
    /// <summary>All documented signals are available.</summary>
    Full = 0,
    /// <summary>Some signals are unavailable or approximate on this platform.</summary>
    Partial = 1,
    /// <summary>Relies on behavior that may change between framework versions; validated but not guaranteed.</summary>
    Experimental = 2,
    /// <summary>Not available on this platform.</summary>
    Unsupported = 3,
}

/// <summary>The approximate cost a collector adds to the instrumented application.</summary>
public enum OverheadLevel
{
    /// <summary>Negligible: work proportional to rare events.</summary>
    Negligible = 0,
    /// <summary>Low: constant work per observed operation.</summary>
    Low = 1,
    /// <summary>Moderate: work per component render or per call.</summary>
    Moderate = 2,
    /// <summary>High: work proportional to render-tree size or payload size.</summary>
    High = 3,
}

/// <summary>The runtime state of a collector.</summary>
public enum CollectorState
{
    /// <summary>Not started yet.</summary>
    NotStarted = 0,
    /// <summary>Running.</summary>
    Running = 1,
    /// <summary>Stopped.</summary>
    Stopped = 2,
    /// <summary>Disabled by configuration.</summary>
    Disabled = 3,
    /// <summary>Not supported on this platform or configuration.</summary>
    Unsupported = 4,
    /// <summary>Failed; see <see cref="CollectorStatus.LastError"/>. The application is not affected.</summary>
    Failed = 5,
}

/// <summary>Static description of a collector: what it observes, how, and at what cost.</summary>
public sealed class CollectorDescriptor
{
    /// <summary>A unique, stable collector name.</summary>
    public required string Name { get; init; }

    /// <summary>What the collector observes.</summary>
    public required string Description { get; init; }

    /// <summary>The mechanism used (public API names).</summary>
    public required string Mechanism { get; init; }

    /// <summary>The capability level on the current platform.</summary>
    public CapabilityLevel Capability { get; init; }

    /// <summary>The overhead profile.</summary>
    public OverheadLevel Overhead { get; init; }

    /// <summary>Compatibility requirements (framework version, hosting model).</summary>
    public string? Compatibility { get; init; }

    /// <summary>How the collector degrades when its mechanism is unavailable.</summary>
    public string? Degradation { get; init; }

    /// <summary>Whether the collector must be enabled explicitly.</summary>
    public bool OptIn { get; init; }
}

/// <summary>A snapshot of a collector's health.</summary>
public sealed class CollectorStatus
{
    /// <summary>The collector descriptor.</summary>
    public required CollectorDescriptor Descriptor { get; init; }

    /// <summary>The collector state.</summary>
    public CollectorState State { get; init; }

    /// <summary>Events emitted and accepted by the pipeline.</summary>
    public long EventsEmitted { get; init; }

    /// <summary>Errors caught inside the collector.</summary>
    public long Errors { get; init; }

    /// <summary>The type and message of the most recent error, if any.</summary>
    public string? LastError { get; init; }

    /// <summary>An informational message, for example why the collector is unsupported.</summary>
    public string? Message { get; init; }
}

/// <summary>Context passed to collectors when they start.</summary>
public sealed class ProfilerCollectorContext
{
    /// <summary>Creates a context.</summary>
    public ProfilerCollectorContext(IProfiler profiler, ILoggerFactory loggerFactory)
    {
        Profiler = profiler;
        LoggerFactory = loggerFactory;
    }

    /// <summary>The profiler.</summary>
    public IProfiler Profiler { get; }

    /// <summary>The profiler options.</summary>
    public ProfilerOptions Options => Profiler.Options;

    /// <summary>The logger factory.</summary>
    public ILoggerFactory LoggerFactory { get; }
}

/// <summary>A source of profiler events.</summary>
/// <remarks>
/// Collectors are started when the first session starts recording and stopped when no session is recording. They must
/// never throw from callbacks into application or framework code, must not block, and must release every resource in
/// <see cref="Stop"/>.
/// </remarks>
public interface IProfilerCollector
{
    /// <summary>The collector description.</summary>
    CollectorDescriptor Descriptor { get; }

    /// <summary>Returns the current status.</summary>
    CollectorStatus GetStatus();

    /// <summary>Starts collection. Failures must be reported through <see cref="GetStatus"/> instead of thrown.</summary>
    void Start(ProfilerCollectorContext context);

    /// <summary>Stops collection and releases resources. Must be idempotent.</summary>
    void Stop();
}

/// <summary>Base class for collectors with health tracking and safe emission.</summary>
public abstract class ProfilerCollectorBase : IProfilerCollector
{
    private long _emitted;
    private long _errors;
    private string? _lastError;
    private string? _message;
    private volatile CollectorState _state;
    private ILogger? _logger;

    /// <inheritdoc />
    public abstract CollectorDescriptor Descriptor { get; }

    /// <summary>The profiler, available after <see cref="Start"/>.</summary>
    protected IProfiler? Profiler { get; private set; }

    /// <summary>Whether the collector is running and at least one session is recording.</summary>
    protected bool IsActive => _state == CollectorState.Running && Profiler is { IsRecording: true };

    /// <summary>The current state.</summary>
    protected CollectorState State => _state;

    /// <inheritdoc />
    public CollectorStatus GetStatus() => new()
    {
        Descriptor = Descriptor,
        State = _state,
        EventsEmitted = Interlocked.Read(ref _emitted),
        Errors = Interlocked.Read(ref _errors),
        LastError = _lastError,
        Message = _message,
    };

    /// <inheritdoc />
    public void Start(ProfilerCollectorContext context)
    {
        if (_state == CollectorState.Running)
        {
            return;
        }

        Profiler = context.Profiler;
        _logger = context.LoggerFactory.CreateLogger(GetType());
        if (!IsEnabled(context.Options))
        {
            _state = CollectorState.Disabled;
            return;
        }

        try
        {
            var unsupportedReason = GetUnsupportedReason(context);
            if (unsupportedReason is not null)
            {
                _message = unsupportedReason;
                _state = CollectorState.Unsupported;
                return;
            }

            OnStart(context);
            _state = CollectorState.Running;
        }
        catch (Exception ex)
        {
            _state = CollectorState.Failed;
            RecordError(ex);
            SafeStop();
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_state != CollectorState.Running)
        {
            return;
        }

        _state = CollectorState.Stopped;
        SafeStop();
    }

    /// <summary>Returns whether the collector is enabled by configuration.</summary>
    protected virtual bool IsEnabled(ProfilerOptions options) => true;

    /// <summary>Returns a reason when the collector cannot run in the current environment; otherwise <see langword="null"/>.</summary>
    protected virtual string? GetUnsupportedReason(ProfilerCollectorContext context) => null;

    /// <summary>Starts collection.</summary>
    protected abstract void OnStart(ProfilerCollectorContext context);

    /// <summary>Stops collection.</summary>
    protected abstract void OnStop();

    /// <summary>Emits an event if a session is recording. Never throws.</summary>
    protected bool Emit(ProfilerEvent profilerEvent)
    {
        var profiler = Profiler;
        if (profiler is null)
        {
            return false;
        }

        if (profiler.Emit(profilerEvent))
        {
            Interlocked.Increment(ref _emitted);
            return true;
        }

        return false;
    }

    /// <summary>Records a caught exception in the collector's health status.</summary>
    protected void RecordError(Exception exception)
    {
        Interlocked.Increment(ref _errors);
        _lastError = $"{exception.GetType().Name}: {exception.Message}";
        _logger?.LogWarning(exception, "Profiler collector {Collector} failed.", Descriptor.Name);
    }

    /// <summary>Sets an informational status message.</summary>
    protected void SetMessage(string? message) => _message = message;

    private void SafeStop()
    {
        try
        {
            OnStop();
        }
        catch (Exception ex)
        {
            RecordError(ex);
        }
    }
}
