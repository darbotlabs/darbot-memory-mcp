using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Darbot.Memory.Mcp.Core.Graph;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Darbot.Memory.Mcp.Core.Acp;

/// <summary>
/// Agent side of the Agent Client Protocol (protocol version 1) backed by the Darbot memory services.
/// One instance serves one client connection; sessions live in a shared <see cref="AcpSessionStore"/>.
/// </summary>
public sealed partial class AcpAgent : IJsonRpcHandler
{
    public const int SupportedProtocolVersion = 1;
    public const string ApiKeyAuthMethodId = "darbot-api-key";

    private readonly IServiceProvider _services;
    private readonly AcpSessionStore _sessions;
    private readonly AcpOptions _options;
    private readonly ILogger _logger;
    private AcpClient? _client;
    private bool _initialized;
    private bool _authenticated;

    public AcpAgent(
        IServiceProvider services,
        AcpSessionStore sessions,
        AcpOptions options,
        ILogger? logger = null,
        bool preAuthenticated = false)
    {
        _services = services;
        _sessions = sessions;
        _options = options;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _authenticated = preAuthenticated || !options.RequireAgentAuth;
    }

    private AcpClient Client => _client ?? throw new InvalidOperationException("The agent is not attached to a connection.");
    private IGraphMemoryService Graph => _services.GetRequiredService<IGraphMemoryService>();
    private IConversationService Conversations => _services.GetRequiredService<IConversationService>();

    public void Attach(JsonRpcPeer peer) => _client = new AcpClient(peer, _options);

    public async Task<object?> HandleRequestAsync(JsonRpcRequestContext ctx, CancellationToken cancellationToken)
    {
        switch (ctx.Method)
        {
            case AcpMethods.Initialize:
                return Initialize(ctx);
            case AcpMethods.Authenticate:
                return Authenticate(ctx);
            case AcpMethods.Logout:
                RequireInitialized();
                _authenticated = !_options.RequireAgentAuth;
                return new JsonRpcEmptyResult();
            case AcpMethods.SessionPrompt:
                RequireReady();
                return await PromptAsync(ctx, cancellationToken).ConfigureAwait(false);
            case AcpMethods.SessionNew:
                RequireReady();
                return await NewSessionAsync(ctx, cancellationToken).ConfigureAwait(false);
            case AcpMethods.SessionLoad:
                RequireReady();
                return await LoadSessionAsync(ctx, cancellationToken).ConfigureAwait(false);
            case AcpMethods.SessionResume:
                RequireReady();
                return await ResumeSessionAsync(ctx, cancellationToken).ConfigureAwait(false);
            case AcpMethods.SessionList:
                RequireReady();
                return await ListSessionsAsync(ctx, cancellationToken).ConfigureAwait(false);
            case AcpMethods.SessionDelete:
                RequireReady();
                _sessions.MarkDeleted(ctx.GetParams<SessionIdRequest>().SessionId);
                return new JsonRpcEmptyResult();
            case AcpMethods.SessionClose:
                RequireReady();
                CloseSession(ctx.GetParams<SessionIdRequest>().SessionId);
                return new JsonRpcEmptyResult();
            case AcpMethods.SessionSetMode:
                RequireReady();
                return SetMode(ctx);
            case AcpMethods.SessionSetConfigOption:
                RequireReady();
                return await SetConfigOptionAsync(ctx, cancellationToken).ConfigureAwait(false);
            default:
                if (ctx.Method.StartsWith('_'))
                {
                    RequireReady();
                    return await HandleExtensionAsync(ctx, cancellationToken).ConfigureAwait(false);
                }

                throw new JsonRpcException(JsonRpcErrorCodes.MethodNotFound, $"Method not found: {ctx.Method}");
        }
    }

    public Task HandleNotificationAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (method == AcpMethods.SessionCancel && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String
            && _sessions.TryGet(id.GetString()!, out var session))
        {
            session.Cancel();
        }

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- lifecycle

