using System.Text.Json.Nodes;

namespace Darbot.Memory.Mcp.Core.Graph.Adapters;

/// <summary>Shared JSON-LD reader/writer for the "jsonld" and "kgforge" adapters.</summary>
internal static class JsonLdCodec
{
    private static readonly string[] NameKeys = { "name", "label", "rdfs:label", "skos:prefLabel", "schema:name" };
    private static readonly string[] DescriptionKeys = { "description", "schema:description", "comment", "rdfs:comment" };

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "@context", "@id", "@type", "id", "type", "name", "label", "description", "alternateName", "keywords",
        "dateCreated", "dateModified", "sameAs"
    };

    public static JsonNode DefaultContext(bool forge)
    {
        var ctx = new JsonObject
        {
            ["@vocab"] = "https://schema.org/",
            ["schema"] = "https://schema.org/",
            ["darbot"] = "https://darbotlabs.com/kg/ns#",
            ["rdfs"] = "http://www.w3.org/2000/01/rdf-schema#",
            ["skos"] = "http://www.w3.org/2004/02/skos/core#"
        };
        if (forge)
        {
            ctx["nxv"] = "https://bluebrain.github.io/nexus/vocabulary/";
            ctx["label"] = "rdfs:label";
        }
        return ctx;
    }

    private static readonly string[] DarbotNodeKeys =
    {
        "darbot:content", "darbot:observations", "darbot:scope", "darbot:source", "darbot:cluster",
        "darbot:validFrom", "darbot:validTo", "darbot:version", "darbot:isLatest", "darbot:position",
        "darbot:embedding", "darbot:style", "darbot:tags"
    };

    public static GraphImportResult Import(string schema, string payload, string? graphName, bool forge)
    {
        var root = AdapterHelpers.ParseJson(payload, schema);
        var b = new GraphBuilder();
        JsonArray resources;
        if (root is JsonArray array)
        {
            resources = array;
        }
        else if (root is JsonObject obj)
        {
            if (obj["@context"] is { } ctx)
            {
                b.Metadata["@context"] = ctx.DeepClone();
            }
            if (obj["@graph"] is JsonArray g)
            {
                resources = g;
            }
            else if (obj["@graph"] is JsonObject single)
            {
                resources = new JsonArray(single.DeepClone());
            }
            else if (AdapterHelpers.Has(obj, "@id", "id", "@type", "type"))
            {
                resources = new JsonArray(obj.DeepClone());
            }
            else
            {
                throw new ArgumentException($"The {schema} payload has no @graph and is not a resource.");
            }
        }
        else
        {
            throw new ArgumentException($"The {schema} payload must be a JSON-LD object or array.");
        }

        var relationResources = new List<JsonObject>();
        var pendingLinks = new List<(string Source, string Type, string Target)>();
        var idsInPayload = new HashSet<string>(StringComparer.Ordinal);
        var counter = 0;

        foreach (var item in resources)
        {
            counter++;
            if (item is not JsonObject r)
            {
                b.Warn($"Resource #{counter} is not an object; skipped.");
                continue;
            }
            var typeNode = r["@type"] ?? r["type"];
            var types = AdapterHelpers.StrList(typeNode);
            if (types.Any(t => t.EndsWith("Relation", StringComparison.OrdinalIgnoreCase)) && (AdapterHelpers.Has(r, "darbot:source", "source")))
            {
                relationResources.Add(r);
                continue;
            }
            var id = AdapterHelpers.Str(r, "@id", "id") ?? $"_:b{counter}";
            var name = AdapterHelpers.Str(r, NameKeys) ?? id;
            idsInPayload.Add(id);

            var props = new Dictionary<string, JsonNode?>();
            var tags = AdapterHelpers.StrList(r["keywords"], commaSeparated: true);
            var aliases = AdapterHelpers.StrList(r["alternateName"]);
            var handled = new HashSet<string>(StringComparer.Ordinal) { "id", "type", "alternateName", "keywords", "dateCreated", "dateModified" };
            foreach (var k in DarbotNodeKeys)
            {
                handled.Add(k);
            }
            if (NameKeys.FirstOrDefault(k => AdapterHelpers.Str(r, k) is not null) is { } usedName)
            {
                handled.Add(usedName);
            }
            if (DescriptionKeys.FirstOrDefault(k => AdapterHelpers.Str(r, k) is not null) is { } usedDescription)
            {
                handled.Add(usedDescription);
            }

            foreach (var kv in r)
            {
                if (handled.Contains(kv.Key) || kv.Key.StartsWith("@", StringComparison.Ordinal))
                {
                    continue;
                }
                var links = LinkTargets(kv.Value);
                if (links is not null && kv.Key != "sameAs")
                {
                    foreach (var target in links)
                    {
                        pendingLinks.Add((id, kv.Key, target));
                    }
                }
                else
                {
                    props[kv.Key] = kv.Value?.DeepClone();
                }
            }
            if (types.Count > 1)
            {
                props["@types"] = AdapterHelpers.ToArray(types);
            }

            var node = new GraphNode
            {
                Id = id,
                Name = name,
                Type = types.FirstOrDefault() ?? "resource",
                Description = AdapterHelpers.Str(r, DescriptionKeys),
                Aliases = aliases,
                Tags = tags.Concat(AdapterHelpers.StrList(r["darbot:tags"])).Distinct().ToList(),
                Observations = AdapterHelpers.StrList(r["darbot:observations"]),
                Content = AdapterHelpers.Str(r["darbot:content"]),
                Scope = AdapterHelpers.Str(r["darbot:scope"]),
                Source = AdapterHelpers.Str(r["darbot:source"]),
                Cluster = AdapterHelpers.Str(r["darbot:cluster"]),
                ValidFrom = AdapterHelpers.Date(r["darbot:validFrom"]),
                ValidTo = AdapterHelpers.Date(r["darbot:validTo"]),
                Version = AdapterHelpers.Int(r["darbot:version"]),
                IsLatest = AdapterHelpers.Bool(r["darbot:isLatest"]) ?? true,
                Position = AdapterHelpers.Position(r["darbot:position"]),
                Embedding = AdapterHelpers.FloatArray(r["darbot:embedding"]),
                Style = AdapterHelpers.Clone(r["darbot:style"]),
                CreatedAt = AdapterHelpers.Date(r["dateCreated"]),
                UpdatedAt = AdapterHelpers.Date(r["dateModified"]),
                Properties = props
            };
            b.AddNode(node);
        }

        foreach (var (source, type, target) in pendingLinks)
        {
            if (!b.HasNode(target))
            {
                b.EnsureNode(target, target, "resource");
            }
            b.AddRelation(source, target, type);
        }

        foreach (var r in relationResources)
        {
            var source = LinkTargets(r["darbot:source"] ?? r["source"])?.FirstOrDefault();
            var target = LinkTargets(r["darbot:target"] ?? r["target"])?.FirstOrDefault();
            if (source is null || target is null)
            {
                b.Warn("Skipped a relation resource without source/target.");
                continue;
            }
            b.EnsureNode(source);
            b.EnsureNode(target);
            var type = AdapterHelpers.Str(r, "darbot:relationType", "relationType") ?? "related_to";
            b.AddRelation(source, target, type, new GraphEdge
            {
                Id = AdapterHelpers.Str(r, "@id", "id") ?? "",
                SourceId = source,
                TargetId = target,
                Label = AdapterHelpers.Str(r["darbot:label"]),
                Fact = AdapterHelpers.Str(r["darbot:fact"]),
                Weight = AdapterHelpers.Num(r["darbot:weight"]),
                Confidence = AdapterHelpers.Num(r["darbot:confidence"]),
                Bidirectional = AdapterHelpers.Bool(r["darbot:bidirectional"]) ?? false,
                Scope = AdapterHelpers.Str(r["darbot:scope"]),
                ValidFrom = AdapterHelpers.Date(r["darbot:validFrom"]),
                ValidTo = AdapterHelpers.Date(r["darbot:validTo"]),
                ExpiredAt = AdapterHelpers.Date(r["darbot:expiredAt"]),
                CreatedAt = AdapterHelpers.Date(r["dateCreated"]),
                Properties = r["darbot:properties"] is JsonObject po ? AdapterHelpers.Extras(po) : new()
            });
        }
        return b.Build(graphName);
    }

    /// <summary>Returns @id targets when the value is a reference ({"@id"}) or array of references; otherwise null.</summary>
    private static List<string>? LinkTargets(JsonNode? value)
    {
        switch (value)
        {
            case JsonObject o when o.Count == 1 && o["@id"] is JsonValue:
                return new List<string> { AdapterHelpers.Str(o["@id"])! };
            case JsonArray a when a.Count > 0 && a.All(x => x is JsonObject xo && xo.Count == 1 && xo["@id"] is JsonValue):
                return a.Select(x => AdapterHelpers.Str(x!["@id"])!).ToList();
            default:
                return null;
        }
    }

    private static JsonObject Ref(string id) => new() { ["@id"] = id };

    public static GraphExportResult Export(GraphAdapterBase adapter, KnowledgeGraph graph, bool forge)
    {
        var warnings = new List<string>();
        var graphArray = new JsonArray();
        var simpleEdges = new Dictionary<string, List<GraphEdge>>(StringComparer.Ordinal);
        var reified = new List<GraphEdge>();
        var nodeProps = graph.Nodes.ToDictionary(n => n.Id, n => n, StringComparer.Ordinal);

        foreach (var e in graph.Edges)
        {
            var simple = e.Label is null && e.Fact is null && e.Weight is null && e.Confidence is null && !e.Bidirectional
                && e.Properties.Count == 0 && e.Scope is null && e.Source is null && e.ValidFrom is null && e.ValidTo is null
                && e.ExpiredAt is null && e.CreatedAt is null && e.Style is null
                && e.Id == AdapterHelpers.EdgeId(e.SourceId, e.Type, e.TargetId)
                && !Reserved.Contains(e.Type) && !e.Type.StartsWith("@", StringComparison.Ordinal)
                && !(nodeProps.TryGetValue(e.SourceId, out var sn) && sn.Properties.ContainsKey(e.Type));
            if (simple)
            {
                if (!simpleEdges.TryGetValue(e.SourceId, out var list))
                {
                    simpleEdges[e.SourceId] = list = new List<GraphEdge>();
                }
                list.Add(e);
            }
            else
            {
                reified.Add(e);
            }
        }

        foreach (var n in graph.Nodes)
        {
            var r = new JsonObject { ["@id"] = n.Id, ["@type"] = n.Type };
            if (n.Properties.TryGetValue("@types", out var moreTypes) && moreTypes is JsonArray ta)
            {
                r["@type"] = ta.DeepClone();
            }
            r["name"] = n.Name;
            AdapterHelpers.SetIf(r, "description", n.Description);
            AdapterHelpers.SetIfAny(r, "alternateName", n.Aliases);
            AdapterHelpers.SetIfAny(r, "keywords", n.Tags);
            AdapterHelpers.SetIf(r, "dateCreated", n.CreatedAt);
            AdapterHelpers.SetIf(r, "dateModified", n.UpdatedAt);
            AdapterHelpers.SetIf(r, "darbot:content", n.Content);
            AdapterHelpers.SetIfAny(r, "darbot:observations", n.Observations);
            AdapterHelpers.SetIf(r, "darbot:scope", n.Scope);
            AdapterHelpers.SetIf(r, "darbot:source", n.Source);
            AdapterHelpers.SetIf(r, "darbot:cluster", n.Cluster);
            AdapterHelpers.SetIf(r, "darbot:validFrom", n.ValidFrom);
            AdapterHelpers.SetIf(r, "darbot:validTo", n.ValidTo);
            if (n.Version is { } v)
            {
                r["darbot:version"] = v;
            }
            if (!n.IsLatest)
            {
                r["darbot:isLatest"] = false;
            }
            AdapterHelpers.SetIf(r, "darbot:position", AdapterHelpers.PositionToJson(n.Position));
            AdapterHelpers.SetIf(r, "darbot:embedding", AdapterHelpers.FloatsToJson(n.Embedding));
            AdapterHelpers.SetIf(r, "darbot:style", n.Style);
            foreach (var kv in n.Properties)
            {
                if (kv.Key != "@types" && r[kv.Key] is null)
                {
                    r[kv.Key] = kv.Value?.DeepClone();
                }
            }
            if (simpleEdges.TryGetValue(n.Id, out var edges))
            {
                foreach (var group in edges.GroupBy(e => e.Type))
                {
                    var targets = group.Select(e => (JsonNode)Ref(e.TargetId)).ToList();
                    r[group.Key] = targets.Count == 1 ? targets[0] : new JsonArray(targets.ToArray());
                }
            }
            graphArray.Add(r);
        }

        foreach (var e in reified)
        {
            var r = new JsonObject
            {
                ["@id"] = e.Id,
                ["@type"] = "darbot:Relation",
                ["darbot:source"] = Ref(e.SourceId),
                ["darbot:target"] = Ref(e.TargetId),
                ["darbot:relationType"] = e.Type
            };
            AdapterHelpers.SetIf(r, "darbot:label", e.Label);
            AdapterHelpers.SetIf(r, "darbot:fact", e.Fact);
            AdapterHelpers.SetIf(r, "darbot:weight", e.Weight);
            AdapterHelpers.SetIf(r, "darbot:confidence", e.Confidence);
            if (e.Bidirectional)
            {
                r["darbot:bidirectional"] = true;
            }
            AdapterHelpers.SetIf(r, "darbot:scope", e.Scope);
            AdapterHelpers.SetIf(r, "darbot:validFrom", e.ValidFrom);
            AdapterHelpers.SetIf(r, "darbot:validTo", e.ValidTo);
            AdapterHelpers.SetIf(r, "darbot:expiredAt", e.ExpiredAt);
            AdapterHelpers.SetIf(r, "dateCreated", e.CreatedAt);
            if (e.Properties.Count > 0)
            {
                r["darbot:properties"] = AdapterHelpers.ToObject(e.Properties);
            }
            graphArray.Add(r);
        }
        if (reified.Any(e => e.Source is not null))
        {
            warnings.Add("Edge 'source' provenance is not represented in JSON-LD relation resources.");
        }

        var root = new JsonObject
        {
            ["@context"] = graph.Metadata.TryGetValue("@context", out var ctx) && ctx is not null ? ctx.DeepClone() : DefaultContext(forge),
            ["@graph"] = graphArray
        };
        return new GraphExportResult
        {
            Schema = adapter.Name,
            ContentType = "application/ld+json",
            Content = AdapterHelpers.Serialize(root),
            Warnings = warnings
        };
    }
}

