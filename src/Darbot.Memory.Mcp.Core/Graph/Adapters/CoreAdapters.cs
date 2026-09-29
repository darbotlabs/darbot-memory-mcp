using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darbot.Memory.Mcp.Core.Graph.Adapters;

/// <summary>Identity adapter: the canonical model serialized as JSON.</summary>
public sealed class DarbotKgAdapter : GraphAdapterBase
{
    public override string Name => "darbot-kg";
    public override string DisplayName => "Darbot canonical knowledge graph";
    public override string Description => "Native darbot-kg/v1 JSON (nodes, edges, clusters, metadata). Lossless round-trip.";

    public override GraphImportResult Import(string payload, string? graphName = null)
    {
        RequirePayload(payload);
        var warnings = new List<string>();
        KnowledgeGraph? graph;
        try
        {
            graph = JsonSerializer.Deserialize<KnowledgeGraph>(payload, GraphJson.Options);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The darbot-kg payload is not a valid knowledge graph: {ex.Message}", ex);
        }
        if (graph is null)
        {
            throw new ArgumentException("The darbot-kg payload is empty.");
        }

        var nodes = new List<GraphNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes ?? new())
        {
            if (node is null || string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Name))
            {
                warnings.Add("Skipped a node without id or name.");
            }
            else if (seen.Add(node.Id))
            {
                nodes.Add(node);
            }
        }
        var edges = new List<GraphEdge>();
        foreach (var edge in graph.Edges ?? new())
        {
            if (edge is null || string.IsNullOrWhiteSpace(edge.Id) || string.IsNullOrWhiteSpace(edge.SourceId) || string.IsNullOrWhiteSpace(edge.TargetId))
            {
                warnings.Add("Skipped an edge without id, source or target.");
            }
            else
            {
                edges.Add(edge);
            }
        }

        var result = graph with
        {
            Name = string.IsNullOrWhiteSpace(graphName) ? graph.Name : graphName!,
            Nodes = nodes,
            Edges = edges,
            Clusters = graph.Clusters ?? new(),
            Metadata = graph.Metadata ?? new()
        };
        return new GraphImportResult(result, warnings);
    }

    public override GraphExportResult Export(KnowledgeGraph graph)
        => Result(JsonSerializer.Serialize(graph, GraphJson.Options));
}

/// <summary>Anthropic reference MCP memory server (JSONL entities/relations or {entities,relations}).</summary>
public sealed class McpMemoryAdapter : GraphAdapterBase
{
    public override string Name => "mcp-memory";
    public override string DisplayName => "MCP memory server (Anthropic reference)";
    public override string Description => "JSONL lines of {type:entity,name,entityType,observations} and {type:relation,from,to,relationType}; also {entities,relations}.";
    public override string ContentType => "application/x-ndjson";

    public override GraphImportResult Import(string payload, string? graphName = null)
    {
        RequirePayload(payload);
        var b = new GraphBuilder();
        var records = new List<JsonNode>();

        JsonNode? whole = null;
        try
        {
            whole = JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
        }

        if (whole is JsonObject obj && (obj.ContainsKey("entities") || obj.ContainsKey("relations")))
        {
            foreach (var e in obj["entities"] as JsonArray ?? new JsonArray())
            {
                if (e is JsonObject eo)
                {
                    eo["type"] ??= "entity";
                    records.Add(eo.DeepClone());
                }
            }
            foreach (var r in obj["relations"] as JsonArray ?? new JsonArray())
            {
                if (r is JsonObject ro)
                {
                    ro["type"] ??= "relation";
                    records.Add(ro.DeepClone());
                }
            }
        }
        else if (whole is JsonArray array)
        {
            records.AddRange(array.Where(x => x is not null).Select(x => x!.DeepClone()));
        }
        else if (whole is JsonObject single)
        {
            records.Add(single.DeepClone());
        }
        else
        {
            var lineNo = 0;
            foreach (var line in payload.Split('\n'))
            {
                lineNo++;
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }
                try
                {
                    if (JsonNode.Parse(trimmed) is { } n)
                    {
                        records.Add(n);
                    }
                }
                catch (JsonException)
                {
                    b.Warn($"Line {lineNo}: not valid JSON, skipped.");
                }
            }
            if (records.Count == 0)
            {
                throw new ArgumentException("The mcp-memory payload contains no parseable JSONL records.");
            }
        }

