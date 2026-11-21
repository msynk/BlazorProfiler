using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using BlazorProfiler.Events;
using BlazorProfiler.Interop;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlazorProfiler.Server;

/// <summary>
/// Measures the size of Blazor Server circuit messages: render batches sent to the browser, messages received from it, and
/// the arguments of .NET-to-JavaScript calls.
/// </summary>
/// <remarks>
/// Sizes are read from the circuit's SignalR hub protocol (<c>blazorpack</c>), which the profiler wraps. Render-batch sizes
/// are attached to <see cref="RenderBatchEvent.TransportBytes"/>, received sizes to
/// <see cref="CircuitActivityEvent.ReceivedBytes"/>, and argument sizes to <see cref="JSInteropEvent.PayloadBytes"/>
/// (calls made through <c>IProfiledJSRuntime</c>).
/// </remarks>
public sealed class CircuitTransport
{
    /// <summary>The name of Blazor's hub protocol.</summary>
    internal const string BlazorPackProtocol = "blazorpack";

    /// <summary>The client method that receives render batches.</summary>
    internal const string RenderBatchTarget = "JS.RenderBatch";

    /// <summary>The client method that starts a .NET-to-JavaScript call.</summary>
    internal const string BeginInvokeJSTarget = "JS.BeginInvokeJS";

    /// <summary>
    /// The client method that receives a <c>byte[]</c> argument of a .NET-to-JavaScript call: the framework sends each one
    /// in a message of its own (as binary) before <see cref="BeginInvokeJSTarget"/>, whose JSON holds only a placeholder.
    /// </summary>
    internal const string ReceiveByteArrayTarget = "JS.ReceiveByteArray";

    // Batches in flight on one circuit are few; the bound only matters when their events are not recorded.
    private const int MaxPendingBatchesPerCircuit = 256;

    private readonly CircuitRegistry _registry;
    private readonly CircuitCollector _collector;
    private readonly ConditionalWeakTable<object?[], StrongBox<long>> _received = [];
    private readonly ConcurrentDictionary<string, Queue<SentBatch>> _sent = new(StringComparer.Ordinal);

    /// <summary>Creates the transport measurement.</summary>
    public CircuitTransport(CircuitRegistry registry, CircuitCollector collector)
    {
        _registry = registry;
        _collector = collector;
    }

    /// <summary>Whether messages should be measured.</summary>
    internal bool IsCollecting => _collector.IsCollecting;

    /// <summary>Records the size of a parsed hub invocation, keyed by its argument array.</summary>
    internal void RecordReceived(object?[] arguments, long bytes) => _received.AddOrUpdate(arguments, new StrongBox<long>(bytes));

    /// <summary>Returns and forgets the size of the hub invocation whose arguments are <paramref name="arguments"/>.</summary>
    internal long? TakeReceived(object?[]? arguments)
    {
        if (arguments is null || !_received.TryGetValue(arguments, out var bytes))
        {
            return null;
        }

        _received.Remove(arguments);
        return bytes.Value;
    }

    /// <summary>
    /// Records a render batch written to a circuit. Batches are written on the circuit's dispatcher, whose synchronization
    /// context identifies the circuit; a batch written elsewhere (after waiting for the connection) is not attributed.
    /// </summary>
    internal void RecordBatchSent(long bytes)
    {
        if (_collector.CurrentProfiler is not { } profiler || _registry.GetCircuitId(SynchronizationContext.Current) is not { } circuitId)
        {
            return;
        }

        var pending = _sent.GetOrAdd(circuitId, static _ => new Queue<SentBatch>());
        lock (pending)
        {
            if (pending.Count >= MaxPendingBatchesPerCircuit)
            {
                pending.Dequeue();
            }

            pending.Enqueue(new SentBatch(profiler.Clock.GetTimestamp(), bytes));
        }
    }

