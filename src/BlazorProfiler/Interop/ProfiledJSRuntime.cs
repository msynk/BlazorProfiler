using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using BlazorProfiler.Collectors;
using BlazorProfiler.Events;
using Microsoft.JSInterop;

namespace BlazorProfiler.Interop;

/// <summary>Reports the state of JavaScript interop collection.</summary>
public sealed class JSInteropCollector : ProfilerCollectorBase
{
    private static readonly JsonSerializerOptions s_estimationOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public override CollectorDescriptor Descriptor { get; } = new()
    {
        Name = "js-interop",
        Description = ".NET-to-JavaScript interop calls: identifier, duration, outcome, argument count, optional payload size estimates, and the originating operation/component.",
        Mechanism = "A decorating IJSRuntime (and, optionally, IJSObjectReference) implementation.",
        Capability = CapabilityLevel.Partial,
        Overhead = OverheadLevel.Low,
        Compatibility = "WebAssembly: IJSRuntime is decorated automatically. Server: the framework requires the concrete circuit runtime, so only calls made through the injectable IProfiledJSRuntime are recorded.",
        Degradation = "Calls made by framework internals that hold the runtime directly, and JavaScript-to-.NET callbacks, are not observed. Payload sizes are estimates.",
    };

    /// <summary>Whether calls should be recorded.</summary>
    public bool IsCollecting => IsActive;

    /// <summary>Whether returned object references are wrapped.</summary>
    public bool WrapObjectReferences => Profiler?.Options.Collectors.JSObjectReferences == true;

    /// <summary>The profiler.</summary>
    internal IProfiler? CurrentProfiler => Profiler;

    /// <summary>Emits an event.</summary>
    public bool Publish(ProfilerEvent profilerEvent) => Emit(profilerEvent);

    /// <inheritdoc />
    protected override bool IsEnabled(ProfilerOptions options) => options.Collectors.JSInterop;

    /// <inheritdoc />
    protected override void OnStart(ProfilerCollectorContext context)
    {
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
    }

    internal long StartTimestamp() => Profiler?.Clock.GetTimestamp() ?? 0;

