using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Darbot.Memory.Mcp.Core.Search;

/// <summary>
/// AI-native context manager implementation for tracking conversation patterns
/// </summary>
public class ConversationContextManager : IConversationContextManager
{
    private readonly ILogger<ConversationContextManager> _logger;
    private readonly ConcurrentDictionary<string, ConversationContext> _contexts;
    private readonly TimeSpan _contextRetention = TimeSpan.FromDays(30);
    private readonly int _maxPatterns = 1000;

    public ConversationContextManager(ILogger<ConversationContextManager> logger)
    {
        _logger = logger;
        _contexts = new ConcurrentDictionary<string, ConversationContext>();
    }

    public ConversationContext GetOrCreateContext(string userId)
    {
        return _contexts.GetOrAdd(userId, CreateNewContext);
    }

    public async Task UpdateSearchPatternAsync(string userId, SearchPattern pattern, CancellationToken cancellationToken = default)
    {
        try
        {
            var context = GetOrCreateContext(userId);
            var updatedPatterns = context.SearchPatterns.ToList();
            updatedPatterns.Add(pattern);

            // Keep only recent patterns
            if (updatedPatterns.Count > _maxPatterns)
            {
                updatedPatterns = updatedPatterns
                    .OrderByDescending(p => p.Timestamp)
                    .Take(_maxPatterns)
                    .ToList();
            }

            var updatedContext = context with
            {
                SearchPatterns = updatedPatterns,
                LastActivity = DateTime.UtcNow
            };

            _contexts.TryUpdate(userId, updatedContext, context);

            _logger.LogDebug("Updated search pattern for user {UserId}: {Query}", userId, pattern.Query);

            // Update topic interests based on the search
            await UpdateTopicInterestsFromSearch(userId, pattern, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update search pattern for user {UserId}", userId);
        }
    }

    public async Task RecordConversationInteractionAsync(string userId, ConversationInteraction interaction, CancellationToken cancellationToken = default)
    {
        try
        {
            var context = GetOrCreateContext(userId);

            // Create conversation pattern from interaction
            var conversationPattern = new ConversationPattern
            {
                ConversationId = interaction.ConversationId,
                AccessTime = interaction.Timestamp,
                ViewDuration = interaction.Duration,
                TopicsDiscussed = ExtractTopicsFromInteraction(interaction),
                ToolsUsed = ExtractToolsFromInteraction(interaction),
                Model = ExtractModelFromInteraction(interaction),
                InteractionType = interaction.Type,
                SatisfactionScore = interaction.SatisfactionRating
            };

            var updatedPatterns = context.ConversationPatterns.ToList();
            updatedPatterns.Add(conversationPattern);

            // Keep only recent patterns
            if (updatedPatterns.Count > _maxPatterns)
            {
                updatedPatterns = updatedPatterns
                    .OrderByDescending(p => p.AccessTime)
                    .Take(_maxPatterns)
                    .ToList();
            }

            var updatedContext = context with
            {
                ConversationPatterns = updatedPatterns,
                LastActivity = DateTime.UtcNow
            };

            _contexts.TryUpdate(userId, updatedContext, context);

            _logger.LogDebug("Recorded conversation interaction for user {UserId}: {ConversationId}",
                userId, interaction.ConversationId);

            // Update preferences based on interaction
            await UpdatePreferencesFromInteraction(userId, interaction, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record conversation interaction for user {UserId}", userId);
        }
    }

    public Task<IReadOnlyList<PersonalizedSuggestion>> GetPersonalizedSuggestionsAsync(string userId, string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var context = GetOrCreateContext(userId);
            var suggestions = new List<PersonalizedSuggestion>();

            // Suggestions based on search history
            suggestions.AddRange(GenerateHistoryBasedSuggestions(context, query));

            // Suggestions based on topic interests
            suggestions.AddRange(GenerateTopicBasedSuggestions(context, query));

            // Suggestions based on tool usage patterns
            suggestions.AddRange(GenerateToolBasedSuggestions(context, query));

            // Suggestions based on model preferences
            suggestions.AddRange(GenerateModelBasedSuggestions(context, query));

            // Sort by confidence and return top suggestions
            var topSuggestions = suggestions
                .OrderByDescending(s => s.Confidence)
                .Take(10)
                .ToList();

            _logger.LogDebug("Generated {Count} personalized suggestions for user {UserId}",
                topSuggestions.Count, userId);

            return Task.FromResult<IReadOnlyList<PersonalizedSuggestion>>(topSuggestions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate personalized suggestions for user {UserId}", userId);
            return Task.FromResult<IReadOnlyList<PersonalizedSuggestion>>(Array.Empty<PersonalizedSuggestion>());
        }
    }

    public Task<ConversationAnalytics> AnalyzeUserPatternsAsync(string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var context = GetOrCreateContext(userId);
            var analysisPeriod = TimeSpan.FromDays(30);
            var cutoffDate = DateTime.UtcNow - analysisPeriod;

            // Filter recent data
            var recentSearches = context.SearchPatterns.Where(p => p.Timestamp >= cutoffDate).ToList();
            var recentConversations = context.ConversationPatterns.Where(p => p.AccessTime >= cutoffDate).ToList();

            // Calculate metrics
            var averageSessionDuration = recentConversations.Any()
                ? recentConversations.Average(c => c.ViewDuration.TotalMinutes)
                : 0;

            var searchIntentDistribution = recentSearches
                .GroupBy(s => s.Intent)
                .ToDictionary(g => g.Key, g => g.Count());

            var behaviorMetrics = new Dictionary<string, double>
            {
                ["search_frequency"] = recentSearches.Count / Math.Max(analysisPeriod.TotalDays, 1),
                ["conversation_frequency"] = recentConversations.Count / Math.Max(analysisPeriod.TotalDays, 1),
                ["search_success_rate"] = recentSearches.Any() ? recentSearches.Average(s => s.SuccessScore) : 0,
                ["satisfaction_rate"] = recentConversations.Any() ? recentConversations.Average(c => c.SatisfactionScore) : 0,
                ["query_refinement_rate"] = recentSearches.Count(s => !string.IsNullOrEmpty(s.RefinedQuery)) / Math.Max(recentSearches.Count, 1.0)
            };

            var analytics = new ConversationAnalytics
            {
                UserId = userId,
                AnalysisPeriod = analysisPeriod,
                TotalConversations = recentConversations.Count,
                TotalSearches = recentSearches.Count,
                AverageSessionDuration = averageSessionDuration,
                TopTopics = context.TopicInterests.OrderByDescending(t => t.InterestScore).Take(10).ToList(),
                PreferredModels = context.ModelPreferences.OrderByDescending(m => m.Value).Take(5).Select(m => m.Key).ToList(),
                FrequentTools = context.ToolUsagePatterns.OrderByDescending(t => t.Value).Take(10).Select(t => t.Key).ToList(),
                SearchIntentDistribution = searchIntentDistribution,
                BehaviorMetrics = behaviorMetrics
            };

            _logger.LogInformation("Generated analytics for user {UserId}: {TotalConversations} conversations, {TotalSearches} searches",
                userId, analytics.TotalConversations, analytics.TotalSearches);

            return Task.FromResult(analytics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze user patterns for user {UserId}", userId);
            return Task.FromException<ConversationAnalytics>(ex);
        }
    }

    public Task CleanupOldContextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cutoffTime = DateTime.UtcNow - _contextRetention;
            var usersToRemove = new List<string>();

            foreach (var kvp in _contexts)
            {
                if (kvp.Value.LastActivity < cutoffTime)
                {
                    usersToRemove.Add(kvp.Key);
                }
            }

            foreach (var userId in usersToRemove)
            {
                _contexts.TryRemove(userId, out _);
            }

            _logger.LogInformation("Cleaned up {Count} old contexts", usersToRemove.Count);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup old contexts");
            return Task.FromException(ex);
        }
    }

