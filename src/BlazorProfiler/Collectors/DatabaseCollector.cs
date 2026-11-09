using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using BlazorProfiler.Events;

namespace BlazorProfiler.Collectors;

/// <summary>
/// Records database commands executed through Entity Framework Core by subscribing to its <c>DiagnosticListener</c>
/// (<c>Microsoft.EntityFrameworkCore</c>). No reference to Entity Framework Core is needed: payload properties are read
/// by name. Command text is normalized (literals replaced) and parameter values are only hashed, never stored.
/// </summary>
/// <remarks>
/// Command timing follows the approach of MiniProfiler: a reader command ends when its data reader is disposed, so the
/// duration includes reading the rows; other commands end when they complete. Correlation (operation, circuit, and the
/// originating component and lifecycle method) is captured when the command starts, on the thread that started it.
/// </remarks>
public sealed class DatabaseCollector : ProfilerCollectorBase, IObserver<DiagnosticListener>
{
    /// <summary>The name of Entity Framework Core's diagnostic listener.</summary>
    public const string ListenerName = "Microsoft.EntityFrameworkCore";

    private const string CommandPrefix = "Microsoft.EntityFrameworkCore.Database.Command.";
    private const string Executing = CommandPrefix + "CommandExecuting";
    private const string Executed = CommandPrefix + "CommandExecuted";
    private const string Error = CommandPrefix + "CommandError";
    private const string Canceled = CommandPrefix + "CommandCanceled";
    private const string ReaderDisposing = CommandPrefix + "DataReaderDisposing";
    private const string RendererContextTypeName = "Microsoft.AspNetCore.Components.Rendering.RendererSynchronizationContext";
    private const int MaxPending = 10_000;
    private const int MaxCommandTextLength = 4_096;

    private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfo?> s_properties = new();
    private static long s_nextContextId;

    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();
    private readonly ConditionalWeakTable<object, StrongBox<long>> _contextIds = [];
    private readonly List<IDisposable> _subscriptions = [];
    private readonly object _lock = new();
    private IDisposable? _allListeners;

    /// <inheritdoc />
    public override CollectorDescriptor Descriptor { get; } = new()
    {
        Name = "database",
        Description = "Database commands executed through Entity Framework Core: normalized SQL, duration (including reading the rows), rows read, synchronous or asynchronous execution, DbContext, and the originating component.",
        Mechanism = "Subscribes to the Microsoft.EntityFrameworkCore DiagnosticListener (CommandExecuting, CommandExecuted, CommandError, CommandCanceled, DataReaderDisposing).",
        Capability = CapabilityLevel.Partial,
        Overhead = OverheadLevel.Low,
        Compatibility = "Entity Framework Core (any provider). Raw ADO.NET commands that bypass Entity Framework Core are not observed.",
        Degradation = "Without Entity Framework Core nothing is recorded. Literals in the command text are replaced with '?'; parameter values are hashed (to recognize identical queries), never stored.",
    };

    /// <summary>Whether the collector is running.</summary>
    public bool IsCollecting => IsActive;

    /// <inheritdoc />
    protected override bool IsEnabled(ProfilerOptions options) => options.Collectors.Database;

    /// <inheritdoc />
    protected override void OnStart(ProfilerCollectorContext context)
    {
        lock (_lock)
        {
            _allListeners ??= DiagnosticListener.AllListeners.Subscribe(this);
        }
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
        lock (_lock)
        {
            _allListeners?.Dispose();
            _allListeners = null;
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
            _pending.Clear();
        }
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener value)
    {
        if (value?.Name != ListenerName)
        {
            return;
        }

        lock (_lock)
        {
            if (_allListeners is not null)
            {
                _subscriptions.Add(value.Subscribe(new CommandObserver(this), static (name, _, _) => name.StartsWith(CommandPrefix, StringComparison.Ordinal)));
            }
        }
    }

    void IObserver<DiagnosticListener>.OnError(Exception error)
    {
    }

    void IObserver<DiagnosticListener>.OnCompleted()
    {
    }

    /// <summary>
    /// Normalizes command text so that executions of the same query shape compare equal: string and numeric literals become
    /// <c>?</c> (identifiers, parameter names, and quoted identifiers are kept), whitespace runs collapse to one space, and
    /// the result is truncated.
    /// </summary>
    public static string Normalize(string? commandText, int maxLength = MaxCommandTextLength)
    {
        if (string.IsNullOrEmpty(commandText))
        {
            return string.Empty;
        }

        var text = commandText.AsSpan();
        var builder = new StringBuilder(Math.Min(text.Length, maxLength + 16));
        var pendingSpace = false;
        for (var i = 0; i < text.Length && builder.Length < maxLength; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            var previous = i > 0 ? text[i - 1] : ' ';
            if (c == '\'' || ((c is 'N' or 'n') && i + 1 < text.Length && text[i + 1] == '\'' && !IsIdentifierPart(previous)))
            {
                // String literal ('' escapes a quote).
                i += c == '\'' ? 1 : 2;
                while (i < text.Length)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }

                        break;
                    }

                    i++;
                }

