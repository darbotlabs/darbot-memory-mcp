using System.Text.Json;
using Darbot.Memory.Mcp.Core.Acp;
using Darbot.Memory.Mcp.Core.Graph;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Darbot.Memory.Mcp.Tests.Acp;

/// <summary>Plays the ACP client: records session/update notifications and answers agent-to-client requests.</summary>
internal sealed class AcpTestClient : IJsonRpcHandler
{
    private readonly object _gate = new();
    private readonly List<JsonElement> _updates = new();
    private readonly List<JsonElement> _permissionRequests = new();

    public JsonRpcPeer Peer { get; private set; } = null!;
    public Func<JsonElement, Task<object?>> OnPermission { get; set; } = _ => Task.FromResult<object?>(Selected("allow"));
    public Dictionary<string, string> Files { get; } = new();
    public List<(string Path, string Content)> Writes { get; } = new();
    public Func<JsonElement, object?>? OnTerminalCreate { get; set; }

    public static object Selected(string optionId) => new { outcome = new { outcome = "selected", optionId } };
    public static object Cancelled() => new { outcome = new { outcome = "cancelled" } };

    public void Attach(JsonRpcPeer peer) => Peer = peer;

    public IReadOnlyList<JsonElement> Updates
    {
        get
        {
            lock (_gate)
            {
                return _updates.ToArray();
            }
        }
    }

    public IReadOnlyList<JsonElement> PermissionRequests
    {
        get
        {
            lock (_gate)
            {
                return _permissionRequests.ToArray();
            }
        }
    }

    public IReadOnlyList<string> UpdateKinds => Updates.Select(u => u.GetProperty("sessionUpdate").GetString()!).ToArray();

    public IReadOnlyList<JsonElement> UpdatesOf(string kind) =>
        Updates.Where(u => u.GetProperty("sessionUpdate").GetString() == kind).ToArray();

    public string AgentText() => string.Concat(UpdatesOf("agent_message_chunk").Select(u => u.GetProperty("content").GetProperty("text").GetString()));

    public void ClearUpdates()
    {
        lock (_gate)
        {
            _updates.Clear();
        }
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters = null) =>
        await Peer.SendRequestAsync(method, parameters, TimeSpan.FromSeconds(15));

    public Task NotifyAsync(string method, object? parameters = null) => Peer.SendNotificationAsync(method, parameters);

