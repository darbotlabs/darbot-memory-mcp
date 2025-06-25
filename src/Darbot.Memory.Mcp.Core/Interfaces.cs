using Darbot.Memory.Mcp.Core.Models;

namespace Darbot.Memory.Mcp.Core.Interfaces;

/// <summary>
/// Interface for storage providers that persist conversation turns
/// </summary>
public interface IStorageProvider
{
    /// <summary>
    /// Writes a conversation turn to storage
    /// </summary>
    Task<bool> WriteConversationTurnAsync(ConversationTurn turn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes multiple conversation turns to storage
    /// </summary>
    Task<BatchWriteResponse> WriteBatchAsync(IEnumerable<ConversationTurn> turns, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the storage provider is healthy and accessible
    /// </summary>
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for formatting conversation turns into markdown
/// </summary>
public interface IConversationFormatter
{
    /// <summary>
    /// Formats a conversation turn into markdown content
    /// </summary>
    string FormatToMarkdown(ConversationTurn turn);

    /// <summary>
    /// Generates a filename for a conversation turn
    /// </summary>
    string GenerateFileName(ConversationTurn turn);
}

/// <summary>
/// Interface for calculating hashes of conversation content
/// </summary>
public interface IHashCalculator
{
    /// <summary>
    /// Calculates a cryptographic hash of the conversation turn content
    /// </summary>
    string CalculateHash(ConversationTurn turn);

    /// <summary>
    /// Validates that a conversation turn's hash is correct
    /// </summary>
    bool ValidateHash(ConversationTurn turn);
}

/// <summary>
/// Interface for the main conversation service
/// </summary>
public interface IConversationService
{
    /// <summary>
    /// Persists a single conversation turn
    /// </summary>
    Task<bool> PersistTurnAsync(ConversationTurn turn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists multiple conversation turns in a batch
    /// </summary>
    Task<BatchWriteResponse> PersistBatchAsync(IEnumerable<ConversationTurn> turns, CancellationToken cancellationToken = default);
}