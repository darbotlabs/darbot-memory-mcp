using Darbot.Memory.Mcp.Core.Models;

namespace Darbot.Memory.Mcp.Core.Search;

/// <summary>
/// AI-native context management for conversation patterns and user behavior
/// Inspired by darbot-browser-mcp's context management
/// </summary>
public interface IConversationContextManager
{
    /// <summary>
    /// Get or create context for a user session
    /// </summary>
    ConversationContext GetOrCreateContext(string userId);

    /// <summary>
    /// Update search patterns and preferences
    /// </summary>
    Task UpdateSearchPatternAsync(string userId, SearchPattern pattern, CancellationToken cancellationToken = default);

    /// <summary>
    /// Record conversation interaction for learning
    /// </summary>
    Task RecordConversationInteractionAsync(string userId, ConversationInteraction interaction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get personalized search suggestions based on user patterns
    /// </summary>
    Task<IReadOnlyList<PersonalizedSuggestion>> GetPersonalizedSuggestionsAsync(string userId, string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyze user conversation patterns
    /// </summary>
    Task<ConversationAnalytics> AnalyzeUserPatternsAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clean up old context data
    /// </summary>
    Task CleanupOldContextAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Context information for a user's conversation and search patterns
/// </summary>
public record ConversationContext
{
    public required string UserId { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime LastActivity { get; init; }
    public required IReadOnlyList<SearchPattern> SearchPatterns { get; init; }
    public required IReadOnlyList<ConversationPattern> ConversationPatterns { get; init; }
    public required IReadOnlyList<TopicInterest> TopicInterests { get; init; }
    public required Dictionary<string, double> ModelPreferences { get; init; }
    public required Dictionary<string, double> ToolUsagePatterns { get; init; }
    public Dictionary<string, object> ExtensionData { get; init; } = new();
}

/// <summary>
/// User's search behavior pattern
/// </summary>
public record SearchPattern
{
    public required string Query { get; init; }
    public required SearchIntent Intent { get; init; }
    public required DateTime Timestamp { get; init; }
    public required IReadOnlyList<string> ClickedResults { get; init; }
    public required double SuccessScore { get; init; }
    public required TimeSpan SearchDuration { get; init; }
    public string? RefinedQuery { get; init; }
    public Dictionary<string, object> Context { get; init; } = new();
}

/// <summary>
/// Pattern in how user interacts with conversations
/// </summary>
public record ConversationPattern
{
    public required string ConversationId { get; init; }
    public required DateTime AccessTime { get; init; }
    public required TimeSpan ViewDuration { get; init; }
    public required IReadOnlyList<string> TopicsDiscussed { get; init; }
    public required IReadOnlyList<string> ToolsUsed { get; init; }
    public required string Model { get; init; }
    public required ConversationInteractionType InteractionType { get; init; }
    public double SatisfactionScore { get; init; }
}

/// <summary>
/// User's interest in specific topics
/// </summary>
public record TopicInterest
{
    public required string Topic { get; init; }
    public required double InterestScore { get; init; }
    public required int InteractionCount { get; init; }
    public required DateTime LastInteraction { get; init; }
    public required IReadOnlyList<string> RelatedTerms { get; init; }
    public double TrendingScore { get; init; }
}

/// <summary>
/// User interaction with a conversation
/// </summary>
public record ConversationInteraction
{
    public required string ConversationId { get; init; }
    public required string UserId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required ConversationInteractionType Type { get; init; }
    public required TimeSpan Duration { get; init; }
    public IReadOnlyList<string> ActionsPerformed { get; init; } = Array.Empty<string>();
    public double SatisfactionRating { get; init; }
    public string? Feedback { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Personalized suggestion based on user patterns
/// </summary>
public record PersonalizedSuggestion
{
    public required string Text { get; init; }
    public required double Confidence { get; init; }
    public required string Type { get; init; }
    public required string Reason { get; init; }
    public IReadOnlyList<string> RelatedTopics { get; init; } = Array.Empty<string>();
    public Dictionary<string, object> Context { get; init; } = new();
}

/// <summary>
/// Analytics about user conversation patterns
/// </summary>
public record ConversationAnalytics
{
    public required string UserId { get; init; }
    public required TimeSpan AnalysisPeriod { get; init; }
    public required int TotalConversations { get; init; }
    public required int TotalSearches { get; init; }
    public required double AverageSessionDuration { get; init; }
    public required IReadOnlyList<TopicInterest> TopTopics { get; init; }
    public required IReadOnlyList<string> PreferredModels { get; init; }
    public required IReadOnlyList<string> FrequentTools { get; init; }
    public required Dictionary<SearchIntent, int> SearchIntentDistribution { get; init; }
    public required Dictionary<string, double> BehaviorMetrics { get; init; }
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Types of conversation interactions
/// </summary>
public enum ConversationInteractionType
{
    View,
    Read,
    Copy,
    Share,
    Reference,
    Continue,
    Rate,
    Bookmark
}