using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using BlazorProfiler.Correlation;
using BlazorProfiler.Events;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorProfiler.Instrumentation;

/// <summary>
/// An instrumented replacement for <see cref="ComponentBase"/> that records per-instance render duration, lifecycle method
/// timing (synchronous and awaited), parameter changes, render requests, render-output changes, children, and disposal.
/// </summary>
/// <remarks>
/// <para>
/// The class reproduces the behavior of <see cref="ComponentBase"/> (.NET 10) member for member, using only public APIs,
/// so components can opt in without code changes: add <c>@inherits BlazorProfiler.Instrumentation.ProfiledComponentBase</c>
/// to <c>_Imports.razor</c> (it applies to components without an explicit <c>@inherits</c>) or to individual components.
/// </para>
/// <para>
/// When the profiler is not registered or not recording, each hook costs a single field check. Components can still be
/// referenced as <see cref="ComponentBase"/>-like types, but not cast to <see cref="ComponentBase"/>.
/// </para>
/// <para>
/// Disposal is recorded when the component does not implement <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/>
/// itself; components that implement them keep their own disposal behavior unchanged.
/// </para>
/// </remarks>
public abstract class ProfiledComponentBase : IComponent, IHandleEvent, IHandleAfterRender, IDisposable
{
    private const int MaxTrackedParameters = 64;

    private static readonly ConcurrentDictionary<Type, LifecycleOverrides> s_overrides = new();

    private readonly RenderFragment _renderFragment;
    private (IComponentRenderMode? Mode, bool Cached) _renderMode;
    private RenderHandle _renderHandle;
    private bool _initialized;
    private bool _hasNeverRendered = true;
    private bool _hasPendingQueuedRender;
    private bool _hasCalledOnAfterRender;

    private ComponentInstrumentation? _instrumentation;
    private string? _typeName;
    private long _instanceId;
    private int _renderNumber;
    private RenderReason _nextRenderReason = RenderReason.FirstRender;
    private ulong _lastOutputHash;
    private bool _hasOutputHash;
    private LifecycleOverrides _overrides;
    private string[]? _parameterNames;
    private object?[]? _parameterValues;
    private string[]? _nextParameterNames;
    private object?[]? _nextParameterValues;
    private int _parameterCount;
    private RenderTreeBuilder? _lastBuilder;
    private ulong _childSetHash;
    private bool _disposed;

    /// <summary>Creates the component.</summary>
    protected ProfiledComponentBase()
    {
        _renderFragment = builder =>
        {
            _hasPendingQueuedRender = false;
            _hasNeverRendered = false;
            var instrumentation = _instrumentation;
            if (instrumentation is null || !instrumentation.IsActive)
            {
                BuildRenderTree(builder);
                return;
            }

            RenderInstrumented(builder, instrumentation);
        };
    }

    /// <summary>Services used to resolve the profiler. Injected by the framework; not intended for application use.</summary>
    [Inject]
    private IServiceProvider? ProfilerServiceProvider { get; set; }

    /// <summary>Gets the <see cref="Microsoft.AspNetCore.Components.RendererInfo"/> the component is running on.</summary>
    protected RendererInfo RendererInfo => _renderHandle.RendererInfo;

    /// <summary>Gets the <see cref="ResourceAssetCollection"/> for the application.</summary>
    protected ResourceAssetCollection Assets => _renderHandle.Assets;

    /// <summary>Gets the <see cref="IComponentRenderMode"/> assigned to this component.</summary>
    protected IComponentRenderMode? AssignedRenderMode
    {
        get
        {
            if (!_renderMode.Cached)
            {
                _renderMode = (_renderHandle.RenderMode, true);
            }

            return _renderMode.Mode;
        }
    }

    /// <summary>The profiler instance identifier of this component, or zero when instrumentation is inactive.</summary>
    protected long ProfilerInstanceId => _instanceId;

    /// <summary>Renders the component to the supplied <see cref="RenderTreeBuilder"/>.</summary>
    protected virtual void BuildRenderTree(RenderTreeBuilder builder)
    {
    }

    /// <summary>Method invoked when the component is ready to start, having received its initial parameters.</summary>
    protected virtual void OnInitialized()
    {
    }

    /// <summary>Method invoked when the component is ready to start, having received its initial parameters.</summary>
    protected virtual Task OnInitializedAsync() => Task.CompletedTask;

