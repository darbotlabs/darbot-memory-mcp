using System.Net;
using System.Text;
using System.Text.Json;
using Darbot.Memory.Mcp.Api.Authentication;
using Darbot.Memory.Mcp.Api.Mcp;
using Darbot.Memory.Mcp.Core.Configuration;
using Darbot.Memory.Mcp.Core.Graph;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Darbot.Memory.Mcp.Tests.Mcp;

/// <summary>In-process host that wires the MCP extension methods with mocked memory services.</summary>
public sealed class McpTestHost : IAsyncLifetime
{
    private WebApplication _app = null!;

    public Mock<IConversationService> Conversations { get; } = new();
    public Mock<IWorkspaceService> Workspaces { get; } = new();
    public Mock<IGraphMemoryService> Graph { get; } = new();
    public HttpClient Http { get; private set; } = null!;
    public string AuthMode { get; init; } = "None";
    public string? ApiKey { get; init; }
    public Dictionary<string, string?> Settings { get; } = new();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(Settings);

        builder.Services.Configure<DarbotConfiguration>(c =>
        {
            c.Auth.Mode = AuthMode;
            c.Auth.ApiKey = ApiKey ?? string.Empty;
        });
        builder.Services.AddAuthentication(ApiKeyAuthenticationOptions.DefaultScheme).AddApiKey();
        builder.Services.AddAuthorization(o => o.AddPolicy("DarbotMemoryWriter", p => p.RequireClaim("scope", "darbot.memory.writer")));
        builder.Services.AddScoped(_ => Conversations.Object);
        builder.Services.AddScoped(_ => Workspaces.Object);
        builder.Services.AddScoped(_ => Graph.Object);
        builder.Services.AddDarbotMcpServer(builder.Configuration);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapDarbotMcpServer();
        await _app.StartAsync();
        Http = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        Http.Dispose();
        await _app.DisposeAsync();
    }

    public async Task<(HttpResponseMessage Response, JsonElement Body)> RpcAsync(string method, object? parameters = null, int id = 1, string path = "/mcp", Action<HttpRequestMessage>? configure = null)
    {
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters ?? new { } });
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        configure?.Invoke(request);

        var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response, ParseBody(response, text));
    }

    private static JsonElement ParseBody(HttpResponseMessage response, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            var data = text.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].Trim()).LastOrDefault(l => l.Length > 0);
            return data is null ? default : JsonDocument.Parse(data).RootElement.Clone();
        }

        return text.TrimStart().StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default;
    }

    public async Task<JsonElement> CallToolAsync(string name, object arguments, int id = 1)
    {
        var (_, body) = await RpcAsync("tools/call", new { name, arguments }, id);
        return body.GetProperty("result");
    }

    public static string ToolText(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString()!;
}

public class McpStatelessTests : IClassFixture<McpTestHost>, IAsyncLifetime
{
    private readonly McpTestHost _host;

    public McpStatelessTests(McpTestHost host) => _host = host;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ToolsList_WorksWithoutASession_AndReturnsNoSessionId()
    {
        var (response, body) = await _host.RpcAsync("tools/list");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Mcp-Session-Id").Should().BeFalse();
        var names = body.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        names.Should().Contain(new[]
        {
            "memory_write_turn", "memory_search_conversations", "memory_list_conversations", "memory_get_conversation", "memory_get_turn",
            "workspace_capture", "workspace_list", "workspace_get", "workspace_restore", "workspace_delete",
            "graph_list", "graph_schemas", "graph_remember", "graph_relate", "graph_search", "graph_neighborhood", "graph_forget", "graph_import", "graph_export"
        });
    }

