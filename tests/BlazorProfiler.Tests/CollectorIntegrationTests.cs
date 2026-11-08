using System.Diagnostics.Metrics;
using BlazorProfiler.Collectors;
using BlazorProfiler.Correlation;
using BlazorProfiler.Events;
using BlazorProfiler.Instrumentation;
using BlazorProfiler.Storage;
using BlazorProfiler.Tests.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorProfiler.Tests;

public class FrameworkMetricsTests
{
    [Fact]
    public async Task RecordsParametersBatchesAndHandlersForPlainComponents()
    {
        await using var f = ProfilerFixture.Create();
        await f.Renderer.RenderAsync<PlainCounter>();
        await f.Renderer.ClickAsync();

        var parameters = f.All<ParametersSetEvent>();
        Assert.Contains(parameters, p => p.ComponentType == typeof(PlainChild).FullName && p.Source == InstrumentationSource.FrameworkMetrics);
        Assert.All(parameters, p => Assert.True(p.Duration >= 0));

        var batches = f.All<RenderBatchEvent>();
        Assert.Equal(f.Renderer.BatchSizes, batches.Select(b => b.UpdatedComponentCount));

        var handler = Assert.Single(f.All<EventHandlerEvent>());
        Assert.Equal(typeof(PlainCounter).FullName, handler.ComponentType);
        Assert.Equal("Increment", handler.MethodName);
        Assert.Equal("onclick", handler.AttributeName);
        Assert.Equal(CorrelationKind.Self, handler.Correlation);
        Assert.NotEqual(0, handler.OperationId);
    }

    [Fact]
    public async Task RenderBatchAfterSynchronousHandlerIsCorrelatedToTheInteraction()
    {
        await using var f = ProfilerFixture.Create();
        await f.Renderer.RenderAsync<PlainCounter>();
        var before = f.All<RenderBatchEvent>().Count;
        await f.Renderer.ClickAsync();

        var handler = Assert.Single(f.All<EventHandlerEvent>());
        var batch = f.All<RenderBatchEvent>().Skip(before).First();
        Assert.Equal(handler.OperationId, batch.OperationId);
        Assert.Equal(CorrelationKind.ExecutionContext, batch.Correlation);

        // Only PlainChild with the changed value re-rendered with the counter (the constant child is skipped by the framework).
        Assert.Equal(2, batch.UpdatedComponentCount);
        var childUpdate = f.All<ParametersSetEvent>().Last();
        Assert.Equal(handler.OperationId, childUpdate.OperationId);
    }

    [Fact]
    public async Task PendingRenderBatchesOfOtherThreadsDoNotSurviveStoppingCollection()
    {
        // A batch whose size is never reported stays pending on the thread that recorded it. Stopping collection must clear
        // it on every thread: otherwise an idle pool thread would keep the closed circuit's context alive and emit the stale
        // batch into the next recording when it renders again.
        await using var f = ProfilerFixture.Create();
        using var meter = f.Root.GetService<IMeterFactory>() is { } factory
            ? factory.Create(ComponentMetricsCollector.LifecycleMeter)
            : new Meter(ComponentMetricsCollector.LifecycleMeter);
        var duration = meter.CreateHistogram<double>("aspnetcore.components.render_diff.duration");
        var size = meter.CreateHistogram<int>("aspnetcore.components.render_diff.size");
        const double StaleSeconds = 0.123456;
        const double FreshSeconds = 0.234567;

        using var worker = new DedicatedThread();
        worker.Run(() => duration.Record(StaleSeconds));
        f.Session.Stop();

        var next = f.Host.StartSession();
        worker.Run(() =>
        {
            duration.Record(FreshSeconds);
            size.Record(3);
        });

        var batches = next.GetEvents(new EventQuery()).OfType<RenderBatchEvent>().ToList();
        Assert.DoesNotContain(batches, b => b.Duration == f.Host.Clock.FromSeconds(StaleSeconds));
        var fresh = Assert.Single(batches, b => b.Duration == f.Host.Clock.FromSeconds(FreshSeconds));
        Assert.Equal(3, fresh.UpdatedComponentCount);
    }

