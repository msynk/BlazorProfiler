namespace BlazorProfiler.Api;

/// <summary>
/// An action the dashboard (or any API client) can run in the profiled application, such as a leak check that forces
/// garbage collections, or switching a development-time simulation on. Commands are listed in <see cref="ProfilerInfo.Commands"/>
/// and executed through <see cref="ProfilerApi.ExecuteCommandAsync"/>. Register implementations as
/// <c>IProfilerCommand</c> singletons.
/// </summary>
public interface IProfilerCommand
{
    /// <summary>Describes the command.</summary>
    ProfilerCommandDescriptor Descriptor { get; }

    /// <summary>A short description of the command's current state (for example "150 ms" or "off"), or null.</summary>
    string? GetState() => null;

    /// <summary>Runs the command. Throw <see cref="ProfilerApiException"/> for invalid arguments.</summary>
    ValueTask<ProfilerCommandResult> ExecuteAsync(string? argument, CancellationToken cancellationToken = default);
}

/// <summary>Describes a <see cref="IProfilerCommand"/>.</summary>
public sealed class ProfilerCommandDescriptor
{
    /// <summary>A unique, URL-safe name, for example <c>check-leaks</c>.</summary>
    public required string Name { get; init; }

    /// <summary>A short title for buttons.</summary>
    public required string Title { get; init; }

    /// <summary>What the command does and what it costs.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Whether the command affects the whole process (every user and circuit). Such commands require access to all
    /// circuits (<see cref="ProfilerApiAccess.CanViewAllCircuits"/>). Defaults to true.
    /// </summary>
    public bool ProcessWide { get; init; } = true;

    /// <summary>Describes the expected argument (shown as a prompt), or null when the command takes none.</summary>
    public string? ArgumentHint { get; init; }
}

/// <summary>A command as listed in <see cref="ProfilerInfo.Commands"/>.</summary>
public sealed class ProfilerCommandInfo
{
    /// <summary>The command name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The argument hint, or null when the command takes no argument.</summary>
    public string? ArgumentHint { get; set; }

    /// <summary>The current state, or null.</summary>
    public string? State { get; set; }
}

/// <summary>The result of a command.</summary>
public sealed class ProfilerCommandResult
{
    /// <summary>A human-readable outcome.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Optional named values (for example counts).</summary>
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
}
