using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BlazorProfiler.Analysis;
using BlazorProfiler.Events;
using BlazorProfiler.Pipeline;

namespace BlazorProfiler.Browser;

/// <summary>
/// Forwards profiler events to the browser's own Performance panel: <c>BlazorProfilerClient</c> (with
/// <c>DevToolsTracks</c>) drains the queued events every second and the browser module writes them with the DevTools
/// extensibility API (<c>performance.measure</c> with <c>detail.devtools</c>) into a "Blazor" track group, next to the
/// browser's own tracks (main thread, network, interactions, layout shifts). Inactive (one field check per event) unless a
/// client subscribed.
/// </summary>
public sealed class DevToolsTrackProcessor : IProfilerEventProcessor
{
    /// <summary>The maximum number of events queued per subscription between drains; more are dropped.</summary>
    public const int MaxQueued = 5_000;

    private readonly object _lock = new();
    private Subscription[] _subscriptions = [];

    /// <summary>Whether any client subscribed.</summary>
    public bool IsActive => Volatile.Read(ref _subscriptions).Length > 0;

    /// <summary>
    /// Subscribes to events. With a <paramref name="circuitId"/>, only that circuit's events are delivered (Blazor Server:
    /// a user never receives another user's data); without one, every event (WebAssembly, where the runtime belongs to the
    /// tab).
    /// </summary>
    public Subscription Subscribe(string? circuitId)
    {
        var subscription = new Subscription(this, circuitId);
        lock (_lock)
        {
            _subscriptions = [.. _subscriptions, subscription];
        }

        return subscription;
    }

    /// <inheritdoc />
    public bool Process(ProfilerEvent profilerEvent)
    {
        var subscriptions = Volatile.Read(ref _subscriptions);
        if (subscriptions.Length == 0 || !IsTracked(profilerEvent))
        {
            return true;
        }

        foreach (var subscription in subscriptions)
        {
            if (subscription.CircuitId is null || subscription.CircuitId == profilerEvent.CircuitId)
            {
                subscription.Enqueue(profilerEvent);
            }
        }

        return true;
    }

    private void Remove(Subscription subscription)
    {
        lock (_lock)
        {
            _subscriptions = [.. _subscriptions.Where(s => s != subscription)];
        }
    }

    private static bool IsTracked(ProfilerEvent e) =>
        e is ComponentRenderEvent or RenderBatchEvent or EventHandlerEvent or HttpRequestEvent or JSInteropEvent or DatabaseCommandEvent or NavigationEvent or OperationEvent or ComponentRetainedEvent;