    /// <summary>Method invoked when the component has received parameters from its parent.</summary>
    protected virtual void OnParametersSet()
    {
    }

    /// <summary>Method invoked when the component has received parameters from its parent.</summary>
    protected virtual Task OnParametersSetAsync() => Task.CompletedTask;

    /// <summary>Notifies the component that its state has changed and queues a render.</summary>
    protected void StateHasChanged()
    {
        var instrumentation = _instrumentation;
        if (_hasPendingQueuedRender)
        {
            RecordRenderRequest(instrumentation, RenderRequestResult.Coalesced);
            return;
        }

        if (!_hasNeverRendered && !ShouldRender() && !_renderHandle.IsRenderingOnMetadataUpdate)
        {
            RecordRenderRequest(instrumentation, RenderRequestResult.SuppressedByShouldRender);
            return;
        }

        _hasPendingQueuedRender = true;
        RecordRenderRequest(instrumentation, RenderRequestResult.Queued);
        try
        {
            _renderHandle.Render(_renderFragment);
        }
        catch
        {
            _hasPendingQueuedRender = false;
            throw;
        }
    }

    /// <summary>Returns a flag to indicate whether the component should render.</summary>
    protected virtual bool ShouldRender() => true;

    /// <summary>Method invoked after each time the component has rendered interactively and the UI has finished updating.</summary>
    protected virtual void OnAfterRender(bool firstRender)
    {
    }

    /// <summary>Method invoked after each time the component has been rendered interactively and the UI has finished updating.</summary>
    protected virtual Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;

    /// <summary>Executes the supplied work item on the associated renderer's synchronization context.</summary>
    protected Task InvokeAsync(Action workItem) => _renderHandle.Dispatcher.InvokeAsync(workItem);

    /// <summary>Executes the supplied work item on the associated renderer's synchronization context.</summary>
    protected Task InvokeAsync(Func<Task> workItem) => _renderHandle.Dispatcher.InvokeAsync(workItem);

    /// <summary>Treats the supplied exception as being thrown by this component.</summary>
    protected Task DispatchExceptionAsync(Exception exception) => _renderHandle.DispatchExceptionAsync(exception);

    void IComponent.Attach(RenderHandle renderHandle)
    {
        if (_renderHandle.IsInitialized)
        {
            throw new InvalidOperationException($"The render handle is already set. Cannot initialize a {nameof(ComponentBase)} more than once.");
        }

        _renderHandle = renderHandle;
        AttachInstrumentation();
    }

