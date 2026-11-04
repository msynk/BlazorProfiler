using BlazorProfiler.Events;

namespace BlazorProfiler.Correlation;

/// <summary>Manual, application-specific instrumentation.</summary>
public static class ProfilerOperations
{
    /// <summary>
    /// Starts a custom operation. Work started inside the returned scope (including asynchronous continuations, HTTP calls,
    /// and interop calls) is attributed to the operation. Dispose the scope to record the operation's duration.
    /// </summary>
    /// <example>
    /// <code>
    /// using (Profiler.StartOperation("Load dashboard", "data"))
    /// {
    ///     await LoadAsync();
    /// }
    /// </code>
    /// </example>
    public static ProfilerOperationScope StartOperation(this IProfiler profiler, string name, string? category = null)
    {
        ArgumentNullException.ThrowIfNull(profiler);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!profiler.IsRecording)
        {
            return ProfilerOperationScope.Inactive;
        }

        var operationId = profiler.CreateOperationId();
        var start = profiler.Clock.GetTimestamp();
        var parent = ProfilerContext.Current;
        var frame = new OperationFrame(operationId, OperationKind.Custom, CorrelationKind.ExecutionContext)
        {
            CircuitId = parent?.CircuitId,
            Parent = parent,
            StartTimestamp = start,
        };

        var previous = ProfilerContext.Push(frame);
        return new ProfilerOperationScope(profiler, frame, previous, name, category);
    }

    /// <summary>Adds an instantaneous custom marker to the timeline.</summary>
    public static bool Mark(this IProfiler profiler, string name)
    {
        ArgumentNullException.ThrowIfNull(profiler);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return profiler.IsRecording && profiler.Emit(new MarkerEvent
        {
            MarkerType = MarkerType.Custom,
            Name = name.Length > 128 ? name[..128] : name,
            Timestamp = profiler.Clock.GetTimestamp(),
        });
    }
}

/// <summary>A custom operation started by <see cref="ProfilerOperations.StartOperation"/>.</summary>
public sealed class ProfilerOperationScope : IDisposable
{
    internal static readonly ProfilerOperationScope Inactive = new();

    private readonly IProfiler? _profiler;
    private readonly OperationFrame? _frame;
    private readonly OperationFrame? _previous;
    private readonly string? _name;
    private readonly string? _category;
    private ProfilerOutcome _outcome;
    private string? _errorType;
    private int _disposed;

    private ProfilerOperationScope()
    {
    }

    internal ProfilerOperationScope(IProfiler profiler, OperationFrame frame, OperationFrame? previous, string name, string? category)
    {
        _profiler = profiler;
        _frame = frame;
        _previous = previous;
        _name = name;
        _category = category;
    }

    /// <summary>The operation identifier, or zero when profiling is inactive.</summary>
    public long OperationId => _frame?.OperationId ?? 0;

    /// <summary>Marks the operation as failed.</summary>
    public void SetError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _outcome = exception is OperationCanceledException ? ProfilerOutcome.Canceled : ProfilerOutcome.Error;
        _errorType = exception.GetType().FullName;
    }

    /// <summary>Ends the operation and restores the previous ambient context.</summary>
    public void Dispose()
    {
        if (_profiler is null || _frame is null || Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (ReferenceEquals(ProfilerContext.Current, _frame))
        {
            ProfilerContext.Restore(_previous);
        }

        var end = _profiler.Clock.GetTimestamp();
        _profiler.Emit(new OperationEvent
        {
            Name = _name,
            Category = _category,
            Timestamp = _frame.StartTimestamp,
            Duration = end - _frame.StartTimestamp,
            OperationId = _frame.OperationId,
            Correlation = CorrelationKind.Self,
            Outcome = _outcome,
            ErrorType = _errorType,
            CircuitId = _frame.CircuitId,
        });
    }
}
