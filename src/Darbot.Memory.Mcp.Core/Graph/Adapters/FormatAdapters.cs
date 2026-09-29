using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace Darbot.Memory.Mcp.Core.Graph.Adapters;

/// <summary>GraphML import and export (System.Xml.Linq).</summary>
public sealed class GraphMlAdapter : GraphAdapterBase
{
    private static readonly XNamespace Ns = "http://graphml.graphdrawing.org/xmlns";

    private static readonly (string Name, string Type)[] NodeKeys =
    {
        ("name", "string"), ("type", "string"), ("description", "string"), ("aliases", "string"), ("tags", "string"),
        ("observations", "string"), ("content", "string"), ("properties", "string"), ("scope", "string"), ("source", "string"),
        ("created", "string"), ("updated", "string"), ("validFrom", "string"), ("validTo", "string"), ("version", "int"),
        ("isLatest", "boolean"), ("x", "double"), ("y", "double"), ("z", "double"), ("cluster", "string"),
        ("embedding", "string"), ("style", "string")
    };

    private static readonly (string Name, string Type)[] EdgeKeys =
    {
        ("type", "string"), ("label", "string"), ("fact", "string"), ("weight", "double"), ("confidence", "double"),
        ("bidirectional", "boolean"), ("properties", "string"), ("scope", "string"), ("source", "string"),
        ("created", "string"), ("validFrom", "string"), ("validTo", "string"), ("expiredAt", "string"), ("style", "string")
    };

