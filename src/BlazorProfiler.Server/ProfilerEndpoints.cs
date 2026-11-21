using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BlazorProfiler.Analysis;
using BlazorProfiler.Api;
using BlazorProfiler.Export;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorProfiler.Server;

/// <summary>Maps the profiler dashboard and its HTTP API.</summary>
public static class ProfilerEndpoints
{
    /// <summary>The request header required on every state-changing request (cross-site request forgery protection).</summary>
    public const string RequestHeader = "X-Blazor-Profiler";

    private const string AccessKey = "BlazorProfiler.Access";

    /// <summary>
    /// Maps <c>{prefix}/</c> (redirects to the dashboard) and <c>{prefix}/api/*</c>. Nothing is mapped when the profiler is
    /// disabled. Every endpoint evaluates access per request: callers without access receive 404 so the endpoints are not
    /// discoverable.
    /// </summary>
    public static IEndpointConventionBuilder MapBlazorProfiler(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetService<ProfilerServerOptions>()
            ?? throw new InvalidOperationException("Call builder.AddBlazorProfiler() before MapBlazorProfiler().");
        var group = endpoints.MapGroup(options.RoutePrefix);
        var profiler = endpoints.ServiceProvider.GetRequiredService<IProfiler>();
        if (!profiler.IsEnabled)
        {
            return group;
        }

        group.AddEndpointFilter(FilterAsync);
        group.ExcludeFromDescription();

        group.MapGet("/", (HttpContext context) =>
        {
            var basePath = context.Request.PathBase.Value?.TrimEnd('/') ?? string.Empty;
            var api = Uri.EscapeDataString(basePath + options.RoutePrefix + "/api");
            return Results.Redirect($"{basePath}/_content/BlazorProfiler/dashboard/index.html?source=api&api={api}");
        });

        var api = group.MapGroup("/api");
        api.MapGet("/info", (HttpContext c, ProfilerApi p) => Json(p.GetInfo(Access(c)), ProfilerJsonContext.Default.ProfilerInfo));
        api.MapGet("/circuits", (HttpContext c, CircuitRegistry circuits) =>
        {
            var access = Access(c);
            return Json(circuits.GetCircuits(id => access.CanViewAllCircuits || access.OwnedCircuits.Contains(id)).ToList(), ServerJsonContext.Default.ListCircuitDescription);
        });
        api.MapGet("/leaks", (HttpContext c, ProfilerApi p) => Json(p.GetLeaks(Access(c)), ProfilerJsonContext.Default.LeakReport));
        api.MapGet("/sessions/{id}/events", (HttpContext c, ProfilerApi p, string id, long? after, int? max) =>
            Json(p.GetEvents(Access(c), id, after ?? 0, max ?? 2_000), ProfilerJsonContext.Default.EventPage));
        api.MapGet("/sessions/{id}/summary", (HttpContext c, ProfilerApi p, string id) => Json(p.GetSummary(Access(c), id), ProfilerJsonContext.Default.SessionSummary));
        api.MapGet("/sessions/{id}/findings", (HttpContext c, ProfilerApi p, string id) => Json(p.GetFindings(Access(c), id), ProfilerJsonContext.Default.DiagnosticReport));
        api.MapGet("/sessions/{id}/operations/{operationId:long}", (HttpContext c, ProfilerApi p, string id, long operationId) =>
            Json(p.GetOperation(Access(c), id, operationId), ProfilerJsonContext.Default.ListProfilerEvent));
        api.MapGet("/sessions/{id}/export", async (HttpContext c, ProfilerApi p, string id, string? format) =>
        {
            var (data, fileName, contentType) = await p.ExportAsync(Access(c), id, format, c.RequestAborted);
            return Results.File(data, contentType, fileName);
        });

        api.MapPost("/sessions", async (HttpContext c, ProfilerApi p) =>
        {
            var request = await ReadAsync(c, ServerJsonContext.Default.StartSessionRequest) ?? new StartSessionRequest();
            return Json(p.StartSession(Access(c), request.Name, request.CircuitId), ProfilerJsonContext.Default.ProfilerSessionStatistics);
        });
        api.MapPost("/sessions/{id}/{action}", (HttpContext c, ProfilerApi p, string id, string action, string? name) =>
            Json(p.Control(Access(c), id, action, name), ProfilerJsonContext.Default.ProfilerSessionStatistics));
        api.MapDelete("/sessions/{id}", (HttpContext c, ProfilerApi p, string id) =>
        {
            p.RemoveSession(Access(c), id);
            return Results.NoContent();
        });
        api.MapPost("/import", async (HttpContext c, ProfilerApi p) =>
        {
            if (c.Request.ContentLength > options.MaxImportBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var sizeFeature = c.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
            {
                sizeFeature.MaxRequestBodySize = options.MaxImportBytes;
            }

            var statistics = await p.ImportAsync(Access(c), c.Request.Body, c.RequestAborted);
            return Json(statistics, ProfilerJsonContext.Default.ProfilerSessionStatistics);
        });
        api.MapPost("/suppressions", async (HttpContext c, ProfilerApi p) =>
        {
            var request = await ReadAsync(c, ProfilerJsonContext.Default.FindingSuppression)
                ?? throw new ProfilerApiException("A suppression is required.");
            p.Suppress(Access(c), request.Fingerprint, request.Kind, request.Note);
            return Results.NoContent();
        });
        api.MapDelete("/suppressions", (HttpContext c, ProfilerApi p, string fingerprint) =>
        {
            p.Unsuppress(Access(c), fingerprint);
            return Results.NoContent();
        });
        api.MapPost("/sessions/{id}/findings/explain", async (HttpContext c, ProfilerApi p, string id, string? fingerprint) =>
            Json(await p.ExplainFindingAsync(Access(c), id, fingerprint, c.RequestAborted), ProfilerJsonContext.Default.FindingExplanation));
        api.MapPost("/commands/{name}", async (HttpContext c, ProfilerApi p, string name, string? argument) =>
            Json(await p.ExecuteCommandAsync(Access(c), name, argument, c.RequestAborted), ProfilerJsonContext.Default.ProfilerCommandResult));

        if (options.EnableMcp)
        {
            MapMcp(endpoints, options);
        }

        return group;
    }

    /// <summary>
    /// Maps the Model Context Protocol endpoint (Streamable HTTP, JSON responses, no server-sent event stream). MCP clients
    /// cannot send the CSRF header of the dashboard API, so the endpoint protects itself differently: it requires a JSON
    /// content type (which a cross-site page cannot send without a CORS preflight, which is never answered) and rejects
    /// browser requests whose <c>Origin</c> is not a local host (DNS rebinding).
    /// </summary>
    private static void MapMcp(IEndpointRouteBuilder endpoints, ProfilerServerOptions options)
    {
        var mcp = endpoints.MapGroup(options.RoutePrefix + "/mcp");
        mcp.ExcludeFromDescription();
        var server = new ProfilerMcpServer(endpoints.ServiceProvider.GetRequiredService<ProfilerApi>(), new ProfilerMcpOptions { AllowControl = options.McpAllowControl });
        mcp.MapPost(string.Empty, async (HttpContext context) =>
        {
            var evaluator = context.RequestServices.GetRequiredService<ProfilerAccessEvaluator>();
            var access = await evaluator.EvaluateAsync(context);
            if (access is null)
            {
                return Results.NotFound();
            }

            if (!IsAllowedMcpOrigin(context.Request))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!context.Request.HasJsonContentType())
            {
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            }

            if (context.Request.ContentLength > options.MaxMcpRequestBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
            {
                sizeFeature.MaxRequestBodySize = options.MaxMcpRequestBytes;
            }

            // Read at most the limit, whatever the server's own body size enforcement (chunked bodies, test hosts, or
            // buffering middleware can bypass it).
            string? body;
            try
            {
                body = await ReadBoundedAsync(context.Request.Body, options.MaxMcpRequestBytes, context.RequestAborted);
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                body = null;
            }

            if (body is null)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            var response = await server.HandleAsync(body, access, context.RequestAborted);
            return response is null
                ? Results.StatusCode(StatusCodes.Status202Accepted)
                : Results.Text(response, "application/json", System.Text.Encoding.UTF8);
        });

        // No server-initiated stream and no session state: GET (SSE) and DELETE (session end) are not supported. Callers
        // without access get 404 like everywhere else, so the endpoint is not discoverable.
        mcp.MapMethods(string.Empty, [HttpMethods.Get, HttpMethods.Delete], async (HttpContext context) =>
        {
            var evaluator = context.RequestServices.GetRequiredService<ProfilerAccessEvaluator>();
            return await evaluator.EvaluateAsync(context) is null
                ? Results.NotFound()
                : Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        });
    }

    /// <summary>Reads a UTF-8 body of at most <paramref name="maxBytes"/> bytes; returns null when it is longer.</summary>
    private static async Task<string?> ReadBoundedAsync(Stream body, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static bool IsAllowedMcpOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            // Not a browser request.
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static async ValueTask<object?> FilterAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var evaluator = context.RequestServices.GetRequiredService<ProfilerAccessEvaluator>();
        var access = await evaluator.EvaluateAsync(context);
        if (access is null)
        {
            return Results.NotFound();
        }

        var method = context.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !context.Request.Headers.ContainsKey(RequestHeader))
        {
            // Custom headers cannot be added by cross-site forms, so this blocks cross-site request forgery.
            return Results.BadRequest(new ProblemBody { Error = $"The {RequestHeader} header is required." });
        }

        context.Items[AccessKey] = access;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        try
        {
            return await next(invocation);
        }
        catch (ProfilerApiException ex)
        {
            return Results.Json(new ProblemBody { Error = ex.Message }, ServerJsonContext.Default.ProblemBody, statusCode: ex.StatusCode);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
    }

    private static ProfilerApiAccess Access(HttpContext context) =>
        context.Items[AccessKey] as ProfilerApiAccess ?? throw new ProfilerApiException("Not found.") { StatusCode = 404 };

    private static IResult Json<T>(T value, JsonTypeInfo<T> typeInfo) => Results.Json(value, typeInfo);

    private static async Task<T?> ReadAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo)
    {
        if (context.Request.ContentLength is 0 || !context.Request.HasJsonContentType())
        {
            return default;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync(context.Request.Body, typeInfo, context.RequestAborted);
        }
        catch (JsonException)
        {
            throw new ProfilerApiException("Invalid JSON body.");
        }
    }
}

/// <summary>The body of a start-session request.</summary>
public sealed class StartSessionRequest
{
    /// <summary>The session name.</summary>
    public string? Name { get; set; }

    /// <summary>Restricts the session to one circuit.</summary>
    public string? CircuitId { get; set; }
}

/// <summary>An error response.</summary>
public sealed class ProblemBody
{
    /// <summary>The error message.</summary>
    public string? Error { get; set; }
}

/// <summary>Source-generated JSON metadata for server-specific payloads.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<CircuitDescription>))]
[JsonSerializable(typeof(StartSessionRequest))]
[JsonSerializable(typeof(ProblemBody))]
[JsonSerializable(typeof(SuppressionKind))]
internal sealed partial class ServerJsonContext : JsonSerializerContext;
