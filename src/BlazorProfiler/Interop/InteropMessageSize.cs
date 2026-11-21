namespace BlazorProfiler.Interop;

/// <summary>
/// Carries the measured size of a .NET-to-JavaScript call's arguments from the transport to the interop decorator.
/// </summary>
/// <remarks>
/// On Blazor Server the framework serializes the arguments and writes the circuit message synchronously inside
/// <c>InvokeAsync</c>, on the calling thread. The decorator listens around that call, and the hub protocol wrapper of the
/// server package reports the size of the arguments it writes. When the message is written later (for example while
/// another write holds the connection), nothing is reported and the size stays an estimate.
/// </remarks>
internal static class InteropMessageSize
{
    [ThreadStatic]
    private static bool t_listening;

    [ThreadStatic]
    private static long t_bytes;

    /// <summary>Whether a call on this thread waits for its message size.</summary>
    internal static bool IsListening => t_listening;

    /// <summary>Starts listening for the size of the message written by the next call on this thread.</summary>
    internal static void Begin()
    {
        t_listening = true;
        t_bytes = -1;
    }

    /// <summary>Stops listening and returns the reported size, if any.</summary>
    internal static long? End()
    {
        t_listening = false;
        var bytes = t_bytes;
        t_bytes = -1;
        return bytes >= 0 ? bytes : null;
    }

    /// <summary>
    /// Reports the size of a message written on this thread for the call: its arguments, and byte arrays the framework
    /// sends in messages of their own before it. Sizes reported during one call add up.
    /// </summary>
    internal static void Report(long bytes)
    {
        if (t_listening)
        {
            t_bytes = Math.Max(0, t_bytes) + bytes;
        }
    }
}
