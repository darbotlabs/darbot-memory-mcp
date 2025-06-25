using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Microsoft.Extensions.Logging;

namespace Darbot.Memory.Mcp.Core.Services;

/// <summary>
/// Main service for managing conversation persistence
/// </summary>
public class ConversationService : IConversationService
{
    private readonly IStorageProvider _storageProvider;
    private readonly IHashCalculator _hashCalculator;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(
        IStorageProvider storageProvider,
        IHashCalculator hashCalculator,
        ILogger<ConversationService> logger)
    {
        _storageProvider = storageProvider;
        _hashCalculator = hashCalculator;
        _logger = logger;
    }

    public async Task<bool> PersistTurnAsync(ConversationTurn turn, CancellationToken cancellationToken = default)
    {
        try
        {
            // Calculate hash if not provided
            var turnWithHash = string.IsNullOrEmpty(turn.Hash)
                ? turn with { Hash = _hashCalculator.CalculateHash(turn) }
                : turn;

            // Validate hash if provided
            if (!string.IsNullOrEmpty(turn.Hash) && !_hashCalculator.ValidateHash(turnWithHash))
            {
                _logger.LogWarning("Hash validation failed for conversation {ConversationId}, turn {Turn}",
                    turn.ConversationId, turn.TurnNumber);
                return false;
            }

            _logger.LogInformation("Persisting conversation turn {ConversationId}:{Turn}",
                turnWithHash.ConversationId, turnWithHash.TurnNumber);

            var result = await _storageProvider.WriteConversationTurnAsync(turnWithHash, cancellationToken);

            if (result)
            {
                _logger.LogInformation("Successfully persisted conversation turn {ConversationId}:{Turn}",
                    turnWithHash.ConversationId, turnWithHash.TurnNumber);
            }
            else
            {
                _logger.LogError("Failed to persist conversation turn {ConversationId}:{Turn}",
                    turnWithHash.ConversationId, turnWithHash.TurnNumber);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error persisting conversation turn {ConversationId}:{Turn}",
                turn.ConversationId, turn.TurnNumber);
            return false;
        }
    }

    public async Task<BatchWriteResponse> PersistBatchAsync(IEnumerable<ConversationTurn> turns, CancellationToken cancellationToken = default)
    {
        var turnsList = turns.ToList();
        _logger.LogInformation("Starting batch persist of {Count} conversation turns", turnsList.Count);

        try
        {
            // Add hashes to turns that don't have them
            var turnsWithHashes = turnsList.Select(turn =>
                string.IsNullOrEmpty(turn.Hash)
                    ? turn with { Hash = _hashCalculator.CalculateHash(turn) }
                    : turn
            ).ToList();

            // Validate hashes
            var invalidTurns = turnsWithHashes.Where(turn => !_hashCalculator.ValidateHash(turn)).ToList();
            if (invalidTurns.Any())
            {
                var errors = invalidTurns.Select(t => $"Invalid hash for conversation {t.ConversationId}, turn {t.TurnNumber}").ToList();
                _logger.LogWarning("Hash validation failed for {Count} turns", invalidTurns.Count);

                return new BatchWriteResponse
                {
                    Success = false,
                    ProcessedCount = 0,
                    Errors = errors,
                    Message = "Hash validation failed for some turns"
                };
            }

            var result = await _storageProvider.WriteBatchAsync(turnsWithHashes, cancellationToken);

            _logger.LogInformation("Batch persist completed: {Success}, processed {ProcessedCount}/{TotalCount}",
                result.Success, result.ProcessedCount, turnsList.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during batch persist of {Count} turns", turnsList.Count);

            return new BatchWriteResponse
            {
                Success = false,
                ProcessedCount = 0,
                Errors = new[] { ex.Message },
                Message = "Batch persist failed due to exception"
            };
        }
    }
}