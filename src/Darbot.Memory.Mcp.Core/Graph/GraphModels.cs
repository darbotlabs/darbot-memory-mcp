using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darbot.Memory.Mcp.Core.Graph;

/// <summary>
/// Canonical, schema-neutral knowledge graph used as the pivot format between agent-memory schemas
/// (3DKG, KGForge, Obsidian, Supermemory, MCP memory, mem0, Graphiti/Zep, Letta, JSON-LD, ...).
/// Adapters translate to and from this model; storage and protocols only ever see this model.
/// </summary>
public sealed record KnowledgeGraph
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "default";
    public string Namespace { get; init; } = "default";
    public string SchemaVersion { get; init; } = "darbot-kg/v1";
    public List<GraphNode> Nodes { get; init; } = new();
    public List<GraphEdge> Edges { get; init; } = new();
    public List<GraphCluster> Clusters { get; init; } = new();
    public Dictionary<string, JsonNode?> Metadata { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>An entity, memory, document, note, flashcard, or schema path pattern.</summary>
public sealed record GraphNode
{
    public required string Id { get; init; }
    /// <summary>Free-form type (person, concept, memory, document, note, block, ...).</summary>
    public string Type { get; init; } = "entity";
    public required string Name { get; init; }
    public string? Description { get; init; }
    public List<string> Aliases { get; init; } = new();
    public List<string> Tags { get; init; } = new();
    /// <summary>Atomic facts / observations attached to the node (MCP memory, mem0, Letta blocks).</summary>
    public List<string> Observations { get; init; } = new();
    /// <summary>Long-form body (Obsidian note body, Supermemory document content, Letta block value).</summary>
    public string? Content { get; init; }
    public Dictionary<string, JsonNode?> Properties { get; init; } = new();
    public float[]? Embedding { get; init; }
    public GraphPosition? Position { get; init; }
    public JsonNode? Style { get; init; }
    public string? Cluster { get; init; }
    /// <summary>Owner scope: user_id / agent_id / run_id / group_id / spaceId / containerTag.</summary>
    public string? Scope { get; init; }
    public string? Source { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    /// <summary>Bi-temporal validity window (Graphiti/Zep). Null means open-ended.</summary>
    public DateTimeOffset? ValidFrom { get; init; }
    public DateTimeOffset? ValidTo { get; init; }
    public int? Version { get; init; }
    public bool IsLatest { get; init; } = true;
}

public sealed record GraphEdge
{
    public required string Id { get; init; }
    public string Type { get; init; } = "related_to";
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public string? Label { get; init; }
    /// <summary>Natural-language fact for the edge (Graphiti/Zep edge.fact).</summary>
    public string? Fact { get; init; }
    public double? Weight { get; init; }
    public double? Confidence { get; init; }
    public bool Bidirectional { get; init; }
    public Dictionary<string, JsonNode?> Properties { get; init; } = new();
    public JsonNode? Style { get; init; }
    public string? Scope { get; init; }
    public string? Source { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ValidFrom { get; init; }
    public DateTimeOffset? ValidTo { get; init; }
    public DateTimeOffset? ExpiredAt { get; init; }
}

public sealed record GraphCluster
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public List<string> NodeIds { get; init; } = new();
    public Dictionary<string, JsonNode?> Properties { get; init; } = new();
}

public readonly record struct GraphPosition(double X, double Y, double Z);

/// <summary>Result of an import or export through a schema adapter.</summary>
public sealed record GraphImportResult(KnowledgeGraph Graph, IReadOnlyList<string> Warnings);

/// <summary>An exported artifact. Text-based schemas return <see cref="Content"/>; multi-file schemas (Obsidian) return <see cref="Files"/>.</summary>
public sealed record GraphExportResult
{
    public required string Schema { get; init; }
    public required string ContentType { get; init; }
    public string? Content { get; init; }
    /// <summary>Relative path to file content, for multi-file exports such as an Obsidian vault.</summary>
    public IReadOnlyDictionary<string, string>? Files { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Search filters for the graph store.</summary>
public sealed record GraphQuery
{
    public string? Text { get; init; }
    public string? Type { get; init; }
    public string? Scope { get; init; }
    public string? Tag { get; init; }
    public bool LatestOnly { get; init; } = true;
    public DateTimeOffset? AsOf { get; init; }
    public int Limit { get; init; } = 25;
}

public sealed record GraphNeighborhood(GraphNode Root, IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);

/// <summary>Shared JSON options for adapters and stores.</summary>
public static class GraphJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
}