    internal void Record(string identifier, object?[]? args, bool synchronous, bool viaReference, long start, Exception? exception)
    {
        var profiler = Profiler;
        if (profiler is null)
        {
            return;
        }

        try
        {
            var e = new JSInteropEvent
            {
                Direction = InteropDirection.DotNetToJS,
                Identifier = identifier,
                Synchronous = synchronous,
                ViaObjectReference = viaReference,
                ArgumentCount = args?.Length ?? 0,
                Timestamp = start,
                Duration = profiler.Clock.GetTimestamp() - start,
                Source = InstrumentationSource.JSRuntimeDecorator,
                Outcome = exception is null ? ProfilerOutcome.Success : exception is OperationCanceledException ? ProfilerOutcome.Canceled : ProfilerOutcome.Error,
                ErrorType = exception?.GetType().FullName,
            };

            if (profiler.Options.Collectors.JSInteropPayloadEstimation && args is { Length: > 0 })
            {
                e.PayloadBytes = EstimatePayload(args);
                e.PayloadBytesQuality = e.PayloadBytes is null ? ValueQuality.Unavailable : ValueQuality.Estimated;
            }

            Emit(e);
        }
        catch (Exception ex)
        {
            RecordError(ex);
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Opt-in estimation. Blazor JS interop already serializes arguments with reflection-based System.Text.Json; failures are caught and reported as unavailable.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Opt-in estimation; failures are caught and reported as unavailable.")]
    private static long? EstimatePayload(object?[] args)
    {
        try
        {
            long total = 0;
            foreach (var arg in args)
            {
                if (arg is null)
                {
                    total += 4;
                }
                else if (arg is string text)
                {
                    total += text.Length + 2;
                }
                else if (arg is byte[] bytes)
                {
                    total += bytes.Length;
                }
                else if (arg is DotNetObjectReference<object> or IJSObjectReference)
                {
                    total += 32;
                }
                else
                {
                    total += JsonSerializer.SerializeToUtf8Bytes(arg, arg.GetType(), s_estimationOptions).Length;
                }
            }

            return total;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// An <see cref="IJSRuntime"/> whose calls are recorded by the profiler. Inject this interface on Blazor Server to profile
/// selected interop calls; in WebAssembly, <see cref="IJSRuntime"/> itself is profiled.
/// </summary>
public interface IProfiledJSRuntime : IJSRuntime
{
    /// <summary>The underlying runtime.</summary>
    IJSRuntime InnerRuntime { get; }
}

/// <summary>A decorating <see cref="IJSRuntime"/> that records calls.</summary>
public class ProfiledJSRuntime : IProfiledJSRuntime
{
    private const DynamicallyAccessedMemberTypes JsonSerialized =
        DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties;

    /// <summary>Creates the decorator.</summary>
    protected ProfiledJSRuntime(IJSRuntime inner, JSInteropCollector collector)
    {
        InnerRuntime = inner;
        Collector = collector;
    }

    /// <inheritdoc />
    public IJSRuntime InnerRuntime { get; }

    /// <summary>The collector.</summary>
    protected JSInteropCollector Collector { get; }

    /// <summary>Creates a decorator that also implements <see cref="IJSInProcessRuntime"/> when the inner runtime does.</summary>
    public static ProfiledJSRuntime Create(IJSRuntime inner, JSInteropCollector collector)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(collector);
        if (inner is ProfiledJSRuntime existing)
        {
            return existing;
        }

        return inner is IJSInProcessRuntime ? new ProfiledJSInProcessRuntime(inner, collector) : new ProfiledJSRuntime(inner, collector);
    }

    /// <inheritdoc />
    public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier, object?[]? args) =>
        InterceptAsync(identifier, args, false, (s, id, a) => ((IJSRuntime)s!).InvokeAsync<TValue>(id, a), InnerRuntime);

    /// <inheritdoc />
    public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        InterceptAsync(identifier, args, false, (s, id, a) => ((IJSRuntime)s!).InvokeAsync<TValue>(id, cancellationToken, a), InnerRuntime);

    /// <inheritdoc />
    public ValueTask<IJSObjectReference> InvokeConstructorAsync(string identifier, object?[]? args) =>
        InterceptAsync(identifier, args, false, (s, id, a) => ((IJSRuntime)s!).InvokeConstructorAsync(id, a), InnerRuntime);

    /// <inheritdoc />
    public ValueTask<IJSObjectReference> InvokeConstructorAsync(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        InterceptAsync(identifier, args, false, (s, id, a) => ((IJSRuntime)s!).InvokeConstructorAsync(id, cancellationToken, a), InnerRuntime);

    /// <inheritdoc />
    public ValueTask<TValue> GetValueAsync<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier) =>
        InterceptAsync(identifier, null, false, (s, id, _) => ((IJSRuntime)s!).GetValueAsync<TValue>(id), InnerRuntime);

    /// <inheritdoc />
    public ValueTask<TValue> GetValueAsync<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier, CancellationToken cancellationToken) =>
        InterceptAsync(identifier, null, false, (s, id, _) => ((IJSRuntime)s!).GetValueAsync<TValue>(id, cancellationToken), InnerRuntime);

    /// <inheritdoc />
    public ValueTask SetValueAsync<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier, TValue value) =>
        InterceptVoidAsync(identifier, [value], (s, id) => ((IJSRuntime)s!).SetValueAsync(id, value), InnerRuntime);

    /// <inheritdoc />
    public ValueTask SetValueAsync<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier, TValue value, CancellationToken cancellationToken) =>
        InterceptVoidAsync(identifier, [value], (s, id) => ((IJSRuntime)s!).SetValueAsync(id, value, cancellationToken), InnerRuntime);

    /// <summary>Intercepts an asynchronous call.</summary>
    internal ValueTask<TValue> InterceptAsync<TValue>(string identifier, object?[]? args, bool viaReference, Func<object?, string, object?[]?, ValueTask<TValue>> invoke, object? target)
    {
        var unwrapped = InteropArguments.Unwrap(args);
        if (!Collector.IsCollecting)
        {
            return invoke(target, identifier, unwrapped);
        }

        var start = Collector.StartTimestamp();
        ValueTask<TValue> task;
        try
        {
            task = invoke(target, identifier, unwrapped);
        }
        catch (Exception ex)
        {
            Collector.Record(identifier, args, synchronous: false, viaReference, start, ex);
            throw;
        }

        if (task.IsCompletedSuccessfully)
        {
            var result = task.Result;
            Collector.Record(identifier, args, synchronous: false, viaReference, start, null);
            return new ValueTask<TValue>(WrapResult(result));
        }

        return AwaitAsync(task, identifier, args, viaReference, start);
    }

    /// <summary>Intercepts an asynchronous void call.</summary>
    internal ValueTask InterceptVoidAsync(string identifier, object?[]? args, Func<object?, string, ValueTask> invoke, object? target)
    {
        if (!Collector.IsCollecting)
        {
            return invoke(target, identifier);
        }

        var start = Collector.StartTimestamp();
        ValueTask task;
        try
        {
            task = invoke(target, identifier);
        }
        catch (Exception ex)
        {
            Collector.Record(identifier, args, synchronous: false, viaReference: false, start, ex);
            throw;
        }

        if (task.IsCompletedSuccessfully)
        {
            Collector.Record(identifier, args, synchronous: false, viaReference: false, start, null);
            return default;
        }

        return AwaitVoidAsync(task, identifier, args, start);
    }