        var relations = new List<JsonNode>();
        foreach (var rec in records)
        {
            var type = AdapterHelpers.Str(rec, "type");
            if (type == "relation" || (type is null && AdapterHelpers.Has(rec, "from", "to")))
            {
                relations.Add(rec);
                continue;
            }
            var name = AdapterHelpers.Str(rec, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                b.Warn("Skipped an entity record without a name.");
                continue;
            }
            var id = AdapterHelpers.IdFromName(name);
            var observations = AdapterHelpers.StrList(AdapterHelpers.Get(rec, "observations"));
            if (b.TryGetNode(id, out var existing))
            {
                observations = existing.Observations.Concat(observations).Distinct().ToList();
            }
            b.AddNode(new GraphNode
            {
                Id = id,
                Name = name,
                Type = AdapterHelpers.Str(rec, "entityType") ?? "entity",
                Observations = observations,
                Properties = AdapterHelpers.Extras(rec, "type", "name", "entityType", "observations")
            });
        }

        foreach (var rel in relations)
        {
            var from = AdapterHelpers.Str(rel, "from");
            var to = AdapterHelpers.Str(rel, "to");
            var relType = AdapterHelpers.Str(rel, "relationType") ?? "related_to";
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                b.Warn("Skipped a relation record without from/to.");
                continue;
            }
            var fromId = AdapterHelpers.IdFromName(from);
            var toId = AdapterHelpers.IdFromName(to);
            foreach (var (id, name) in new[] { (fromId, from), (toId, to) })
            {
                if (!b.HasNode(id))
                {
                    b.EnsureNode(id, name, "unknown");
                    b.Warn($"Relation references unknown entity '{name}'; created a placeholder.");
                }
            }
            b.AddRelation(fromId, toId, relType);
        }

        return b.Build(graphName);
    }

    public override GraphExportResult Export(KnowledgeGraph graph)
    {
        var warnings = new List<string>();
        var index = AdapterHelpers.IndexNodes(graph);
        var lines = new List<string>();
        foreach (var node in graph.Nodes)
        {
            var observations = node.Observations.ToList();
            foreach (var extra in new[] { node.Description, node.Content })
            {
                if (!string.IsNullOrWhiteSpace(extra) && !observations.Contains(extra!))
                {
                    observations.Add(extra!);
                }
            }
            var entity = new JsonObject
            {
                ["type"] = "entity",
                ["name"] = node.Name,
                ["entityType"] = node.Type,
                ["observations"] = AdapterHelpers.ToArray(observations)
            };
            lines.Add(entity.ToJsonString());
        }
        foreach (var edge in graph.Edges)
        {
            var relation = new JsonObject
            {
                ["type"] = "relation",
                ["from"] = AdapterHelpers.NameOf(index, edge.SourceId),
                ["to"] = AdapterHelpers.NameOf(index, edge.TargetId),
                ["relationType"] = edge.Type
            };
            lines.Add(relation.ToJsonString());
        }
        if (graph.Nodes.Any(n => n.Properties.Count > 0 || n.Tags.Count > 0 || n.Aliases.Count > 0))
        {
            warnings.Add("mcp-memory has no place for properties, tags or aliases; they were omitted.");
        }
        if (graph.Edges.Any(e => e.Fact is not null || e.Weight is not null || e.Properties.Count > 0))
        {
            warnings.Add("mcp-memory relations only carry from/to/relationType; edge facts, weights and properties were omitted.");
        }
        return Result(string.Join("\n", lines) + "\n", warnings);
    }
}

/// <summary>Mem0 memories plus graph-memory relations.</summary>
public sealed class Mem0Adapter : GraphAdapterBase
{
    public override string Name => "mem0";
    public override string DisplayName => "Mem0";
    public override string Description => "Mem0 memory objects (id, memory, user_id/agent_id/run_id, categories, metadata) and graph-memory relations.";

