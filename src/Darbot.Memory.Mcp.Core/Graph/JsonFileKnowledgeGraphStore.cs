using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darbot.Memory.Mcp.Core.Graph;

/// <summary>
/// Thread-safe knowledge graph store: in-memory, hydrated lazily from and atomically persisted to
/// <c>&lt;RootPath&gt;\&lt;safe-graph-name&gt;.kg.json</c>.
/// </summary>
public sealed class JsonFileKnowledgeGraphStore : IKnowledgeGraphStore
{
    private const string FileSuffix = ".kg.json";
    private const int MaxDepth = 4;

    private sealed class GraphState
    {
        public required KnowledgeGraph Meta { get; set; }
        public Dictionary<string, GraphNode> Nodes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, GraphEdge> Edges { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, GraphCluster> Clusters { get; } = new(StringComparer.Ordinal);

        public KnowledgeGraph Snapshot() => Meta with
        {
            Nodes = Nodes.Values.ToList(),
            Edges = Edges.Values.ToList(),
            Clusters = Clusters.Values.ToList(),
            Metadata = new Dictionary<string, JsonNode?>(Meta.Metadata)
        };
    }

    private readonly object _gate = new();
    private readonly string _root;
    private Dictionary<string, GraphState>? _graphs;

    public JsonFileKnowledgeGraphStore(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A root path is required.", nameof(rootPath));
        }
        _root = Path.GetFullPath(rootPath);
    }

    public JsonFileKnowledgeGraphStore(GraphMemoryConfiguration configuration) : this(configuration.RootPath)
    {
    }