    public override string Name => "graphml";
    public override string DisplayName => "GraphML";
    public override string Description => "GraphML XML with typed data keys for node/edge fields; foreign GraphML keys are kept as properties.";
    public override string ContentType => "application/graphml+xml";

    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (XmlConvert.IsXmlChar(ch) || char.IsSurrogate(ch))
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    public override GraphExportResult Export(KnowledgeGraph graph)
    {
        var root = new XElement(Ns + "graphml");
        foreach (var (name, type) in NodeKeys)
        {
            root.Add(new XElement(Ns + "key", new XAttribute("id", "n_" + name), new XAttribute("for", "node"), new XAttribute("attr.name", name), new XAttribute("attr.type", type)));
        }
        foreach (var (name, type) in EdgeKeys)
        {
            root.Add(new XElement(Ns + "key", new XAttribute("id", "e_" + name), new XAttribute("for", "edge"), new XAttribute("attr.name", name), new XAttribute("attr.type", type)));
        }
        foreach (var name in new[] { "name", "namespace", "id" })
        {
            root.Add(new XElement(Ns + "key", new XAttribute("id", "g_" + name), new XAttribute("for", "graph"), new XAttribute("attr.name", name), new XAttribute("attr.type", "string")));
        }

        var g = new XElement(Ns + "graph", new XAttribute("id", Clean(graph.Name)), new XAttribute("edgedefault", "directed"));
        g.Add(Data("g_name", graph.Name), Data("g_namespace", graph.Namespace), Data("g_id", graph.Id));
        foreach (var n in graph.Nodes)
        {
            var el = new XElement(Ns + "node", new XAttribute("id", Clean(n.Id)));
            Add(el, "n_name", n.Name);
            Add(el, "n_type", n.Type);
            Add(el, "n_description", n.Description);
            AddJson(el, "n_aliases", n.Aliases.Count > 0 ? AdapterHelpers.ToArray(n.Aliases) : null);
            AddJson(el, "n_tags", n.Tags.Count > 0 ? AdapterHelpers.ToArray(n.Tags) : null);
            AddJson(el, "n_observations", n.Observations.Count > 0 ? AdapterHelpers.ToArray(n.Observations) : null);
            Add(el, "n_content", n.Content);
            AddJson(el, "n_properties", n.Properties.Count > 0 ? AdapterHelpers.ToObject(n.Properties) : null);
            Add(el, "n_scope", n.Scope);
            Add(el, "n_source", n.Source);
            Add(el, "n_created", n.CreatedAt is { } c ? AdapterHelpers.Iso(c) : null);
            Add(el, "n_updated", n.UpdatedAt is { } u ? AdapterHelpers.Iso(u) : null);
            Add(el, "n_validFrom", n.ValidFrom is { } vf ? AdapterHelpers.Iso(vf) : null);
            Add(el, "n_validTo", n.ValidTo is { } vt ? AdapterHelpers.Iso(vt) : null);
            Add(el, "n_version", n.Version?.ToString(CultureInfo.InvariantCulture));
            if (!n.IsLatest)
            {
                Add(el, "n_isLatest", "false");
            }
            if (n.Position is { } p)
            {
                Add(el, "n_x", p.X.ToString("R", CultureInfo.InvariantCulture));
                Add(el, "n_y", p.Y.ToString("R", CultureInfo.InvariantCulture));
                Add(el, "n_z", p.Z.ToString("R", CultureInfo.InvariantCulture));
            }
            Add(el, "n_cluster", n.Cluster);
            AddJson(el, "n_embedding", AdapterHelpers.FloatsToJson(n.Embedding));
            AddJson(el, "n_style", n.Style);
            g.Add(el);
        }
        foreach (var e in graph.Edges)
        {
            var el = new XElement(Ns + "edge", new XAttribute("id", Clean(e.Id)), new XAttribute("source", Clean(e.SourceId)), new XAttribute("target", Clean(e.TargetId)));
            Add(el, "e_type", e.Type);
            Add(el, "e_label", e.Label);
            Add(el, "e_fact", e.Fact);
            Add(el, "e_weight", e.Weight?.ToString("R", CultureInfo.InvariantCulture));
            Add(el, "e_confidence", e.Confidence?.ToString("R", CultureInfo.InvariantCulture));
            if (e.Bidirectional)
            {
                Add(el, "e_bidirectional", "true");
            }
            AddJson(el, "e_properties", e.Properties.Count > 0 ? AdapterHelpers.ToObject(e.Properties) : null);
            Add(el, "e_scope", e.Scope);
            Add(el, "e_source", e.Source);
            Add(el, "e_created", e.CreatedAt is { } c ? AdapterHelpers.Iso(c) : null);
            Add(el, "e_validFrom", e.ValidFrom is { } vf ? AdapterHelpers.Iso(vf) : null);
            Add(el, "e_validTo", e.ValidTo is { } vt ? AdapterHelpers.Iso(vt) : null);
            Add(el, "e_expiredAt", e.ExpiredAt is { } ex ? AdapterHelpers.Iso(ex) : null);
            AddJson(el, "e_style", e.Style);
            g.Add(el);
        }
        root.Add(g);
        var doc = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + new XDocument(root).ToString();
        return Result(doc);
    }

    private static XElement Data(string key, string? value) => new(Ns + "data", new XAttribute("key", key), Clean(value));

    private static void Add(XElement parent, string key, string? value)
    {
        if (value is not null)
        {
            parent.Add(Data(key, value));
        }
    }

    private static void AddJson(XElement parent, string key, JsonNode? value)
    {
        if (value is not null)
        {
            parent.Add(Data(key, value.ToJsonString(GraphJson.Options.WithoutIndent())));
        }
    }

    public override GraphImportResult Import(string payload, string? graphName = null)
    {
        RequirePayload(payload);
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(payload), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new ArgumentException($"The graphml payload is not valid XML: {ex.Message}", ex);
        }
        if (doc.Root is null || doc.Root.Name.LocalName != "graphml")
        {
            throw new ArgumentException("The graphml payload has no <graphml> root element.");
        }

