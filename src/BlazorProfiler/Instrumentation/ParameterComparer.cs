namespace BlazorProfiler.Instrumentation;

/// <summary>Result of comparing a parameter value with its previous value.</summary>
internal enum ParameterComparison
{
    /// <summary>The values are equal (same reference or equal immutable values).</summary>
    Equal,
    /// <summary>The value changed.</summary>
    Changed,
    /// <summary>The value is a different instance of a type the framework cannot compare by value.</summary>
    DifferentInstance,
}

/// <summary>Compares parameter values using the same rules as Blazor's change detection.</summary>
/// <remarks>
/// Blazor compares a parameter by value only when its type is "known immutable": any type whose <see cref="TypeCode"/> is not
/// <see cref="TypeCode.Object"/> (primitives, strings, enums, <see cref="decimal"/>, <see cref="DateTime"/>), <see cref="Guid"/>,
/// <see cref="DateOnly"/>, <see cref="TimeOnly"/>, and <c>EventCallback</c>/<c>EventCallback&lt;T&gt;</c>. Every other value is
/// treated as possibly changed, so the child re-renders. This comparer reports such values as
/// <see cref="ParameterComparison.DifferentInstance"/> unless they are the same reference (or an equal value type is known to
/// differ), so callers can distinguish "a value changed" from "the framework cannot tell".
/// </remarks>
internal static class ParameterComparer
{
    public static ParameterComparison Compare(object? previous, object? current)
    {
        if (ReferenceEquals(previous, current))
        {
            return ParameterComparison.Equal;
        }

        if (previous is null || current is null)
        {
            return ParameterComparison.Changed;
        }

        var type = current.GetType();
        if (type != previous.GetType())
        {
            return ParameterComparison.Changed;
        }

        if (IsKnownImmutable(type))
        {
            return previous.Equals(current) ? ParameterComparison.Equal : ParameterComparison.Changed;
        }

        if (type.IsValueType)
        {
            // Boxed structs are always new instances; equal values still make the framework re-render.
            return previous.Equals(current) ? ParameterComparison.DifferentInstance : ParameterComparison.Changed;
        }

        // Delegates, RenderFragments, and objects: contents cannot be compared, and the framework re-renders the child.
        return ParameterComparison.DifferentInstance;
    }

    private static bool IsKnownImmutable(Type type) =>
        Type.GetTypeCode(type) != TypeCode.Object
        || type == typeof(Guid)
        || type == typeof(DateOnly)
        || type == typeof(TimeOnly)
        || type == typeof(Microsoft.AspNetCore.Components.EventCallback)
        || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Microsoft.AspNetCore.Components.EventCallback<>));
}