    private InitializeResponse Initialize(JsonRpcRequestContext ctx)
    {
        var request = ctx.GetParams<InitializeRequest>();
        if (request.ProtocolVersion < 0)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "protocolVersion must be a non-negative integer");
        }

        Client.Capabilities = request.ClientCapabilities ?? new ClientCapabilities();
        Client.Info = request.ClientInfo;
        _initialized = true;

        return DescribeAgent(_options) with { Meta = request.Meta };
    }

    /// <summary>The initialize response an agent with these options advertises (also served by GET /acp/info).</summary>
    public static InitializeResponse DescribeAgent(AcpOptions options)
    {
        return new InitializeResponse
        {
            // Same version when supported, otherwise the latest one this agent speaks.
            ProtocolVersion = SupportedProtocolVersion,
            AgentCapabilities = new AgentCapabilities
            {
                LoadSession = true,
                PromptCapabilities = new PromptCapabilities { Image = false, Audio = false, EmbeddedContext = true },
                McpCapabilities = new McpCapabilities { Http = true, Sse = false },
                SessionCapabilities = new SessionCapabilities
                {
                    List = new EmptyCapability(),
                    Delete = new EmptyCapability(),
                    Resume = new EmptyCapability(),
                    Close = new EmptyCapability()
                },
                Auth = options.RequireAgentAuth ? new AgentAuthCapabilities { Logout = new EmptyCapability() } : null
            },
            AuthMethods = options.RequireAgentAuth
                ? new List<AuthMethod>
                {
                    new()
                    {
                        Id = ApiKeyAuthMethodId,
                        Name = "Darbot API key",
                        Description = "Send the Darbot API key as _meta.apiKey in the authenticate request."
                    }
                }
                : new List<AuthMethod>(),
            AgentInfo = new Implementation { Name = options.AgentName, Title = options.AgentTitle, Version = options.AgentVersion }
        };
    }

    private object Authenticate(JsonRpcRequestContext ctx)
    {
        RequireInitialized();
        var request = ctx.GetParams<AuthenticateRequest>();
        if (!_options.RequireAgentAuth)
        {
            return new JsonRpcEmptyResult();
        }

        if (request.MethodId != ApiKeyAuthMethodId)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"Unknown authentication method '{request.MethodId}'");
        }

        var supplied = request.Meta?["apiKey"]?.GetValue<string>();
        if (!AcpClient.KeysEqual(_options.ApiKey, supplied))
        {
            throw new JsonRpcException(JsonRpcErrorCodes.AuthRequired, "Authentication failed");
        }

        _authenticated = true;
        return new JsonRpcEmptyResult();
    }

    private void RequireInitialized()
    {
        if (!_initialized)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidRequest, "The 'initialize' request must be completed first");
        }
    }

    private void RequireReady()
    {
        RequireInitialized();
        if (!_authenticated)
        {
            throw new JsonRpcException(
                JsonRpcErrorCodes.AuthRequired,
                "Authentication required",
                new JsonObject { ["authMethods"] = new JsonArray(ApiKeyAuthMethodId) });
        }
    }

    // ---------------------------------------------------------------- sessions

    private static bool IsAbsolutePath(string path) =>
        path.StartsWith('/') || path.StartsWith('\\') || Regex.IsMatch(path, @"^[A-Za-z]:[\\/]");

    private static void ValidateSessionPaths(string cwd, IEnumerable<string>? additional)
    {
        if (!IsAbsolutePath(cwd))
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "cwd must be an absolute path");
        }

        if (additional is not null && additional.Any(p => !IsAbsolutePath(p)))
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "additionalDirectories entries must be absolute paths");
        }
    }

    private async Task<NewSessionResponse> NewSessionAsync(JsonRpcRequestContext ctx, CancellationToken ct)
    {
        var request = ctx.GetParams<NewSessionRequest>();
        ValidateSessionPaths(request.Cwd, request.AdditionalDirectories);

        var session = _sessions.Create(request.Cwd, _options.DefaultGraph, _options.PersistSessionsAsConversations);
        session.McpServers = request.McpServers;
        session.AdditionalDirectories = request.AdditionalDirectories ?? new List<string>();
        session.Meta = request.Meta;

        ctx.OnResponded(() => Client.SendUpdateAsync(session.Id, AvailableCommandsUpdateFor()));
        return new NewSessionResponse
        {
            SessionId = session.Id,
            Modes = ModeState(session),
            ConfigOptions = await BuildConfigOptionsAsync(session, ct).ConfigureAwait(false),
            Meta = request.Meta
        };
    }

    private async Task<AcpSession> ResolveSessionAsync(string sessionId, string cwd, IReadOnlyList<McpServer>? servers, IEnumerable<string>? additional, CancellationToken ct)
    {
        if (_sessions.IsDeleted(sessionId))
        {
            throw SessionNotFound(sessionId);
        }

        if (!_sessions.TryGet(sessionId, out var session))
        {
            var turns = sessionId.StartsWith("acp-", StringComparison.Ordinal)
                ? await SafeGetConversationAsync(sessionId, ct).ConfigureAwait(false)
                : Array.Empty<ConversationTurn>();
            if (turns.Count == 0)
            {
                throw SessionNotFound(sessionId);
            }

            session = _sessions.GetOrAdd(sessionId, () =>
            {
                var s = new AcpSession(sessionId, cwd)
                {
                    Graph = _options.DefaultGraph,
                    PersistTurns = _options.PersistSessionsAsConversations,
                    TurnCount = turns.Max(t => t.TurnNumber)
                };
                s.ReplaceHistory(turns.OrderBy(t => t.TurnNumber).SelectMany(t => new[]
                {
                    new AcpHistoryEntry(true, t.Prompt, new DateTimeOffset(DateTime.SpecifyKind(t.UtcTimestamp, DateTimeKind.Utc))),
                    new AcpHistoryEntry(false, t.Response, new DateTimeOffset(DateTime.SpecifyKind(t.UtcTimestamp, DateTimeKind.Utc)))
                }));
                s.Title = Snippet(turns.OrderBy(t => t.TurnNumber).First().Prompt, 60);
                return s;
            });
        }

        session.Cwd = cwd;
        if (servers is not null)
        {
            session.McpServers = servers;
        }

        session.AdditionalDirectories = additional?.ToList() ?? new List<string>();
        return session;
    }

    private async Task<IReadOnlyList<ConversationTurn>> SafeGetConversationAsync(string conversationId, CancellationToken ct)
    {
        try
        {
            return await Conversations.GetConversationAsync(conversationId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not load persisted conversation {ConversationId}", conversationId);
            return Array.Empty<ConversationTurn>();
        }
    }

    private static JsonRpcException SessionNotFound(string sessionId) =>
        new(JsonRpcErrorCodes.ResourceNotFound, $"Session not found: {sessionId}", new JsonObject { ["sessionId"] = sessionId });

    private AcpSession RequireSession(string sessionId)
    {
        if (!_sessions.TryGet(sessionId, out var session) || _sessions.IsDeleted(sessionId))
        {
            throw SessionNotFound(sessionId);
        }

        return session;
    }

    private async Task<LoadSessionResponse> LoadSessionAsync(JsonRpcRequestContext ctx, CancellationToken ct)
    {
        var request = ctx.GetParams<LoadSessionRequest>();
        ValidateSessionPaths(request.Cwd, request.AdditionalDirectories);
        var session = await ResolveSessionAsync(request.SessionId, request.Cwd, request.McpServers, request.AdditionalDirectories, ct).ConfigureAwait(false);

        // The whole transcript is replayed before the response, as the protocol requires.
        var history = session.History;
        for (var i = 0; i < history.Count; i++)
        {
            var content = ContentBlock.FromText(history[i].Text);
            var messageId = $"{session.Id}-{i}";
            SessionUpdate update = history[i].IsUser
                ? new UserMessageChunk { Content = content, MessageId = messageId }
                : new AgentMessageChunk { Content = content, MessageId = messageId };
            await Client.SendUpdateAsync(session.Id, update, CancellationToken.None).ConfigureAwait(false);
        }

        ctx.OnResponded(() => Client.SendUpdateAsync(session.Id, AvailableCommandsUpdateFor()));
        return new LoadSessionResponse
        {
            Modes = ModeState(session),
            ConfigOptions = await BuildConfigOptionsAsync(session, ct).ConfigureAwait(false),
            Meta = request.Meta
        };
    }

    private async Task<LoadSessionResponse> ResumeSessionAsync(JsonRpcRequestContext ctx, CancellationToken ct)
    {
        var request = ctx.GetParams<ResumeSessionRequest>();
        ValidateSessionPaths(request.Cwd, request.AdditionalDirectories);
        var session = await ResolveSessionAsync(request.SessionId, request.Cwd, request.McpServers, request.AdditionalDirectories, ct).ConfigureAwait(false);
        ctx.OnResponded(() => Client.SendUpdateAsync(session.Id, AvailableCommandsUpdateFor()));
        return new LoadSessionResponse
        {
            Modes = ModeState(session),
            ConfigOptions = await BuildConfigOptionsAsync(session, ct).ConfigureAwait(false),
            Meta = request.Meta
        };
    }

    private async Task<ListSessionsResponse> ListSessionsAsync(JsonRpcRequestContext ctx, CancellationToken ct)
    {
        const int pageSize = 50;
        var request = ctx.GetParams<ListSessionsRequest>();
        var infos = _sessions.List(request.Cwd)
            .Select(s => new SessionInfo
            {
                SessionId = s.Id,
                Cwd = s.Cwd,
                Title = s.Title,
                UpdatedAt = s.UpdatedAt.ToString("o", CultureInfo.InvariantCulture)
            })
            .ToList();

        if (request.Cwd is null)
        {
            try
            {
                var known = infos.Select(i => i.SessionId).ToHashSet(StringComparer.Ordinal);
                var persisted = await Conversations.ListConversationsAsync(new ConversationListRequest { Take = 500 }, ct).ConfigureAwait(false);
                infos.AddRange(persisted.Conversations
                    .Where(c => c.ConversationId.StartsWith("acp-", StringComparison.Ordinal)
                                && !known.Contains(c.ConversationId)
                                && !_sessions.IsDeleted(c.ConversationId))
                    .Select(c => new SessionInfo
                    {
                        SessionId = c.ConversationId,
                        Cwd = Directory.GetCurrentDirectory(),
                        Title = c.LastPrompt is null ? null : Snippet(c.LastPrompt, 60),
                        UpdatedAt = new DateTimeOffset(DateTime.SpecifyKind(c.LastTurnTimestamp, DateTimeKind.Utc)).ToString("o", CultureInfo.InvariantCulture)
                    }));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not list persisted ACP conversations");
            }
        }

        infos = infos.OrderByDescending(i => i.UpdatedAt, StringComparer.Ordinal).ToList();
        var offset = 0;
        if (request.Cursor is not null && !int.TryParse(request.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset))
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Invalid cursor");
        }

        var page = infos.Skip(offset).Take(pageSize).ToList();
        return new ListSessionsResponse
        {
            Sessions = page,
            NextCursor = offset + pageSize < infos.Count ? (offset + pageSize).ToString(CultureInfo.InvariantCulture) : null,
            Meta = request.Meta
        };
    }

    private void CloseSession(string sessionId)
    {
        RequireSession(sessionId);
        _sessions.Remove(sessionId);
    }

    private static SessionModeState ModeState(AcpSession session) =>
        new() { CurrentModeId = session.ModeId, AvailableModes = AcpModes.All.ToList() };

    private object SetMode(JsonRpcRequestContext ctx)
    {
        var request = ctx.GetParams<SetSessionModeRequest>();
        var session = RequireSession(request.SessionId);
        ApplyMode(session, request.ModeId);
        ctx.OnResponded(() => Client.SendUpdateAsync(session.Id, new CurrentModeUpdate { CurrentModeId = session.ModeId }));
        return new JsonRpcEmptyResult { };
    }

    private static void ApplyMode(AcpSession session, string modeId)
    {
        if (!AcpModes.All.Any(m => m.Id == modeId))
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"Unknown mode '{modeId}'");
        }

        session.ModeId = modeId;
    }

    private async Task<SetSessionConfigOptionResponse> SetConfigOptionAsync(JsonRpcRequestContext ctx, CancellationToken ct)
    {
        var request = ctx.GetParams<SetSessionConfigOptionRequest>();
        var session = RequireSession(request.SessionId);
        var modeChanged = false;
        switch (request.ConfigId)
        {
            case "mode":
                ApplyMode(session, RequireString(request.Value));
                modeChanged = true;
                break;
            case "graph":
                var graph = RequireString(request.Value).Trim();
                if (graph.Length == 0)
                {
                    throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "graph must not be empty");
                }

                session.Graph = graph;
                break;
            case "persist_turns":
                if (request.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "persist_turns expects a boolean value");
                }

                session.PersistTurns = request.Value.GetBoolean();
                break;
            default:
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"Unknown config option '{request.ConfigId}'");
        }

        if (modeChanged)
        {
            ctx.OnResponded(() => Client.SendUpdateAsync(session.Id, new CurrentModeUpdate { CurrentModeId = session.ModeId }));
        }

        return new SetSessionConfigOptionResponse
        {
            ConfigOptions = await BuildConfigOptionsAsync(session, ct).ConfigureAwait(false),
            Meta = request.Meta
        };
    }

    private static string RequireString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Expected a string value");

    private async Task<List<SessionConfigOption>> BuildConfigOptionsAsync(AcpSession session, CancellationToken ct)
    {
        var graphs = new List<string> { _options.DefaultGraph };
        try
        {
            graphs.AddRange((await Graph.ListGraphsAsync(ct).ConfigureAwait(false)).Select(g => g.Name));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Graph list unavailable while building config options");
        }

        graphs.Add(session.Graph);
        var options = new List<SessionConfigOption>
        {
            new()
            {
                Id = "mode",
                Name = "Mode",
                Description = "How plain messages are handled.",
                Category = "mode",
                Type = "select",
                CurrentValue = session.ModeId,
                Options = AcpModes.All.Select(m => new SessionConfigSelectOption { Value = m.Id, Name = m.Name, Description = m.Description }).ToList()
            },
            new()
            {
                Id = "graph",
                Name = "Knowledge graph",
                Description = "The graph that memories are read from and written to.",
                Category = "_graph",
                Type = "select",
                CurrentValue = session.Graph,
                Options = graphs.Distinct(StringComparer.Ordinal).Select(g => new SessionConfigSelectOption { Value = g, Name = g }).ToList()
            }
        };

        if (Client.SupportsBooleanConfig)
        {
            options.Add(new SessionConfigOption
            {
                Id = "persist_turns",
                Name = "Persist turns",
                Description = "Store each turn as an audit-trail conversation.",
                Category = "_persistence",
                Type = "boolean",
                CurrentValue = session.PersistTurns
            });
        }

        return options;
    }

    // ---------------------------------------------------------------- extension methods

    private async Task<object?> HandleExtensionAsync(JsonRpcRequestContext ctx, CancellationToken ct)
    {
        var p = ctx.Params.ValueKind == JsonValueKind.Object ? ctx.Params : default;
        string? Str(string name) =>
            p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string Required(string name) =>
            Str(name) ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"'{name}' is required");
        bool Bool(string name, bool fallback) =>
            p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean()
                : fallback;

        switch (ctx.Method)
        {
            case "_darbot/graph/import":
                {
                    var graph = await Graph.ImportAsync(Required("schema"), Required("payload"), Str("graph") ?? _options.DefaultGraph, Bool("merge", true), ct).ConfigureAwait(false);
                    return new { graph = graph.Name, nodes = graph.Nodes.Count, edges = graph.Edges.Count };
                }
            case "_darbot/graph/export":
                return await Graph.ExportAsync(Required("schema"), Str("graph") ?? _options.DefaultGraph, ct).ConfigureAwait(false);
            case "_darbot/graph/list":
                {
                    var graphs = await Graph.ListGraphsAsync(ct).ConfigureAwait(false);
                    return new
                    {
                        graphs = graphs.Select(g => new { name = g.Name, nodes = g.Nodes.Count, edges = g.Edges.Count, updatedAt = g.UpdatedAt })
                    };
                }
            case "_darbot/graph/search":
                {
                    var limit = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("limit", out var l) && l.TryGetInt32(out var li) ? li : 25;
                    var nodes = await Graph.SearchAsync(Str("graph") ?? _options.DefaultGraph, new GraphQuery { Text = Str("text"), Type = Str("type"), Tag = Str("tag"), Limit = limit }, ct).ConfigureAwait(false);
                    return new { nodes = nodes.Select(n => n with { Embedding = null }) };
                }
            default:
                throw new JsonRpcException(JsonRpcErrorCodes.MethodNotFound, $"Method not found: {ctx.Method}");
        }
    }

    private static AvailableCommandsUpdate AvailableCommandsUpdateFor() => new() { AvailableCommands = Commands.ToList() };

    internal static string Snippet(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var single = Regex.Replace(text.Trim(), @"\s+", " ");
        return single.Length <= max ? single : single[..(max - 1)] + "…";
    }
}



