using System.Runtime.CompilerServices;
using BlazorProfiler.Analysis;
using BlazorProfiler.Collectors;
using BlazorProfiler.Events;
using BlazorProfiler.Tests.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BlazorProfiler.Tests;

[Collection(nameof(RetentionTests))]
public class RetainerInspectorTests
{
    private static readonly Func<Type, bool> IsService = t => t == typeof(Service);

    public sealed class Service
    {
        public event Action? Changed;

        public List<Action> Listeners { get; } = [];

        public HashSet<Action> Set { get; } = [];

        public Dictionary<string, Action> Named { get; } = new(StringComparer.Ordinal);

        public bool HasSubscribers => Changed is not null;

        public void Notify() => Changed?.Invoke();
    }

    /// <summary>A plain class receiving a service through its constructor (constructor injection).</summary>
    public sealed class Subscriber
    {
        private readonly Service _service;

        public Subscriber(Service service, bool lambda = false)
        {
            _service = service;
            if (lambda)
            {
                // Captures a local as well as this, so the compiler generates a closure object.
                var calls = 0;
                _service.Changed += () =>
                {
                    calls++;
                    Refresh();
                };
            }
            else
            {
                _service.Changed += Refresh;
            }
        }

        public int Refreshes { get; private set; }

        public void Refresh() => Refreshes++;
    }

    /// <summary>Receives the service through an [Inject] property, as Razor components do.</summary>
    public sealed class InjectedSubscriber
    {
        [Inject]
        public Service State { get; set; } = default!;

        public void Subscribe() => State.Changed += Refresh;

        public void Refresh()
        {
        }
    }

    /// <summary>Owns its helper: a subscription to it is a reference cycle, which the garbage collector reclaims.</summary>
    public sealed class Owner
    {
        private readonly Service _helper = new();

        public Owner() => _helper.Changed += Refresh;

        public void Refresh() => _ = _helper;
    }

    public sealed class Holder
    {
        public static event Action? StaticChanged;

        private DotNetObjectReference<Holder>? _reference;
        private Timer? _timer;

        public static void RaiseStatic() => StaticChanged?.Invoke();

        public void SubscribeStatic() => StaticChanged += OnStatic;

        public void UnsubscribeStatic() => StaticChanged -= OnStatic;

        public void CreateReference(bool dispose)
        {
            _reference = DotNetObjectReference.Create(this);
            if (dispose)
            {
                _reference.Dispose();
            }
        }

        public void StartTimer() => _timer = new Timer(_ => OnStatic(), null, Timeout.Infinite, Timeout.Infinite);

        public void OnStatic() => _ = _timer;
    }

    /// <summary>Holds delegates in framework collections of an injected service.</summary>
    public sealed class CollectionSubscriber
    {
        [Inject]
        public Service State { get; set; } = default!;

        public void Subscribe(string where)
        {
            switch (where)
            {
                case "list":
                    State.Listeners.Add(Refresh);
                    break;
                case "set":
                    State.Set.Add(Refresh);
                    break;
                default:
                    State.Named["me"] = Refresh;
                    break;
            }
        }

        public void Refresh()
        {
        }
    }

    [Fact]
    public void FindsEventHandlersOnServicesReceivedThroughTheConstructor()
    {
        var service = new Service();
        var subscriber = new Subscriber(service);

        var hint = Assert.Single(RetainerInspector.Find(subscriber, IsService)!);
        Assert.StartsWith("Service.Changed → Subscriber.Refresh", hint, StringComparison.Ordinal);
        Assert.False(ComponentRetainedEvent.IsTentative(hint));

        // Without knowing that Service is a registered service, a constructor-assigned field could be an owned helper.
        Assert.Null(RetainerInspector.Find(subscriber));
    }

