using System.Text.Json;
using Darbot.Memory.Mcp.Core.Acp;
using Darbot.Memory.Mcp.Core.Graph;
using Darbot.Memory.Mcp.Core.Models;
using FluentAssertions;
using Moq;
using Xunit;

namespace Darbot.Memory.Mcp.Tests.Acp;

public class AcpLifecycleTests
{
    [Fact]
    public async Task Initialize_NegotiatesVersionAndAdvertisesCapabilities()
    {
        await using var h = new AcpHarness();
        var result = await h.InitializeAsync();

        result.GetProperty("protocolVersion").GetInt32().Should().Be(1);
        var caps = result.GetProperty("agentCapabilities");
        caps.GetProperty("loadSession").GetBoolean().Should().BeTrue();
        caps.GetProperty("promptCapabilities").GetProperty("image").GetBoolean().Should().BeFalse();
        caps.GetProperty("promptCapabilities").GetProperty("audio").GetBoolean().Should().BeFalse();
        caps.GetProperty("promptCapabilities").GetProperty("embeddedContext").GetBoolean().Should().BeTrue();
        caps.GetProperty("mcpCapabilities").GetProperty("http").GetBoolean().Should().BeTrue();
        caps.GetProperty("mcpCapabilities").GetProperty("sse").GetBoolean().Should().BeFalse();
        caps.GetProperty("sessionCapabilities").TryGetProperty("list", out _).Should().BeTrue();
        result.GetProperty("agentInfo").GetProperty("name").GetString().Should().Be("darbot-memory");
        result.GetProperty("authMethods").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public async Task Initialize_RespondsWithLatestSupportedVersion(int requested)
    {
        await using var h = new AcpHarness();
        var result = await h.InitializeAsync(version: requested);
        result.GetProperty("protocolVersion").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Initialize_WithoutProtocolVersion_IsInvalidParams()
    {
        await using var h = new AcpHarness();
        var act = () => h.Client.CallAsync("initialize", new { clientCapabilities = new { } });
        (await act.Should().ThrowAsync<JsonRpcRemoteException>()).Which.Code.Should().Be(JsonRpcErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task SessionMethods_BeforeInitialize_AreRejected()
    {
        await using var h = new AcpHarness();
        var act = () => h.Client.CallAsync("session/new", new { cwd = "/x", mcpServers = Array.Empty<object>() });
        (await act.Should().ThrowAsync<JsonRpcRemoteException>()).Which.Code.Should().Be(JsonRpcErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task NewSession_ReturnsModesConfigOptionsAndAdvertisesCommands()
    {
        await using var h = new AcpHarness();
        await h.InitializeAsync();

        var result = await h.Client.CallAsync("session/new", new
        {
            cwd = "/work/project",
            mcpServers = new object[]
            {
                new { name = "fs", command = "npx", args = new[] { "fs" }, env = Array.Empty<object>() },
                new { type = "http", name = "remote", url = "https://example.test/mcp", headers = Array.Empty<object>() }
            },
            _meta = new { trace = "abc" }
        });

        var id = result.GetProperty("sessionId").GetString()!;
        id.Should().StartWith("acp-");
        result.GetProperty("modes").GetProperty("currentModeId").GetString().Should().Be("recall");
        result.GetProperty("modes").GetProperty("availableModes").EnumerateArray().Select(m => m.GetProperty("id").GetString())
            .Should().BeEquivalentTo("recall", "capture", "graph");
        result.GetProperty("configOptions").EnumerateArray().Select(o => o.GetProperty("id").GetString()).Should().Contain(new[] { "mode", "graph" });
        result.GetProperty("_meta").GetProperty("trace").GetString().Should().Be("abc");

        await h.Client.WaitUntilAsync(() => h.Client.UpdateKinds.Contains("available_commands_update"));
        var commands = h.Client.UpdatesOf("available_commands_update")[0].GetProperty("availableCommands").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).ToList();
        commands.Should().Contain(new[] { "remember", "recall", "forget", "relate", "graph", "schemas", "import", "export", "workspace", "help" });

        h.Sessions.TryGet(id, out var session).Should().BeTrue();
        session.McpServers.Should().HaveCount(2);
        session.McpServers[0].Should().BeOfType<McpServerStdio>();
        session.McpServers[1].Should().BeOfType<McpServerHttp>();
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("")]
    public async Task NewSession_RequiresAbsoluteCwd(string cwd)
    {
        await using var h = new AcpHarness();
        await h.InitializeAsync();
        var act = () => h.Client.CallAsync("session/new", new { cwd, mcpServers = Array.Empty<object>() });
        (await act.Should().ThrowAsync<JsonRpcRemoteException>()).Which.Code.Should().Be(JsonRpcErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task NewSession_AcceptsWindowsAbsolutePath()
    {
        await using var h = new AcpHarness();
        var id = await h.NewSessionAsync(@"C:\work\repo");
        h.Sessions.TryGet(id, out var session).Should().BeTrue();
        session.Cwd.Should().Be(@"C:\work\repo");
    }

    [Fact]
    public async Task SetMode_ValidatesAndNotifies()
    {
        await using var h = new AcpHarness();
        var id = await h.NewSessionAsync();

        await h.Client.CallAsync("session/set_mode", new { sessionId = id, modeId = "graph" });
        await h.Client.WaitUntilAsync(() => h.Client.UpdateKinds.Contains("current_mode_update"));
        h.Client.UpdatesOf("current_mode_update")[0].GetProperty("currentModeId").GetString().Should().Be("graph");

        var act = () => h.Client.CallAsync("session/set_mode", new { sessionId = id, modeId = "nope" });
        (await act.Should().ThrowAsync<JsonRpcRemoteException>()).Which.Code.Should().Be(JsonRpcErrorCodes.InvalidParams);

        var unknown = () => h.Client.CallAsync("session/set_mode", new { sessionId = "acp-missing", modeId = "graph" });
        (await unknown.Should().ThrowAsync<JsonRpcRemoteException>()).Which.Code.Should().Be(JsonRpcErrorCodes.ResourceNotFound);
    }

    [Fact]
    public async Task SetConfigOption_ChangesGraphAndMode()
    {
        await using var h = new AcpHarness();
        var id = await h.NewSessionAsync();

        var result = await h.Client.CallAsync("session/set_config_option", new { sessionId = id, configId = "graph", value = "projects" });
        result.GetProperty("configOptions").EnumerateArray()
            .Single(o => o.GetProperty("id").GetString() == "graph").GetProperty("currentValue").GetString().Should().Be("projects");

        await h.Client.CallAsync("session/set_config_option", new { sessionId = id, configId = "mode", value = "capture" });
        h.Sessions.TryGet(id, out var session).Should().BeTrue();
        session.Graph.Should().Be("projects");
        session.ModeId.Should().Be("capture");

        var bad = () => h.Client.CallAsync("session/set_config_option", new { sessionId = id, configId = "nope", value = "x" });
        (await bad.Should().ThrowAsync<JsonRpcRemoteException>()).Which.Code.Should().Be(JsonRpcErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task BooleanConfigOption_IsOnlyOfferedWhenClientSupportsIt()
    {
        await using var h = new AcpHarness();
        await h.InitializeAsync(new { session = new { configOptions = new { boolean = new { } } } });
        var result = await h.Client.CallAsync("session/new", new { cwd = "/w", mcpServers = Array.Empty<object>() });
        result.GetProperty("configOptions").EnumerateArray().Select(o => o.GetProperty("id").GetString()).Should().Contain("persist_turns");

        await using var plain = new AcpHarness();
        await plain.InitializeAsync();
        var plainResult = await plain.Client.CallAsync("session/new", new { cwd = "/w", mcpServers = Array.Empty<object>() });
        plainResult.GetProperty("configOptions").EnumerateArray().Select(o => o.GetProperty("id").GetString()).Should().NotContain("persist_turns");
    }

    [Fact]
    public async Task ListDeleteCloseAndResume_ManageSessions()
    {
        await using var h = new AcpHarness();
        var first = await h.NewSessionAsync("/a");
        var second = (await h.Client.CallAsync("session/new", new { cwd = "/b", mcpServers = Array.Empty<object>() })).GetProperty("sessionId").GetString()!;

        var all = await h.Client.CallAsync("session/list", new { });
        all.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("sessionId").GetString()).Should().BeEquivalentTo(first, second);

        var filtered = await h.Client.CallAsync("session/list", new { cwd = "/b" });
        filtered.GetProperty("sessions").GetArrayLength().Should().Be(1);

        await h.Client.CallAsync("session/close", new { sessionId = first });
        var resumed = await h.Client.CallAsync("session/resume", new { sessionId = second, cwd = "/b" });
        resumed.GetProperty("modes").GetProperty("currentModeId").GetString().Should().Be("recall");

        await h.Client.CallAsync("session/delete", new { sessionId = second });
        var afterDelete = () => h.Client.CallAsync("session/resume", new { sessionId = second, cwd = "/b" });
        (await afterDelete.Should().ThrowAsync<JsonRpcRemoteException>()).Which.Code.Should().Be(JsonRpcErrorCodes.ResourceNotFound);
    }

    [Fact]
    public async Task AgentAuth_RequiresAuthenticateBeforeSessions()
    {
        await using var h = new AcpHarness(o => { o.RequireAgentAuth = true; o.ApiKey = "s3cret"; });
        var init = await h.InitializeAsync();
        init.GetProperty("authMethods").EnumerateArray().Single().GetProperty("id").GetString().Should().Be(AcpAgent.ApiKeyAuthMethodId);

        var early = () => h.Client.CallAsync("session/new", new { cwd = "/w", mcpServers = Array.Empty<object>() });
        (await early.Should().ThrowAsync<JsonRpcRemoteException>()).Which.Code.Should().Be(JsonRpcErrorCodes.AuthRequired);

        var wrong = () => h.Client.CallAsync("authenticate", new { methodId = AcpAgent.ApiKeyAuthMethodId, _meta = new { apiKey = "nope" } });
        (await wrong.Should().ThrowAsync<JsonRpcRemoteException>()).Which.Code.Should().Be(JsonRpcErrorCodes.AuthRequired);

        await h.Client.CallAsync("authenticate", new { methodId = AcpAgent.ApiKeyAuthMethodId, _meta = new { apiKey = "s3cret" } });
        var ok = await h.Client.CallAsync("session/new", new { cwd = "/w", mcpServers = Array.Empty<object>() });
        ok.GetProperty("sessionId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AgentAuth_PreAuthenticatedConnectionSkipsTheHandshake()
    {
        await using var h = new AcpHarness(o => { o.RequireAgentAuth = true; o.ApiKey = "s3cret"; }, preAuthenticated: true);
        await h.InitializeAsync();
        var ok = await h.Client.CallAsync("session/new", new { cwd = "/w", mcpServers = Array.Empty<object>() });
        ok.GetProperty("sessionId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SessionLoad_ReplaysHistoryBeforeResponding()
    {
        var sessions = new AcpSessionStore();
        string id;
        await using (var first = new AcpHarness(sessions: sessions))
        {
            id = await first.NewSessionAsync();
            await first.PromptAsync(id, "/remember Bob owns the blue bike");
        }

        await using var h = new AcpHarness(sessions: sessions);
        await h.InitializeAsync();
        var result = await h.Client.CallAsync("session/load", new { sessionId = id, cwd = "/work/project", mcpServers = Array.Empty<object>() });

        result.GetProperty("modes").GetProperty("currentModeId").GetString().Should().Be("recall");
        var kinds = h.Client.UpdateKinds.Take(2).ToList();
        kinds.Should().Equal("user_message_chunk", "agent_message_chunk");
        h.Client.Updates[0].GetProperty("content").GetProperty("text").GetString().Should().Be("/remember Bob owns the blue bike");
        h.Client.Updates[1].GetProperty("content").GetProperty("text").GetString().Should().Contain("Remembered");
    }

    [Fact]
    public async Task SessionLoad_HydratesFromPersistedConversation()
    {
        await using var h = new AcpHarness();
        h.Conversations.Setup(c => c.GetConversationAsync("acp-persisted", It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new ConversationTurn { ConversationId = "acp-persisted", TurnNumber = 1, UtcTimestamp = DateTime.UtcNow, Prompt = "hello", Model = "darbot-acp", Response = "hi there" },
            new ConversationTurn { ConversationId = "acp-persisted", TurnNumber = 2, UtcTimestamp = DateTime.UtcNow, Prompt = "again", Model = "darbot-acp", Response = "welcome back" }
        });
        await h.InitializeAsync();

        await h.Client.CallAsync("session/load", new { sessionId = "acp-persisted", cwd = "/w", mcpServers = Array.Empty<object>() });

        h.Client.UpdateKinds.Take(4).Should().Equal("user_message_chunk", "agent_message_chunk", "user_message_chunk", "agent_message_chunk");
        h.Client.Updates[3].GetProperty("content").GetProperty("text").GetString().Should().Be("welcome back");
        h.Sessions.TryGet("acp-persisted", out var session).Should().BeTrue();
        session.TurnCount.Should().Be(2);
    }

    [Fact]
    public async Task SessionLoad_UnknownSession_IsResourceNotFound()
    {
        await using var h = new AcpHarness();
        await h.InitializeAsync();
        var act = () => h.Client.CallAsync("session/load", new { sessionId = "acp-nope", cwd = "/w", mcpServers = Array.Empty<object>() });
        (await act.Should().ThrowAsync<JsonRpcRemoteException>()).Which.Code.Should().Be(JsonRpcErrorCodes.ResourceNotFound);
    }

    [Fact]
    public async Task SessionList_IncludesPersistedAcpConversations()
    {
        await using var h = new AcpHarness();
        h.Conversations.Setup(c => c.ListConversationsAsync(It.IsAny<ConversationListRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationListResponse
            {
                Conversations = new[]
                {
                    new ConversationSummary { ConversationId = "acp-old", TurnCount = 2, FirstTurnTimestamp = DateTime.UtcNow.AddDays(-1), LastTurnTimestamp = DateTime.UtcNow, ModelsUsed = new[] { "darbot-acp" }, ToolsUsed = Array.Empty<string>(), LastPrompt = "what about bikes" },
                    new ConversationSummary { ConversationId = "other-conv", TurnCount = 1, FirstTurnTimestamp = DateTime.UtcNow, LastTurnTimestamp = DateTime.UtcNow, ModelsUsed = Array.Empty<string>(), ToolsUsed = Array.Empty<string>() }
                },
                TotalCount = 2, HasMore = false, Skip = 0, Take = 500
            });
        await h.InitializeAsync();

        var result = await h.Client.CallAsync("session/list", new { });
        var sessions = result.GetProperty("sessions").EnumerateArray().ToList();
        sessions.Should().ContainSingle();
        sessions[0].GetProperty("sessionId").GetString().Should().Be("acp-old");
        sessions[0].GetProperty("title").GetString().Should().Be("what about bikes");
    }

    [Fact]
    public async Task ExtensionMethods_AreHandledAndUnknownOnesAreRejected()
    {
        await using var h = new AcpHarness();
        await h.InitializeAsync();
        h.Graph.Setup(g => g.ImportAsync("mcp-memory", "{\"x\":1}", "kg", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeGraph
            {
                Name = "kg",
                Nodes = { new GraphNode { Id = "a", Name = "A" }, new GraphNode { Id = "b", Name = "B" } },
                Edges = { new GraphEdge { Id = "e", SourceId = "a", TargetId = "b" } }
            });

        var result = await h.Client.CallAsync("_darbot/graph/import", new { schema = "mcp-memory", payload = "{\"x\":1}", graph = "kg" });
        result.GetProperty("graph").GetString().Should().Be("kg");
        result.GetProperty("nodes").GetInt32().Should().Be(2);
        result.GetProperty("edges").GetInt32().Should().Be(1);

        var missing = () => h.Client.CallAsync("_darbot/graph/import", new { payload = "{}" });
        (await missing.Should().ThrowAsync<JsonRpcRemoteException>()).Which.Code.Should().Be(JsonRpcErrorCodes.InvalidParams);

        var unknown = () => h.Client.CallAsync("_acme/unknown", new { });
        (await unknown.Should().ThrowAsync<JsonRpcRemoteException>()).Which.Code.Should().Be(JsonRpcErrorCodes.MethodNotFound);

        await h.Client.NotifyAsync("_acme/ignored", new { });
        (await h.Client.CallAsync("_darbot/graph/list")).GetProperty("graphs").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task MetaIsPassedThroughOnPromptResponses()
    {
        await using var h = new AcpHarness();
        var id = await h.NewSessionAsync();
        var result = await h.Client.CallAsync("session/prompt", new { sessionId = id, prompt = new object[] { new { type = "text", text = "/help" } }, _meta = new { requestId = "r-1" } });
        result.GetProperty("_meta").GetProperty("requestId").GetString().Should().Be("r-1");
    }
}