    public override GraphImportResult Import(string payload, string? graphName = null)
    {
        var root = AdapterHelpers.ParseJson(RequirePayload(payload), Name);
        var b = new GraphBuilder();
        JsonArray memories;
        JsonArray relations;
        if (root is JsonArray array)
        {
            memories = array;
            relations = new JsonArray();
        }
        else
        {
            memories = (AdapterHelpers.Field(root, "results", "memories", "memory") as JsonArray) ?? new JsonArray();
            relations = (AdapterHelpers.Field(root, "relations", "graph_relations") as JsonArray) ?? new JsonArray();
            if (AdapterHelpers.Has(root, "memory") && AdapterHelpers.Get(root, "memory") is JsonValue)
            {
                memories = new JsonArray(root.DeepClone());
            }
            if (AdapterHelpers.Get(root, "relations") is JsonObject nested && nested["relations"] is JsonArray inner)
            {
                relations = inner;
            }
            if (memories.Count == 0 && relations.Count == 0 && !AdapterHelpers.Has(root, "results", "memories", "relations", "graph_relations"))
            {
                throw new ArgumentException("The mem0 payload has no memories or relations.");
            }
        }

        foreach (var m in memories)
        {
            var text = AdapterHelpers.Str(m, "memory", "text", "data");
            if (m is not JsonObject || string.IsNullOrWhiteSpace(text))
            {
                b.Warn("Skipped a mem0 memory without text.");
                continue;
            }
            var id = AdapterHelpers.Str(m, "id") ?? "mem-" + AdapterHelpers.Hash(text)[..16];
            var userId = AdapterHelpers.Str(m, "user_id");
            var agentId = AdapterHelpers.Str(m, "agent_id");
            var runId = AdapterHelpers.Str(m, "run_id");
            var props = AdapterHelpers.Extras(m, "id", "memory", "categories", "created_at", "updated_at");
            b.AddNode(new GraphNode
            {
                Id = id,
                Type = "memory",
                Name = AdapterHelpers.Truncate(text, 80),
                Content = text,
                Tags = AdapterHelpers.StrList(AdapterHelpers.Get(m, "categories")),
                Scope = userId ?? agentId ?? runId,
                Properties = props,
                CreatedAt = AdapterHelpers.Date(AdapterHelpers.Get(m, "created_at")),
                UpdatedAt = AdapterHelpers.Date(AdapterHelpers.Get(m, "updated_at"))
            });
        }

        foreach (var r in relations)
        {
            var source = AdapterHelpers.Str(r, "source");
            var target = AdapterHelpers.Str(r, "destination", "target");
            var rel = AdapterHelpers.Str(r, "relationship", "relation");
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(rel))
            {
                b.Warn("Skipped a mem0 relation missing source, target or relationship.");
                continue;
            }
            var s = EntityNode(b, source, AdapterHelpers.Str(r, "source_type"));
            var t = EntityNode(b, target, AdapterHelpers.Str(r, "destination_type", "target_type"));
            b.AddRelation(s, t, rel, new GraphEdge { Id = "", SourceId = s, TargetId = t, Properties = AdapterHelpers.Extras(r, "source", "source_type", "relationship", "relation", "destination", "destination_type", "target", "target_type") });
        }
        return b.Build(graphName);
    }

    private static string EntityNode(GraphBuilder b, string name, string? type)
    {
        var id = "entity-" + AdapterHelpers.IdFromName(name);
        if (!b.HasNode(id))
        {
            b.AddNode(new GraphNode { Id = id, Name = name, Type = type ?? "entity" });
        }
        return id;
    }

    public override GraphExportResult Export(KnowledgeGraph graph)
    {
        var warnings = new List<string>();
        var index = AdapterHelpers.IndexNodes(graph);
        var connected = new HashSet<string>(graph.Edges.SelectMany(e => new[] { e.SourceId, e.TargetId }), StringComparer.Ordinal);
        var results = new JsonArray();
        var entityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            if (!string.Equals(n.Type, "memory", StringComparison.OrdinalIgnoreCase) && connected.Contains(n.Id))
            {
                entityIds.Add(n.Id);
                continue;
            }
            var obj = new JsonObject();
            foreach (var kv in n.Properties)
            {
                obj[kv.Key] = kv.Value?.DeepClone();
            }
            obj["id"] = n.Id;
            obj["memory"] = n.Content ?? n.Description ?? (n.Observations.Count > 0 ? string.Join(" ", n.Observations) : n.Name);
            AdapterHelpers.SetIf(obj, "user_id", obj["user_id"] is null && n.Scope is not null ? n.Scope : null);
            AdapterHelpers.SetIfAny(obj, "categories", n.Tags);
            AdapterHelpers.SetIf(obj, "created_at", n.CreatedAt);
            AdapterHelpers.SetIf(obj, "updated_at", n.UpdatedAt);
            results.Add(obj);
        }

        var relations = new JsonArray();
        foreach (var e in graph.Edges)
        {
            var src = index.GetValueOrDefault(e.SourceId);
            var dst = index.GetValueOrDefault(e.TargetId);
            var rel = new JsonObject
            {
                ["source"] = src?.Name ?? e.SourceId,
                ["source_type"] = src?.Type ?? "entity",
                ["relationship"] = e.Type,
                ["destination"] = dst?.Name ?? e.TargetId,
                ["destination_type"] = dst?.Type ?? "entity"
            };
            foreach (var kv in e.Properties)
            {
                rel[kv.Key] = kv.Value?.DeepClone();
            }
            relations.Add(rel);
        }

        if (graph.Nodes.Any(n => n.Embedding is not null || n.Observations.Count > 0))
        {
            warnings.Add("mem0 does not model embeddings or observations on nodes; they were omitted.");
        }
        var root = new JsonObject { ["results"] = results, ["relations"] = relations };
        return Result(AdapterHelpers.Serialize(root), warnings);
    }
}

