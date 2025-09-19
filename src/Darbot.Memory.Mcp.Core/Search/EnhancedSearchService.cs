using System.Diagnostics;
using System.Text.RegularExpressions;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Microsoft.Extensions.Logging;

namespace Darbot.Memory.Mcp.Core.Search;

/// <summary>
/// Enhanced search service with AI-native capabilities and relevance scoring
/// </summary>
public class EnhancedSearchService : IEnhancedSearchService
{
    private readonly IStorageProvider _storageProvider;
    private readonly IQueryParser _queryParser;
    private readonly IRelevanceScorer _relevanceScorer;
    private readonly ISearchIndexer _searchIndexer;
    private readonly ILogger<EnhancedSearchService> _logger;

    public EnhancedSearchService(
        IStorageProvider storageProvider,
        IQueryParser queryParser,
        IRelevanceScorer relevanceScorer,
        ISearchIndexer searchIndexer,
        ILogger<EnhancedSearchService> logger)
    {
        _storageProvider = storageProvider;
        _queryParser = queryParser;
        _relevanceScorer = relevanceScorer;
        _searchIndexer = searchIndexer;
        _logger = logger;
    }

    public async Task<EnhancedSearchResponse> SearchAsync(EnhancedSearchRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Enhanced search started for query: {Query}, mode: {Mode}",
                request.Query, request.Mode);

            // Parse the query using AI-native query understanding
            var parsedQuery = await _queryParser.ParseQueryAsync(request.Query, request.Context, cancellationToken);
            _logger.LogDebug("Query parsed: {ParsedQuery}", parsedQuery);

            // Convert to traditional search request for storage provider
            var traditionalRequest = ConvertToTraditionalRequest(request, parsedQuery);

            // Execute the search using existing storage provider
            var searchResponse = await _storageProvider.SearchConversationsAsync(traditionalRequest, cancellationToken);

            // Score and rank the results
            var scoredResults = await ScoreAndRankResults(searchResponse.Results, parsedQuery, request, cancellationToken);

            // Generate suggestions if requested
            var suggestions = request.IncludeSuggestions
                ? await GenerateSuggestions(request.Query, parsedQuery, cancellationToken)
                : Array.Empty<SearchSuggestion>();

            stopwatch.Stop();

            var response = new EnhancedSearchResponse
            {
                Results = scoredResults,
                TotalCount = searchResponse.TotalCount,
                HasMore = searchResponse.HasMore,
                Skip = request.Skip,
                Take = request.Take,
                SearchTime = stopwatch.Elapsed,
                Suggestions = suggestions,
                QueryInterpretation = parsedQuery.Interpretation,
                Metadata = new Dictionary<string, object>
                {
                    ["query_complexity"] = parsedQuery.Complexity,
                    ["search_mode"] = request.Mode.ToString(),
                    ["fields_searched"] = request.Fields.Any() ? request.Fields : new[] { SearchField.All }
                }
            };

            _logger.LogInformation("Enhanced search completed: {ResultCount} results in {SearchTime}ms",
                scoredResults.Count, stopwatch.ElapsedMilliseconds);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Enhanced search failed for query: {Query}", request.Query);

