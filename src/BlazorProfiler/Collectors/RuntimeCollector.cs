using System.Runtime.CompilerServices;
using BlazorProfiler.Events;

namespace BlazorProfiler.Collectors;

/// <summary>Samples managed runtime and garbage-collection state at a fixed interval.</summary>
/// <remarks>
/// Uses only public, permission-free runtime APIs (<see cref="GC"/>, <see cref="Environment"/>, <see cref="ThreadPool"/>,
/// <see cref="Monitor"/>). Values that are not available in the browser (CPU, working set, thread pool) are reported as
/// unavailable there instead of being estimated.
/// </remarks>
public sealed class RuntimeCollector : ProfilerCollectorBase
{
    private const int MaxTrackedExceptionTypes = 64;

    [ThreadStatic]
    private static bool t_inExceptionHandler;

    private readonly object _lock = new();
    private Timer? _timer;
    private Snapshot _last;
    private long _exceptions;

    // Lock-free: the handler runs on every throwing thread, so it must not serialize them. The sampler swaps the dictionary;
    // a count that races with the swap lands in the next sample or is lost, which keeps the bound approximate only.
    private System.Collections.Concurrent.ConcurrentDictionary<string, StrongBox<int>> _exceptionTypes = new(StringComparer.Ordinal);
    private EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs>? _firstChance;

    /// <inheritdoc />
    public override CollectorDescriptor Descriptor { get; } = new()
    {
        Name = "runtime",
        Description = "Allocation rate, heap size, GC collection counts and pause time, and (server) CPU time, working set, thread-pool and lock-contention indicators.",
        Mechanism = "Periodic sampling of GC.GetTotalAllocatedBytes(precise: true), GC.GetGCMemoryInfo, GC.CollectionCount, GC.GetTotalPauseDuration, Environment.CpuUsage, Environment.WorkingSet, ThreadPool, Monitor.LockContentionCount, and Timer.ActiveCount; first-chance exceptions are counted by type (AppDomain.FirstChanceException).",
        Capability = OperatingSystem.IsBrowser() ? CapabilityLevel.Partial : CapabilityLevel.Full,
        Overhead = OverheadLevel.Negligible,
        Compatibility = "All hosting models. In WebAssembly values describe the .NET runtime in the browser tab; on the server they describe the whole process.",
        Degradation = "Platform-specific values are reported as unavailable. Allocation data is process-wide and never attributed to individual components.",
    };

    /// <inheritdoc />
    protected override bool IsEnabled(ProfilerOptions options) => options.Collectors.Runtime;