        var b = new GraphBuilder();
        var keys = new Dictionary<string, (string For, string Name, string Type)>(StringComparer.Ordinal);
        foreach (var k in doc.Root.Elements().Where(x => x.Name.LocalName == "key"))
        {
            var id = (string?)k.Attribute("id");
            if (id is null)
            {
                continue;
            }
            var attrName = (string?)k.Attribute("attr.name") ?? (string?)k.Attribute("yfiles.type") ?? id;
            keys[id] = ((string?)k.Attribute("for") ?? "all", attrName, (string?)k.Attribute("attr.type") ?? "string");
        }

        var graphEl = doc.Root.Elements().FirstOrDefault(x => x.Name.LocalName == "graph")
            ?? throw new ArgumentException("The graphml payload has no <graph> element.");
        var defaultUndirected = string.Equals((string?)graphEl.Attribute("edgedefault"), "undirected", StringComparison.OrdinalIgnoreCase);
        var graphData = ReadData(graphEl, keys);
        var ns = graphData.GetValueOrDefault("namespace");
        var graphId = graphData.GetValueOrDefault("id");

        foreach (var el in graphEl.Descendants().Where(x => x.Name.LocalName == "node"))
        {
            var id = (string?)el.Attribute("id");
            if (string.IsNullOrEmpty(id))
            {
                b.Warn("Skipped a <node> without id.");
                continue;
            }
            var d = ReadData(el, keys);
            var props = new Dictionary<string, JsonNode?>();
            if (d.TryGetValue("properties", out var pj) && TryJson(pj) is JsonObject po)
            {
                AdapterHelpers.MergeInto(props, po);
            }
            var known = new HashSet<string>(NodeKeys.Select(k => k.Name)) { "label" };
            foreach (var kv in d)
            {
                if (!known.Contains(kv.Key))
                {
                    props[kv.Key] = Coerce(kv.Value, TypeOf(keys, "node", kv.Key));
                }
            }
            b.AddNode(new GraphNode
            {
                Id = id,
                Name = d.GetValueOrDefault("name") ?? d.GetValueOrDefault("label") ?? id,
                Type = d.GetValueOrDefault("type") ?? "entity",
                Description = d.GetValueOrDefault("description"),
                Aliases = AdapterHelpers.StrList(TryJson(d.GetValueOrDefault("aliases"))),
                Tags = AdapterHelpers.StrList(TryJson(d.GetValueOrDefault("tags"))),
                Observations = AdapterHelpers.StrList(TryJson(d.GetValueOrDefault("observations"))),
                Content = d.GetValueOrDefault("content"),
                Scope = d.GetValueOrDefault("scope"),
                Source = d.GetValueOrDefault("source"),
                Cluster = d.GetValueOrDefault("cluster"),
                CreatedAt = ParseDate(d.GetValueOrDefault("created")),
                UpdatedAt = ParseDate(d.GetValueOrDefault("updated")),
                ValidFrom = ParseDate(d.GetValueOrDefault("validFrom")),
                ValidTo = ParseDate(d.GetValueOrDefault("validTo")),
                Version = int.TryParse(d.GetValueOrDefault("version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ver) ? ver : null,
                IsLatest = !bool.TryParse(d.GetValueOrDefault("isLatest"), out var latest) || latest,
                Position = d.ContainsKey("x") || d.ContainsKey("y")
                    ? new GraphPosition(Dbl(d.GetValueOrDefault("x")), Dbl(d.GetValueOrDefault("y")), Dbl(d.GetValueOrDefault("z")))
                    : null,
                Embedding = AdapterHelpers.FloatArray(TryJson(d.GetValueOrDefault("embedding"))),
                Style = TryJson(d.GetValueOrDefault("style")),
                Properties = props
            });
        }

        foreach (var el in graphEl.Descendants().Where(x => x.Name.LocalName == "edge"))
        {
            var s = (string?)el.Attribute("source");
            var t = (string?)el.Attribute("target");
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(t))
            {
                b.Warn("Skipped an <edge> without source/target.");
                continue;
            }
            var d = ReadData(el, keys);
            var props = new Dictionary<string, JsonNode?>();
            if (d.TryGetValue("properties", out var pj) && TryJson(pj) is JsonObject po)
            {
                AdapterHelpers.MergeInto(props, po);
            }
            var known = new HashSet<string>(EdgeKeys.Select(k => k.Name));
            foreach (var kv in d)
            {
                if (!known.Contains(kv.Key))
                {
                    props[kv.Key] = Coerce(kv.Value, TypeOf(keys, "edge", kv.Key));
                }
            }
            var explicitType = d.GetValueOrDefault("type");
            var type = explicitType ?? d.GetValueOrDefault("label") ?? "related_to";
            var directed = (string?)el.Attribute("directed");
            var bidirectional = bool.TryParse(d.GetValueOrDefault("bidirectional"), out var bi)
                ? bi
                : directed is not null ? directed.Equals("false", StringComparison.OrdinalIgnoreCase) : defaultUndirected;
            b.EnsureNode(s);
            b.EnsureNode(t);
            b.AddRelation(s, t, type, new GraphEdge
            {
                Id = (string?)el.Attribute("id") ?? "",
                SourceId = s,
                TargetId = t,
                Label = explicitType is null ? null : d.GetValueOrDefault("label"),
                Fact = d.GetValueOrDefault("fact"),
                Weight = Dbl(d.GetValueOrDefault("weight"), null),
                Confidence = Dbl(d.GetValueOrDefault("confidence"), null),
                Bidirectional = bidirectional,
                Scope = d.GetValueOrDefault("scope"),
                Source = d.GetValueOrDefault("source"),
                CreatedAt = ParseDate(d.GetValueOrDefault("created")),
                ValidFrom = ParseDate(d.GetValueOrDefault("validFrom")),
                ValidTo = ParseDate(d.GetValueOrDefault("validTo")),
                ExpiredAt = ParseDate(d.GetValueOrDefault("expiredAt")),
                Style = TryJson(d.GetValueOrDefault("style")),
                Properties = props
            });
        }

        var result = b.Build(graphName ?? graphData.GetValueOrDefault("name") ?? (string?)graphEl.Attribute("id"), ns);
        return graphId is null ? result : result with { Graph = result.Graph with { Id = graphId } };
    }

