using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darbot.Memory.Mcp.Core.Graph.Adapters;

/// <summary>Common plumbing shared by all schema adapters.</summary>
public abstract class GraphAdapterBase : IGraphSchemaAdapter
{
    public abstract string Name { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }
    public virtual string ContentType => "application/json";
    public virtual bool CanImport => true;
    public virtual bool CanExport => true;

    public abstract GraphImportResult Import(string payload, string? graphName = null);
    public abstract GraphExportResult Export(KnowledgeGraph graph);

    protected GraphExportResult Result(string content, IEnumerable<string>? warnings = null) => new()
    {
        Schema = Name,
        ContentType = ContentType,
        Content = content,
        Warnings = warnings?.ToList() ?? new List<string>()
    };

    protected GraphExportResult FilesResult(IReadOnlyDictionary<string, string> files, IEnumerable<string>? warnings = null) => new()
    {
        Schema = Name,
        ContentType = ContentType,
        Files = files,
        Warnings = warnings?.ToList() ?? new List<string>()
    };

    protected string RequirePayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new ArgumentException($"The {Name} payload is empty.", nameof(payload));
        }
        return payload;
    }
}

/// <summary>Accumulates nodes and edges (deduplicated by id) and warnings while an adapter imports.</summary>
public sealed class GraphBuilder
{
    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphEdge> _edges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphCluster> _clusters = new(StringComparer.Ordinal);

    public List<string> Warnings { get; } = new();
    public Dictionary<string, JsonNode?> Metadata { get; } = new();
    public IReadOnlyDictionary<string, GraphNode> Nodes => _nodes;
    public IReadOnlyDictionary<string, GraphEdge> Edges => _edges;

    public void Warn(string message) => Warnings.Add(message);

    public void AddNode(GraphNode node) => _nodes[node.Id] = node;
    public bool HasNode(string id) => _nodes.ContainsKey(id);
    public bool TryGetNode(string id, out GraphNode node) => _nodes.TryGetValue(id, out node!);

    public void AddEdge(GraphEdge edge) => _edges[edge.Id] = edge;
    public void AddCluster(GraphCluster cluster) => _clusters[cluster.Id] = cluster;

    /// <summary>Adds a placeholder node when an edge references an entity that was not part of the payload.</summary>
    public GraphNode EnsureNode(string id, string? name = null, string type = "entity")
    {
        if (_nodes.TryGetValue(id, out var existing))
        {
            return existing;
        }
        var node = new GraphNode { Id = id, Name = string.IsNullOrWhiteSpace(name) ? id : name!, Type = type };
        _nodes[id] = node;
        return node;
    }

    public GraphEdge AddRelation(string sourceId, string targetId, string type, GraphEdge? template = null)
    {
        var edge = (template ?? new GraphEdge { Id = "", SourceId = sourceId, TargetId = targetId }) with
        {
            Id = template?.Id is { Length: > 0 } id ? id : AdapterHelpers.EdgeId(sourceId, type, targetId),
            SourceId = sourceId,
            TargetId = targetId,
            Type = type
        };
        _edges[edge.Id] = edge;
        return edge;
    }

    public GraphImportResult Build(string? graphName, string? ns = null)
    {
        var graph = new KnowledgeGraph
        {
            Name = string.IsNullOrWhiteSpace(graphName) ? "imported" : graphName!,
            Namespace = string.IsNullOrWhiteSpace(ns) ? "default" : ns!,
            Nodes = _nodes.Values.ToList(),
            Edges = _edges.Values.ToList(),
            Clusters = _clusters.Values.ToList(),
            Metadata = new Dictionary<string, JsonNode?>(Metadata)
        };
        return new GraphImportResult(graph, Warnings);
    }
}