    private ConversationContext CreateNewContext(string userId)
    {
        return new ConversationContext
        {
            UserId = userId,
            CreatedAt = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow,
            SearchPatterns = Array.Empty<SearchPattern>(),
            ConversationPatterns = Array.Empty<ConversationPattern>(),
            TopicInterests = Array.Empty<TopicInterest>(),
            ModelPreferences = new Dictionary<string, double>(),
            ToolUsagePatterns = new Dictionary<string, double>()
        };
    }

    private Task UpdateTopicInterestsFromSearch(string userId, SearchPattern pattern, CancellationToken cancellationToken)
    {
        var context = GetOrCreateContext(userId);
        var topics = ExtractTopicsFromQuery(pattern.Query);
        var updatedInterests = context.TopicInterests.ToList();

        foreach (var topic in topics)
        {
            var existingInterest = updatedInterests.FirstOrDefault(i =>
                string.Equals(i.Topic, topic, StringComparison.OrdinalIgnoreCase));

            if (existingInterest != null)
            {
                // Update existing interest
                var index = updatedInterests.IndexOf(existingInterest);
                updatedInterests[index] = existingInterest with
                {
                    InterestScore = Math.Min(existingInterest.InterestScore + (pattern.SuccessScore * 0.1), 1.0),
                    InteractionCount = existingInterest.InteractionCount + 1,
                    LastInteraction = pattern.Timestamp,
                    TrendingScore = CalculateTrendingScore(existingInterest, pattern.Timestamp)
                };
            }
            else
            {
                // Add new interest
                updatedInterests.Add(new TopicInterest
                {
                    Topic = topic,
                    InterestScore = pattern.SuccessScore * 0.1,
                    InteractionCount = 1,
                    LastInteraction = pattern.Timestamp,
                    RelatedTerms = ExtractRelatedTerms(pattern.Query, topic),
                    TrendingScore = 1.0
                });
            }
        }

        var updatedContext = context with { TopicInterests = updatedInterests };
        _contexts.TryUpdate(userId, updatedContext, context);
        return Task.CompletedTask;
    }

