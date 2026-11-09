using System.Globalization;
using BlazorProfiler.Api;
using Microsoft.Extensions.Hosting;

namespace BlazorProfiler.Server;

/// <summary>
/// Simulates network latency for Blazor Server circuits during development: every message from the browser (events,
/// JavaScript interop results, render acknowledgements) waits the configured delay before the server processes it, so
/// interactions, renders, and interop calls take about one delay longer, as on a slow connection. Local development has
/// almost no latency, which hides missing loading states, double submissions, and chatty interop.
/// </summary>
/// <remarks>Never active in the Production environment. Change it with the <c>simulate-latency</c> command.</remarks>
public sealed class LatencySimulator
{
    /// <summary>The largest allowed delay.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);

    private long _delayTicks;

    /// <summary>Creates the simulator with an initial delay.</summary>
    public LatencySimulator(ProfilerServerOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        IsAllowed = !environment.IsProduction();
        Delay = options.SimulatedLatency;
    }

    /// <summary>Whether simulation may be enabled (not in Production).</summary>
    public bool IsAllowed { get; }

    /// <summary>The delay added to each browser message; zero when off.</summary>
    public TimeSpan Delay
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _delayTicks));
        set
        {
            if (value < TimeSpan.Zero || value > MaxDelay)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"The simulated latency must be between 0 and {MaxDelay.TotalMilliseconds} ms.");
            }

            Interlocked.Exchange(ref _delayTicks, IsAllowed ? value.Ticks : 0);
        }
    }
}

/// <summary>The <c>simulate-latency</c> command: sets the delay added to every message from the browser (0 turns it off).</summary>
public sealed class SimulateLatencyCommand : IProfilerCommand
{
    private readonly LatencySimulator _simulator;

    /// <summary>Creates the command.</summary>
    public SimulateLatencyCommand(LatencySimulator simulator) => _simulator = simulator;

    /// <inheritdoc />
    public ProfilerCommandDescriptor Descriptor { get; } = new()
    {
        Name = "simulate-latency",
        Title = "Simulate latency",
        Description = "Delays every message from the browser to Blazor Server circuits (events, interop results, render acknowledgements) to reproduce a slow connection. Affects every circuit of this server; not available in Production.",
        ArgumentHint = "Delay per browser message in milliseconds (0 turns simulation off, at most 5000):",
    };

    /// <inheritdoc />
    public string? GetState() => !_simulator.IsAllowed ? "not available in Production" : _simulator.Delay == TimeSpan.Zero ? "off" : $"{_simulator.Delay.TotalMilliseconds:0} ms";

    /// <inheritdoc />
    public ValueTask<ProfilerCommandResult> ExecuteAsync(string? argument, CancellationToken cancellationToken = default)
    {
        if (!_simulator.IsAllowed)
        {
            throw new ProfilerApiException("Latency simulation is not available in the Production environment.");
        }

        if (!double.TryParse(argument?.Trim().TrimEnd('s', 'm', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds)
            || milliseconds < 0 || milliseconds > LatencySimulator.MaxDelay.TotalMilliseconds)
        {
            throw new ProfilerApiException($"Enter a delay between 0 and {LatencySimulator.MaxDelay.TotalMilliseconds:0} milliseconds.");
        }

        _simulator.Delay = TimeSpan.FromMilliseconds(milliseconds);
        return ValueTask.FromResult(new ProfilerCommandResult
        {
            Message = milliseconds == 0
                ? "Latency simulation is off."
                : $"Every message from the browser to the server is now delayed by {milliseconds:0} ms. Interactions, renders, and interop calls take about that much longer.",
            Values = { ["delayMs"] = milliseconds.ToString("0", CultureInfo.InvariantCulture) },
        });
    }
}
