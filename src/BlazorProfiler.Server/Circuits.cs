using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using BlazorProfiler.Collectors;
using BlazorProfiler.Correlation;
using BlazorProfiler.Events;
using BlazorProfiler.Sessions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorProfiler.Server;

/// <summary>Tracks live circuits, their synchronization contexts, and their owners.</summary>
/// <remarks>Owner keys are kept in memory for authorization only and are never written to events or exported.</remarks>
public sealed class CircuitRegistry
{
    private readonly ConcurrentDictionary<string, CircuitEntry> _circuits = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<SynchronizationContext, string> _contexts = [];

    /// <summary>Registers a circuit and the synchronization context of its renderer.</summary>
    public void Register(string circuitId, SynchronizationContext? context, string? ownerKey, DateTimeOffset openedAt)
    {
        _circuits[circuitId] = new CircuitEntry(circuitId, ownerKey, openedAt) { Connected = true };
        if (context is not null)
        {
            _contexts.AddOrUpdate(context, circuitId);
        }
    }

    /// <summary>Updates the connection state of a circuit.</summary>
    public void SetConnected(string circuitId, bool connected)
    {
        if (_circuits.TryGetValue(circuitId, out var entry))
        {
            entry.Connected = connected;
        }
    }

    /// <summary>Removes a circuit.</summary>
    public void Remove(string circuitId) => _circuits.TryRemove(circuitId, out _);

    /// <summary>Returns the circuit whose renderer owns a synchronization context.</summary>
    public string? GetCircuitId(SynchronizationContext? context) =>
        context is not null && _contexts.TryGetValue(context, out var id) ? id : null;