/// <summary>Zep / Graphiti temporal knowledge graph.</summary>
public sealed class GraphitiAdapter : GraphAdapterBase
{
    public override string Name => "graphiti";
    public override string DisplayName => "Graphiti / Zep temporal graph";
    public override string Description => "Entity nodes (uuid, name, labels, summary, group_id), entity edges (fact, valid_at, invalid_at, expired_at) and episodic nodes.";

    public override GraphImportResult Import(string payload, string? graphName = null)
    {
        var root = AdapterHelpers.ParseJson(RequirePayload(payload), Name);
        var b = new GraphBuilder();
        if (root is not JsonObject)
        {
            throw new ArgumentException("The graphiti payload must be an object with nodes, edges and episodes.");
        }
        var nodes = AdapterHelpers.Field(root, "nodes", "entity_nodes") as JsonArray ?? new JsonArray();
        var edges = AdapterHelpers.Field(root, "edges", "entity_edges") as JsonArray ?? new JsonArray();
        var episodes = AdapterHelpers.Field(root, "episodes", "episodic_nodes") as JsonArray ?? new JsonArray();
        if (nodes.Count + edges.Count + episodes.Count == 0 && !AdapterHelpers.Has(root, "nodes", "edges", "episodes", "entity_nodes", "entity_edges", "episodic_nodes"))
        {
            throw new ArgumentException("The graphiti payload has no nodes, edges or episodes.");
        }

        foreach (var n in nodes)
        {
            var name = AdapterHelpers.Str(n, "name");
            var id = AdapterHelpers.Str(n, "uuid", "id");
            if (n is not JsonObject || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id))
            {
                b.Warn("Skipped a graphiti node without uuid/name.");
                continue;
            }
            var labels = AdapterHelpers.StrList(AdapterHelpers.Get(n, "labels"));
            var props = AdapterHelpers.Extras(n, "uuid", "id", "name", "summary", "group_id", "created_at");
            if (labels.Count > 0)
            {
                props["labels"] = AdapterHelpers.ToArray(labels);
            }
            b.AddNode(new GraphNode
            {
                Id = id,
                Name = name,
                Type = labels.FirstOrDefault(l => !l.Equals("Entity", StringComparison.OrdinalIgnoreCase)) ?? "entity",
                Description = AdapterHelpers.Str(n, "summary"),
                Scope = AdapterHelpers.Str(n, "group_id"),
                CreatedAt = AdapterHelpers.Date(AdapterHelpers.Get(n, "created_at")),
                Properties = props
            });
        }

        foreach (var ep in episodes)
        {
            var id = AdapterHelpers.Str(ep, "uuid", "id");
            var name = AdapterHelpers.Str(ep, "name") ?? id;
            if (ep is not JsonObject || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                b.Warn("Skipped a graphiti episode without uuid/name.");
                continue;
            }
            b.AddNode(new GraphNode
            {
                Id = id,
                Name = name,
                Type = "episode",
                Content = AdapterHelpers.Str(ep, "content"),
                Source = AdapterHelpers.Str(ep, "source"),
                Description = AdapterHelpers.Str(ep, "source_description"),
                Scope = AdapterHelpers.Str(ep, "group_id"),
                ValidFrom = AdapterHelpers.Date(AdapterHelpers.Get(ep, "valid_at")),
                CreatedAt = AdapterHelpers.Date(AdapterHelpers.Get(ep, "created_at")),
                Properties = AdapterHelpers.Extras(ep, "uuid", "id", "name", "content", "source", "source_description", "group_id", "valid_at", "created_at")
            });
        }

