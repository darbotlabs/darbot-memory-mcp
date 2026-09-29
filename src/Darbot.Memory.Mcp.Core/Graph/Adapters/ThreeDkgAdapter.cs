using System.Text.Json.Nodes;

namespace Darbot.Memory.Mcp.Core.Graph.Adapters;

/// <summary>3DKG graphSnapshot (entities/relationships/clusters/flashcards), triples and schema path patterns.</summary>
public sealed class ThreeDkgAdapter : GraphAdapterBase
{
    public const string PatternType = "schema_path_pattern";
    public const string FlashcardType = "flashcard";
    private const string DarbotKey = "darbot";

    private static readonly string[] MetadataPassThrough =
        { "semanticPaths", "connectors", "workflows", "agentConfigs", "integrationIndexes", "viewState" };

    public override string Name => "3dkg";
    public override string DisplayName => "3DKG";
    public override string Description => "3DKG graphSnapshot (entities, relationships, clusters, flashcards), bare triple arrays and schemaPathPattern objects.";

    public override GraphImportResult Import(string payload, string? graphName = null)
    {
        var root = AdapterHelpers.ParseJson(RequirePayload(payload), Name);
        var b = new GraphBuilder();
        string? snapshotId = null;
        string? snapshotName = null;

        if (root is JsonArray array)
        {
            foreach (var item in array)
            {
                if (AdapterHelpers.Has(item, "subject", "predicate"))
                {
                    ImportTriple(b, item!);
                }
                else if (AdapterHelpers.Has(item, "steps") && AdapterHelpers.Has(item, "id", "name"))
                {
                    ImportPattern(b, item!);
                }
                else
                {
                    b.Warn("Skipped an array element that is neither a triple nor a schemaPathPattern.");
                }
            }
        }
        else if (root is JsonObject)
        {
            var snap = AdapterHelpers.Get(root, "graphSnapshot") ?? root;
            var recognised = AdapterHelpers.Has(snap, "entities", "relationships", "clusters", "flashcards", "triples", "patterns", "schemaPathPatterns")
                || (AdapterHelpers.Has(snap, "steps") && AdapterHelpers.Has(snap, "id", "name"))
                || AdapterHelpers.Has(snap, "subject", "predicate");
            if (!recognised)
            {
                throw new ArgumentException("The 3dkg payload is not a graphSnapshot, triple list or schemaPathPattern.");
            }
            snapshotId = AdapterHelpers.Str(snap, "id");
            snapshotName = AdapterHelpers.Str(snap, "name");
            if (AdapterHelpers.Str(snap, "version") is { } version)
            {
                b.Metadata["version"] = version;
            }
            if (AdapterHelpers.Get(snap, "metadata") is { } meta)
            {
                b.Metadata["graphMetadata"] = meta.DeepClone();
            }
            foreach (var key in MetadataPassThrough)
            {
                if (AdapterHelpers.Get(snap, key) is { } value)
                {
                    b.Metadata[key] = value.DeepClone();
                }
            }

            foreach (var e in (AdapterHelpers.Get(snap, "entities") as JsonArray) ?? new JsonArray())
            {
                ImportEntity(b, e);
            }
            foreach (var f in (AdapterHelpers.Get(snap, "flashcards") as JsonArray) ?? new JsonArray())
            {
                ImportFlashcard(b, f);
            }
            foreach (var p in AdapterHelpers.Field(snap, "schemaPathPatterns", "patterns") as JsonArray ?? new JsonArray())
            {
                ImportPattern(b, p);
            }
            if (AdapterHelpers.Has(snap, "steps") && AdapterHelpers.Has(snap, "id", "name") && !AdapterHelpers.Has(snap, "entities"))
            {
                ImportPattern(b, snap!);
            }
            foreach (var t in (AdapterHelpers.Get(snap, "triples") as JsonArray) ?? new JsonArray())
            {
                ImportTriple(b, t);
            }
            if (AdapterHelpers.Has(snap, "subject", "predicate"))
            {
                ImportTriple(b, snap!);
            }
            foreach (var r in (AdapterHelpers.Get(snap, "relationships") as JsonArray) ?? new JsonArray())
            {
                ImportRelationship(b, r);
            }
            foreach (var c in (AdapterHelpers.Get(snap, "clusters") as JsonArray) ?? new JsonArray())
            {
                ImportCluster(b, c);
            }
        }
        else
        {
            throw new ArgumentException("The 3dkg payload must be a JSON object or array.");
        }

        var result = b.Build(graphName ?? snapshotName);
        return snapshotId is null ? result : result with { Graph = result.Graph with { Id = snapshotId } };
    }