public static class AdapterHelpers
{
    public static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Lower-case url/file-safe slug; falls back to a hash-based token for empty results.</summary>
    public static string Slug(string? value, string fallback = "item")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        var sb = new StringBuilder();
        var pendingDash = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingDash && sb.Length > 0)
                {
                    sb.Append('-');
                }
                pendingDash = false;
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                pendingDash = true;
            }
        }
        return sb.Length == 0 ? $"{fallback}-{Hash(value)[..8]}" : sb.ToString();
    }

    /// <summary>Deterministic node id from a display name. Names containing lossy punctuation get a short hash suffix.</summary>
    public static string IdFromName(string name)
    {
        var slug = Slug(name, "entity");
        var lossy = name.Any(c => !(char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_'));
        return lossy ? $"{slug}-{Hash(name)[..6]}" : slug;
    }

    public static string EdgeId(string source, string type, string target)
        => "edge-" + Hash($"{source}\u001f{type}\u001f{target}")[..16];

    public static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        var line = value.Trim().ReplaceLineEndings(" ");
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
    }

    public static string Iso(DateTimeOffset value) => value.ToString("o", CultureInfo.InvariantCulture);

    // ---- JSON reading ----

    public static JsonNode ParseJson(string payload, string schema)
    {
        try
        {
            var node = JsonNode.Parse(payload, null, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            return node ?? throw new ArgumentException($"The {schema} payload is empty JSON.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The {schema} payload is not valid JSON: {ex.Message}", ex);
        }
    }

    public static JsonNode? Get(JsonNode? node, string key)
        => node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    /// <summary>First present, non-null property among <paramref name="keys"/>.</summary>
    public static JsonNode? Field(JsonNode? node, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Get(node, key);
            if (value is not null)
            {
                return value;
            }
        }
        return null;
    }

    public static bool Has(JsonNode? node, params string[] keys)
        => node is JsonObject obj && keys.Any(k => obj.ContainsKey(k));

    public static string? Str(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                return value.GetValue<string>();
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return value.ToJsonString();
            default:
                return null;
        }
    }

    public static string? Str(JsonNode? node, params string[] keys)
    {
        foreach (var key in keys)
        {
            var s = Str(Get(node, key));
            if (!string.IsNullOrEmpty(s))
            {
                return s;
            }
        }
        return null;
    }

    public static double? Num(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var d))
            {
                return d;
            }
            if (value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            {
                return d;
            }
        }
        return null;
    }

    public static int? Int(JsonNode? node) => Num(node) is { } d ? (int)d : null;

    public static bool? Bool(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b))
            {
                return b;
            }
            if (value.TryGetValue<string>(out var s) && bool.TryParse(s, out b))
            {
                return b;
            }
        }
        return null;
    }

    public static DateTimeOffset? Date(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<string>(out var s))
        {
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        }
        if (value.TryGetValue<double>(out var n))
        {
            return n > 1e11 ? DateTimeOffset.FromUnixTimeMilliseconds((long)n) : DateTimeOffset.FromUnixTimeSeconds((long)n);
        }
        return null;
    }

    /// <summary>Reads an array of strings (or a single string / comma separated string when allowed).</summary>
    public static List<string> StrList(JsonNode? node, bool commaSeparated = false)
    {
        var result = new List<string>();
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    if (Str(item) is { Length: > 0 } s)
                    {
                        result.Add(s);
                    }
                }
                break;
            case JsonValue:
                if (Str(node) is { Length: > 0 } single)
                {
                    if (commaSeparated)
                    {
                        result.AddRange(single.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }
                    else
                    {
                        result.Add(single);
                    }
                }
                break;
        }
        return result;
    }

    public static float[]? FloatArray(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count == 0)
        {
            return null;
        }
        var result = new List<float>(array.Count);
        foreach (var item in array)
        {
            if (Num(item) is { } d)
            {
                result.Add((float)d);
            }
        }
        return result.Count == 0 ? null : result.ToArray();
    }

    public static GraphPosition? Position(JsonNode? node)
        => node is JsonObject ? new GraphPosition(Num(Get(node, "x")) ?? 0, Num(Get(node, "y")) ?? 0, Num(Get(node, "z")) ?? 0) : null;

    /// <summary>Clones all properties of <paramref name="obj"/> except the handled keys.</summary>
    public static Dictionary<string, JsonNode?> Extras(JsonNode? obj, params string[] handled)
    {
        var result = new Dictionary<string, JsonNode?>();
        if (obj is JsonObject o)
        {
            var skip = new HashSet<string>(handled, StringComparer.Ordinal);
            foreach (var kv in o)
            {
                if (!skip.Contains(kv.Key))
                {
                    result[kv.Key] = kv.Value?.DeepClone();
                }
            }
        }
        return result;
    }

    /// <summary>Copies an object's members into a property bag (deep clone).</summary>
    public static void MergeInto(Dictionary<string, JsonNode?> target, JsonNode? source)
    {
        if (source is JsonObject o)
        {
            foreach (var kv in o)
            {
                target[kv.Key] = kv.Value?.DeepClone();
            }
        }
    }

    public static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    // ---- JSON writing ----

    public static JsonObject ToObject(IEnumerable<KeyValuePair<string, JsonNode?>> props)
    {
        var obj = new JsonObject();
        foreach (var kv in props)
        {
            obj[kv.Key] = kv.Value?.DeepClone();
        }
        return obj;
    }

    public static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var v in values)
        {
            array.Add(JsonValue.Create(v));
        }
        return array;
    }

    public static void SetIf(JsonObject obj, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            obj[key] = value;
        }
    }

    public static void SetIf(JsonObject obj, string key, DateTimeOffset? value)
    {
        if (value is { } v)
        {
            obj[key] = Iso(v);
        }
    }

    public static void SetIf(JsonObject obj, string key, double? value)
    {
        if (value is { } v)
        {
            obj[key] = v;
        }
    }

    public static void SetIf(JsonObject obj, string key, JsonNode? value)
    {
        if (value is not null)
        {
            obj[key] = value.DeepClone();
        }
    }

    public static void SetIfAny(JsonObject obj, string key, IEnumerable<string> values)
    {
        var array = ToArray(values);
        if (array.Count > 0)
        {
            obj[key] = array;
        }
    }

    public static JsonObject? PositionToJson(GraphPosition? p)
        => p is { } v ? new JsonObject { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z } : null;

    public static JsonArray? FloatsToJson(float[]? values)
    {
        if (values is null)
        {
            return null;
        }
        var array = new JsonArray();
        foreach (var f in values)
        {
            array.Add(JsonValue.Create(f));
        }
        return array;
    }

    public static string Serialize(JsonNode node) => node.ToJsonString(GraphJson.Options);

    /// <summary>Resolves a display name for an edge endpoint.</summary>
    public static string NameOf(IReadOnlyDictionary<string, GraphNode> nodes, string id)
        => nodes.TryGetValue(id, out var n) ? n.Name : id;

    public static Dictionary<string, GraphNode> IndexNodes(KnowledgeGraph graph)
    {
        var map = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            map[n.Id] = n;
        }
        return map;
    }
}