        foreach (var e in edges)
        {
            var src = AdapterHelpers.Str(e, "source_node_uuid", "source");
            var dst = AdapterHelpers.Str(e, "target_node_uuid", "target");
            if (e is not JsonObject || string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst))
            {
                b.Warn("Skipped a graphiti edge without source/target uuid.");
                continue;
            }
            var type = AdapterHelpers.Str(e, "name") ?? "RELATES_TO";
            var id = AdapterHelpers.Str(e, "uuid", "id") ?? AdapterHelpers.EdgeId(src, type, dst);
            b.AddRelation(src, dst, type, new GraphEdge
            {
                Id = id,
                SourceId = src,
                TargetId = dst,
                Fact = AdapterHelpers.Str(e, "fact"),
                ValidFrom = AdapterHelpers.Date(AdapterHelpers.Get(e, "valid_at")),
                ValidTo = AdapterHelpers.Date(AdapterHelpers.Get(e, "invalid_at")),
                ExpiredAt = AdapterHelpers.Date(AdapterHelpers.Get(e, "expired_at")),
                CreatedAt = AdapterHelpers.Date(AdapterHelpers.Get(e, "created_at")),
                Scope = AdapterHelpers.Str(e, "group_id"),
                Properties = AdapterHelpers.Extras(e, "uuid", "id", "name", "fact", "source_node_uuid", "target_node_uuid", "source", "target", "valid_at", "invalid_at", "expired_at", "created_at", "group_id")
            });
        }
        return b.Build(graphName);
    }

    public override GraphExportResult Export(KnowledgeGraph graph)
    {
        var nodes = new JsonArray();
        var episodes = new JsonArray();
        foreach (var n in graph.Nodes)
        {
            if (n.Type == "episode")
            {
                var ep = AdapterHelpers.ToObject(n.Properties);
                ep["uuid"] = n.Id;
                ep["name"] = n.Name;
                AdapterHelpers.SetIf(ep, "content", n.Content);
                AdapterHelpers.SetIf(ep, "source", n.Source);
                AdapterHelpers.SetIf(ep, "source_description", n.Description);
                AdapterHelpers.SetIf(ep, "group_id", n.Scope);
                AdapterHelpers.SetIf(ep, "valid_at", n.ValidFrom);
                AdapterHelpers.SetIf(ep, "created_at", n.CreatedAt);
                episodes.Add(ep);
                continue;
            }
            var obj = new JsonObject { ["uuid"] = n.Id, ["name"] = n.Name };
            var attributes = AdapterHelpers.ToObject(n.Properties.Where(kv => kv.Key != "labels"));
            if (n.Properties.TryGetValue("labels", out var labels) && labels is JsonArray)
            {
                obj["labels"] = labels.DeepClone();
            }
            else
            {
                obj["labels"] = AdapterHelpers.ToArray(new[] { "Entity", n.Type }.Distinct());
            }
            AdapterHelpers.SetIf(obj, "summary", n.Description);
            AdapterHelpers.SetIf(obj, "group_id", n.Scope);
            AdapterHelpers.SetIf(obj, "created_at", n.CreatedAt);
            foreach (var kv in attributes)
            {
                if (obj[kv.Key] is null)
                {
                    obj[kv.Key] = kv.Value?.DeepClone();
                }
            }
            nodes.Add(obj);
        }

        var edges = new JsonArray();
        foreach (var e in graph.Edges)
        {
            var obj = AdapterHelpers.ToObject(e.Properties);
            obj["uuid"] = e.Id;
            obj["source_node_uuid"] = e.SourceId;
            obj["target_node_uuid"] = e.TargetId;
            obj["name"] = e.Type;
            obj["fact"] = e.Fact ?? e.Label ?? e.Type;
            AdapterHelpers.SetIf(obj, "group_id", e.Scope);
            AdapterHelpers.SetIf(obj, "valid_at", e.ValidFrom);
            AdapterHelpers.SetIf(obj, "invalid_at", e.ValidTo);
            AdapterHelpers.SetIf(obj, "expired_at", e.ExpiredAt);
            AdapterHelpers.SetIf(obj, "created_at", e.CreatedAt);
            edges.Add(obj);
        }
        var root = new JsonObject { ["nodes"] = nodes, ["edges"] = edges, ["episodes"] = episodes };
        return Result(AdapterHelpers.Serialize(root));
    }
}

/// <summary>Letta / MemGPT core memory blocks and archival passages.</summary>
public sealed class LettaAdapter : GraphAdapterBase
{
    public override string Name => "letta";
    public override string DisplayName => "Letta / MemGPT";
    public override string Description => "Core memory blocks (label, value, limit) and archival passages (text, embedding, metadata).";

