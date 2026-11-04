using BlazorProfiler.Events;

namespace BlazorProfiler.Storage;

/// <summary>
/// Incrementally maintained aggregates of one session. Aggregates are updated for every accepted event and are not
/// affected by raw-event eviction, so long sessions keep accurate totals after old events have been discarded.
/// </summary>
/// <remarks>Not thread-safe; the owning session serializes access.</remarks>
public sealed class SessionAggregates
{
    /// <summary>The key used for aggregation entries beyond <see cref="RetentionOptions.MaxAggregateKeys"/>.</summary>
    public const string OverflowKey = "(other)";

    private readonly IProfilerClock _clock;
    private readonly int _maxKeys;
    private readonly int _maxInstances;
    private readonly long _startTimestamp;
    private readonly TimeSeriesBucket[] _buckets;

    private readonly Dictionary<string, ComponentAggregate> _components = new(StringComparer.Ordinal);
    private readonly Dictionary<long, InstanceAggregate> _instances = [];
    private readonly Dictionary<string, HandlerAggregate> _handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EndpointAggregate> _endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InteropAggregate> _interop = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _navigations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CircuitAggregate> _circuits = new(StringComparer.Ordinal);
    private readonly double[] _kindCounts = new double[(int)ProfilerEventKind.Operation + 1];
    private readonly DurationStats _batches = new();
    private readonly DurationStats _interactions = new();
    private readonly DurationStats _longTasks = new();

    private double _batchUpdatedComponents;
    private int _batchMaxUpdatedComponents;
    private long _firstTimestamp = long.MaxValue;
    private long _lastTimestamp = long.MinValue;
    private bool _sampled;
    private RuntimeSampleEvent? _lastRuntime;
    private long _runtimeAllocated;
    private long _runtimeGen0;
    private long _runtimeGen1;
    private long _runtimeGen2;
    private long _runtimePauseTicks;
    private long _runtimeSampleTicks;
    private long _droppedReported;
    private long _errors;

    /// <summary>Creates aggregates for a session that started at <paramref name="startTimestamp"/>.</summary>
    public SessionAggregates(IProfilerClock clock, RetentionOptions retention, long startTimestamp)
    {
        _clock = clock;
        _maxKeys = Math.Max(16, retention.MaxAggregateKeys);
        _maxInstances = Math.Max(16, retention.MaxTrackedInstances);
        _startTimestamp = startTimestamp;
        _buckets = new TimeSeriesBucket[Math.Max(10, retention.TimeSeriesBuckets)];
    }

