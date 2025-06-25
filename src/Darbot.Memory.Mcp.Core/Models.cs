namespace Darbot.Memory.Mcp.Core.Models;

/// <summary>
/// Represents a conversation turn in the MCP system
/// </summary>
public record ConversationTurn
{
    public required string ConversationId { get; init; }
    public required int TurnNumber { get; init; }
    public required DateTime UtcTimestamp { get; init; }
    public required string Prompt { get; init; }
    public required string Model { get; init; }
    public required string Response { get; init; }
    public IReadOnlyList<string> ToolsUsed { get; init; } = Array.Empty<string>();
    public string? Hash { get; init; }
    public string SchemaVersion { get; init; } = "v1.0.0";
}

/// <summary>
/// Represents the header metadata for a conversation turn
/// </summary>
public record ConversationMetadata
{
    public required string ConversationId { get; init; }
    public required int TurnNumber { get; init; }
    public required DateTime UtcTimestamp { get; init; }
    public required string Hash { get; init; }
    public string SchemaVersion { get; init; } = "v1.0.0";
}

/// <summary>
/// Request model for batch writing messages
/// </summary>
public record BatchWriteRequest
{
    public required IReadOnlyList<ConversationTurn> Messages { get; init; }
}

/// <summary>
/// Response model for batch write operations
/// </summary>
public record BatchWriteResponse
{
    public required bool Success { get; init; }
    public required int ProcessedCount { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public string? Message { get; init; }
}
