using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BlazorProfiler.Analysis;
using BlazorProfiler.Api;
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
    public async Task IngestsStartupAndWebVitals()
    {
        await using var f = ProfilerFixture.Create();
        var collector = f.Root.GetRequiredService<BrowserCollector>();
        var origin = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000;
        var json = $$"""
            [
              {"k":"s","n":"navigate","t":{{origin}},"d":4200,"ttfb":120,"fcp":600,"dcl":700,"ld":900,"ia":4200,"fc":40,"ft":7340032,"fd":20971520,"fca":2,"fu":3,"fdl":3100,"rk":"webapp-webassembly",
               "lf":[{"n":"dotnet.native.wasm","ts":3000000,"ds":9000000,"d":1500,"c":false},{"n":"App.wasm","ts":0,"ds":200000,"d":2,"c":true}]},
              {"k":"v","n":"LCP","t":{{origin + 4300}},"d":0,"v":3100,"tag":"img"},
              {"k":"v","n":"CLS","t":{{origin + 4300}},"d":0,"v":0.31,"tag":"div"},
              {"k":"v","n":"FID","t":{{origin + 4300}},"d":0,"v":1}
            ]
            """;
        Assert.Equal(3, collector.Ingest(json, 0));

        var startup = Assert.Single(f.All<BrowserStartupEvent>());
        Assert.Equal(4200, f.Host.Clock.ToMilliseconds(startup.Duration), 3);
        Assert.Equal(600, startup.FirstContentfulPaintMs);
        Assert.Equal(7340032, startup.FrameworkTransferBytes);
        Assert.Equal(3, startup.FrameworkUncompressedFiles);
        Assert.Equal("dotnet.native.wasm", startup.LargestFiles[0].Name);
        Assert.True(startup.LargestFiles[1].Cached);

        var summary = f.Session.GetSummary();
        Assert.Equal(4200, summary.Startup!.InteractiveMs);
        Assert.Equal("needs-improvement", summary.Vitals["LCP"].Rating);
        Assert.Equal("poor", summary.Vitals["CLS"].Rating);
        Assert.False(summary.Vitals.ContainsKey("FID"));

        var findings = f.Host.Diagnostics.Analyze(f.Session).Findings;
        Assert.Contains(findings, x => x.RuleId == "BP7003");
        Assert.Contains(findings, x => x.RuleId == "BP7004" && x.Title.Contains("3.6", StringComparison.Ordinal));
        Assert.Contains(findings, x => x.RuleId == "BP7005" && x.Subject == "CLS" && x.Severity >= Analysis.DiagnosticSeverity.Medium);
        Assert.Contains(findings, x => x.RuleId == "BP7005" && x.Subject == "LCP" && x.Severity == Analysis.DiagnosticSeverity.Low);
    }

    [Fact]
    public async Task RejectsOversizedOrInvalidPayloads()
    {
        await using var f = ProfilerFixture.Create();
        var collector = f.Root.GetRequiredService<BrowserCollector>();
        Assert.Equal(0, collector.Ingest(new string('x', BrowserCollector.MaxPayloadLength + 1), 0));

        // Discarded batches are never silent: they show up in the collector's health.
        Assert.Equal(1, collector.GetStatus().Errors);
        Assert.Contains("exceeded the limit", collector.GetStatus().LastError, StringComparison.Ordinal);
        Assert.Equal(0, collector.Ingest("{not json", 0));
        Assert.Equal(2, collector.GetStatus().Errors);
    }

    [Fact]
    public async Task ReportsEntriesBeyondTheBatchLimit()
    {
        await using var f = ProfilerFixture.Create();
        var collector = f.Root.GetRequiredService<BrowserCollector>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entries = string.Join(",", Enumerable.Range(0, BrowserCollector.MaxEntriesPerBatch + 5).Select(_ => $$"""{"k":"l","n":"longtask","t":{{now}},"d":60,"b":10}"""));
        Assert.Equal(BrowserCollector.MaxEntriesPerBatch, collector.Ingest($"[{entries}]", 0));
        Assert.Equal(1, collector.GetStatus().Errors);
    }

    [Theory]
    [InlineData('x')]
    [InlineData('"')] // escaped in the arguments and again by the JSON hub protocol: the worst case for escaping
    [InlineData('中')] // three UTF-8 bytes per UTF-16 code unit
    [InlineData('\u0001')] // escaped as \u0001: six bytes in the arguments, seven in the hub message
    public void ServerBatchBudgetFitsTheDefaultSignalRMessageLimit(char fill)
    {
        // Models blazor-profiler.js: each entry costs the UTF-8 length of its JSON-escaped form (the batch travels as a
        // string argument) and a batch is filled up to the budget. The resulting hub message must stay within SignalR's
        // default 32 KB MaximumReceiveMessageSize, which Blazor Server enforces by closing the circuit.
        var stringify = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var entry = JsonSerializer.Serialize(new { k = "l", n = "long-animation-frame", t = 1730000000000.5, d = 120.5, s = new string(fill, 512) }, stringify);
        var cost = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(entry, stringify)) - 1;
        var count = (BrowserCollector.ServerMaxBatchBytes - 2) / cost;
        Assert.True(count > 0);
        var batch = "[" + string.Join(",", Enumerable.Repeat(entry, count)) + "]";

        // BeginInvokeDotNetFromJS(callId, assembly, method, objectId, argsJson), sent with the JSON hub protocol (the
        // MessagePack-based protocol does not escape argsJson again, so it is smaller).
        var argsJson = JsonSerializer.Serialize(new[] { batch }, stringify);
        var message = JsonSerializer.Serialize(new { type = 1, target = "BeginInvokeDotNetFromJS", arguments = new object?[] { "4294967295", null, "BlazorProfiler.Ingest", 2147483647, argsJson } }, stringify);
        Assert.True(Encoding.UTF8.GetByteCount(message) + 1 <= 32 * 1024, $"Hub message of {Encoding.UTF8.GetByteCount(message)} bytes.");

        // In WebAssembly a batch within the byte budget is always within the .NET-side length limit (in characters).
        Assert.True(BrowserCollector.WebAssemblyMaxBatchBytes <= BrowserCollector.MaxPayloadLength);
    }
}