    private static Dictionary<string, string> ReadData(XElement el, Dictionary<string, (string For, string Name, string Type)> keys)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var data in el.Elements().Where(x => x.Name.LocalName == "data"))
        {
            var key = (string?)data.Attribute("key");
            if (key is null)
            {
                continue;
            }
            var name = keys.TryGetValue(key, out var def) ? def.Name : key;
            result[name] = data.Value;
        }
        return result;
    }

    private static string TypeOf(Dictionary<string, (string For, string Name, string Type)> keys, string scope, string name)
        => keys.Values.FirstOrDefault(k => k.Name == name && (k.For == scope || k.For == "all")).Type ?? "string";

    private static JsonNode? Coerce(string value, string type) => type switch
    {
        "int" or "long" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => JsonValue.Create(l),
        "float" or "double" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
        "boolean" when bool.TryParse(value, out var bo) => JsonValue.Create(bo),
        _ => JsonValue.Create(value)
    };

    private static JsonNode? TryJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ParseDate(string? text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static double Dbl(string? text) => Dbl(text, 0) ?? 0;

    private static double? Dbl(string? text, double? fallback)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
}

internal static class JsonOptionsExtensions
{
    public static JsonSerializerOptions WithoutIndent(this JsonSerializerOptions options)
        => new(options) { WriteIndented = false };
}

/// <summary>Neo4j Cypher export (import is not supported).</summary>
public sealed class CypherAdapter : GraphAdapterBase
{
    public override string Name => "cypher";
    public override string DisplayName => "Neo4j Cypher";
    public override string Description => "MERGE statements for nodes and relationships (export only).";
    public override string ContentType => "text/plain";
    public override bool CanImport => false;

