using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;

namespace BlazorProfiler.Collectors;

/// <summary>
/// Looks for the usual retainers of a component instance that stayed reachable after it was released, using reflection
/// over objects the component itself references. It finds:
/// <list type="bullet">
/// <item>delegates targeting the component (directly or through a closure) in events, delegate fields, and delegate
/// collections of services the component references (<c>[Inject]</c>, <c>[CascadingParameter]</c>, or constructor-injected),
/// the classic <c>AppState.OnChange += StateHasChanged</c> without unsubscribing;</item>
/// <item>delegates targeting the component in static fields of the component's assembly (types without a static
/// constructor, so inspection never runs app code);</item>
/// <item><c>DotNetObjectReference</c> instances the component holds that were not disposed (tentative: the JavaScript
/// runtime keeps them alive only once they were passed to JavaScript);</item>
/// <item>timers the component holds (an active timer is rooted by the runtime's timer queue and keeps its callback target
/// alive).</item>
/// </list>
/// </summary>
/// <remarks>
/// This is a heuristic with bounded cost: it never walks the heap, never enumerates arbitrary sequences (only arrays and
/// framework collections), and inspects at most <see cref="MaxObjects"/> objects. Missing members (trimming) only reduce
/// what it finds. A heap dump remains the authoritative way to find a retaining path.
/// </remarks>
internal static class RetainerInspector
{
    /// <summary>
    /// The maximum number of objects inspected per component (fields of the component and of the services it references;
    /// each framework collection found contributes at most 1,000 items more).
    /// </summary>
    public const int MaxObjects = 256;

    /// <summary>The maximum number of hints returned.</summary>
    public const int MaxHints = 8;

    /// <summary>
    /// The prefix of tentative hints: references that retain the instance only under a condition the inspector cannot check
    /// (for example a timer that may already be disposed). See <see cref="Events.ComponentRetainedEvent.IsTentative"/>.
    /// </summary>
    public const string PossiblePrefix = Events.ComponentRetainedEvent.TentativePrefix;

    private const int MaxCollectionItems = 1_000;
    private const int MaxStaticFields = 5_000;
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly ConditionalWeakTable<Assembly, FieldInfo[]> s_staticDelegateFields = [];