    public override GraphImportResult Import(string payload, string? graphName = null)
    {
        var root = AdapterHelpers.ParseJson(RequirePayload(payload), Name);
        var b = new GraphBuilder();
        var blocks = new List<JsonNode>();
        var passages = new List<JsonNode>();

        if (root is JsonArray array)
        {
            foreach (var item in array.OfType<JsonNode>())
            {
                (AdapterHelpers.Has(item, "label") ? blocks : passages).Add(item);
            }
        }
        else if (root is JsonObject)
        {
            var memory = AdapterHelpers.Get(root, "memory");
            foreach (var candidate in new[]
            {
                AdapterHelpers.Get(root, "blocks"),
                AdapterHelpers.Get(root, "memory_blocks"),
                AdapterHelpers.Get(root, "core_memory"),
                AdapterHelpers.Get(memory, "blocks"),
                memory is JsonArray ? memory : null
            })
            {
                if (candidate is JsonArray blockArray)
                {
                    blocks.AddRange(blockArray.OfType<JsonNode>());
                }
                else if (candidate is JsonObject blockMap && !AdapterHelpers.Has(blockMap, "label"))
                {
                    foreach (var kv in blockMap)
                    {
                        if (kv.Value is JsonObject bo)
                        {
                            var clone = (JsonObject)bo.DeepClone();
                            clone["label"] ??= kv.Key;
                            blocks.Add(clone);
                        }
                        else if (AdapterHelpers.Str(kv.Value) is { } text)
                        {
                            blocks.Add(new JsonObject { ["label"] = kv.Key, ["value"] = text });
                        }
                    }
                }
            }
            foreach (var candidate in new[]
            {
                AdapterHelpers.Get(root, "passages"),
                AdapterHelpers.Get(root, "archival_memory"),
                AdapterHelpers.Get(root, "archival_passages")
            })
            {
                if (candidate is JsonArray passageArray)
                {
                    passages.AddRange(passageArray.OfType<JsonNode>());
                }
            }
        }

        foreach (var block in blocks)
        {
            var label = AdapterHelpers.Str(block, "label");
            if (string.IsNullOrWhiteSpace(label))
            {
                b.Warn("Skipped a memory block without a label.");
                continue;
            }
            b.AddNode(new GraphNode
            {
                Id = AdapterHelpers.Str(block, "id") ?? "block-" + AdapterHelpers.Slug(label),
                Type = "memory_block",
                Name = label,
                Content = AdapterHelpers.Str(block, "value") ?? string.Empty,
                Description = AdapterHelpers.Str(block, "description"),
                Properties = AdapterHelpers.Extras(block, "id", "label", "value", "description"),
                CreatedAt = AdapterHelpers.Date(AdapterHelpers.Get(block, "created_at")),
                UpdatedAt = AdapterHelpers.Date(AdapterHelpers.Get(block, "updated_at"))
            });
        }
        foreach (var passage in passages)
        {
            var text = AdapterHelpers.Str(passage, "text");
            if (string.IsNullOrWhiteSpace(text))
            {
                b.Warn("Skipped an archival passage without text.");
                continue;
            }
            var props = AdapterHelpers.Extras(passage, "id", "text", "embedding", "created_at", "metadata");
            if (AdapterHelpers.Get(passage, "metadata") is { } meta)
            {
                props["metadata"] = meta.DeepClone();
            }
            b.AddNode(new GraphNode
            {
                Id = AdapterHelpers.Str(passage, "id") ?? "passage-" + AdapterHelpers.Hash(text)[..16],
                Type = "passage",
                Name = AdapterHelpers.Truncate(text, 80),
                Content = text,
                Embedding = AdapterHelpers.FloatArray(AdapterHelpers.Get(passage, "embedding")),
                CreatedAt = AdapterHelpers.Date(AdapterHelpers.Get(passage, "created_at")),
                Properties = props
            });
        }
        if (b.Nodes.Count == 0 && b.Warnings.Count == 0)
        {
            throw new ArgumentException("The letta payload contains no memory blocks or passages.");
        }
        return b.Build(graphName);
    }

    public override GraphExportResult Export(KnowledgeGraph graph)
    {
        var warnings = new List<string>();
        var blocks = new JsonArray();
        var passages = new JsonArray();
        foreach (var n in graph.Nodes)
        {
            if (n.Type == "memory_block")
            {
                var obj = AdapterHelpers.ToObject(n.Properties);
                obj["id"] = n.Id;
                obj["label"] = n.Name;
                obj["value"] = n.Content ?? string.Empty;
                obj["limit"] ??= Math.Max(2000, (n.Content?.Length ?? 0) + 500);
                AdapterHelpers.SetIf(obj, "description", n.Description);
                blocks.Add(obj);
                continue;
            }
            if (n.Type != "passage")
            {
                warnings.Add($"Node '{n.Name}' of type '{n.Type}' was exported as an archival passage.");
            }
            var passage = AdapterHelpers.ToObject(n.Properties);
            passage["id"] = n.Id;
            passage["text"] = n.Content ?? n.Description ?? (n.Observations.Count > 0 ? string.Join("\n", n.Observations) : n.Name);
            AdapterHelpers.SetIf(passage, "embedding", AdapterHelpers.FloatsToJson(n.Embedding));
            AdapterHelpers.SetIf(passage, "created_at", n.CreatedAt);
            passages.Add(passage);
        }
        if (graph.Edges.Count > 0)
        {
            warnings.Add("Letta memory has no edges; relationships were omitted.");
        }
        var root = new JsonObject { ["blocks"] = blocks, ["passages"] = passages };
        return Result(AdapterHelpers.Serialize(root), warnings);
    }
}

