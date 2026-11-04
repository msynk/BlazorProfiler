namespace BlazorProfiler.Storage;

/// <summary>
/// Streaming duration statistics with a fixed-size reservoir for percentile estimates. Memory use is constant.
/// </summary>
public sealed class DurationStats
{
    private const int ReservoirSize = 128;

    private long[]? _reservoir;
    private int _reservoirCount;
    private long _seen;
    private ulong _random = 0x9E3779B97F4A7C15UL;

    /// <summary>The number of observations (weighted by sample weight).</summary>
    public double Count { get; private set; }

    /// <summary>The sum of observed durations in ticks (weighted).</summary>
    public double TotalTicks { get; private set; }

    /// <summary>The largest observed duration in ticks.</summary>
    public long MaxTicks { get; private set; }

    /// <summary>Adds an observation.</summary>
    public void Add(long ticks, double weight = 1)
    {
        if (ticks < 0)
        {
            ticks = 0;
        }

        Count += weight;
        TotalTicks += ticks * weight;
        if (ticks > MaxTicks)
        {
            MaxTicks = ticks;
        }

        _reservoir ??= new long[ReservoirSize];
        _seen++;
        if (_reservoirCount < ReservoirSize)
        {
            _reservoir[_reservoirCount++] = ticks;
        }
        else
        {
            // Algorithm R with a deterministic xorshift generator (reproducible results for identical input).
            _random ^= _random << 13;
            _random ^= _random >> 7;
            _random ^= _random << 17;
            var index = (long)(_random % (ulong)_seen);
            if (index < ReservoirSize)
            {
                _reservoir[index] = ticks;
            }
        }
    }

    /// <summary>The mean duration in ticks.</summary>
    public double MeanTicks => Count > 0 ? TotalTicks / Count : 0;

    /// <summary>Estimates a percentile (0..100) in ticks from the reservoir.</summary>
    public long Percentile(double percentile)
    {
        if (_reservoirCount == 0 || _reservoir is null)
        {
            return 0;
        }

        var copy = new long[_reservoirCount];
        Array.Copy(_reservoir, copy, _reservoirCount);
        Array.Sort(copy);
        var rank = (int)Math.Ceiling(percentile / 100.0 * copy.Length) - 1;
        return copy[Math.Clamp(rank, 0, copy.Length - 1)];
    }

    /// <summary>Creates a summary in milliseconds.</summary>
    public DurationSummary ToSummary(IProfilerClock clock) => new()
    {
        Count = Math.Round(Count, 2),
        TotalMs = Round(clock.ToMilliseconds((long)TotalTicks)),
        MeanMs = Round(clock.ToMilliseconds((long)MeanTicks)),
        P50Ms = Round(clock.ToMilliseconds(Percentile(50))),
        P95Ms = Round(clock.ToMilliseconds(Percentile(95))),
        MaxMs = Round(clock.ToMilliseconds(MaxTicks)),
    };

    private static double Round(double value) => Math.Round(value, 3);
}

/// <summary>Summary of a duration distribution. Percentiles are estimates from a bounded reservoir.</summary>
public sealed class DurationSummary
{
    /// <summary>The number of observations (weighted by sample weight; may be fractional when sampling).</summary>
    public double Count { get; set; }

    /// <summary>The total duration in milliseconds.</summary>
    public double TotalMs { get; set; }

    /// <summary>The mean duration in milliseconds.</summary>
    public double MeanMs { get; set; }

    /// <summary>The estimated median in milliseconds.</summary>
    public double P50Ms { get; set; }

    /// <summary>The estimated 95th percentile in milliseconds.</summary>
    public double P95Ms { get; set; }

    /// <summary>The maximum in milliseconds.</summary>
    public double MaxMs { get; set; }
}