    /// <summary>Runs actions on one dedicated thread, like a pool thread reused by successive renders.</summary>
    private sealed class DedicatedThread : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(Action Action, TaskCompletionSource Done)> _queue = [];
        private readonly Thread _thread;

        public DedicatedThread()
        {
            _thread = new Thread(() =>
            {
                foreach (var (action, done) in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        action();
                        done.SetResult();
                    }
                    catch (Exception ex)
                    {
                        done.SetException(ex);
                    }
                }
            })
            { IsBackground = true };
            _thread.Start();
        }

        public void Run(Action action)
        {
            var done = new TaskCompletionSource();
            _queue.Add((action, done));
            done.Task.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
        }
    }

    [Fact]
    public async Task AmbientInteractionDoesNotLeakIntoLaterWork()
    {
        await using var f = ProfilerFixture.Create();
        var (component, _) = await f.Renderer.RenderAsync<ProfiledCounter>();
        await f.Renderer.ClickAsync();
        var countBefore = f.All<ComponentRenderEvent>().Count;
        await f.Renderer.Dispatcher.InvokeAsync(component.Touch);
        var later = f.All<ComponentRenderEvent>().Skip(countBefore).ToList();
        Assert.NotEmpty(later);
        Assert.All(later, r => Assert.Equal(0, r.OperationId));
        Assert.Null(ProfilerContext.Current);
    }

    [Fact]
    public async Task DoesNotLeaveActivityCurrentWhenOnlyTheProfilerListens()
    {
        await using var f = ProfilerFixture.Create();
        await f.Renderer.RenderAsync<PlainCounter>();
        System.Diagnostics.Activity? observed = new("sentinel");
        await f.Renderer.Dispatcher.InvokeAsync(() =>
        {
            observed = System.Diagnostics.Activity.Current;
        });
        await f.Renderer.ClickAsync();
        Assert.Null(observed);
        Assert.Null(System.Diagnostics.Activity.Current);
    }

    [Fact]
    public async Task ComponentCreationIsRecordedWithStableInstanceIds()
    {
        await using var f = ProfilerFixture.Create();
        await f.Renderer.RenderAsync<PlainCounter>();
        var created = f.All<ComponentCreatedEvent>();
        Assert.Equal(3, created.Count);
        Assert.Equal(3, created.Select(c => c.ComponentInstanceId).Distinct().Count());
        Assert.All(created, c => Assert.Equal(InstrumentationSource.ComponentActivator, c.Source));
        Assert.Equal(2, created.Count(c => c.ComponentType == typeof(PlainChild).FullName));
    }

    [Fact]
    public async Task ExistingActivatorIsWrappedNotReplaced()
    {
        var custom = new CountingActivator();
        await using var f = ProfilerFixture.Create(services: s => s.AddSingleton<IComponentActivator>(custom));
        await f.Renderer.RenderAsync<PlainCounter>();
        Assert.Equal(3, custom.Created);
        Assert.Equal(3, f.All<ComponentCreatedEvent>().Count);
    }

    [Fact]
    public async Task CollectorsAreQuietWhenNoSessionIsRecording()
    {
        await using var f = ProfilerFixture.Create();
        f.Session.Stop();
        await f.Renderer.RenderAsync<PlainCounter>();
        await f.Renderer.ClickAsync();
        Assert.Equal(0, f.Host.GetHealth().Emitted);
        Assert.All(f.Host.GetHealth().Collectors, c => Assert.NotEqual(CollectorState.Running, c.State));
    }

    [Fact]
    public async Task ProfilerOwnComponentsAreExcluded()
    {
        await using var f = ProfilerFixture.Create(services: s => s.AddScoped<Microsoft.JSInterop.IJSRuntime, InteropTests.FakeJSRuntime>());
        await f.Renderer.RenderAsync<Browser.BlazorProfilerClient>();
        Assert.DoesNotContain(f.Events.OfType<ComponentEvent>(), e => e.ComponentType!.StartsWith("BlazorProfiler.", StringComparison.Ordinal));
    }

    private sealed class CountingActivator : IComponentActivator
    {
        public int Created;

        public IComponent CreateInstance(Type componentType)
        {
            Created++;
            return (IComponent)Activator.CreateInstance(componentType)!;
        }
    }
}