    private Task UpdatePreferencesFromInteraction(string userId, ConversationInteraction interaction, CancellationToken cancellationToken)
    {
        var context = GetOrCreateContext(userId);
        var model = ExtractModelFromInteraction(interaction);
        var tools = ExtractToolsFromInteraction(interaction);

        // Update model preferences
        var updatedModelPrefs = new Dictionary<string, double>(context.ModelPreferences);
        if (!string.IsNullOrEmpty(model))
        {
            var currentScore = updatedModelPrefs.TryGetValue(model, out var score) ? score : 0;
            updatedModelPrefs[model] = Math.Min(currentScore + (interaction.SatisfactionRating * 0.1), 1.0);
        }

        // Update tool usage patterns
        var updatedToolPatterns = new Dictionary<string, double>(context.ToolUsagePatterns);
        foreach (var tool in tools)
        {
            var currentUsage = updatedToolPatterns.TryGetValue(tool, out var usage) ? usage : 0;
            updatedToolPatterns[tool] = currentUsage + 1;
        }

        var updatedContext = context with
        {
            ModelPreferences = updatedModelPrefs,
            ToolUsagePatterns = updatedToolPatterns
        };

        _contexts.TryUpdate(userId, updatedContext, context);
        return Task.CompletedTask;
    }

    private IReadOnlyList<PersonalizedSuggestion> GenerateHistoryBasedSuggestions(ConversationContext context, string query)
    {
        var suggestions = new List<PersonalizedSuggestion>();
        var recentSearches = context.SearchPatterns
            .Where(p => p.Timestamp >= DateTime.UtcNow.AddDays(-7))
            .OrderByDescending(p => p.SuccessScore)
            .Take(5);

        foreach (var search in recentSearches)
        {
            if (search.SuccessScore > 0.7 && IsQueryRelated(query, search.Query))
            {
                suggestions.Add(new PersonalizedSuggestion
                {
                    Text = search.Query,
                    Confidence = search.SuccessScore * 0.8,
                    Type = "search_history",
                    Reason = "Based on your successful recent searches"
                });
            }
        }

        return suggestions;
    }

    private IReadOnlyList<PersonalizedSuggestion> GenerateTopicBasedSuggestions(ConversationContext context, string query)
    {
        var suggestions = new List<PersonalizedSuggestion>();
        var topTopics = context.TopicInterests
            .OrderByDescending(t => t.InterestScore * t.TrendingScore)
            .Take(5);

        foreach (var topic in topTopics)
        {
            if (IsQueryRelatedToTopic(query, topic.Topic))
            {
                suggestions.Add(new PersonalizedSuggestion
                {
                    Text = $"{query} {topic.Topic}",
                    Confidence = topic.InterestScore * 0.7,
                    Type = "topic_interest",
                    Reason = $"Based on your interest in {topic.Topic}",
                    RelatedTopics = topic.RelatedTerms
                });
            }
        }

        return suggestions;
    }

