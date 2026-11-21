namespace BlazorProfiler.Demo.Client;

public sealed record Product(int Id, string Name, decimal Price);

public sealed record ProductDetails(int Id, string Name, string Description, int Stock);

public sealed record Settings(string Currency, int PageSize);

/// <summary>A process-wide event used by the "leaky" demo to show a classic subscription leak.</summary>
public static class DemoEvents
{
    public static event Action? Tick;

    public static int SubscriberCount => Tick?.GetInvocationList().Length ?? 0;

    public static void Raise() => Tick?.Invoke();
}

public static class DemoCatalog
{
    public static readonly (string Key, string Title, string Rules)[] All =
    [
        ("render-storm", "Render storm", "BP1001, BP1003, BP1004, BP6004"),
        ("large-list", "Large list", "BP1002, BP1006, BP1007, BP6006, BP7009"),
        ("slow-handler", "Slow handlers", "BP1005, BP2001, BP6001, BP6003"),
        ("search", "Search as you type", "BP2002"),
        ("requests", "Request patterns", "BP2003, BP3001-BP3003, BP3006, BP3007"),
        ("flaky", "Failing endpoint", "BP3004, BP3005"),
        ("interop", "Interop chatter", "BP4001-BP4004, BP4007, BP7008"),
        ("allocations", "Allocation spike", "BP5001, BP5002"),
        ("leaky", "Leaky component", "BP4005, BP4006, BP5003, BP5004"),
        ("churn", "Rows recreated", "BP1008"),
        ("layout", "Layout thrashing", "BP7002, BP7006"),
        ("frustration", "Rage and dead clicks", "BP7007"),
        ("thread-pool", "Thread-pool saturation", "BP6005"),
    ];
}
