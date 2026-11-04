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
    private readonly object _lock = new();
    private Timer? _timer;
    private Snapshot _last;

    /// <inheritdoc />
    public override CollectorDescriptor Descriptor { get; } = new()
    {
        Name = "runtime",
        Description = "Allocation rate, heap size, GC collection counts and pause time, and (server) CPU time, working set, thread-pool and lock-contention indicators.",
        Mechanism = "Periodic sampling of GC.GetTotalAllocatedBytes(precise: true), GC.GetGCMemoryInfo, GC.CollectionCount, GC.GetTotalPauseDuration, Environment.CpuUsage, Environment.WorkingSet, ThreadPool, and Monitor.LockContentionCount.",
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
            var interval = context.Options.Collectors.RuntimeSampleInterval;
            _timer = new Timer(static s => ((RuntimeCollector)s!).Sample(), this, interval, interval);
        }
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
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
            lock (_lock)
            {
                previous = _last;
                current = Capture(profiler.Clock.GetTimestamp());
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
                GcPauseTicks = clock.FromTimeSpan(current.Pause - previous.Pause),
                CpuTicks = browser ? null : clock.FromTimeSpan(current.Cpu - previous.Cpu),
                ProcessorCount = Environment.ProcessorCount,
                WorkingSetBytes = browser ? null : current.WorkingSet,
                ThreadPoolThreads = browser ? null : current.ThreadPoolThreads,
                ThreadPoolQueueLength = browser ? null : current.ThreadPoolQueue,
                LockContentions = browser ? null : current.LockContentions - previous.LockContentions,
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
}