    /// <summary>Intercepts a synchronous call.</summary>
    internal TValue Intercept<TValue>(string identifier, object?[]? args, bool viaReference, Func<object?, string, object?[]?, TValue> invoke, object? target)
    {
        var unwrapped = InteropArguments.Unwrap(args);
        if (!Collector.IsCollecting)
        {
            return invoke(target, identifier, unwrapped);
        }

        var start = Collector.StartTimestamp();
        try
        {
            var result = invoke(target, identifier, unwrapped);
            Collector.Record(identifier, args, synchronous: true, viaReference, start, null);
            return WrapResult(result);
        }
        catch (Exception ex)
        {
            Collector.Record(identifier, args, synchronous: true, viaReference, start, ex);
            throw;
        }
    }

    private async ValueTask<TValue> AwaitAsync<TValue>(ValueTask<TValue> task, string identifier, object?[]? args, bool viaReference, long start)
    {
        TValue result;
        try
        {
            result = await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Collector.Record(identifier, args, synchronous: false, viaReference, start, ex);
            throw;
        }

        Collector.Record(identifier, args, synchronous: false, viaReference, start, null);
        return WrapResult(result);
    }

    private async ValueTask AwaitVoidAsync(ValueTask task, string identifier, object?[]? args, long start)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Collector.Record(identifier, args, synchronous: false, viaReference: false, start, ex);
            throw;
        }

        Collector.Record(identifier, args, synchronous: false, viaReference: false, start, null);
    }

    private TValue WrapResult<TValue>(TValue value)
    {
        if (!Collector.WrapObjectReferences || value is null)
        {
            return value;
        }

        // Only wrap when the declared type is an interface the wrapper implements; concrete types are returned unchanged.
        if (typeof(TValue) == typeof(IJSObjectReference) && value is IJSObjectReference reference)
        {
            return (TValue)(object)ProfiledJSObjectReference.Create(reference, Collector);
        }

        if (typeof(TValue) == typeof(IJSInProcessObjectReference) && value is IJSInProcessObjectReference inProcess)
        {
            return (TValue)(object)ProfiledJSObjectReference.Create(inProcess, Collector);
        }

        return value;
    }
}

/// <summary>A decorating <see cref="IJSInProcessRuntime"/> that records calls.</summary>
public sealed class ProfiledJSInProcessRuntime : ProfiledJSRuntime, IJSInProcessRuntime
{
    private const DynamicallyAccessedMemberTypes JsonSerialized =
        DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties;

    internal ProfiledJSInProcessRuntime(IJSRuntime inner, JSInteropCollector collector)
        : base(inner, collector)
    {
    }

    private IJSInProcessRuntime Sync => (IJSInProcessRuntime)InnerRuntime;

    /// <inheritdoc />
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed.")]
    public TResult Invoke<[DynamicallyAccessedMembers(JsonSerialized)] TResult>(string identifier, params object?[]? args) =>
        Intercept(identifier, args, false, (s, id, a) => ((IJSInProcessRuntime)s!).Invoke<TResult>(id, a), Sync);

    /// <inheritdoc />
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed.")]
    public IJSInProcessObjectReference InvokeConstructor(string identifier, params object?[]? args) =>
        Intercept(identifier, args, false, (s, id, a) => ((IJSInProcessRuntime)s!).InvokeConstructor(id, a), Sync);

    /// <inheritdoc />
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed.")]
    public TValue GetValue<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier) =>
        Intercept(identifier, null, false, (s, id, _) => ((IJSInProcessRuntime)s!).GetValue<TValue>(id), Sync);

    /// <inheritdoc />
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed.")]
    public void SetValue<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier, TValue value) =>
        Intercept<object?>(identifier, [value], false, (s, id, _) =>
        {
            ((IJSInProcessRuntime)s!).SetValue(id, value);
            return null;
        }, Sync);
}

/// <summary>Helpers for interop arguments.</summary>
internal static class InteropArguments
{
    /// <summary>Replaces profiled object references in top-level arguments with the original references.</summary>
    public static object?[]? Unwrap(object?[]? args)
    {
        if (args is null)
        {
            return null;
        }

        object?[]? copy = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is ProfiledJSObjectReference profiled)
            {
                copy ??= (object?[])args.Clone();
                copy[i] = profiled.Inner;
            }
        }

        return copy ?? args;
    }
}