    public override GraphImportResult Import(string payload, string? graphName = null)
        => throw new NotSupportedException("The cypher adapter is export-only.");

    public override GraphExportResult Export(KnowledgeGraph graph)
    {
        var warnings = new List<string>();
        var sb = new StringBuilder();
        sb.Append("// Darbot knowledge graph '").Append(graph.Name.ReplaceLineEndings(" ")).Append("' exported ").Append(AdapterHelpers.Iso(DateTimeOffset.UtcNow)).Append('\n');
        sb.Append("CREATE CONSTRAINT entity_id IF NOT EXISTS FOR (n:Entity) REQUIRE n.id IS UNIQUE;\n");

        foreach (var n in graph.Nodes)
        {
            var sets = new List<string> { $"n:{Label(n.Type)}" };
            var values = new List<(string Key, string Value)>
            {
                ("name", Str(n.Name)),
                ("type", Str(n.Type))
            };
            if (n.Description is not null) values.Add(("description", Str(n.Description)));
            if (n.Content is not null) values.Add(("content", Str(n.Content)));
            if (n.Aliases.Count > 0) values.Add(("aliases", List(n.Aliases)));
            if (n.Tags.Count > 0) values.Add(("tags", List(n.Tags)));
            if (n.Observations.Count > 0) values.Add(("observations", List(n.Observations)));
            if (n.Scope is not null) values.Add(("scope", Str(n.Scope)));
            if (n.Source is not null) values.Add(("source", Str(n.Source)));
            if (n.Cluster is not null) values.Add(("cluster", Str(n.Cluster)));
            if (n.CreatedAt is { } c) values.Add(("createdAt", Str(AdapterHelpers.Iso(c))));
            if (n.UpdatedAt is { } u) values.Add(("updatedAt", Str(AdapterHelpers.Iso(u))));
            if (n.ValidFrom is { } vf) values.Add(("validFrom", Str(AdapterHelpers.Iso(vf))));
            if (n.ValidTo is { } vt) values.Add(("validTo", Str(AdapterHelpers.Iso(vt))));
            if (n.Version is { } ver) values.Add(("version", ver.ToString(CultureInfo.InvariantCulture)));
            if (!n.IsLatest) values.Add(("isLatest", "false"));
            if (n.Position is { } p)
            {
                values.Add(("x", Num(p.X)));
                values.Add(("y", Num(p.Y)));
                values.Add(("z", Num(p.Z)));
            }
            AddProps(values, n.Properties);
            sets.AddRange(values.Select(v => $"n.{Key(v.Key)} = {v.Value}"));
            sb.Append("MERGE (n:Entity {id: ").Append(Str(n.Id)).Append("}) SET ").Append(string.Join(", ", sets)).Append(";\n");
        }

        foreach (var e in graph.Edges)
        {
            var values = new List<(string Key, string Value)> { ("type", Str(e.Type)) };
            if (e.Label is not null) values.Add(("label", Str(e.Label)));
            if (e.Fact is not null) values.Add(("fact", Str(e.Fact)));
            if (e.Weight is { } w) values.Add(("weight", Num(w)));
            if (e.Confidence is { } cf) values.Add(("confidence", Num(cf)));
            if (e.Bidirectional) values.Add(("bidirectional", "true"));
            if (e.Scope is not null) values.Add(("scope", Str(e.Scope)));
            if (e.Source is not null) values.Add(("source", Str(e.Source)));
            if (e.CreatedAt is { } c) values.Add(("createdAt", Str(AdapterHelpers.Iso(c))));
            if (e.ValidFrom is { } vf) values.Add(("validFrom", Str(AdapterHelpers.Iso(vf))));
            if (e.ValidTo is { } vt) values.Add(("validTo", Str(AdapterHelpers.Iso(vt))));
            if (e.ExpiredAt is { } ex) values.Add(("expiredAt", Str(AdapterHelpers.Iso(ex))));
            AddProps(values, e.Properties);
            sb.Append("MATCH (a:Entity {id: ").Append(Str(e.SourceId)).Append("}), (b:Entity {id: ").Append(Str(e.TargetId)).Append("}) ")
              .Append("MERGE (a)-[r:").Append(RelType(e.Type)).Append(" {id: ").Append(Str(e.Id)).Append("}]->(b) SET ")
              .Append(string.Join(", ", values.Select(v => $"r.{Key(v.Key)} = {v.Value}"))).Append(";\n");
        }
        if (graph.Nodes.Any(n => n.Embedding is not null))
        {
            warnings.Add("Embeddings were not exported to Cypher.");
        }
        return Result(sb.ToString(), warnings);
    }

