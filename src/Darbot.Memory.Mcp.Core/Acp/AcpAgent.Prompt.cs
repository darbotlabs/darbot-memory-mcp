using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Darbot.Memory.Mcp.Core.Graph;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Darbot.Memory.Mcp.Core.Acp;

public sealed partial class AcpAgent
{
    public static readonly IReadOnlyList<AvailableCommand> Commands = new[]
    {
        Command("remember", "Store a fact or note in the knowledge graph", "text to remember"),
        Command("recall", "Search memories and past conversations", "what to look for"),
        Command("forget", "Delete a knowledge graph node (asks for permission)", "node id"),
        Command("relate", "Link two nodes with a typed edge", "<source id> <type> <target id> [fact]"),
        Command("graph", "List graphs, show one, or switch with 'use <name>'", "[name | use <name>]"),
        Command("schemas", "List supported import/export memory schemas", null),
        Command("import", "Import a memory schema payload from a file path or inline text", "<schema> <path-or-inline> [--graph name] [--replace]"),
        Command("export", "Export a graph in a memory schema, optionally to a file", "<schema> [graph] [--to path]"),
        Command("workspace", "Capture, list, restore or delete workspace snapshots", "capture [name] | list | restore <id> | delete <id>"),
        Command("help", "Show available commands", null)
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "what", "who", "when", "where", "which", "how", "why", "that", "this", "these", "those",
        "about", "remember", "recall", "know", "you", "your", "are", "was", "were", "have", "has", "had", "does", "did", "tell",
        "from", "into", "any", "anything", "can", "could", "would", "should", "please", "there", "their", "them", "then", "than"
    };

    private static AvailableCommand Command(string name, string description, string? hint) =>
        new() { Name = name, Description = description, Input = hint is null ? null : new AvailableCommandInput { Hint = hint } };

    private static readonly Regex CommandPattern = new(@"^/([A-Za-z][\w-]*)(?:\s+(.*))?$", RegexOptions.Singleline | RegexOptions.Compiled);

    // ---------------------------------------------------------------- turn lifecycle

