using System.Diagnostics;

namespace BlazorProfiler;

/// <summary>
/// Source of monotonic timestamps used for all duration calculations, plus a wall-clock anchor used to convert
/// timestamps to absolute time for display and cross-system correlation.
/// </summary>
public interface IProfilerClock
{
    /// <summary>Gets the current monotonic timestamp in ticks.</summary>
    long GetTimestamp();

    /// <summary>The number of ticks per second.</summary>
    long Frequency { get; }

    /// <summary>The monotonic timestamp captured together with <see cref="AnchorTime"/>.</summary>
    long AnchorTimestamp { get; }

    /// <summary>The wall-clock time corresponding to <see cref="AnchorTimestamp"/>.</summary>
    DateTimeOffset AnchorTime { get; }
}

/// <summary>An <see cref="IProfilerClock"/> backed by <see cref="Stopwatch"/>.</summary>
/// <remarks>
/// <see cref="Stopwatch"/> is the same time source used by the ASP.NET Core metrics this profiler consumes, so
/// durations reported by the framework and by the profiler share one clock domain.
/// </remarks>
public sealed class StopwatchProfilerClock : IProfilerClock
{
    /// <summary>A shared instance anchored when first used.</summary>
    public static StopwatchProfilerClock Instance { get; } = new();

    /// <summary>Creates a clock anchored at the current time.</summary>
    public StopwatchProfilerClock()
    {
        AnchorTime = DateTimeOffset.UtcNow;
        AnchorTimestamp = Stopwatch.GetTimestamp();
    }

    /// <inheritdoc />
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    /// <inheritdoc />
    public long Frequency => Stopwatch.Frequency;

    /// <inheritdoc />
    public long AnchorTimestamp { get; }

    /// <inheritdoc />
    public DateTimeOffset AnchorTime { get; }
}

/// <summary>Conversion helpers for <see cref="IProfilerClock"/>.</summary>
public static class ProfilerClockExtensions
{
    /// <summary>Converts a tick count to milliseconds.</summary>
    public static double ToMilliseconds(this IProfilerClock clock, long ticks) => ticks * 1000.0 / clock.Frequency;

    /// <summary>Converts milliseconds to a tick count.</summary>
    public static long FromMilliseconds(this IProfilerClock clock, double milliseconds) => (long)(milliseconds * clock.Frequency / 1000.0);

    /// <summary>Converts seconds to a tick count.</summary>
    public static long FromSeconds(this IProfilerClock clock, double seconds) => (long)(seconds * clock.Frequency);

    /// <summary>Converts a <see cref="TimeSpan"/> to a tick count.</summary>
    public static long FromTimeSpan(this IProfilerClock clock, TimeSpan value) => (long)(value.TotalSeconds * clock.Frequency);

    /// <summary>Converts a monotonic timestamp to wall-clock time using the clock's anchor.</summary>
    public static DateTimeOffset ToDateTimeOffset(this IProfilerClock clock, long timestamp)
    {
        var deltaTicks = timestamp - clock.AnchorTimestamp;
        return clock.AnchorTime.AddTicks((long)(deltaTicks * ((double)TimeSpan.TicksPerSecond / clock.Frequency)));
    }

    /// <summary>Converts a wall-clock time to a monotonic timestamp using the clock's anchor.</summary>
    public static long FromDateTimeOffset(this IProfilerClock clock, DateTimeOffset time)
    {
        var deltaTicks = (time - clock.AnchorTime).Ticks;
        return clock.AnchorTimestamp + (long)(deltaTicks * ((double)clock.Frequency / TimeSpan.TicksPerSecond));
    }
}