    /// <summary>Returns short descriptions of likely retainers of <paramref name="component"/>, or null when none were found.</summary>
    /// <param name="component">The released instance.</param>
    /// <param name="isService">
    /// Whether a type is registered in dependency injection. Objects the component references are inspected for delegates
    /// only when they came from outside the component: <c>[Inject]</c> and <c>[CascadingParameter]</c> properties, and
    /// fields whose type is a registered service (constructor injection). Objects the component created itself are skipped:
    /// a subscription to them is a reference cycle that the garbage collector reclaims, not a leak.
    /// </param>
    public static string[]? Find(object component, Func<Type, bool>? isService = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        var hints = new List<string>();
        try
        {
            var componentType = component.GetType();
            var componentName = Short(componentType);
            var budget = new Budget();
            var external = GetExternalFields(componentType);

            // 1. Objects the component references directly (injected services and cascading values; timers and
            // DotNetObjectReference instances it owns).
            foreach (var (field, value) in GetFieldValues(component, budget))
            {
                if (hints.Count >= MaxHints)
                {
                    break;
                }

                switch (value)
                {
                    case System.Timers.Timer { Enabled: true }:
                        hints.Add($"{componentName}.{FieldName(field)} (System.Timers.Timer) is still enabled — a running timer is rooted by the runtime and keeps its Elapsed handlers alive; stop and dispose it in Dispose");
                        continue;
                    case Timer or PeriodicTimer:
                        // Whether these timers were disposed cannot be read through public APIs, so the hint is tentative.
                        hints.Add($"{PossiblePrefix}{componentName}.{FieldName(field)} ({Short(value.GetType())}) — if it was not disposed, the runtime's timer queue keeps its callback target alive; dispose it in Dispose");
                        continue;
                    case System.Timers.Timer:
                    case Delegate:
                    case string:
                        continue;
                }

                if (IsUndisposedDotNetObjectReference(value, component))
                {
                    // Tentative: only a reference that was passed to JavaScript is tracked (and kept alive) by the JS runtime.
                    hints.Add($"{PossiblePrefix}{componentName}.{FieldName(field)} (DotNetObjectReference) was not disposed — once passed to JavaScript, the JS runtime keeps it and the component alive; dispose it in Dispose");
                    continue;
                }

                if (value.GetType().IsValueType || ReferenceEquals(value, component) || IsFrameworkInternal(value.GetType()))
                {
                    continue;
                }

                if (!external.Contains(field.Name) && !(isService?.Invoke(field.FieldType) ?? false))
                {
                    // Created by the component itself: a subscription to it is a collectable cycle.
                    continue;
                }

                // Delegates on the referenced object (events and delegate fields) that point back to the component.
                foreach (var hint in FindDelegatesTargeting(value, component, budget))
                {
                    hints.Add($"{Short(value.GetType())}.{hint.Member} → {componentName}.{hint.Method}{(hint.ViaClosure ? " (lambda)" : string.Empty)} — subscribed by the component and never removed; unsubscribe in Dispose");
                    if (hints.Count >= MaxHints)
                    {
                        break;
                    }
                }
            }

            // 2. Static delegates in the component's assembly (static events, static callbacks). Separate budget: the
            // static fields are few and cheap to read, and must not be skipped because step 1 used the budget.
            if (hints.Count < MaxHints)
            {
                foreach (var field in GetStaticDelegateFields(componentType.Assembly))
                {
                    if (hints.Count >= MaxHints)
                    {
                        break;
                    }

                    object? value;
                    try
                    {
                        value = field.GetValue(null);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (value is Delegate d && Targets(d, component, out var method, out var viaClosure))
                    {
                        hints.Add($"static {Short(field.DeclaringType!)}.{FieldName(field)} → {componentName}.{method}{(viaClosure ? " (lambda)" : string.Empty)} — static events keep handlers alive for the lifetime of the app; unsubscribe in Dispose");
                    }
                }
            }
        }
        catch (Exception)
        {
            // Best effort: inspection must never fail the leak check.
        }

        return hints.Count == 0 ? null : [.. hints.Distinct(StringComparer.Ordinal)];
    }

    private static IEnumerable<(FieldInfo Field, object Value)> GetFieldValues(object instance, Budget budget)
    {
        foreach (var field in GetInstanceFields(instance.GetType()))
        {
            if (budget.Exhausted)
            {
                yield break;
            }

            object? value;
            try
            {
                value = field.GetValue(instance);
            }
            catch (Exception)
            {
                continue;
            }

            if (value is not null)
            {
                budget.Objects++;
                yield return (field, value);
            }
        }
    }

    private static IEnumerable<(string Member, string Method, bool ViaClosure)> FindDelegatesTargeting(object owner, object component, Budget budget)
    {
        foreach (var (field, value) in GetFieldValues(owner, budget))
        {
            switch (value)
            {
                case Delegate d when Targets(d, component, out var method, out var viaClosure):
                    yield return (FieldName(field), method, viaClosure);
                    break;
                case Array array when array.GetType().GetElementType() is { } element && typeof(Delegate).IsAssignableFrom(element):
                    foreach (var match in FromSequence(array, field, component, budget))
                    {
                        yield return match;
                    }

                    break;
                case IEnumerable collection when value is not string && IsFrameworkCollection(value.GetType()):
                    foreach (var match in FromSequence(collection, field, component, budget))
                    {
                        yield return match;
                    }

                    break;
            }
        }
    }

    private static IEnumerable<(string Member, string Method, bool ViaClosure)> FromSequence(IEnumerable items, FieldInfo field, object component, Budget budget)
    {
        var matches = new List<(string, string, bool)>();
        try
        {
            var count = 0;
            foreach (var item in items)
            {
                if (++count > MaxCollectionItems || budget.Exhausted)
                {
                    break;
                }

                var candidate = item switch
                {
                    DictionaryEntry entry => entry.Value,
                    not null when item.GetType() is { IsGenericType: true } t && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) => KeyValuePairValue(item),
                    _ => item,
                };
                if (candidate is Delegate d && Targets(d, component, out var method, out var viaClosure))
                {
                    matches.Add((FieldName(field) + "[]", method, viaClosure));
                    break;
                }
            }
        }
        catch (Exception)
        {
            // The collection was modified concurrently, or its enumerator failed: skip it.
        }

        return matches;
    }

    private static bool Targets(Delegate value, object component, out string method, out bool viaClosure)
    {
        foreach (var d in value.GetInvocationList())
        {
            var target = d.Target;
            if (target is null)
            {
                continue;
            }

            if (ReferenceEquals(target, component))
            {
                method = d.Method.Name;
                viaClosure = false;
                return true;
            }

            // A lambda that captured the component: the target is a compiler-generated closure with a field holding it.
            if (target.GetType().IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || target.GetType().Name.Contains("DisplayClass", StringComparison.Ordinal))
            {
                foreach (var field in GetInstanceFields(target.GetType()))
                {
                    object? captured;
                    try
                    {
                        captured = field.GetValue(target);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (ReferenceEquals(captured, component))
                    {
                        method = CleanLambdaName(d.Method.Name);
                        viaClosure = true;
                        return true;
                    }
                }
            }
        }

        method = string.Empty;
        viaClosure = false;
        return false;
    }

    private static bool IsUndisposedDotNetObjectReference(object value, object component)
    {
        var type = value.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition().FullName != "Microsoft.JSInterop.DotNetObjectReference`1")
        {
            return false;
        }

        try
        {
            // Value throws ObjectDisposedException once the reference is disposed. Only references to the component itself
            // (or its own helper objects) matter; the check is cheap either way.
            var target = GetValueProperty(type).GetValue(value);
            return target is not null && (ReferenceEquals(target, component) || !IsFrameworkInternal(target.GetType()));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is ObjectDisposedException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "KeyValuePair<TKey, TValue>.Value is public and always preserved with the type.")]
    private static object? KeyValuePairValue(object pair)
    {
        try
        {
            return pair.GetType().GetProperty("Value")?.GetValue(pair);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The backing fields of <c>[Inject]</c> and <c>[CascadingParameter]</c> properties: references from outside.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Blazor preserves the properties of component types.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Blazor preserves the properties of component types and their base types.")]
    private static HashSet<string> GetExternalFields(Type type)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            try
            {
                foreach (var property in current.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (property.IsDefined(typeof(InjectAttribute), inherit: true) || property.IsDefined(typeof(CascadingParameterAttribute), inherit: true))
                    {
                        fields.Add($"<{property.Name}>k__BackingField");
                    }
                }
            }
            catch (Exception)
            {
                // Best effort.
            }
        }