    private static void ImportEntity(GraphBuilder b, JsonNode? e)
    {
        var name = AdapterHelpers.Str(e, "name");
        var id = AdapterHelpers.Str(e, "id") ?? (name is null ? null : "e-" + AdapterHelpers.IdFromName(name));
        if (e is not JsonObject || id is null)
        {
            b.Warn("Skipped an entity without id or name.");
            return;
        }
        var props = new Dictionary<string, JsonNode?>();
        AdapterHelpers.MergeInto(props, AdapterHelpers.Get(e, "properties"));
        var node = new GraphNode
        {
            Id = id,
            Name = name ?? id,
            Type = AdapterHelpers.Str(e, "type") ?? "entity",
            Description = AdapterHelpers.Str(e, "description"),
            Aliases = AdapterHelpers.StrList(AdapterHelpers.Get(e, "aliases")),
            Embedding = AdapterHelpers.FloatArray(AdapterHelpers.Get(e, "embedding")),
            Position = AdapterHelpers.Position(AdapterHelpers.Get(e, "position")),
            Style = AdapterHelpers.Clone(AdapterHelpers.Get(e, "style")),
            Cluster = AdapterHelpers.Str(e, "cluster"),
            CreatedAt = AdapterHelpers.Date(AdapterHelpers.Get(e, "createdAt")),
            UpdatedAt = AdapterHelpers.Date(AdapterHelpers.Get(e, "updatedAt")),
            Properties = props
        };
        var src = AdapterHelpers.Get(e, "source");
        if (src is JsonValue)
        {
            node = node with { Source = AdapterHelpers.Str(src) };
        }
        else if (src is not null)
        {
            props["_source"] = src.DeepClone();
        }
        var extras = AdapterHelpers.Extras(e, "id", "type", "name", "description", "aliases", "properties", "embedding", "position", "style", "cluster", "source", "createdAt", "updatedAt");
        if (extras.Count > 0)
        {
            props["_3dkg"] = AdapterHelpers.ToObject(extras);
        }
        b.AddNode(ApplyDarbotBlock(node));
    }

    /// <summary>Restores darbot-only node fields (content, observations, scope...) stashed under properties.darbot and tags.</summary>
    private static GraphNode ApplyDarbotBlock(GraphNode node)
    {
        if (node.Properties.Remove("tags", out var tags) && tags is JsonArray)
        {
            node = node with { Tags = AdapterHelpers.StrList(tags) };
        }
        else if (tags is not null)
        {
            node.Properties["tags"] = tags;
        }
        if (node.Properties.TryGetValue(DarbotKey, out var block) && block is JsonObject d)
        {
            node.Properties.Remove(DarbotKey);
            node = node with
            {
                Content = AdapterHelpers.Str(d["content"]),
                Observations = AdapterHelpers.StrList(d["observations"]),
                Scope = AdapterHelpers.Str(d["scope"]),
                ValidFrom = AdapterHelpers.Date(d["validFrom"]),
                ValidTo = AdapterHelpers.Date(d["validTo"]),
                Version = AdapterHelpers.Int(d["version"]),
                IsLatest = AdapterHelpers.Bool(d["isLatest"]) ?? true,
                Tags = node.Tags.Count > 0 ? node.Tags : AdapterHelpers.StrList(d["tags"])
            };
        }
        return node;
    }