/// <summary>Supermemory documents, memories (with versioned relations) and graph-style nodes/edges.</summary>
public sealed class SupermemoryAdapter : GraphAdapterBase
{
    public override string Name => "supermemory";
    public override string DisplayName => "Supermemory";
    public override string Description => "Supermemory documents and memories (version, isLatest, memoryRelations updates/extends/derives) or {nodes,edges}.";

    public override GraphImportResult Import(string payload, string? graphName = null)
    {
        var root = AdapterHelpers.ParseJson(RequirePayload(payload), Name);
        var b = new GraphBuilder();
        var docs = new List<JsonNode>();
        var mems = new List<JsonNode>();
        JsonArray? gNodes = null;
        JsonArray? gEdges = null;

        if (root is JsonArray array)
        {
            foreach (var item in array.OfType<JsonNode>())
            {
                (AdapterHelpers.Has(item, "memory") ? mems : docs).Add(item);
            }
        }
        else if (root is JsonObject)
        {
            docs.AddRange((AdapterHelpers.Get(root, "documents") as JsonArray)?.OfType<JsonNode>() ?? Enumerable.Empty<JsonNode>());
            mems.AddRange((AdapterHelpers.Get(root, "memories") as JsonArray)?.OfType<JsonNode>() ?? Enumerable.Empty<JsonNode>());
            gNodes = AdapterHelpers.Get(root, "nodes") as JsonArray;
            gEdges = AdapterHelpers.Get(root, "edges") as JsonArray;
            if (docs.Count + mems.Count == 0 && gNodes is null && gEdges is null && !AdapterHelpers.Has(root, "documents", "memories"))
            {
                throw new ArgumentException("The supermemory payload has no documents, memories, nodes or edges.");
            }
        }

        foreach (var d in docs)
        {
            var id = AdapterHelpers.Str(d, "id") ?? AdapterHelpers.Str(d, "customId");
            if (d is not JsonObject || string.IsNullOrWhiteSpace(id))
            {
                b.Warn("Skipped a supermemory document without id.");
                continue;
            }
            var tags = AdapterHelpers.StrList(AdapterHelpers.Get(d, "containerTags"));
            var props = AdapterHelpers.Extras(d, "id", "content", "title", "summary", "containerTags", "createdAt", "updatedAt");
            if (props.Remove("type", out var docType) && docType is not null)
            {
                props["supermemory_type"] = docType;
            }
            b.AddNode(new GraphNode
            {
                Id = id,
                Type = "document",
                Name = AdapterHelpers.Str(d, "title") ?? AdapterHelpers.Str(d, "customId") ?? id,
                Content = AdapterHelpers.Str(d, "content"),
                Description = AdapterHelpers.Str(d, "summary"),
                Tags = tags,
                Scope = tags.FirstOrDefault(),
                Properties = props,
                CreatedAt = AdapterHelpers.Date(AdapterHelpers.Get(d, "createdAt")),
                UpdatedAt = AdapterHelpers.Date(AdapterHelpers.Get(d, "updatedAt"))
            });
        }

        var relations = new List<(string From, string To, string Type)>();
        foreach (var m in mems)
        {
            var text = AdapterHelpers.Str(m, "memory", "content");
            var id = AdapterHelpers.Str(m, "id");
            if (m is not JsonObject || string.IsNullOrWhiteSpace(text))
            {
                b.Warn("Skipped a supermemory memory without text.");
                continue;
            }
            id ??= "mem-" + AdapterHelpers.Hash(text)[..16];
            var tags = AdapterHelpers.StrList(AdapterHelpers.Get(m, "containerTags"));
            var space = AdapterHelpers.Str(m, "spaceId");
            var props = AdapterHelpers.Extras(m, "id", "memory", "content", "containerTags", "version", "isLatest", "memoryRelations", "createdAt", "updatedAt");
            b.AddNode(new GraphNode
            {
                Id = id,
                Type = "memory",
                Name = AdapterHelpers.Truncate(text, 80),
                Content = text,
                Tags = tags,
                Scope = space ?? tags.FirstOrDefault(),
                Version = AdapterHelpers.Int(AdapterHelpers.Get(m, "version")),
                IsLatest = AdapterHelpers.Bool(AdapterHelpers.Get(m, "isLatest")) ?? true,
                Properties = props,
                CreatedAt = AdapterHelpers.Date(AdapterHelpers.Get(m, "createdAt")),
                UpdatedAt = AdapterHelpers.Date(AdapterHelpers.Get(m, "updatedAt"))
            });
            if (AdapterHelpers.Get(m, "memoryRelations") is JsonObject rels)
            {
                foreach (var kv in rels)
                {
                    relations.Add((id, kv.Key, AdapterHelpers.Str(kv.Value) ?? "extends"));
                }
            }
        }
        foreach (var (from, to, type) in relations)
        {
            b.AddRelation(from, to, type);
        }

        if (gNodes is not null)
        {
            foreach (var n in gNodes)
            {
                var id = AdapterHelpers.Str(n, "id");
                if (n is not JsonObject || string.IsNullOrWhiteSpace(id))
                {
                    b.Warn("Skipped a graph node without id.");
                    continue;
                }
                var type = AdapterHelpers.Str(n, "type") ?? "document";
                b.AddNode(new GraphNode
                {
                    Id = id,
                    Type = type,
                    Name = AdapterHelpers.Str(n, "title", "label", "name") ?? id,
                    Content = AdapterHelpers.Str(n, "content", "memory"),
                    Description = AdapterHelpers.Str(n, "summary", "description"),
                    Properties = AdapterHelpers.Extras(n, "id", "type", "title", "label", "name", "content", "memory", "summary", "description")
                });
            }
        }
        if (gEdges is not null)
        {
            foreach (var e in gEdges)
            {
                var s = AdapterHelpers.Str(e, "source", "from");
                var t = AdapterHelpers.Str(e, "target", "to");
                if (string.IsNullOrWhiteSpace(s) || string.IsNullOrWhiteSpace(t))
                {
                    b.Warn("Skipped a graph edge without source/target.");
                    continue;
                }
                b.AddRelation(s, t, AdapterHelpers.Str(e, "type", "label", "relation") ?? "related_to");
            }
        }
        return b.Build(graphName);
    }