    /// <inheritdoc />
    protected override void OnStart(ProfilerCollectorContext context)
    {
        lock (_lock)
        {
            _last = Capture(context.Profiler.Clock.GetTimestamp());
            _last.Exceptions = Interlocked.Read(ref _exceptions);

            // Types counted before this start (a previous recording) belong to no sample.
            Interlocked.Exchange(ref _exceptionTypes, new(StringComparer.Ordinal));
            var interval = context.Options.Collectors.RuntimeSampleInterval;
            _timer = new Timer(static s => ((RuntimeCollector)s!).Sample(), this, interval, interval);
            _firstChance ??= OnFirstChanceException;
            AppDomain.CurrentDomain.FirstChanceException += _firstChance;
        }
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            if (_firstChance is not null)
            {
                AppDomain.CurrentDomain.FirstChanceException -= _firstChance;
            }
        }
    }

    /// <summary>
    /// Counts every exception thrown in the process (including caught ones) and its type. Runs on the throwing thread for
    /// every exception, so it only increments counters; an exception thrown inside it is ignored, never re-entered.
    /// </summary>
    private void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        if (t_inExceptionHandler)
        {
            return;
        }

        t_inExceptionHandler = true;
        try
        {
            Interlocked.Increment(ref _exceptions);
            var types = Volatile.Read(ref _exceptionTypes);
            var type = e.Exception.GetType().FullName ?? "Exception";
            if (!types.TryGetValue(type, out var counter))
            {
                counter = types.Count < MaxTrackedExceptionTypes
                    ? types.GetOrAdd(type, static _ => new StrongBox<int>())
                    : types.GetOrAdd("(other types)", static _ => new StrongBox<int>());
            }

            Interlocked.Increment(ref counter.Value);
        }
        catch
        {
            // Counting must never affect the application.
        }
        finally
        {
            t_inExceptionHandler = false;
        }
    }

    private Dictionary<string, int>? TakeExceptionTypes()
    {
        var taken = Interlocked.Exchange(ref _exceptionTypes, new(StringComparer.Ordinal));
        return taken.IsEmpty
            ? null
            : taken.OrderByDescending(p => p.Value.Value).Take(RuntimeSampleEvent.MaxExceptionTypes).ToDictionary(p => p.Key, p => p.Value.Value, StringComparer.Ordinal);
    }

    /// <summary>Takes a sample immediately (used by tests and when flushing).</summary>
    public void Sample()
    {
        var profiler = Profiler;
        if (profiler is null || !profiler.IsRecording || State != CollectorState.Running)
        {
            return;
        }

        try
        {
            Snapshot previous;
            Snapshot current;
            Dictionary<string, int>? types;
            lock (_lock)
            {
                previous = _last;
                current = Capture(profiler.Clock.GetTimestamp());

                // The count and the per-type counts are taken together, so the types describe the same interval.
                current.Exceptions = Interlocked.Read(ref _exceptions);
                types = TakeExceptionTypes();
                _last = current;
            }

            var clock = profiler.Clock;
            var browser = OperatingSystem.IsBrowser();
            Emit(new RuntimeSampleEvent
            {
                Timestamp = previous.Timestamp,
                Duration = current.Timestamp - previous.Timestamp,
                AllocatedBytes = Math.Max(0, current.AllocatedBytes - previous.AllocatedBytes),
                HeapSizeBytes = current.HeapSizeBytes > 0 ? current.HeapSizeBytes : null,
                TotalMemoryBytes = current.TotalMemoryBytes,
                Gen0Collections = current.Gen0 - previous.Gen0,
                Gen1Collections = current.Gen1 - previous.Gen1,
                Gen2Collections = current.Gen2 - previous.Gen2,
                // The WebAssembly (Mono) runtime does not track GC pause time: GC.GetTotalPauseDuration always returns zero.
                GcPauseTicks = browser ? null : clock.FromTimeSpan(current.Pause - previous.Pause),
                CpuTicks = browser ? null : clock.FromTimeSpan(current.Cpu - previous.Cpu),
                ProcessorCount = Environment.ProcessorCount,
                WorkingSetBytes = browser ? null : current.WorkingSet,
                ThreadPoolThreads = browser ? null : current.ThreadPoolThreads,
                ThreadPoolQueueLength = browser ? null : current.ThreadPoolQueue,
                LockContentions = browser ? null : current.LockContentions - previous.LockContentions,
                Exceptions = Math.Max(0, current.Exceptions - previous.Exceptions),
                ExceptionTypes = types,
                ActiveTimers = current.ActiveTimers,
            });
        }
        catch (Exception ex)
        {
            RecordError(ex);
        }
    }

    private static Snapshot Capture(long timestamp)
    {
        var browser = OperatingSystem.IsBrowser();
        var info = GC.GetGCMemoryInfo();
        return new Snapshot
        {
            Timestamp = timestamp,
            AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true),
            HeapSizeBytes = info.HeapSizeBytes,
            TotalMemoryBytes = GC.GetTotalMemory(forceFullCollection: false),
            Gen0 = GC.CollectionCount(0),
            Gen1 = GC.CollectionCount(1),
            Gen2 = GC.CollectionCount(2),
            Pause = GC.GetTotalPauseDuration(),
            Cpu = browser ? TimeSpan.Zero : Environment.CpuUsage.TotalTime,
            WorkingSet = browser ? 0 : Environment.WorkingSet,
            ThreadPoolThreads = browser ? 0 : ThreadPool.ThreadCount,
            ThreadPoolQueue = browser ? 0 : ThreadPool.PendingWorkItemCount,
            LockContentions = browser ? 0 : Monitor.LockContentionCount,
            ActiveTimers = Timer.ActiveCount,
        };
    }

    private struct Snapshot
    {
        public long Timestamp;
        public long AllocatedBytes;
        public long HeapSizeBytes;
        public long TotalMemoryBytes;
        public int Gen0;
        public int Gen1;
        public int Gen2;
        public TimeSpan Pause;
        public TimeSpan Cpu;
        public long WorkingSet;
        public int ThreadPoolThreads;
        public long ThreadPoolQueue;
        public long LockContentions;
        public long Exceptions;
        public long ActiveTimers;
    }
}

/// <summary>Information about the hosting environment, supplied by the hosting integration.</summary>
public sealed class ProfilerHostingInfo
{
    /// <summary>Whether the profiler runs inside Blazor WebAssembly.</summary>
    public bool IsWebAssembly { get; set; } = OperatingSystem.IsBrowser();

    /// <summary>A short label for the runtime (for example <c>server</c> or <c>webassembly</c>).</summary>
    public string Source { get; set; } = OperatingSystem.IsBrowser() ? "webassembly" : "server";

    /// <summary>The environment name, when known.</summary>
    public string? EnvironmentName { get; set; }

    /// <summary>
    /// The URL of the dashboard, relative to the app's base URL. WebAssembly apps use the static dashboard with the
    /// same-origin bridge; server apps use the endpoint mapped by <c>MapBlazorProfiler</c>.
    /// </summary>
    public string DashboardUrl { get; set; } = "_content/BlazorProfiler/dashboard/index.html?source=bridge";

    /// <summary>
    /// The URL of the Model Context Protocol endpoint relative to the app's base URL, when the hosting integration maps one
    /// (Blazor Server); used by in-page agent tools when the dashboard bridge is off.
    /// </summary>
    public string? McpUrl { get; set; }
}
