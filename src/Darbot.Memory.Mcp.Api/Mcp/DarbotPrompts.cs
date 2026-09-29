using System.ComponentModel;
using System.Text;
using Darbot.Memory.Mcp.Core.Graph;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Darbot.Memory.Mcp.Api.Mcp;

[McpServerPromptType]
public sealed class DarbotPrompts
{
    [McpServerPrompt(Name = "recall_context", Title = "Recall context")]
    [Description("Gather what the knowledge graph and past conversations know about a topic so the model can answer with grounded memory.")]
    public async Task<ChatMessage> RecallContext(
        IGraphMemoryService memory,
        IConversationService conversations,
        [Description("The topic or question to recall.")] string topic,
        [Description("Knowledge graph to search.")] string graph = "default",
        CancellationToken cancellationToken = default)
    {
        var nodes = await memory.SearchAsync(graph, new GraphQuery { Text = topic, Limit = 8 }, cancellationToken);
        var turns = (await conversations.SearchConversationsAsync(new ConversationSearchRequest { SearchText = topic, Take = 5 }, cancellationToken)).Results;

        var sb = new StringBuilder();
        sb.AppendLine($"Answer the request about \"{topic}\" using only the stored memory below. Say so if the memory does not cover it.");
        sb.AppendLine();
        sb.AppendLine($"## Knowledge graph '{graph}' ({nodes.Count} matches)");
        foreach (var node in nodes)
        {
            sb.AppendLine($"- {node.Name} [{node.Type}] (id: {node.Id})");
            if (!string.IsNullOrWhiteSpace(node.Description))
            {
                sb.AppendLine($"  {node.Description}");
            }

            foreach (var observation in node.Observations.Take(5))
            {
                sb.AppendLine($"  * {observation}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"## Past conversation turns ({turns.Count} matches)");
        foreach (var turn in turns)
        {
            sb.AppendLine($"- {turn.ConversationId} #{turn.TurnNumber} ({turn.UtcTimestamp:u}): user: {Trim(turn.Prompt)} | assistant: {Trim(turn.Response)}");
        }

        return new ChatMessage(ChatRole.User, sb.ToString());
    }

    [McpServerPrompt(Name = "summarize_conversation", Title = "Summarize conversation")]
    [Description("Load a stored conversation and ask the model to summarize it with decisions and open items.")]
    public async Task<ChatMessage> SummarizeConversation(
        IConversationService conversations,
        [Description("Identifier of the conversation to summarize.")] string conversationId,
        CancellationToken cancellationToken = default)
    {
        var turns = await conversations.GetConversationAsync(conversationId, cancellationToken);
        if (turns.Count == 0)
        {
            throw new McpProtocolException($"Conversation '{conversationId}' was not found.", McpErrorCode.InvalidParams);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Summarize conversation '{conversationId}' ({turns.Count} turns). List the key facts, decisions and open items.");
        sb.AppendLine();
        foreach (var turn in turns.OrderBy(t => t.TurnNumber))
        {
            sb.AppendLine($"### Turn {turn.TurnNumber} ({turn.UtcTimestamp:u}, model {turn.Model})");
            sb.AppendLine($"User: {turn.Prompt}");
            sb.AppendLine($"Assistant: {turn.Response}");
            sb.AppendLine();
        }

        return new ChatMessage(ChatRole.User, sb.ToString());
    }

    [McpServerPrompt(Name = "import_from_schema", Title = "Import from a memory schema")]
    [Description("Guide the model through importing memory data from an external schema with graph_import.")]
    public ChatMessage ImportFromSchema(
        IGraphMemoryService memory,
        [Description("Schema to import from; see graph_schemas.")] string schema,
        [Description("Where the data comes from, such as a pasted export or a file description.")] string? source = null,
        [Description("Target knowledge graph.")] string graph = "default")
    {
        var adapter = memory.Schemas.FirstOrDefault(s => string.Equals(s.Name, schema, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpProtocolException(
                $"Unknownschema '{schema}'. Available: {string.Join(", ", memory.Schemas.Select(s => s.Name))}.",
                McpErrorCode.InvalidParams);

        if (!adapter.CanImport)
        {
            throw new McpProtocolException($"Schema '{adapter.Name}' does not support import.", McpErrorCode.InvalidParams);
        }

        var text = $"Import memory data written in the {adapter.DisplayName} schema ({adapter.Description}) into knowledge graph '{graph}'.\n" +
                   (string.IsNullOrWhiteSpace(source) ? string.Empty : $"Source: {source}\n") +
                   $"Call the graph_import tool with schema='{adapter.Name}', graph='{graph}', merge=true and the raw payload. " +
                   "Then call graph_search or graph_list to verify the result and report how many nodes and edges were imported.";
        return new ChatMessage(ChatRole.User, text);
    }

    private static string Trim(string text) => text.Length <= 240 ? text : text[..239] + "…";
}


