using System.Buffers;
using BlazorProfiler.Events;
using BlazorProfiler.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlazorProfiler.Server.Tests;

public sealed class CircuitTransportTests
{
    private static async Task<WebApplication> CreateAppAsync(bool profilerFirst, bool measure = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (!profilerFirst)
        {
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        }

        builder.AddBlazorProfiler(
            o =>
            {
                o.Collectors.Runtime = false;
                o.Pipeline.SynchronousProcessing = true;
            },
            server => server.MeasureCircuitMessages = measure);
        if (profilerFirst)
        {
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        }

        var app = builder.Build();
        await app.StartAsync();
        return app;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WrapsBlazorsHubProtocolOnlyWhateverTheRegistrationOrder(bool profilerFirst)
    {
        await using var app = await CreateAppAsync(profilerFirst);
        var resolver = app.Services.GetRequiredService<IHubProtocolResolver>();

        Assert.IsType<ProfilingHubProtocol>(resolver.GetProtocol("blazorpack", null));
        Assert.IsNotType<ProfilingHubProtocol>(resolver.GetProtocol("json", null));
        Assert.Null(resolver.GetProtocol("blazorpack", ["json"]));
        Assert.Contains(resolver.AllProtocols, p => p is ProfilingHubProtocol);
        Assert.Same(resolver.GetProtocol("blazorpack", null), resolver.GetProtocol("blazorpack", null));
    }

    [Fact]
    public async Task LeavesTheHubProtocolAloneWhenMeasurementIsOff()
    {
        await using var app = await CreateAppAsync(profilerFirst: false, measure: false);
        var resolver = app.Services.GetRequiredService<IHubProtocolResolver>();
        Assert.IsNotType<ProfilingHubProtocol>(resolver.GetProtocol("blazorpack", null));
        Assert.DoesNotContain(resolver.AllProtocols, p => p is ProfilingHubProtocol);
    }

    [Fact]
    public async Task MeasuresRenderBatchesWrittenOnACircuitAndInvocationsReceived()
    {
        await using var app = await CreateAppAsync(profilerFirst: false);
        var profiler = app.Services.GetRequiredService<IProfiler>();
        profiler.StartSession(new ProfilerSessionOptions { Name = "transport" });
        var protocol = app.Services.GetRequiredService<IHubProtocolResolver>().GetProtocol("blazorpack", null)!;
        var transport = app.Services.GetRequiredService<CircuitTransport>();
        var registry = app.Services.GetRequiredService<CircuitRegistry>();
        var context = new SynchronizationContext();
        registry.Register("c1", context, null, DateTimeOffset.UtcNow);

        var previous = SynchronizationContext.Current;
        var start = profiler.Clock.GetTimestamp();
        var output = new ArrayBufferWriter<byte>();
        try
        {
            // Written outside the circuit's dispatcher: not attributed.
            protocol.WriteMessage(new InvocationMessage("JS.RenderBatch", [1L, new byte[50]]), new ArrayBufferWriter<byte>());

            SynchronizationContext.SetSynchronizationContext(context);
            protocol.WriteMessage(new InvocationMessage("JS.RenderBatch", [2L, new byte[1_000]]), output);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var end = profiler.Clock.GetTimestamp();
        Assert.True(output.WrittenCount > 1_000);

        // A batch that ended before the write does not take it; the batch spanning the write does, once.
        Assert.Null(transport.TakeBatchBytes(new RenderBatchEvent { CircuitId = "c1", Timestamp = start - 100, Duration = 1 }));
        var batch = new RenderBatchEvent { CircuitId = "c1", Timestamp = start, Duration = end - start };
        Assert.Equal(output.WrittenCount, transport.TakeBatchBytes(batch));
        Assert.Null(transport.TakeBatchBytes(batch));

        // Received invocations are measured and keyed by their argument array, which SignalR hands to the hub method.
        var sent = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(new InvocationMessage("OnRenderCompleted", [2L, null]), sent);
        var input = new ReadOnlySequence<byte>(sent.WrittenMemory);
        Assert.True(protocol.TryParseMessage(ref input, new Binder(), out var message));
        var arguments = ((InvocationMessage)message).Arguments;
        Assert.Equal(sent.WrittenCount, transport.TakeReceived(arguments));
        Assert.Null(transport.TakeReceived(arguments));
    }

    [Fact]
    public async Task MeasuresInteropArgumentsIncludingByteArraysSentSeparately()
    {
        await using var app = await CreateAppAsync(profilerFirst: false);
        app.Services.GetRequiredService<IProfiler>().StartSession(new ProfilerSessionOptions { Name = "interop" });
        var protocol = app.Services.GetRequiredService<IHubProtocolResolver>().GetProtocol("blazorpack", null)!;

        // A byte[] argument travels in a JS.ReceiveByteArray message before the call, whose JSON holds a placeholder.
        const string json = "[{\"__byte[]\":0},\"text\"]";
        Interop.InteropMessageSize.Begin();
        protocol.WriteMessage(new InvocationMessage("JS.ReceiveByteArray", [0L, new byte[5_000]]), new ArrayBufferWriter<byte>());
        protocol.WriteMessage(new InvocationMessage("JS.BeginInvokeJS", [1L, "draw", json, 0, 0L]), new ArrayBufferWriter<byte>());
        Assert.Equal(5_000 + json.Length, Interop.InteropMessageSize.End());

        // Not listening: nothing is kept for the next call.
        protocol.WriteMessage(new InvocationMessage("JS.ReceiveByteArray", [1L, new byte[10]]), new ArrayBufferWriter<byte>());
        Interop.InteropMessageSize.Begin();
        Assert.Null(Interop.InteropMessageSize.End());
    }

    [Fact]
    public async Task DoesNotMeasureWhileNotRecording()
    {
        await using var app = await CreateAppAsync(profilerFirst: false);
        foreach (var session in app.Services.GetRequiredService<IProfiler>().GetSessions())
        {
            session.Stop();
        }

        var protocol = app.Services.GetRequiredService<IHubProtocolResolver>().GetProtocol("blazorpack", null)!;
        var transport = app.Services.GetRequiredService<CircuitTransport>();

        var sent = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(new InvocationMessage("OnRenderCompleted", [2L, null]), sent);
        var input = new ReadOnlySequence<byte>(sent.WrittenMemory);
        Assert.True(protocol.TryParseMessage(ref input, new Binder(), out var message));
        Assert.Null(transport.TakeReceived(((InvocationMessage)message).Arguments));
    }

    private sealed class Binder : IInvocationBinder
    {
        public Type GetReturnType(string invocationId) => typeof(object);

        public IReadOnlyList<Type> GetParameterTypes(string methodName) => [typeof(long), typeof(string)];

        public Type GetStreamItemType(string streamId) => typeof(object);
    }
}