            // Fallback to basic search
            return await FallbackToBasicSearch(request, cancellationToken);
        }
    }

    public async Task<SearchSuggestionsResponse> GetSuggestionsAsync(string query, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogDebug("Generating suggestions for query: {Query}", query);

            var suggestions = new List<SearchSuggestion>();

            // Query expansion suggestions
            suggestions.AddRange(await GenerateQueryExpansions(query, cancellationToken));

            // Typo correction suggestions
            suggestions.AddRange(await GenerateTypoCorrections(query, cancellationToken));

            // Related topic suggestions
            suggestions.AddRange(await GenerateRelatedTopics(query, cancellationToken));

            // Sort by confidence
            suggestions = suggestions.OrderByDescending(s => s.Confidence).Take(10).ToList();

            stopwatch.Stop();

            return new SearchSuggestionsResponse
            {
                Suggestions = suggestions,
                ResponseTime = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate suggestions for query: {Query}", query);
            return new SearchSuggestionsResponse
            {
                Suggestions = Array.Empty<SearchSuggestion>(),
                ResponseTime = stopwatch.Elapsed
            };
        }
    }

    public async Task RecordSearchInteractionAsync(SearchInteraction interaction, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Recording search interaction: {Type} for query: {Query}",
                interaction.Type, interaction.Query);

            // Store interaction for learning (this would typically go to a separate analytics store)
            await _searchIndexer.RecordInteractionAsync(interaction, cancellationToken);

            _logger.LogDebug("Search interaction recorded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record search interaction for query: {Query}", interaction.Query);
        }
    }

    public async Task<RelatedConversationsResponse> GetRelatedConversationsAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogDebug("Finding related conversations for: {ConversationId}", conversationId);

            // Get the target conversation
            var targetConversation = await _storageProvider.GetConversationAsync(conversationId, cancellationToken);
            if (!targetConversation.Any())
            {
                return new RelatedConversationsResponse
                {
                    Conversations = Array.Empty<RelatedConversation>(),
                    SearchTime = stopwatch.Elapsed
                };
            }

            // Find similar conversations using various similarity metrics
            var relatedConversations = await FindSimilarConversations(targetConversation, cancellationToken);

            stopwatch.Stop();

            return new RelatedConversationsResponse
            {
                Conversations = relatedConversations,
                SearchTime = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find related conversations for: {ConversationId}", conversationId);
            return new RelatedConversationsResponse
            {
                Conversations = Array.Empty<RelatedConversation>(),
                SearchTime = stopwatch.Elapsed
            };
        }
    }

    private ConversationSearchRequest ConvertToTraditionalRequest(EnhancedSearchRequest request, ParsedQuery parsedQuery)
    {
        return new ConversationSearchRequest
        {
            ConversationId = request.ConversationId,
            SearchText = parsedQuery.ProcessedQuery,
            Model = request.Model,
            FromDate = request.FromDate,
            ToDate = request.ToDate,
            ToolsUsed = request.ToolsUsed,
            Skip = request.Skip,
            Take = Math.Max(request.Take, 100), // Get more results for better scoring
            SortBy = "timestamp",
            SortDescending = true
        };
    }

    private async Task<IReadOnlyList<ScoredConversationTurn>> ScoreAndRankResults(
        IReadOnlyList<ConversationTurn> results,
        ParsedQuery parsedQuery,
        EnhancedSearchRequest request,
        CancellationToken cancellationToken)
    {
        var scoredResults = new List<ScoredConversationTurn>();

        foreach (var result in results)
        {
            var score = await _relevanceScorer.CalculateRelevanceAsync(result, parsedQuery, cancellationToken);
            var highlights = GenerateHighlights(result, parsedQuery);

            scoredResults.Add(new ScoredConversationTurn
            {
                Turn = result,
                RelevanceScore = score.Score,
                Highlights = highlights,
                ScoreExplanation = score.Explanation
            });
        }

        // Sort by relevance score and apply pagination
        return scoredResults
            .OrderByDescending(r => r.RelevanceScore)
            .Skip(request.Skip)
            .Take(request.Take)
            .ToList();
    }

    private IReadOnlyList<SearchHighlight> GenerateHighlights(ConversationTurn turn, ParsedQuery parsedQuery)
    {
        var highlights = new List<SearchHighlight>();

        // Highlight matches in prompt
        highlights.AddRange(FindHighlights("Prompt", turn.Prompt, parsedQuery.Terms));

        // Highlight matches in response
        highlights.AddRange(FindHighlights("Response", turn.Response, parsedQuery.Terms));

        // Highlight matches in tools
        foreach (var tool in turn.ToolsUsed)
        {
            highlights.AddRange(FindHighlights("Tools", tool, parsedQuery.Terms));
        }

        return highlights;
    }

    private IEnumerable<SearchHighlight> FindHighlights(string field, string text, IReadOnlyList<string> terms)
    {
        var highlights = new List<SearchHighlight>();

        foreach (var term in terms)
        {
            var regex = new Regex(Regex.Escape(term), RegexOptions.IgnoreCase);
            var matches = regex.Matches(text);

            foreach (Match match in matches)
            {
                var contextStart = Math.Max(0, match.Index - 50);
                var contextEnd = Math.Min(text.Length, match.Index + match.Length + 50);
                var context = text.Substring(contextStart, contextEnd - contextStart);

                var highlightedContext = regex.Replace(context, "<mark>$&</mark>");

                highlights.Add(new SearchHighlight
                {
                    Field = field,
                    OriginalText = context,
                    HighlightedText = highlightedContext,
                    StartIndex = match.Index,
                    Length = match.Length
                });
            }
        }

        return highlights;
    }

    private async Task<IReadOnlyList<SearchSuggestion>> GenerateSuggestions(
        string originalQuery,
        ParsedQuery parsedQuery,
        CancellationToken cancellationToken)
    {
        var suggestions = new List<SearchSuggestion>();

        // Add query expansion suggestions
        suggestions.AddRange(await GenerateQueryExpansions(originalQuery, cancellationToken));

        // Add related search suggestions based on parsed query
        if (parsedQuery.Intent.HasValue)
        {
            suggestions.AddRange(await GenerateIntentBasedSuggestions(parsedQuery.Intent.Value, cancellationToken));
        }

        return suggestions.Take(5).ToList();
    }

    private async Task<IEnumerable<SearchSuggestion>> GenerateQueryExpansions(string query, CancellationToken cancellationToken)
    {
        var expansions = new List<SearchSuggestion>();

        // Simple query expansion based on common patterns
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 1)
        {
            // Single word - suggest adding context
            expansions.Add(new SearchSuggestion
            {
                Text = $"{query} error",
                Confidence = 0.7,
                Type = "query_expansion",
                Description = "Search for errors related to this term"
            });

            expansions.Add(new SearchSuggestion
            {
                Text = $"how to {query}",
                Confidence = 0.6,
                Type = "query_expansion",
                Description = "Search for how-to discussions"
            });
        }

        return expansions;
    }

    private async Task<IEnumerable<SearchSuggestion>> GenerateTypoCorrections(string query, CancellationToken cancellationToken)
    {
        // Simple typo correction suggestions (in a real implementation, you'd use a proper spell checker)
        var corrections = new List<SearchSuggestion>();

        var commonTypos = new Dictionary<string, string>
        {
            ["erro"] = "error",
            ["problm"] = "problem",
            ["isue"] = "issue",
            ["debuging"] = "debugging",
            ["configration"] = "configuration"
        };

        foreach (var typo in commonTypos)
        {
            if (query.Contains(typo.Key, StringComparison.OrdinalIgnoreCase))
            {
                var corrected = query.Replace(typo.Key, typo.Value, StringComparison.OrdinalIgnoreCase);
                corrections.Add(new SearchSuggestion
                {
                    Text = corrected,
                    Confidence = 0.8,
                    Type = "typo_correction",
                    Description = $"Did you mean '{corrected}'?"
                });
            }
        }

        return corrections;
    }

    private async Task<IEnumerable<SearchSuggestion>> GenerateRelatedTopics(string query, CancellationToken cancellationToken)
    {
        var topics = new List<SearchSuggestion>();

        // Generate related topics based on common programming/development themes
        var topicMappings = new Dictionary<string, string[]>
        {
            ["error"] = new[] { "exception", "bug", "debugging", "troubleshooting" },
            ["api"] = new[] { "rest", "endpoint", "http", "json", "request" },
            ["database"] = new[] { "sql", "query", "connection", "schema", "migration" },
            ["authentication"] = new[] { "login", "oauth", "token", "security", "authorization" }
        };

        foreach (var mapping in topicMappings)
        {
            if (query.Contains(mapping.Key, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var relatedTerm in mapping.Value)
                {
                    topics.Add(new SearchSuggestion
                    {
                        Text = relatedTerm,
                        Confidence = 0.6,
                        Type = "related_topic",
                        Description = $"Related to {mapping.Key}"
                    });
                }
            }
        }

        return topics;
    }

    private async Task<IEnumerable<SearchSuggestion>> GenerateIntentBasedSuggestions(SearchIntent intent, CancellationToken cancellationToken)
    {
        var suggestions = new List<SearchSuggestion>();

        switch (intent)
        {
            case SearchIntent.Troubleshooting:
                suggestions.Add(new SearchSuggestion
                {
                    Text = "error resolution",
                    Confidence = 0.8,
                    Type = "intent_based",
                    Description = "Find error resolution discussions"
                });
                break;

            case SearchIntent.HowTo:
                suggestions.Add(new SearchSuggestion
                {
                    Text = "step by step guide",
                    Confidence = 0.8,
                    Type = "intent_based",
                    Description = "Find detailed guides"
                });
                break;

            case SearchIntent.Comparison:
                suggestions.Add(new SearchSuggestion
                {
                    Text = "pros and cons",
                    Confidence = 0.7,
                    Type = "intent_based",
                    Description = "Find comparison discussions"
                });
                break;
        }

        return suggestions;
    }

    private async Task<IReadOnlyList<RelatedConversation>> FindSimilarConversations(
        IReadOnlyList<ConversationTurn> targetConversation,
        CancellationToken cancellationToken)
    {
        var relatedConversations = new List<RelatedConversation>();

        // Get all conversations for similarity comparison
        var allConversationsRequest = new ConversationListRequest
        {
            Take = 1000 // Limit for performance
        };

        var allConversations = await _storageProvider.ListConversationsAsync(allConversationsRequest, cancellationToken);

        // Calculate similarity for each conversation
        foreach (var conversation in allConversations.Conversations)
        {
            if (conversation.ConversationId == targetConversation.First().ConversationId)
                continue; // Skip self

            var similarity = await CalculateConversationSimilarity(targetConversation, conversation, cancellationToken);

            if (similarity.Score > 0.3) // Threshold for relevance
            {
                relatedConversations.Add(new RelatedConversation
                {
                    Summary = conversation,
                    SimilarityScore = similarity.Score,
                    CommonTopics = similarity.CommonTopics,
                    CommonTools = similarity.CommonTools,
                    Explanation = similarity.Explanation
                });
            }
        }

        return relatedConversations
            .OrderByDescending(c => c.SimilarityScore)
            .Take(10)
            .ToList();
    }

    private async Task<ConversationSimilarity> CalculateConversationSimilarity(
        IReadOnlyList<ConversationTurn> targetConversation,
        ConversationSummary candidate,
        CancellationToken cancellationToken)
    {
        var similarity = new ConversationSimilarity();

        // Tool overlap similarity
        var targetTools = targetConversation.SelectMany(t => t.ToolsUsed).Distinct().ToList();
        var candidateTools = candidate.ToolsUsed.ToList();
        var commonTools = targetTools.Intersect(candidateTools).ToList();

        if (targetTools.Any() && candidateTools.Any())
        {
            var toolSimilarity = (double)commonTools.Count / Math.Max(targetTools.Count, candidateTools.Count);
            similarity.Score += toolSimilarity * 0.4; // 40% weight for tool similarity
        }

        // Model similarity
        var targetModels = targetConversation.Select(t => t.Model).Distinct().ToList();
        var candidateModels = candidate.ModelsUsed.ToList();
        var commonModels = targetModels.Intersect(candidateModels).ToList();

        if (targetModels.Any() && candidateModels.Any())
        {
            var modelSimilarity = (double)commonModels.Count / Math.Max(targetModels.Count, candidateModels.Count);
            similarity.Score += modelSimilarity * 0.2; // 20% weight for model similarity
        }

        // Time proximity (conversations closer in time are more likely to be related)
        var targetTime = targetConversation.Max(t => t.UtcTimestamp);
        var candidateTime = candidate.LastTurnTimestamp;
        var timeDiff = Math.Abs((targetTime - candidateTime).TotalDays);
        var timeSimilarity = Math.Max(0, 1 - (timeDiff / 30)); // Decay over 30 days
        similarity.Score += timeSimilarity * 0.1; // 10% weight for time proximity

        // Text similarity would require more advanced NLP techniques
        // For now, we'll use a simple keyword overlap approach
        var targetKeywords = ExtractKeywords(string.Join(" ", targetConversation.Select(t => t.Prompt + " " + t.Response)));
        var candidateKeywords = ExtractKeywords(candidate.LastPrompt ?? "");
        var commonKeywords = targetKeywords.Intersect(candidateKeywords, StringComparer.OrdinalIgnoreCase).ToList();

        if (targetKeywords.Any() && candidateKeywords.Any())
        {
            var keywordSimilarity = (double)commonKeywords.Count / Math.Max(targetKeywords.Count, candidateKeywords.Count);
            similarity.Score += keywordSimilarity * 0.3; // 30% weight for keyword similarity
        }

        similarity.CommonTools = commonTools;
        similarity.CommonTopics = commonKeywords;
        similarity.Explanation = GenerateSimilarityExplanation(similarity);

        return similarity;
    }

    private IReadOnlyList<string> ExtractKeywords(string text)
    {
        // Simple keyword extraction (in practice, you'd use more sophisticated NLP)
        var words = text.Split(new[] { ' ', '\n', '\t', '.', ',', ';', ':', '!', '?' },
            StringSplitOptions.RemoveEmptyEntries);

        var stopWords = new HashSet<string> { "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with", "by", "is", "are", "was", "were", "be", "been", "have", "has", "had", "do", "does", "did", "will", "would", "could", "should", "can", "may", "might", "must", "this", "that", "these", "those", "i", "you", "he", "she", "it", "we", "they", "me", "him", "her", "us", "them" };

        return words
            .Where(w => w.Length > 3 && !stopWords.Contains(w.ToLowerInvariant()))
            .Take(20)
            .ToList();
    }

    private string GenerateSimilarityExplanation(ConversationSimilarity similarity)
    {
        var explanations = new List<string>();

        if (similarity.CommonTools.Any())
        {
            explanations.Add($"Uses similar tools: {string.Join(", ", similarity.CommonTools)}");
        }

        if (similarity.CommonTopics.Any())
        {
            explanations.Add($"Discusses similar topics: {string.Join(", ", similarity.CommonTopics.Take(3))}");
        }

        return string.Join("; ", explanations);
    }

    private async Task<EnhancedSearchResponse> FallbackToBasicSearch(EnhancedSearchRequest request, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Falling back to basic search for query: {Query}", request.Query);

        try
        {
            var basicRequest = new ConversationSearchRequest
            {
                SearchText = request.Query,
                ConversationId = request.ConversationId,
                Model = request.Model,
                FromDate = request.FromDate,
                ToDate = request.ToDate,
                ToolsUsed = request.ToolsUsed,
                Skip = request.Skip,
                Take = request.Take
            };

            var basicResponse = await _storageProvider.SearchConversationsAsync(basicRequest, cancellationToken);

            // Convert to enhanced response format with basic scoring
            var scoredResults = basicResponse.Results.Select((turn, index) => new ScoredConversationTurn
            {
                Turn = turn,
                RelevanceScore = 1.0 - (index * 0.01), // Simple rank-based scoring
                Highlights = Array.Empty<SearchHighlight>(),
                ScoreExplanation = "Basic text match"
            }).ToList();

            return new EnhancedSearchResponse
            {
                Results = scoredResults,
                TotalCount = basicResponse.TotalCount,
                HasMore = basicResponse.HasMore,
                Skip = request.Skip,
                Take = request.Take,
                SearchTime = TimeSpan.FromMilliseconds(100),
                QueryInterpretation = "Fallback to basic search"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Basic search fallback also failed");

            return new EnhancedSearchResponse
            {
                Results = Array.Empty<ScoredConversationTurn>(),
                TotalCount = 0,
                HasMore = false,
                Skip = request.Skip,
                Take = request.Take,
                SearchTime = TimeSpan.FromMilliseconds(0),
                QueryInterpretation = "Search failed"
            };
        }
    }
}

// Helper models for internal use
public record ConversationSimilarity
{
    public double Score { get; set; }
    public IReadOnlyList<string> CommonTools { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> CommonTopics { get; set; } = Array.Empty<string>();
    public string Explanation { get; set; } = "";
}

public record ParsedQuery
{
    public required string OriginalQuery { get; init; }
    public required string ProcessedQuery { get; init; }
    public required IReadOnlyList<string> Terms { get; init; }
    public SearchIntent? Intent { get; init; }
    public string? Interpretation { get; init; }
    public double Complexity { get; init; }
}

public record RelevanceScore
{
    public required double Score { get; init; }
    public string? Explanation { get; init; }
}

public enum SearchIntent
{
    General,
    Troubleshooting,
    HowTo,
    Comparison,
    Definition,
    Example
}