    /// <summary>File-system and dictionary key for a graph name.</summary>
    public static string SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "default";
        }
        var chars = name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        var safe = new string(chars).Trim('.').ToLowerInvariant();
        if (safe.Length > 100)
        {
            safe = safe[..100];
        }
        return safe.Length == 0 ? "default" : safe;
    }

    public Task<IReadOnlyList<KnowledgeGraph>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var list = Load().Values.OrderBy(g => g.Meta.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.Snapshot()).ToList();
            return Task.FromResult<IReadOnlyList<KnowledgeGraph>>(list);
        }
    }

    public Task<KnowledgeGraph?> GetAsync(string name, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Load().TryGetValue(SafeName(name), out var state) ? state.Snapshot() : null);
        }
    }

    public Task SaveAsync(KnowledgeGraph graph, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        lock (_gate)
        {
            var state = NewState(graph with { UpdatedAt = DateTimeOffset.UtcNow });
            Load()[SafeName(graph.Name)] = state;
            Persist(state);
        }
        return Task.CompletedTask;
    }

    public Task<KnowledgeGraph> MergeAsync(string name, KnowledgeGraph incoming, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        lock (_gate)
        {
            var state = GetOrCreate(name, incoming.Namespace);
            foreach (var n in incoming.Nodes)
            {
                state.Nodes[n.Id] = n;
            }
            foreach (var e in incoming.Edges)
            {
                state.Edges[e.Id] = e;
            }
            foreach (var c in incoming.Clusters)
            {
                state.Clusters[c.Id] = c;
            }
            foreach (var kv in incoming.Metadata)
            {
                state.Meta.Metadata[kv.Key] = kv.Value?.DeepClone();
            }
            Touch(state);
            return Task.FromResult(state.Snapshot());
        }
    }

    public Task<bool> DeleteAsync(string name, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = SafeName(name);
            if (!Load().Remove(key))
            {
                return Task.FromResult(false);
            }
            var path = FilePath(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return Task.FromResult(true);
        }
    }

    public Task<GraphNode> UpsertNodeAsync(string graphName, GraphNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_gate)
        {
            var state = GetOrCreate(graphName);
            var now = DateTimeOffset.UtcNow;
            state.Nodes.TryGetValue(node.Id, out var existing);
            var stored = node with { CreatedAt = existing?.CreatedAt ?? node.CreatedAt ?? now, UpdatedAt = now };
            state.Nodes[node.Id] = stored;
            Touch(state);
            return Task.FromResult(stored);
        }
    }

    public Task<GraphEdge> UpsertEdgeAsync(string graphName, GraphEdge edge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edge);
        lock (_gate)
        {
            var state = GetOrCreate(graphName);
            var stored = edge with { CreatedAt = state.Edges.TryGetValue(edge.Id, out var existing) ? existing.CreatedAt ?? edge.CreatedAt : edge.CreatedAt ?? DateTimeOffset.UtcNow };
            state.Edges[edge.Id] = stored;
            Touch(state);
            return Task.FromResult(stored);
        }
    }

    public Task<bool> DeleteNodeAsync(string graphName, string nodeId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!Load().TryGetValue(SafeName(graphName), out var state) || !state.Nodes.Remove(nodeId))
            {
                return Task.FromResult(false);
            }
            foreach (var id in state.Edges.Values.Where(e => e.SourceId == nodeId || e.TargetId == nodeId).Select(e => e.Id).ToList())
            {
                state.Edges.Remove(id);
            }
            foreach (var cluster in state.Clusters.Values.Where(c => c.NodeIds.Contains(nodeId)).ToList())
            {
                state.Clusters[cluster.Id] = cluster with { NodeIds = cluster.NodeIds.Where(i => i != nodeId).ToList() };
            }
            Touch(state);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<GraphNode>> SearchNodesAsync(string graphName, GraphQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            if (!Load().TryGetValue(SafeName(graphName), out var state))
            {
                return Task.FromResult<IReadOnlyList<GraphNode>>(Array.Empty<GraphNode>());
            }
            var terms = (query.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var limit = query.Limit <= 0 ? 25 : Math.Min(query.Limit, 1000);
            var results = new List<(GraphNode Node, int Score)>();
            foreach (var n in state.Nodes.Values)
            {
                if (!Matches(n, query))
                {
                    continue;
                }
                var score = Score(n, terms);
                if (score < 0)
                {
                    continue;
                }
                results.Add((n, score));
            }
            var ordered = results
                .OrderByDescending(r => r.Score)
                .ThenByDescending(r => r.Node.UpdatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(r => r.Node.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(r => r.Node)
                .ToList();
            return Task.FromResult<IReadOnlyList<GraphNode>>(ordered);
        }
    }

    public Task<GraphNeighborhood?> GetNeighborhoodAsync(string graphName, string nodeId, int depth = 1, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!Load().TryGetValue(SafeName(graphName), out var state) || !state.Nodes.TryGetValue(nodeId, out var root))
            {
                return Task.FromResult<GraphNeighborhood?>(null);
            }
            depth = Math.Clamp(depth, 0, MaxDepth);
            var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var e in state.Edges.Values)
            {
                (adjacency.TryGetValue(e.SourceId, out var a) ? a : adjacency[e.SourceId] = new()).Add(e.TargetId);
                (adjacency.TryGetValue(e.TargetId, out var b) ? b : adjacency[e.TargetId] = new()).Add(e.SourceId);
            }
            var visited = new HashSet<string>(StringComparer.Ordinal) { nodeId };
            var frontier = new List<string> { nodeId };
            for (var d = 0; d < depth && frontier.Count > 0; d++)
            {
                var next = new List<string>();
                foreach (var id in frontier)
                {
                    if (!adjacency.TryGetValue(id, out var neighbours))
                    {
                        continue;
                    }
                    foreach (var nb in neighbours)
                    {
                        if (state.Nodes.ContainsKey(nb) && visited.Add(nb))
                        {
                            next.Add(nb);
                        }
                    }
                }
                frontier = next;
            }
            var nodes = visited.Where(id => id != nodeId).Select(id => state.Nodes[id]).ToList();
            var edges = state.Edges.Values.Where(e => visited.Contains(e.SourceId) && visited.Contains(e.TargetId)).ToList();
            return Task.FromResult<GraphNeighborhood?>(new GraphNeighborhood(root, nodes, edges));
        }
    }

    // ------------------------------------------------------------------ helpers

    private static bool Matches(GraphNode n, GraphQuery q)
    {
        if (!string.IsNullOrWhiteSpace(q.Type) && !string.Equals(n.Type, q.Type, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(q.Scope) && !string.Equals(n.Scope, q.Scope, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(q.Tag) && !n.Tags.Contains(q.Tag.TrimStart('#'), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        if (q.LatestOnly && !n.IsLatest)
        {
            return false;
        }
        if (q.AsOf is { } asOf)
        {
            if (n.ValidFrom is { } from && from > asOf)
            {
                return false;
            }
            if (n.ValidTo is { } to && to <= asOf)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Returns -1 when a term is not found anywhere; otherwise a relevance score.</summary>
    private static int Score(GraphNode n, string[] terms)
    {
        if (terms.Length == 0)
        {
            return 0;
        }
        var total = 0;
        foreach (var term in terms)
        {
            var s = 0;
            if (n.Name.Equals(term, StringComparison.OrdinalIgnoreCase)) s = Math.Max(s, 100);
            else if (n.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase)) s = Math.Max(s, 60);
            else if (n.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) s = Math.Max(s, 40);
            if (n.Aliases.Any(a => a.Contains(term, StringComparison.OrdinalIgnoreCase))) s = Math.Max(s, 30);
            if (n.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase))) s = Math.Max(s, 20);
            if (n.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) s = Math.Max(s, 15);
            if (n.Observations.Any(o => o.Contains(term, StringComparison.OrdinalIgnoreCase))) s = Math.Max(s, 10);
            if (n.Content?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) s = Math.Max(s, 5);
            if (s == 0)
            {
                return -1;
            }
            total += s;
        }
        return total;
    }

    private string FilePath(string safeName) => Path.Combine(_root, safeName + FileSuffix);

    private static GraphState NewState(KnowledgeGraph graph)
    {
        var state = new GraphState { Meta = graph with { Nodes = new(), Edges = new(), Clusters = new(), Metadata = new Dictionary<string, JsonNode?>(graph.Metadata) } };
        foreach (var n in graph.Nodes)
        {
            state.Nodes[n.Id] = n;
        }
        foreach (var e in graph.Edges)
        {
            state.Edges[e.Id] = e;
        }
        foreach (var c in graph.Clusters)
        {
            state.Clusters[c.Id] = c;
        }
        return state;
    }

    private GraphState GetOrCreate(string name, string? ns = null)
    {
        var graphs = Load();
        var key = SafeName(name);
        if (!graphs.TryGetValue(key, out var state))
        {
            state = NewState(new KnowledgeGraph
            {
                Name = string.IsNullOrWhiteSpace(name) ? "default" : name.Trim(),
                Namespace = string.IsNullOrWhiteSpace(ns) ? "default" : ns!
            });
            graphs[key] = state;
        }
        return state;
    }

    private void Touch(GraphState state)
    {
        state.Meta = state.Meta with { UpdatedAt = DateTimeOffset.UtcNow };
        Persist(state);
    }

    private Dictionary<string, GraphState> Load()
    {
        if (_graphs is not null)
        {
            return _graphs;
        }
        var graphs = new Dictionary<string, GraphState>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*" + FileSuffix))
            {
                try
                {
                    var graph = JsonSerializer.Deserialize<KnowledgeGraph>(File.ReadAllText(file), GraphJson.Options);
                    if (graph is not null)
                    {
                        graphs[SafeName(Path.GetFileName(file)[..^FileSuffix.Length])] = NewState(graph);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // A corrupt graph file must not take the whole store down.
                }
            }
        }
        _graphs = graphs;
        return graphs;
    }

    private void Persist(GraphState state)
    {
        Directory.CreateDirectory(_root);
        var path = FilePath(SafeName(state.Meta.Name));
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state.Snapshot(), GraphJson.Options));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
