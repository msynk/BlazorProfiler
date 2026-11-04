using BlazorProfiler.Samples.Client;

namespace BlazorProfiler.Samples;

/// <summary>A small API used by the demos. Delays simulate realistic server latency.</summary>
internal static class DemoApi
{
    public static void MapDemoApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");
        api.MapGet("/products", async () =>
        {
            await Task.Delay(40);
            return Enumerable.Range(1, 12).Select(i => new Product(i, $"Product {i}", i * 9.99m));
        });
        api.MapGet("/products/{id:int}", async (int id) =>
        {
            await Task.Delay(30);
            return new ProductDetails(id, $"Product {id}", $"Details of product {id}", id * 3);
        });
        api.MapGet("/settings", async () =>
        {
            await Task.Delay(25);
            return new Settings("EUR", 20);
        });
        api.MapGet("/profile", async () =>
        {
            await Task.Delay(60);
            return "{\"name\":\"Demo user\"}";
        });
        api.MapGet("/orders", async () =>
        {
            await Task.Delay(80);
            return "[{\"id\":1},{\"id\":2}]";
        });
        api.MapGet("/slow", async (int? ms) =>
        {
            await Task.Delay(Math.Clamp(ms ?? 1000, 0, 10_000));
            return new string('r', 2048);
        });
        api.MapGet("/flaky", async () =>
        {
            await Task.Delay(15);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        });
    }
}
