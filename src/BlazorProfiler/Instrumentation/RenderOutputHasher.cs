using Microsoft.AspNetCore.Components.RenderTree;

namespace BlazorProfiler.Instrumentation;

/// <summary>
/// Computes a structural hash of a component's render output so consecutive renders with identical output can be detected.
/// </summary>
/// <remarks>
/// <para>
/// The hash covers frame types, sequence numbers, element names, text and markup content, attribute names, and attribute
/// values of immutable types (strings, numbers, booleans, enums, dates, GUIDs). Delegates and other objects contribute only
/// their type, because event handlers are typically re-created on every render without changing behavior. Child components
/// contribute their type; their parameters are compared by the child itself.
/// </para>
/// <para>
/// Content is hashed in memory and never stored. Hashes use the process-randomized string hash and are only comparable
/// within one process.
/// </para>
/// </remarks>
internal static class RenderOutputHasher
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Hash(ArrayRange<RenderTreeFrame> frames)
    {
        var hash = Offset;
        var array = frames.Array;
        for (var i = 0; i < frames.Count; i++)
        {
            ref var frame = ref array[i];
            hash = Mix(hash, (int)frame.FrameType);
            hash = Mix(hash, frame.Sequence);
            switch (frame.FrameType)
            {
                case RenderTreeFrameType.Element:
                    hash = Mix(hash, frame.ElementName);
                    hash = Mix(hash, frame.ElementSubtreeLength);
                    hash = MixValue(hash, frame.ElementKey);
                    break;
                case RenderTreeFrameType.Text:
                    hash = Mix(hash, frame.TextContent);
                    break;
                case RenderTreeFrameType.Markup:
                    hash = Mix(hash, frame.MarkupContent);
                    break;
                case RenderTreeFrameType.Attribute:
                    hash = Mix(hash, frame.AttributeName);
                    hash = MixValue(hash, frame.AttributeValue);
                    break;
                case RenderTreeFrameType.Component:
                    hash = Mix(hash, frame.ComponentType.GetHashCode());
                    hash = Mix(hash, frame.ComponentSubtreeLength);
                    hash = MixValue(hash, frame.ComponentKey);
                    break;
                case RenderTreeFrameType.Region:
                    hash = Mix(hash, frame.RegionSubtreeLength);
                    break;
            }
        }

        return hash;
    }

    private static ulong Mix(ulong hash, int value) => (hash ^ (uint)value) * Prime;

    private static ulong Mix(ulong hash, string? value) => value is null ? Mix(hash, -1) : Mix(Mix(hash, StringComparer.Ordinal.GetHashCode(value)), value.Length);

    private static ulong MixValue(ulong hash, object? value) => value switch
    {
        null => Mix(hash, -2),
        string s => Mix(hash, s),
        bool b => Mix(hash, b ? 1 : 0),
        int or long or short or byte or sbyte or uint or ulong or ushort or double or float or decimal or char
            or DateTime or DateTimeOffset or TimeSpan or Guid or DateOnly or TimeOnly or Enum => Mix(Mix(hash, value.GetType().GetHashCode()), value.GetHashCode()),
        _ => Mix(hash, value.GetType().GetHashCode()),
    };
}