public class InstrumentationTests
{
    [Fact]
    public async Task RecordsPerInstanceRendersWithReasons()
    {
        await using var f = ProfilerFixture.Create();
        var (component, _) = await f.Renderer.RenderAsync<ProfiledCounter>();
        await f.Renderer.ClickAsync();
        await f.Renderer.Dispatcher.InvokeAsync(component.Touch);

        var counterRenders = f.All<ComponentRenderEvent>().Where(r => r.ComponentType == typeof(ProfiledCounter).FullName).ToList();
        Assert.Equal([RenderReason.FirstRender, RenderReason.EventHandler, RenderReason.StateHasChanged], counterRenders.Select(r => r.Reason));
        Assert.Equal([1, 2, 3], counterRenders.Select(r => r.RenderNumber));
        Assert.All(counterRenders, r => Assert.True(r.FrameCount > 0));
        Assert.Single(counterRenders.Select(r => r.ComponentInstanceId).Distinct());
    }

    [Fact]
    public async Task DetectsReferenceOnlyParameterChangesAndDeduplicatesFrameworkMetrics()
    {
        await using var f = ProfilerFixture.Create();
        await f.Renderer.RenderAsync<ProfiledCounter>();
        await f.Renderer.ClickAsync();

        var childUpdates = f.All<ParametersSetEvent>().Where(p => p.ComponentType == typeof(ProfiledChild).FullName).ToList();
        Assert.All(childUpdates, p => Assert.Equal(InstrumentationSource.ComponentInstrumentation, p.Source));
        Assert.Equal(2, childUpdates.Count(p => p.ChangeState == ParameterChangeState.Initial));

        // The child whose Value changed vs. the constant child that re-rendered only because OnSelect is a new delegate.
        var changed = Assert.Single(childUpdates, p => p.ChangeState == ParameterChangeState.Changed);
        Assert.Contains("Value", changed.ChangedParameters!);
        var referenceOnly = Assert.Single(childUpdates, p => p.ChangeState == ParameterChangeState.ChangedByReferenceOnly);
        Assert.Equal(["OnSelect"], referenceOnly.ChangedParameters!);
    }

    [Fact]
    public async Task RecordsComponentTreeAndDisposal()
    {
        await using var f = ProfilerFixture.Create();
        var (_, id) = await f.Renderer.RenderAsync<ProfiledCounter>(new() { ["ShowExtra"] = true });
        var children = f.All<ComponentChildrenEvent>().Last(c => c.ComponentType == typeof(ProfiledCounter).FullName);
        Assert.Equal(3, children.ChildInstanceIds.Length);
        Assert.All(children.ChildTypes, t => Assert.Equal(typeof(ProfiledChild).FullName, t));

        await f.Renderer.SetParametersAsync(id, new() { ["ShowExtra"] = false });
        var disposed = Assert.Single(f.All<ComponentDisposedEvent>());
        Assert.Contains(disposed.ComponentInstanceId, children.ChildInstanceIds);
        Assert.True(disposed.LifetimeTicks > 0);
        Assert.Equal(2, f.All<ComponentChildrenEvent>().Last(c => c.ComponentType == typeof(ProfiledCounter).FullName).ChildInstanceIds.Length);

        var summary = f.Session.GetSummary();
        var counterInstance = summary.Instances.Single(i => i.ComponentType == typeof(ProfiledCounter).FullName);
        Assert.Equal(2, summary.Instances.Count(i => i.ParentInstanceId == counterInstance.InstanceId && i.DisposedTimestamp is null));
    }