                builder.Append('?');
                continue;
            }

            if (c is '[' or '"' or '`')
            {
                // Quoted identifier: copied as is.
                var close = c == '[' ? ']' : c;
                builder.Append(c);
                for (i++; i < text.Length && builder.Length < maxLength; i++)
                {
                    builder.Append(text[i]);
                    if (text[i] == close)
                    {
                        break;
                    }
                }

                continue;
            }

            if (char.IsAsciiDigit(c) && !IsIdentifierPart(previous))
            {
                // Numeric literal (including decimals, exponents, and hexadecimal).
                while (i + 1 < text.Length && (char.IsAsciiLetterOrDigit(text[i + 1]) || text[i + 1] == '.'))
                {
                    i++;
                }

                builder.Append('?');
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '$' or '#' or '.' or ':';

    /// <summary>A 64-bit FNV-1a hash.</summary>
    internal static long Hash(string text, long seed = unchecked((long)14695981039346656037UL))
    {
        var hash = unchecked((ulong)seed);
        foreach (var c in text)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return unchecked((long)hash);
    }

    private void OnCommandEvent(string name, object? payload)
    {
        var profiler = Profiler;
        if (payload is null || profiler is null || !IsCollecting)
        {
            return;
        }

        try
        {
            if (Get(payload, "CommandId") is not Guid commandId)
            {
                return;
            }

            switch (name)
            {
                case Executing:
                    Start(profiler, commandId, payload);
                    break;
                case Executed:
                    if (_pending.TryGetValue(commandId, out var executed))
                    {
                        if (executed.Event.ExecuteMethod == "Reader" && Get(payload, "Result") is DbDataReader)
                        {
                            // Ends when the reader is disposed, so reading the rows is included.
                            break;
                        }

                        Finish(profiler, commandId, ProfilerOutcome.Success, null, null);
                    }

                    break;
                case ReaderDisposing:
                    Finish(profiler, commandId, ProfilerOutcome.Success, null, Get(payload, "ReadCount") as int?);
                    break;
                case Error:
                    Finish(profiler, commandId, ProfilerOutcome.Error, (Get(payload, "Exception") as Exception)?.GetType().FullName, null);
                    break;
                case Canceled:
                    Finish(profiler, commandId, ProfilerOutcome.Canceled, typeof(OperationCanceledException).FullName, null);
                    break;
            }
        }
        catch (Exception ex)
        {
            RecordError(ex);
        }
    }

    private void Start(IProfiler profiler, Guid commandId, object payload)
    {
        if (_pending.Count >= MaxPending)
        {
            // Commands whose end was never observed (should not happen); drop the oldest half instead of growing.
            foreach (var key in _pending.Keys.Take(MaxPending / 2))
            {
                _pending.TryRemove(key, out _);
            }
        }

        var command = Get(payload, "Command") as DbCommand;
        var text = Normalize(command?.CommandText);
        var commandHash = Hash(text);
        var parameterHash = commandHash;
        var parameterCount = 0;
        if (command is not null)
        {
            foreach (DbParameter parameter in command.Parameters)
            {
                parameterCount++;
                parameterHash = Hash(parameter.ParameterName ?? string.Empty, parameterHash);
                parameterHash = Hash(ValueText(parameter.Value), parameterHash);
            }
        }

        var context = Get(payload, "Context");
        var e = new DatabaseCommandEvent
        {
            Timestamp = profiler.Clock.GetTimestamp(),
            CommandText = text,
            CommandHash = commandHash,
            ParameterHash = parameterHash,
            ParameterCount = parameterCount,
            ExecuteMethod = Get(payload, "ExecuteMethod")?.ToString() switch
            {
                "ExecuteReader" => "Reader",
                "ExecuteNonQuery" or "NonQuery" => "NonQuery",
                "ExecuteScalar" => "Scalar",
                var other => other,
            },
            IsAsync = Get(payload, "IsAsync") as bool? ?? false,
            ContextType = context?.GetType().FullName,
            ContextInstanceId = context is null ? 0 : _contextIds.GetValue(context, static _ => new StrongBox<long>(Interlocked.Increment(ref s_nextContextId))).Value,
            Provider = command?.Connection?.GetType().Name,
            OnRendererThread = OperatingSystem.IsBrowser() ? null : SynchronizationContext.Current?.GetType().FullName == RendererContextTypeName,
            Source = InstrumentationSource.DatabaseDiagnostics,
        };

        // Capture the ambient operation, circuit, and component now: the command may complete on another thread.
        (profiler as ProfilerHost)?.Correlate(e);
        _pending[commandId] = new Pending(e);
    }

    private void Finish(IProfiler profiler, Guid commandId, ProfilerOutcome outcome, string? errorType, int? rowsRead)
    {
        if (!_pending.TryRemove(commandId, out var pending))
        {
            return;
        }

        var e = pending.Event;
        e.Duration = Math.Max(0, profiler.Clock.GetTimestamp() - e.Timestamp);
        e.Outcome = outcome;
        e.ErrorType = errorType;
        e.RowsRead = rowsRead;
        Emit(e);
    }

    private static string ValueText(object? value) => value switch
    {
        null or DBNull => "\0null",
        byte[] bytes => bytes.Length.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, 64)),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    [UnconditionalSuppressMessage("Trimming", "IL2080", Justification = "Entity Framework Core payload types are only present when the application uses them; a trimmed property reads as null and the value is skipped.")]
    private static object? Get(object payload, string name)
    {
        var property = s_properties.GetOrAdd((payload.GetType(), name), static key => key.Type.GetProperty(key.Name, BindingFlags.Public | BindingFlags.Instance));
        return property?.GetValue(payload);
    }

    private sealed record Pending(DatabaseCommandEvent Event);

    private sealed class CommandObserver(DatabaseCollector collector) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value) => collector.OnCommandEvent(value.Key, value.Value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
