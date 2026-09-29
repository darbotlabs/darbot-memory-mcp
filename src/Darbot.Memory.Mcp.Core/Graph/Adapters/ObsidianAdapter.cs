using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Darbot.Memory.Mcp.Core.Graph.Adapters;

/// <summary>
/// Obsidian vault: one markdown note per node (YAML frontmatter, observations, Dataview-style relations) plus a JSON Canvas file.
/// Import payload / export files are path-to-content maps.
/// </summary>
public sealed partial class ObsidianAdapter : GraphAdapterBase
{
    private const string ObservationsHeading = "## Observations";
    private const string RelationsHeading = "## Relations";
    private const string LinksTo = "links_to";

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly HashSet<string> ReservedFrontmatter = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "type", "name", "aliases", "alias", "tags", "tag", "created", "updated", "modified", "description",
        "scope", "source", "cluster", "validFrom", "validTo", "version", "isLatest", "position"
    };

    private static readonly HashSet<string> AttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".bmp", ".pdf", ".mp3", ".mp4", ".wav", ".webm", ".mov", ".ogg"
    };

    public override string Name => "obsidian";
    public override string DisplayName => "Obsidian vault";
    public override string Description => "Markdown notes with YAML frontmatter, [[wikilinks]], Dataview inline fields and a JSON Canvas 1.0 file. Payload/export is a map of path to file content.";

    [GeneratedRegex(@"!?\[\[(?<t>[^\]\|#\^]+?)(?:[#\^][^\]\|]*)?(?:\|[^\]]*)?\]\]")]
    private static partial Regex WikiLink();

    [GeneratedRegex(@"^\s*(?:[-*+]\s+)?(?<k>[A-Za-z_][\w \-]*?)::\s*(?<v>\S.*)$")]
    private static partial Regex InlineField();

    [GeneratedRegex(@"(?<![\w/&#\[])#(?<t>[A-Za-z_][\w/\-]*)")]
    private static partial Regex InlineTag();

    [GeneratedRegex(@"^(?<k>""[^""]*""|[^:\s#\-][^:]*?)\s*:(?:\s+(?<v>.*)|\s*)$")]
    private static partial Regex YamlKey();

    [GeneratedRegex(@"[\\/:*?""<>|#^\[\]]+")]
    private static partial Regex UnsafeFileChars();

    // ------------------------------------------------------------------ export

    public override GraphExportResult Export(KnowledgeGraph graph)
    {
        var warnings = new List<string>();
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var baseNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = AdapterHelpers.IndexNodes(graph);

        foreach (var n in graph.Nodes)
        {
            var baseName = SafeFileName(n.Name);
            if (baseName.Length == 0)
            {
                baseName = SafeFileName(n.Id);
            }
            var candidate = baseName;
            var counter = 1;
            while (!used.Add(candidate))
            {
                candidate = counter == 1 ? $"{baseName} ({SafeFileName(n.Id)})" : $"{baseName} ({SafeFileName(n.Id)}) {counter}";
                counter++;
            }
            baseNames[n.Id] = candidate;
        }

        var outgoing = graph.Edges.ToLookup(e => e.SourceId, StringComparer.Ordinal);
        foreach (var n in graph.Nodes)
        {
            files[baseNames[n.Id] + ".md"] = RenderNote(n, baseNames, outgoing[n.Id], warnings);
        }

        var lossyEdges = graph.Edges.Count(e => e.Fact is not null || e.Weight is not null || e.Properties.Count > 0 || e.ValidFrom is not null);
        if (lossyEdges > 0)
        {
            warnings.Add($"{lossyEdges} edge(s) carry facts/weights/properties/validity that Obsidian relation lines cannot hold; only the relation type and target were written.");
        }
        if (graph.Nodes.Any(n => n.Embedding is not null))
        {
            warnings.Add("Embeddings are not written to Obsidian notes.");
        }

        var canvasName = SafeFileName(graph.Name);
        if (canvasName.Length == 0)
        {
            canvasName = "graph";
        }
        while (files.ContainsKey(canvasName + ".canvas"))
        {
            canvasName += "_graph";
        }
        files[canvasName + ".canvas"] = RenderCanvas(graph, index);
        return FilesResult(files, warnings.Distinct().ToList());
    }

    private static string SafeFileName(string value)
    {
        var cleaned = UnsafeFileChars().Replace(value ?? string.Empty, " ").Trim();
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim(' ', '.');
        return cleaned.Length > 120 ? cleaned[..120].TrimEnd() : cleaned;
    }

    private static string RelationKey(string type)
    {
        var key = Regex.Replace(type.Trim(), @"[^\w\- ]+", "_").Replace(' ', '_');
        return key.Length == 0 || !(char.IsLetter(key[0]) || key[0] == '_') ? "rel_" + key : key;
    }

    private static string RenderNote(GraphNode n, Dictionary<string, string> baseNames, IEnumerable<GraphEdge> edges, List<string> warnings)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        WriteYaml(sb, "id", JsonValue.Create(n.Id));
        WriteYaml(sb, "type", JsonValue.Create(n.Type));
        if (!string.Equals(baseNames[n.Id], n.Name, StringComparison.Ordinal))
        {
            WriteYaml(sb, "name", JsonValue.Create(n.Name));
        }
        if (n.Aliases.Count > 0)
        {
            WriteYaml(sb, "aliases", AdapterHelpers.ToArray(n.Aliases));
        }
        if (n.Tags.Count > 0)
        {
            WriteYaml(sb, "tags", AdapterHelpers.ToArray(n.Tags.Select(t => t.TrimStart('#').Replace(' ', '-'))));
        }
        if (n.CreatedAt is { } c)
        {
            WriteYaml(sb, "created", JsonValue.Create(AdapterHelpers.Iso(c)));
        }
        if (n.UpdatedAt is { } u)
        {
            WriteYaml(sb, "updated", JsonValue.Create(AdapterHelpers.Iso(u)));
        }
        if (n.Description is not null)
        {
            WriteYaml(sb, "description", JsonValue.Create(n.Description));
        }
        if (n.Scope is not null)
        {
            WriteYaml(sb, "scope", JsonValue.Create(n.Scope));
        }
        if (n.Source is not null)
        {
            WriteYaml(sb, "source", JsonValue.Create(n.Source));
        }
        if (n.Cluster is not null)
        {
            WriteYaml(sb, "cluster", JsonValue.Create(n.Cluster));
        }
        if (n.ValidFrom is { } vf)
        {
            WriteYaml(sb, "validFrom", JsonValue.Create(AdapterHelpers.Iso(vf)));
        }
        if (n.ValidTo is { } vt)
        {
            WriteYaml(sb, "validTo", JsonValue.Create(AdapterHelpers.Iso(vt)));
        }
        if (n.Version is { } ver)
        {
            WriteYaml(sb, "version", JsonValue.Create(ver));
        }
        if (!n.IsLatest)
        {
            WriteYaml(sb, "isLatest", JsonValue.Create(false));
        }
        if (n.Position is { } pos)
        {
            WriteYaml(sb, "position", AdapterHelpers.PositionToJson(pos));
        }
        foreach (var kv in n.Properties)
        {
            if (ReservedFrontmatter.Contains(kv.Key))
            {
                warnings.Add($"Property '{kv.Key}' on '{n.Name}' collides with a reserved frontmatter key and was not written.");
                continue;
            }
            WriteYaml(sb, kv.Key, kv.Value);
        }
        sb.Append("---\n");

        if (!string.IsNullOrWhiteSpace(n.Content))
        {
            sb.Append(n.Content.TrimEnd()).Append("\n\n");
        }
        if (n.Observations.Count > 0)
        {
            sb.Append(ObservationsHeading).Append('\n');
            foreach (var o in n.Observations)
            {
                sb.Append("- ").Append(o.ReplaceLineEndings(" ")).Append('\n');
            }
            sb.Append('\n');
        }
        var edgeList = edges.ToList();
        if (edgeList.Count > 0)
        {
            sb.Append(RelationsHeading).Append('\n');
            foreach (var e in edgeList)
            {
                var target = baseNames.TryGetValue(e.TargetId, out var t) ? t : SafeFileName(e.TargetId);
                sb.Append("- ").Append(RelationKey(e.Type)).Append(":: [[").Append(target).Append("]]\n");
            }
        }
        return sb.ToString();
    }

    private static string RenderCanvas(KnowledgeGraph graph, Dictionary<string, GraphNode> index)
    {
        var cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Max(1, graph.Nodes.Count))));
        var nodes = new JsonArray();
        var i = 0;
        foreach (var n in graph.Nodes)
        {
            var x = n.Position is { } p ? (int)Math.Round(p.X) : i % cols * 340;
            var y = n.Position is { } q ? (int)Math.Round(q.Y) : i / cols * 200;
            i++;
            var text = new StringBuilder("# ").Append(n.Name);
            var summary = n.Description ?? AdapterHelpers.Truncate(n.Content, 160);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                text.Append("\n\n").Append(summary);
            }
            nodes.Add(new JsonObject
            {
                ["id"] = n.Id,
                ["type"] = "text",
                ["x"] = x,
                ["y"] = y,
                ["width"] = 300,
                ["height"] = 140,
                ["text"] = text.ToString()
            });
        }
        var edges = new JsonArray();
        foreach (var e in graph.Edges)
        {
            if (!index.ContainsKey(e.SourceId) || !index.ContainsKey(e.TargetId))
            {
                continue;
            }
            edges.Add(new JsonObject
            {
                ["id"] = e.Id,
                ["fromNode"] = e.SourceId,
                ["toNode"] = e.TargetId,
                ["label"] = e.Type
            });
        }
        return new JsonObject { ["nodes"] = nodes, ["edges"] = edges }.ToJsonString(GraphJson.Options);
    }

    private static void WriteYaml(StringBuilder sb, string key, JsonNode? value)
    {
        var k = Regex.IsMatch(key, @"^[A-Za-z_][A-Za-z0-9_\-]*$") ? key : JsonSerializer.Serialize(key, Relaxed);
        switch (value)
        {
            case null:
                sb.Append(k).Append(": null\n");
                break;
            case JsonArray a when a.Count > 0 && a.All(x => x is JsonValue):
                sb.Append(k).Append(":\n");
                foreach (var item in a)
                {
                    sb.Append("  - ").Append(item!.ToJsonString(Relaxed)).Append('\n');
                }
                break;
            default:
                sb.Append(k).Append(": ").Append(value.ToJsonString(Relaxed)).Append('\n');
                break;
        }
    }

    // ------------------------------------------------------------------ import

    private sealed class Note
    {
        public required string Path { get; init; }
        public required string BaseName { get; init; }
        public required string NodeId { get; set; }
        public required JsonObject Front { get; init; }
        public required string Body { get; init; }
    }

    public override GraphImportResult Import(string payload, string? graphName = null)
    {
        RequirePayload(payload);
        var b = new GraphBuilder();
        var files = ReadFiles(payload, graphName, b);

        var notes = new List<Note>();
        var canvases = new List<(string Path, string Content)>();
        foreach (var (path, content) in files)
        {
            if (path.StartsWith(".obsidian/", StringComparison.OrdinalIgnoreCase) || path.Contains("/.obsidian/") || path.Contains("/.trash/"))
            {
                continue;
            }
            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                var (front, body) = SplitFrontmatter(content);
                var noExt = path[..^3];
                var baseName = noExt[(noExt.LastIndexOf('/') + 1)..];
                notes.Add(new Note
                {
                    Path = path,
                    BaseName = baseName,
                    NodeId = AdapterHelpers.Str(front["id"]) ?? AdapterHelpers.Slug(noExt, "note"),
                    Front = front,
                    Body = body
                });
            }
            else if (path.EndsWith(".canvas", StringComparison.OrdinalIgnoreCase))
            {
                canvases.Add((path, content));
            }
        }
        if (notes.Count == 0 && canvases.Count == 0)
        {
            throw new ArgumentException("The obsidian payload contains no markdown notes or canvas files.");
        }

        var byRef = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in notes)
        {
            var name = AdapterHelpers.Str(note.Front["name"]) ?? note.BaseName;
            var noExt = note.Path[..^3];
            byRef.TryAdd(noExt, note.NodeId);
            byRef.TryAdd(note.BaseName, note.NodeId);
            byRef.TryAdd(name, note.NodeId);
            foreach (var alias in AdapterHelpers.StrList(note.Front["aliases"] ?? note.Front["alias"], commaSeparated: true))
            {
                byRef.TryAdd(alias, note.NodeId);
            }
        }

        var pendingEdges = new List<(string Source, string Type, string Target)>();
        foreach (var note in notes)
        {
            ImportNote(b, note, byRef, pendingEdges);
        }

        foreach (var (source, type, target) in pendingEdges)
        {
            var targetId = ResolveLink(b, target, byRef);
            if (targetId is null || targetId == source)
            {
                continue;
            }
            b.AddRelation(source, targetId, type);
        }

        foreach (var (path, content) in canvases)
        {
            ImportCanvas(b, path, content, byRef);
        }
        return b.Build(graphName);
    }

    private static Dictionary<string, string> ReadFiles(string payload, string? graphName, GraphBuilder b)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        JsonNode? root = null;
        try
        {
            root = JsonNode.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The obsidian payload is not valid JSON (expected an object of path to file content): {ex.Message}", ex);
        }
        if (root is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                var path = kv.Key.Replace('\\', '/').TrimStart('/');
                if (AdapterHelpers.Str(kv.Value) is { } content && kv.Value is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                {
                    result[path] = content;
                }
                else
                {
                    b.Warn($"File '{kv.Key}' is not a string; skipped.");
                }
            }
            return result;
        }
        throw new ArgumentException("The obsidian payload must be a JSON object of path to file content.");
    }

    private static (JsonObject Front, string Body) SplitFrontmatter(string text)
    {
        text = text.TrimStart('\uFEFF').Replace("\r\n", "\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (new JsonObject(), text);
        }
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            return (new JsonObject(), text);
        }
        var yaml = end > 4 ? text[4..end] : string.Empty;
        var rest = text[(end + 4)..];
        var nl = rest.IndexOf('\n');
        rest = nl < 0 ? string.Empty : rest[(nl + 1)..];
        var lines = yaml.Split('\n');
        var i = 0;
        return (ParseMap(lines, ref i, 0), rest);
    }

    private static int Indent(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] == ' ')
        {
            n++;
        }
        return n;
    }

    private static JsonObject ParseMap(string[] lines, ref int i, int indent)
    {
        var map = new JsonObject();
        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                i++;
                continue;
            }
            var ind = Indent(line);
            if (ind < indent)
            {
                break;
            }
            var m = YamlKey().Match(line.Trim());
            i++;
            if (ind > indent || !m.Success)
            {
                continue;
            }
            var key = m.Groups["k"].Value;
            if (key.StartsWith('"'))
            {
                key = key.Trim('"');
            }
            var v = m.Groups["v"].Success ? m.Groups["v"].Value.Trim() : string.Empty;
            if (v.Length > 0)
            {
                map[key] = ParseScalar(v);
                continue;
            }
            var j = i;
            while (j < lines.Length && string.IsNullOrWhiteSpace(lines[j]))
            {
                j++;
            }
            if (j < lines.Length && Indent(lines[j]) >= ind && lines[j].TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                var list = new JsonArray();
                while (j < lines.Length)
                {
                    var t = lines[j].Trim();
                    if (t.Length == 0)
                    {
                        j++;
                        continue;
                    }
                    if (Indent(lines[j]) < ind || !t.StartsWith("- ", StringComparison.Ordinal))
                    {
                        break;
                    }
                    list.Add(ParseScalar(t[2..]));
                    j++;
                }
                i = j;
                map[key] = list;
            }
            else if (j < lines.Length && Indent(lines[j]) > ind)
            {
                i = j;
                map[key] = ParseMap(lines, ref i, Indent(lines[j]));
            }
            else
            {
                map[key] = null;
            }
        }
        return map;
    }

    private static JsonNode? ParseScalar(string raw)
    {
        var v = raw.Trim();
        if (v.Length == 0 || v == "~" || v.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (v[0] == '"')
        {
            try
            {
                return JsonNode.Parse(v);
            }
            catch (JsonException)
            {
                return JsonValue.Create(v.Trim('"'));
            }
        }
        if (v[0] == '\'' && v.Length >= 2 && v[^1] == '\'')
        {
            return JsonValue.Create(v[1..^1].Replace("''", "'"));
        }
        if (v[0] == '[' || v[0] == '{')
        {
            try
            {
                return JsonNode.Parse(v);
            }
            catch (JsonException)
            {
                if (v[0] == '[' && v[^1] == ']')
                {
                    return AdapterHelpers.ToArray(v[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.Trim('"', '\'')));
                }
                return JsonValue.Create(v);
            }
        }
        if (v.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(true);
        }
        if (v.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(false);
        }
        if (Regex.IsMatch(v, @"^-?(0|[1-9]\d*)(\.\d+)?$") && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? JsonValue.Create(l) : JsonValue.Create(d);
        }
        return JsonValue.Create(v);
    }

    private static void ImportNote(GraphBuilder b, Note note, Dictionary<string, string> byRef, List<(string, string, string)> pending)
    {
        var f = note.Front;
        var tags = AdapterHelpers.StrList(f["tags"] ?? f["tag"], commaSeparated: true).Select(t => t.TrimStart('#')).ToList();
        var props = new Dictionary<string, JsonNode?>();
        foreach (var kv in f)
        {
            if (!ReservedFrontmatter.Contains(kv.Key))
            {
                props[kv.Key] = kv.Value?.DeepClone();
            }
        }

        var content = new StringBuilder();
        var observations = new List<string>();
        var section = 0; // 0 content, 1 observations, 2 relations
        var inFence = false;
        var scan = new List<string>();
        foreach (var raw in note.Body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            if (!inFence)
            {
                if (line.Equals(ObservationsHeading, StringComparison.OrdinalIgnoreCase))
                {
                    section = 1;
                    continue;
                }
                if (line.Equals(RelationsHeading, StringComparison.OrdinalIgnoreCase))
                {
                    section = 2;
                    continue;
                }
                if (section != 0 && Regex.IsMatch(line, @"^#{1,2}\s"))
                {
                    section = 0;
                }
            }

            if (inFence || section == 0)
            {
                content.Append(line).Append('\n');
                if (!inFence)
                {
                    scan.Add(line);
                }
                continue;
            }

            var text = Regex.Replace(line, @"^\s*[-*+]\s+", string.Empty).Trim();
            if (text.Length == 0)
            {
                continue;
            }
            if (section == 1)
            {
                observations.Add(text);
                scan.Add(text);
            }
            else
            {
                var field = InlineField().Match(line);
                if (field.Success && WikiLink().IsMatch(field.Groups["v"].Value))
                {
                    var type = NormalizeKey(field.Groups["k"].Value);
                    foreach (Match link in WikiLink().Matches(field.Groups["v"].Value))
                    {
                        pending.Add((note.NodeId, type, link.Groups["t"].Value));
                    }
                }
                else
                {
                    foreach (Match link in WikiLink().Matches(text))
                    {
                        pending.Add((note.NodeId, "related_to", link.Groups["t"].Value));
                    }
                }
            }
        }

        foreach (var line in scan)
        {
            var field = InlineField().Match(line);
            if (field.Success)
            {
                var key = NormalizeKey(field.Groups["k"].Value);
                var value = field.Groups["v"].Value.Trim();
                if (WikiLink().IsMatch(value))
                {
                    foreach (Match link in WikiLink().Matches(value))
                    {
                        pending.Add((note.NodeId, key, link.Groups["t"].Value));
                    }
                }
                else
                {
                    props.TryAdd(key, JsonValue.Create(value));
                }
                continue;
            }
            foreach (Match link in WikiLink().Matches(line))
            {
                pending.Add((note.NodeId, LinksTo, link.Groups["t"].Value));
            }
            foreach (Match tag in InlineTag().Matches(line))
            {
                tags.Add(tag.Groups["t"].Value);
            }
        }

        var body = content.ToString().Trim();
        b.AddNode(new GraphNode
        {
            Id = note.NodeId,
            Name = AdapterHelpers.Str(f["name"]) ?? note.BaseName,
            Type = AdapterHelpers.Str(f["type"]) ?? "note",
            Description = AdapterHelpers.Str(f["description"]),
            Aliases = AdapterHelpers.StrList(f["aliases"] ?? f["alias"], commaSeparated: true),
            Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Observations = observations,
            Content = body.Length == 0 ? null : body,
            Scope = AdapterHelpers.Str(f["scope"]),
            Source = AdapterHelpers.Str(f["source"]),
            Cluster = AdapterHelpers.Str(f["cluster"]),
            ValidFrom = AdapterHelpers.Date(f["validFrom"]),
            ValidTo = AdapterHelpers.Date(f["validTo"]),
            Version = AdapterHelpers.Int(f["version"]),
            IsLatest = AdapterHelpers.Bool(f["isLatest"]) ?? true,
            Position = AdapterHelpers.Position(f["position"]),
            CreatedAt = AdapterHelpers.Date(f["created"]),
            UpdatedAt = AdapterHelpers.Date(f["updated"] ?? f["modified"]),
            Properties = props
        });
    }

    private static string NormalizeKey(string key) => Regex.Replace(key.Trim(), @"\s+", "_");

    private static string? ResolveLink(GraphBuilder b, string reference, Dictionary<string, string> byRef)
    {
        var target = reference.Trim().Replace('\\', '/');
        if (target.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            target = target[..^3];
        }
        var ext = Path.GetExtension(target);
        if (ext.Length > 0 && AttachmentExtensions.Contains(ext))
        {
            return null;
        }
        if (byRef.TryGetValue(target, out var id))
        {
            return id;
        }
        var last = target[(target.LastIndexOf('/') + 1)..];
        if (byRef.TryGetValue(last, out id))
        {
            return id;
        }
        if (last.Length == 0)
        {
            return null;
        }
        id = AdapterHelpers.IdFromName(last);
        if (!b.HasNode(id))
        {
            b.AddNode(new GraphNode { Id = id, Name = last, Type = "note" });
        }
        byRef[last] = id;
        return id;
    }

    private void ImportCanvas(GraphBuilder b, string path, string content, Dictionary<string, string> byRef)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            b.Warn($"Canvas '{path}' is not valid JSON; skipped.");
            return;
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cn in (AdapterHelpers.Get(root, "nodes") as JsonArray) ?? new JsonArray())
        {
            var cid = AdapterHelpers.Str(cn, "id");
            if (cn is not JsonObject || cid is null)
            {
                b.Warn($"Canvas '{path}': skipped a node without id.");
                continue;
            }
            var type = AdapterHelpers.Str(cn, "type") ?? "text";
            var position = new GraphPosition(AdapterHelpers.Num(cn["x"]) ?? 0, AdapterHelpers.Num(cn["y"]) ?? 0, 0);
            string? nodeId = null;
            switch (type)
            {
                case "file":
                    nodeId = AdapterHelpers.Str(cn, "file") is { } file ? ResolveLink(b, file, byRef) : null;
                    break;
                case "text":
                    {
                        var text = AdapterHelpers.Str(cn, "text") ?? string.Empty;
                        var title = text.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimStart('#', '*', ' ').TrimEnd('*', ' ')).FirstOrDefault(l => l.Length > 0);
                        if (b.HasNode(cid))
                        {
                            nodeId = cid;
                        }
                        else if (title is not null && byRef.TryGetValue(title, out var existing))
                        {
                            nodeId = existing;
                        }
                        else
                        {
                            nodeId = "canvas-" + cid;
                            b.AddNode(new GraphNode { Id = nodeId, Name = title ?? cid, Type = "note", Content = text.Length == 0 ? null : text });
                        }
                        break;
                    }
                case "link":
                    nodeId = "canvas-" + cid;
                    b.AddNode(new GraphNode { Id = nodeId, Name = AdapterHelpers.Str(cn, "url") ?? cid, Type = "link", Properties = new() { ["url"] = AdapterHelpers.Str(cn, "url") } });
                    break;
                case "group":
                    nodeId = "canvas-" + cid;
                    b.AddNode(new GraphNode { Id = nodeId, Name = AdapterHelpers.Str(cn, "label") ?? cid, Type = "group" });
                    break;
                default:
                    b.Warn($"Canvas '{path}': unsupported node type '{type}' skipped.");
                    break;
            }
            if (nodeId is null)
            {
                continue;
            }
            map[cid] = nodeId;
            if (b.TryGetNode(nodeId, out var node) && node.Position is null)
            {
                b.AddNode(node with { Position = position });
            }
        }
        foreach (var ce in (AdapterHelpers.Get(root, "edges") as JsonArray) ?? new JsonArray())
        {
            var from = AdapterHelpers.Str(ce, "fromNode");
            var to = AdapterHelpers.Str(ce, "toNode");
            if (from is null || to is null || !map.TryGetValue(from, out var s) || !map.TryGetValue(to, out var t))
            {
                b.Warn($"Canvas '{path}': skipped an edge with unknown endpoints.");
                continue;
            }
            var label = AdapterHelpers.Str(ce, "label");
            b.AddRelation(s, t, string.IsNullOrWhiteSpace(label) ? "related_to" : NormalizeKey(label));
        }
    }
}
