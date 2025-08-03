using Darbot.Memory.Mcp.Core.Models;

namespace Darbot.Memory.Mcp.Core.Search;

/// <summary>
/// Enhanced search capabilities with relevance scoring and AI-native features
/// </summary>
public interface IEnhancedSearchService
{
    /// <summary>
    /// Intelligent search with natural language query understanding
    /// </summary>
    Task<EnhancedSearchResponse> SearchAsync(EnhancedSearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get search suggestions based on query and context
    /// </summary>
    Task<SearchSuggestionsResponse> GetSuggestionsAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyze search patterns and improve future searches
    /// </summary>
    Task RecordSearchInteractionAsync(SearchInteraction interaction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get related conversations based on context similarity
    /// </summary>
    Task<RelatedConversationsResponse> GetRelatedConversationsAsync(string conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Enhanced search request with AI-native capabilities
/// </summary>
public record EnhancedSearchRequest
{
    public required string Query { get; init; }
    public string? ConversationId { get; init; }
    public string? Model { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public IReadOnlyList<string> ToolsUsed { get; init; } = Array.Empty<string>();
    public SearchMode Mode { get; init; } = SearchMode.Intelligent;
    public IReadOnlyList<SearchField> Fields { get; init; } = Array.Empty<SearchField>();
    public int Skip { get; init; } = 0;
    public int Take { get; init; } = 50;
    public bool IncludeScore { get; init; } = true;
    public bool IncludeSuggestions { get; init; } = false;
    public string? UserId { get; init; }
    public Dictionary<string, object> Context { get; init; } = new();
}

/// <summary>
/// Enhanced search response with relevance scoring
/// </summary>
public record EnhancedSearchResponse
{
    public required IReadOnlyList<ScoredConversationTurn> Results { get; init; }
    public required int TotalCount { get; init; }
    public required bool HasMore { get; init; }
    public required int Skip { get; init; }
    public required int Take { get; init; }
    public required TimeSpan SearchTime { get; init; }
    public IReadOnlyList<SearchSuggestion> Suggestions { get; init; } = Array.Empty<SearchSuggestion>();
    public string? QueryInterpretation { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Conversation turn with relevance score
/// </summary>
public record ScoredConversationTurn
{
    public required ConversationTurn Turn { get; init; }
    public required double RelevanceScore { get; init; }
    public required IReadOnlyList<SearchHighlight> Highlights { get; init; }
    public string? ScoreExplanation { get; init; }
}

/// <summary>
/// Search highlight for showing matched text
/// </summary>
public record SearchHighlight
{
    public required string Field { get; init; }
    public required string OriginalText { get; init; }
    public required string HighlightedText { get; init; }
    public required int StartIndex { get; init; }
    public required int Length { get; init; }
}

/// <summary>
/// Search suggestions for query expansion
/// </summary>
public record SearchSuggestion
{
    public required string Text { get; init; }
    public required double Confidence { get; init; }
    public required string Type { get; init; } // query_expansion, typo_correction, related_topic
    public string? Description { get; init; }
}

/// <summary>
/// Search suggestions response
/// </summary>
public record SearchSuggestionsResponse
{
    public required IReadOnlyList<SearchSuggestion> Suggestions { get; init; }
    public required TimeSpan ResponseTime { get; init; }
}

/// <summary>
/// User search interaction for learning
/// </summary>
public record SearchInteraction
{
    public required string Query { get; init; }
    public required string UserId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string ResultId { get; init; }
    public required SearchInteractionType Type { get; init; }
    public double? ResultScore { get; init; }
    public int? ResultRank { get; init; }
    public Dictionary<string, object> Context { get; init; } = new();
}

/// <summary>
/// Related conversations based on similarity
/// </summary>
public record RelatedConversationsResponse
{
    public required IReadOnlyList<RelatedConversation> Conversations { get; init; }
    public required TimeSpan SearchTime { get; init; }
}

/// <summary>
/// Related conversation with similarity score
/// </summary>
public record RelatedConversation
{
    public required ConversationSummary Summary { get; init; }
    public required double SimilarityScore { get; init; }
    public required IReadOnlyList<string> CommonTopics { get; init; }
    public required IReadOnlyList<string> CommonTools { get; init; }
    public string? Explanation { get; init; }
}

/// <summary>
/// Search modes for different query types
/// </summary>
public enum SearchMode
{
    Exact,          // Exact phrase matching
    Fuzzy,          // Fuzzy matching with typo tolerance
    Semantic,       // Semantic similarity search
    Intelligent,    // AI-powered query understanding (default)
    Hybrid          // Combination of multiple approaches
}

/// <summary>
/// Fields to search in
/// </summary>
public enum SearchField
{
    All,
    Prompt,
    Response,
    Model,
    Tools,
    ConversationId,
    Metadata
}

/// <summary>
/// Types of search interactions for learning
/// </summary>
public enum SearchInteractionType
{
    Click,          // User clicked on a result
    View,           // User viewed a result
    Copy,           // User copied content from result
    Share,          // User shared a result
    Ignore,         // User ignored/skipped result
    Negative        // User indicated result was not helpful
}