/// <summary>Blue Brain Nexus Forge / KGForge JSON-LD resources.</summary>
public sealed class KgForgeAdapter : GraphAdapterBase
{
    public override string Name => "kgforge";
    public override string DisplayName => "KGForge / Nexus Forge";
    public override string Description => "JSON-LD resources ({@context,@graph}) with @id/@type; links are {\"@id\"} references, rich edges are reified darbot:Relation resources.";
    public override string ContentType => "application/ld+json";

    public override GraphImportResult Import(string payload, string? graphName = null)
        => JsonLdCodec.Import(Name, RequirePayload(payload), graphName, forge: true);

    public override GraphExportResult Export(KnowledgeGraph graph) => JsonLdCodec.Export(this, graph, forge: true);
}

/// <summary>Generic schema.org JSON-LD.</summary>
public sealed class JsonLdAdapter : GraphAdapterBase
{
    public override string Name => "jsonld";
    public override string DisplayName => "JSON-LD (schema.org)";
    public override string Description => "schema.org JSON-LD with @graph, @id, @type, name, description, sameAs and {\"@id\"} references.";
    public override string ContentType => "application/ld+json";

    public override GraphImportResult Import(string payload, string? graphName = null)
        => JsonLdCodec.Import(Name, RequirePayload(payload), graphName, forge: false);

    public override GraphExportResult Export(KnowledgeGraph graph) => JsonLdCodec.Export(this, graph, forge: false);
}