    public async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(10);
        }
    }

    public async Task<object?> HandleRequestAsync(JsonRpcRequestContext context, CancellationToken cancellationToken)
    {
        switch (context.Method)
        {
            case AcpMethods.SessionRequestPermission:
                lock (_gate)
                {
                    _permissionRequests.Add(context.Params.Clone());
                }

                return await OnPermission(context.Params.Clone());
            case AcpMethods.FsReadTextFile:
                var path = context.Params.GetProperty("path").GetString()!;
                return Files.TryGetValue(path, out var content)
                    ? new { content }
                    : throw new JsonRpcException(JsonRpcErrorCodes.ResourceNotFound, "File not found");
            case AcpMethods.FsWriteTextFile:
                Writes.Add((context.Params.GetProperty("path").GetString()!, context.Params.GetProperty("content").GetString()!));
                return new JsonRpcEmptyResult();
            case AcpMethods.TerminalCreate when OnTerminalCreate is not null:
                return OnTerminalCreate(context.Params.Clone());
            default:
                throw new JsonRpcException(JsonRpcErrorCodes.MethodNotFound, $"Method not found: {context.Method}");
        }
    }

    public Task HandleNotificationAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (method == AcpMethods.SessionUpdate)
        {
            lock (_gate)
            {
                var update = parameters.GetProperty("update").Clone();
                _updates.Add(update);
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>An agent wired to an <see cref="AcpTestClient"/> through an in-memory connection, with mocked memory services.</summary>
internal sealed class AcpHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _serverTask;
    private readonly Task _clientTask;
    private readonly InMemoryAcpConnection _clientConnection;

    public AcpHarness(Action<AcpOptions>? configure = null, AcpSessionStore? sessions = null, bool preAuthenticated = false)
    {
        Options = new AcpOptions { PromptTimeoutSeconds = 30 };
        configure?.Invoke(Options);
        Sessions = sessions ?? new AcpSessionStore();

        Adapter.SetupGet(a => a.Name).Returns("mcp-memory");
        Adapter.SetupGet(a => a.DisplayName).Returns("MCP Memory");
        Adapter.SetupGet(a => a.Description).Returns("Anthropic MCP memory server format");
        Adapter.SetupGet(a => a.ContentType).Returns("application/jsonl");
        Adapter.SetupGet(a => a.CanImport).Returns(true);
        Adapter.SetupGet(a => a.CanExport).Returns(true);

        Graph.SetupGet(g => g.Schemas).Returns(new[] { Adapter.Object });
        Graph.Setup(g => g.SearchAsync(It.IsAny<string>(), It.IsAny<GraphQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GraphNode>());
        Graph.Setup(g => g.ListGraphsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<KnowledgeGraph>());
        Graph.Setup(g => g.RememberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string g, string text, string? _, string? _, string? _, IEnumerable<string>? _, CancellationToken _) =>
                new GraphNode { Id = "node-1", Name = text.Length > 20 ? text[..20] : text, Type = "memory", Content = text });
        Graph.Setup(g => g.ForgetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        Conversations.Setup(c => c.PersistTurnAsync(It.IsAny<ConversationTurn>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        Conversations.Setup(c => c.GetConversationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ConversationTurn>());
        Conversations.Setup(c => c.SearchConversationsAsync(It.IsAny<ConversationSearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationSearchResponse { Results = Array.Empty<ConversationTurn>(), TotalCount = 0, HasMore = false, Skip = 0, Take = 5 });
        Conversations.Setup(c => c.ListConversationsAsync(It.IsAny<ConversationListRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationListResponse { Conversations = Array.Empty<ConversationSummary>(), TotalCount = 0, HasMore = false, Skip = 0, Take = 50 });

        var services = new ServiceCollection();
        services.AddSingleton(Graph.Object);
        services.AddSingleton(Conversations.Object);
        services.AddSingleton(Workspaces.Object);
        var provider = services.BuildServiceProvider();

        var (serverSide, clientSide) = InMemoryAcpConnection.CreatePair();
        _clientConnection = clientSide;
        _serverTask = AcpAgentHost.RunAsync(serverSide, provider, Sessions, Options, NullLogger.Instance, preAuthenticated, _cts.Token);

        var peer = new JsonRpcPeer(clientSide, Client, logger: NullLogger.Instance);
        Client.Attach(peer);
        _clientTask = peer.RunAsync(_cts.Token);
    }

    public Mock<IGraphSchemaAdapter> Adapter { get; } = new();
    public Mock<IGraphMemoryService> Graph { get; } = new();
    public Mock<IConversationService> Conversations { get; } = new();
    public Mock<IWorkspaceService> Workspaces { get; } = new();
    public AcpOptions Options { get; }
    public AcpSessionStore Sessions { get; }
    public AcpTestClient Client { get; } = new();

    public Task<JsonElement> InitializeAsync(object? capabilities = null, int version = 1) =>
        Client.CallAsync("initialize", new
        {
            protocolVersion = version,
            clientCapabilities = capabilities ?? new { fs = new { readTextFile = true, writeTextFile = true }, terminal = true },
            clientInfo = new { name = "test-client", version = "0.0.1" }
        });

    public async Task<string> NewSessionAsync(string cwd = "/work/project")
    {
        await InitializeAsync();
        var result = await Client.CallAsync("session/new", new { cwd, mcpServers = Array.Empty<object>() });
        var id = result.GetProperty("sessionId").GetString()!;
        await Client.WaitUntilAsync(() => Client.UpdateKinds.Contains("available_commands_update"));
        Client.ClearUpdates();
        return id;
    }

    public async Task<JsonElement> PromptAsync(string sessionId, string text) =>
        await Client.CallAsync("session/prompt", new { sessionId, prompt = new object[] { new { type = "text", text } } });

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _clientConnection.DisposeAsync();
        try
        {
            await Task.WhenAll(_serverTask, _clientTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // shutdown races are irrelevant to the assertions
        }

        _cts.Dispose();
    }
}
