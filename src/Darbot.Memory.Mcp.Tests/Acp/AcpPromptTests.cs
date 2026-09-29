using System.Text.Json;
using Darbot.Memory.Mcp.Core.Acp;
using Darbot.Memory.Mcp.Core.Graph;
using Darbot.Memory.Mcp.Core.Models;
using FluentAssertions;
using Moq;
using Xunit;

namespace Darbot.Memory.Mcp.Tests.Acp;

public class AcpPromptTests
{
    private static string Status(JsonElement update) =>
        update.TryGetProperty("status", out var s) ? s.GetString()! : string.Empty;

    [Fact]
    public async Task Remember_StreamsToolCallLifecycleThenMessage()
    {
        await using var h = new AcpHarness();
        var id = await h.NewSessionAsync();

        var response = await h.PromptAsync(id, "/remember Alice prefers green tea");

        response.GetProperty("stopReason").GetString().Should().Be("end_turn");
        h.Client.UpdateKinds.Where(k => k != "session_info_update").Should()
            .Equal("tool_call", "tool_call_update", "tool_call_update", "agent_message_chunk");

        var start = h.Client.UpdatesOf("tool_call")[0];
        start.GetProperty("kind").GetString().Should().Be("edit");
        Status(start).Should().Be("pending");
        var progress = h.Client.UpdatesOf("tool_call_update");
        Status(progress[0]).Should().Be("in_progress");
        Status(progress[1]).Should().Be("completed");
        progress[1].GetProperty("toolCallId").GetString().Should().Be(start.GetProperty("toolCallId").GetString());
        h.Client.AgentText().Should().Contain("node-1");

        h.Graph.Verify(g => g.RememberAsync("default", "Alice prefers green tea", null, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Prompt_PersistsTurnAsConversationAndSetsTitle()
    {
        await using var h = new AcpHarness();
        var id = await h.NewSessionAsync();

        await h.PromptAsync(id, "/remember something worth keeping");

        h.Conversations.Verify(c => c.PersistTurnAsync(
            It.Is<ConversationTurn>(t => t.ConversationId == id && t.TurnNumber == 1 && t.Prompt == "/remember something worth keeping"
                                         && t.Model == "darbot-acp" && t.Response.Contains("Remembered") && t.ToolsUsed.Contains("graph.remember")),
            It.IsAny<CancellationToken>()), Times.Once);
        h.Client.UpdatesOf("session_info_update").Should().ContainSingle()
            .Which.GetProperty("title").GetString().Should().Be("/remember something worth keeping");
    }

    [Fact]
    public async Task Prompt_DoesNotPersistWhenDisabled()
    {
        await using var h = new AcpHarness(o => o.PersistSessionsAsConversations = false);
        var id = await h.NewSessionAsync();
        await h.PromptAsync(id, "/help");
        h.Conversations.Verify(c => c.PersistTurnAsync(It.IsAny<ConversationTurn>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PlainText_RecallsGraphNodesAndConversationsWithPlan()
    {
        await using var h = new AcpHarness();
        h.Graph.Setup(g => g.SearchAsync("default", It.Is<GraphQuery>(q => q.Text == "bike"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new GraphNode { Id = "n-bike", Name = "Blue bike", Type = "memory", Observations = { "Owned by Bob" } } });
        h.Conversations.Setup(c => c.SearchConversationsAsync(It.Is<ConversationSearchRequest>(r => r.SearchText == "bike"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationSearchResponse
            {
                Results = new[] { new ConversationTurn { ConversationId = "conv-7", TurnNumber = 3, UtcTimestamp = DateTime.UtcNow, Prompt = "Who has the bike?", Model = "m", Response = "Bob does." } },
                TotalCount = 1, HasMore = false, Skip = 0, Take = 5
            });
        var id = await h.NewSessionAsync();

        var response = await h.PromptAsync(id, "bike");

        response.GetProperty("stopReason").GetString().Should().Be("end_turn");
        var plans = h.Client.UpdatesOf("plan");
        plans.Should().NotBeEmpty();
        plans[0].GetProperty("entries").GetArrayLength().Should().Be(3);
        plans[^1].GetProperty("entries").EnumerateArray().Should().OnlyContain(e => e.GetProperty("status").GetString() == "completed");
        h.Client.UpdatesOf("tool_call").Select(u => u.GetProperty("kind").GetString()).Should().Equal("search", "search");
        var text = h.Client.AgentText();
        text.Should().Contain("Blue bike").And.Contain("Owned by Bob").And.Contain("conv-7");
    }

    [Fact]
    public async Task PlainText_FallsBackToKeywordSearchAndAdmitsWhenNothingIsStored()
    {
        await using var h = new AcpHarness();
        var id = await h.NewSessionAsync();

        await h.PromptAsync(id, "what do you remember about telescopes?");

        h.Graph.Verify(g => g.SearchAsync("default", It.Is<GraphQuery>(q => q.Text == "telescopes"), It.IsAny<CancellationToken>()), Times.Once);
        h.Client.AgentText().Should().Contain("don't have anything stored");
    }

    [Fact]
    public async Task CaptureMode_StoresPlainMessages()
    {
        await using var h = new AcpHarness();
        var id = await h.NewSessionAsync();
        await h.Client.CallAsync("session/set_mode", new { sessionId = id, modeId = "capture" });

        await h.PromptAsync(id, "The deploy key rotates monthly");

        h.Graph.Verify(g => g.RememberAsync("default", "The deploy key rotates monthly", null, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GraphMode_ShowsNeighborhoodOfTopMatch()
    {
        await using var h = new AcpHarness();
        var alice = new GraphNode { Id = "a", Name = "Alice", Type = "person" };
        var acme = new GraphNode { Id = "c", Name = "Acme", Type = "org" };
        h.Graph.Setup(g => g.SearchAsync("default", It.IsAny<GraphQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { alice });
        h.Graph.Setup(g => g.NeighborhoodAsync("default", "a", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphNeighborhood(alice, new[] { acme }, new[] { new GraphEdge { Id = "e", SourceId = "a", TargetId = "c", Type = "works_at" } }));
        var id = await h.NewSessionAsync();
        await h.Client.CallAsync("session/set_mode", new { sessionId = id, modeId = "graph" });

        await h.PromptAsync(id, "Alice");

        h.Client.AgentText().Should().Contain("Alice —works_at→ Acme");
    }

    [Fact]
    public async Task Relate_CreatesEdge()
    {
        await using var h = new AcpHarness();
        h.Graph.Setup(g => g.RelateAsync("default", "a", "b", "knows", "met at work", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphEdge { Id = "edge-9", SourceId = "a", TargetId = "b", Type = "knows" });
        var id = await h.NewSessionAsync();

        await h.PromptAsync(id, "/relate a knows b met at work");

        h.Client.AgentText().Should().Contain("edge-9");
        h.Client.UpdatesOf("tool_call_update").Last().GetProperty("status").GetString().Should().Be("completed");
    }

    [Fact]
    public async Task SchemasGraphAndHelp_ProduceReadOnlyOutput()
    {
        await using var h = new AcpHarness();
        h.Graph.Setup(g => g.ListGraphsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new KnowledgeGraph { Name = "default", Nodes = { new GraphNode { Id = "a", Name = "A" } } }
        });
        var id = await h.NewSessionAsync();

        await h.PromptAsync(id, "/schemas");
        h.Client.AgentText().Should().Contain("mcp-memory");
        h.Client.ClearUpdates();

        await h.PromptAsync(id, "/graph");
        h.Client.AgentText().Should().Contain("`default`").And.Contain("1 nodes");
        h.Client.UpdatesOf("tool_call")[0].GetProperty("kind").GetString().Should().Be("read");
        h.Client.ClearUpdates();

        await h.PromptAsync(id, "/help");
        h.Client.AgentText().Should().Contain("/remember").And.Contain("/workspace");
        h.Client.ClearUpdates();

        await h.PromptAsync(id, "/graph use notes");
        h.Client.AgentText().Should().Contain("Now using graph `notes`");
        h.Sessions.TryGet(id, out var session).Should().BeTrue();
        session.Graph.Should().Be("notes");
    }

    [Fact]
    public async Task UnknownCommand_ShowsHelp()
    {
        await using var h = new AcpHarness();
        var id = await h.NewSessionAsync();
        var response = await h.PromptAsync(id, "/bogus thing");
        response.GetProperty("stopReason").GetString().Should().Be("end_turn");
        h.Client.AgentText().Should().Contain("Unknown command `/bogus`").And.Contain("/remember");
    }

    [Fact]
    public async Task EmbeddedResource_IsIngestedAsDocument()
    {
        await using var h = new AcpHarness();
        var id = await h.NewSessionAsync();

        var response = await h.Client.CallAsync("session/prompt", new
        {
            sessionId = id,
            prompt = new object[]
            {
                new { type = "resource", resource = new { uri = "file:///work/notes/design.md", mimeType = "text/markdown", text = "# Design\nUse ports and adapters." } }
            }
        });

        response.GetProperty("stopReason").GetString().Should().Be("end_turn");
        h.Graph.Verify(g => g.RememberAsync("default", "# Design\nUse ports and adapters.", "design.md", "document", null, It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Client.UpdatesOf("tool_call")[0].GetProperty("locations")[0].GetProperty("path").GetString().Should().EndWith("design.md");
    }

    [Fact]
    public async Task EmptyPrompt_IsRefused()
    {
        await using var h = new AcpHarness();
        var id = await h.NewSessionAsync();
        var response = await h.Client.CallAsync("session/prompt", new { sessionId = id, prompt = Array.Empty<object>() });
        response.GetProperty("stopReason").GetString().Should().Be("refusal");
    }

    [Fact]
    public async Task ServiceFailure_FailsToolCallButEndsTurnGracefully()
    {
        await using var h = new AcpHarness();
        h.Graph.Setup(g => g.RememberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store offline"));
        var id = await h.NewSessionAsync();

        var response = await h.PromptAsync(id, "/remember anything");

        response.GetProperty("stopReason").GetString().Should().Be("end_turn");
        h.Client.UpdatesOf("tool_call_update").Last().GetProperty("status").GetString().Should().Be("failed");
        h.Client.AgentText().Should().Contain("store offline");
    }

    [Fact]
    public async Task Cancel_StopsTurnWithCancelledAndFailsOpenToolCalls()
    {
        await using var h = new AcpHarness();
        var searching = new TaskCompletionSource();
        h.Graph.Setup(g => g.SearchAsync("default", It.IsAny<GraphQuery>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, GraphQuery _, CancellationToken ct) =>
            {
                searching.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return (IReadOnlyList<GraphNode>)Array.Empty<GraphNode>();
            });
        var id = await h.NewSessionAsync();

        var prompt = h.PromptAsync(id, "slow query");
        await searching.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Client.NotifyAsync("session/cancel", new { sessionId = id });
        var response = await prompt.WaitAsync(TimeSpan.FromSeconds(10));

        response.GetProperty("stopReason").GetString().Should().Be("cancelled");
        var last = h.Client.UpdatesOf("tool_call_update").Last();
        last.GetProperty("status").GetString().Should().Be("failed");

        // the session accepts new work after a cancelled turn
        h.Graph.Setup(g => g.SearchAsync("default", It.IsAny<GraphQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<GraphNode>());
        (await h.PromptAsync(id, "/help")).GetProperty("stopReason").GetString().Should().Be("end_turn");
    }

    [Fact]
    public async Task ConcurrentPrompt_IsRejected()
    {
        await using var h = new AcpHarness();
        var started = new TaskCompletionSource();
        h.Graph.Setup(g => g.SearchAsync("default", It.IsAny<GraphQuery>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, GraphQuery _, CancellationToken ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return (IReadOnlyList<GraphNode>)Array.Empty<GraphNode>();
            });
        var id = await h.NewSessionAsync();
        var first = h.PromptAsync(id, "blocked");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = () => h.PromptAsync(id, "/help");
        (await second.Should().ThrowAsync<JsonRpcRemoteException>()).Which.Code.Should().Be(JsonRpcErrorCodes.InvalidRequest);

        await h.Client.NotifyAsync("session/cancel", new { sessionId = id });
        (await first.WaitAsync(TimeSpan.FromSeconds(10))).GetProperty("stopReason").GetString().Should().Be("cancelled");
    }

    [Fact]
    public async Task Prompt_ForUnknownSession_IsResourceNotFound()
    {
        await using var h = new AcpHarness();
        await h.InitializeAsync();
        var act = () => h.PromptAsync("acp-unknown", "hello");
        (await act.Should().ThrowAsync<JsonRpcRemoteException>()).Which.Code.Should().Be(JsonRpcErrorCodes.ResourceNotFound);
    }

    [Fact]
    public async Task Prompt_MissingPromptArray_IsInvalidParams()
    {
        await using var h = new AcpHarness();
        var id = await h.NewSessionAsync();
        var act = () => h.Client.CallAsync("session/prompt", new { sessionId = id });
        (await act.Should().ThrowAsync<JsonRpcRemoteException>()).Which.Code.Should().Be(JsonRpcErrorCodes.InvalidParams);
    }

    [Fact]
    public async Task Prompt_UnknownContentBlockType_IsInvalidParams()
    {
        await using var h = new AcpHarness();
        var id = await h.NewSessionAsync();
        var act = () => h.Client.CallAsync("session/prompt", new { sessionId = id, prompt = new object[] { new { type = "hologram" } } });
        (await act.Should().ThrowAsync<JsonRpcRemoteException>()).Which.Code.Should().Be(JsonRpcErrorCodes.InvalidParams);
    }
}

public class AcpPermissionTests
{
    [Fact]
    public async Task Forget_Allowed_DeletesAfterPermissionGrant()
    {
        await using var h = new AcpHarness();
        var id = await h.NewSessionAsync();

        var response = await h.PromptAsync(id, "/forget node-42");

        response.GetProperty("stopReason").GetString().Should().Be("end_turn");
        var request = h.Client.PermissionRequests.Should().ContainSingle().Subject;
        request.GetProperty("sessionId").GetString().Should().Be(id);
        request.GetProperty("toolCall").GetProperty("kind").GetString().Should().Be("delete");
        var toolCallId = h.Client.UpdatesOf("tool_call")[0].GetProperty("toolCallId").GetString();
        request.GetProperty("toolCall").GetProperty("toolCallId").GetString().Should().Be(toolCallId);
        request.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("kind").GetString())
            .Should().Equal("allow_once", "allow_always", "reject_once", "reject_always");

        h.Graph.Verify(g => g.ForgetAsync("default", "node-42", It.IsAny<CancellationToken>()), Times.Once);
        h.Client.UpdatesOf("tool_call_update").Last().GetProperty("status").GetString().Should().Be("completed");
        h.Client.AgentText().Should().Contain("Forgot node `node-42`");
    }

    [Fact]
    public async Task Forget_Rejected_KeepsNode()
    {
        await using var h = new AcpHarness();
        h.Client.OnPermission = _ => Task.FromResult<object?>(AcpTestClient.Selected("reject"));
        var id = await h.NewSessionAsync();

        var response = await h.PromptAsync(id, "/forget node-42");

        response.GetProperty("stopReason").GetString().Should().Be("end_turn");
        h.Graph.Verify(g => g.ForgetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Client.UpdatesOf("tool_call_update").Last().GetProperty("status").GetString().Should().Be("failed");
        h.Client.AgentText().Should().Contain("kept node `node-42`");
    }

    [Fact]
    public async Task Forget_CancelledOutcome_IsTreatedAsRejection()
    {
        await using var h = new AcpHarness();
        h.Client.OnPermission = _ => Task.FromResult<object?>(AcpTestClient.Cancelled());
        var id = await h.NewSessionAsync();

        await h.PromptAsync(id, "/forget node-42");

        h.Graph.Verify(g => g.ForgetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Forget_AllowAlways_SkipsFuturePrompts()
    {
        await using var h = new AcpHarness();
        h.Client.OnPermission = _ => Task.FromResult<object?>(AcpTestClient.Selected("allow_always"));
        var id = await h.NewSessionAsync();

        await h.PromptAsync(id, "/forget one");
        await h.PromptAsync(id, "/forget two");

        h.Client.PermissionRequests.Should().HaveCount(1);
        h.Graph.Verify(g => g.ForgetAsync("default", "two", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Forget_RejectAlways_BlocksFutureDeletionsWithoutAsking()
    {
        await using var h = new AcpHarness();
        h.Client.OnPermission = _ => Task.FromResult<object?>(AcpTestClient.Selected("reject_always"));
        var id = await h.NewSessionAsync();

        await h.PromptAsync(id, "/forget one");
        await h.PromptAsync(id, "/forget two");

        h.Client.PermissionRequests.Should().HaveCount(1);
        h.Graph.Verify(g => g.ForgetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Forget_ClientWithoutPermissionSupport_IsSafelyRejected()
    {
        await using var h = new AcpHarness();
        h.Client.OnPermission = _ => throw new JsonRpcException(JsonRpcErrorCodes.MethodNotFound, "nope");
        var id = await h.NewSessionAsync();

        await h.PromptAsync(id, "/forget node-42");

        h.Graph.Verify(g => g.ForgetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Forget_NodeMissing_FailsToolCall()
    {
        await using var h = new AcpHarness();
        h.Graph.Setup(g => g.ForgetAsync("default", "ghost", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var id = await h.NewSessionAsync();

        await h.PromptAsync(id, "/forget ghost");

        h.Client.UpdatesOf("tool_call_update").Last().GetProperty("status").GetString().Should().Be("failed");
        h.Client.AgentText().Should().Contain("couldn't find node `ghost`");
    }

    [Fact]
    public async Task Cancel_WhilePermissionIsPending_EndsTurnAsCancelled()
    {
        await using var h = new AcpHarness();
        var asked = new TaskCompletionSource();
        h.Client.OnPermission = async _ =>
        {
            asked.TrySetResult();
            await Task.Delay(Timeout.Infinite);
            return null;
        };
        var id = await h.NewSessionAsync();

        var prompt = h.PromptAsync(id, "/forget node-42");
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Client.NotifyAsync("session/cancel", new { sessionId = id });
        var response = await prompt.WaitAsync(TimeSpan.FromSeconds(10));

        response.GetProperty("stopReason").GetString().Should().Be("cancelled");
        h.Graph.Verify(g => g.ForgetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

public class AcpFileSystemTests
{
    private const string Payload = "{\"type\":\"entity\",\"name\":\"Alice\"}";

    [Fact]
    public async Task Import_ReadsPathThroughClientFileSystem()
    {
        await using var h = new AcpHarness();
        h.Client.Files["/work/project/memory.jsonl"] = Payload;
        h.Graph.Setup(g => g.ImportAsync("mcp-memory", Payload, "default", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeGraph { Name = "default", Nodes = { new GraphNode { Id = "a", Name = "Alice" } } });
        var id = await h.NewSessionAsync("/work/project");

        await h.PromptAsync(id, "/import mcp-memory memory.jsonl");

        h.Graph.Verify(g => g.ImportAsync("mcp-memory", Payload, "default", true, It.IsAny<CancellationToken>()), Times.Once);
        h.Client.UpdatesOf("tool_call").Select(u => u.GetProperty("kind").GetString()).Should().Equal("read", "edit");
        h.Client.UpdatesOf("plan").Last().GetProperty("entries").EnumerateArray().Should().OnlyContain(e => e.GetProperty("status").GetString() == "completed");
        h.Client.AgentText().Should().Contain("1 nodes");
    }

    [Fact]
    public async Task Import_Inline_DoesNotNeedFileSystemCapability()
    {
        await using var h = new AcpHarness();
        h.Graph.Setup(g => g.ImportAsync("mcp-memory", Payload, "notes", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeGraph { Name = "notes" });
        await h.InitializeAsync(new { });
        var id = (await h.Client.CallAsync("session/new", new { cwd = "/w", mcpServers = Array.Empty<object>() })).GetProperty("sessionId").GetString()!;

        await h.PromptAsync(id, $"/import mcp-memory {Payload} --graph notes");

        h.Graph.Verify(g => g.ImportAsync("mcp-memory", Payload, "notes", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Import_PathWithoutClientCapability_ExplainsWhy()
    {
        await using var h = new AcpHarness();
        await h.InitializeAsync(new { });
        var id = (await h.Client.CallAsync("session/new", new { cwd = "/w", mcpServers = Array.Empty<object>() })).GetProperty("sessionId").GetString()!;

        await h.PromptAsync(id, "/import mcp-memory /w/data.jsonl");

        h.Graph.Verify(g => g.ImportAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Client.AgentText().Should().Contain("didn't advertise file system access");
    }

    [Fact]
    public async Task Import_Replace_AsksForPermissionAndHonorsRejection()
    {
        await using var h = new AcpHarness();
        h.Client.OnPermission = _ => Task.FromResult<object?>(AcpTestClient.Selected("reject"));
        var id = await h.NewSessionAsync();

        await h.PromptAsync(id, $"/import mcp-memory {Payload} --replace");

        h.Client.PermissionRequests.Should().ContainSingle();
        h.Graph.Verify(g => g.ImportAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Import_UnknownSchema_ListsGuidance()
    {
        await using var h = new AcpHarness();
        var id = await h.NewSessionAsync();
        await h.PromptAsync(id, "/import nonsense {}");
        h.Client.AgentText().Should().Contain("not an importable schema");
    }

    [Fact]
    public async Task Export_Inline_ReturnsContent()
    {
        await using var h = new AcpHarness();
        h.Graph.Setup(g => g.ExportAsync("mcp-memory", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphExportResult { Schema = "mcp-memory", ContentType = "application/jsonl", Content = "{\"a\":1}" });
        var id = await h.NewSessionAsync();

        await h.PromptAsync(id, "/export mcp-memory");

        h.Client.AgentText().Should().Contain("{\"a\":1}");
    }

    [Fact]
    public async Task Export_ToPath_WritesThroughClientAfterPermission()
    {
        await using var h = new AcpHarness();
        h.Graph.Setup(g => g.ExportAsync("mcp-memory", "team", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphExportResult { Schema = "mcp-memory", ContentType = "application/jsonl", Content = "{\"a\":1}" });
        var id = await h.NewSessionAsync("/work/project");

        await h.PromptAsync(id, "/export mcp-memory team --to out/memory.jsonl");

        h.Client.PermissionRequests.Should().ContainSingle();
        h.Client.Writes.Should().ContainSingle().Which.Should().Be(("/work/project/out/memory.jsonl", "{\"a\":1}"));
        var done = h.Client.UpdatesOf("tool_call_update").Last();
        done.GetProperty("status").GetString().Should().Be("completed");
        done.GetProperty("content")[0].GetProperty("type").GetString().Should().Be("diff");
    }

    [Fact]
    public async Task Export_MultiFile_WritesEveryFileBelowTarget()
    {
        await using var h = new AcpHarness();
        h.Graph.Setup(g => g.ExportAsync("mcp-memory", "default", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphExportResult
            {
                Schema = "mcp-memory",
                ContentType = "text/markdown",
                Files = new Dictionary<string, string> { ["a.md"] = "A", ["notes/b.md"] = "B" }
            });
        var id = await h.NewSessionAsync(@"C:\work");

        await h.PromptAsync(id, @"/export mcp-memory --to vault");

        h.Client.Writes.Select(w => w.Path).Should().BeEquivalentTo(@"C:\work\vault\a.md", @"C:\work\vault\notes\b.md");
    }

    [Fact]
    public async Task Export_ToPathRejected_WritesNothing()
    {
        await using var h = new AcpHarness();
        h.Client.OnPermission = _ => Task.FromResult<object?>(AcpTestClient.Selected("reject"));
        h.Graph.Setup(g => g.ExportAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GraphExportResult { Schema = "mcp-memory", ContentType = "x", Content = "c" });
        var id = await h.NewSessionAsync();

        await h.PromptAsync(id, "/export mcp-memory --to /tmp/out.jsonl");

        h.Client.Writes.Should().BeEmpty();
    }
}

public class AcpWorkspaceTests
{
    [Fact]
    public async Task Capture_And_List_UseWorkspaceService()
    {
        await using var h = new AcpHarness();
        h.Workspaces.Setup(w => w.CaptureWorkspaceAsync(It.Is<CaptureWorkspaceRequest>(r => r.Name == "morning"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CaptureWorkspaceResponse { Success = true, WorkspaceId = "ws-1", CapturedAt = DateTime.UtcNow, ComponentsCount = 4 });
        h.Workspaces.Setup(w => w.ListWorkspacesAsync(It.IsAny<ListWorkspacesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListWorkspacesResponse { Workspaces = Array.Empty<WorkspaceSummary>(), TotalCount = 0, HasMore = false, Skip = 0, Take = 20 });
        var id = await h.NewSessionAsync();

        await h.PromptAsync(id, "/workspace capture morning");
        h.Client.AgentText().Should().Contain("ws-1");
        h.Client.ClearUpdates();

        await h.PromptAsync(id, "/workspace list");
        h.Client.AgentText().Should().Contain("No workspaces captured yet");
    }

    [Fact]
    public async Task Delete_And_Restore_RequirePermission()
    {
        await using var h = new AcpHarness();
        h.Client.OnPermission = _ => Task.FromResult<object?>(AcpTestClient.Selected("reject"));
        var id = await h.NewSessionAsync();

        await h.PromptAsync(id, "/workspace delete ws-1");
        await h.PromptAsync(id, "/workspace restore ws-1");

        h.Client.PermissionRequests.Should().HaveCount(2);
        h.Workspaces.Verify(w => w.DeleteWorkspaceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Workspaces.Verify(w => w.RestoreWorkspaceAsync(It.IsAny<RestoreWorkspaceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_Allowed_CallsService()
    {
        await using var h = new AcpHarness();
        h.Workspaces.Setup(w => w.DeleteWorkspaceAsync("ws-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var id = await h.NewSessionAsync();

        await h.PromptAsync(id, "/workspace delete ws-1");

        h.Workspaces.Verify(w => w.DeleteWorkspaceAsync("ws-1", It.IsAny<CancellationToken>()), Times.Once);
        h.Client.AgentText().Should().Contain("Deleted workspace `ws-1`");
    }
}
