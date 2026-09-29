using System.ComponentModel;
using Darbot.Memory.Mcp.Core.Graph;
using Darbot.Memory.Mcp.Core.Interfaces;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Darbot.Memory.Mcp.Api.Mcp;

[McpServerResourceType]
public sealed class DarbotResources
{
    [McpServerResource(UriTemplate = "darbot://graph-schemas", Name = "graph_schemas", Title = "Memory graph schemas", MimeType = "application/json")]
    [Description("The agent-memory schemas that can be imported into or exported from the knowledge graph.")]
    public string GraphSchemas(IGraphMemoryService memory) => McpJson.Serialize(new
    {
        schemas = memory.Schemas.Select(s => new { s.Name, s.DisplayName, s.Description, s.ContentType, s.CanImport, s.CanExport })
    });

    [McpServerResource(UriTemplate = "darbot://graphs/{name}", Name = "graph", Title = "Knowledge graph", MimeType = "application/json")]
    [Description("A complete knowledge graph in the canonical Darbot format (nodes, edges and clusters).")]
    public async Task<string> Graph(IGraphMemoryService memory, string name, CancellationToken cancellationToken)
    {
        var graph = await memory.GetGraphAsync(Uri.UnescapeDataString(name), cancellationToken)
            ?? throw new McpProtocolException($"Graph '{name}' was not found.", McpErrorCode.InvalidParams);
        return McpJson.Serialize(graph with { Nodes = graph.Nodes.Select(McpJson.Slim).ToList() });
    }

    [McpServerResource(UriTemplate = "darbot://conversations/{id}", Name = "conversation", Title = "Conversation", MimeType = "application/json")]
    [Description("All stored turns of one conversation.")]
    public async Task<string> Conversation(IConversationService conversations, string id, CancellationToken cancellationToken)
    {
        var conversationId = Uri.UnescapeDataString(id);
        var turns = await conversations.GetConversationAsync(conversationId, cancellationToken);
        return turns.Count == 0
            ? throw new McpProtocolException($"Conversation '{conversationId}' was not found.", McpErrorCode.InvalidParams)
            : McpJson.Serialize(new { conversationId, turns });
    }
}