    private IReadOnlyList<PersonalizedSuggestion> GenerateToolBasedSuggestions(ConversationContext context, string query)
    {
        var suggestions = new List<PersonalizedSuggestion>();
        var frequentTools = context.ToolUsagePatterns
            .OrderByDescending(t => t.Value)
            .Take(3);

        foreach (var tool in frequentTools)
        {
            suggestions.Add(new PersonalizedSuggestion
            {
                Text = $"{query} using {tool.Key}",
                Confidence = Math.Min(tool.Value / 10.0, 0.8),
                Type = "tool_usage",
                Reason = $"You frequently use {tool.Key}"
            });
        }

        return suggestions;
    }

    private IReadOnlyList<PersonalizedSuggestion> GenerateModelBasedSuggestions(ConversationContext context, string query)
    {
        var suggestions = new List<PersonalizedSuggestion>();
        var preferredModel = context.ModelPreferences
            .OrderByDescending(m => m.Value)
            .FirstOrDefault();

        if (preferredModel.Key != null && preferredModel.Value > 0.5)
        {
            suggestions.Add(new PersonalizedSuggestion
            {
                Text = $"{query} with {preferredModel.Key}",
                Confidence = preferredModel.Value * 0.6,
                Type = "model_preference",
                Reason = $"Based on your preference for {preferredModel.Key}"
            });
        }

        return suggestions;
    }

    private IReadOnlyList<string> ExtractTopicsFromQuery(string query)
    {
        // Simple topic extraction (in practice, you'd use more sophisticated NLP)
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var topics = new List<string>();

        // Technology keywords
        var techPatterns = new[] { "api", "database", "authentication", "error", "bug", "performance", "security", "deployment" };
        foreach (var pattern in techPatterns)
        {
            if (query.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                topics.Add(pattern);
            }
        }

        // Extract longer phrases as potential topics
        if (words.Length >= 2)
        {
            for (int i = 0; i < words.Length - 1; i++)
            {
                if (words[i].Length > 3 && words[i + 1].Length > 3)
                {
                    topics.Add($"{words[i]} {words[i + 1]}");
                }
            }
        }

        return topics.Take(5).ToList();
    }

    private IReadOnlyList<string> ExtractRelatedTerms(string query, string topic)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !string.Equals(w, topic, StringComparison.OrdinalIgnoreCase) && w.Length > 3)
            .Take(3)
            .ToList();

        return words;
    }

    private IReadOnlyList<string> ExtractTopicsFromInteraction(ConversationInteraction interaction)
    {
        // Extract topics from metadata or actions
        if (interaction.Metadata.TryGetValue("topics", out var topicsObj) && topicsObj is IEnumerable<string> topics)
        {
            return topics.ToList();
        }

        return Array.Empty<string>();
    }

    private IReadOnlyList<string> ExtractToolsFromInteraction(ConversationInteraction interaction)
    {
        if (interaction.Metadata.TryGetValue("tools", out var toolsObj) && toolsObj is IEnumerable<string> tools)
        {
            return tools.ToList();
        }

        return Array.Empty<string>();
    }

    private string ExtractModelFromInteraction(ConversationInteraction interaction)
    {
        if (interaction.Metadata.TryGetValue("model", out var modelObj) && modelObj is string model)
        {
            return model;
        }

        return "";
    }

    private double CalculateTrendingScore(TopicInterest existingInterest, DateTime currentTime)
    {
        var daysSinceLastInteraction = (currentTime - existingInterest.LastInteraction).TotalDays;
        return Math.Max(0.1, 1.0 - (daysSinceLastInteraction / 30.0)); // Decay over 30 days
    }

    private bool IsQueryRelated(string query1, string query2)
    {
        var words1 = query1.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words2 = query2.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var commonWords = words1.Intersect(words2).Count();
        var totalWords = Math.Max(words1.Length, words2.Length);

        return commonWords > 0 && (double)commonWords / totalWords > 0.3;
    }

    private bool IsQueryRelatedToTopic(string query, string topic)
    {
        return query.Contains(topic, StringComparison.OrdinalIgnoreCase) ||
               topic.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}