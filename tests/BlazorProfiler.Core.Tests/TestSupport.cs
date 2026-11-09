using BlazorProfiler.Analysis;
using BlazorProfiler.Collectors;
using BlazorProfiler.Events;
using BlazorProfiler.Pipeline;
using BlazorProfiler.Sessions;

namespace BlazorProfiler.Core.Tests;

/// <summary>A manually advanced clock with microsecond ticks.</summary>
public sealed class FakeClock : IProfilerClock
{
    public long Now { get; set; } = 1_000_000;

    public long Frequency => 1_000_000;

    public long AnchorTimestamp => 0;

    public DateTimeOffset AnchorTime { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public long GetTimestamp() => Now;

    public void AdvanceMs(double ms) => Now += (long)(ms * 1000);

    public long Ms(double ms) => (long)(ms * 1000);
}

public static class TestHost
{
    public static ProfilerOptions SyncOptions(Action<ProfilerOptions>? configure = null)
    {
        var options = new ProfilerOptions { Enabled = true, AutoStartSession = false };
        options.Pipeline.SynchronousProcessing = true;
        options.Filters.ExcludedUrlFragments.Clear();
        configure?.Invoke(options);
        return options;
    }

    public static ProfilerHost Create(
        FakeClock? clock = null,
        Action<ProfilerOptions>? configure = null,
        IEnumerable<IProfilerCollector>? collectors = null,
        IEnumerable<IProfilerEventProcessor>? processors = null,
        IEnumerable<IDiagnosticRule>? rules = null) =>
        new(SyncOptions(configure), clock ?? new FakeClock(), collectors, processors: processors, rules: rules);

    /// <summary>Creates a host with a session and returns both.</summary>
    public static (ProfilerHost Host, IProfilerSession Session, FakeClock Clock) WithSession(Action<ProfilerOptions>? configure = null, IEnumerable<IDiagnosticRule>? rules = null)
    {
        var clock = new FakeClock();
        var host = Create(clock, configure, rules: rules);
        var session = host.StartSession();
        return (host, session, clock);
    }
}

/// <summary>Fluent helpers to create events at fake-clock times (milliseconds).</summary>
public static class Ev
{
    public static long T(double ms) => 1_000_000 + (long)(ms * 1000);

    public static long D(double ms) => (long)(ms * 1000);

    public static ComponentRenderEvent Render(string type, double atMs, double durationMs = 1, long instance = 1, bool? unchanged = null, long operation = 0) => new()
    {
        ComponentType = type,
        ComponentInstanceId = instance,
        Timestamp = T(atMs),
        Duration = D(durationMs),
        OutputUnchanged = unchanged,
        OperationId = operation,
        Source = InstrumentationSource.ComponentInstrumentation,
    };

    public static ParametersSetEvent Parameters(string type, double atMs, double durationMs = 0.1, ParameterChangeState state = ParameterChangeState.Unknown, InstrumentationSource source = InstrumentationSource.FrameworkMetrics, string[]? changed = null, long operation = 0) => new()
    {
        ComponentType = type,
        Timestamp = T(atMs),
        Duration = D(durationMs),
        ChangeState = state,
        Source = source,
        ChangedParameters = changed,
        OperationId = operation,
    };

    public static EventHandlerEvent Handler(string type, string method, double atMs, double durationMs, long operation, string attribute = "onclick", double syncMs = 0) => new()
    {
        ComponentType = type,
        MethodName = method,
        AttributeName = attribute,
        Timestamp = T(atMs),
        Duration = D(durationMs),
        OperationId = operation,
        Correlation = CorrelationKind.Self,
        SynchronousDuration = D(syncMs),
        Source = InstrumentationSource.FrameworkMetrics,
    };

    public static RenderBatchEvent Batch(double atMs, double durationMs, int updated, long operation = 0, bool? roundTrip = false, string? circuit = null) => new()
    {
        Timestamp = T(atMs),
        Duration = D(durationMs),
        UpdatedComponentCount = updated,
        OperationId = operation,
        IncludesClientRoundTrip = roundTrip,
        CircuitId = circuit,
        Correlation = operation != 0 ? CorrelationKind.ExecutionContext : CorrelationKind.None,
    };