    private static readonly HashSet<string> Standard = new(StringComparer.Ordinal)
    {
        "id", "name", "type", "description", "content", "aliases", "tags", "observations", "scope", "source", "cluster",
        "createdAt", "updatedAt", "validFrom", "validTo", "version", "isLatest", "x", "y", "z", "label", "fact", "weight",
        "confidence", "bidirectional", "expiredAt"
    };

    private static void AddProps(List<(string Key, string Value)> values, Dictionary<string, JsonNode?> props)
    {
        foreach (var kv in props)
        {
            var key = Standard.Contains(kv.Key) ? "prop_" + kv.Key : kv.Key;
            switch (kv.Value)
            {
                case null:
                    break;
                case JsonArray a when a.All(x => x is JsonValue v && v.GetValueKind() == JsonValueKind.String):
                    values.Add((key, List(a.Select(x => x!.GetValue<string>()))));
                    break;
                case JsonValue v:
                    values.Add((key, v.GetValueKind() switch
                    {
                        JsonValueKind.String => Str(v.GetValue<string>()),
                        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToJsonString(),
                        _ => Str(v.ToJsonString())
                    }));
                    break;
                default:
                    values.Add((key, Str(kv.Value.ToJsonString())));
                    break;
            }
        }
    }

    internal static string Str(string? value)
    {
        var sb = new StringBuilder("'");
        foreach (var ch in value ?? string.Empty)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(ch))
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        return sb.Append('\'').ToString();
    }

    private static string List(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Str)) + "]";

    private static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    private static string Key(string key)
        => System.Text.RegularExpressions.Regex.IsMatch(key, @"^[A-Za-z_][A-Za-z0-9_]*$") ? key : "`" + key.Replace("`", "``") + "`";

    internal static string Label(string type)
    {
        var parts = System.Text.RegularExpressions.Regex.Split(type ?? string.Empty, @"[^A-Za-z0-9]+").Where(p => p.Length > 0);
        var label = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        if (label.Length == 0)
        {
            return "Entity";
        }
        return char.IsDigit(label[0]) ? "T" + label : label;
    }

    internal static string RelType(string type)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(type ?? string.Empty, @"([a-z0-9])([A-Z])", "$1_$2");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[^A-Za-z0-9]+", "_").Trim('_').ToUpperInvariant();
        if (s.Length == 0)
        {
            return "RELATED_TO";
        }
        return char.IsDigit(s[0]) ? "REL_" + s : s;
    }
}

/// <summary>RDF N-Triples export (import is not supported).</summary>
public sealed class NTriplesAdapter : GraphAdapterBase
{
    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string Rdfs = "http://www.w3.org/2000/01/rdf-schema#";
    private const string Xsd = "http://www.w3.org/2001/XMLSchema#";

    public override string Name => "ntriples";
    public override string DisplayName => "RDF N-Triples";
    public override string Description => "RDF N-Triples with IRIs under https://darbotlabs.com/kg/<namespace>/ (export only).";
    public override string ContentType => "application/n-triples";
    public override bool CanImport => false;

    public override GraphImportResult Import(string payload, string? graphName = null)
        => throw new NotSupportedException("The ntriples adapter is export-only.");