    // Everything up to the first await runs on the read loop, so the turn is registered before any later session/cancel is read.
    private async Task<PromptResponse> PromptAsync(JsonRpcRequestContext ctx, CancellationToken requestToken)
    {
        var request = ctx.GetParams<PromptRequest>();
        var session = RequireSession(request.SessionId);
        if (!session.TryBeginTurn(requestToken, out var turnCts))
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidRequest, "A prompt turn is already in progress for this session");
        }

        try
        {
            if (_options.PromptTimeoutSeconds > 0)
            {
                turnCts.CancelAfter(TimeSpan.FromSeconds(_options.PromptTimeoutSeconds));
            }

            var stop = await RunTurnAsync(session, request.Prompt, turnCts.Token, requestToken).ConfigureAwait(false);
            return new PromptResponse { StopReason = stop, Meta = request.Meta };
        }
        finally
        {
            session.EndTurn(turnCts);
        }
    }

    private async Task<StopReason> RunTurnAsync(AcpSession session, List<ContentBlock> blocks, CancellationToken token, CancellationToken requestToken)
    {
        var turn = new AcpTurn(Client, session, token);
        var userText = string.Join("\n", blocks.OfType<TextContent>().Select(b => b.Text)).Trim();
        var documents = blocks.OfType<EmbeddedResourceContent>().ToList();
        var links = blocks.OfType<ResourceLinkContent>().ToList();

        if (userText.Length == 0 && documents.Count == 0 && links.Count == 0)
        {
            await turn.MessageAsync("I can only work with text and embedded documents. Send a message or try `/help`.").ConfigureAwait(false);
            return StopReason.Refusal;
        }

        var stop = StopReason.EndTurn;
        session.AddHistory(true, DescribePrompt(userText, documents, links));
        try
        {
            if (documents.Count > 0)
            {
                await IngestDocumentsAsync(turn, session, documents).ConfigureAwait(false);
            }

            if (links.Count > 0)
            {
                await turn.MessageAsync(
                    "Received resource links (" + string.Join(", ", links.Select(l => $"`{l.Name}`")) +
                    "). I don't fetch links; attach the content as an embedded resource to store it.\n\n").ConfigureAwait(false);
            }

            if (userText.Length > 0)
            {
                var match = CommandPattern.Match(userText);
                if (match.Success)
                {
                    await RunCommandAsync(turn, session, match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value.Trim()).ConfigureAwait(false);
                }
                else
                {
                    await RunPlainAsync(turn, session, userText).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await turn.AbortOpenToolsAsync("Cancelled").ConfigureAwait(false);
            if (requestToken.IsCancellationRequested)
            {
                await FinishTurnAsync(session, turn, userText, documents, StopReason.Cancelled).ConfigureAwait(false);
                throw;
            }

            if (!session.CancelRequested)
            {
                await FinishTurnAsync(session, turn, userText, documents, StopReason.Cancelled).ConfigureAwait(false);
                throw new JsonRpcException(JsonRpcErrorCodes.InternalError, "Prompt timed out");
            }

            stop = StopReason.Cancelled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not JsonRpcException)
        {
            _logger.LogError(ex, "Prompt turn failed in session {SessionId}", session.Id);
            await turn.AbortOpenToolsAsync(ex.Message).ConfigureAwait(false);
            await turn.MessageAsync($"Something went wrong: {ex.Message}\n").ConfigureAwait(false);
        }

        await FinishTurnAsync(session, turn, userText, documents, stop).ConfigureAwait(false);
        return stop;
    }

    private static string DescribePrompt(string text, List<EmbeddedResourceContent> documents, List<ResourceLinkContent> links)
    {
        var sb = new StringBuilder(text);
        foreach (var uri in documents.Select(d => d.Resource.Uri).Concat(links.Select(l => l.Uri)))
        {
            sb.Append(sb.Length > 0 ? "\n" : string.Empty).Append("[attachment: ").Append(uri).Append(']');
        }

        return sb.ToString();
    }

    private async Task FinishTurnAsync(AcpSession session, AcpTurn turn, string userText, List<EmbeddedResourceContent> documents, StopReason stop)
    {
        var response = turn.Response;
        if (response.Length > 0)
        {
            session.AddHistory(false, response);
        }

        session.TurnCount++;
        if (session.Title is null)
        {
            session.Title = Snippet(userText.Length > 0 ? userText : documents.FirstOrDefault()?.Resource.Uri, 60);
            try
            {
                await Client.SendUpdateAsync(
                    session.Id,
                    new SessionInfoUpdate { Title = session.Title, UpdatedAt = session.UpdatedAt.ToString("o", CultureInfo.InvariantCulture) },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not send session info update");
            }
        }

        if (!session.PersistTurns || !_options.PersistSessionsAsConversations)
        {
            return;
        }

        try
        {
            await Conversations.PersistTurnAsync(new ConversationTurn
            {
                ConversationId = session.Id,
                TurnNumber = session.TurnCount,
                UtcTimestamp = DateTime.UtcNow,
                Prompt = userText.Length > 0 ? userText : DescribePrompt(string.Empty, documents, new List<ResourceLinkContent>()),
                Model = "darbot-acp",
                Response = response.Length > 0 ? response : (stop == StopReason.Cancelled ? "(cancelled)" : "(no response)"),
                ToolsUsed = turn.ToolsUsed.Distinct().ToArray()
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not persist ACP turn for session {SessionId}", session.Id);
        }
    }

    // ---------------------------------------------------------------- plain messages

    private Task RunPlainAsync(AcpTurn turn, AcpSession session, string text) =>
        session.ModeId switch
        {
            AcpModes.Capture => RememberAsync(turn, session, text),
            AcpModes.Graph => ExploreAsync(turn, session, text),
            _ => RecallAsync(turn, session, text)
        };

    private async Task RecallAsync(AcpTurn turn, AcpSession session, string query)
    {
        var token = turn.Token;
        await turn.PlanAsync("Search the knowledge graph", "Search past conversations", "Compose an answer").ConfigureAwait(false);

        await turn.StepAsync(0, PlanEntryStatus.InProgress).ConfigureAwait(false);
        var nodes = new List<GraphNode>();
        var graphTool = await turn.StartToolAsync($"Search graph “{session.Graph}”", ToolKind.Search, "graph.search", RawInput(("graph", session.Graph), ("query", query))).ConfigureAwait(false);
        try
        {
            await graphTool.RunningAsync().ConfigureAwait(false);
            nodes = await SearchNodesAsync(session.Graph, query, _options.MaxRecallResults, token).ConfigureAwait(false);
            await graphTool.CompleteAsync($"{nodes.Count} matching node(s)", RawOutput(nodes.Select(Project))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await graphTool.FailAsync(ex.Message).ConfigureAwait(false);
        }

        await turn.StepAsync(0, PlanEntryStatus.Completed).ConfigureAwait(false);

        await turn.StepAsync(1, PlanEntryStatus.InProgress).ConfigureAwait(false);
        var turns = new List<ConversationTurn>();
        var convTool = await turn.StartToolAsync("Search conversation history", ToolKind.Search, "conversations.search", RawInput(("query", query))).ConfigureAwait(false);
        try
        {
            await convTool.RunningAsync().ConfigureAwait(false);
            turns = await SearchTurnsAsync(query, _options.MaxRecallResults, token).ConfigureAwait(false);
            await convTool.CompleteAsync($"{turns.Count} matching turn(s)", RawOutput(turns.Select(t => new { t.ConversationId, t.TurnNumber, t.UtcTimestamp }))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await convTool.FailAsync(ex.Message).ConfigureAwait(false);
        }

        await turn.StepAsync(1, PlanEntryStatus.Completed).ConfigureAwait(false);
        await turn.StepAsync(2, PlanEntryStatus.InProgress).ConfigureAwait(false);

        if (nodes.Count == 0 && turns.Count == 0)
        {
            await turn.MessageAsync($"I don't have anything stored about “{Snippet(query, 80)}” yet. Use `/remember <text>` to teach me, or `/import` to load a memory schema.\n").ConfigureAwait(false);
        }
        else
        {
            var sb = new StringBuilder("Here is what I remember.\n\n");
            if (nodes.Count > 0)
            {
                sb.Append($"**Knowledge graph `{session.Graph}`**\n");
                foreach (var node in nodes)
                {
                    sb.Append(DescribeNode(node));
                }

                sb.Append('\n');
            }

            if (turns.Count > 0)
            {
                sb.Append("**Past conversations**\n");
                foreach (var t in turns)
                {
                    sb.Append($"- `{t.ConversationId}` #{t.TurnNumber} ({t.UtcTimestamp:yyyy-MM-dd}): “{Snippet(t.Prompt, 100)}” → {Snippet(t.Response, 160)}\n");
                }
            }

            await turn.MessageAsync(sb.ToString()).ConfigureAwait(false);
        }

        await turn.StepAsync(2, PlanEntryStatus.Completed).ConfigureAwait(false);
    }

    private async Task ExploreAsync(AcpTurn turn, AcpSession session, string query)
    {
        var tool = await turn.StartToolAsync($"Explore graph “{session.Graph}”", ToolKind.Search, "graph.explore", RawInput(("graph", session.Graph), ("query", query))).ConfigureAwait(false);
        await tool.RunningAsync().ConfigureAwait(false);
        var nodes = await SearchNodesAsync(session.Graph, query, _options.MaxRecallResults, turn.Token).ConfigureAwait(false);
        if (nodes.Count == 0)
        {
            await tool.CompleteAsync("No matching nodes", null).ConfigureAwait(false);
            await turn.MessageAsync($"No nodes in `{session.Graph}` match “{Snippet(query, 80)}”.\n").ConfigureAwait(false);
            return;
        }

        var hood = await Graph.NeighborhoodAsync(session.Graph, nodes[0].Id, 1, turn.Token).ConfigureAwait(false);
        await tool.CompleteAsync($"{nodes.Count} node(s); neighborhood of `{nodes[0].Id}` has {hood?.Edges.Count ?? 0} edge(s)", RawOutput(nodes.Select(Project))).ConfigureAwait(false);

        var sb = new StringBuilder($"Top matches in `{session.Graph}`:\n");
        foreach (var node in nodes)
        {
            sb.Append(DescribeNode(node));
        }

        if (hood is not null && hood.Edges.Count > 0)
        {
            var names = hood.Nodes.Append(hood.Root).GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First().Name);
            sb.Append($"\nNeighborhood of **{hood.Root.Name}**:\n");
            foreach (var edge in hood.Edges.Take(25))
            {
                names.TryGetValue(edge.SourceId, out var src);
                names.TryGetValue(edge.TargetId, out var dst);
                sb.Append($"- {src ?? edge.SourceId} —{edge.Type}→ {dst ?? edge.TargetId}{(edge.Fact is null ? string.Empty : $" ({Snippet(edge.Fact, 100)})")}\n");
            }
        }

        await turn.MessageAsync(sb.ToString()).ConfigureAwait(false);
    }

    private async Task<List<GraphNode>> SearchNodesAsync(string graph, string query, int limit, CancellationToken ct)
    {
        var found = (await Graph.SearchAsync(graph, new GraphQuery { Text = query, Limit = limit }, ct).ConfigureAwait(false)).ToList();
        if (found.Count == 0)
        {
            foreach (var keyword in Keywords(query).Take(4))
            {
                foreach (var node in await Graph.SearchAsync(graph, new GraphQuery { Text = keyword, Limit = limit }, ct).ConfigureAwait(false))
                {
                    if (found.All(f => f.Id != node.Id))
                    {
                        found.Add(node);
                    }
                }

                if (found.Count >= limit)
                {
                    break;
                }
            }
        }

        return found.Take(limit).ToList();
    }

    private async Task<List<ConversationTurn>> SearchTurnsAsync(string query, int limit, CancellationToken ct)
    {
        async Task<IReadOnlyList<ConversationTurn>> Search(string text) =>
            (await Conversations.SearchConversationsAsync(new ConversationSearchRequest { SearchText = text, Take = limit }, ct).ConfigureAwait(false)).Results;

        var found = (await Search(query).ConfigureAwait(false)).ToList();
        if (found.Count == 0)
        {
            foreach (var keyword in Keywords(query).Take(4))
            {
                foreach (var t in await Search(keyword).ConfigureAwait(false))
                {
                    if (found.All(f => f.ConversationId != t.ConversationId || f.TurnNumber != t.TurnNumber))
                    {
                        found.Add(t);
                    }
                }

                if (found.Count >= limit)
                {
                    break;
                }
            }
        }

        return found.Take(limit).ToList();
    }

    private static IEnumerable<string> Keywords(string text) =>
        Regex.Split(text, @"[^\p{L}\p{N}_-]+")
            .Where(w => w.Length >= 3 && !StopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(w => w.Length);

    // ---------------------------------------------------------------- documents

    private async Task IngestDocumentsAsync(AcpTurn turn, AcpSession session, List<EmbeddedResourceContent> documents)
    {
        foreach (var document in documents)
        {
            var resource = document.Resource;
            var name = DocumentName(resource.Uri);
            var tool = await turn.StartToolAsync($"Ingest document {name}", ToolKind.Edit, "graph.remember", RawInput(("graph", session.Graph), ("uri", resource.Uri)), new[] { LocationFor(resource.Uri) }).ConfigureAwait(false);
            if (resource.Text is null)
            {
                await tool.FailAsync("Binary resources are not supported.").ConfigureAwait(false);
                await turn.MessageAsync($"Skipped `{name}`: only text resources can be stored.\n\n").ConfigureAwait(false);
                continue;
            }

            await tool.RunningAsync().ConfigureAwait(false);
            var node = await Graph.RememberAsync(session.Graph, resource.Text, name, "document", null, new[] { "acp", "document" }, turn.Token).ConfigureAwait(false);
            await tool.CompleteAsync($"Stored as node {node.Id}", RawOutput(Project(node))).ConfigureAwait(false);
            await turn.MessageAsync($"Stored document `{name}` as node `{node.Id}`.\n\n").ConfigureAwait(false);
        }
    }

    private static string DocumentName(string uri)
    {
        var trimmed = uri.TrimEnd('/', '\\');
        var index = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        return index >= 0 && index < trimmed.Length - 1 ? trimmed[(index + 1)..] : trimmed;
    }

    private static ToolCallLocation LocationFor(string uriOrPath) =>
        new() { Path = Uri.TryCreate(uriOrPath, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : uriOrPath };

    // ---------------------------------------------------------------- slash commands

    private async Task RunCommandAsync(AcpTurn turn, AcpSession session, string name, string args)
    {
        switch (name)
        {
            case "help":
                await HelpAsync(turn).ConfigureAwait(false);
                break;
            case "remember":
                if (args.Length == 0)
                {
                    await turn.MessageAsync("Usage: `/remember <text>`\n").ConfigureAwait(false);
                    break;
                }

                await RememberAsync(turn, session, args).ConfigureAwait(false);
                break;
            case "recall":
                if (args.Length == 0)
                {
                    await turn.MessageAsync("Usage: `/recall <query>`\n").ConfigureAwait(false);
                    break;
                }

                await RecallAsync(turn, session, args).ConfigureAwait(false);
                break;
            case "forget":
                await ForgetAsync(turn, session, args).ConfigureAwait(false);
                break;
            case "relate":
                await RelateAsync(turn, session, args).ConfigureAwait(false);
                break;
            case "graph":
                await GraphCommandAsync(turn, session, args).ConfigureAwait(false);
                break;
            case "schemas":
                await SchemasAsync(turn).ConfigureAwait(false);
                break;
            case "import":
                await ImportAsync(turn, session, args).ConfigureAwait(false);
                break;
            case "export":
                await ExportAsync(turn, session, args).ConfigureAwait(false);
                break;
            case "workspace":
                await WorkspaceAsync(turn, session, args).ConfigureAwait(false);
                break;
            default:
                await turn.MessageAsync($"Unknown command `/{name}`.\n\n").ConfigureAwait(false);
                await HelpAsync(turn).ConfigureAwait(false);
                break;
        }
    }

    private static Task HelpAsync(AcpTurn turn)
    {
        var sb = new StringBuilder("**Darbot Memory commands**\n\n");
        foreach (var command in Commands)
        {
            sb.Append($"- `/{command.Name}{(command.Input is null ? string.Empty : " " + command.Input.Hint)}` — {command.Description}\n");
        }

        sb.Append("\nPlain messages are handled by the current mode (recall, capture or graph).\n");
        return turn.MessageAsync(sb.ToString());
    }

    private async Task RememberAsync(AcpTurn turn, AcpSession session, string text)
    {
        var tool = await turn.StartToolAsync("Remember", ToolKind.Edit, "graph.remember", RawInput(("graph", session.Graph), ("text", text))).ConfigureAwait(false);
        await tool.RunningAsync().ConfigureAwait(false);
        var node = await Graph.RememberAsync(session.Graph, text, null, null, null, null, turn.Token).ConfigureAwait(false);
        await tool.CompleteAsync($"Remembered as node {node.Id}", RawOutput(Project(node))).ConfigureAwait(false);
        await turn.MessageAsync($"Remembered in `{session.Graph}` as **{node.Name}** (`{node.Id}`).\n").ConfigureAwait(false);
    }

    private async Task ForgetAsync(AcpTurn turn, AcpSession session, string args)
    {
        var nodeId = args.Trim();
        if (nodeId.Length == 0)
        {
            await turn.MessageAsync("Usage: `/forget <nodeId>`\n").ConfigureAwait(false);
            return;
        }

        await turn.PlanAsync("Confirm deletion", "Delete node").ConfigureAwait(false);
        var tool = await turn.StartToolAsync($"Forget node {nodeId}", ToolKind.Delete, "graph.forget", RawInput(("graph", session.Graph), ("nodeId", nodeId))).ConfigureAwait(false);

        await turn.StepAsync(0, PlanEntryStatus.InProgress).ConfigureAwait(false);
        var allowed = await ConfirmAsync(turn, session, tool, "forget", $"Delete node `{nodeId}` from graph `{session.Graph}`?").ConfigureAwait(false);
        await turn.StepAsync(0, PlanEntryStatus.Completed).ConfigureAwait(false);
        if (!allowed)
        {
            await tool.FailAsync("Rejected by the user.").ConfigureAwait(false);
            await turn.StepAsync(1, PlanEntryStatus.Completed).ConfigureAwait(false);
            await turn.MessageAsync($"Okay, I kept node `{nodeId}`.\n").ConfigureAwait(false);
            return;
        }

        await turn.StepAsync(1, PlanEntryStatus.InProgress).ConfigureAwait(false);
        await tool.RunningAsync().ConfigureAwait(false);
        var removed = await Graph.ForgetAsync(session.Graph, nodeId, turn.Token).ConfigureAwait(false);
        if (removed)
        {
            await tool.CompleteAsync($"Forgot node {nodeId}", RawOutput(new { forgotten = true })).ConfigureAwait(false);
            await turn.MessageAsync($"Forgot node `{nodeId}`.\n").ConfigureAwait(false);
        }
        else
        {
            await tool.FailAsync($"Node {nodeId} was not found.").ConfigureAwait(false);
            await turn.MessageAsync($"I couldn't find node `{nodeId}` in `{session.Graph}`.\n").ConfigureAwait(false);
        }

        await turn.StepAsync(1, PlanEntryStatus.Completed).ConfigureAwait(false);
    }

    private async Task<bool> ConfirmAsync(AcpTurn turn, AcpSession session, AcpTurn.ToolHandle tool, string operation, string question)
    {
        if (session.RememberedChoice(operation) is { } remembered)
        {
            return remembered;
        }

        var options = new[]
        {
            new PermissionOption { OptionId = "allow", Name = "Allow", Kind = PermissionOptionKind.AllowOnce },
            new PermissionOption { OptionId = "allow_always", Name = $"Always allow ({operation})", Kind = PermissionOptionKind.AllowAlways },
            new PermissionOption { OptionId = "reject", Name = "Reject", Kind = PermissionOptionKind.RejectOnce },
            new PermissionOption { OptionId = "reject_always", Name = $"Always reject ({operation})", Kind = PermissionOptionKind.RejectAlways }
        };

        PermissionDecision decision;
        try
        {
            decision = await Client.RequestPermissionAsync(session.Id, tool.ToRef(question), options, turn.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or TimeoutException)
        {
            _logger.LogWarning(ex, "Permission request failed; treating as rejected");
            return false;
        }

        turn.Token.ThrowIfCancellationRequested();
        if (decision.Kind == PermissionOptionKind.AllowAlways)
        {
            session.RememberChoice(operation, true);
        }
        else if (decision.Kind == PermissionOptionKind.RejectAlways)
        {
            session.RememberChoice(operation, false);
        }

        return decision.Allowed;
    }

    private async Task RelateAsync(AcpTurn turn, AcpSession session, string args)
    {
        var parts = Regex.Split(args, @"\s+", RegexOptions.None).Where(p => p.Length > 0).ToArray();
        if (parts.Length < 3)
        {
            await turn.MessageAsync("Usage: `/relate <source id> <type> <target id> [fact]`\n").ConfigureAwait(false);
            return;
        }

        var fact = parts.Length > 3 ? string.Join(' ', parts.Skip(3)) : null;
        var tool = await turn.StartToolAsync($"Relate {parts[0]} → {parts[2]}", ToolKind.Edit, "graph.relate", RawInput(("graph", session.Graph), ("source", parts[0]), ("type", parts[1]), ("target", parts[2]))).ConfigureAwait(false);
        await tool.RunningAsync().ConfigureAwait(false);
        var edge = await Graph.RelateAsync(session.Graph, parts[0], parts[2], parts[1], fact, turn.Token).ConfigureAwait(false);
        await tool.CompleteAsync($"Created edge {edge.Id}", RawOutput(edge)).ConfigureAwait(false);
        await turn.MessageAsync($"Linked `{edge.SourceId}` —{edge.Type}→ `{edge.TargetId}` (edge `{edge.Id}`).\n").ConfigureAwait(false);
    }

    private async Task GraphCommandAsync(AcpTurn turn, AcpSession session, string args)
    {
        if (args.StartsWith("use ", StringComparison.OrdinalIgnoreCase))
        {
            var target = args[4..].Trim();
            if (target.Length == 0)
            {
                await turn.MessageAsync("Usage: `/graph use <name>`\n").ConfigureAwait(false);
                return;
            }

            session.Graph = target;
            await turn.MessageAsync($"Now using graph `{target}`.\n").ConfigureAwait(false);
            return;
        }

        var tool = await turn.StartToolAsync(args.Length == 0 ? "List graphs" : $"Read graph {args}", ToolKind.Read, "graph.get", RawInput(("graph", args))).ConfigureAwait(false);
        await tool.RunningAsync().ConfigureAwait(false);
        if (args.Length == 0)
        {
            var graphs = await Graph.ListGraphsAsync(turn.Token).ConfigureAwait(false);
            await tool.CompleteAsync($"{graphs.Count} graph(s)", RawOutput(graphs.Select(g => new { g.Name, nodes = g.Nodes.Count, edges = g.Edges.Count }))).ConfigureAwait(false);
            var sb = new StringBuilder(graphs.Count == 0 ? "No graphs yet. `/remember` something to create one.\n" : "**Graphs**\n");
            foreach (var g in graphs)
            {
                sb.Append($"- `{g.Name}` — {g.Nodes.Count} nodes, {g.Edges.Count} edges (updated {g.UpdatedAt:yyyy-MM-dd HH:mm} UTC){(g.Name == session.Graph ? " ← current" : string.Empty)}\n");
            }

            await turn.MessageAsync(sb.ToString()).ConfigureAwait(false);
            return;
        }

        var graph = await Graph.GetGraphAsync(args, turn.Token).ConfigureAwait(false);
        if (graph is null)
        {
            await tool.FailAsync($"Graph {args} was not found.").ConfigureAwait(false);
            await turn.MessageAsync($"Graph `{args}` does not exist.\n").ConfigureAwait(false);
            return;
        }

        await tool.CompleteAsync($"{graph.Nodes.Count} nodes, {graph.Edges.Count} edges", null).ConfigureAwait(false);
        var summary = new StringBuilder($"**Graph `{graph.Name}`** — {graph.Nodes.Count} nodes, {graph.Edges.Count} edges, {graph.Clusters.Count} clusters\n");
        var types = graph.Nodes.GroupBy(n => n.Type).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}: {g.Count()}");
        summary.Append("Types: ").Append(string.Join(", ", types)).Append("\n\n");
        foreach (var node in graph.Nodes.Take(10))
        {
            summary.Append(DescribeNode(node));
        }

        if (graph.Nodes.Count > 10)
        {
            summary.Append($"…and {graph.Nodes.Count - 10} more.\n");
        }

        await turn.MessageAsync(summary.ToString()).ConfigureAwait(false);
    }

    private async Task SchemasAsync(AcpTurn turn)
    {
        var tool = await turn.StartToolAsync("List memory schemas", ToolKind.Read, "graph.schemas", null).ConfigureAwait(false);
        var schemas = Graph.Schemas;
        await tool.CompleteAsync($"{schemas.Count} schema(s)", RawOutput(schemas.Select(s => new { s.Name, s.CanImport, s.CanExport }))).ConfigureAwait(false);
        var sb = new StringBuilder("**Memory schemas**\n");
        foreach (var s in schemas)
        {
            var abilities = string.Join('/', new[] { s.CanImport ? "import" : null, s.CanExport ? "export" : null }.Where(a => a is not null));
            sb.Append($"- `{s.Name}` — {s.DisplayName}: {s.Description} ({abilities})\n");
        }

        await turn.MessageAsync(sb.ToString()).ConfigureAwait(false);
    }

    private async Task ImportAsync(AcpTurn turn, AcpSession session, string args)
    {
        var match = Regex.Match(args, @"^(\S+)\s+(.+)$", RegexOptions.Singleline);
        if (!match.Success)
        {
            await turn.MessageAsync("Usage: `/import <schema> <path-or-inline> [--graph name] [--replace]`\n").ConfigureAwait(false);
            return;
        }

        var schema = match.Groups[1].Value;
        var source = match.Groups[2].Value.Trim();
        var graphName = session.Graph;
        var replace = false;
        var graphFlag = Regex.Match(source, @"\s+--graph\s+(\S+)\s*$");
        if (graphFlag.Success)
        {
            graphName = graphFlag.Groups[1].Value;
            source = source[..graphFlag.Index].TrimEnd();
        }

        var replaceFlag = Regex.Match(source, @"\s+--replace\s*$");
        if (replaceFlag.Success)
        {
            replace = true;
            source = source[..replaceFlag.Index].TrimEnd();
        }

        var adapter = Graph.Schemas.FirstOrDefault(s => string.Equals(s.Name, schema, StringComparison.OrdinalIgnoreCase));
        if (adapter is null || !adapter.CanImport)
        {
            await turn.MessageAsync($"`{schema}` is not an importable schema. Run `/schemas` to see the options.\n").ConfigureAwait(false);
            return;
        }

        await turn.PlanAsync("Read the source", "Convert and store the graph", "Summarize the result").ConfigureAwait(false);
        await turn.StepAsync(0, PlanEntryStatus.InProgress).ConfigureAwait(false);

        string payload;
        var inline = source.StartsWith('{') || source.StartsWith('[') || source.Contains('\n');
        if (inline)
        {
            payload = source;
        }
        else
        {
            var path = ResolveClientPath(session.Cwd, source.Trim('"'));
            var readTool = await turn.StartToolAsync($"Read {path}", ToolKind.Read, "fs.read_text_file", RawInput(("path", path)), new[] { new ToolCallLocation { Path = path } }).ConfigureAwait(false);
            if (!Client.CanReadFiles)
            {
                await readTool.FailAsync("The client does not support fs/read_text_file.").ConfigureAwait(false);
                await turn.MessageAsync("Your client didn't advertise file system access, so I can't read files. Paste the payload inline instead.\n").ConfigureAwait(false);
                return;
            }

            await readTool.RunningAsync().ConfigureAwait(false);
            try
            {
                payload = await Client.ReadTextFileAsync(session.Id, path, cancellationToken: turn.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonRpcRemoteException or TimeoutException)
            {
                await readTool.FailAsync(ex.Message).ConfigureAwait(false);
                await turn.MessageAsync($"Could not read `{path}`: {ex.Message}\n").ConfigureAwait(false);
                return;
            }

            await readTool.CompleteAsync($"Read {payload.Length} characters", null).ConfigureAwait(false);
        }

        await turn.StepAsync(0, PlanEntryStatus.Completed).ConfigureAwait(false);
        await turn.StepAsync(1, PlanEntryStatus.InProgress).ConfigureAwait(false);

        var tool = await turn.StartToolAsync($"Import {adapter.DisplayName} into {graphName}", ToolKind.Edit, "graph.import", RawInput(("schema", adapter.Name), ("graph", graphName), ("merge", (!replace).ToString()))).ConfigureAwait(false);
        if (replace && !await ConfirmAsync(turn, session, tool, "import-replace", $"Replace the contents of graph `{graphName}` with this import?").ConfigureAwait(false))
        {
            await tool.FailAsync("Rejected by the user.").ConfigureAwait(false);
            await turn.MessageAsync("Import cancelled; nothing was changed.\n").ConfigureAwait(false);
            return;
        }

        await tool.RunningAsync().ConfigureAwait(false);
        var graph = await Graph.ImportAsync(adapter.Name, payload, graphName, !replace, turn.Token).ConfigureAwait(false);
        await tool.CompleteAsync($"{graph.Nodes.Count} nodes, {graph.Edges.Count} edges", RawOutput(new { graph = graph.Name, nodes = graph.Nodes.Count, edges = graph.Edges.Count })).ConfigureAwait(false);
        await turn.StepAsync(1, PlanEntryStatus.Completed).ConfigureAwait(false);
        await turn.StepAsync(2, PlanEntryStatus.InProgress).ConfigureAwait(false);
        await turn.MessageAsync($"Imported **{adapter.DisplayName}** into `{graphName}` ({(replace ? "replaced" : "merged")}): {graph.Nodes.Count} nodes, {graph.Edges.Count} edges now in the graph.\n").ConfigureAwait(false);
        await turn.StepAsync(2, PlanEntryStatus.Completed).ConfigureAwait(false);
    }

    private async Task ExportAsync(AcpTurn turn, AcpSession session, string args)
    {
        var parts = Regex.Split(args, @"\s+").Where(p => p.Length > 0).ToList();
        string? target = null;
        var toIndex = parts.FindIndex(p => p == "--to");
        if (toIndex >= 0)
        {
            target = toIndex + 1 < parts.Count ? parts[toIndex + 1].Trim('"') : null;
            if (target is null)
            {
                await turn.MessageAsync("`--to` needs a path.\n").ConfigureAwait(false);
                return;
            }

            parts.RemoveRange(toIndex, 2);
        }

        if (parts.Count == 0)
        {
            await turn.MessageAsync("Usage: `/export <schema> [graph] [--to path]`\n").ConfigureAwait(false);
            return;
        }

        var adapter = Graph.Schemas.FirstOrDefault(s => string.Equals(s.Name, parts[0], StringComparison.OrdinalIgnoreCase));
        if (adapter is null || !adapter.CanExport)
        {
            await turn.MessageAsync($"`{parts[0]}` is not an exportable schema. Run `/schemas` to see the options.\n").ConfigureAwait(false);
            return;
        }

        var graphName = parts.Count > 1 ? parts[1] : session.Graph;
        await turn.PlanAsync("Export the graph", target is null ? "Show the result" : "Write the file(s)").ConfigureAwait(false);
        await turn.StepAsync(0, PlanEntryStatus.InProgress).ConfigureAwait(false);
        var tool = await turn.StartToolAsync($"Export {graphName} as {adapter.DisplayName}", ToolKind.Read, "graph.export", RawInput(("schema", adapter.Name), ("graph", graphName))).ConfigureAwait(false);
        await tool.RunningAsync().ConfigureAwait(false);
        var result = await Graph.ExportAsync(adapter.Name, graphName, turn.Token).ConfigureAwait(false);
        await tool.CompleteAsync(result.Files is null ? $"{result.Content?.Length ?? 0} characters ({result.ContentType})" : $"{result.Files.Count} file(s)", null).ConfigureAwait(false);
        await turn.StepAsync(0, PlanEntryStatus.Completed).ConfigureAwait(false);
        await turn.StepAsync(1, PlanEntryStatus.InProgress).ConfigureAwait(false);

        var files = result.Files is { Count: > 0 }
            ? result.Files.ToDictionary(f => JoinClientPath(ResolveClientPath(session.Cwd, target ?? string.Empty), f.Key), f => f.Value)
            : new Dictionary<string, string>();
        if (target is not null)
        {
            if (!Client.CanWriteFiles)
            {
                await turn.MessageAsync("Your client didn't advertise file writing (fs/write_text_file), so I can't write files.\n").ConfigureAwait(false);
                return;
            }

            if (result.Files is null)
            {
                files[ResolveClientPath(session.Cwd, target)] = result.Content ?? string.Empty;
            }

            var writeTool = await turn.StartToolAsync($"Write {files.Count} file(s)", ToolKind.Edit, "fs.write_text_file", RawInput(("target", target)), files.Keys.Select(p => new ToolCallLocation { Path = p }).ToList()).ConfigureAwait(false);
            if (!await ConfirmAsync(turn, session, writeTool, "export-write", $"Write {files.Count} file(s) to `{target}`?").ConfigureAwait(false))
            {
                await writeTool.FailAsync("Rejected by the user.").ConfigureAwait(false);
                await turn.MessageAsync("Export not written.\n").ConfigureAwait(false);
                return;
            }

            await writeTool.RunningAsync().ConfigureAwait(false);
            foreach (var (path, content) in files)
            {
                await Client.WriteTextFileAsync(session.Id, path, content, turn.Token).ConfigureAwait(false);
            }

            await writeTool.CompleteAsync($"Wrote {files.Count} file(s)", null, files.Select(f => (ToolCallContent)new DiffToolCallContent { Path = f.Key, NewText = f.Value }).ToList()).ConfigureAwait(false);
            await turn.MessageAsync($"Exported `{graphName}` as {adapter.DisplayName} to {string.Join(", ", files.Keys.Select(k => $"`{k}`"))}.\n").ConfigureAwait(false);
        }
        else if (result.Files is not null)
        {
            await turn.MessageAsync($"Exported `{graphName}` as {adapter.DisplayName}: {result.Files.Count} files ({string.Join(", ", result.Files.Keys.Take(10).Select(k => $"`{k}`"))}). Use `--to <directory>` to write them.\n").ConfigureAwait(false);
        }
        else
        {
            await turn.MessageAsync($"Exported `{graphName}` as {adapter.DisplayName}:\n\n```\n{result.Content}\n```\n").ConfigureAwait(false);
        }

        await turn.StepAsync(1, PlanEntryStatus.Completed).ConfigureAwait(false);
    }

    private async Task WorkspaceAsync(AcpTurn turn, AcpSession session, string args)
    {
        var workspaces = _services.GetService<IWorkspaceService>();
        if (workspaces is null)
        {
            await turn.MessageAsync("Workspace capture is not available on this server.\n").ConfigureAwait(false);
            return;
        }

        var parts = Regex.Split(args, @"\s+", RegexOptions.None, TimeSpan.FromSeconds(1)).Where(p => p.Length > 0).ToArray();
        var action = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        switch (action)
        {
            case "capture":
                {
                    var name = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : $"acp-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                    var tool = await turn.StartToolAsync($"Capture workspace {name}", ToolKind.Other, "workspace.capture", RawInput(("name", name))).ConfigureAwait(false);
                    await tool.RunningAsync().ConfigureAwait(false);
                    var response = await workspaces.CaptureWorkspaceAsync(new CaptureWorkspaceRequest { Name = name }, turn.Token).ConfigureAwait(false);
                    if (response.Success)
                    {
                        await tool.CompleteAsync($"Captured {response.ComponentsCount} component(s) as {response.WorkspaceId}", RawOutput(response)).ConfigureAwait(false);
                        await turn.MessageAsync($"Captured workspace **{name}** (`{response.WorkspaceId}`, {response.ComponentsCount} components).\n").ConfigureAwait(false);
                    }
                    else
                    {
                        await tool.FailAsync(response.Message ?? string.Join("; ", response.Errors)).ConfigureAwait(false);
                        await turn.MessageAsync($"Workspace capture failed: {response.Message ?? string.Join("; ", response.Errors)}\n").ConfigureAwait(false);
                    }

                    break;
                }
            case "list":
                {
                    var tool = await turn.StartToolAsync("List workspaces", ToolKind.Read, "workspace.list", null).ConfigureAwait(false);
                    var response = await workspaces.ListWorkspacesAsync(new ListWorkspacesRequest { Take = 20 }, turn.Token).ConfigureAwait(false);
                    await tool.CompleteAsync($"{response.TotalCount} workspace(s)", RawOutput(response.Workspaces.Select(w => new { w.WorkspaceId, w.Name }))).ConfigureAwait(false);
                    var sb = new StringBuilder(response.Workspaces.Count == 0 ? "No workspaces captured yet.\n" : "**Workspaces**\n");
                    foreach (var w in response.Workspaces)
                    {
                        sb.Append($"- **{w.Name}** (`{w.WorkspaceId}`) — captured {w.CreatedUtc:yyyy-MM-dd HH:mm} UTC on {w.Device.DeviceName}\n");
                    }

                    await turn.MessageAsync(sb.ToString()).ConfigureAwait(false);
                    break;
                }
            case "restore" or "delete" when parts.Length > 1:
                {
                    var id = parts[1];
                    var restore = action == "restore";
                    var tool = await turn.StartToolAsync($"{(restore ? "Restore" : "Delete")} workspace {id}", restore ? ToolKind.Edit : ToolKind.Delete, restore ? "workspace.restore" : "workspace.delete", RawInput(("workspaceId", id))).ConfigureAwait(false);
                    if (!await ConfirmAsync(turn, session, tool, $"workspace-{action}", $"{(restore ? "Restore" : "Permanently delete")} workspace `{id}`?").ConfigureAwait(false))
                    {
                        await tool.FailAsync("Rejected by the user.").ConfigureAwait(false);
                        await turn.MessageAsync("Okay, nothing was changed.\n").ConfigureAwait(false);
                        break;
                    }

                    await tool.RunningAsync().ConfigureAwait(false);
                    bool ok;
                    string? message = null;
                    if (restore)
                    {
                        var r = await workspaces.RestoreWorkspaceAsync(new RestoreWorkspaceRequest { WorkspaceId = id }, turn.Token).ConfigureAwait(false);
                        ok = r.Success;
                        message = r.Message;
                    }
                    else
                    {
                        ok = await workspaces.DeleteWorkspaceAsync(id, turn.Token).ConfigureAwait(false);
                    }

                    if (ok)
                    {
                        await tool.CompleteAsync($"{(restore ? "Restored" : "Deleted")} {id}", null).ConfigureAwait(false);
                        await turn.MessageAsync($"{(restore ? "Restored" : "Deleted")} workspace `{id}`.\n").ConfigureAwait(false);
                    }
                    else
                    {
                        await tool.FailAsync(message ?? "Operation failed.").ConfigureAwait(false);
                        await turn.MessageAsync($"Could not {action} workspace `{id}`{(message is null ? string.Empty : ": " + message)}.\n").ConfigureAwait(false);
                    }

                    break;
                }
            default:
                await turn.MessageAsync("Usage: `/workspace capture [name]`, `/workspace list`, `/workspace restore <id>` or `/workspace delete <id>`\n").ConfigureAwait(false);
                break;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string ResolveClientPath(string cwd, string path)
    {
        if (path.Length == 0)
        {
            return cwd;
        }

        if (IsAbsolutePath(path))
        {
            return path;
        }

        return JoinClientPath(cwd, path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith(".\\", StringComparison.Ordinal) ? path[2..] : path);
    }

    private static string JoinClientPath(string baseDir, string relative)
    {
        var separator = baseDir.Contains('\\') && !baseDir.Contains('/') ? '\\' : '/';
        return baseDir.TrimEnd('/', '\\') + separator + relative.Replace('/', separator).Replace('\\', separator).TrimStart(separator);
    }

    private static GraphNode Project(GraphNode node) => node with { Embedding = null };

    private static JsonNode? RawInput(params (string Key, string? Value)[] values)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in values)
        {
            obj[key] = value;
        }

        return obj;
    }

    private static JsonNode? RawOutput(object value) => JsonSerializer.SerializeToNode(value, value.GetType(), GraphJson.Options);

    private static string DescribeNode(GraphNode node)
    {
        var sb = new StringBuilder($"- **{node.Name}** ({node.Type}, `{node.Id}`)");
        if (!string.IsNullOrWhiteSpace(node.Description))
        {
            sb.Append(" — ").Append(Snippet(node.Description, 160));
        }

        sb.Append('\n');
        foreach (var observation in node.Observations.Take(3))
        {
            sb.Append("  - ").Append(Snippet(observation, 160)).Append('\n');
        }

        if (node.Observations.Count == 0 && !string.IsNullOrWhiteSpace(node.Content))
        {
            sb.Append("  > ").Append(Snippet(node.Content, 200)).Append('\n');
        }

        return sb.ToString();
    }
}

/// <summary>Per-prompt emitter: streams message chunks, plan and tool call updates and records what was said.</summary>
internal sealed class AcpTurn
{
    private readonly AcpClient _client;
    private readonly AcpSession _session;
    private readonly StringBuilder _response = new();
    private readonly List<ToolHandle> _open = new();
    private readonly string _messageId = Guid.NewGuid().ToString("N");
    private List<PlanEntry> _plan = new();
    private int _toolCounter;

    public AcpTurn(AcpClient client, AcpSession session, CancellationToken token)
    {
        _client = client;
        _session = session;
        Token = token;
    }

    public CancellationToken Token { get; }
    public string Response => _response.ToString();
    public List<string> ToolsUsed { get; } = new();

    // Updates are never tied to the turn token so that in-flight output is still delivered when a turn is cancelled.
    private Task SendAsync(SessionUpdate update) => _client.SendUpdateAsync(_session.Id, update, CancellationToken.None);

    public async Task MessageAsync(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        _response.Append(text);
        var position = 0;
        while (position < text.Length)
        {
            var end = Math.Min(text.Length, position + 1500);
            var newline = end < text.Length ? text.LastIndexOf('\n', end - 1, end - position) : -1;
            if (newline >= position)
            {
                end = newline + 1;
            }

            await SendAsync(new AgentMessageChunk { Content = ContentBlock.FromText(text[position..end]), MessageId = _messageId }).ConfigureAwait(false);
            position = end;
        }
    }

    public Task PlanAsync(params string[] steps)
    {
        _plan = steps.Select(s => new PlanEntry { Content = s }).ToList();
        return SendAsync(new PlanUpdate { Entries = _plan.ToList() });
    }

    public Task StepAsync(int index, PlanEntryStatus status)
    {
        if (index < 0 || index >= _plan.Count)
        {
            return Task.CompletedTask;
        }

        _plan[index] = _plan[index] with { Status = status };
        return SendAsync(new PlanUpdate { Entries = _plan.ToList() });
    }

    public async Task<ToolHandle> StartToolAsync(string title, ToolKind kind, string name, JsonNode? rawInput, IReadOnlyList<ToolCallLocation>? locations = null)
    {
        var handle = new ToolHandle(this, $"call_{_session.TurnCount + 1}_{Interlocked.Increment(ref _toolCounter)}", title, kind, rawInput, locations?.ToList());
        ToolsUsed.Add(name);
        _open.Add(handle);
        await SendAsync(new ToolCallUpdateStart
        {
            ToolCallId = handle.Id,
            Title = title,
            Name = name,
            Kind = kind,
            Status = ToolCallStatus.Pending,
            RawInput = rawInput?.DeepClone(),
            Locations = handle.Locations
        }).ConfigureAwait(false);
        return handle;
    }

    public async Task AbortOpenToolsAsync(string reason)
    {
        foreach (var tool in _open.ToArray())
        {
            try
            {
                await tool.FailAsync(reason).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonRpcConnectionClosedException)
            {
                return;
            }
        }
    }

    internal sealed class ToolHandle
    {
        private readonly AcpTurn _turn;
        private readonly JsonNode? _rawInput;

        public ToolHandle(AcpTurn turn, string id, string title, ToolKind kind, JsonNode? rawInput, List<ToolCallLocation>? locations)
        {
            _turn = turn;
            Id = id;
            Title = title;
            Kind = kind;
            _rawInput = rawInput;
            Locations = locations;
        }

        public string Id { get; }
        public string Title { get; }
        public ToolKind Kind { get; }
        public List<ToolCallLocation>? Locations { get; }
        public bool Finished { get; private set; }

        public ToolCallRef ToRef(string description) => new()
        {
            ToolCallId = Id,
            Title = Title,
            Kind = Kind,
            Status = ToolCallStatus.Pending,
            Content = new List<ToolCallContent> { ToolCallContent.Text(description) },
            Locations = Locations,
            RawInput = _rawInput?.DeepClone()
        };

        public Task RunningAsync() =>
            _turn.SendAsync(new ToolCallUpdateProgress { ToolCallId = Id, Status = ToolCallStatus.InProgress });

        public Task CompleteAsync(string? summary, JsonNode? rawOutput, List<ToolCallContent>? content = null) =>
            FinishAsync(ToolCallStatus.Completed, summary, rawOutput, content);

        public Task FailAsync(string message) => FinishAsync(ToolCallStatus.Failed, message, null, null);

        private async Task FinishAsync(ToolCallStatus status, string? summary, JsonNode? rawOutput, List<ToolCallContent>? content)
        {
            if (Finished)
            {
                return;
            }

            Finished = true;
            _turn._open.Remove(this);
            await _turn.SendAsync(new ToolCallUpdateProgress
            {
                ToolCallId = Id,
                Status = status,
                Content = content ?? (summary is null ? null : new List<ToolCallContent> { ToolCallContent.Text(summary) }),
                RawOutput = rawOutput
            }).ConfigureAwait(false);
        }
    }
}

/// <summary>Runs an <see cref="AcpAgent"/> over a connection until the client disconnects.</summary>
public static class AcpAgentHost
{
    public static async Task RunAsync(
        IAcpConnection connection,
        IServiceProvider services,
        AcpSessionStore sessions,
        AcpOptions options,
        ILogger? logger = null,
        bool preAuthenticated = false,
        CancellationToken cancellationToken = default)
    {
        var agent = new AcpAgent(services, sessions, options, logger, preAuthenticated);
        await using var peer = new JsonRpcPeer(connection, agent, options.MaxMessageBytes, logger);
        agent.Attach(peer);
        await peer.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}