    private static void ImportFlashcard(GraphBuilder b, JsonNode? f)
    {
        var id = AdapterHelpers.Str(f, "id");
        if (f is not JsonObject || id is null)
        {
            b.Warn("Skipped a flashcard without id.");
            return;
        }
        var props = AdapterHelpers.Extras(f, "id", "title", "content", "zone", "tags", "position", "createdAt", "updatedAt");
        b.AddNode(new GraphNode
        {
            Id = id,
            Type = FlashcardType,
            Name = AdapterHelpers.Str(f, "title") ?? id,
            Content = AdapterHelpers.Str(f, "content"),
            Scope = AdapterHelpers.Str(f, "zone"),
            Tags = AdapterHelpers.StrList(AdapterHelpers.Get(f, "tags")),
            Position = AdapterHelpers.Position(AdapterHelpers.Get(f, "position")),
            CreatedAt = AdapterHelpers.Date(AdapterHelpers.Get(f, "createdAt")),
            UpdatedAt = AdapterHelpers.Date(AdapterHelpers.Get(f, "updatedAt")),
            Properties = props
        });
    }

    private static void ImportPattern(GraphBuilder b, JsonNode? p)
    {
        var id = AdapterHelpers.Str(p, "id");
        var name = AdapterHelpers.Str(p, "name");
        if (p is not JsonObject || (id is null && name is null))
        {
            b.Warn("Skipped a schemaPathPattern without id or name.");
            return;
        }
        id ??= "spp-" + AdapterHelpers.Slug(name);
        b.AddNode(new GraphNode
        {
            Id = id,
            Type = PatternType,
            Name = name ?? id,
            Description = AdapterHelpers.Str(p, "description"),
            Content = AdapterHelpers.Str(p, "scenario"),
            Tags = AdapterHelpers.StrList(AdapterHelpers.Get(p, "tags")),
            CreatedAt = AdapterHelpers.Date(AdapterHelpers.Get(p, "createdAt")),
            UpdatedAt = AdapterHelpers.Date(AdapterHelpers.Get(p, "updatedAt")),
            Properties = AdapterHelpers.Extras(p, "id", "name", "description", "scenario", "tags", "createdAt", "updatedAt")
        });
    }

    private static string TripleNode(GraphBuilder b, string name, string? type)
    {
        var id = "e-" + AdapterHelpers.IdFromName(name);
        if (!b.HasNode(id))
        {
            b.AddNode(new GraphNode { Id = id, Name = name, Type = type ?? "entity" });
        }
        return id;
    }

    private static void ImportTriple(GraphBuilder b, JsonNode? t)
    {
        var s = AdapterHelpers.Str(t, "subject");
        var p = AdapterHelpers.Str(t, "predicate");
        var o = AdapterHelpers.Str(t, "object");
        if (t is not JsonObject || string.IsNullOrWhiteSpace(s) || string.IsNullOrWhiteSpace(p) || string.IsNullOrWhiteSpace(o))
        {
            b.Warn("Skipped a triple missing subject, predicate or object.");
            return;
        }
        var sid = TripleNode(b, s, AdapterHelpers.Str(t, "subjectType"));
        var oid = TripleNode(b, o, AdapterHelpers.Str(t, "objectType"));
        var edge = new GraphEdge
        {
            Id = "",
            SourceId = sid,
            TargetId = oid,
            Confidence = AdapterHelpers.Num(AdapterHelpers.Get(t, "confidence")),
            Properties = AdapterHelpers.Extras(t, "subject", "predicate", "object", "subjectType", "objectType", "confidence", "source")
        };
        var src = AdapterHelpers.Get(t, "source");
        if (src is JsonValue)
        {
            edge = edge with { Source = AdapterHelpers.Str(src) };
        }
        else if (src is not null)
        {
            edge.Properties["_source"] = src.DeepClone();
        }
        b.AddRelation(sid, oid, p, edge);
    }