    public override GraphExportResult Export(KnowledgeGraph graph)
    {
        var warnings = new List<string>();
        var docs = new JsonArray();
        var mems = new JsonArray();
        var memoryIds = new HashSet<string>(graph.Nodes.Where(n => n.Type == "memory").Select(n => n.Id), StringComparer.Ordinal);
        var standard = new HashSet<string> { "updates", "extends", "derives" };
        static IEnumerable<string> TagsOrScope(GraphNode n)
            => n.Tags.Count > 0 ? n.Tags : (n.Scope is null ? Array.Empty<string>() : new[] { n.Scope });

        foreach (var n in graph.Nodes)
        {
            var obj = AdapterHelpers.ToObject(n.Properties);
            if (n.Type == "memory")
            {
                obj["id"] = n.Id;
                obj["memory"] = n.Content ?? n.Description ?? n.Name;
                AdapterHelpers.SetIfAny(obj, "containerTags", TagsOrScope(n));
                obj["version"] = n.Version ?? 1;
                obj["isLatest"] = n.IsLatest;
                AdapterHelpers.SetIf(obj, "createdAt", n.CreatedAt);
                AdapterHelpers.SetIf(obj, "updatedAt", n.UpdatedAt);
                var relations = new JsonObject();
                foreach (var e in graph.Edges.Where(e => e.SourceId == n.Id))
                {
                    relations[e.TargetId] = e.Type;
                    if (!standard.Contains(e.Type))
                    {
                        warnings.Add($"Relation type '{e.Type}' is not one of updates/extends/derives.");
                    }
                }
                if (relations.Count > 0)
                {
                    obj["memoryRelations"] = relations;
                }
                mems.Add(obj);
                continue;
            }
            if (obj.Remove("supermemory_type", out var docType))
            {
                obj["type"] = docType;
            }
            obj["id"] = n.Id;
            obj["title"] = n.Name;
            AdapterHelpers.SetIf(obj, "content", n.Content);
            AdapterHelpers.SetIf(obj, "summary", n.Description);
            AdapterHelpers.SetIfAny(obj, "containerTags", TagsOrScope(n));
            AdapterHelpers.SetIf(obj, "createdAt", n.CreatedAt);
            AdapterHelpers.SetIf(obj, "updatedAt", n.UpdatedAt);
            docs.Add(obj);
            if (n.Type != "document")
            {
                warnings.Add($"Node '{n.Name}' of type '{n.Type}' was exported as a document.");
            }
        }
        var dropped = graph.Edges.Count(e => !memoryIds.Contains(e.SourceId));
        if (dropped > 0)
        {
            warnings.Add($"{dropped} edge(s) not originating from a memory node cannot be expressed as memoryRelations and were omitted.");
        }
        var root = new JsonObject { ["documents"] = docs, ["memories"] = mems };
        return Result(AdapterHelpers.Serialize(root), warnings.Distinct().ToList());
    }
}
