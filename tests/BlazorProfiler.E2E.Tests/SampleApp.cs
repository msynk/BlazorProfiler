using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.Playwright;

// Tests share one running app and profiler; run them sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace BlazorProfiler.E2E.Tests;

/// <summary>Runs the sample application as a real Kestrel process and a headless Chromium browser.</summary>
public class SampleApp : IAsyncLifetime
{
    private Process? _process;

    protected virtual string EnvironmentName => "Development";

    public string BaseUrl { get; private set; } = "";

    public HttpClient Http { get; private set; } = default!;

    public IPlaywright Playwright { get; private set; } = default!;

    public IBrowser Browser { get; private set; } = default!;

    public async ValueTask InitializeAsync()
    {
        var assembly = typeof(SampleApp).Assembly;
        string Metadata(string key) => assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;
        var projectDirectory = Path.GetFullPath(Metadata("SampleDirectory"));
        var dll = Path.Combine(projectDirectory, "bin", Metadata("Configuration"), "net10.0", "BlazorProfiler.Demo.Server.dll");
        if (!File.Exists(dll))
        {
            throw new InvalidOperationException($"Sample not built: {dll}");
        }

        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        var start = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls {BaseUrl.TrimEnd('/')}")
        {
            WorkingDirectory = projectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = EnvironmentName;
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        _process = Process.Start(start)!;
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        Http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                using var response = await Http.GetAsync("/");
                if (response.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch (HttpRequestException)
            {
            }

            if (DateTime.UtcNow > deadline || _process.HasExited)
            {
                throw new InvalidOperationException("The sample app did not start.");
            }

            await Task.Delay(250);
        }

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        Playwright?.Dispose();
        Http?.Dispose();
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Calls the server profiler API.</summary>
    public async Task<JsonElement> ApiAsync(string path, HttpMethod? method = null, object? body = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, "_blazor-profiler/api" + path);
        if (method is not null && method != HttpMethod.Get)
        {
            request.Headers.Add("X-Blazor-Profiler", "1");
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Starts a fresh process-wide session so each test analyzes only its own activity.</summary>
    public async Task<string> StartSessionAsync(string name)
    {
        var info = await ApiAsync("/info");
        foreach (var session in info.GetProperty("sessions").EnumerateArray())
        {
            if (session.GetProperty("state").GetString() == "Recording")
            {
                await ApiAsync($"/sessions/{session.GetProperty("id").GetString()}/stop", HttpMethod.Post);
            }
        }

        return (await ApiAsync("/sessions", HttpMethod.Post, new { name })).GetProperty("id").GetString()!;
    }

    public async Task<List<JsonElement>> FindingsAsync(string sessionId) =>
        [.. (await ApiAsync($"/sessions/{sessionId}/findings")).GetProperty("findings").EnumerateArray()];

    public async Task<List<JsonElement>> EventsAsync(string sessionId)
    {
        var all = new List<JsonElement>();
        long after = 0;
        while (true)
        {
            var page = await ApiAsync($"/sessions/{sessionId}/events?after={after}&max=5000");
            all.AddRange(page.GetProperty("events").EnumerateArray());
            after = page.GetProperty("cursor").GetInt64();
            if (!page.GetProperty("hasMore").GetBoolean())
            {
                return all;
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>The sample running in the Production environment.</summary>
public sealed class ProductionSampleApp : SampleApp
{
    protected override string EnvironmentName => "Production";
}

/// <summary>Helpers shared by tests.</summary>
public static class Waits
{
    public static async Task UntilAsync(Func<Task<bool>> condition, int timeoutMs = 20_000, string? message = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(message ?? "Condition not met in time.");
            }

            await Task.Delay(200);
        }
    }

    public static string Str(this JsonElement e, string property) =>
        e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
}