public class DashboardBridgeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandlesStreamedRequests(bool offload)
    {
        // Requests above the circuit's message size limit (trace imports) are streamed instead of sent as one message.
        await using var f = ProfilerFixture.Create();
        var callbacks = Callbacks(f, ProfilerApiAccess.Full, offload);
        var request = new BytesStreamReference(Encoding.UTF8.GetBytes($$"""{"op":"info","name":"{{new string('x', 100_000)}}"}"""));
        using var response = JsonDocument.Parse(await callbacks.HandleApiStreamRequest(request));
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains(response.RootElement.GetProperty("info").GetProperty("sessions").EnumerateArray(), s => s.GetProperty("id").GetString() == f.Session.Id);
        Assert.True(request.Disposed);
    }

    [Fact]
    public async Task DoesNotReadStreamsOfUnauthorizedCallers()
    {
        await using var f = ProfilerFixture.Create();
        var callbacks = Callbacks(f, access: null, offload: true);
        var request = new BytesStreamReference(Encoding.UTF8.GetBytes("""{"op":"info"}"""));
        Assert.Contains("Not authorized", await callbacks.HandleApiStreamRequest(request), StringComparison.Ordinal);
        Assert.False(request.Opened);
        Assert.True(request.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OffloadedRequestsDoNotRunOnTheCallingContext(bool offload)
    {
        // On the server, bridge requests arrive on the circuit's dispatcher: analysis must not run inline on it, or it would
        // block the user's UI. In WebAssembly (no offload) there is only one thread anyway.
        var probe = new ContextProbeRule();
        await using var f = ProfilerFixture.Create(services: s => s.AddSingleton<IDiagnosticRule>(probe));
        var callbacks = Callbacks(f, ProfilerApiAccess.Full, offload);
        var dispatcher = new SynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(dispatcher);
        try
        {
            var json = await callbacks.HandleApiRequest($$"""{"op":"findings","sessionId":"{{f.Session.Id}}"}""");
            using var response = JsonDocument.Parse(json);
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.True(probe.Evaluated);
        Assert.Equal(offload, !ReferenceEquals(dispatcher, probe.Context));
    }

    private static BlazorProfilerClient.BrowserCallbacks Callbacks(ProfilerFixture f, ProfilerApiAccess? access, bool offload) =>
        new(f.Root.GetRequiredService<BrowserCollector>(), f.Root.GetRequiredService<ProfilerApi>(), new FixedAccess(access), offload);

    private sealed class FixedAccess(ProfilerApiAccess? access) : IProfilerBridgeAccessProvider
    {
        public ValueTask<ProfilerApiAccess?> GetAccessAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(access);
    }

    private sealed class ContextProbeRule : IDiagnosticRule
    {
        public DiagnosticRuleDescriptor Descriptor { get; } = new() { Id = "TESTPROBE", Title = "Probe", Description = "Records where rules run." };

        public bool Evaluated { get; private set; }

        public SynchronizationContext? Context { get; private set; }

        public IEnumerable<DiagnosticFinding> Evaluate(DiagnosticContext context)
        {
            Evaluated = true;
            Context = SynchronizationContext.Current;
            return [];
        }
    }

    private sealed class BytesStreamReference(byte[] data) : IJSStreamReference
    {
        public bool Opened { get; private set; }

        public bool Disposed { get; private set; }

        public long Length => data.Length;

        public ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            Assert.True(maxAllowedSize >= data.Length);
            Opened = true;
            return ValueTask.FromResult<Stream>(new MemoryStream(data));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
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
        Assert.Equal(10, collectors.Count);
        Assert.Single(provider.GetServices<BlazorProfiler.Api.IProfilerCommand>(), c => c.Descriptor.Name == "check-leaks");
        Assert.False(provider.GetRequiredService<ProfilerOptions>().Collectors.Browser);
        Assert.Single(services, d => d.ServiceType == typeof(Microsoft.AspNetCore.Components.IComponentActivator));
    }
}
