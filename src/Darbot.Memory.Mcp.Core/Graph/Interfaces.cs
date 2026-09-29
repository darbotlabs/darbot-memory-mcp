namespace Darbot.Memory.Mcp.Core.Graph;

/// <summary>
/// Translates between one external agent-memory / knowledge-graph schema and the canonical <see cref="KnowledgeGraph"/>.
/// </summary>
public interface IGraphSchemaAdapter
{
    /// <summary>Stable lower-case identifier, e.g. "3dkg", "kgforge", "obsidian", "supermemory", "mcp-memory".</summary>
    string Name { get; }
    string DisplayName { get; }
    string Description { get; }
    /// <summary>Content type produced by <see cref="Export"/>.</summary>
    string ContentType { get; }
    bool CanImport { get; }
    bool CanExport { get; }

    /// <summary>Import a payload (JSON, JSONL, markdown, ...). For multi-file schemas the payload is a JSON object of path to content.</summary>
    GraphImportResult Import(string payload, string? graphName = null);

    GraphExportResult Export(KnowledgeGraph graph);
}

public interface IGraphSchemaRegistry
{
    IReadOnlyList<IGraphSchemaAdapter> Adapters { get; }
    IGraphSchemaAdapter? Find(string name);
    IGraphSchemaAdapter GetRequired(string name);
}

/// <summary>Persistent store for canonical knowledge graphs, keyed by namespace + graph name.</summary>
public interface IKnowledgeGraphStore
{
    Task<IReadOnlyList<KnowledgeGraph>> ListAsync(CancellationToken ct = default);
    Task<KnowledgeGraph?> GetAsync(string name, CancellationToken ct = default);
    /// <summary>Insert or replace a whole graph.</summary>
    Task SaveAsync(KnowledgeGraph graph, CancellationToken ct = default);
    /// <summary>Merge nodes/edges into an existing graph (creating it if needed). Nodes/edges with the same Id are replaced.</summary>
    Task<KnowledgeGraph> MergeAsync(string name, KnowledgeGraph incoming, CancellationToken ct = default);
    Task<bool> DeleteAsync(string name, CancellationToken ct = default);

    Task<GraphNode> UpsertNodeAsync(string graphName, GraphNode node, CancellationToken ct = default);
    Task<GraphEdge> UpsertEdgeAsync(string graphName, GraphEdge edge, CancellationToken ct = default);
    Task<bool> DeleteNodeAsync(string graphName, string nodeId, CancellationToken ct = default);
    Task<IReadOnlyList<GraphNode>> SearchNodesAsync(string graphName, GraphQuery query, CancellationToken ct = default);
    Task<GraphNeighborhood?> GetNeighborhoodAsync(string graphName, string nodeId, int depth = 1, CancellationToken ct = default);
}

/// <summary>Facade used by REST, MCP and ACP so every surface shares the same behavior.</summary>
public interface IGraphMemoryService
{
    IReadOnlyList<IGraphSchemaAdapter> Schemas { get; }
    Task<KnowledgeGraph> ImportAsync(string schema, string payload, string graphName, bool merge = true, CancellationToken ct = default);
    Task<GraphExportResult> ExportAsync(string schema, string graphName, CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeGraph>> ListGraphsAsync(CancellationToken ct = default);
    Task<KnowledgeGraph?> GetGraphAsync(string graphName, CancellationToken ct = default);
    Task<IReadOnlyList<GraphNode>> SearchAsync(string graphName, GraphQuery query, CancellationToken ct = default);
    Task<GraphNode> RememberAsync(string graphName, string text, string? name = null, string? type = null, string? scope = null, IEnumerable<string>? tags = null, CancellationToken ct = default);
    Task<GraphEdge> RelateAsync(string graphName, string sourceId, string targetId, string type, string? fact = null, CancellationToken ct = default);
    Task<GraphNeighborhood?> NeighborhoodAsync(string graphName, string nodeId, int depth = 1, CancellationToken ct = default);
    Task<bool> ForgetAsync(string graphName, string nodeId, CancellationToken ct = default);
}