    public static HttpRequestEvent Http(string url, double atMs, double durationMs, long operation = 0, int status = 200, string method = "GET", string? origin = null, LifecycleMethod? lifecycle = null, long instance = 0) => new()
    {
        Method = method,
        Url = url,
        Timestamp = T(atMs),
        Duration = D(durationMs),
        StatusCode = status,
        OperationId = operation,
        OriginComponentType = origin,
        OriginLifecycleMethod = lifecycle,
        OriginComponentInstanceId = instance,
        Source = InstrumentationSource.HttpMessageHandler,
    };

    public static DatabaseCommandEvent Db(string sql, double atMs, double durationMs = 2, long operation = 0, long arguments = 0, string? origin = null, long instance = 0, LifecycleMethod? lifecycle = null, string? circuit = null, bool isAsync = true, bool? onRenderer = null, long context = 0, string method = "Reader") => new()
    {
        CommandText = sql,
        CommandHash = sql.GetHashCode(StringComparison.Ordinal),
        ParameterHash = HashCode.Combine(sql, arguments),
        ExecuteMethod = method,
        Timestamp = T(atMs),
        Duration = D(durationMs),
        OperationId = operation,
        OriginComponentType = origin,
        OriginComponentInstanceId = instance,
        OriginLifecycleMethod = lifecycle,
        CircuitId = circuit,
        IsAsync = isAsync,
        OnRendererThread = onRenderer,
        ContextType = context == 0 ? null : "App.Data.ShopContext",
        ContextInstanceId = context,
        Source = InstrumentationSource.DatabaseDiagnostics,
    };

    public static JSInteropEvent Interop(string identifier, double atMs, double durationMs = 0.5, long operation = 0, long? payload = null, long instance = 0, string? origin = null) => new()
    {
        Identifier = identifier,
        Timestamp = T(atMs),
        Duration = D(durationMs),
        OperationId = operation,
        PayloadBytes = payload,
        PayloadBytesQuality = payload is null ? ValueQuality.Unavailable : ValueQuality.Estimated,
        OriginComponentInstanceId = instance,
        OriginComponentType = origin,
    };

    public static RuntimeSampleEvent Runtime(double atMs, double durationMs, long allocated, long? heap = null, int gen0 = 0, int gen2 = 0, double pauseMs = 0, long? queue = null) => new()
    {
        Timestamp = T(atMs),
        Duration = D(durationMs),
        AllocatedBytes = allocated,
        HeapSizeBytes = heap,
        Gen0Collections = gen0,
        Gen2Collections = gen2,
        GcPauseTicks = D(pauseMs),
        ThreadPoolQueueLength = queue,
    };
}

/// <summary>A processor that blocks until released, used to fill the queue deterministically.</summary>
public sealed class BlockingProcessor : IProfilerEventProcessor, IDisposable
{
    private readonly ManualResetEventSlim _release = new(false);

    public int Processed;

    public bool Process(ProfilerEvent profilerEvent)
    {
        _release.Wait(TimeSpan.FromSeconds(30));
        Interlocked.Increment(ref Processed);
        return true;
    }

    public void Release() => _release.Set();

    public void Dispose() => _release.Dispose();
}

/// <summary>A collector that throws.</summary>
public sealed class ThrowingCollector : ProfilerCollectorBase
{
    public override CollectorDescriptor Descriptor { get; } = new() { Name = "throwing", Description = "Throws on start", Mechanism = "test" };

    protected override void OnStart(ProfilerCollectorContext context) => throw new InvalidOperationException("boom");

    protected override void OnStop() => throw new InvalidOperationException("stop boom");
}

/// <summary>A collector that records start/stop calls and can emit events.</summary>
public sealed class RecordingCollector : ProfilerCollectorBase
{
    public int Starts;
    public int Stops;

    public override CollectorDescriptor Descriptor { get; } = new() { Name = "recording", Description = "Test", Mechanism = "test" };

    protected override void OnStart(ProfilerCollectorContext context) => Starts++;

    protected override void OnStop() => Stops++;

    public bool Publish(ProfilerEvent e) => Emit(e);
}