    /// <summary>Updates the aggregates with an event.</summary>
    public void Add(ProfilerEvent e)
    {
        var weight = e.SampleWeight > 0 ? e.SampleWeight : 1;
        if (weight > 1)
        {
            _sampled = true;
        }

        var kindIndex = (int)e.Kind;
        if ((uint)kindIndex < (uint)_kindCounts.Length)
        {
            _kindCounts[kindIndex] += weight;
        }

        if (e.Timestamp < _firstTimestamp)
        {
            _firstTimestamp = e.Timestamp;
        }

        if (e.EndTimestamp > _lastTimestamp)
        {
            _lastTimestamp = e.EndTimestamp;
        }

        if (e.Outcome == ProfilerOutcome.Error)
        {
            _errors++;
        }

        var bucket = GetBucket(e.EndTimestamp);
        if (bucket is not null && e.Outcome == ProfilerOutcome.Error)
        {
            bucket.Errors++;
        }

        switch (e)
        {
            case ComponentCreatedEvent created:
                {
                    var component = GetComponent(created.ComponentType);
                    component.Created += weight;
                    TrackInstance(created)?.SetCreated(created.Timestamp);
                    break;
                }

            case ComponentDisposedEvent disposed:
                {
                    var component = GetComponent(disposed.ComponentType);
                    component.Disposed += weight;
                    var instance = TrackInstance(disposed);
                    instance?.SetDisposed(disposed.Timestamp);
                    break;
                }

            case ParametersSetEvent parameters:
                {
                    var component = GetComponent(parameters.ComponentType);
                    component.ParametersSets.Add(parameters.Duration, weight);
                    if (parameters.ChangeState == ParameterChangeState.Unchanged)
                    {
                        component.ParametersUnchanged += weight;
                    }
                    else if (parameters.ChangeState == ParameterChangeState.ChangedByReferenceOnly)
                    {
                        component.ParametersChangedByReferenceOnly += weight;
                        if (parameters.ChangedParameters is not null)
                        {
                            foreach (var name in parameters.ChangedParameters)
                            {
                                component.ReferenceOnlyParameterNames.TryGetValue(name, out var count);
                                if (count > 0 || component.ReferenceOnlyParameterNames.Count < 32)
                                {
                                    component.ReferenceOnlyParameterNames[name] = count + weight;
                                }
                            }
                        }
                    }

                    if (parameters.Outcome == ProfilerOutcome.Error)
                    {
                        component.Errors++;
                    }

                    if (parameters.Source == InstrumentationSource.ComponentInstrumentation)
                    {
                        component.Instrumented = true;
                        var instance = TrackInstance(parameters);
                        if (instance is not null)
                        {
                            instance.ParametersSets++;
                            if (parameters.ChangeState is ParameterChangeState.Unchanged or ParameterChangeState.ChangedByReferenceOnly)
                            {
                                instance.UnchangedParametersSets++;
                            }
                        }
                    }

                    if (bucket is not null)
                    {
                        bucket.ParametersSets += weight;
                    }

                    break;
                }

            case ComponentRenderEvent render:
                {
                    var component = GetComponent(render.ComponentType);
                    component.Instrumented = true;
                    component.Renders.Add(render.Duration, weight);
                    if (render.OutputUnchanged == true)
                    {
                        component.RendersWithUnchangedOutput += weight;
                    }

                    if (render.OutputUnchanged is not null)
                    {
                        component.RendersWithOutputComparison += weight;
                    }

                    var instance = TrackInstance(render);
                    if (instance is not null)
                    {
                        instance.Renders++;
                        instance.RenderTicks += render.Duration;
                        instance.LastRender = render.Timestamp;
                        if (render.OutputUnchanged == true)
                        {
                            instance.RendersWithUnchangedOutput++;
                        }
                    }

                    if (bucket is not null)
                    {
                        bucket.ComponentRenders += weight;
                    }

                    break;
                }

            case RenderRequestEvent request:
                {
                    var component = GetComponent(request.ComponentType);
                    component.Instrumented = true;
                    switch (request.Result)
                    {
                        case RenderRequestResult.Queued:
                            break;
                        case RenderRequestResult.Coalesced:
                            component.RenderRequestsCoalesced += weight;
                            break;
                        default:
                            component.RenderRequestsSuppressed += weight;
                            break;
                    }

                    break;
                }

            case LifecycleMethodEvent lifecycle:
                {
                    var component = GetComponent(lifecycle.ComponentType);
                    component.Instrumented = true;
                    if (!component.Lifecycle.TryGetValue(lifecycle.Method, out var stats))
                    {
                        stats = new DurationStats();
                        component.Lifecycle[lifecycle.Method] = stats;
                    }

                    stats.Add(lifecycle.Duration, weight);
                    break;
                }

            case ComponentChildrenEvent children:
                {
                    for (var i = 0; i < children.ChildInstanceIds.Length; i++)
                    {
                        var childId = children.ChildInstanceIds[i];
                        if (childId == 0)
                        {
                            continue;
                        }

                        var childType = i < children.ChildTypes.Length ? children.ChildTypes[i] : null;
                        var child = GetInstance(childId, childType);
                        if (child is not null)
                        {
                            child.ParentInstanceId = children.ComponentInstanceId;
                        }
                    }

                    TrackInstance(children);
                    break;
                }

            case EventHandlerEvent handler:
                {
                    var key = string.Concat(handler.ComponentType, "|", handler.MethodName, "|", handler.AttributeName);
                    var aggregate = GetOrAdd(_handlers, key, k => new HandlerAggregate(handler.ComponentType, handler.MethodName, handler.AttributeName));
                    aggregate.Durations.Add(handler.Duration, weight);
                    if (handler.Outcome == ProfilerOutcome.Error)
                    {
                        aggregate.Errors++;
                    }

                    GetComponent(handler.ComponentType).EventHandlers.Add(handler.Duration, weight);
                    if (bucket is not null)
                    {
                        bucket.EventHandlers += weight;
                        bucket.EventHandlerMs += _clock.ToMilliseconds(handler.Duration);
                    }

                    break;
                }

            case RenderBatchEvent batch:
                {
                    _batches.Add(batch.Duration, weight);
                    _batchUpdatedComponents += batch.UpdatedComponentCount * weight;
                    _batchMaxUpdatedComponents = Math.Max(_batchMaxUpdatedComponents, batch.UpdatedComponentCount);
                    if (bucket is not null)
                    {
                        bucket.RenderBatches += weight;
                        bucket.UpdatedComponents += batch.UpdatedComponentCount * weight;
                        bucket.RenderBatchMs += _clock.ToMilliseconds(batch.Duration);
                    }

                    if (batch.CircuitId is not null)
                    {
                        GetCircuit(batch.CircuitId).Batches.Add(batch.Duration, weight);
                    }

                    break;
                }

            case NavigationEvent navigation:
                {
                    var key = navigation.Route ?? navigation.ComponentType ?? "(unknown)";
                    _navigations.TryGetValue(key, out var count);
                    if (count > 0 || _navigations.Count < _maxKeys)
                    {
                        _navigations[key] = count + weight;
                    }

                    break;
                }

            case HttpRequestEvent http:
                {
                    var key = http.Endpoint ?? http.Url ?? "(unknown)";
                    var aggregate = GetOrAdd(_endpoints, key, k => new EndpointAggregate(k, http.Method));
                    aggregate.Durations.Add(http.Duration, weight);
                    if (http.Outcome != ProfilerOutcome.Success || http.StatusCode >= 400)
                    {
                        aggregate.Failures += weight;
                    }

                    if (http.StatusCode is { } status)
                    {
                        aggregate.StatusCodes.TryGetValue(status, out var statusCount);
                        aggregate.StatusCodes[status] = statusCount + weight;
                    }

                    if (http.ResponseBytes is { } bytes)
                    {
                        aggregate.ResponseBytes += bytes;
                    }

                    if (http.FromCache == true)
                    {
                        aggregate.CacheHits += weight;
                    }

                    if (bucket is not null)
                    {
                        bucket.HttpRequests += weight;
                    }

                    break;
                }

            case JSInteropEvent js:
                {
                    var key = string.Concat(js.Direction == InteropDirection.DotNetToJS ? "→ " : "← ", js.Identifier);
                    var aggregate = GetOrAdd(_interop, key, k => new InteropAggregate(js.Direction, js.Identifier));
                    aggregate.Durations.Add(js.Duration, weight);
                    if (js.Outcome != ProfilerOutcome.Success)
                    {
                        aggregate.Failures += weight;
                    }

                    if (js.PayloadBytes is { } payload)
                    {
                        aggregate.PayloadBytes += payload;
                        aggregate.MaxPayloadBytes = Math.Max(aggregate.MaxPayloadBytes, payload);
                    }

                    if (bucket is not null)
                    {
                        bucket.InteropCalls += weight;
                    }

                    break;
                }

            case RuntimeSampleEvent runtime:
                {
                    _lastRuntime = runtime;
                    _runtimeAllocated += runtime.AllocatedBytes ?? 0;
                    _runtimeGen0 += runtime.Gen0Collections ?? 0;
                    _runtimeGen1 += runtime.Gen1Collections ?? 0;
                    _runtimeGen2 += runtime.Gen2Collections ?? 0;
                    _runtimePauseTicks += runtime.GcPauseTicks ?? 0;
                    _runtimeSampleTicks += runtime.Duration;
                    if (bucket is not null)
                    {
                        bucket.AllocatedBytes += runtime.AllocatedBytes ?? 0;
                        bucket.GcCollections += (runtime.Gen0Collections ?? 0) + (runtime.Gen1Collections ?? 0) + (runtime.Gen2Collections ?? 0);
                        bucket.HeapBytes = runtime.HeapSizeBytes ?? runtime.TotalMemoryBytes ?? bucket.HeapBytes;
                    }

                    break;
                }

            case CircuitEvent circuit when circuit.CircuitId is not null:
                {
                    var aggregate = GetCircuit(circuit.CircuitId);
                    switch (circuit.Change)
                    {
                        case CircuitChange.Opened:
                            aggregate.Opened = circuit.Timestamp;
                            break;
                        case CircuitChange.Closed:
                            aggregate.Closed = circuit.Timestamp;
                            break;
                        case CircuitChange.ConnectionDown:
                            aggregate.ConnectionDowns++;
                            aggregate.Connected = false;
                            break;
                        case CircuitChange.ConnectionUp:
                            aggregate.ConnectionUps++;
                            aggregate.Connected = true;
                            break;
                    }

                    break;
                }

            case CircuitActivityEvent activity when activity.CircuitId is not null:
                {
                    var aggregate = GetCircuit(activity.CircuitId);
                    aggregate.InboundActivities.Add(activity.Duration, weight);
                    break;
                }

            case BrowserInteractionEvent interaction:
                _interactions.Add(interaction.Duration, weight);
                break;

            case BrowserLongTaskEvent longTask:
                _longTasks.Add(longTask.Duration, weight);
                break;

            case MarkerEvent { MarkerType: MarkerType.EventsDropped } dropped:
                _droppedReported += dropped.Count;
                break;
        }
    }