    public override GraphExportResult Export(KnowledgeGraph graph)
    {
        var warnings = new List<string>();
        var ns = "https://darbotlabs.com/kg/" + Uri.EscapeDataString(AdapterHelpers.Slug(graph.Namespace, "default")) + "/";
        string Iri(string kind, string value) => $"<{ns}{kind}/{Uri.EscapeDataString(value)}>";
        static string Full(string iri) => $"<{iri}>";

        var sb = new StringBuilder();
        void Triple(string s, string p, string o) => sb.Append(s).Append(' ').Append(p).Append(' ').Append(o).Append(" .\n");
        string Typed(DateTimeOffset d) => Literal(AdapterHelpers.Iso(d)) + "^^" + Full(Xsd + "dateTime");

        foreach (var n in graph.Nodes)
        {
            var s = Iri("node", n.Id);
            Triple(s, Full(Rdf + "type"), Iri("type", n.Type));
            Triple(s, Full(Rdfs + "label"), Literal(n.Name));
            if (n.Description is not null) Triple(s, Full(Rdfs + "comment"), Literal(n.Description));
            foreach (var a in n.Aliases) Triple(s, Full("http://www.w3.org/2004/02/skos/core#altLabel"), Literal(a));
            foreach (var t in n.Tags) Triple(s, Full("https://schema.org/keywords"), Literal(t));
            if (n.Content is not null) Triple(s, Iri("prop", "content"), Literal(n.Content));
            foreach (var o in n.Observations) Triple(s, Iri("prop", "observation"), Literal(o));
            if (n.Scope is not null) Triple(s, Iri("prop", "scope"), Literal(n.Scope));
            if (n.Source is not null) Triple(s, Iri("prop", "source"), Literal(n.Source));
            if (n.Cluster is not null) Triple(s, Iri("prop", "cluster"), Literal(n.Cluster));
            if (n.CreatedAt is { } c) Triple(s, Full("http://purl.org/dc/terms/created"), Typed(c));
            if (n.UpdatedAt is { } u) Triple(s, Full("http://purl.org/dc/terms/modified"), Typed(u));
            if (n.ValidFrom is { } vf) Triple(s, Iri("prop", "validFrom"), Typed(vf));
            if (n.ValidTo is { } vt) Triple(s, Iri("prop", "validTo"), Typed(vt));
            if (n.Version is { } ver) Triple(s, Iri("prop", "version"), Literal(ver.ToString(CultureInfo.InvariantCulture)) + "^^" + Full(Xsd + "integer"));
            foreach (var kv in n.Properties)
            {
                if (kv.Value is null)
                {
                    continue;
                }
                var p = Iri("prop", kv.Key);
                if (kv.Value is JsonValue v)
                {
                    Triple(s, p, v.GetValueKind() switch
                    {
                        JsonValueKind.String => Literal(v.GetValue<string>()),
                        JsonValueKind.Number => Literal(v.ToJsonString()) + "^^" + Full(Xsd + "double"),
                        JsonValueKind.True or JsonValueKind.False => Literal(v.ToJsonString()) + "^^" + Full(Xsd + "boolean"),
                        _ => Literal(v.ToJsonString())
                    });
                }
                else
                {
                    Triple(s, p, Literal(kv.Value.ToJsonString()));
                }
            }
        }
        foreach (var e in graph.Edges)
        {
            var p = Iri("rel", e.Type);
            Triple(Iri("node", e.SourceId), p, Iri("node", e.TargetId));
            if (e.Bidirectional)
            {
                Triple(Iri("node", e.TargetId), p, Iri("node", e.SourceId));
            }
        }
        if (graph.Edges.Any(e => e.Fact is not null || e.Weight is not null || e.Properties.Count > 0))
        {
            warnings.Add("Edge facts, weights and properties are not represented in plain N-Triples.");
        }
        return Result(sb.ToString(), warnings);
    }

    internal static string Literal(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(ch))
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