    private static void ImportRelationship(GraphBuilder b, JsonNode? r)
    {
        var s = AdapterHelpers.Str(r, "sourceId");
        var t = AdapterHelpers.Str(r, "targetId");
        if (r is not JsonObject || string.IsNullOrWhiteSpace(s) || string.IsNullOrWhiteSpace(t))
        {
            b.Warn("Skipped a relationship without sourceId/targetId.");
            return;
        }
        var props = new Dictionary<string, JsonNode?>();
        AdapterHelpers.MergeInto(props, AdapterHelpers.Get(r, "properties"));
        var edge = new GraphEdge
        {
            Id = AdapterHelpers.Str(r, "id") ?? "",
            SourceId = s,
            TargetId = t,
            Label = AdapterHelpers.Str(r, "label"),
            Weight = AdapterHelpers.Num(AdapterHelpers.Get(r, "weight")),
            Bidirectional = AdapterHelpers.Bool(AdapterHelpers.Get(r, "bidirectional")) ?? false,
            Style = AdapterHelpers.Clone(AdapterHelpers.Get(r, "style")),
            CreatedAt = AdapterHelpers.Date(AdapterHelpers.Get(r, "createdAt")),
            Properties = props
        };
        var src = AdapterHelpers.Get(r, "source");
        if (src is JsonValue)
        {
            edge = edge with { Source = AdapterHelpers.Str(src) };
        }
        else if (src is not null)
        {
            props["_source"] = src.DeepClone();
        }
        var extras = AdapterHelpers.Extras(r, "id", "type", "sourceId", "targetId", "label", "weight", "properties", "style", "bidirectional", "source", "createdAt");
        if (extras.Count > 0)
        {
            props["_3dkg"] = AdapterHelpers.ToObject(extras);
        }
        if (props.Remove(DarbotKey, out var block) && block is JsonObject d)
        {
            edge = edge with
            {
                Fact = AdapterHelpers.Str(d["fact"]),
                Confidence = AdapterHelpers.Num(d["confidence"]),
                Scope = AdapterHelpers.Str(d["scope"]),
                ValidFrom = AdapterHelpers.Date(d["validFrom"]),
                ValidTo = AdapterHelpers.Date(d["validTo"]),
                ExpiredAt = AdapterHelpers.Date(d["expiredAt"])
            };
        }
        b.EnsureNode(s);
        b.EnsureNode(t);
        b.AddRelation(s, t, AdapterHelpers.Str(r, "type") ?? "related_to", edge);
    }

    private static void ImportCluster(GraphBuilder b, JsonNode? c)
    {
        var id = AdapterHelpers.Str(c, "id");
        if (c is not JsonObject || id is null)
        {
            b.Warn("Skipped a cluster without id.");
            return;
        }
        var members = b.Nodes.Values.Where(n => n.Cluster == id).Select(n => n.Id).ToList();
        b.AddCluster(new GraphCluster
        {
            Id = id,
            Name = AdapterHelpers.Str(c, "name"),
            NodeIds = members,
            Properties = AdapterHelpers.Extras(c, "id", "name", "entityCount")
        });
    }

