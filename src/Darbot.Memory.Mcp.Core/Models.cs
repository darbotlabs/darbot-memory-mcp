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

/// <summary>
/// Request model for searching conversations
/// </summary>
public record ConversationSearchRequest
{
    public string? ConversationId { get; init; }
    public string? SearchText { get; init; }
    public string? Model { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public IReadOnlyList<string> ToolsUsed { get; init; } = Array.Empty<string>();
    public int Skip { get; init; } = 0;
    public int Take { get; init; } = 50;
    public string SortBy { get; init; } = "timestamp"; // timestamp, conversationId, turnNumber
    public bool SortDescending { get; init; } = true;
}

/// <summary>
/// Response model for conversation search results
/// </summary>
public record ConversationSearchResponse
{
    public required IReadOnlyList<ConversationTurn> Results { get; init; }
    public required int TotalCount { get; init; }
    public required bool HasMore { get; init; }
    public required int Skip { get; init; }
    public required int Take { get; init; }
}

/// <summary>
/// Request model for listing conversations
/// </summary>
public record ConversationListRequest
{
    public int Skip { get; init; } = 0;
    public int Take { get; init; } = 50;
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public string SortBy { get; init; } = "lastActivity"; // lastActivity, conversationId, turnCount
    public bool SortDescending { get; init; } = true;
}

/// <summary>
/// Summary of a conversation for listing purposes
/// </summary>
public record ConversationSummary
{
    public required string ConversationId { get; init; }
    public required int TurnCount { get; init; }
    public required DateTime FirstTurnTimestamp { get; init; }
    public required DateTime LastTurnTimestamp { get; init; }
    public required IReadOnlyList<string> ModelsUsed { get; init; }
    public required IReadOnlyList<string> ToolsUsed { get; init; }
    public string? LastPrompt { get; init; }
}

/// <summary>
/// Response model for conversation listing
/// </summary>
public record ConversationListResponse
{
    public required IReadOnlyList<ConversationSummary> Conversations { get; init; }
    public required int TotalCount { get; init; }
    public required bool HasMore { get; init; }
    public required int Skip { get; init; }
    public required int Take { get; init; }
}
