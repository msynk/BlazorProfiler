using System.Text.Json;
using BlazorProfiler.Api;
using BlazorProfiler.Cli;
using BlazorProfiler.Events;
using BlazorProfiler.Export;

namespace BlazorProfiler.Core.Tests;

public sealed class McpTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bp-mcp-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static (ProfilerHost Host, ProfilerApi Api, string SessionId) CreateWithData()
    {
        var (host, session, _) = TestHost.WithSession(o => o.Instrumentation.TrackRenderOutput = true);
        host.Emit(Ev.Handler("App.Pages.Orders", "Load", 0, 80, operation: 7));
        for (var i = 0; i < 8; i++)
        {
            host.Emit(Ev.Db("SELECT * FROM Lines WHERE OrderId = @p0", 1 + i, 2, operation: 7, arguments: i, origin: "App.Pages.Orders"));
        }

        host.Emit(Ev.Render("App.Pages.Orders", 60, 3, instance: 1, operation: 7));
        host.Emit(new ComponentRenderEvent { ComponentType = "App.Shared.Row", ComponentInstanceId = 2, Timestamp = Ev.T(65), Duration = Ev.D(1), Reason = RenderReason.Parameters, OperationId = 7, Source = InstrumentationSource.ComponentInstrumentation });
        return (host, new ProfilerApi(host, "test"), session.Id);
    }

    private static async Task<JsonElement> Call(ProfilerMcpServer server, string json, ProfilerApiAccess? access = null)
    {
        var response = await server.HandleAsync(json, access ?? ProfilerApiAccess.Full);
        Assert.NotNull(response);
        return JsonDocument.Parse(response).RootElement.Clone();
    }

    private static async Task<(string Text, bool IsError)> Tool(ProfilerMcpServer server, string name, object? arguments = null, ProfilerApiAccess? access = null)
    {
        var request = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new Dictionary<string, object?> { ["name"] = name, ["arguments"] = arguments ?? new Dictionary<string, object?>() },
        });
        var root = await Call(server, request, access);
        if (root.TryGetProperty("error", out var error))
        {
            return (error.GetProperty("message").GetString()!, true);
        }

        var result = root.GetProperty("result");
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task BridgeServesTheReadOnlyToolsForInPageAgents()
    {
        var (host, api, sessionId) = CreateWithData();
        using var owner = host;
        async Task<JsonElement> Bridge(string message, ProfilerApiAccess access)
        {
            var response = await api.HandleAsync(new ProfilerApiRequest { Op = "mcp", McpMessage = message }, access);
            Assert.True(response.Ok, response.Error);
            return JsonDocument.Parse(response.Mcp!).RootElement.Clone();
        }

        // Control tools are never listed through the bridge, even with full access: in-page agents only read.
        var tools = (await Bridge("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", ProfilerApiAccess.Full)).GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("list_findings", tools);
        Assert.DoesNotContain("control_session", tools);
        Assert.DoesNotContain("run_command", tools);

        var call = await Bridge(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "list_findings", arguments = new { session = sessionId, minSeverity = "Low" } } }), ProfilerApiAccess.Full);
        Assert.False(call.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("BP8001", call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal);

        var refused = await Bridge("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"control_session","arguments":{}}}""", ProfilerApiAccess.Full);
        Assert.True(refused.TryGetProperty("error", out _) || refused.GetProperty("result").GetProperty("isError").GetBoolean());

        // Notifications have no answer.
        Assert.Null((await api.HandleAsync(new ProfilerApiRequest { Op = "mcp", McpMessage = """{"jsonrpc":"2.0","method":"notifications/initialized"}""" }, ProfilerApiAccess.Full)).Mcp);
    }

    [Fact]
    public async Task InitializeNegotiatesTheProtocolVersion()
    {
        var (host, api, _) = CreateWithData();
        using var owner = host;
        var server = new ProfilerMcpServer(api);

        var root = await Call(server, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""");
        var result = root.GetProperty("result");
        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("blazor-profiler", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));

        var unknown = await Call(server, """{"jsonrpc":"2.0","id":"a","method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");
        Assert.Equal(ProfilerMcpServer.LatestProtocolVersion, unknown.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal("a", unknown.GetProperty("id").GetString());
    }

    [Fact]
    public async Task NotificationsGetNoResponseAndErrorsUseJsonRpcCodes()
    {
        var (host, api, _) = CreateWithData();
        using var owner = host;
        var server = new ProfilerMcpServer(api);

        Assert.Null(await server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", ProfilerApiAccess.Full));
        Assert.Equal(-32700, (await Call(server, "{not json")).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32601, (await Call(server, """{"jsonrpc":"2.0","id":2,"method":"sampling/createMessage"}""")).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32600, (await Call(server, "[]")).GetProperty("error").GetProperty("code").GetInt32());
        Assert.True((await Call(server, """{"jsonrpc":"2.0","id":3,"method":"ping"}""")).TryGetProperty("result", out _));
    }

    [Fact]
    public async Task ToolListIsReadOnlyUnlessControlIsAllowed()
    {
        var (host, api, _) = CreateWithData();
        using var owner = host;

        static async Task<List<string>> Names(ProfilerMcpServer s, ProfilerApiAccess access)
        {
            var root = await Call(s, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", access);
            return [.. root.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!)];
        }

        var readOnly = await Names(new ProfilerMcpServer(api), ProfilerApiAccess.Full);
        Assert.Contains("get_overview", readOnly);
        Assert.Contains("explain_finding", readOnly);
        Assert.DoesNotContain("run_command", readOnly);
        Assert.DoesNotContain("control_session", readOnly);

        var control = new ProfilerMcpServer(api, new ProfilerMcpOptions { AllowControl = true });
        Assert.Contains("run_command", await Names(control, ProfilerApiAccess.Full));

        // Callers without control access never see control tools, whatever the server allows.
        Assert.DoesNotContain("run_command", await Names(control, new ProfilerApiAccess { CanViewAllCircuits = true, CanControl = false }));
        Assert.True((await Tool(new ProfilerMcpServer(api), "run_command", new { name = "check-leaks" })).IsError);
    }

    [Fact]
    public async Task ReadToolsDescribeTheSession()
    {
        var (host, api, sessionId) = CreateWithData();
        using var owner = host;
        var server = new ProfilerMcpServer(api);

        var (sessions, _) = await Tool(server, "list_sessions");
        Assert.Contains(sessionId, sessions, StringComparison.Ordinal);

        var (overview, _) = await Tool(server, "get_overview");
        Assert.Contains("Database commands: 8", overview, StringComparison.Ordinal);
        Assert.Contains("BP8001", overview, StringComparison.Ordinal);

        var (findings, _) = await Tool(server, "list_findings", new { session = sessionId, minSeverity = "Low" });
        Assert.Contains("BP8001:", findings, StringComparison.Ordinal);
        var id = findings.Split('\n').First(l => l.Contains("BP8001:", StringComparison.Ordinal)).Split('|', StringSplitOptions.TrimEntries)[1].Replace("\\|", "|", StringComparison.Ordinal);

        var (explanation, isError) = await Tool(server, "explain_finding", new { id });
        Assert.False(isError, explanation);
        Assert.Contains("**Suggested fix:**", explanation, StringComparison.Ordinal);
        Assert.Contains("Related interactions (get_interaction): 7", explanation, StringComparison.Ordinal);

        var (interactions, _) = await Tool(server, "list_interactions");
        Assert.Contains("| 7 | interaction | onclick → Orders.Load |", interactions, StringComparison.Ordinal);

        var (interaction, _) = await Tool(server, "get_interaction", new { operationId = 7 });
        Assert.Contains("SQL SELECT * FROM Lines", interaction, StringComparison.Ordinal);
        Assert.Contains("render Row #2 (Parameters)", interaction, StringComparison.Ordinal);

        var (component, _) = await Tool(server, "get_component", new { component = "Row" });
        Assert.Contains("# App.Shared.Row", component, StringComparison.Ordinal);
        Assert.Contains("Parameters: 1", component, StringComparison.Ordinal);

        var (components, _) = await Tool(server, "list_components", new { limit = 5 });
        Assert.Contains("App.Pages.Orders", components, StringComparison.Ordinal);

        var (report, _) = await Tool(server, "get_report");
        Assert.StartsWith("# Blazor Profiler report", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidArgumentsAreReportedAsToolErrors()
    {
        var (host, api, _) = CreateWithData();
        using var owner = host;
        var server = new ProfilerMcpServer(api);

        Assert.True((await Tool(server, "explain_finding", new { id = "BP9999:nothing" })).IsError);
        Assert.True((await Tool(server, "get_component", new { component = "Missing" })).IsError);
        Assert.True((await Tool(server, "list_findings", new { minSeverity = "Huge" })).IsError);
        Assert.True((await Tool(server, "get_interaction", new { operationId = "x" })).IsError);
        Assert.True((await Tool(server, "get_overview", new { session = "nope" })).IsError);
        Assert.True((await Tool(server, "no_such_tool")).IsError);
    }

    [Fact]
    public async Task SessionsOfOtherCircuitsAreNotVisibleThroughMcp()
    {
        var (host, api, sessionId) = CreateWithData();
        using var owner = host;
        var server = new ProfilerMcpServer(api);
        var restricted = new ProfilerApiAccess { CanViewAllCircuits = false, OwnedCircuits = new HashSet<string>(StringComparer.Ordinal) { "mine" } };

        var (sessions, _) = await Tool(server, "list_sessions", access: restricted);
        Assert.DoesNotContain(sessionId, sessions, StringComparison.Ordinal);
        Assert.True((await Tool(server, "get_overview", new { session = sessionId }, restricted)).IsError);
    }

    [Fact]
    public async Task ControlToolsStartSessionsAndRunCommandsWhenAllowed()
    {
        var (host, api, _) = CreateWithData();
        using var owner = host;
        var server = new ProfilerMcpServer(api, new ProfilerMcpOptions { AllowControl = true });

        var (started, isError) = await Tool(server, "control_session", new { action = "start", name = "agent" });
        Assert.False(isError, started);
        Assert.Contains("is Recording", started, StringComparison.Ordinal);
        Assert.True((await Tool(server, "control_session", new { action = "explode" })).IsError);
        Assert.True((await Tool(server, "run_command", new { name = "missing" })).IsError);
    }

    [Fact]
    public async Task CliServesTracesOverStdio()
    {
        var (host, _, sessionId) = CreateWithData();
        string path;
        using (host)
        {
            var session = host.GetSession(sessionId)!;
            var document = TraceDocumentBuilder.Create(session, host.Options, null, null, "test");
            path = Path.Combine(_directory, "orders.bptrace");
            await using var stream = File.Create(path);
            await new CompressedTraceExporter().ExportAsync(document, stream);
        }

        var input = new StringReader(string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            string.Empty,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_findings","arguments":{}}}""") + "\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await TraceCli.RunAsync(["mcp", path], input, output, error);

        Assert.Equal(TraceCli.Success, code);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(1, JsonDocument.Parse(lines[0]).RootElement.GetProperty("id").GetInt32());
        Assert.Contains("BP8001", JsonDocument.Parse(lines[1]).RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("1 trace(s) loaded", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(TraceCli.UsageError, await TraceCli.RunAsync(["mcp"], TextReader.Null, TextWriter.Null, TextWriter.Null));
    }
}