    [Fact]
    public void FindsEventHandlersOnInjectedProperties()
    {
        var subscriber = new InjectedSubscriber { State = new Service() };
        subscriber.Subscribe();
        var hint = Assert.Single(RetainerInspector.Find(subscriber)!);
        Assert.StartsWith("Service.Changed → InjectedSubscriber.Refresh", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoresSubscriptionsToObjectsTheInstanceOwns()
    {
        Assert.Null(RetainerInspector.Find(new Owner(), _ => false));
    }

    [Fact]
    public void FindsLambdasThatCaptureTheInstance()
    {
        var subscriber = new Subscriber(new Service(), lambda: true);
        var hint = Assert.Single(RetainerInspector.Find(subscriber, IsService)!);
        Assert.Contains("Service.Changed → Subscriber.", hint, StringComparison.Ordinal);
        Assert.Contains("(lambda)", hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list", "Listeners[]")]
    [InlineData("set", "Set[]")]
    [InlineData("dictionary", "Named[]")]
    public void FindsDelegatesInFrameworkCollections(string where, string member)
    {
        var subscriber = new CollectionSubscriber { State = new Service() };
        subscriber.Subscribe(where);
        var hint = Assert.Single(RetainerInspector.Find(subscriber)!);
        Assert.StartsWith($"Service.{member} → CollectionSubscriber.Refresh", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsStaticEventsAndReportsReferencesAndTimersAsTentative()
    {
        var holder = new Holder();
        Assert.Null(RetainerInspector.Find(holder));

        holder.SubscribeStatic();
        holder.CreateReference(dispose: false);
        holder.StartTimer();
        try
        {
            var hints = RetainerInspector.Find(holder)!;
            var staticHint = Assert.Single(hints, h => h.StartsWith("static Holder.StaticChanged → Holder.OnStatic", StringComparison.Ordinal));
            Assert.False(ComponentRetainedEvent.IsTentative(staticHint));
            Assert.True(ComponentRetainedEvent.IsTentative(Assert.Single(hints, h => h.Contains("(DotNetObjectReference) was not disposed", StringComparison.Ordinal))));
            Assert.True(ComponentRetainedEvent.IsTentative(Assert.Single(hints, h => h.Contains("Holder._timer (Timer)", StringComparison.Ordinal))));
        }
        finally
        {
            holder.UnsubscribeStatic();
        }

        var disposed = new Holder();
        disposed.CreateReference(dispose: true);
        Assert.Null(RetainerInspector.Find(disposed));
    }

    [Fact]
    public async Task LeakCheckReportsTheEventSubscriptionThatRetainsAComponent()
    {
        await using var f = ProfilerFixture.Create(
            o =>
            {
                o.Collectors.RetentionMinimumAge = TimeSpan.Zero;
                o.Collectors.RetentionGcCycles = 1;
            },
            services => services.AddSingleton<AppState>());
        var id = await RenderAndForget<SubscribingHost>(f.Renderer);
        await f.Renderer.SetParametersAsync(id, new() { ["Show"] = false });

        await f.Root.GetRequiredService<Api.ProfilerApi>().ExecuteCommandAsync(Api.ProfilerApiAccess.Full, "check-leaks", null);
        await f.Host.FlushAsync();

        var retained = Assert.Single(f.All<ComponentRetainedEvent>(), e => e.ComponentType == typeof(SubscribingChild).FullName);
        Assert.Contains(retained.RetainedBy!, h => h.StartsWith("AppState.Changed → SubscribingChild.StateHasChanged", StringComparison.Ordinal));

        var finding = Assert.Single(f.Host.Diagnostics.Analyze(f.Session).Findings, x => x.RuleId == "BP5004" && x.ComponentType == typeof(SubscribingChild).FullName);
        Assert.Equal(DiagnosticConfidence.High, finding.Confidence);
        Assert.NotEqual(DiagnosticSeverity.Info, finding.Severity);
        Assert.Contains(finding.Evidence, e => e.Label == "Likely retained by");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> RenderAndForget<T>(TestRenderer renderer)
        where T : IComponent
    {
        var (_, id) = await renderer.RenderAsync<T>();
        return id;
    }
}
