using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorProfiler.Analysis;
using BlazorProfiler.Collectors;
using BlazorProfiler.Events;
using BlazorProfiler.Sessions;
using BlazorProfiler.Storage;

namespace BlazorProfiler.Export;

/// <summary>Source-generated JSON metadata for every type the profiler serializes. Trimming- and AOT-safe.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    MaxDepth = 32)]
[JsonSerializable(typeof(TraceDocument))]
[JsonSerializable(typeof(ProfilerEvent))]
[JsonSerializable(typeof(List<ProfilerEvent>))]
[JsonSerializable(typeof(SessionSummary))]
[JsonSerializable(typeof(DiagnosticReport))]
[JsonSerializable(typeof(ProfilerHealthSnapshot))]
[JsonSerializable(typeof(ProfilerSessionStatistics))]
[JsonSerializable(typeof(List<ProfilerSessionStatistics>))]
[JsonSerializable(typeof(FindingSuppression))]
[JsonSerializable(typeof(List<FindingSuppression>))]
[JsonSerializable(typeof(CollectorStatus))]
[JsonSerializable(typeof(Api.ProfilerInfo))]
[JsonSerializable(typeof(Api.EventPage))]
[JsonSerializable(typeof(Api.ProfilerApiRequest))]
[JsonSerializable(typeof(Api.ProfilerApiResponse))]
[JsonSerializable(typeof(Api.ProfilerCommandResult))]
public sealed partial class ProfilerJsonContext : JsonSerializerContext;

/// <summary>Clones events (used when export redaction must not modify live events).</summary>
internal static class EventCloner
{
    public static ProfilerEvent Clone(ProfilerEvent profilerEvent)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(profilerEvent, ProfilerJsonContext.Default.ProfilerEvent);
        return JsonSerializer.Deserialize(bytes, ProfilerJsonContext.Default.ProfilerEvent)!;
    }
}

/// <summary>Writes traces to a stream.</summary>
public interface IProfilerExporter
{
    /// <summary>A unique exporter name (for example <c>json</c>).</summary>
    string Name { get; }

    /// <summary>The MIME content type of the output.</summary>
    string ContentType { get; }

    /// <summary>The recommended file extension, including the leading dot.</summary>
    string FileExtension { get; }

    /// <summary>Writes the trace.</summary>
    Task ExportAsync(TraceDocument document, Stream output, CancellationToken cancellationToken = default);
}

/// <summary>Exports the native trace format as plain JSON.</summary>
public sealed class JsonTraceExporter : IProfilerExporter
{
    /// <inheritdoc />
    public string Name => "json";

    /// <inheritdoc />
    public string ContentType => "application/json";

    /// <inheritdoc />
    public string FileExtension => ".bptrace.json";

    /// <inheritdoc />
    public Task ExportAsync(TraceDocument document, Stream output, CancellationToken cancellationToken = default) =>
        JsonSerializer.SerializeAsync(output, document, ProfilerJsonContext.Default.TraceDocument, cancellationToken);
}

/// <summary>Exports the native trace format as gzip-compressed JSON (the recommended file format).</summary>
public sealed class CompressedTraceExporter : IProfilerExporter
{
    /// <inheritdoc />
    public string Name => "compressed";

    /// <inheritdoc />
    public string ContentType => "application/gzip";

    /// <inheritdoc />
    public string FileExtension => ".bptrace";