    /// <summary>Creates an immutable summary in display units.</summary>
    public SessionSummary CreateSummary()
    {
        var summary = new SessionSummary
        {
            ContainsSampledData = _sampled,
            FirstEventTimestamp = _firstTimestamp == long.MaxValue ? null : _firstTimestamp,
            LastEventTimestamp = _lastTimestamp == long.MinValue ? null : _lastTimestamp,
            Errors = _errors,
            DroppedEventsReported = _droppedReported,
            RenderBatches = new RenderBatchSummary
            {
                Durations = _batches.ToSummary(_clock),
                TotalUpdatedComponents = Math.Round(_batchUpdatedComponents, 2),
                MaxUpdatedComponents = _batchMaxUpdatedComponents,
            },
            BrowserInteractions = _interactions.ToSummary(_clock),
            BrowserLongTasks = _longTasks.ToSummary(_clock),
        };

        for (var i = 0; i < _kindCounts.Length; i++)
        {
            if (_kindCounts[i] > 0)
            {
                summary.EventCounts[((ProfilerEventKind)i).ToString()] = Math.Round(_kindCounts[i], 2);
            }
        }

        foreach (var component in _components.Values)
        {
            summary.Components.Add(component.ToSummary(_clock));
        }

        foreach (var instance in _instances.Values)
        {
            summary.Instances.Add(instance.ToSummary(_clock));
        }

        foreach (var handler in _handlers.Values)
        {
            summary.Handlers.Add(new HandlerSummary
            {
                ComponentType = handler.ComponentType,
                MethodName = handler.MethodName,
                AttributeName = handler.AttributeName,
                Durations = handler.Durations.ToSummary(_clock),
                Errors = handler.Errors,
            });
        }

        foreach (var endpoint in _endpoints.Values)
        {
            summary.Endpoints.Add(new EndpointSummary
            {
                Endpoint = endpoint.Endpoint,
                Method = endpoint.Method,
                Durations = endpoint.Durations.ToSummary(_clock),
                Failures = Math.Round(endpoint.Failures, 2),
                CacheHits = Math.Round(endpoint.CacheHits, 2),
                ResponseBytes = endpoint.ResponseBytes,
                StatusCodes = endpoint.StatusCodes.ToDictionary(p => p.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), p => Math.Round(p.Value, 2)),
            });
        }

