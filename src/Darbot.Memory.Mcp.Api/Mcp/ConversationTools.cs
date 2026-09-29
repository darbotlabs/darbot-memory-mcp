using System.ComponentModel;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Darbot.Memory.Mcp.Api.Mcp;

[McpServerToolType]
public sealed class ConversationTools(IConversationService conversations, IServiceProvider services)
{
    [McpServerTool(Name = "memory_write_turn", Title = "Write conversation turn", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Persist one conversation turn (prompt and response) to the audit trail as a Markdown file with an integrity hash.")]
    public async Task<string> WriteTurn(
        [Description("Identifier of the conversation this turn belongs to.")] string conversationId,
        [Description("The user prompt for this turn.")] string prompt,
        [Description("The assistant response for this turn.")] string response,
        [Description("Name of the model that produced the response.")] string model = "mcp-client",
        [Description("Turn number within the conversation. Omit to append after the last stored turn.")] int? turnNumber = null,
        [Description("Names of tools that were used during this turn.")] string[]? toolsUsed = null,
        CancellationToken cancellationToken = default)
    {
        var number = turnNumber;
        if (number is null)
        {
            var existing = await conversations.GetConversationAsync(conversationId, cancellationToken);
            number = existing.Count == 0 ? 1 : existing.Max(t => t.TurnNumber) + 1;
        }

        var ok = await conversations.PersistTurnAsync(new ConversationTurn
        {
            ConversationId = conversationId,
            TurnNumber = number.Value,
            UtcTimestamp = DateTime.UtcNow,
            Prompt = prompt,
            Model = model,
            Response = response,
            ToolsUsed = toolsUsed ?? Array.Empty<string>()
        }, cancellationToken);

        return ok
            ? McpJson.Serialize(new { success = true, conversationId, turnNumber = number.Value })
            : throw new McpException($"Failed to persist turn {number} of conversation '{conversationId}'.");
    }

    [McpServerTool(Name = "memory_search_conversations", Title = "Search conversations", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Search stored conversation turns by text, conversation, model or date range.")]
    public async Task<string> SearchConversations(
        [Description("Text to look for in prompts and responses.")] string? searchText = null,
        [Description("Restrict to a single conversation id.")] string? conversationId = null,
        [Description("Restrict to turns produced by this model.")] string? model = null,
        [Description("Only turns at or after this UTC date-time (ISO 8601).")] DateTime? fromDate = null,
        [Description("Only turns at or before this UTC date-time (ISO 8601).")] DateTime? toDate = null,
        [Description("Number of results to skip.")] int skip = 0,
        [Description("Maximum number of results to return (1-200).")] int take = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await conversations.SearchConversationsAsync(new ConversationSearchRequest
        {
            SearchText = searchText,
            ConversationId = conversationId,
            Model = model,
            FromDate = fromDate,
            ToDate = toDate,
            Skip = Math.Max(0, skip),
            Take = Math.Clamp(take, 1, 200)
        }, cancellationToken);
        return McpJson.Serialize(result);
    }

    [McpServerTool(Name = "memory_list_conversations", Title = "List conversations", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List stored conversations with turn counts, models, tools and last activity.")]
    public async Task<string> ListConversations(
        [Description("Number of conversations to skip.")] int skip = 0,
        [Description("Maximum number of conversations to return (1-200).")] int take = 20,
        [Description("Only conversations active at or after this UTC date-time (ISO 8601).")] DateTime? fromDate = null,
        [Description("Only conversations active at or before this UTC date-time (ISO 8601).")] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await conversations.ListConversationsAsync(new ConversationListRequest
        {
            Skip = Math.Max(0, skip),
            Take = Math.Clamp(take, 1, 200),
            FromDate = fromDate,
            ToDate = toDate
        }, cancellationToken);
        return McpJson.Serialize(result);
    }

    [McpServerTool(Name = "memory_get_conversation", Title = "Get conversation", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Retrieve every stored turn of one conversation.")]
    public async Task<string> GetConversation(
        [Description("Identifier of the conversation.")] string conversationId,
        CancellationToken cancellationToken = default)
    {
        var turns = await conversations.GetConversationAsync(conversationId, cancellationToken);
        return turns.Count == 0
            ? throw new McpException($"Conversation '{conversationId}' was not found.")
            : McpJson.Serialize(new { conversationId, turns });
    }

    [McpServerTool(Name = "memory_get_turn", Title = "Get conversation turn", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Retrieve one specific turn of a conversation.")]
    public async Task<string> GetTurn(
        [Description("Identifier of the conversation.")] string conversationId,
        [Description("The turn number to retrieve (1-based).")] int turnNumber,
        CancellationToken cancellationToken = default)
    {
        var turn = await conversations.GetConversationTurnAsync(conversationId, turnNumber, cancellationToken);
        return turn is null
            ? throw new McpException($"Turn {turnNumber} of conversation '{conversationId}' was not found.")
            : McpJson.Serialize(turn);
    }

    [McpServerTool(Name = "browser_history_search", Title = "Search browser history", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Search synced browser history. Only available when browser history is enabled on the server.")]
    public async Task<string> SearchBrowserHistory(
        [Description("Substring of the URL.")] string? url = null,
        [Description("Substring of the page title.")] string? title = null,
        [Description("Restrict to a domain such as example.com.")] string? domain = null,
        [Description("Number of results to skip.")] int skip = 0,
        [Description("Maximum number of results to return (1-200).")] int take = 25,
        CancellationToken cancellationToken = default)
    {
        var history = services.GetService(typeof(IBrowserHistoryService)) as IBrowserHistoryService
            ?? throw new McpException("Browser history is not enabled on this server.");
        var result = await history.SearchBrowserHistoryAsync(new BrowserHistorySearchRequest
        {
            Url = url,
            Title = title,
            Domain = domain,
            Skip = Math.Max(0, skip),
            Take = Math.Clamp(take, 1, 200)
        }, cancellationToken);
        return McpJson.Serialize(result);
    }
}