    /// <summary>
    /// Writes events as the JSON array the browser module expects: <c>[name, track, startEpochMs, endEpochMs, color,
    /// tooltip, [[key, value], ...]]</c>; an empty track means a marker. Times are converted to the browser's clock by
    /// subtracting <paramref name="clockOffsetMs"/> (server time minus browser time).
    /// </summary>
    public static string Serialize(IReadOnlyList<ProfilerEvent> events, IProfilerClock clock, double clockOffsetMs)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var e in events)
            {
                var (name, track, color, tooltip, properties) = Describe(e, clock);
                var start = (clock.ToDateTimeOffset(e.Timestamp) - DateTimeOffset.UnixEpoch).TotalMilliseconds - clockOffsetMs;
                writer.WriteStartArray();
                writer.WriteStringValue(name);
                writer.WriteStringValue(track);
                writer.WriteNumberValue(Math.Round(start, 3));
                writer.WriteNumberValue(Math.Round(start + clock.ToMilliseconds(e.Duration), 3));
                writer.WriteStringValue(color);
                writer.WriteStringValue(tooltip);
                writer.WriteStartArray();
                foreach (var (key, value) in properties)
                {
                    if (value is null)
                    {
                        continue;
                    }

                    writer.WriteStartArray();
                    writer.WriteStringValue(key);
                    writer.WriteStringValue(value);
                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static (string Name, string Track, string Color, string Tooltip, (string, string?)[] Properties) Describe(ProfilerEvent e, IProfilerClock clock)
    {
        var ms = clock.ToMilliseconds(e.Duration);
        var failed = e.Outcome is ProfilerOutcome.Error or ProfilerOutcome.Canceled;
        static string Ms(double v) => DiagnosticContext.FormatMs(v);
        static string Short(string? type) => DiagnosticContext.ShortType(type);
        string Shade(string family) => failed ? "error" : ms < 0.5 ? family + "-light" : ms < 10 ? family : ms < 100 ? family + "-dark" : "error";
        switch (e)
        {
            case ComponentRenderEvent r:
                return (Short(r.ComponentType), "Components", Shade("primary"), $"Render {Short(r.ComponentType)} #{r.ComponentInstanceId}: {Ms(ms)} ({r.Reason})",
                    [("Component", r.ComponentType), ("Instance", r.ComponentInstanceId.ToString(CultureInfo.InvariantCulture)), ("Reason", r.Reason.ToString()), ("Render", r.RenderNumber.ToString(CultureInfo.InvariantCulture)), ("Output", r.OutputUnchanged is null ? null : r.OutputUnchanged.Value ? "identical to the previous render" : "changed")]);
            case RenderBatchEvent b:
                return ($"Render batch ({b.UpdatedComponentCount})", "Render batches", Shade("secondary"), $"{b.UpdatedComponentCount} component(s) diffed, {Ms(ms)}{(b.IncludesClientRoundTrip == true ? " including the round trip to the browser" : string.Empty)}",
                    [("Components diffed", b.UpdatedComponentCount.ToString(CultureInfo.InvariantCulture)), ("Includes round trip", b.IncludesClientRoundTrip?.ToString())]);
            case EventHandlerEvent h:
                return ($"{h.AttributeName} → {Short(h.ComponentType)}.{h.MethodName}", "Event handlers", Shade("tertiary"), $"{h.AttributeName} → {h.ComponentType}.{h.MethodName}: {Ms(ms)}",
                    [("Component", h.ComponentType), ("Method", h.MethodName), ("Synchronous part", h.SynchronousDuration > 0 ? Ms(clock.ToMilliseconds(h.SynchronousDuration)) : null), ("Operation", h.OperationId.ToString(CultureInfo.InvariantCulture))]);
            case HttpRequestEvent http:
                return ($"{http.Method} {http.Endpoint ?? http.Url}", "HTTP (.NET)", Shade("primary"), $"{http.Method} {http.Url} → {http.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? http.Outcome.ToString()} in {Ms(ms)}",
                    [("URL", http.Url), ("Status", http.StatusCode?.ToString(CultureInfo.InvariantCulture)), ("Response bytes", http.ResponseBytes?.ToString(CultureInfo.InvariantCulture)), ("Started by", http.OriginComponentType is null ? null : $"{Short(http.OriginComponentType)}{(http.OriginLifecycleMethod is { } m ? "." + m : string.Empty)}")]);
            case JSInteropEvent js:
                return (js.Identifier ?? "interop", "JS interop", Shade("secondary"), $"{js.Identifier}: {Ms(ms)}",
                    [("Identifier", js.Identifier), ("Synchronous", js.Synchronous ? "yes" : "no"), ("Started by", js.OriginComponentType is null ? null : Short(js.OriginComponentType))]);
            case DatabaseCommandEvent db:
                var sql = db.CommandText ?? "SQL";
                return (sql.Length > 60 ? sql[..60] + "…" : sql, "Database", Shade("tertiary"), $"{sql} ({Ms(ms)})",
                    [("Query", sql.Length > 500 ? sql[..500] + "…" : sql), ("Rows read", db.RowsRead?.ToString(CultureInfo.InvariantCulture)), ("Mode", db.IsAsync ? "async" : "sync"), ("Started by", db.OriginComponentType is null ? null : Short(db.OriginComponentType))]);
            case OperationEvent op:
                return (op.Name ?? "Operation", "Operations", Shade("secondary"), $"{op.Name}: {Ms(ms)}", [("Category", op.Category), ("Component", op.ComponentType)]);
            case NavigationEvent n:
                return ($"Blazor: navigate {n.Route}", string.Empty, "primary", $"Navigation to {n.Route} ({Short(n.ComponentType)})", [("Route", n.Route), ("Page", n.ComponentType)]);
            case ComponentRetainedEvent retained:
                return ($"Blazor: {Short(retained.ComponentType)} retained", string.Empty, "error", $"{retained.ComponentType} #{retained.ComponentInstanceId} still reachable after {retained.SurvivedCollections} full GC(s)", [("Component", retained.ComponentType)]);
            default:
                return (e.Kind.ToString(), "Other", "secondary", e.Kind.ToString(), []);
        }
    }

    /// <summary>A client's subscription. Dispose it to stop receiving events.</summary>
    public sealed class Subscription : IDisposable
    {
        private readonly DevToolsTrackProcessor _owner;
        private readonly ConcurrentQueue<ProfilerEvent> _queue = new();
        private int _count;

        internal Subscription(DevToolsTrackProcessor owner, string? circuitId)
        {
            _owner = owner;
            CircuitId = circuitId;
        }

        /// <summary>The circuit whose events are delivered, or null for all events.</summary>
        public string? CircuitId { get; }

        /// <summary>Events dropped because the queue was full.</summary>
        public long Dropped { get; private set; }

        /// <summary>Removes up to <paramref name="max"/> queued events.</summary>
        public List<ProfilerEvent> Drain(int max)
        {
            var list = new List<ProfilerEvent>(Math.Min(max, Volatile.Read(ref _count)));
            while (list.Count < max && _queue.TryDequeue(out var e))
            {
                Interlocked.Decrement(ref _count);
                list.Add(e);
            }

            return list;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _owner.Remove(this);
            _queue.Clear();
        }

        internal void Enqueue(ProfilerEvent e)
        {
            if (Interlocked.Increment(ref _count) > MaxQueued)
            {
                Interlocked.Decrement(ref _count);
                Dropped++;
                return;
            }

            _queue.Enqueue(e);
        }
    }
}
