using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using BlazorProfiler.Collectors;
using BlazorProfiler.Events;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

[assembly: MetadataUpdateHandler(typeof(BlazorProfiler.Instrumentation.ProfilerHotReloadHandler))]

namespace BlazorProfiler.Instrumentation;

/// <summary>Assigns stable, process-unique identifiers to component instances without keeping them alive.</summary>
public sealed class ComponentInstanceRegistry
{
    private readonly ConditionalWeakTable<object, InstanceInfo> _instances = [];
    private readonly ConcurrentDictionary<Type, string> _typeNames = new();
    private readonly ConcurrentDictionary<string, byte> _instrumentedTypes = new(StringComparer.Ordinal);
    private long _nextId;

    /// <summary>Returns the identifier of a component instance, assigning one on first use.</summary>
    public long GetOrAssign(object component) => GetInfo(component).Id;

    /// <summary>Returns the identifier of a component instance if one has been assigned.</summary>
    public bool TryGet(object component, out long id)
    {
        if (_instances.TryGetValue(component, out var info))
        {
            id = info.Id;
            return true;
        }

        id = 0;
        return false;
    }

    /// <summary>Returns the creation timestamp recorded for an instance, or zero.</summary>
    public long GetCreatedTimestamp(object component) => _instances.TryGetValue(component, out var info) ? info.CreatedTimestamp : 0;

    /// <summary>Returns the cached full name of a type.</summary>
    public string GetTypeName(Type type) => _typeNames.GetOrAdd(type, static t => t.FullName ?? t.Name);

    /// <summary>Records that a component type is instrumented, so framework metrics for it are not ingested twice.</summary>
    public void MarkInstrumented(string typeName) => _instrumentedTypes.TryAdd(typeName, 0);

    /// <summary>Returns whether a component type is instrumented.</summary>
    public bool IsInstrumented(string? typeName) => typeName is not null && !_instrumentedTypes.IsEmpty && _instrumentedTypes.ContainsKey(typeName);

    internal InstanceInfo Register(object component, long timestamp)
    {
        var info = GetInfo(component);
        info.CreatedTimestamp = timestamp;
        return info;
    }

    private InstanceInfo GetInfo(object component) =>
        _instances.GetValue(component, _ => new InstanceInfo(Interlocked.Increment(ref _nextId)));

    internal sealed class InstanceInfo(long id)
    {
        public long Id { get; } = id;

        public long CreatedTimestamp { get; set; }
    }
}

/// <summary>Reports the state of opt-in component instrumentation (<see cref="ProfiledComponentBase"/>).</summary>
public sealed class ComponentInstrumentationCollector : ProfilerCollectorBase
{
    /// <inheritdoc />
    public override CollectorDescriptor Descriptor { get; } = new()
    {
        Name = "component-instrumentation",
        Description = "Per-instance render, lifecycle, parameter-change, render-request, and component-tree data from components inheriting ProfiledComponentBase.",
        Mechanism = "Opt-in base class reimplementing ComponentBase semantics with timing hooks; render-output hashing via RenderTreeBuilder.GetFrames().",
        Capability = CapabilityLevel.Full,
        Overhead = OverheadLevel.Moderate,
        Compatibility = "Any hosting model. Only components that inherit ProfiledComponentBase (for example through @inherits in _Imports.razor) are instrumented.",
        Degradation = "Components that do not inherit ProfiledComponentBase are profiled through framework metrics only (per-type parameter updates; no per-instance renders).",
        OptIn = true,
    };

    /// <summary>Whether instrumentation should record.</summary>
    public bool IsCollecting => IsActive;

    /// <summary>Emits an event.</summary>
    public bool Publish(ProfilerEvent profilerEvent) => Emit(profilerEvent);

    /// <inheritdoc />
    protected override void OnStart(ProfilerCollectorContext context)
    {
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
    }
}

/// <summary>Services used by <see cref="ProfiledComponentBase"/>.</summary>
public sealed class ComponentInstrumentation
{
    /// <summary>Creates the service.</summary>
    public ComponentInstrumentation(IProfiler profiler, ComponentInstanceRegistry registry, ComponentInstrumentationCollector collector)
        : this(profiler, registry, collector, null)
    {
    }

    /// <summary>Creates the instrumentation context with retention tracking for disposed instances.</summary>
    public ComponentInstrumentation(IProfiler profiler, ComponentInstanceRegistry registry, ComponentInstrumentationCollector collector, ComponentRetentionCollector? retention)
    {
        Profiler = profiler;
        Registry = registry;
        Collector = collector;
        Retention = retention;
    }

