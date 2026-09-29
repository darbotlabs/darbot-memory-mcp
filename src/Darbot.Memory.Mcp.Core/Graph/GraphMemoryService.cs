using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Darbot.Memory.Mcp.Core.Graph;

public sealed class GraphMemoryService : IGraphMemoryService
{
    public const string ImportWarningsKey = "importWarnings";

    private readonly IGraphSchemaRegistry _registry;
    private readonly IKnowledgeGraphStore _store;
    private readonly GraphMemoryConfiguration _config;

    public GraphMemoryService(IGraphSchemaRegistry registry, IKnowledgeGraphStore store, GraphMemoryConfiguration config)
    {
        _registry = registry;
        _store = store;
        _config = config;
    }

    public IReadOnlyList<IGraphSchemaAdapter> Schemas => _registry.Adapters;

    private string Resolve(string? graphName)
        => string.IsNullOrWhiteSpace(graphName) ? _config.DefaultGraph : graphName.Trim();

    /// <summary>Imports a payload. Adapter warnings are returned (not persisted) in Metadata["importWarnings"].</summary>
    public async Task<KnowledgeGraph> ImportAsync(string schema, string payload, string graphName, bool merge = true, CancellationToken ct = default)
    {
        var adapter = _registry.GetRequired(schema);
        if (!adapter.CanImport)
        {
            throw new NotSupportedException($"The '{adapter.Name}' schema does not support import.");
        }
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new ArgumentException("The import payload is empty.", nameof(payload));
        }
        if (Encoding.UTF8.GetByteCount(payload) > _config.MaxImportBytes)
        {
            throw new ArgumentException($"The import payload exceeds the {_config.MaxImportBytes} byte limit.", nameof(payload));
        }

        var name = Resolve(graphName);
        var imported = adapter.Import(payload, name);
        var graph = imported.Graph with { Name = name };
        KnowledgeGraph stored;
        if (merge)
        {
            stored = await _store.MergeAsync(name, graph, ct).ConfigureAwait(false);
        }
        else
        {
            await _store.SaveAsync(graph, ct).ConfigureAwait(false);
            stored = (await _store.GetAsync(name, ct).ConfigureAwait(false))!;
        }

        var metadata = new Dictionary<string, JsonNode?>(stored.Metadata);
        var warnings = new JsonArray();
        foreach (var w in imported.Warnings)
        {
            warnings.Add(JsonValue.Create(w));
        }
        metadata[ImportWarningsKey] = warnings;
        return stored with { Metadata = metadata };
    }

    public async Task<GraphExportResult> ExportAsync(string schema, string graphName, CancellationToken ct = default)
    {
        var adapter = _registry.GetRequired(schema);
        if (!adapter.CanExport)
        {
            throw new NotSupportedException($"The '{adapter.Name}' schema does not support export.");
        }
        var graph = await _store.GetAsync(Resolve(graphName), ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Graph '{Resolve(graphName)}' was not found.");
        return adapter.Export(graph);
    }

    public Task<IReadOnlyList<KnowledgeGraph>> ListGraphsAsync(CancellationToken ct = default) => _store.ListAsync(ct);

    public Task<KnowledgeGraph?> GetGraphAsync(string graphName, CancellationToken ct = default) => _store.GetAsync(Resolve(graphName), ct);

    public Task<IReadOnlyList<GraphNode>> SearchAsync(string graphName, GraphQuery query, CancellationToken ct = default)
        => _store.SearchNodesAsync(Resolve(graphName), query ?? new GraphQuery(), ct);

    public async Task<GraphNode> RememberAsync(string graphName, string text, string? name = null, string? type = null, string? scope = null, IEnumerable<string>? tags = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Memory text is required.", nameof(text));
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Trim() + "\u001f" + (scope ?? string.Empty)))).ToLowerInvariant();
        var node = new GraphNode
        {
            Id = "mem-" + hash[..16],
            Type = string.IsNullOrWhiteSpace(type) ? "memory" : type.Trim(),
            Name = string.IsNullOrWhiteSpace(name) ? Truncate(text) : name.Trim(),
            Content = text.Trim(),
            Scope = string.IsNullOrWhiteSpace(scope) ? null : scope.Trim(),
            Tags = (tags ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().TrimStart('#')).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
        return await _store.UpsertNodeAsync(Resolve(graphName), node, ct).ConfigureAwait(false);
    }

    public async Task<GraphEdge> RelateAsync(string graphName, string sourceId, string targetId, string type, string? fact = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(targetId))
        {
            throw new ArgumentException("Both sourceId and targetId are required.");
        }
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("A relation type is required.", nameof(type));
        }
        var name = Resolve(graphName);
        foreach (var id in new[] { sourceId, targetId })
        {
            if (await _store.GetNeighborhoodAsync(name, id, 0, ct).ConfigureAwait(false) is null)
            {
                throw new KeyNotFoundException($"Node '{id}' was not found in graph '{name}'.");
            }
        }
        var edgeType = type.Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceId}\u001f{edgeType}\u001f{targetId}"))).ToLowerInvariant();
        return await _store.UpsertEdgeAsync(name, new GraphEdge
        {
            Id = "edge-" + hash[..16],
            SourceId = sourceId,
            TargetId = targetId,
            Type = edgeType,
            Fact = string.IsNullOrWhiteSpace(fact) ? null : fact.Trim()
        }, ct).ConfigureAwait(false);
    }

    public Task<GraphNeighborhood?> NeighborhoodAsync(string graphName, string nodeId, int depth = 1, CancellationToken ct = default)
        => _store.GetNeighborhoodAsync(Resolve(graphName), nodeId, depth, ct);

    public Task<bool> ForgetAsync(string graphName, string nodeId, CancellationToken ct = default)
        => _store.DeleteNodeAsync(Resolve(graphName), nodeId, ct);

    private static string Truncate(string text)
    {
        var line = text.Trim().ReplaceLineEndings(" ");
        return line.Length <= 80 ? line : line[..79].TrimEnd() + "…";
    }
}
