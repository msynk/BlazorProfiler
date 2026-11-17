namespace BlazorProfiler.Tests.Components;

/// <summary>A singleton state container with a change event, as used by many Blazor apps.</summary>
public sealed class AppState
{
    public event Action? Changed;

    public void Notify() => Changed?.Invoke();
}