    /// <summary>Returns the circuits owned by a principal key.</summary>
    public IReadOnlySet<string> GetOwned(string? ownerKey)
    {
        if (string.IsNullOrEmpty(ownerKey))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return _circuits.Values.Where(c => string.Equals(c.OwnerKey, ownerKey, StringComparison.Ordinal)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Returns live circuits, optionally restricted to a set of identifiers.</summary>
    public IReadOnlyList<CircuitDescription> GetCircuits(Func<string, bool> visible) =>
        [.. _circuits.Values.Where(c => visible(c.Id)).OrderBy(c => c.OpenedAt).Select(c => new CircuitDescription { CircuitId = c.Id, Connected = c.Connected, OpenedAt = c.OpenedAt, Authenticated = c.OwnerKey is not null })];

    /// <summary>Computes the owner key of a principal: the name identifier claim, or the name, of an authenticated user.</summary>
    public static string? GetOwnerKey(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.Identity.Name;
    }

    private sealed class CircuitEntry(string id, string? ownerKey, DateTimeOffset openedAt)
    {
        public string Id { get; } = id;

        public string? OwnerKey { get; } = ownerKey;

        public DateTimeOffset OpenedAt { get; } = openedAt;

        public bool Connected { get; set; }
    }
}

/// <summary>A live circuit as returned to dashboards.</summary>
public sealed class CircuitDescription
{
    /// <summary>The circuit identifier (not the secret used by the client to reconnect).</summary>
    public string CircuitId { get; set; } = string.Empty;

    /// <summary>Whether the circuit is connected.</summary>
    public bool Connected { get; set; }

    /// <summary>When the circuit opened.</summary>
    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>Whether the circuit belongs to an authenticated user.</summary>
    public bool Authenticated { get; set; }
}

/// <summary>Collects Blazor Server circuit lifecycle and inbound activity.</summary>
public sealed class CircuitCollector : ProfilerCollectorBase
{
    /// <inheritdoc />
    public override CollectorDescriptor Descriptor { get; } = new()
    {
        Name = "circuits",
        Description = "Circuit open/close and connection up/down events, and the duration of every inbound circuit activity (browser events, interop callbacks, render acknowledgements, navigation) by hub method.",
        Mechanism = "A CircuitHandler (OnCircuitOpened/Closed, OnConnectionUp/Down, CreateInboundActivityHandler) plus a SignalR IHubFilter that names the hub method behind each inbound activity.",
        Capability = CapabilityLevel.Full,
        Overhead = OverheadLevel.Low,
        Compatibility = "Blazor Server and Interactive Server render mode (.NET 8+ inbound activity handlers).",
        Degradation = "When the hub filter does not run for the component hub, activities are recorded without a hub method name.",
    };

    /// <summary>Whether circuit data should be recorded.</summary>
    public bool IsCollecting => IsActive;

    /// <summary>The profiler, once started.</summary>
    internal IProfiler? CurrentProfiler => Profiler;

    /// <summary>Emits an event.</summary>
    public bool Publish(ProfilerEvent profilerEvent) => Emit(profilerEvent);

    /// <inheritdoc />
    protected override bool IsEnabled(ProfilerOptions options) => options.Collectors.Circuits;

    /// <inheritdoc />
    protected override void OnStart(ProfilerCollectorContext context)
    {
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
    }
}

/// <summary>Attributes events to circuits using the renderer's synchronization context.</summary>
/// <remarks>
/// Each circuit's renderer has its own synchronization context, and framework measurements are recorded on it. The context
/// is captured when the circuit opens (circuit handlers run on the circuit's dispatcher), so work that is not part of an
/// inbound activity (for example a timer calling <c>InvokeAsync</c>) is still attributed to the right circuit.
/// </remarks>
public sealed class CircuitCorrelationProvider : IProfilerCorrelationProvider
{
    private readonly CircuitRegistry _registry;

    /// <summary>Creates the provider.</summary>
    public CircuitCorrelationProvider(CircuitRegistry registry) => _registry = registry;

    /// <inheritdoc />
    public void Enrich(ProfilerEvent profilerEvent)
    {
        profilerEvent.CircuitId ??= _registry.GetCircuitId(SynchronizationContext.Current);
        if (profilerEvent is RenderBatchEvent { IncludesClientRoundTrip: null } batch)
        {
            // Circuit batches complete when the browser acknowledges them; static SSR batches complete immediately.
            batch.IncludesClientRoundTrip = batch.CircuitId is not null;
        }
    }
}

/// <summary>Names the SignalR hub method behind each circuit inbound activity.</summary>
public sealed class ProfilerHubFilter : IHubFilter
{
    private const string ComponentHubName = "Microsoft.AspNetCore.Components.Server.ComponentHub";

    private static readonly AsyncLocal<HubInvocationInfo?> s_current = new();

    /// <summary>The hub invocation currently being processed, if any.</summary>
    internal static HubInvocationInfo? Current => s_current.Value;

    /// <inheritdoc />
    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (!string.Equals(invocationContext.Hub.GetType().FullName, ComponentHubName, StringComparison.Ordinal))
        {
            return next(invocationContext);
        }

        s_current.Value = new HubInvocationInfo(invocationContext.HubMethodName, IsProfilerInternal(invocationContext));
        return next(invocationContext);
    }

    private static bool IsProfilerInternal(HubInvocationContext context)
    {
        if (context.HubMethodName != "BeginInvokeDotNetFromJS")
        {
            return false;
        }

        foreach (var argument in context.HubMethodArguments)
        {
            if (argument is string text && text.StartsWith("BlazorProfiler.", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal sealed record HubInvocationInfo(string Method, bool ProfilerInternal);
}

/// <summary>A <see cref="CircuitHandler"/> that records circuit lifecycle and inbound activities.</summary>
public sealed class ProfilerCircuitHandler : CircuitHandler
{
    private readonly CircuitCollector _collector;
    private readonly CircuitRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly ProfilerServerOptions _options;
    private string? _sessionId;

    /// <summary>Creates the handler.</summary>
    public ProfilerCircuitHandler(CircuitCollector collector, CircuitRegistry registry, IServiceProvider services, ProfilerServerOptions options)
    {
        _collector = collector;
        _registry = registry;
        _services = services;
        _options = options;
    }

    /// <summary>Runs before other circuit handlers so their work is attributed to the circuit.</summary>
    public override int Order => int.MinValue + 100;

    /// <inheritdoc />
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        string? owner = null;
        try
        {
            var authentication = _services.GetService<AuthenticationStateProvider>();
            if (authentication is not null)
            {
                owner = CircuitRegistry.GetOwnerKey((await authentication.GetAuthenticationStateAsync()).User);
            }
        }
        catch (Exception)
        {
            // Authentication may not be configured; the circuit is then anonymous.
        }

        _registry.Register(circuit.Id, SynchronizationContext.Current, owner, DateTimeOffset.UtcNow);
        Record(circuit, CircuitChange.Opened);

        if (_options.AutoCircuitSessions && _collector.CurrentProfiler is { IsEnabled: true } profiler)
        {
            try
            {
                _sessionId = profiler.StartSession(new ProfilerSessionOptions { Name = "Circuit " + circuit.Id[..Math.Min(8, circuit.Id.Length)], CircuitId = circuit.Id }).Id;
            }
            catch (InvalidOperationException)
            {
                // Session limit reached; circuit data remains available in process-wide sessions.
            }
        }
    }

    /// <inheritdoc />
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _registry.SetConnected(circuit.Id, true);
        Record(circuit, CircuitChange.ConnectionUp);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _registry.SetConnected(circuit.Id, false);
        Record(circuit, CircuitChange.ConnectionDown);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Record(circuit, CircuitChange.Closed);
        _registry.Remove(circuit.Id);
        if (_sessionId is not null && _collector.CurrentProfiler?.GetSession(_sessionId) is { } session)
        {
            session.Stop();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        context => HandleInboundActivityAsync(context, next);

    private Task HandleInboundActivityAsync(CircuitInboundActivityContext context, Func<CircuitInboundActivityContext, Task> next)
    {
        var profiler = _collector.CurrentProfiler;
        if (!_collector.IsCollecting || profiler is null)
        {
            return next(context);
        }

        var hub = ProfilerHubFilter.Current;
        if (hub?.ProfilerInternal == true)
        {
            return next(context);
        }

        return TrackAsync(context, next, profiler, hub?.Method);
    }

    private async Task TrackAsync(CircuitInboundActivityContext context, Func<CircuitInboundActivityContext, Task> next, IProfiler profiler, string? method)
    {
        var start = profiler.Clock.GetTimestamp();

        // Flow the circuit identifier with the activity so everything it triggers is attributed to the circuit.
        ProfilerContext.Push(new OperationFrame(0, OperationKind.ComponentScope, CorrelationKind.None)
        {
            CircuitId = context.Circuit.Id,
            Parent = ProfilerContext.Current,
            StartTimestamp = start,
        });

        Exception? failure = null;
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            _collector.Publish(new CircuitActivityEvent
            {
                CircuitId = context.Circuit.Id,
                ActivityName = method,
                Timestamp = start,
                Duration = profiler.Clock.GetTimestamp() - start,
                Outcome = failure is null ? ProfilerOutcome.Success : failure is OperationCanceledException ? ProfilerOutcome.Canceled : ProfilerOutcome.Error,
                ErrorType = failure?.GetType().FullName,
            });
        }
    }

    private void Record(Circuit circuit, CircuitChange change)
    {
        if (!_collector.IsCollecting || _collector.CurrentProfiler is not { } profiler)
        {
            return;
        }

        _collector.Publish(new CircuitEvent { CircuitId = circuit.Id, Change = change, Timestamp = profiler.Clock.GetTimestamp() });
    }
}