    /// <inheritdoc />
    public async Task ExportAsync(TraceDocument document, Stream output, CancellationToken cancellationToken = default)
    {
        var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
        await using (gzip.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(gzip, document, ProfilerJsonContext.Default.TraceDocument, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Limits applied when importing traces. Imported files are untrusted input.</summary>
public sealed class TraceImportOptions
{
    /// <summary>The maximum size of the input stream in bytes (compressed size for compressed traces).</summary>
    public long MaxInputBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>The maximum decompressed size in bytes.</summary>
    public long MaxDecompressedBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>The maximum number of events.</summary>
    public int MaxEvents { get; set; } = 1_000_000;

    /// <summary>The maximum length of any string value; longer strings are truncated.</summary>
    public int MaxStringLength { get; set; } = 2_048;
}

/// <summary>Thrown when a trace cannot be imported.</summary>
public sealed class TraceImportException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TraceImportException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public TraceImportException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public TraceImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reads and validates traces.</summary>
/// <remarks>
/// Imported traces are treated as untrusted data: input and decompressed sizes are limited, only the known event types
/// are accepted, every value is validated, and no code or type names from the input are ever executed or resolved.
/// </remarks>
public static class TraceImporter
{
    /// <summary>Reads a trace (plain or gzip-compressed JSON) and validates it.</summary>
    /// <exception cref="TraceImportException">The input is invalid or exceeds a limit.</exception>
    public static async Task<TraceDocument> ReadAsync(Stream input, TraceImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= new TraceImportOptions();
        var limited = new LimitedReadStream(input, options.MaxInputBytes, "The trace exceeds the maximum input size.");
        var buffered = new BufferedStream(limited, 8192);
        Stream source = buffered;
        try
        {
            var header = new byte[2];
            var read = await buffered.ReadAsync(header, cancellationToken).ConfigureAwait(false);
            var prefix = new PrefixedStream(header.AsMemory(0, read), buffered);
            source = read == 2 && header[0] == 0x1f && header[1] == 0x8b
                ? new LimitedReadStream(new GZipStream(prefix, CompressionMode.Decompress), options.MaxDecompressedBytes, "The trace exceeds the maximum decompressed size.")
                : prefix;

            var document = await JsonSerializer.DeserializeAsync(source, ProfilerJsonContext.Default.TraceDocument, cancellationToken).ConfigureAwait(false)
                ?? throw new TraceImportException("The trace is empty.");
            Validate(document, options);
            return document;
        }
        catch (TraceImportException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException or IOException)
        {
            throw new TraceImportException("The trace is not a valid Blazor Profiler trace: " + ex.Message, ex);
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Validates a trace document in place (truncating overlong strings).</summary>
    /// <exception cref="TraceImportException">The document is invalid.</exception>
    public static void Validate(TraceDocument document, TraceImportOptions options)
    {
        if (!string.Equals(document.Format, TraceDocument.FormatName, StringComparison.Ordinal))
        {
            throw new TraceImportException($"Unknown trace format '{Truncate(document.Format, 64)}'.");
        }

        if (document.FormatVersion is < 1 or > TraceDocument.CurrentFormatVersion)
        {
            throw new TraceImportException($"Unsupported trace format version {document.FormatVersion}.");
        }

        if (document.EventSchemaVersion is < 1 or > ProfilerEvent.SchemaVersion)
        {
            throw new TraceImportException($"Unsupported event schema version {document.EventSchemaVersion}.");
        }

        if (document.Clock is null || document.Clock.Frequency <= 0)
        {
            throw new TraceImportException("The trace clock is missing or invalid.");
        }

        document.Events ??= [];
        if (document.Events.Count > options.MaxEvents)
        {
            throw new TraceImportException($"The trace contains {document.Events.Count} events; the limit is {options.MaxEvents}.");
        }

        document.Metadata ??= new TraceMetadata();
        document.Metadata.SessionName = Truncate(document.Metadata.SessionName, 128);
        document.Collectors ??= [];

        var max = options.MaxStringLength;
        foreach (var e in document.Events)
        {
            if (e is null)
            {
                throw new TraceImportException("The trace contains a null event.");
            }

            if (e.Duration < 0)
            {
                throw new TraceImportException($"Event {e.Id} has a negative duration.");
            }

            if (double.IsNaN(e.SampleWeight) || double.IsInfinity(e.SampleWeight) || e.SampleWeight <= 0 || e.SampleWeight > 1_000_000)
            {
                throw new TraceImportException($"Event {e.Id} has an invalid sample weight.");
            }

            e.CircuitId = Truncate(e.CircuitId, 256);
            e.ErrorType = Truncate(e.ErrorType, max);
            e.TraceId = Truncate(e.TraceId, 64);
            e.SpanId = Truncate(e.SpanId, 32);
            switch (e)
            {
                case ComponentChildrenEvent children:
                    children.ComponentType = Truncate(children.ComponentType, max);
                    children.ChildInstanceIds ??= [];
                    children.ChildTypes ??= [];
                    if (children.ChildInstanceIds.Length > 100_000 || children.ChildTypes.Length > 100_000)
                    {
                        throw new TraceImportException($"Event {e.Id} has too many children.");
                    }

                    for (var i = 0; i < children.ChildTypes.Length; i++)
                    {
                        children.ChildTypes[i] = Truncate(children.ChildTypes[i], max) ?? string.Empty;
                    }

                    break;
                case ParametersSetEvent parameters:
                    parameters.ComponentType = Truncate(parameters.ComponentType, max);
                    if (parameters.ChangedParameters is { Length: > 256 })
                    {
                        parameters.ChangedParameters = parameters.ChangedParameters[..256];
                    }

                    if (parameters.ChangedParameters is not null)
                    {
                        for (var i = 0; i < parameters.ChangedParameters.Length; i++)
                        {
                            parameters.ChangedParameters[i] = Truncate(parameters.ChangedParameters[i], 256) ?? string.Empty;
                        }
                    }

                    break;
                case EventHandlerEvent handler:
                    handler.ComponentType = Truncate(handler.ComponentType, max);
                    handler.MethodName = Truncate(handler.MethodName, max);
                    handler.AttributeName = Truncate(handler.AttributeName, 128);
                    break;
                case NavigationEvent navigation:
                    navigation.ComponentType = Truncate(navigation.ComponentType, max);
                    navigation.Route = Truncate(navigation.Route, max);
                    break;
                case ComponentEvent component:
                    component.ComponentType = Truncate(component.ComponentType, max);
                    break;
                case HttpRequestEvent http:
                    http.Method = Truncate(http.Method, 16);
                    http.Url = Truncate(http.Url, max);
                    http.Endpoint = Truncate(http.Endpoint, max);
                    http.OriginComponentType = Truncate(http.OriginComponentType, max);
                    break;
                case DatabaseCommandEvent db:
                    db.CommandText = Truncate(db.CommandText, max);
                    db.ExecuteMethod = Truncate(db.ExecuteMethod, 32);
                    db.ContextType = Truncate(db.ContextType, max);
                    db.Provider = Truncate(db.Provider, 128);
                    db.OriginComponentType = Truncate(db.OriginComponentType, max);
                    break;
                case JSInteropEvent js:
                    js.Identifier = Truncate(js.Identifier, max);
                    js.OriginComponentType = Truncate(js.OriginComponentType, max);
                    break;
                case OperationEvent operation:
                    operation.Name = Truncate(operation.Name, max);
                    operation.Category = Truncate(operation.Category, 128);
                    operation.ComponentType = Truncate(operation.ComponentType, max);
                    break;
                case MarkerEvent marker:
                    marker.Name = Truncate(marker.Name, 256);
                    break;
                case CircuitActivityEvent activity:
                    activity.ActivityName = Truncate(activity.ActivityName, 128);
                    break;
                case BrowserInteractionEvent interaction:
                    interaction.EventType = Truncate(interaction.EventType, 64);
                    interaction.TargetTag = Truncate(interaction.TargetTag, 64);
                    break;
                case BrowserLongTaskEvent longTask:
                    longTask.EntryType = Truncate(longTask.EntryType, 64);
                    longTask.Script = Truncate(longTask.Script, max);
                    break;
                case BrowserStartupEvent startup:
                    startup.NavigationType = Truncate(startup.NavigationType, 32);
                    startup.RuntimeKind = Truncate(startup.RuntimeKind, 32);
                    if (startup.LargestFiles.Count > 50)
                    {
                        startup.LargestFiles = startup.LargestFiles.GetRange(0, 50);
                    }

                    foreach (var file in startup.LargestFiles)
                    {
                        file.Name = Truncate(file.Name, 256);
                    }

                    break;
                case BrowserVitalEvent vital:
                    vital.Name = Truncate(vital.Name, 16);
                    vital.TargetTag = Truncate(vital.TargetTag, 64);
                    break;
                case BrowserResourceEvent resource:
                    resource.Url = Truncate(resource.Url, max);
                    resource.InitiatorType = Truncate(resource.InitiatorType, 64);
                    break;
            }
        }

        if (document.Findings is { Count: > 10_000 })
        {
            throw new TraceImportException("The trace contains too many findings.");
        }
    }

    /// <summary>Creates a read-only session from a validated trace.</summary>
    public static ProfilerSession CreateSession(TraceDocument document, ProfilerOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        var clock = new FixedProfilerClock(document.Clock.Frequency, document.Clock.AnchorTimestamp, document.Clock.AnchorTime);
        var retention = new RetentionOptions
        {
            MaxEvents = Math.Max(100, document.Events.Count),
            MaxMemoryBytes = long.MaxValue / 4,
            MaxAge = TimeSpan.FromDays(3650),
            MaxAggregateKeys = options.Retention.MaxAggregateKeys,
            MaxTrackedInstances = options.Retention.MaxTrackedInstances,
            TimeSeriesBuckets = options.Retention.TimeSeriesBuckets,
        };

        var start = document.Events.Count == 0 ? clock.AnchorTimestamp : document.Events.Min(e => e.Timestamp);
        if (document.Metadata.CreatedAt != default)
        {
            start = Math.Min(start, clock.FromDateTimeOffset(document.Metadata.CreatedAt));
        }

        var session = new ProfilerSession(
            Guid.NewGuid().ToString("N"),
            new ProfilerSessionOptions { Name = document.Metadata.SessionName ?? "Imported trace", CircuitId = document.Metadata.CircuitId },
            clock,
            retention,
            start,
            imported: true,
            stateChanged: null);

        foreach (var e in document.Events)
        {
            session.AppendImported(e);
        }

        return session;
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    /// <summary>A stream that replays a small prefix before reading from an inner stream.</summary>
    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (!_prefix.IsEmpty)
            {
                var n = Math.Min(buffer.Length, _prefix.Length);
                _prefix.Span[..n].CopyTo(buffer);
                _prefix = _prefix[n..];
                return n;
            }

            return inner.Read(buffer);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_prefix.IsEmpty)
            {
                var n = Math.Min(buffer.Length, _prefix.Length);
                _prefix[..n].CopyTo(buffer);
                _prefix = _prefix[n..];
                return n;
            }

            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>A read-only stream wrapper that fails when more than a maximum number of bytes is read.</summary>
internal sealed class LimitedReadStream(Stream inner, long maxBytes, string message) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Count(int read)
    {
        _read += read;
        if (_read > maxBytes)
        {
            throw new TraceImportException(message);
        }

        return read;
    }
}

/// <summary>A clock with fixed parameters, used for imported traces.</summary>
public sealed class FixedProfilerClock : IProfilerClock
{
    private readonly long _created = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>Creates the clock.</summary>
    public FixedProfilerClock(long frequency, long anchorTimestamp, DateTimeOffset anchorTime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(frequency, 0);
        Frequency = frequency;
        AnchorTimestamp = anchorTimestamp;
        AnchorTime = anchorTime;
    }

    /// <inheritdoc />
    public long Frequency { get; }

    /// <inheritdoc />
    public long AnchorTimestamp { get; }

    /// <inheritdoc />
    public DateTimeOffset AnchorTime { get; }

    /// <inheritdoc />
    public long GetTimestamp()
    {
        var elapsedSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(_created).TotalSeconds;
        return AnchorTimestamp + (long)(elapsedSeconds * Frequency);
    }
}
