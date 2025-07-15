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

    public async Task<ConversationSearchResponse> SearchConversationsAsync(ConversationSearchRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Searching conversations with criteria: {Criteria}", 
                System.Text.Json.JsonSerializer.Serialize(request));

            var result = await _storageProvider.SearchConversationsAsync(request, cancellationToken);

            _logger.LogInformation("Search completed: found {Count} results out of {Total} total",
                result.Results.Count, result.TotalCount);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching conversations");
            return new ConversationSearchResponse
            {
                Results = Array.Empty<ConversationTurn>(),
                TotalCount = 0,
                HasMore = false,
                Skip = request.Skip,
                Take = request.Take
            };
        }
    }

    public async Task<ConversationListResponse> ListConversationsAsync(ConversationListRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Listing conversations with skip: {Skip}, take: {Take}", request.Skip, request.Take);

            var result = await _storageProvider.ListConversationsAsync(request, cancellationToken);

            _logger.LogInformation("List completed: returned {Count} conversations out of {Total} total",
                result.Conversations.Count, result.TotalCount);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing conversations");
            return new ConversationListResponse
            {
                Conversations = Array.Empty<ConversationSummary>(),
                TotalCount = 0,
                HasMore = false,
                Skip = request.Skip,
                Take = request.Take
            };
        }
    }

    public async Task<ConversationTurn?> GetConversationTurnAsync(string conversationId, int turnNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Retrieving conversation turn {ConversationId}:{TurnNumber}", conversationId, turnNumber);

            var result = await _storageProvider.GetConversationTurnAsync(conversationId, turnNumber, cancellationToken);

            if (result != null)
            {
                _logger.LogInformation("Successfully retrieved conversation turn {ConversationId}:{TurnNumber}", conversationId, turnNumber);
            }
            else
            {
                _logger.LogWarning("Conversation turn {ConversationId}:{TurnNumber} not found", conversationId, turnNumber);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving conversation turn {ConversationId}:{TurnNumber}", conversationId, turnNumber);
            return null;
        }
    }

    public async Task<IReadOnlyList<ConversationTurn>> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Retrieving full conversation {ConversationId}", conversationId);

            var result = await _storageProvider.GetConversationAsync(conversationId, cancellationToken);

            _logger.LogInformation("Successfully retrieved conversation {ConversationId} with {TurnCount} turns", 
                conversationId, result.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving conversation {ConversationId}", conversationId);
            return Array.Empty<ConversationTurn>();
        }
    }
}