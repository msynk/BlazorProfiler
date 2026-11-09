using System.Runtime.CompilerServices;
using BlazorProfiler.Api;
using BlazorProfiler.Events;

namespace BlazorProfiler.Collectors;

/// <summary>
/// Detects component instances that stay reachable after they were released: disposed instances of components that
/// inherit <c>ProfiledComponentBase</c>, and every component created in a dependency-injection scope that ended (a closed
/// Blazor Server circuit or a completed static rendering request). Released instances are watched through weak references;
/// an instance still alive after <see cref="CollectorOptions.RetentionGcCycles"/> full garbage collections (and at least
/// <see cref="CollectorOptions.RetentionMinimumAge"/> after its release) is reported once as a <see cref="ComponentRetainedEvent"/>.
/// </summary>
/// <remarks>
/// The approach follows leak trackers in other UI frameworks (Flutter's leak_tracker, Android's LeakCanary): weak
/// references never keep instances alive, and the check runs only after collections that happen anyway, unless a leak
/// check is requested explicitly (<see cref="CheckNow"/> with forced collections). The collector does not find what
/// retains an instance; a memory profiler or heap dump answers that.
/// </remarks>
public sealed class ComponentRetentionCollector : ProfilerCollectorBase
{
    /// <summary>The maximum number of released instances watched at once; the oldest are dropped beyond it.</summary>
    public const int MaxWatched = 50_000;

    private static readonly TimeSpan s_checkInterval = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly ConditionalWeakTable<object, object> _known = [];
    private List<Watched> _watched = [];
    private Timer? _timer;
    private int _lastFullCollections = -1;
    private long _dropped;

    /// <inheritdoc />
    public override CollectorDescriptor Descriptor { get; } = new()
    {
        Name = "component-retention",
        Description = "Component instances still reachable after they were disposed (instrumented components) or after their circuit or request scope ended (all components): likely memory leaks.",
        Mechanism = "Weak references registered on disposal (ProfiledComponentBase) and at the end of the dependency-injection scope (component activator), checked after full garbage collections.",
        Capability = CapabilityLevel.Partial,
        Overhead = OverheadLevel.Low,
        Compatibility = "All hosting models. In WebAssembly the application scope never ends, so only disposed instances of ProfiledComponentBase components are watched.",
        Degradation = "Detection waits for full garbage collections that happen anyway; use the leak check command (forces collections) for an immediate answer. What retains an instance is not determined.",
    };

    /// <summary>Whether the collector is running.</summary>
    public bool IsCollecting => IsActive;

    /// <summary>The number of instances currently watched.</summary>
    public int WatchedCount
    {
        get
        {
            lock (_lock)
            {
                return _watched.Count;
            }
        }
    }

    /// <inheritdoc />
    protected override bool IsEnabled(ProfilerOptions options) => options.Collectors.ComponentRetention;