    /// <summary>The profiler.</summary>
    public IProfiler Profiler { get; }

    /// <summary>The instance registry.</summary>
    public ComponentInstanceRegistry Registry { get; }

    /// <summary>The collector used for health and emission.</summary>
    public ComponentInstrumentationCollector Collector { get; }

    /// <summary>Watches disposed instances for retention (memory leaks), when enabled.</summary>
    public ComponentRetentionCollector? Retention { get; }

    /// <summary>The instrumentation options.</summary>
    public InstrumentationOptions Options => Profiler.Options.Instrumentation;

    /// <summary>Whether instrumentation should record right now.</summary>
    public bool IsActive => Collector.IsCollecting;
}

/// <summary>Reports the state of component-creation tracking.</summary>
public sealed class ComponentActivationCollector : ProfilerCollectorBase
{
    /// <inheritdoc />
    public override CollectorDescriptor Descriptor { get; } = new()
    {
        Name = "component-activation",
        Description = "Component instance creation with type and stable instance identity.",
        Mechanism = "A decorating Microsoft.AspNetCore.Components.IComponentActivator registered in dependency injection.",
        Capability = CapabilityLevel.Full,
        Overhead = OverheadLevel.Low,
        Compatibility = "All hosting models (.NET 8+ IComponentActivator). Wraps an application-registered activator if present.",
        Degradation = "When disabled, components are still created by the original activator; creation events are not recorded.",
    };

    /// <summary>Whether creation events should be recorded.</summary>
    public bool IsCollecting => IsActive;

    /// <summary>Emits an event.</summary>
    public bool Publish(ProfilerEvent profilerEvent) => Emit(profilerEvent);

    /// <inheritdoc />
    protected override bool IsEnabled(ProfilerOptions options) => options.Collectors.ComponentActivator;

    /// <inheritdoc />
    protected override void OnStart(ProfilerCollectorContext context) => ProfilerHotReloadHandler.Updated += OnHotReload;

    /// <inheritdoc />
    protected override void OnStop() => ProfilerHotReloadHandler.Updated -= OnHotReload;

    // Renders right after a hot reload update re-render the affected components; a marker keeps them from being mistaken
    // for application behavior.
    private void OnHotReload(Type[]? updatedTypes)
    {
        if (Profiler is { } profiler)
        {
            Emit(new MarkerEvent
            {
                MarkerType = MarkerType.Custom,
                Name = updatedTypes is { Length: > 0 } ? $"Hot reload ({updatedTypes.Length} type(s): {string.Join(", ", updatedTypes.Take(3).Select(t => t.Name))})" : "Hot reload",
                Timestamp = profiler.Clock.GetTimestamp(),
            });
        }
    }
}

/// <summary>
/// An <see cref="IComponentActivator"/> that records component creation and then delegates to the application's activator
/// or reproduces the framework's default behavior (constructor injection through <see cref="ActivatorUtilities"/>).
/// </summary>
public sealed class ProfilerComponentActivator : IComponentActivator, IDisposable
{
    /// <summary>The maximum number of instances per scope handed to retention tracking when the scope ends.</summary>
    public const int MaxTrackedPerScope = 20_000;

    private static readonly ConcurrentDictionary<Type, ObjectFactory> s_factories = new();

    private readonly IServiceProvider _services;
    private readonly IComponentActivator? _inner;
    private readonly ComponentInstanceRegistry _registry;
    private readonly ComponentActivationCollector _collector;
    private readonly IProfiler _profiler;
    private readonly ComponentRetentionCollector? _retention;
    private readonly object _scopeLock = new();
    private List<ScopeInstance>? _scopeInstances;
    private string? _circuitId;
    private bool _disposed;
    private bool _scopeFull;

    /// <summary>Creates the activator.</summary>
    public ProfilerComponentActivator(IServiceProvider services, IComponentActivator? inner, ComponentInstanceRegistry registry, ComponentActivationCollector collector, IProfiler profiler)
    {
        _services = services;
        _inner = inner;
        _registry = registry;
        _collector = collector;
        _profiler = profiler;
        (profiler as ProfilerHost)?.Start();
    }

