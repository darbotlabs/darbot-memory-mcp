using Darbot.Memory.Mcp.Core.Models;

namespace Darbot.Memory.Mcp.Core.Search;

/// <summary>
/// AI-native query parser for understanding natural language search queries
/// </summary>
public interface IQueryParser
{
    /// <summary>
    /// Parse a natural language query into structured search terms
    /// </summary>
    Task<ParsedQuery> ParseQueryAsync(string query, Dictionary<string, object> context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Relevance scorer for ranking search results
/// </summary>
public interface IRelevanceScorer
{
    /// <summary>
    /// Calculate relevance score for a conversation turn against parsed query
    /// </summary>
    Task<RelevanceScore> CalculateRelevanceAsync(ConversationTurn turn, ParsedQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Search indexer for managing search indices and analytics
/// </summary>
public interface ISearchIndexer
{
    /// <summary>
    /// Index a conversation turn for search
    /// </summary>
    Task IndexConversationAsync(ConversationTurn turn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Record search interaction for learning
    /// </summary>
    Task RecordInteractionAsync(SearchInteraction interaction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Build or rebuild search indices
    /// </summary>
    Task RebuildIndicesAsync(CancellationToken cancellationToken = default);
}