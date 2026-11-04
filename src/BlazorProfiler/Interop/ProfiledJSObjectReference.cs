using System.Diagnostics.CodeAnalysis;
using Microsoft.JSInterop;

namespace BlazorProfiler.Interop;

/// <summary>
/// A decorating <see cref="IJSObjectReference"/> that records calls on JavaScript object references (for example imported
/// modules). Only used when <see cref="CollectorOptions.JSObjectReferences"/> is enabled.
/// </summary>
/// <remarks>
/// Wrapped references are unwrapped automatically when passed as top-level interop arguments through a profiled runtime or
/// reference. A wrapped reference nested inside another argument object cannot be serialized by the framework; this is why
/// wrapping is opt-in.
/// </remarks>
public class ProfiledJSObjectReference : IJSObjectReference
{
    private const DynamicallyAccessedMemberTypes JsonSerialized =
        DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties;

    private readonly ProfiledJSRuntime _interceptor;

    private protected ProfiledJSObjectReference(IJSObjectReference inner, JSInteropCollector collector)
    {
        Inner = inner;
        Collector = collector;
        _interceptor = new InterceptorRuntime(collector);
    }

    /// <summary>The original reference.</summary>
    public IJSObjectReference Inner { get; }

    private protected JSInteropCollector Collector { get; }

    private protected ProfiledJSRuntime Interceptor => _interceptor;

    /// <summary>Creates a wrapper that also implements <see cref="IJSInProcessObjectReference"/> when the inner reference does.</summary>
    public static IJSObjectReference Create(IJSObjectReference inner, JSInteropCollector collector)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (inner is ProfiledJSObjectReference)
        {
            return inner;
        }

        return inner is IJSInProcessObjectReference ? new ProfiledJSInProcessObjectReference(inner, collector) : new ProfiledJSObjectReference(inner, collector);
    }

    /// <summary>Creates a wrapper for an in-process reference.</summary>
    public static IJSInProcessObjectReference Create(IJSInProcessObjectReference inner, JSInteropCollector collector) =>
        (IJSInProcessObjectReference)Create((IJSObjectReference)inner, collector);

    /// <inheritdoc />
    public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier, object?[]? args) =>
        _interceptor.InterceptAsync(identifier, args, true, (s, id, a) => ((IJSObjectReference)s!).InvokeAsync<TValue>(id, a), Inner);

    /// <inheritdoc />
    public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        _interceptor.InterceptAsync(identifier, args, true, (s, id, a) => ((IJSObjectReference)s!).InvokeAsync<TValue>(id, cancellationToken, a), Inner);

    /// <inheritdoc />
    public ValueTask<IJSObjectReference> InvokeConstructorAsync(string identifier, object?[]? args) =>
        _interceptor.InterceptAsync(identifier, args, true, (s, id, a) => ((IJSObjectReference)s!).InvokeConstructorAsync(id, a), Inner);

    /// <inheritdoc />
    public ValueTask<IJSObjectReference> InvokeConstructorAsync(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        _interceptor.InterceptAsync(identifier, args, true, (s, id, a) => ((IJSObjectReference)s!).InvokeConstructorAsync(id, cancellationToken, a), Inner);

    /// <inheritdoc />
    public ValueTask<TValue> GetValueAsync<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier) =>
        _interceptor.InterceptAsync(identifier, null, true, (s, id, _) => ((IJSObjectReference)s!).GetValueAsync<TValue>(id), Inner);

    /// <inheritdoc />
    public ValueTask<TValue> GetValueAsync<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier, CancellationToken cancellationToken) =>
        _interceptor.InterceptAsync(identifier, null, true, (s, id, _) => ((IJSObjectReference)s!).GetValueAsync<TValue>(id, cancellationToken), Inner);

    /// <inheritdoc />
    public ValueTask SetValueAsync<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier, TValue value) =>
        _interceptor.InterceptVoidAsync(identifier, [value], (s, id) => ((IJSObjectReference)s!).SetValueAsync(id, value), Inner);

    /// <inheritdoc />
    public ValueTask SetValueAsync<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier, TValue value, CancellationToken cancellationToken) =>
        _interceptor.InterceptVoidAsync(identifier, [value], (s, id) => ((IJSObjectReference)s!).SetValueAsync(id, value, cancellationToken), Inner);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return Inner.DisposeAsync();
    }

    /// <summary>A runtime instance used only for its interception helpers.</summary>
    private sealed class InterceptorRuntime(JSInteropCollector collector) : ProfiledJSRuntime(NullRuntime.Instance, collector);

    private sealed class NullRuntime : IJSRuntime
    {
        public static readonly NullRuntime Instance = new();

        public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier, object?[]? args) => throw new NotSupportedException();

        public ValueTask<TValue> InvokeAsync<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
    }
}

/// <summary>A decorating <see cref="IJSInProcessObjectReference"/> that records calls.</summary>
public sealed class ProfiledJSInProcessObjectReference : ProfiledJSObjectReference, IJSInProcessObjectReference
{
    private const DynamicallyAccessedMemberTypes JsonSerialized =
        DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties;

    internal ProfiledJSInProcessObjectReference(IJSObjectReference inner, JSInteropCollector collector)
        : base(inner, collector)
    {
    }

    private IJSInProcessObjectReference Sync => (IJSInProcessObjectReference)Inner;

    /// <inheritdoc />
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed.")]
    public TValue Invoke<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier, params object?[]? args) =>
        Interceptor.Intercept(identifier, args, true, (s, id, a) => ((IJSInProcessObjectReference)s!).Invoke<TValue>(id, a), Sync);

    /// <inheritdoc />
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed.")]
    public IJSInProcessObjectReference InvokeConstructor(string identifier, object?[]? args) =>
        Interceptor.Intercept(identifier, args, true, (s, id, a) => ((IJSInProcessObjectReference)s!).InvokeConstructor(id, a), Sync);

    /// <inheritdoc />
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed.")]
    public TValue GetValue<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier) =>
        Interceptor.Intercept(identifier, null, true, (s, id, _) => ((IJSInProcessObjectReference)s!).GetValue<TValue>(id), Sync);

    /// <inheritdoc />
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed.")]
    public void SetValue<[DynamicallyAccessedMembers(JsonSerialized)] TValue>(string identifier, TValue value) =>
        Interceptor.Intercept<object?>(identifier, [value], true, (s, id, _) =>
        {
            ((IJSInProcessObjectReference)s!).SetValue(id, value);
            return null;
        }, Sync);

    /// <inheritdoc />
    public void Dispose() => Sync.Dispose();
}