    /// <summary>Sets parameters supplied by the component's parent in the render tree.</summary>
    public virtual Task SetParametersAsync(ParameterView parameters)
    {
        var instrumentation = _instrumentation;
        if (instrumentation is not null && instrumentation.IsActive)
        {
            return SetParametersInstrumented(parameters, instrumentation);
        }

        parameters.SetParameterProperties(this);
        if (!_initialized)
        {
            _initialized = true;
            return RunInitAndSetParametersAsync(null);
        }

        return CallOnParametersSetAsync(null);
    }

    Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg)
    {
        var instrumentation = _instrumentation;
        Task task;
        if (instrumentation is not null && instrumentation.IsActive)
        {
            var clock = instrumentation.Profiler.Clock;
            var start = clock.GetTimestamp();
            var interaction = ProfilerContext.Current;
            var previous = ProfilerContext.Push(ProfilerContext.CreateComponentFrame(_typeName, _instanceId, null, start));
            try
            {
                task = callback.InvokeAsync(arg);
            }
            finally
            {
                ProfilerContext.Restore(previous);
            }

            if (interaction is { Kind: OperationKind.Interaction })
            {
                interaction.HandlerSynchronousTicks = clock.GetTimestamp() - start;
                interaction.HandlerInstanceId = _instanceId;
            }

            _nextRenderReason = RenderReason.EventHandler;
        }
        else
        {
            task = callback.InvokeAsync(arg);
        }

        var shouldAwaitTask = task.Status != TaskStatus.RanToCompletion && task.Status != TaskStatus.Canceled;
        StateHasChanged();
        return shouldAwaitTask ? CallStateHasChangedOnAsyncCompletion(task, RenderReason.EventHandler) : Task.CompletedTask;
    }

    Task IHandleAfterRender.OnAfterRenderAsync()
    {
        var firstRender = !_hasCalledOnAfterRender;
        _hasCalledOnAfterRender = true;
        var instrumentation = _instrumentation;
        if (instrumentation is null || !instrumentation.IsActive)
        {
            OnAfterRender(firstRender);
            return OnAfterRenderAsync(firstRender);
        }

        if (instrumentation.Options.TrackComponentTree)
        {
            TrackChildren(instrumentation);
        }

        var track = instrumentation.Options.TrackLifecycle;
        if ((_overrides & LifecycleOverrides.OnAfterRender) == 0)
        {
            OnAfterRender(firstRender);
        }
        else
        {
            var start = track ? instrumentation.Profiler.Clock.GetTimestamp() : 0;
            var previous = ProfilerContext.Push(ProfilerContext.CreateComponentFrame(_typeName, _instanceId, LifecycleMethod.OnAfterRender, start));
            try
            {
                OnAfterRender(firstRender);
                if (track)
                {
                    RecordLifecycle(instrumentation, LifecycleMethod.OnAfterRender, start, null);
                }
            }
            finally
            {
                ProfilerContext.Restore(previous);
            }
        }

        if ((_overrides & LifecycleOverrides.OnAfterRenderAsync) == 0)
        {
            return OnAfterRenderAsync(firstRender);
        }

        var asyncStart = track ? instrumentation.Profiler.Clock.GetTimestamp() : 0;
        var previousAsync = ProfilerContext.Push(ProfilerContext.CreateComponentFrame(_typeName, _instanceId, LifecycleMethod.OnAfterRenderAsync, asyncStart));
        Task task;
        try
        {
            task = OnAfterRenderAsync(firstRender);
        }
        finally
        {
            ProfilerContext.Restore(previousAsync);
        }

        if (track)
        {
            RecordLifecycle(instrumentation, LifecycleMethod.OnAfterRenderAsync, asyncStart, task);
        }

        return task;
    }

    void IDisposable.Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var instrumentation = _instrumentation;
        if (instrumentation is not null && instrumentation.IsActive && _instanceId != 0)
        {
            var now = instrumentation.Profiler.Clock.GetTimestamp();
            var created = instrumentation.Registry.GetCreatedTimestamp(this);
            instrumentation.Collector.Publish(new ComponentDisposedEvent
            {
                ComponentType = _typeName,
                ComponentInstanceId = _instanceId,
                Timestamp = now,
                LifetimeTicks = created > 0 ? now - created : 0,
                Source = InstrumentationSource.ComponentInstrumentation,
            });
        }

        // Release references held only for instrumentation.
        _lastBuilder = null;
        _parameterValues = null;
        _parameterNames = null;
        _nextParameterValues = null;
        _nextParameterNames = null;
    }

    private void AttachInstrumentation()
    {
        try
        {
            var instrumentation = ProfilerServiceProvider?.GetService<ComponentInstrumentation>();
            if (instrumentation is null)
            {
                return;
            }

            _instrumentation = instrumentation;
            _typeName = instrumentation.Registry.GetTypeName(GetType());
            _instanceId = instrumentation.Registry.GetOrAssign(this);
            _overrides = s_overrides.GetOrAdd(GetType(), DetectOverrides);
            instrumentation.Registry.MarkInstrumented(_typeName);
        }
        catch (Exception)
        {
            _instrumentation = null;
        }
    }

    private Task RunInitAndSetParametersAsync(ComponentInstrumentation? instrumentation) =>
        instrumentation is null ? RunInitAndSetParametersCoreAsync() : RunInitAndSetParametersInstrumentedAsync(instrumentation);

    private async Task RunInitAndSetParametersCoreAsync()
    {
        OnInitialized();
        var task = OnInitializedAsync();
        if (task.Status != TaskStatus.RanToCompletion && task.Status != TaskStatus.Canceled)
        {
            if (task.Status != TaskStatus.Faulted)
            {
                StateHasChanged();
            }

            try
            {
                await task;
            }
            catch
            {
                if (!task.IsCanceled)
                {
                    throw;
                }
            }
        }

        await CallOnParametersSetAsync(null);
    }

    private async Task RunInitAndSetParametersInstrumentedAsync(ComponentInstrumentation instrumentation)
    {
        var track = instrumentation.Options.TrackLifecycle;
        var clock = instrumentation.Profiler.Clock;

        if ((_overrides & LifecycleOverrides.OnInitialized) == 0)
        {
            OnInitialized();
        }
        else
        {
            var start = track ? clock.GetTimestamp() : 0;
            var previous = ProfilerContext.Push(ProfilerContext.CreateComponentFrame(_typeName, _instanceId, LifecycleMethod.OnInitialized, start));
            try
            {
                OnInitialized();
            }
            finally
            {
                ProfilerContext.Restore(previous);
            }

            if (track)
            {
                RecordLifecycle(instrumentation, LifecycleMethod.OnInitialized, start, null);
            }
        }

        Task task;
        if ((_overrides & LifecycleOverrides.OnInitializedAsync) == 0)
        {
            task = OnInitializedAsync();
        }
        else
        {
            var asyncStart = track ? clock.GetTimestamp() : 0;
            var previous = ProfilerContext.Push(ProfilerContext.CreateComponentFrame(_typeName, _instanceId, LifecycleMethod.OnInitializedAsync, asyncStart));
            try
            {
                task = OnInitializedAsync();
            }
            finally
            {
                ProfilerContext.Restore(previous);
            }

            if (track)
            {
                RecordLifecycle(instrumentation, LifecycleMethod.OnInitializedAsync, asyncStart, task);
            }
        }

        if (task.Status != TaskStatus.RanToCompletion && task.Status != TaskStatus.Canceled)
        {
            if (task.Status != TaskStatus.Faulted)
            {
                StateHasChanged();
            }

            try
            {
                await task;
            }
            catch
            {
                if (!task.IsCanceled)
                {
                    throw;
                }
            }

            _nextRenderReason = RenderReason.AsyncLifecycleCompletion;
        }

        await CallOnParametersSetAsync(instrumentation);
    }

    private Task CallOnParametersSetAsync(ComponentInstrumentation? instrumentation)
    {
        Task task;
        if (instrumentation is not null && instrumentation.IsActive)
        {
            var track = instrumentation.Options.TrackLifecycle;
            var clock = instrumentation.Profiler.Clock;
            if ((_overrides & LifecycleOverrides.OnParametersSet) == 0)
            {
                OnParametersSet();
            }
            else
            {
                var start = track ? clock.GetTimestamp() : 0;
                var previous = ProfilerContext.Push(ProfilerContext.CreateComponentFrame(_typeName, _instanceId, LifecycleMethod.OnParametersSet, start));
                try
                {
                    OnParametersSet();
                }
                finally
                {
                    ProfilerContext.Restore(previous);
                }

                if (track)
                {
                    RecordLifecycle(instrumentation, LifecycleMethod.OnParametersSet, start, null);
                }
            }

            if ((_overrides & LifecycleOverrides.OnParametersSetAsync) == 0)
            {
                task = OnParametersSetAsync();
            }
            else
            {
                var asyncStart = track ? clock.GetTimestamp() : 0;
                var previous = ProfilerContext.Push(ProfilerContext.CreateComponentFrame(_typeName, _instanceId, LifecycleMethod.OnParametersSetAsync, asyncStart));
                try
                {
                    task = OnParametersSetAsync();
                }
                finally
                {
                    ProfilerContext.Restore(previous);
                }

                if (track)
                {
                    RecordLifecycle(instrumentation, LifecycleMethod.OnParametersSetAsync, asyncStart, task);
                }
            }

            if (_renderNumber > 0 && _nextRenderReason != RenderReason.AsyncLifecycleCompletion)
            {
                _nextRenderReason = RenderReason.Parameters;
            }
        }
        else
        {
            OnParametersSet();
            task = OnParametersSetAsync();
        }

        var shouldAwaitTask = task.Status != TaskStatus.RanToCompletion && task.Status != TaskStatus.Canceled;
        if (task.Status != TaskStatus.Faulted)
        {
            StateHasChanged();
        }

        return shouldAwaitTask ? CallStateHasChangedOnAsyncCompletion(task, RenderReason.AsyncLifecycleCompletion) : Task.CompletedTask;
    }

    private async Task CallStateHasChangedOnAsyncCompletion(Task task, RenderReason reason)
    {
        try
        {
            await task;
        }
        catch
        {
            if (task.IsCanceled)
            {
                return;
            }

            throw;
        }

        if (_instrumentation is not null)
        {
            _nextRenderReason = reason;
        }

        StateHasChanged();
    }

    private Task SetParametersInstrumented(ParameterView parameters, ComponentInstrumentation instrumentation)
    {
        var clock = instrumentation.Profiler.Clock;
        var start = clock.GetTimestamp();
        var changeState = ParameterChangeState.Unknown;
        string[]? changed = null;
        var count = 0;
        if (instrumentation.Options.TrackParameterChanges)
        {
            changeState = CompareParameters(parameters, out changed, out count);
        }
        else
        {
            foreach (var _ in parameters)
            {
                count++;
            }
        }

        // Overridden lifecycle methods push their own component frames, so no frame is needed around the whole call
        // (each ambient-context change allocates).
        Task task;
        try
        {
            parameters.SetParameterProperties(this);
            if (!_initialized)
            {
                _initialized = true;
                task = RunInitAndSetParametersAsync(instrumentation);
            }
            else
            {
                task = CallOnParametersSetAsync(instrumentation);
            }
        }
        catch (Exception ex)
        {
            EmitParametersSet(instrumentation, start, clock.GetTimestamp(), changeState, changed, count, ex);
            throw;
        }

        if (task.IsCompleted)
        {
            EmitParametersSet(instrumentation, start, clock.GetTimestamp(), changeState, changed, count, task.Exception?.InnerException);
        }
        else
        {
            var state = new ParametersCompletionState(this, instrumentation, start, changeState, changed, count);
            _ = task.ContinueWith(
                static (t, s) =>
                {
                    var st = (ParametersCompletionState)s!;
                    st.Component.EmitParametersSet(st.Instrumentation, st.Start, st.Instrumentation.Profiler.Clock.GetTimestamp(), st.ChangeState, st.Changed, st.Count, t.IsCanceled ? new OperationCanceledException() : t.Exception?.InnerException);
                },
                state,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return task;
    }

    private void EmitParametersSet(ComponentInstrumentation instrumentation, long start, long end, ParameterChangeState state, string[]? changed, int count, Exception? exception)
    {
        try
        {
            instrumentation.Collector.Publish(new ParametersSetEvent
            {
                ComponentType = _typeName,
                ComponentInstanceId = _instanceId,
                Timestamp = start,
                Duration = end - start,
                ChangeState = state,
                ChangedParameters = changed,
                ParameterCount = count,
                Source = InstrumentationSource.ComponentInstrumentation,
                Outcome = exception is null ? ProfilerOutcome.Success : exception is OperationCanceledException ? ProfilerOutcome.Canceled : ProfilerOutcome.Error,
                ErrorType = exception?.GetType().FullName,
            });
        }
        catch (Exception)
        {
            // Profiling must never affect the component.
        }
    }

    private ParameterChangeState CompareParameters(ParameterView parameters, out string[]? changedNames, out int count)
    {
        changedNames = null;
        var first = _parameterNames is null;
        var names = _nextParameterNames ?? new string[Math.Max(4, _parameterCount)];
        var values = _nextParameterValues ?? new object?[names.Length];
        count = 0;
        List<string>? valueChanged = null;
        List<string>? referenceChanged = null;

        foreach (var parameter in parameters)
        {
            if (count >= MaxTrackedParameters)
            {
                break;
            }

            if (count == names.Length)
            {
                Array.Resize(ref names, names.Length * 2);
                Array.Resize(ref values, values.Length * 2);
            }

            names[count] = parameter.Name;
            values[count] = parameter.Value;
            count++;

            if (first)
            {
                continue;
            }

            var index = FindPrevious(parameter.Name);
            if (index < 0)
            {
                (valueChanged ??= []).Add(parameter.Name);
                continue;
            }

            switch (ParameterComparer.Compare(_parameterValues![index], parameter.Value))
            {
                case ParameterComparison.Changed:
                    (valueChanged ??= []).Add(parameter.Name);
                    break;
                case ParameterComparison.DifferentInstance:
                    (referenceChanged ??= []).Add(parameter.Name);
                    break;
            }
        }

        var previousCount = _parameterCount;

        // Double-buffer the arrays so steady-state parameter updates do not allocate.
        _nextParameterNames = _parameterNames;
        _nextParameterValues = _parameterValues;
        if (_nextParameterValues is not null)
        {
            Array.Clear(_nextParameterValues);
        }

        _parameterNames = names;
        _parameterValues = values;
        _parameterCount = count;

        if (first)
        {
            return ParameterChangeState.Initial;
        }

        if (valueChanged is not null || previousCount != count)
        {
            changedNames = valueChanged?.ToArray();
            return ParameterChangeState.Changed;
        }

        if (referenceChanged is not null)
        {
            changedNames = [.. referenceChanged];
            return ParameterChangeState.ChangedByReferenceOnly;
        }

        return ParameterChangeState.Unchanged;
    }

    private int FindPrevious(string name)
    {
        var names = _parameterNames!;
        for (var i = 0; i < _parameterCount; i++)
        {
            if (ReferenceEquals(names[i], name) || string.Equals(names[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private void RenderInstrumented(RenderTreeBuilder builder, ComponentInstrumentation instrumentation)
    {
        var clock = instrumentation.Profiler.Clock;
        var reason = _renderNumber == 0 ? RenderReason.FirstRender : _nextRenderReason;
        _nextRenderReason = RenderReason.StateHasChanged;
        var start = clock.GetTimestamp();
        Exception? failure = null;
        try
        {
            BuildRenderTree(builder);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            var end = clock.GetTimestamp();
            _renderNumber++;
            try
            {
                var frameCount = -1;
                bool? unchanged = null;
                if (failure is null)
                {
                    var frames = builder.GetFrames();
                    frameCount = frames.Count;
                    if (instrumentation.Options.TrackRenderOutput)
                    {
                        var hash = RenderOutputHasher.Hash(frames);
                        unchanged = _hasOutputHash ? hash == _lastOutputHash : null;
                        _lastOutputHash = hash;
                        _hasOutputHash = true;
                    }

                    if (instrumentation.Options.TrackComponentTree)
                    {
                        _lastBuilder = builder;
                    }
                }

                instrumentation.Collector.Publish(new ComponentRenderEvent
                {
                    ComponentType = _typeName,
                    ComponentInstanceId = _instanceId,
                    Timestamp = start,
                    Duration = end - start,
                    FrameCount = frameCount,
                    OutputUnchanged = unchanged,
                    Reason = reason,
                    RenderNumber = _renderNumber,
                    Source = InstrumentationSource.ComponentInstrumentation,
                    Outcome = failure is null ? ProfilerOutcome.Success : ProfilerOutcome.Error,
                    ErrorType = failure?.GetType().FullName,
                });
            }
            catch (Exception)
            {
                // Profiling must never affect rendering.
            }
        }
    }

    private void RecordRenderRequest(ComponentInstrumentation? instrumentation, RenderRequestResult result)
    {
        // Queued requests correspond one-to-one with render events, so only requests that did not render are recorded.
        if (result == RenderRequestResult.Queued || instrumentation is null || !instrumentation.IsActive || !instrumentation.Options.TrackRenderRequests)
        {
            return;
        }

        instrumentation.Collector.Publish(new RenderRequestEvent
        {
            ComponentType = _typeName,
            ComponentInstanceId = _instanceId,
            Timestamp = instrumentation.Profiler.Clock.GetTimestamp(),
            Result = result,
            Source = InstrumentationSource.ComponentInstrumentation,
        });
    }

    private void RecordLifecycle(ComponentInstrumentation instrumentation, LifecycleMethod method, long start, Task? task)
    {
        var clock = instrumentation.Profiler.Clock;
        var syncEnd = clock.GetTimestamp();
        if (task is null || task.IsCompleted)
        {
            if (syncEnd - start < clock.FromTimeSpan(instrumentation.Options.MinimumLifecycleDuration) && task?.IsCompletedSuccessfully != false)
            {
                return;
            }

            PublishLifecycle(instrumentation, method, start, syncEnd, syncEnd - start, completedAsynchronously: false, task?.Exception?.InnerException ?? (task?.IsCanceled == true ? new OperationCanceledException() : null));
            return;
        }

        var state = new LifecycleCompletionState(this, instrumentation, method, start, syncEnd - start);
        _ = task.ContinueWith(
            static (t, s) =>
            {
                var st = (LifecycleCompletionState)s!;
                st.Component.PublishLifecycle(st.Instrumentation, st.Method, st.Start, st.Instrumentation.Profiler.Clock.GetTimestamp(), st.SynchronousTicks, completedAsynchronously: true, t.IsCanceled ? new OperationCanceledException() : t.Exception?.InnerException);
            },
            state,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void PublishLifecycle(ComponentInstrumentation instrumentation, LifecycleMethod method, long start, long end, long synchronousTicks, bool completedAsynchronously, Exception? exception)
    {
        try
        {
            instrumentation.Collector.Publish(new LifecycleMethodEvent
            {
                ComponentType = _typeName,
                ComponentInstanceId = _instanceId,
                Method = method,
                Timestamp = start,
                Duration = end - start,
                SynchronousDuration = synchronousTicks,
                CompletedAsynchronously = completedAsynchronously,
                Source = InstrumentationSource.ComponentInstrumentation,
                Outcome = exception is null ? ProfilerOutcome.Success : exception is OperationCanceledException ? ProfilerOutcome.Canceled : ProfilerOutcome.Error,
                ErrorType = exception?.GetType().FullName,
            });
        }
        catch (Exception)
        {
            // Profiling must never affect the component.
        }
    }

    private void TrackChildren(ComponentInstrumentation instrumentation)
    {
        var builder = _lastBuilder;
        if (builder is null || _disposed)
        {
            return;
        }

        try
        {
            var frames = builder.GetFrames();
            List<long>? ids = null;
            List<string>? types = null;
            ulong hash = 14695981039346656037UL;
            for (var i = 0; i < frames.Count; i++)
            {
                ref var frame = ref frames.Array[i];
                if (frame.FrameType != Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Component)
                {
                    continue;
                }

                var child = frame.Component;
                if (child is null)
                {
                    continue;
                }

                var id = instrumentation.Registry.GetOrAssign(child);
                (ids ??= []).Add(id);
                (types ??= []).Add(instrumentation.Registry.GetTypeName(frame.ComponentType));
                hash = (hash ^ (ulong)id) * 1099511628211UL;
            }

            if (hash == _childSetHash)
            {
                return;
            }

            _childSetHash = hash;
            instrumentation.Collector.Publish(new ComponentChildrenEvent
            {
                ComponentType = _typeName,
                ComponentInstanceId = _instanceId,
                Timestamp = instrumentation.Profiler.Clock.GetTimestamp(),
                ChildInstanceIds = ids?.ToArray() ?? [],
                ChildTypes = types?.ToArray() ?? [],
                Source = InstrumentationSource.ComponentInstrumentation,
            });
        }
        catch (Exception)
        {
            // Tree discovery is best effort.
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Blazor preserves all members of component types. A missing method is treated as overridden, which only adds detail.")]
    private static LifecycleOverrides DetectOverrides(Type type)
    {
        var result = LifecycleOverrides.None;
        if (IsOverridden(type, nameof(OnInitialized), Type.EmptyTypes))
        {
            result |= LifecycleOverrides.OnInitialized;
        }

        if (IsOverridden(type, nameof(OnInitializedAsync), Type.EmptyTypes))
        {
            result |= LifecycleOverrides.OnInitializedAsync;
        }

        if (IsOverridden(type, nameof(OnParametersSet), Type.EmptyTypes))
        {
            result |= LifecycleOverrides.OnParametersSet;
        }

        if (IsOverridden(type, nameof(OnParametersSetAsync), Type.EmptyTypes))
        {
            result |= LifecycleOverrides.OnParametersSetAsync;
        }

        if (IsOverridden(type, nameof(OnAfterRender), [typeof(bool)]))
        {
            result |= LifecycleOverrides.OnAfterRender;
        }

        if (IsOverridden(type, nameof(OnAfterRenderAsync), [typeof(bool)]))
        {
            result |= LifecycleOverrides.OnAfterRenderAsync;
        }

        return result;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Blazor preserves all members of component types. A missing method is treated as overridden, which only adds detail.")]
    private static bool IsOverridden(Type type, string name, Type[] parameters)
    {
        try
        {
            var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, parameters, modifiers: null);
            return method is null || method.DeclaringType != typeof(ProfiledComponentBase);
        }
        catch (AmbiguousMatchException)
        {
            return true;
        }
    }

    [Flags]
    private enum LifecycleOverrides
    {
        None = 0,
        OnInitialized = 1,
        OnInitializedAsync = 2,
        OnParametersSet = 4,
        OnParametersSetAsync = 8,
        OnAfterRender = 16,
        OnAfterRenderAsync = 32,
    }

    private sealed record ParametersCompletionState(ProfiledComponentBase Component, ComponentInstrumentation Instrumentation, long Start, ParameterChangeState ChangeState, string[]? Changed, int Count);

    private sealed record LifecycleCompletionState(ProfiledComponentBase Component, ComponentInstrumentation Instrumentation, LifecycleMethod Method, long Start, long SynchronousTicks);
}
