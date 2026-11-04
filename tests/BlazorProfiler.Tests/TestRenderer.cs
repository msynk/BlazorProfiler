using BlazorProfiler.Events;
using BlazorProfiler.Sessions;
using BlazorProfiler.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

// Framework metrics and activities are process-wide; run these tests sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace BlazorProfiler.Tests;

/// <summary>
/// A minimal renderer built on the public <see cref="Renderer"/> base class. Display updates complete synchronously, like
/// Blazor WebAssembly.
/// </summary>
public sealed class TestRenderer : Renderer
{
    private readonly Dictionary<string, ulong> _handlers = new(StringComparer.Ordinal);

    public TestRenderer(IServiceProvider services)
        : base(services, NullLoggerFactory.Instance)
    {
    }

    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    public List<int> BatchSizes { get; } = [];

    public List<Exception> Exceptions { get; } = [];

    public async Task<(T Component, int Id)> RenderAsync<T>(Dictionary<string, object?>? parameters = null)
        where T : IComponent
    {
        T component = default!;
        var id = 0;
        await Dispatcher.InvokeAsync(async () =>
        {
            component = (T)InstantiateComponent(typeof(T));
            id = AssignRootComponentId(component);
            await RenderRootComponentAsync(id, parameters is null ? ParameterView.Empty : ParameterView.FromDictionary(parameters));
        });
        return (component, id);
    }

    public Task SetParametersAsync(int id, Dictionary<string, object?> parameters) =>
        Dispatcher.InvokeAsync(() => RenderRootComponentAsync(id, ParameterView.FromDictionary(parameters)));

    public Task ClickAsync(string key = "onclick") =>
        Dispatcher.InvokeAsync(() => DispatchEventAsync(_handlers[key], null, new MouseEventArgs()));

    public Task RemoveAsync(int id) => Dispatcher.InvokeAsync(() => RemoveRootComponent(id));

    public string GetText(int componentId)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames.Array[i].FrameType == RenderTreeFrameType.Text)
            {
                text.Append(frames.Array[i].TextContent);
            }
        }

        return text.ToString();
    }

    protected override void HandleException(Exception exception) => Exceptions.Add(exception);

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
    {
        BatchSizes.Add(renderBatch.UpdatedComponents.Count);
        for (var i = 0; i < renderBatch.ReferenceFrames.Count; i++)
        {
            ref var frame = ref renderBatch.ReferenceFrames.Array[i];
            if (frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeEventHandlerId != 0)
            {
                _handlers[frame.AttributeName] = frame.AttributeEventHandlerId;
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>A configured profiler, session, and renderer.</summary>
public sealed class ProfilerFixture : IAsyncDisposable
{
    private ProfilerFixture(ServiceProvider root, IServiceScope scope)
    {
        Root = root;
        Scope = scope;
        Host = root.GetRequiredService<ProfilerHost>();
        Session = Host.StartSession();
        Renderer = new TestRenderer(scope.ServiceProvider);
    }

    public ServiceProvider Root { get; }

    public IServiceScope Scope { get; }

    public ProfilerHost Host { get; }

    public IProfilerSession Session { get; }

    public TestRenderer Renderer { get; }

    public StubHttpHandler Http => Root.GetRequiredService<StubHttpHandler>();

    public static ProfilerFixture Create(Action<ProfilerOptions>? configure = null, Action<IServiceCollection>? services = null)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();

        // Application services are registered first, as in a typical Program.cs.
        services?.Invoke(collection);
        collection.AddBlazorProfiler(o =>
        {
            o.Enabled = true;
            o.AutoStartSession = false;
            o.Pipeline.SynchronousProcessing = true;
            o.Collectors.Runtime = false;
            configure?.Invoke(o);
        });
        collection.AddSingleton<StubHttpHandler>();
        collection.AddHttpClient("test").ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<StubHttpHandler>());
        collection.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("test"));
        var root = collection.BuildServiceProvider();
        return new ProfilerFixture(root, root.CreateScope());
    }

    public List<ProfilerEvent> Events => Session.GetEvents(new EventQuery());

    public List<T> All<T>()
        where T : ProfilerEvent => [.. Events.OfType<T>()];

    public async ValueTask DisposeAsync()
    {
        await Renderer.DisposeAsync();
        Scope.Dispose();
        await Root.DisposeAsync();
    }
}

/// <summary>An HTTP handler returning canned responses.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    public int StatusCode { get; set; } = 200;

    public Exception? Throw { get; set; }

    public int Calls;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        await Task.Delay(5, cancellationToken);
        if (Throw is not null)
        {
            throw Throw;
        }

        return new HttpResponseMessage((System.Net.HttpStatusCode)StatusCode) { Content = new StringContent("{\"ok\":true}") };
    }
}