    [Fact]
    public async Task IndependentRequests_EachSucceedWithoutSharedState()
    {
        var first = await _host.RpcAsync("tools/list", id: 1);
        var second = await _host.RpcAsync("tools/list", id: 2);
        var third = await _host.RpcAsync("ping", id: 3);

        first.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        third.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Response.Headers.Contains("Mcp-Session-Id").Should().BeFalse();
        second.Response.Headers.Contains("Mcp-Session-Id").Should().BeFalse();
        second.Body.GetProperty("id").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Initialize_DoesNotIssueASessionAndReportsServerInfo()
    {
        var (response, body) = await _host.RpcAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "test", version = "1" }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Mcp-Session-Id").Should().BeFalse();
        var result = body.GetProperty("result");
        result.GetProperty("serverInfo").GetProperty("name").GetString().Should().Be("darbot-memory-mcp");
        result.GetProperty("instructions").GetString().Should().Contain("graph_");
        result.GetProperty("capabilities").TryGetProperty("tools", out _).Should().BeTrue();
        result.GetProperty("capabilities").TryGetProperty("resources", out _).Should().BeTrue();
        result.GetProperty("capabilities").TryGetProperty("prompts", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ToolAnnotations_MarkDestructiveAndReadOnlyToolsCorrectly()
    {
        var (_, body) = await _host.RpcAsync("tools/list");
        var tools = body.GetProperty("result").GetProperty("tools").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);

        bool Flag(string tool, string name) => tools[tool].GetProperty("annotations").TryGetProperty(name, out var v) && v.GetBoolean();

        Flag("graph_search", "readOnlyHint").Should().BeTrue();
        Flag("memory_get_turn", "readOnlyHint").Should().BeTrue();
        Flag("graph_forget", "destructiveHint").Should().BeTrue();
        Flag("workspace_delete", "destructiveHint").Should().BeTrue();
        Flag("workspace_restore", "destructiveHint").Should().BeTrue();
        Flag("graph_remember", "destructiveHint").Should().BeFalse();
        Flag("memory_write_turn", "destructiveHint").Should().BeFalse();
        Flag("graph_forget", "idempotentHint").Should().BeTrue();
        tools["graph_search"].GetProperty("title").GetString().Should().Be("Search graph");
        tools["graph_import"].GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo("schema", "payload");
        tools.Values.Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t.GetProperty("description").GetString()));
    }

    [Fact]
    public async Task ToolsCall_ExecutesAgainstMockedServices()
    {
        _host.Graph.Setup(g => g.RememberAsync("notes", "Bob likes chess", "Bob", "person", null, It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphNode { Id = "n1", Name = "Bob", Type = "person", Content = "Bob likes chess", Embedding = new[] { 0.1f } });

        var result = await _host.CallToolAsync("graph_remember", new { text = "Bob likes chess", graph = "notes", name = "Bob", type = "person" });

        (result.TryGetProperty("isError", out var isError) && isError.GetBoolean()).Should().BeFalse();
        var node = JsonDocument.Parse(McpTestHost.ToolText(result)).RootElement;
        node.GetProperty("id").GetString().Should().Be("n1");
        node.TryGetProperty("embedding", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ToolsCall_ConversationTools_WriteAndRead()
    {
        _host.Conversations.Setup(c => c.GetConversationAsync("c1", It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ConversationTurn { ConversationId = "c1", TurnNumber = 4, UtcTimestamp = DateTime.UtcNow, Prompt = "p", Model = "m", Response = "r" }
        });
        _host.Conversations.Setup(c => c.PersistTurnAsync(It.IsAny<ConversationTurn>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var write = await _host.CallToolAsync("memory_write_turn", new { conversationId = "c1", prompt = "hi", response = "hello", toolsUsed = new[] { "t" } });
        JsonDocument.Parse(McpTestHost.ToolText(write)).RootElement.GetProperty("turnNumber").GetInt32().Should().Be(5);
        _host.Conversations.Verify(c => c.PersistTurnAsync(It.Is<ConversationTurn>(t => t.TurnNumber == 5 && t.Prompt == "hi" && t.ToolsUsed.Contains("t")), It.IsAny<CancellationToken>()), Times.Once);

        var get = await _host.CallToolAsync("memory_get_conversation", new { conversationId = "c1" });
        JsonDocument.Parse(McpTestHost.ToolText(get)).RootElement.GetProperty("turns").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ToolsCall_NotFound_ReturnsToolErrorWithMessage()
    {
        _host.Conversations.Setup(c => c.GetConversationTurnAsync("c1", 9, It.IsAny<CancellationToken>())).ReturnsAsync((ConversationTurn?)null);

        var result = await _host.CallToolAsync("memory_get_turn", new { conversationId = "c1", turnNumber = 9 });

        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        McpTestHost.ToolText(result).Should().Contain("Turn 9 of conversation 'c1' was not found");
    }

    [Fact]
    public async Task ToolsCall_AdapterFailures_AreReportedToTheClient()
    {
        _host.Graph.Setup(g => g.ImportAsync("bogus", "{}", "default", true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Unknown schema 'bogus'."));

        var result = await _host.CallToolAsync("graph_import", new { schema = "bogus", payload = "{}" });

        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        McpTestHost.ToolText(result).Should().Contain("Unknown schema 'bogus'");
    }

    [Fact]
    public async Task ToolsCall_GraphSearchAndSchemas()
    {
        var adapter = new Mock<IGraphSchemaAdapter>();
        adapter.SetupGet(a => a.Name).Returns("obsidian");
        adapter.SetupGet(a => a.DisplayName).Returns("Obsidian vault");
        adapter.SetupGet(a => a.Description).Returns("Markdown notes");
        adapter.SetupGet(a => a.ContentType).Returns("application/json");
        adapter.SetupGet(a => a.CanImport).Returns(true);
        adapter.SetupGet(a => a.CanExport).Returns(true);
        _host.Graph.SetupGet(g => g.Schemas).Returns(new[] { adapter.Object });
        _host.Graph.Setup(g => g.SearchAsync("default", It.Is<GraphQuery>(q => q.Text == "chess" && q.Limit == 5), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new GraphNode { Id = "n1", Name = "Bob" } });

        var schemas = JsonDocument.Parse(McpTestHost.ToolText(await _host.CallToolAsync("graph_schemas", new { }))).RootElement;
        schemas.GetProperty("schemas")[0].GetProperty("name").GetString().Should().Be("obsidian");

        var search = JsonDocument.Parse(McpTestHost.ToolText(await _host.CallToolAsync("graph_search", new { query = "chess", limit = 5 }))).RootElement;
        search.GetProperty("count").GetInt32().Should().Be(1);
        search.GetProperty("nodes")[0].GetProperty("name").GetString().Should().Be("Bob");
    }

    [Fact]
    public async Task ToolsCall_WorkspaceCaptureFailure_IsAnError()
    {
        _host.Workspaces.Setup(w => w.CaptureWorkspaceAsync(It.IsAny<CaptureWorkspaceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CaptureWorkspaceResponse { Success = false, WorkspaceId = "", CapturedAt = DateTime.UtcNow, ComponentsCount = 0, Message = "disk full" });

        var result = await _host.CallToolAsync("workspace_capture", new { name = "w" });

        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        McpTestHost.ToolText(result).Should().Contain("disk full");
    }

    [Fact]
    public async Task BrowserHistoryTool_ToleratesMissingService()
    {
        var result = await _host.CallToolAsync("browser_history_search", new { domain = "example.com" });

        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        McpTestHost.ToolText(result).Should().Contain("Browser history is not enabled");
    }

    [Fact]
    public async Task Resources_ListTemplatesAndRead()
    {
        var adapter = new Mock<IGraphSchemaAdapter>();
        adapter.SetupGet(a => a.Name).Returns("kgforge");
        _host.Graph.SetupGet(g => g.Schemas).Returns(new[] { adapter.Object });
        _host.Graph.Setup(g => g.GetGraphAsync("team", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeGraph { Name = "team", Nodes = { new GraphNode { Id = "a", Name = "A" } } });
        _host.Conversations.Setup(c => c.GetConversationAsync("c9", It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ConversationTurn { ConversationId = "c9", TurnNumber = 1, UtcTimestamp = DateTime.UtcNow, Prompt = "p", Model = "m", Response = "r" }
        });

        var (_, list) = await _host.RpcAsync("resources/list");
        list.GetProperty("result").GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("uri").GetString()).Should().Contain("darbot://graph-schemas");

        var (_, templates) = await _host.RpcAsync("resources/templates/list");
        templates.GetProperty("result").GetProperty("resourceTemplates").EnumerateArray().Select(r => r.GetProperty("uriTemplate").GetString())
            .Should().Contain(new[] { "darbot://graphs/{name}", "darbot://conversations/{id}" });

        var (_, schemas) = await _host.RpcAsync("resources/read", new { uri = "darbot://graph-schemas" });
        schemas.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString().Should().Contain("kgforge");

        var (_, graph) = await _host.RpcAsync("resources/read", new { uri = "darbot://graphs/team" });
        graph.GetProperty("result").GetProperty("contents")[0].GetProperty("mimeType").GetString().Should().Be("application/json");
        graph.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString().Should().Contain("\"name\":\"team\"");

        var (_, conversation) = await _host.RpcAsync("resources/read", new { uri = "darbot://conversations/c9" });
        conversation.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString().Should().Contain("c9");
    }

    [Fact]
    public async Task Prompts_ListAndGet()
    {
        _host.Graph.Setup(g => g.SearchAsync("default", It.IsAny<GraphQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new GraphNode { Id = "n1", Name = "Bob", Type = "person", Observations = { "plays chess" } } });
        _host.Conversations.Setup(c => c.SearchConversationsAsync(It.IsAny<ConversationSearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationSearchResponse { Results = Array.Empty<ConversationTurn>(), TotalCount = 0, HasMore = false, Skip = 0, Take = 5 });
        var adapter = new Mock<IGraphSchemaAdapter>();
        adapter.SetupGet(a => a.Name).Returns("mem0");
        adapter.SetupGet(a => a.DisplayName).Returns("mem0");
        adapter.SetupGet(a => a.Description).Returns("mem0 memories");
        adapter.SetupGet(a => a.CanImport).Returns(true);
        _host.Graph.SetupGet(g => g.Schemas).Returns(new[] { adapter.Object });

        var (_, list) = await _host.RpcAsync("prompts/list");
        list.GetProperty("result").GetProperty("prompts").EnumerateArray().Select(p => p.GetProperty("name").GetString())
            .Should().BeEquivalentTo("recall_context", "summarize_conversation", "import_from_schema");

        var (_, recall) = await _host.RpcAsync("prompts/get", new { name = "recall_context", arguments = new { topic = "chess" } });
        recall.GetProperty("result").GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString()
            .Should().Contain("Bob").And.Contain("plays chess");

        var (_, import) = await _host.RpcAsync("prompts/get", new { name = "import_from_schema", arguments = new { schema = "mem0", source = "pasted export" } });
        import.GetProperty("result").GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString()
            .Should().Contain("graph_import").And.Contain("pasted export");
    }
}

public class McpAuthorizationTests
{
    [Fact]
    public async Task ApiKeyMode_RejectsMissingKeyAndAcceptsValidKey()
    {
        var host = new McpTestHost { AuthMode = "APIKey", ApiKey = "s3cret" };
        await host.InitializeAsync();
        try
        {
            (await host.RpcAsync("tools/list")).Response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await host.RpcAsync("tools/list", configure: r => r.Headers.Add("X-API-Key", "wrong"))).Response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var ok = await host.RpcAsync("tools/list", configure: r => r.Headers.Add("X-API-Key", "s3cret"));
            ok.Response.StatusCode.Should().Be(HttpStatusCode.OK);
            ok.Body.GetProperty("result").GetProperty("tools").GetArrayLength().Should().BeGreaterThan(10);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task AuthModeNone_AllowsAnonymousAccess()
    {
        var host = new McpTestHost();
        await host.InitializeAsync();
        try
        {
            (await host.RpcAsync("tools/list")).Response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConfiguredPathAndOptionalAuthorization_AreHonored()
    {
        var host = new McpTestHost { AuthMode = "APIKey", ApiKey = "k" };
        host.Settings["Darbot:Mcp:Path"] = "/custom/mcp";
        host.Settings["Darbot:Mcp:RequireAuthorization"] = "false";
        host.Settings["Darbot:Mcp:MaxConcurrentClients"] = "10";
        host.Settings["Darbot:Mcp:ConnectionTimeout"] = "00:01:00";
        host.Settings["Darbot:Mcp:KeepAliveInterval"] = "00:00:30";
        await host.InitializeAsync();
        try
        {
            (await host.RpcAsync("tools/list", path: "/mcp")).Response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await host.RpcAsync("tools/list", path: "/custom/mcp")).Response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }
}


