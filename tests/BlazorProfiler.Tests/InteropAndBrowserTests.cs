using System.Diagnostics.CodeAnalysis;
using BlazorProfiler.Browser;
using BlazorProfiler.Collectors;
using BlazorProfiler.Correlation;
using BlazorProfiler.Events;
using BlazorProfiler.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BlazorProfiler.Tests;

public class InteropTests
{
    private static (ProfilerFixture Fixture, ProfiledJSRuntime Runtime, FakeJSRuntime Inner) Create(Action<ProfilerOptions>? configure = null)
    {
        var f = ProfilerFixture.Create(configure);
        var inner = new FakeJSRuntime();
        return (f, ProfiledJSRuntime.Create(inner, f.Root.GetRequiredService<JSInteropCollector>()), inner);
    }

    [Fact]
    public async Task RecordsAsyncAndSyncCallsAndPreservesInProcessInterface()
    {
        var (f, runtime, inner) = Create(o => o.Collectors.JSInteropPayloadEstimation = true);
        await using var _ = f;
        Assert.IsAssignableFrom<IJSInProcessRuntime>(runtime);
        Assert.Equal(42, await runtime.InvokeAsync<int>("math.answer", ["x", 1]));
        Assert.Equal(42, ((IJSInProcessRuntime)runtime).Invoke<int>("math.answer"));
        await runtime.InvokeVoidAsync("log", "hello");

        var calls = f.All<JSInteropEvent>();
        Assert.Equal(3, calls.Count);
        Assert.Equal("math.answer", calls[0].Identifier);
        Assert.Equal(2, calls[0].ArgumentCount);
        Assert.False(calls[0].Synchronous);
        Assert.True(calls[1].Synchronous);
        Assert.Equal(ValueQuality.Estimated, calls[0].PayloadBytesQuality);
        Assert.True(calls[0].PayloadBytes > 0);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task FailuresAndCancellationPropagateAndAreRecorded()
    {
        var (f, runtime, inner) = Create();
        await using var _ = f;
        inner.Fail = new JSException("boom");
        await Assert.ThrowsAsync<JSException>(() => runtime.InvokeAsync<int>("bad", []).AsTask());
        inner.Fail = new TaskCanceledException();
        await Assert.ThrowsAsync<TaskCanceledException>(() => runtime.InvokeAsync<int>("slow", []).AsTask());
        var calls = f.All<JSInteropEvent>();
        Assert.Equal(ProfilerOutcome.Error, calls[0].Outcome);
        Assert.Equal(ProfilerOutcome.Canceled, calls[1].Outcome);
    }

    [Fact]
    public async Task ObjectReferencesAreWrappedOnlyWhenEnabledAndUnwrappedAsArguments()
    {
        var (f, runtime, inner) = Create(o => o.Collectors.JSObjectReferences = true);
        await using var _ = f;
        var module = await runtime.InvokeAsync<IJSObjectReference>("import", "./x.js");
        Assert.IsType<ProfiledJSObjectReference>(module);
        await module.InvokeVoidAsync("draw", 1);
        await runtime.InvokeVoidAsync("consume", module);
        Assert.Same(inner.Module, inner.LastArgs![0]);
        var calls = f.All<JSInteropEvent>();
        Assert.Contains(calls, c => c.Identifier == "draw" && c.ViaObjectReference);

        var (f2, runtime2, _) = Create();
        await using var __ = f2;
        Assert.IsNotType<ProfiledJSObjectReference>(await runtime2.InvokeAsync<IJSObjectReference>("import", "./x.js"));
    }

    [Fact]
    public async Task CallsAreAttributedToTheAmbientOperation()
    {
        var (f, runtime, _) = Create();
        await using var _ = f;
        using (f.Host.StartOperation("custom"))
        {
            await runtime.InvokeVoidAsync("log");
        }

        var operation = Assert.Single(f.All<OperationEvent>());
        Assert.Equal(operation.OperationId, Assert.Single(f.All<JSInteropEvent>()).OperationId);
    }

    [Fact]
    public void ProfiledRuntimeIsRegisteredForServerOptIn()
    {
        var services = new ServiceCollection();
        services.AddScoped<IJSRuntime, FakeJSRuntime>();
        services.AddBlazorProfiler(o => o.Enabled = true);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var profiled = scope.ServiceProvider.GetRequiredService<IProfiledJSRuntime>();
        Assert.IsType<FakeJSRuntime>(profiled.InnerRuntime);
    }

    internal sealed class FakeJSRuntime : IJSInProcessRuntime
    {
        public int Calls;

        public Exception? Fail { get; set; }

        public object?[]? LastArgs { get; private set; }

        public FakeModule Module { get; } = new();

        public async ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(string identifier, object?[]? args)
        {
            Calls++;
            LastArgs = args;
            await Task.Yield();
            if (Fail is not null)
            {
                throw Fail;
            }

            return identifier == "import" ? (TValue)(object)Module : Result<TValue>();
        }

        public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public TResult Invoke<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TResult>(string identifier, params object?[]? args)
        {
            Calls++;
            return Result<TResult>();
        }

        private static T Result<T>() => typeof(T) == typeof(int) ? (T)(object)42 : default!;
    }

    internal sealed class FakeModule : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public class BrowserIngestionTests
{
    [Fact]
    public async Task ConvertsEntriesToEventsOnTheProfilerClock()
    {
        await using var f = ProfilerFixture.Create();
        var collector = f.Root.GetRequiredService<BrowserCollector>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var json = $$"""
            [
              {"k":"i","n":"click","t":{{now}},"d":240,"id":10,"pr":200,"pd":30,"tag":"button"},
              {"k":"l","n":"long-animation-frame","t":{{now}},"d":180,"b":130,"s":"https://app.test/app.js?v=secret"},
              {"k":"r","n":"https://cdn.test/img.png","t":{{now}},"d":20,"it":"img","ts":0,"ds":1000},
              {"k":"x","t":{{now}},"d":1},
              {"k":"i","t":{{now}},"d":-1}
            ]
            """;
        Assert.Equal(3, collector.Ingest(json, 0));
        var interaction = Assert.Single(f.All<BrowserInteractionEvent>());
        Assert.Equal(240, f.Host.Clock.ToMilliseconds(interaction.Duration), 3);
        Assert.Equal("button", interaction.TargetTag);
        var wall = f.Host.Clock.ToDateTimeOffset(interaction.Timestamp);
        Assert.True(Math.Abs((wall - DateTimeOffset.FromUnixTimeMilliseconds(now)).TotalMilliseconds) < 5);
        Assert.Equal("https://app.test/app.js?v=[redacted]", Assert.Single(f.All<BrowserLongTaskEvent>()).Script);
        Assert.True(Assert.Single(f.All<BrowserResourceEvent>()).FromCache);
    }

    [Fact]
    public async Task RejectsOversizedOrInvalidPayloads()
    {
        await using var f = ProfilerFixture.Create();
        var collector = f.Root.GetRequiredService<BrowserCollector>();
        Assert.Equal(0, collector.Ingest(new string('x', BrowserCollector.MaxPayloadLength + 1), 0));
        Assert.Equal(0, collector.Ingest("{not json", 0));
        Assert.Equal(1, collector.GetStatus().Errors);
    }
}

public class RuntimeCollectorTests
{
    [Fact]
    public async Task SampleReportsAllocationsAndCollections()
    {
        await using var f = ProfilerFixture.Create(o => o.Collectors.Runtime = true);
        var collector = f.Root.GetRequiredService<RuntimeCollector>();
        var garbage = new List<byte[]>();
        for (var i = 0; i < 100; i++)
        {
            garbage.Add(new byte[10_000]);
        }

        GC.Collect(0);
        collector.Sample();
        // The periodic timer may also have sampled meanwhile, so assert on totals.
        var samples = f.All<RuntimeSampleEvent>();
        var sample = samples.Last();
        Assert.True(samples.Sum(s => s.AllocatedBytes) >= 1_000_000);
        Assert.True(samples.Sum(s => s.Gen0Collections) >= 1);
        Assert.NotNull(sample.WorkingSetBytes);
        Assert.NotNull(sample.CpuTicks);
        GC.KeepAlive(garbage);
    }
}

public class RegistrationTests
{
    [Fact]
    public void DisabledProfilerAddsNoInterceptionPoints()
    {
        var services = new ServiceCollection();
        services.AddBlazorProfiler(o => o.Enabled = false);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(Microsoft.AspNetCore.Components.IComponentActivator));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(Instrumentation.ComponentInstrumentation));
        using var provider = services.BuildServiceProvider();
        var profiler = provider.GetRequiredService<IProfiler>();
        Assert.False(profiler.IsEnabled);
        Assert.False(profiler.IsRecording);
    }

    [Fact]
    public void RegistersAllCollectorsOnceEvenWhenCalledTwice()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBlazorProfiler(o => o.Enabled = true);
        services.AddBlazorProfiler(o => o.Collectors.Browser = false);
        using var provider = services.BuildServiceProvider();
        var collectors = provider.GetServices<IProfilerCollector>().ToList();
        Assert.Equal(collectors.Count, collectors.Select(c => c.Descriptor.Name).Distinct().Count());
        Assert.Equal(8, collectors.Count);
        Assert.False(provider.GetRequiredService<ProfilerOptions>().Collectors.Browser);
        Assert.Single(services, d => d.ServiceType == typeof(Microsoft.AspNetCore.Components.IComponentActivator));
    }
}