        return fields;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "DotNetObjectReference<T>.Value is public and used by the JS interop serializer, so it is preserved.")]
    private static PropertyInfo GetValueProperty(Type type) => type.GetProperty("Value")!;

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Best-effort diagnostics: fields removed by trimming are simply not inspected.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Best-effort diagnostics: fields removed by trimming are simply not inspected.")]
    private static IEnumerable<FieldInfo> GetInstanceFields(Type type)
    {
        for (var current = type; current is not null && current != typeof(object) && current != typeof(ComponentBase); current = current.BaseType)
        {
            if (current.Assembly == typeof(RetainerInspector).Assembly)
            {
                // ProfiledComponentBase's own fields are profiler state, not retainers.
                break;
            }

            FieldInfo[] fields;
            try
            {
                fields = current.GetFields(InstanceFields);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var field in fields)
            {
                if (!field.FieldType.IsPrimitive && !field.FieldType.IsEnum && field.FieldType != typeof(string))
                {
                    yield return field;
                }
            }
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Best-effort diagnostics: types removed by trimming are simply not inspected.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Best-effort diagnostics: fields removed by trimming are simply not inspected.")]
    private static FieldInfo[] GetStaticDelegateFields(Assembly assembly) =>
        s_staticDelegateFields.GetValue(assembly, static a =>
        {
            var result = new List<FieldInfo>();
            Type[] types;
            try
            {
                types = a.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.Where(t => t is not null)!];
            }
            catch (Exception)
            {
                return [];
            }

            foreach (var type in types)
            {
                // Reading a static field can run the type's static constructor, which would change when the app's own code
                // runs (and a failing one would break the type for good), so types with one are skipped. Compiler-generated
                // lambda caches are not retainers worth reporting.
                if (type.ContainsGenericParameters || type.TypeInitializer is not null || type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || type.Name.StartsWith('<'))
                {
                    continue;
                }

                try
                {
                    foreach (var field in type.GetFields(StaticFields))
                    {
                        if (typeof(Delegate).IsAssignableFrom(field.FieldType) && !field.IsLiteral)
                        {
                            result.Add(field);
                            if (result.Count >= MaxStaticFields)
                            {
                                return [.. result];
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Skip types whose fields cannot be read.
                }
            }

            return [.. result];
        });

    private static bool IsFrameworkInternal(Type type)
    {
        var ns = type.Namespace ?? string.Empty;
        return ns.StartsWith("System", StringComparison.Ordinal)
            || ns.StartsWith("Microsoft.AspNetCore.Components.Rendering", StringComparison.Ordinal)
            || ns.StartsWith("Microsoft.AspNetCore.Components.RenderTree", StringComparison.Ordinal)
            || type.Assembly == typeof(RetainerInspector).Assembly
            || type.Assembly == typeof(ProfilerHost).Assembly;
    }

    private static bool IsFrameworkCollection(Type type) =>
        type.Namespace is "System.Collections.Generic" or "System.Collections.Concurrent" or "System.Collections.Immutable" or "System.Collections.ObjectModel";

    private static string FieldName(FieldInfo field)
    {
        var name = field.Name;

        // Auto-property and field-like event backing fields: <Name>k__BackingField.
        if (name.StartsWith('<') && name.IndexOf('>', StringComparison.Ordinal) is var end and > 1)
        {
            return name[1..end];
        }

        return name;
    }

    private static string CleanLambdaName(string name)
    {
        // <OnInitialized>b__3_0 → OnInitialized
        if (name.StartsWith('<') && name.IndexOf('>', StringComparison.Ordinal) is var end and > 1)
        {
            return name[1..end];
        }

        return name;
    }

    private static string Short(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick > 0 ? name[..tick] : name;
    }

    private sealed class Budget
    {
        public int Objects;

        public bool Exhausted => Objects >= MaxObjects;
    }
}