    /// <summary>
    /// Returns the size of the message that carried a recorded render batch: the first batch written on its circuit
    /// between the batch's start and its acknowledgement. Batches are written and acknowledged in order, so earlier writes
    /// belong to batches that were not recorded and are discarded.
    /// </summary>
    internal long? TakeBatchBytes(RenderBatchEvent batch)
    {
        if (batch.CircuitId is null || !_sent.TryGetValue(batch.CircuitId, out var pending))
        {
            return null;
        }

        lock (pending)
        {
            while (pending.TryPeek(out var head))
            {
                if (head.Timestamp < batch.Timestamp)
                {
                    pending.Dequeue();
                    continue;
                }

                if (head.Timestamp <= batch.EndTimestamp)
                {
                    pending.Dequeue();
                    return head.Bytes;
                }

                break;
            }
        }

        return null;
    }

    /// <summary>Forgets the pending batches of a closed circuit.</summary>
    internal void Forget(string circuitId) => _sent.TryRemove(circuitId, out _);

    /// <summary>
    /// Wraps the hub protocol resolver so Blazor's hub protocol is measured. Registration order does not matter: when
    /// SignalR registers its resolver later, it keeps the profiler's (it only adds its own when none exists).
    /// </summary>
    internal static void Register(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(CircuitTransport)))
        {
            return;
        }

        services.AddSingleton<CircuitTransport>();
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IHubProtocolResolver) && !d.IsKeyedService);
        Func<IServiceProvider, IHubProtocolResolver>? inner = existing is not null
            ? sp => Create(existing, sp)
            : DefaultResolverType() is { } type ? sp => (IHubProtocolResolver)ActivatorUtilities.CreateInstance(sp, type) : null;
        if (inner is null)
        {
            // SignalR's resolver could not be found; messages are not measured.
            return;
        }

        services.RemoveAll<IHubProtocolResolver>();
        services.AddSingleton<IHubProtocolResolver>(sp => new ProfilingHubProtocolResolver(inner(sp), sp.GetRequiredService<CircuitTransport>()));
    }

    private static IHubProtocolResolver Create(ServiceDescriptor descriptor, IServiceProvider services) =>
        (IHubProtocolResolver)(descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(services)
            ?? ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!));

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Server applications are not trimmed; a missing type disables measurement.")]
    private static Type? DefaultResolverType() =>
        typeof(IHubProtocolResolver).Assembly.GetType("Microsoft.AspNetCore.SignalR.Internal.DefaultHubProtocolResolver")
        ?? Type.GetType("Microsoft.AspNetCore.SignalR.Internal.DefaultHubProtocolResolver, Microsoft.AspNetCore.SignalR.Core");

    private readonly record struct SentBatch(long Timestamp, long Bytes);
}

/// <summary>Returns the registered hub protocols, with Blazor's protocol wrapped for measurement.</summary>
internal sealed class ProfilingHubProtocolResolver : IHubProtocolResolver
{
    private readonly IHubProtocolResolver _inner;
    private readonly CircuitTransport _transport;
    private readonly ConcurrentDictionary<IHubProtocol, IHubProtocol> _wrapped = new(ReferenceEqualityComparer.Instance);

    public ProfilingHubProtocolResolver(IHubProtocolResolver inner, CircuitTransport transport)
    {
        _inner = inner;
        _transport = transport;
        AllProtocols = [.. inner.AllProtocols.Select(Wrap)];
    }

    public IReadOnlyList<IHubProtocol> AllProtocols { get; }

    public IHubProtocol? GetProtocol(string protocolName, IReadOnlyList<string>? supportedProtocols) =>
        _inner.GetProtocol(protocolName, supportedProtocols) is { } protocol ? Wrap(protocol) : null;

