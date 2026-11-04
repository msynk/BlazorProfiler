using BlazorProfiler.Events;

namespace BlazorProfiler.Storage;

/// <summary>A query over stored events.</summary>
public sealed class EventQuery
{
    /// <summary>Inclusive lower bound on <see cref="ProfilerEvent.EndTimestamp"/>.</summary>
    public long? From { get; set; }

    /// <summary>Inclusive upper bound on <see cref="ProfilerEvent.Timestamp"/>.</summary>
    public long? To { get; set; }

    /// <summary>Restricts results to these kinds.</summary>
    public IReadOnlySet<ProfilerEventKind>? Kinds { get; set; }

    /// <summary>Restricts results to one operation.</summary>
    public long? OperationId { get; set; }

    /// <summary>Restricts results to one component type.</summary>
    public string? ComponentType { get; set; }

    /// <summary>The maximum number of results (most recent events win when the limit is reached).</summary>
    public int Limit { get; set; } = int.MaxValue;

    internal bool Matches(ProfilerEvent e)
    {
        if (From is { } from && e.EndTimestamp < from)
        {
            return false;
        }

        if (To is { } to && e.Timestamp > to)
        {
            return false;
        }

        if (Kinds is not null && !Kinds.Contains(e.Kind))
        {
            return false;
        }

        if (OperationId is { } operation && e.OperationId != operation)
        {
            return false;
        }

        if (ComponentType is not null)
        {
            var type = e switch
            {
                ComponentEvent c => c.ComponentType,
                HttpRequestEvent h => h.OriginComponentType,
                JSInteropEvent j => j.OriginComponentType,
                _ => null,
            };
            if (!string.Equals(type, ComponentType, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Stores the raw events of one session.</summary>
/// <remarks>Implementations are not required to be thread-safe; the owning session serializes access.</remarks>
public interface IProfilerEventStore
{
    /// <summary>The number of retained events.</summary>
    int Count { get; }

    /// <summary>The approximate memory retained by events.</summary>
    long EstimatedBytes { get; }

    /// <summary>The total number of events appended since creation or the last clear.</summary>
    long TotalAppended { get; }

    /// <summary>The number of events evicted because of retention limits.</summary>
    long EvictedCount { get; }

    /// <summary>The sequence number of the oldest retained event (sequence numbers start at 1).</summary>
    long OldestSequence { get; }

    /// <summary>Appends an event, evicting old events as required by the retention limits.</summary>
    void Append(ProfilerEvent profilerEvent, long now);

    /// <summary>Evicts events older than the retention age.</summary>
    void EvictExpired(long now);

    /// <summary>Removes every event.</summary>
    void Clear();

    /// <summary>Returns the events matching <paramref name="query"/> in storage order.</summary>
    List<ProfilerEvent> Query(EventQuery query);

    /// <summary>
    /// Returns up to <paramref name="max"/> events whose sequence number is greater than <paramref name="afterSequence"/>,
    /// together with the sequence number of the last returned event.
    /// </summary>
    List<ProfilerEvent> GetAfter(long afterSequence, int max, out long lastSequence);

    /// <summary>Returns the retained events of one operation.</summary>
    IReadOnlyList<ProfilerEvent> GetOperation(long operationId);

    /// <summary>Copies all retained events in storage order.</summary>
    ProfilerEvent[] ToArray();
}

/// <summary>
/// A bounded, in-memory ring buffer of events with count, memory, and age limits, plus an operation index.
/// </summary>
public sealed class BoundedEventStore : IProfilerEventStore
{
    private const int InitialCapacity = 256;

    private readonly int _maxEvents;
    private readonly long _maxBytes;
    private readonly long _maxAgeTicks;
    private readonly Dictionary<long, OperationEvents> _byOperation = [];

    private ProfilerEvent[] _buffer;
    private int _head; // index of the oldest event
    private int _count;
    private long _appended;
    private long _bytes;
    private long _evicted;

    /// <summary>Creates a store.</summary>
    /// <param name="maxEvents">The maximum number of retained events.</param>
    /// <param name="maxBytes">The approximate maximum retained memory.</param>
    /// <param name="maxAgeTicks">The maximum event age in clock ticks.</param>
    public BoundedEventStore(int maxEvents, long maxBytes, long maxAgeTicks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEvents, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAgeTicks, 1);
        _maxEvents = maxEvents;
        _maxBytes = maxBytes;
        _maxAgeTicks = maxAgeTicks;
        _buffer = new ProfilerEvent[Math.Min(InitialCapacity, maxEvents)];
    }

    /// <inheritdoc />
    public int Count => _count;

    /// <inheritdoc />
    public long EstimatedBytes => _bytes;

    /// <inheritdoc />
    public long TotalAppended => _appended;

    /// <inheritdoc />
    public long EvictedCount => _evicted;

    /// <inheritdoc />
    public long OldestSequence => _appended - _count + 1;

    /// <inheritdoc />
    public void Append(ProfilerEvent profilerEvent, long now)
    {
        var size = profilerEvent.EstimateSize();
        while (_count > 0 && (_count >= _maxEvents || _bytes + size > _maxBytes))
        {
            EvictOldest();
        }

        if (_count == _buffer.Length)
        {
            Grow();
        }

        _buffer[(_head + _count) % _buffer.Length] = profilerEvent;
        _count++;
        _appended++;
        _bytes += size;

        if (profilerEvent.OperationId != 0)
        {
            if (!_byOperation.TryGetValue(profilerEvent.OperationId, out var list))
            {
                list = new OperationEvents();
                _byOperation[profilerEvent.OperationId] = list;
            }

            list.Items.Add(profilerEvent);
        }

        EvictExpired(now);
    }

    /// <inheritdoc />
    public void EvictExpired(long now)
    {
        var cutoff = now - _maxAgeTicks;
        while (_count > 1 && _buffer[_head].EndTimestamp < cutoff)
        {
            EvictOldest();
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        Array.Clear(_buffer);
        _head = 0;
        _count = 0;
        _bytes = 0;
        _appended = 0;
        _evicted = 0;
        _byOperation.Clear();
    }

    /// <inheritdoc />
    public List<ProfilerEvent> Query(EventQuery query)
    {
        var results = new List<ProfilerEvent>();
        for (var i = 0; i < _count; i++)
        {
            var e = _buffer[(_head + i) % _buffer.Length];
            if (query.Matches(e))
            {
                results.Add(e);
            }
        }

        if (results.Count > query.Limit)
        {
            results.RemoveRange(0, results.Count - query.Limit);
        }

        return results;
    }

    /// <inheritdoc />
    public List<ProfilerEvent> GetAfter(long afterSequence, int max, out long lastSequence)
    {
        var oldest = OldestSequence;
        var startSequence = Math.Max(afterSequence + 1, oldest);
        var results = new List<ProfilerEvent>(Math.Clamp((int)Math.Min(max, _appended - startSequence + 1), 0, 4096));
        lastSequence = afterSequence;
        for (var sequence = startSequence; sequence <= _appended && results.Count < max; sequence++)
        {
            var logical = (int)(sequence - oldest);
            results.Add(_buffer[(_head + logical) % _buffer.Length]);
            lastSequence = sequence;
        }

        if (results.Count == 0 && afterSequence > _appended)
        {
            // The cursor is ahead of the store (for example after Clear); restart from the beginning.
            lastSequence = _appended;
        }

        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<ProfilerEvent> GetOperation(long operationId) =>
        _byOperation.TryGetValue(operationId, out var list) ? list.ToArray() : [];

    /// <inheritdoc />
    public ProfilerEvent[] ToArray()
    {
        var result = new ProfilerEvent[_count];
        for (var i = 0; i < _count; i++)
        {
            result[i] = _buffer[(_head + i) % _buffer.Length];
        }

        return result;
    }

    private void EvictOldest()
    {
        var e = _buffer[_head];
        _buffer[_head] = null!;
        _head = (_head + 1) % _buffer.Length;
        _count--;
        _evicted++;
        _bytes -= e.EstimateSize();
        if (_bytes < 0)
        {
            _bytes = 0;
        }

        if (e.OperationId != 0 && _byOperation.TryGetValue(e.OperationId, out var list))
        {
            list.RemoveOldest(e);
            if (list.Count == 0)
            {
                _byOperation.Remove(e.OperationId);
            }
        }
    }

    private void Grow()
    {
        var newCapacity = (int)Math.Min((long)_buffer.Length * 2, _maxEvents);
        if (newCapacity <= _buffer.Length)
        {
            newCapacity = _buffer.Length + 1;
        }

        var newBuffer = new ProfilerEvent[newCapacity];
        for (var i = 0; i < _count; i++)
        {
            newBuffer[i] = _buffer[(_head + i) % _buffer.Length];
        }

        _buffer = newBuffer;
        _head = 0;
    }

    /// <summary>
    /// The retained events of one operation. Events are evicted in insertion order, so removal advances a start offset
    /// instead of shifting the list, keeping eviction O(1) amortized for large operations.
    /// </summary>
    private sealed class OperationEvents
    {
        public List<ProfilerEvent> Items { get; } = new(4);

        private int _start;

        public int Count => Items.Count - _start;

        public void RemoveOldest(ProfilerEvent e)
        {
            if (_start < Items.Count && ReferenceEquals(Items[_start], e))
            {
                Items[_start] = null!;
                _start++;
            }
            else
            {
                var index = Items.IndexOf(e, _start);
                if (index < 0)
                {
                    return;
                }

                Items.RemoveAt(index);
            }

            if (_start > 32 && _start * 2 > Items.Count)
            {
                Items.RemoveRange(0, _start);
                _start = 0;
            }
        }

        public ProfilerEvent[] ToArray()
        {
            var result = new ProfilerEvent[Count];
            Items.CopyTo(_start, result, 0, result.Length);
            return result;
        }
    }
}