    [Fact]
    public async Task DetectsRendersWithUnchangedOutput()
    {
        await using var f = ProfilerFixture.Create(o => o.Instrumentation.TrackRenderOutput = true);
        var (component, _) = await f.Renderer.RenderAsync<ProfiledCounter>();
        await f.Renderer.Dispatcher.InvokeAsync(component.Touch);
        await f.Renderer.ClickAsync();
        var renders = f.All<ComponentRenderEvent>().Where(r => r.ComponentType == typeof(ProfiledCounter).FullName).ToList();
        Assert.Equal([null, true, false], renders.Select(r => r.OutputUnchanged));
    }

    [Fact]
    public async Task HandlerSynchronousDurationAndInstanceAreAttachedToTheHandlerEvent()
    {
        await using var f = ProfilerFixture.Create();
        await f.Renderer.RenderAsync<ProfiledCounter>();
        await f.Renderer.ClickAsync();
        var handler = Assert.Single(f.All<EventHandlerEvent>());
        Assert.True(handler.SynchronousDuration > 0);
        Assert.Equal(f.All<ComponentRenderEvent>().First(r => r.ComponentType == typeof(ProfiledCounter).FullName).ComponentInstanceId, handler.ComponentInstanceId);
    }

    [Fact]
    public async Task RecordsLifecycleSynchronousAndAwaitedTimeAndAttributesHttpToTheComponent()
    {
        await using var f = ProfilerFixture.Create();
        await f.Renderer.RenderAsync<SlowInit>();
        await f.Host.FlushAsync();
        await WaitUntil(() => f.All<HttpRequestEvent>().Count == 1 && f.All<LifecycleMethodEvent>().Any(l => l.Method == LifecycleMethod.OnInitializedAsync));

        var init = f.All<LifecycleMethodEvent>().Single(l => l.Method == LifecycleMethod.OnInitialized);
        Assert.True(f.Host.Clock.ToMilliseconds(init.SynchronousDuration) >= 25);
        var initAsync = f.All<LifecycleMethodEvent>().Single(l => l.Method == LifecycleMethod.OnInitializedAsync);
        Assert.True(initAsync.CompletedAsynchronously);
        Assert.True(initAsync.Duration > initAsync.SynchronousDuration);

        var http = Assert.Single(f.All<HttpRequestEvent>());
        Assert.Equal(typeof(SlowInit).FullName, http.OriginComponentType);
        Assert.Equal(LifecycleMethod.OnInitializedAsync, http.OriginLifecycleMethod);
        Assert.Equal(200, http.StatusCode);
        Assert.Equal("https://api.test/items/{id}?token=[redacted]", http.Url);
        Assert.Equal("GET https://api.test/items/{id}", http.Endpoint);
    }

    [Fact]
    public async Task ShouldRenderSuppressionAndCoalescingAreRecorded()
    {
        await using var f = ProfilerFixture.Create();
        var (component, id) = await f.Renderer.RenderAsync<LifecycleProbe>(new() { ["Value"] = 1 });
        await WaitUntil(() => component.Calls.Contains("initAsyncDone"));
        await f.Renderer.SetParametersAsync(id, new() { ["Value"] = 2, ["Suppress"] = true });
        await f.Renderer.Dispatcher.InvokeAsync(() =>
        {
            component.Refresh();
        });

        var requests = f.All<RenderRequestEvent>();
        Assert.Contains(requests, r => r.Result == RenderRequestResult.SuppressedByShouldRender);
        Assert.DoesNotContain(requests, r => r.Result == RenderRequestResult.Queued);
    }

