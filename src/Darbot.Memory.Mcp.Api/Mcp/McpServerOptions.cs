namespace Darbot.Memory.Mcp.Api.Mcp;

/// <summary>Configuration for the stateless MCP endpoint (bound from <c>Darbot:Mcp</c>).</summary>
public sealed class McpServerOptions
{
    public const string SectionName = "Darbot:Mcp";
    public const string AuthorizationPolicy = "DarbotMemoryWriter";

    public string Path { get; set; } = "/mcp";

    /// <summary>No Mcp-Session-Id, every request is self-contained; safe behind a load balancer or serverless host.</summary>
    public bool Stateless { get; set; } = true;

    public bool RequireAuthorization { get; set; } = true;
    public string ServerName { get; set; } = "darbot-memory-mcp";
    public string ServerTitle { get; set; } = "Darbot Memory MCP";
    public string ServerVersion { get; set; } = "1.0.0";

    public string Instructions { get; set; } =
        "Darbot Memory persists conversational audit trails, workspace snapshots and a schema-neutral knowledge graph. " +
        "Use memory_* tools to write and query conversation turns, workspace_* tools to capture and restore workspaces, " +
        "and graph_* tools to remember facts, relate entities, search, explore neighborhoods and import/export memory schemas " +
        "(3DKG, KGForge, Obsidian, Supermemory, MCP memory, mem0, Graphiti and others; call graph_schemas to list them). " +
        "Every call is self-contained: pass the graph name explicitly when you do not want the 'default' graph.";

    // Tolerated for backwards compatibility with existing appsettings; not used by the stateless transport.
    public int MaxConcurrentClients { get; set; } = 10;
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);
}