    private IHubProtocol Wrap(IHubProtocol protocol) =>
        string.Equals(protocol.Name, CircuitTransport.BlazorPackProtocol, StringComparison.OrdinalIgnoreCase) && protocol is not ProfilingHubProtocol
            ? _wrapped.GetOrAdd(protocol, static (p, transport) => new ProfilingHubProtocol(p, transport), _transport)
            : protocol;
}

/// <summary>Delegates to Blazor's hub protocol and measures the messages it reads and writes.</summary>
internal sealed class ProfilingHubProtocol : IHubProtocol
{
    private readonly IHubProtocol _inner;
    private readonly CircuitTransport _transport;

    public ProfilingHubProtocol(IHubProtocol inner, CircuitTransport transport)
    {
        _inner = inner;
        _transport = transport;
    }

    public string Name => _inner.Name;

    public int Version => _inner.Version;

    public TransferFormat TransferFormat => _inner.TransferFormat;

    public bool IsVersionSupported(int version) => _inner.IsVersionSupported(version);

    public bool TryParseMessage(ref ReadOnlySequence<byte> input, IInvocationBinder binder, [NotNullWhen(true)] out HubMessage? message)
    {
        var before = input.Length;
        if (!_inner.TryParseMessage(ref input, binder, out message))
        {
            return false;
        }

        if (message is InvocationMessage { Arguments: { Length: > 0 } arguments } && _transport.IsCollecting)
        {
            _transport.RecordReceived(arguments, before - input.Length);
        }

        return true;
    }

    public void WriteMessage(HubMessage message, IBufferWriter<byte> output)
    {
        if (message is not InvocationMessage invocation || !_transport.IsCollecting)
        {
            _inner.WriteMessage(message, output);
            return;
        }

        if (string.Equals(invocation.Target, CircuitTransport.RenderBatchTarget, StringComparison.Ordinal))
        {
            var counting = new CountingBufferWriter(output);
            _inner.WriteMessage(message, counting);
            _transport.RecordBatchSent(counting.Written);
            return;
        }

        ReportInteropArguments(invocation);
        _inner.WriteMessage(message, output);
    }

    public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message)
    {
        var bytes = _inner.GetMessageBytes(message);
        if (message is InvocationMessage invocation && _transport.IsCollecting)
        {
            if (string.Equals(invocation.Target, CircuitTransport.RenderBatchTarget, StringComparison.Ordinal))
            {
                _transport.RecordBatchSent(bytes.Length);
            }
            else
            {
                ReportInteropArguments(invocation);
            }
        }

        return bytes;
    }

    /// <summary>
    /// Reports the size of the arguments of a .NET-to-JavaScript call (<c>JS.BeginInvokeJS</c>: call id, identifier,
    /// arguments JSON, ...; and <c>JS.ReceiveByteArray</c>: id, bytes for each <c>byte[]</c> argument) to the interop
    /// decorator waiting on this thread.
    /// </summary>
    private static void ReportInteropArguments(InvocationMessage invocation)
    {
        if (!InteropMessageSize.IsListening || invocation.Arguments is not { } arguments)
        {
            return;
        }

        if (string.Equals(invocation.Target, CircuitTransport.BeginInvokeJSTarget, StringComparison.Ordinal)
            && arguments.Length > 2
            && arguments[1] is string
            && arguments[2] is null or string)
        {
            InteropMessageSize.Report(arguments[2] is string json ? Encoding.UTF8.GetByteCount(json) : 0);
        }
        else if (string.Equals(invocation.Target, CircuitTransport.ReceiveByteArrayTarget, StringComparison.Ordinal)
            && arguments.Length > 1
            && arguments[1] is byte[] bytes)
        {
            InteropMessageSize.Report(bytes.Length);
        }
    }

    /// <summary>Counts the bytes written through a buffer writer.</summary>
    private sealed class CountingBufferWriter(IBufferWriter<byte> inner) : IBufferWriter<byte>
    {
        public long Written { get; private set; }

        public void Advance(int count)
        {
            Written += count;
            inner.Advance(count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
    }
}