    [Fact]
    public async Task ProfiledComponentBaseMatchesComponentBaseLifecycle()
    {
        var profiled = await RunProbe<LifecycleProbe>(p => p.Calls, (r, id) => r.GetText(id));
        var plain = await RunProbe<BaseLifecycleProbe>(p => p.Calls, (r, id) => r.GetText(id));
        Assert.Equal(plain.Calls, profiled.Calls);
        Assert.Equal(plain.Text, profiled.Text);
        Assert.Equal(plain.Batches, profiled.Batches);
    }

    [Fact]
    public async Task InstrumentationIsInertWithoutProfilerRegistration()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new TestRenderer(services);
        var (component, id) = await renderer.RenderAsync<LifecycleProbe>(new() { ["Value"] = 5 });
        await WaitUntil(() => component.Calls.Contains("initAsyncDone"));
        Assert.StartsWith("5:", renderer.GetText(id), StringComparison.Ordinal);
    }

    private static async Task<(List<string> Calls, string Text, List<int> Batches)> RunProbe<T>(Func<T, List<string>> calls, Func<TestRenderer, int, string> text)
        where T : IComponent
    {
        await using var f = ProfilerFixture.Create();
        var (component, id) = await f.Renderer.RenderAsync<T>(new() { ["Value"] = 1 });
        await WaitUntil(() => calls(component).Contains("initAsyncDone") && calls(component).Count(c => c.StartsWith("after", StringComparison.Ordinal)) >= 2);
        await f.Renderer.SetParametersAsync(id, new() { ["Value"] = 2 });
        await f.Renderer.SetParametersAsync(id, new() { ["Value"] = 3, ["Suppress"] = true });
        return ([.. calls(component)], text(f.Renderer, id), [.. f.Renderer.BatchSizes]);
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "Condition not met in time.");
    }
}

public class HttpHandlerTests
{
    [Fact]
    public async Task RecordsFailuresAndPropagatesExceptions()
    {
        await using var f = ProfilerFixture.Create();
        f.Http.Throw = new HttpRequestException("down");
        var client = f.Scope.ServiceProvider.GetRequiredService<HttpClient>();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.test/a"));
        var e = Assert.Single(f.All<HttpRequestEvent>());
        Assert.Equal(ProfilerOutcome.Error, e.Outcome);
        Assert.Equal(typeof(HttpRequestException).FullName, e.ErrorType);
    }

    [Fact]
    public async Task RecordsCancellationAndErrorStatus()
    {
        await using var f = ProfilerFixture.Create();
        var client = f.Scope.ServiceProvider.GetRequiredService<HttpClient>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("https://api.test/a", cts.Token));
        f.Http.StatusCode = 503;
        using var response = await client.GetAsync("https://api.test/b");
        var events = f.All<HttpRequestEvent>();
        Assert.Equal(ProfilerOutcome.Canceled, events[0].Outcome);
        Assert.Equal(ProfilerOutcome.Error, events[1].Outcome);
        Assert.Equal(503, events[1].StatusCode);
    }

    [Fact]
    public async Task ManualHandlerAndFactoryHandlerDoNotDoubleCount()
    {
        await using var f = ProfilerFixture.Create();
        var factoryClient = f.Scope.ServiceProvider.GetRequiredService<HttpClient>();
        var manual = new HttpClient(Http.ProfilerHttpMessageHandler.Create(f.Root, new PassThrough(factoryClient)));
        using var response = await manual.GetAsync("https://api.test/x");
        Assert.Single(f.All<HttpRequestEvent>());
    }

    [Fact]
    public async Task RetryMarkerIsRecorded()
    {
        await using var f = ProfilerFixture.Create();
        var client = f.Scope.ServiceProvider.GetRequiredService<HttpClient>();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.test/r");
        request.Options.Set(Http.ProfilerHttpMessageHandler.RetryAttemptKey, 2);
        using var response = await client.SendAsync(request);
        Assert.Equal(2, Assert.Single(f.All<HttpRequestEvent>()).RetryAttempt);
    }

    private sealed class PassThrough(HttpClient inner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            inner.SendAsync(new HttpRequestMessage(request.Method, request.RequestUri), cancellationToken);
    }
}