    public override GraphExportResult Export(KnowledgeGraph graph)
    {
        var warnings = new List<string>();
        var entities = new JsonArray();
        var flashcards = new JsonArray();
        var patterns = new JsonArray();

        foreach (var n in graph.Nodes)
        {
            switch (n.Type)
            {
                case PatternType:
                    patterns.Add(ExportPattern(n));
                    break;
                case FlashcardType:
                    flashcards.Add(ExportFlashcard(n));
                    break;
                default:
                    entities.Add(ExportEntity(n));
                    break;
            }
        }

        var relationships = new JsonArray();
        foreach (var e in graph.Edges)
        {
            var props = new Dictionary<string, JsonNode?>(e.Properties);
            props.Remove("_source");
            props.Remove("_3dkg");
            var block = new JsonObject();
            AdapterHelpers.SetIf(block, "fact", e.Fact);
            AdapterHelpers.SetIf(block, "confidence", e.Confidence);
            AdapterHelpers.SetIf(block, "scope", e.Scope);
            AdapterHelpers.SetIf(block, "validFrom", e.ValidFrom);
            AdapterHelpers.SetIf(block, "validTo", e.ValidTo);
            AdapterHelpers.SetIf(block, "expiredAt", e.ExpiredAt);
            var pObj = AdapterHelpers.ToObject(props);
            if (block.Count > 0)
            {
                pObj[DarbotKey] = block;
            }
            var obj = new JsonObject
            {
                ["id"] = e.Id,
                ["type"] = e.Type,
                ["sourceId"] = e.SourceId,
                ["targetId"] = e.TargetId
            };
            AdapterHelpers.SetIf(obj, "label", e.Label);
            AdapterHelpers.SetIf(obj, "weight", e.Weight);
            if (pObj.Count > 0)
            {
                obj["properties"] = pObj;
            }
            AdapterHelpers.SetIf(obj, "style", e.Style);
            if (e.Bidirectional)
            {
                obj["bidirectional"] = true;
            }
            if (e.Source is not null)
            {
                obj["source"] = e.Source;
            }
            else if (e.Properties.TryGetValue("_source", out var os))
            {
                AdapterHelpers.SetIf(obj, "source", os);
            }
            AdapterHelpers.SetIf(obj, "createdAt", e.CreatedAt);
            if (e.Properties.TryGetValue("_3dkg", out var x) && x is JsonObject xo)
            {
                foreach (var kv in xo)
                {
                    obj[kv.Key] ??= kv.Value?.DeepClone();
                }
            }
            relationships.Add(obj);
        }

        var clusters = new JsonArray();
        foreach (var c in graph.Clusters)
        {
            var members = c.NodeIds.Union(graph.Nodes.Where(n => n.Cluster == c.Id).Select(n => n.Id)).Count();
            var obj = AdapterHelpers.ToObject(c.Properties);
            obj["id"] = c.Id;
            obj["name"] = c.Name ?? c.Id;
            obj["entityCount"] = members;
            clusters.Add(obj);
        }

        var meta = graph.Metadata.TryGetValue("graphMetadata", out var gm) && gm is JsonObject gmo
            ? (JsonObject)gmo.DeepClone()
            : new JsonObject();
        meta["schemaVersion"] ??= "1.0.0";
        meta["entityCount"] = entities.Count;
        meta["relationshipCount"] = relationships.Count;
        meta["clusterCount"] = clusters.Count;
        meta["flashcardCount"] = flashcards.Count;
        meta["createdAt"] ??= AdapterHelpers.Iso(graph.CreatedAt);
        meta["updatedAt"] = AdapterHelpers.Iso(graph.UpdatedAt);

        var root = new JsonObject
        {
            ["id"] = graph.Id,
            ["name"] = graph.Name,
            ["version"] = graph.Metadata.TryGetValue("version", out var v) && AdapterHelpers.Str(v) is { } vs ? vs : "1.0.0",
            ["entities"] = entities,
            ["relationships"] = relationships,
            ["clusters"] = clusters
        };
        if (flashcards.Count > 0)
        {
            root["flashcards"] = flashcards;
        }
        if (patterns.Count > 0)
        {
            root["schemaPathPatterns"] = patterns;
        }
        foreach (var key in MetadataPassThrough)
        {
            if (graph.Metadata.TryGetValue(key, out var value) && value is not null)
            {
                root[key] = value.DeepClone();
            }
        }
        root["metadata"] = meta;
        return Result(AdapterHelpers.Serialize(root), warnings);
    }

