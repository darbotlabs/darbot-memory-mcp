using System.ComponentModel;
using Darbot.Memory.Mcp.Core.Graph;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Darbot.Memory.Mcp.Api.Mcp;

[McpServerToolType]
public sealed class GraphTools(IGraphMemoryService memory)
{
    private const string GraphParam = "Name of the knowledge graph. Defaults to 'default'.";

    [McpServerTool(Name = "graph_list", Title = "List knowledge graphs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List knowledge graphs with node and edge counts.")]
    public async Task<string> List(CancellationToken cancellationToken = default)
    {
        var graphs = await memory.ListGraphsAsync(cancellationToken);
        return McpJson.Serialize(new { graphs = graphs.Select(McpJson.Summarize) });
    }

    [McpServerTool(Name = "graph_schemas", Title = "List memory schemas", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List the agent-memory schemas this server can import and export (for example 3dkg, kgforge, obsidian, supermemory, mcp-memory).")]
    public string Schemas() => McpJson.Serialize(new
    {
        schemas = memory.Schemas.Select(s => new
        {
            s.Name,
            s.DisplayName,
            s.Description,
            s.ContentType,
            s.CanImport,
            s.CanExport
        })
    });

    [McpServerTool(Name = "graph_remember", Title = "Remember", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Store a fact, note or entity as a node in the knowledge graph and return the created node.")]
    public async Task<string> Remember(
        [Description("The text to remember.")] string text,
        [Description(GraphParam)] string graph = "default",
        [Description("Short name for the node. Derived from the text when omitted.")] string? name = null,
        [Description("Node type such as person, concept, memory, document or note.")] string? type = null,
        [Description("Owner scope such as a user, agent or run id.")] string? scope = null,
        [Description("Tags to attach to the node.")] string[]? tags = null,
        CancellationToken cancellationToken = default)
    {
        var node = await McpJson.GuardAsync(() => memory.RememberAsync(graph, text, name, type, scope, tags, cancellationToken));
        return McpJson.Serialize(McpJson.Slim(node));
    }

    [McpServerTool(Name = "graph_relate", Title = "Relate nodes", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a typed edge between two existing nodes and return the edge.")]
    public async Task<string> Relate(
        [Description("Id of the source node.")] string sourceId,
        [Description("Id of the target node.")] string targetId,
        [Description("Relationship type such as works_at, related_to or depends_on.")] string type,
        [Description("Optional natural-language fact describing the relationship.")] string? fact = null,
        [Description(GraphParam)] string graph = "default",
        CancellationToken cancellationToken = default)
    {
        var edge = await McpJson.GuardAsync(() => memory.RelateAsync(graph, sourceId, targetId, type, fact, cancellationToken));
        return McpJson.Serialize(edge);
    }

    [McpServerTool(Name = "graph_search", Title = "Search graph", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Search nodes in a knowledge graph by text, type, tag or scope.")]
    public async Task<string> Search(
        [Description("Text to match against node names, descriptions, observations and content.")] string? query = null,
        [Description(GraphParam)] string graph = "default",
        [Description("Restrict to a node type.")] string? type = null,
        [Description("Restrict to nodes carrying this tag.")] string? tag = null,
        [Description("Restrict to an owner scope.")] string? scope = null,
        [Description("Only the latest version of each node.")] bool latestOnly = true,
        [Description("Maximum number of nodes to return (1-200).")] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        var nodes = await McpJson.GuardAsync(() => memory.SearchAsync(graph, new GraphQuery
        {
            Text = query,
            Type = type,
            Tag = tag,
            Scope = scope,
            LatestOnly = latestOnly,
            Limit = Math.Clamp(limit, 1, 200)
        }, cancellationToken));
        return McpJson.Serialize(new { graph = graph, count = nodes.Count, nodes = nodes.Select(McpJson.Slim) });
    }

    [McpServerTool(Name = "graph_neighborhood", Title = "Node neighborhood", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Return a node together with the nodes and edges around it up to the given depth.")]
    public async Task<string> Neighborhood(
        [Description("Id of the node at the center.")] string nodeId,
        [Description(GraphParam)] string graph = "default",
        [Description("How many hops to expand (1-5).")] int depth = 1,
        CancellationToken cancellationToken = default)
    {
        var hood = await McpJson.GuardAsync(() => memory.NeighborhoodAsync(graph, nodeId, Math.Clamp(depth, 1, 5), cancellationToken));
        return hood is null
            ? throw new McpException($"Node '{nodeId}' was not found in graph '{graph}'.")
            : McpJson.Serialize(new
            {
                root = McpJson.Slim(hood.Root),
                nodes = hood.Nodes.Select(McpJson.Slim),
                edges = hood.Edges
            });
    }

    [McpServerTool(Name = "graph_forget", Title = "Forget node", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Delete a node (and its edges) from the knowledge graph.")]
    public async Task<string> Forget(
        [Description("Id of the node to delete.")] string nodeId,
        [Description(GraphParam)] string graph = "default",
        CancellationToken cancellationToken = default)
    {
        var removed = await McpJson.GuardAsync(() => memory.ForgetAsync(graph, nodeId, cancellationToken));
        return removed
            ? McpJson.Serialize(new { forgotten = true, nodeId, graph = graph })
            : throw new McpException($"Node '{nodeId}' was not found in graph '{graph}'.");
    }

    [McpServerTool(Name = "graph_import", Title = "Import memory schema", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Import a payload written in one of the supported memory schemas into a graph. With merge=false the graph is replaced.")]
    public async Task<string> Import(
        [Description("Schema name from graph_schemas, for example obsidian or mcp-memory.")] string schema,
        [Description("The schema payload (JSON, JSONL or markdown). Multi-file schemas take a JSON object of path to content.")] string payload,
        [Description(GraphParam)] string graph = "default",
        [Description("Merge into the existing graph (true) or replace it (false).")] bool merge = true,
        CancellationToken cancellationToken = default)
    {
        var result = await McpJson.GuardAsync(() => memory.ImportAsync(schema, payload, graph, merge, cancellationToken));
        return McpJson.Serialize(new { imported = true, schema, merge, graph = McpJson.Summarize(result) });
    }

    [McpServerTool(Name = "graph_export", Title = "Export memory schema", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Export a graph in one of the supported memory schemas. Multi-file schemas return a map of relative path to content.")]
    public async Task<string> Export(
        [Description("Schema name from graph_schemas.")] string schema,
        [Description(GraphParam)] string graph = "default",
        CancellationToken cancellationToken = default)
    {
        var result = await McpJson.GuardAsync(() => memory.ExportAsync(schema, graph, cancellationToken));
        return McpJson.Serialize(result);
    }
}

