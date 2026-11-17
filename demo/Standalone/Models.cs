namespace BlazorProfiler.Demo.Standalone;

public sealed record Product(int Id, string Name, decimal Price);

/// <summary>An application-wide state container with a change event, as many Blazor apps have.</summary>
public sealed class AppState
{
    public event Action? Changed;

    public void Notify() => Changed?.Invoke();
}