    private static JsonObject ExportEntity(GraphNode n)
    {
        var props = new Dictionary<string, JsonNode?>(n.Properties);
        props.Remove("_source");
        props.Remove("_3dkg");
        var block = new JsonObject();
        AdapterHelpers.SetIf(block, "content", n.Content);
        AdapterHelpers.SetIfAny(block, "observations", n.Observations);
        AdapterHelpers.SetIf(block, "scope", n.Scope);
        AdapterHelpers.SetIf(block, "validFrom", n.ValidFrom);
        AdapterHelpers.SetIf(block, "validTo", n.ValidTo);
        if (n.Version is { } ver)
        {
            block["version"] = ver;
        }
        if (!n.IsLatest)
        {
            block["isLatest"] = false;
        }
        var pObj = AdapterHelpers.ToObject(props);
        if (n.Tags.Count > 0 && !props.ContainsKey("tags"))
        {
            pObj["tags"] = AdapterHelpers.ToArray(n.Tags);
        }
        if (block.Count > 0)
        {
            pObj[DarbotKey] = block;
        }

        var obj = new JsonObject { ["id"] = n.Id, ["type"] = n.Type, ["name"] = n.Name };
        AdapterHelpers.SetIf(obj, "description", n.Description);
        AdapterHelpers.SetIfAny(obj, "aliases", n.Aliases);
        if (pObj.Count > 0)
        {
            obj["properties"] = pObj;
        }
        AdapterHelpers.SetIf(obj, "embedding", AdapterHelpers.FloatsToJson(n.Embedding));
        AdapterHelpers.SetIf(obj, "position", AdapterHelpers.PositionToJson(n.Position));
        AdapterHelpers.SetIf(obj, "style", n.Style);
        AdapterHelpers.SetIf(obj, "cluster", n.Cluster);
        if (n.Source is not null)
        {
            obj["source"] = n.Source;
        }
        else if (n.Properties.TryGetValue("_source", out var os))
        {
            AdapterHelpers.SetIf(obj, "source", os);
        }
        AdapterHelpers.SetIf(obj, "createdAt", n.CreatedAt);
        AdapterHelpers.SetIf(obj, "updatedAt", n.UpdatedAt);
        if (n.Properties.TryGetValue("_3dkg", out var x) && x is JsonObject xo)
        {
            foreach (var kv in xo)
            {
                obj[kv.Key] ??= kv.Value?.DeepClone();
            }
        }
        return obj;
    }

    private static JsonObject ExportFlashcard(GraphNode n)
    {
        var obj = AdapterHelpers.ToObject(n.Properties);
        obj["id"] = n.Id;
        obj["title"] = n.Name;
        obj["content"] = n.Content ?? n.Description ?? string.Empty;
        obj["agentId"] ??= "darbot-memory";
        AdapterHelpers.SetIf(obj, "zone", n.Scope);
        AdapterHelpers.SetIfAny(obj, "tags", n.Tags);
        AdapterHelpers.SetIf(obj, "position", AdapterHelpers.PositionToJson(n.Position));
        AdapterHelpers.SetIf(obj, "createdAt", n.CreatedAt);
        AdapterHelpers.SetIf(obj, "updatedAt", n.UpdatedAt);
        return obj;
    }

    private static JsonObject ExportPattern(GraphNode n)
    {
        var obj = AdapterHelpers.ToObject(n.Properties);
        obj["id"] = n.Id;
        obj["name"] = n.Name;
        AdapterHelpers.SetIf(obj, "description", n.Description);
        AdapterHelpers.SetIf(obj, "scenario", n.Content);
        obj["steps"] ??= new JsonArray();
        AdapterHelpers.SetIfAny(obj, "tags", n.Tags);
        AdapterHelpers.SetIf(obj, "createdAt", n.CreatedAt);
        AdapterHelpers.SetIf(obj, "updatedAt", n.UpdatedAt);
        return obj;
    }
}