        foreach (var interop in _interop.Values)
        {
            summary.Interop.Add(new InteropSummary
            {
                Direction = interop.Direction,
                Identifier = interop.Identifier,
                Durations = interop.Durations.ToSummary(_clock),
                Failures = Math.Round(interop.Failures, 2),
                PayloadBytes = interop.PayloadBytes,
                MaxPayloadBytes = interop.MaxPayloadBytes,
            });
        }

        foreach (var (route, count) in _navigations)
        {
            summary.Navigations.Add(new NavigationSummary { Route = route, Count = Math.Round(count, 2) });
        }

        foreach (var circuit in _circuits.Values)
        {
            summary.Circuits.Add(new CircuitSummary
            {
                CircuitId = circuit.CircuitId,
                Connected = circuit.Connected,
                Closed = circuit.Closed is not null,
                ConnectionDowns = circuit.ConnectionDowns,
                ConnectionUps = circuit.ConnectionUps,
                InboundActivities = circuit.InboundActivities.ToSummary(_clock),
                RenderBatches = circuit.Batches.ToSummary(_clock),
                OpenedTimestamp = circuit.Opened,
                ClosedTimestamp = circuit.Closed,
            });
        }

        summary.Runtime = new RuntimeSummary
        {
            Samples = (long)_kindCounts[(int)ProfilerEventKind.RuntimeSample],
            AllocatedBytes = _runtimeAllocated,
            Gen0Collections = _runtimeGen0,
            Gen1Collections = _runtimeGen1,
            Gen2Collections = _runtimeGen2,
            GcPauseMs = Math.Round(_clock.ToMilliseconds(_runtimePauseTicks), 3),
            SampledMs = Math.Round(_clock.ToMilliseconds(_runtimeSampleTicks), 3),
            HeapSizeBytes = _lastRuntime?.HeapSizeBytes,
            TotalMemoryBytes = _lastRuntime?.TotalMemoryBytes,
            WorkingSetBytes = _lastRuntime?.WorkingSetBytes,
            ThreadPoolThreads = _lastRuntime?.ThreadPoolThreads,
            ThreadPoolQueueLength = _lastRuntime?.ThreadPoolQueueLength,
            ProcessorCount = _lastRuntime?.ProcessorCount,
        };

