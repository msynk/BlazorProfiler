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
    {
        Profiler = profiler;
        Registry = registry;
        Collector = collector;
    }

    /// <summary>The profiler.</summary>
    public IProfiler Profiler { get; }

    /// <summary>The instance registry.</summary>
    public ComponentInstanceRegistry Registry { get; }

    /// <summary>The collector used for health and emission.</summary>
    public ComponentInstrumentationCollector Collector { get; }

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
    protected override void OnStart(ProfilerCollectorContext context)
    {
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
    }
}

/// <summary>
/// An <see cref="IComponentActivator"/> that records component creation and then delegates to the application's activator
/// or reproduces the framework's default behavior (constructor injection through <see cref="ActivatorUtilities"/>).
/// </summary>
public sealed class ProfilerComponentActivator : IComponentActivator
{
    private static readonly ConcurrentDictionary<Type, ObjectFactory> s_factories = new();

    private readonly IServiceProvider _services;
    private readonly IComponentActivator? _inner;
    private readonly ComponentInstanceRegistry _registry;
    private readonly ComponentActivationCollector _collector;
    private readonly IProfiler _profiler;

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
                _collector.Publish(new ComponentCreatedEvent
                {
                    ComponentType = _registry.GetTypeName(component.GetType()),
                    ComponentInstanceId = info.Id,
                    Timestamp = timestamp,
                    Source = InstrumentationSource.ComponentActivator,
                });
            }
            catch (Exception)
            {
                // Never let profiling affect component creation.
            }
        }

        return component;
    }

    /// <summary>Clears cached factories (after a hot reload update).</summary>
    internal static void ClearFactories() => s_factories.Clear();

    private static ObjectFactory GetFactory([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type componentType)
    {
        if (!s_factories.TryGetValue(componentType, out var factory))
        {
            factory = ActivatorUtilities.CreateFactory(componentType, Type.EmptyTypes);
            s_factories.TryAdd(componentType, factory);
        }

        return factory;
    }
}

/// <summary>Clears profiler caches after a hot reload update. Invoked by the runtime.</summary>
internal static class ProfilerHotReloadHandler
{
    /// <summary>Clears caches.</summary>
    public static void ClearCache(Type[]? updatedTypes) => ProfilerComponentActivator.ClearFactories();
}