    /// <inheritdoc />
    protected override void OnStart(ProfilerCollectorContext context)
    {
        lock (_lock)
        {
            _lastFullCollections = GC.CollectionCount(GC.MaxGeneration);
            _timer = new Timer(static s => ((ComponentRetentionCollector)s!).Check(forced: false), this, s_checkInterval, s_checkInterval);
        }
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            _watched = [];
        }
    }

    /// <summary>
    /// Watches a released component instance. Never throws; does nothing when the collector is not running or the instance
    /// is already watched.
    /// </summary>
    /// <param name="component">The released instance (held weakly).</param>
    /// <param name="componentType">The component type name.</param>
    /// <param name="instanceId">The profiler instance id, or zero.</param>
    /// <param name="reason">Why the instance was released.</param>
    /// <param name="circuitId">The circuit the instance belonged to, if known.</param>
    public void Watch(object component, string? componentType, long instanceId, RetentionReason reason, string? circuitId)
    {
        var profiler = Profiler;
        if (!IsCollecting || profiler is null || component is null)
        {
            return;
        }

        try
        {
            lock (_lock)
            {
                if (_known.TryGetValue(component, out _))
                {
                    return;
                }

                _known.Add(component, Marker);
                if (_watched.Count >= MaxWatched)
                {
                    _watched.RemoveAt(0);
                    _dropped++;
                }

                _watched.Add(new Watched(new WeakReference(component), componentType ?? component.GetType().FullName, instanceId, reason, circuitId, profiler.Clock.GetTimestamp(), GC.CollectionCount(GC.MaxGeneration)));
            }
        }
        catch (Exception ex)
        {
            RecordError(ex);
        }
    }

    /// <summary>
    /// Returns the circuit of the current thread's renderer (Blazor Server), or null. Call it where the component runs (for
    /// example during disposal); the check that reports retention runs later on another thread.
    /// </summary>
    public static string? CurrentCircuitId(IProfiler profiler)
    {
        if (profiler is not ProfilerHost host)
        {
            return null;
        }

        var probe = new MarkerEvent();
        host.Correlate(probe);
        return probe.CircuitId;
    }

    /// <summary>
    /// Checks the watched instances now. With <paramref name="collectGarbage"/>, first forces full, compacting collections
    /// (and, outside the browser, waits for pending finalizers), which pauses the whole process: use it for explicit leak
    /// checks only.
    /// </summary>
    public LeakCheckResult CheckNow(bool collectGarbage)
    {
        if (collectGarbage)
        {
            for (var i = 0; i < 2; i++)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                if (!OperatingSystem.IsBrowser())
                {
                    GC.WaitForPendingFinalizers();
                }
            }

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        return Check(forced: collectGarbage);
    }

    private LeakCheckResult Check(bool forced)
    {
        var result = new LeakCheckResult();
        var profiler = Profiler;
        if (profiler is null || State != CollectorState.Running)
        {
            return result;
        }

        try
        {
            var collections = GC.CollectionCount(GC.MaxGeneration);
            var now = profiler.Clock.GetTimestamp();
            var minimumAge = profiler.Clock.FromTimeSpan(profiler.Options.Collectors.RetentionMinimumAge);
            var cycles = Math.Max(1, profiler.Options.Collectors.RetentionGcCycles);
            List<ComponentRetainedEvent>? reports = null;
            lock (_lock)
            {
                if (!forced && collections == _lastFullCollections)
                {
                    result.Watched = _watched.Count;
                    return result;
                }

                _lastFullCollections = collections;
                var keep = new List<Watched>(_watched.Count);
                foreach (var w in _watched)
                {
                    if (!w.Target.IsAlive)
                    {
                        result.Collected++;
                        continue;
                    }

                    var survived = collections - w.FullCollectionsAtRelease;
                    if (survived >= cycles && now - w.ReleasedTimestamp >= minimumAge)
                    {
                        (reports ??= []).Add(new ComponentRetainedEvent
                        {
                            ComponentType = w.ComponentType,
                            ComponentInstanceId = w.InstanceId,
                            CircuitId = w.CircuitId,
                            Reason = w.Reason,
                            ReleasedTimestamp = w.ReleasedTimestamp,
                            SurvivedCollections = survived,
                            ForcedCollection = forced,
                            Timestamp = now,
                            Correlation = CorrelationKind.None,
                        });
                        continue;
                    }

                    keep.Add(w);
                }

                _watched = keep;
                result.Watched = keep.Count;
            }

            foreach (var report in reports ?? [])
            {
                Emit(report);
            }

            result.Retained = reports?.Count ?? 0;
        }
        catch (Exception ex)
        {
            RecordError(ex);
        }

        return result;
    }

    private static readonly object Marker = new();

    private sealed record Watched(WeakReference Target, string? ComponentType, long InstanceId, RetentionReason Reason, string? CircuitId, long ReleasedTimestamp, int FullCollectionsAtRelease);
}

/// <summary>The outcome of a retention check.</summary>
public sealed class LeakCheckResult
{
    /// <summary>Instances reported as retained by this check.</summary>
    public int Retained { get; set; }

    /// <summary>Watched instances that were collected since the previous check.</summary>
    public int Collected { get; set; }

    /// <summary>Instances still watched (released recently or not yet past the collection threshold).</summary>
    public int Watched { get; set; }
}

/// <summary>
/// The <c>check-leaks</c> command: forces full garbage collections and reports released components that are still
/// reachable (rule BP5004).
/// </summary>
public sealed class LeakCheckCommand : IProfilerCommand
{
    private readonly ComponentRetentionCollector _collector;

    /// <summary>Creates the command.</summary>
    public LeakCheckCommand(ComponentRetentionCollector collector) => _collector = collector;

    /// <inheritdoc />
    public ProfilerCommandDescriptor Descriptor { get; } = new()
    {
        Name = "check-leaks",
        Title = "Check for leaks",
        Description = "Forces full garbage collections, then reports disposed components (and components of closed circuits) that are still reachable. The collections pause the whole process briefly.",
    };

    /// <inheritdoc />
    public string? GetState() => _collector.IsCollecting ? $"{_collector.WatchedCount} released instance(s) watched" : "retention tracking is off";

    /// <inheritdoc />
    public ValueTask<ProfilerCommandResult> ExecuteAsync(string? argument, CancellationToken cancellationToken = default)
    {
        if (!_collector.IsCollecting)
        {
            throw new ProfilerApiException("Retention tracking is not running. Enable Collectors.ComponentRetention and start a session.");
        }

        var result = _collector.CheckNow(collectGarbage: true);
        var message = result.Retained == 0
            ? $"No retained components found. {result.Collected} released instance(s) were collected; {result.Watched} released too recently or not yet past the collection threshold are still watched."
            : $"{result.Retained} released component instance(s) are still reachable (see rule BP5004). {result.Collected} were collected; {result.Watched} are still watched.";
        return ValueTask.FromResult(new ProfilerCommandResult
        {
            Message = message,
            Values =
            {
                ["retained"] = result.Retained.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["collected"] = result.Collected.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["watched"] = result.Watched.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        });
    }
}