        var currentSecond = (_lastTimestamp == long.MinValue ? 0 : _lastTimestamp - _startTimestamp) / Math.Max(1, _clock.Frequency);
        foreach (var bucket in _buckets)
        {
            if (bucket is not null && bucket.Second > currentSecond - _buckets.Length)
            {
                summary.TimeSeries.Add(bucket.ToPoint());
            }
        }

        summary.TimeSeries.Sort((a, b) => a.Second.CompareTo(b.Second));
        return summary;
    }

    private TimeSeriesBucket? GetBucket(long timestamp)
    {
        if (timestamp < _startTimestamp)
        {
            return null;
        }

        var second = (timestamp - _startTimestamp) / Math.Max(1, _clock.Frequency);
        var index = (int)(second % _buckets.Length);
        var bucket = _buckets[index];
        if (bucket is null || bucket.Second != second)
        {
            if (bucket is not null && bucket.Second > second)
            {
                // Late event for a bucket that has already been recycled.
                return null;
            }

            bucket = new TimeSeriesBucket { Second = second };
            _buckets[index] = bucket;
        }

        return bucket;
    }

    private ComponentAggregate GetComponent(string? type) =>
        GetOrAdd(_components, type ?? "(unknown)", k => new ComponentAggregate(k));

    private CircuitAggregate GetCircuit(string circuitId) =>
        GetOrAdd(_circuits, circuitId, k => new CircuitAggregate(k));

    private InstanceAggregate? TrackInstance(ComponentEvent e) =>
        e.ComponentInstanceId == 0 ? null : GetInstance(e.ComponentInstanceId, e.ComponentType);

    private InstanceAggregate? GetInstance(long id, string? type)
    {
        if (_instances.TryGetValue(id, out var instance))
        {
            return instance;
        }

        if (_instances.Count >= _maxInstances)
        {
            // Prefer evicting a disposed instance; otherwise stop tracking new instances.
            long? victim = null;
            foreach (var (key, value) in _instances)
            {
                if (value.Disposed is not null)
                {
                    victim = key;
                    break;
                }
            }

            if (victim is null)
            {
                return null;
            }

            _instances.Remove(victim.Value);
        }

        instance = new InstanceAggregate(id, type);
        _instances[id] = instance;
        return instance;
    }

    private TValue GetOrAdd<TValue>(Dictionary<string, TValue> map, string key, Func<string, TValue> factory)
    {
        if (map.TryGetValue(key, out var value))
        {
            return value;
        }

        if (map.Count >= _maxKeys)
        {
            key = OverflowKey;
            if (map.TryGetValue(key, out value))
            {
                return value;
            }
        }

        value = factory(key);
        map[key] = value;
        return value;
    }

    private sealed class ComponentAggregate(string type)
    {
        public string Type { get; } = type;
        public bool Instrumented { get; set; }
        public double Created { get; set; }
        public double Disposed { get; set; }
        public DurationStats Renders { get; } = new();
        public double RendersWithUnchangedOutput { get; set; }
        public double RendersWithOutputComparison { get; set; }
        public DurationStats ParametersSets { get; } = new();
        public double ParametersUnchanged { get; set; }
        public double ParametersChangedByReferenceOnly { get; set; }
        public Dictionary<string, double> ReferenceOnlyParameterNames { get; } = new(StringComparer.Ordinal);
        public double RenderRequestsCoalesced { get; set; }
        public double RenderRequestsSuppressed { get; set; }
        public DurationStats EventHandlers { get; } = new();
        public Dictionary<LifecycleMethod, DurationStats> Lifecycle { get; } = [];
        public long Errors { get; set; }

        public ComponentSummary ToSummary(IProfilerClock clock) => new()
        {
            ComponentType = Type,
            Instrumented = Instrumented,
            Created = Math.Round(Created, 2),
            Disposed = Math.Round(Disposed, 2),
            Renders = Renders.ToSummary(clock),
            RendersWithUnchangedOutput = Math.Round(RendersWithUnchangedOutput, 2),
            RendersWithOutputComparison = Math.Round(RendersWithOutputComparison, 2),
            ParametersSets = ParametersSets.ToSummary(clock),
            ParametersUnchanged = Math.Round(ParametersUnchanged, 2),
            ParametersChangedByReferenceOnly = Math.Round(ParametersChangedByReferenceOnly, 2),
            ReferenceOnlyParameters = ReferenceOnlyParameterNames.ToDictionary(p => p.Key, p => Math.Round(p.Value, 2), StringComparer.Ordinal),
            RenderRequestsCoalesced = Math.Round(RenderRequestsCoalesced, 2),
            RenderRequestsSuppressed = Math.Round(RenderRequestsSuppressed, 2),
            EventHandlers = EventHandlers.ToSummary(clock),
            Lifecycle = Lifecycle.ToDictionary(p => p.Key.ToString(), p => p.Value.ToSummary(clock), StringComparer.Ordinal),
            Errors = Errors,
        };
    }

    private sealed class InstanceAggregate(long id, string? type)
    {
        public long Id { get; } = id;
        public string? Type { get; } = type;
        public long? Created { get; private set; }
        public long? Disposed { get; private set; }
        public long ParentInstanceId { get; set; }
        public int Renders { get; set; }
        public long RenderTicks { get; set; }
        public int RendersWithUnchangedOutput { get; set; }
        public int ParametersSets { get; set; }
        public int UnchangedParametersSets { get; set; }
        public long? LastRender { get; set; }

        public void SetCreated(long timestamp) => Created = timestamp;

        public void SetDisposed(long timestamp) => Disposed = timestamp;

        public ComponentInstanceSummary ToSummary(IProfilerClock clock) => new()
        {
            InstanceId = Id,
            ComponentType = Type,
            ParentInstanceId = ParentInstanceId,
            CreatedTimestamp = Created,
            DisposedTimestamp = Disposed,
            Renders = Renders,
            RenderMs = Math.Round(clock.ToMilliseconds(RenderTicks), 3),
            RendersWithUnchangedOutput = RendersWithUnchangedOutput,
            ParametersSets = ParametersSets,
            UnchangedParametersSets = UnchangedParametersSets,
            LastRenderTimestamp = LastRender,
        };
    }

    private sealed class HandlerAggregate(string? componentType, string? methodName, string? attributeName)
    {
        public string? ComponentType { get; } = componentType;
        public string? MethodName { get; } = methodName;
        public string? AttributeName { get; } = attributeName;
        public DurationStats Durations { get; } = new();
        public long Errors { get; set; }
    }

    private sealed class EndpointAggregate(string endpoint, string? method)
    {
        public string Endpoint { get; } = endpoint;
        public string? Method { get; } = method;
        public DurationStats Durations { get; } = new();
        public double Failures { get; set; }
        public double CacheHits { get; set; }
        public long ResponseBytes { get; set; }
        public Dictionary<int, double> StatusCodes { get; } = [];
    }

    private sealed class InteropAggregate(InteropDirection direction, string? identifier)
    {
        public InteropDirection Direction { get; } = direction;
        public string? Identifier { get; } = identifier;
        public DurationStats Durations { get; } = new();
        public double Failures { get; set; }
        public long PayloadBytes { get; set; }
        public long MaxPayloadBytes { get; set; }
    }

    private sealed class CircuitAggregate(string circuitId)
    {
        public string CircuitId { get; } = circuitId;
        public long? Opened { get; set; }
        public long? Closed { get; set; }
        public bool Connected { get; set; } = true;
        public int ConnectionDowns { get; set; }
        public int ConnectionUps { get; set; }
        public DurationStats InboundActivities { get; } = new();
        public DurationStats Batches { get; } = new();
    }

    private sealed class TimeSeriesBucket
    {
        public long Second { get; init; }
        public double RenderBatches { get; set; }
        public double UpdatedComponents { get; set; }
        public double RenderBatchMs { get; set; }
        public double ComponentRenders { get; set; }
        public double ParametersSets { get; set; }
        public double EventHandlers { get; set; }
        public double EventHandlerMs { get; set; }
        public double HttpRequests { get; set; }
        public double InteropCalls { get; set; }
        public long AllocatedBytes { get; set; }
        public int GcCollections { get; set; }
        public long? HeapBytes { get; set; }
        public int Errors { get; set; }

        public TimeSeriesPoint ToPoint() => new()
        {
            Second = Second,
            RenderBatches = Math.Round(RenderBatches, 2),
            UpdatedComponents = Math.Round(UpdatedComponents, 2),
            RenderBatchMs = Math.Round(RenderBatchMs, 3),
            ComponentRenders = Math.Round(ComponentRenders, 2),
            ParametersSets = Math.Round(ParametersSets, 2),
            EventHandlers = Math.Round(EventHandlers, 2),
            EventHandlerMs = Math.Round(EventHandlerMs, 3),
            HttpRequests = Math.Round(HttpRequests, 2),
            InteropCalls = Math.Round(InteropCalls, 2),
            AllocatedBytes = AllocatedBytes,
            GcCollections = GcCollections,
            HeapBytes = HeapBytes,
            Errors = Errors,
        };
    }
}
