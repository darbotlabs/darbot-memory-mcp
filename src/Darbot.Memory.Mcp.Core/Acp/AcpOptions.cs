namespace Darbot.Memory.Mcp.Core.Acp;

/// <summary>Configuration for the ACP agent (bound from <c>Darbot:Acp</c>).</summary>
public sealed class AcpOptions
{
    public const string SectionName = "Darbot:Acp";

    public bool Enabled { get; set; } = true;
    public string Path { get; set; } = "/acp";
    public bool RequireAuthorization { get; set; } = true;
    public int MaxMessageBytes { get; set; } = 4 * 1024 * 1024;
    public int PromptTimeoutSeconds { get; set; } = 300;
    public bool PersistSessionsAsConversations { get; set; } = true;

    /// <summary>Timeout for agent-to-client requests such as fs/* (permission requests wait for the user).</summary>
    public int ClientRequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Advertise an agent-handled auth method and reject session methods until authenticated.</summary>
    public bool RequireAgentAuth { get; set; }

    /// <summary>Expected key for the agent auth method (passed as <c>_meta.apiKey</c> on <c>authenticate</c>).</summary>
    public string? ApiKey { get; set; }

    public string AgentName { get; set; } = "darbot-memory";
    public string AgentTitle { get; set; } = "Darbot Memory";
    public string AgentVersion { get; set; } = "1.0.0";
    public string DefaultGraph { get; set; } = "default";
    public int MaxRecallResults { get; set; } = 5;

    public AcpOptions Clone() => (AcpOptions)MemberwiseClone();
}