    /// <summary>
    /// Creates the activator. When <paramref name="retention"/> is given, the activator must be scoped: when its scope ends
    /// (a Blazor Server circuit closes, or a static rendering request completes) every instance it created is watched for
    /// retention.
    /// </summary>
    public ProfilerComponentActivator(IServiceProvider services, IComponentActivator? inner, ComponentInstanceRegistry registry, ComponentActivationCollector collector, IProfiler profiler, ComponentRetentionCollector? retention)
        : this(services, inner, registry, collector, profiler)
    {
        _retention = retention;
    }

    /// <inheritdoc />
    public IComponent CreateInstance([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type componentType)
    {
        IComponent component;
        if (_inner is not null)
        {
            component = _inner.CreateInstance(componentType);
        }
        else
        {
            if (!typeof(IComponent).IsAssignableFrom(componentType))
            {
                throw new ArgumentException($"The type {componentType.FullName} does not implement {nameof(IComponent)}.", nameof(componentType));
            }

            component = (IComponent)GetFactory(componentType)(_services, []);
        }

        if (_collector.IsCollecting)
        {
            try
            {
                var timestamp = _profiler.Clock.GetTimestamp();
                var info = _registry.Register(component, timestamp);
                var typeName = _registry.GetTypeName(component.GetType());
                _collector.Publish(new ComponentCreatedEvent
                {
                    ComponentType = typeName,
                    ComponentInstanceId = info.Id,
                    Timestamp = timestamp,
                    Source = InstrumentationSource.ComponentActivator,
                });
                TrackForScope(component, typeName, info.Id);
            }
            catch (Exception)
            {
                // Never let profiling affect component creation.
            }
        }

        return component;
    }

    /// <summary>Hands the instances created in this scope that are still alive to retention tracking.</summary>
    public void Dispose()
    {
        List<ScopeInstance>? instances;
        lock (_scopeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            instances = _scopeInstances;
            _scopeInstances = null;
        }

        if (instances is null || _retention is not { IsCollecting: true } retention)
        {
            return;
        }

        foreach (var instance in instances)
        {
            if (instance.Reference.Target is { } target)
            {
                retention.Watch(target, instance.ComponentType, instance.InstanceId, RetentionReason.ScopeEnded, _circuitId);
            }
        }
    }

    /// <summary>Clears cached factories (after a hot reload update).</summary>
    internal static void ClearFactories() => s_factories.Clear();

    private void TrackForScope(IComponent component, string typeName, long instanceId)
    {
        // In WebAssembly the application's scope lasts as long as the tab, so its instances would never be checked.
        if (_scopeFull || OperatingSystem.IsBrowser() || _retention is not { IsCollecting: true })
        {
            return;
        }

        // Instances are created on the scope's renderer, where the circuit (if any) can be resolved.
        _circuitId ??= ComponentRetentionCollector.CurrentCircuitId(_profiler);
        lock (_scopeLock)
        {
            if (_disposed)
            {
                return;
            }

            _scopeInstances ??= [];
            if (_scopeInstances.Count >= MaxTrackedPerScope)
            {
                // Long-lived scopes (large circuits): forget instances that were already collected; if that frees nothing,
                // stop tracking this scope instead of scanning the list again for every new instance.
                _scopeInstances.RemoveAll(i => !i.Reference.IsAlive);
                if (_scopeInstances.Count >= MaxTrackedPerScope)
                {
                    _scopeFull = true;
                    return;
                }
            }

            _scopeInstances.Add(new ScopeInstance(new WeakReference(component), typeName, instanceId));
        }
    }

    private static ObjectFactory GetFactory([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type componentType)
    {
        if (!s_factories.TryGetValue(componentType, out var factory))
        {
            factory = ActivatorUtilities.CreateFactory(componentType, Type.EmptyTypes);
            s_factories.TryAdd(componentType, factory);
        }

        return factory;
    }

    private sealed record ScopeInstance(WeakReference Reference, string ComponentType, long InstanceId);
}

/// <summary>Clears profiler caches after a hot reload update. Invoked by the runtime.</summary>
internal static class ProfilerHotReloadHandler
{
    /// <summary>Raised after a hot reload update was applied, with the updated types (if known).</summary>
    public static event Action<Type[]?>? Updated;

    /// <summary>Clears caches.</summary>
    public static void ClearCache(Type[]? updatedTypes) => ProfilerComponentActivator.ClearFactories();

    /// <summary>Notifies listeners after the update was applied. Invoked by the runtime.</summary>
    public static void UpdateApplication(Type[]? updatedTypes) => Updated?.Invoke(updatedTypes);
}
