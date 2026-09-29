namespace Darbot.Memory.Mcp.Core.Graph;

/// <summary>Case-insensitive lookup of schema adapters by name or alias.</summary>
public sealed class GraphSchemaRegistry : IGraphSchemaRegistry
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["obsidian-vault"] = "obsidian",
        ["vault"] = "obsidian",
        ["nexus-forge"] = "kgforge",
        ["forge"] = "kgforge",
        ["nexus"] = "kgforge",
        ["3d-kg"] = "3dkg",
        ["3dkg-graph"] = "3dkg",
        ["anthropic-memory"] = "mcp-memory",
        ["mcp"] = "mcp-memory",
        ["memory"] = "mcp-memory",
        ["zep"] = "graphiti",
        ["memgpt"] = "letta",
        ["rdf"] = "ntriples",
        ["nt"] = "ntriples",
        ["neo4j"] = "cypher",
        ["json-ld"] = "jsonld",
        ["schema.org"] = "jsonld",
        ["darbot"] = "darbot-kg",
        ["native"] = "darbot-kg",
        ["canonical"] = "darbot-kg",
        ["mem-0"] = "mem0",
        ["super-memory"] = "supermemory"
    };

    private readonly Dictionary<string, IGraphSchemaAdapter> _byName;

    public GraphSchemaRegistry(IEnumerable<IGraphSchemaAdapter> adapters)
    {
        Adapters = adapters.ToList();
        _byName = new Dictionary<string, IGraphSchemaAdapter>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in Adapters)
        {
            _byName[adapter.Name] = adapter;
        }
    }

    public IReadOnlyList<IGraphSchemaAdapter> Adapters { get; }

    public IGraphSchemaAdapter? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        var key = name.Trim();
        if (_byName.TryGetValue(key, out var adapter))
        {
            return adapter;
        }
        return Aliases.TryGetValue(key, out var target) && _byName.TryGetValue(target, out adapter) ? adapter : null;
    }

    public IGraphSchemaAdapter GetRequired(string name)
        => Find(name) ?? throw new ArgumentException($"Unknown graph schema '{name}'. Available: {string.Join(", ", Adapters.Select(a => a.Name))}.", nameof(name));
}
