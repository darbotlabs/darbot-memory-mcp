using Darbot.Memory.Mcp.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Darbot.Memory.Mcp.Core.Search;

/// <summary>
/// Advanced relevance scorer using TF-IDF and contextual matching
/// </summary>
public class RelevanceScorer : IRelevanceScorer
{
    private readonly ILogger<RelevanceScorer> _logger;
    private readonly Dictionary<string, double> _termFrequencies;
    private readonly Dictionary<string, int> _documentFrequencies;
    private int _totalDocuments;

    public RelevanceScorer(ILogger<RelevanceScorer> logger)
    {
        _logger = logger;
        _termFrequencies = new Dictionary<string, double>();
        _documentFrequencies = new Dictionary<string, int>();
        _totalDocuments = 0;
    }

    public async Task<RelevanceScore> CalculateRelevanceAsync(ConversationTurn turn, ParsedQuery query, CancellationToken cancellationToken = default)
    {
        var scores = new List<(string component, double score, double weight)>();

        // Calculate relevance for different components
        scores.Add(("prompt", CalculateTextRelevance(turn.Prompt, query), 0.4));
        scores.Add(("response", CalculateTextRelevance(turn.Response, query), 0.3));
        scores.Add(("model", CalculateModelRelevance(turn.Model, query), 0.1));
        scores.Add(("tools", CalculateToolsRelevance(turn.ToolsUsed, query), 0.15));
        scores.Add(("temporal", CalculateTemporalRelevance(turn.UtcTimestamp, query), 0.05));

        // Calculate weighted total score
        var totalScore = scores.Sum(s => s.score * s.weight);

        // Apply intent-based boost
        totalScore = ApplyIntentBoost(totalScore, turn, query);

        // Normalize score to 0-1 range
        totalScore = Math.Min(Math.Max(totalScore, 0), 1);

        var explanation = GenerateScoreExplanation(scores, totalScore, query.Intent);

        _logger.LogDebug("Calculated relevance score {Score} for turn {ConversationId}:{TurnNumber}", 
            totalScore, turn.ConversationId, turn.TurnNumber);

        return new RelevanceScore
        {
            Score = totalScore,
            Explanation = explanation
        };
    }

    private double CalculateTextRelevance(string text, ParsedQuery query)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var textLower = text.ToLowerInvariant();
        var relevanceScore = 0.0;

        // Exact phrase matching (highest weight)
        if (!string.IsNullOrEmpty(query.ProcessedQuery) && 
            textLower.Contains(query.ProcessedQuery.ToLowerInvariant()))
        {
            relevanceScore += 0.8;
        }

        // Individual term matching with TF-IDF-like scoring
        foreach (var term in query.Terms)
        {
            var termLower = term.ToLowerInvariant();
            var termCount = CountOccurrences(textLower, termLower);
            
            if (termCount > 0)
            {
                // Term frequency component
                var tf = (double)termCount / GetWordCount(text);
                
                // Simple IDF approximation (would be better with actual corpus statistics)
                var idf = Math.Log(1000 / Math.Max(GetTermDocumentFrequency(termLower), 1));
                
                var tfidf = tf * idf;
                relevanceScore += Math.Min(tfidf * 0.1, 0.3); // Cap individual term contribution
            }
        }

        // Proximity bonus - terms appearing close together get bonus
        relevanceScore += CalculateProximityBonus(textLower, query.Terms);

        // Length normalization - prevent very long texts from dominating
        var lengthNormalization = Math.Min(1.0, 1000.0 / Math.Max(text.Length, 100));
        relevanceScore *= lengthNormalization;

        return Math.Min(relevanceScore, 1.0);
    }

    private double CalculateModelRelevance(string model, ParsedQuery query)
    {
        if (string.IsNullOrEmpty(model))
            return 0;

        var modelLower = model.ToLowerInvariant();
        
        // Check if any query terms match the model name
        foreach (var term in query.Terms)
        {
            if (modelLower.Contains(term.ToLowerInvariant()))
            {
                return 0.8; // High relevance for model matches
            }
        }

        // Check for model family matches (gpt, claude, etc.)
        var modelFamilies = new Dictionary<string, string[]>
        {
            ["gpt"] = new[] { "openai", "chatgpt", "gpt-3", "gpt-4" },
            ["claude"] = new[] { "anthropic", "claude-1", "claude-2" },
            ["llama"] = new[] { "meta", "llama-2" }
        };

        foreach (var term in query.Terms)
        {
            foreach (var family in modelFamilies)
            {
                if ((term.ToLowerInvariant().Contains(family.Key) && modelLower.Contains(family.Key)) ||
                    family.Value.Any(variant => term.ToLowerInvariant().Contains(variant) && modelLower.Contains(variant)))
                {
                    return 0.6;
                }
            }
        }

        return 0;
    }

    private double CalculateToolsRelevance(IReadOnlyList<string> tools, ParsedQuery query)
    {
        if (!tools.Any())
            return 0;

        var toolsLower = tools.Select(t => t.ToLowerInvariant()).ToList();
        var matchedTools = 0;

        foreach (var term in query.Terms)
        {
            var termLower = term.ToLowerInvariant();
            if (toolsLower.Any(tool => tool.Contains(termLower) || termLower.Contains(tool)))
            {
                matchedTools++;
            }
        }

        return matchedTools > 0 ? Math.Min((double)matchedTools / tools.Count, 1.0) : 0;
    }

    private double CalculateTemporalRelevance(DateTime turnTimestamp, ParsedQuery query)
    {
        // More recent conversations get slight boost
        var daysSinceCreation = (DateTime.UtcNow - turnTimestamp).TotalDays;
        
        // Decay function: newer conversations get higher scores
        return Math.Max(0, 1 - (daysSinceCreation / 365)); // Decay over a year
    }

    private double ApplyIntentBoost(double baseScore, ConversationTurn turn, ParsedQuery query)
    {
        if (!query.Intent.HasValue)
            return baseScore;

        var boost = 1.0;

        switch (query.Intent.Value)
        {
            case SearchIntent.Troubleshooting:
                // Boost conversations that mention errors, problems, or solutions
                if (ContainsErrorKeywords(turn.Prompt) || ContainsErrorKeywords(turn.Response))
                {
                    boost = 1.2;
                }
                break;

            case SearchIntent.HowTo:
                // Boost conversations with step-by-step content or instructional language
                if (ContainsInstructionalKeywords(turn.Response))
                {
                    boost = 1.15;
                }
                break;

            case SearchIntent.Definition:
                // Boost conversations that provide explanations or definitions
                if (ContainsDefinitionKeywords(turn.Response))
                {
                    boost = 1.1;
                }
                break;

            case SearchIntent.Example:
                // Boost conversations that provide examples or code snippets
                if (ContainsExampleKeywords(turn.Response) || ContainsCodeBlocks(turn.Response))
                {
                    boost = 1.15;
                }
                break;
        }

        return Math.Min(baseScore * boost, 1.0);
    }

    private bool ContainsErrorKeywords(string text)
    {
        var errorKeywords = new[] { "error", "exception", "problem", "issue", "bug", "fail", "broken", "fix", "solve", "solution" };
        var textLower = text.ToLowerInvariant();
        return errorKeywords.Any(keyword => textLower.Contains(keyword));
    }

    private bool ContainsInstructionalKeywords(string text)
    {
        var instructionalKeywords = new[] { "step", "first", "then", "next", "finally", "follow", "guide", "tutorial", "instructions" };
        var textLower = text.ToLowerInvariant();
        return instructionalKeywords.Any(keyword => textLower.Contains(keyword));
    }

    private bool ContainsDefinitionKeywords(string text)
    {
        var definitionKeywords = new[] { "is defined as", "means", "refers to", "definition", "explanation", "basically", "essentially" };
        var textLower = text.ToLowerInvariant();
        return definitionKeywords.Any(keyword => textLower.Contains(keyword));
    }

    private bool ContainsExampleKeywords(string text)
    {
        var exampleKeywords = new[] { "example", "for instance", "such as", "like this", "here's how", "sample", "demo" };
        var textLower = text.ToLowerInvariant();
        return exampleKeywords.Any(keyword => textLower.Contains(keyword));
    }

    private bool ContainsCodeBlocks(string text)
    {
        // Check for common code block indicators
        return text.Contains("```") || text.Contains("```") || 
               Regex.IsMatch(text, @"^\s*[\w\s]+\(\s*\)", RegexOptions.Multiline) ||
               text.Contains("function") || text.Contains("class") || text.Contains("def ");
    }

    private double CalculateProximityBonus(string text, IReadOnlyList<string> terms)
    {
        if (terms.Count < 2)
            return 0;

        var bonus = 0.0;
        var words = text.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < terms.Count - 1; i++)
        {
            for (int j = i + 1; j < terms.Count; j++)
            {
                var term1Positions = FindWordPositions(words, terms[i]);
                var term2Positions = FindWordPositions(words, terms[j]);

                foreach (var pos1 in term1Positions)
                {
                    foreach (var pos2 in term2Positions)
                    {
                        var distance = Math.Abs(pos1 - pos2);
                        if (distance <= 5) // Terms within 5 words of each other
                        {
                            bonus += 0.1 / distance; // Closer terms get higher bonus
                        }
                    }
                }
            }
        }

        return Math.Min(bonus, 0.2); // Cap proximity bonus
    }

    private List<int> FindWordPositions(string[] words, string term)
    {
        var positions = new List<int>();
        var termLower = term.ToLowerInvariant();

        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].ToLowerInvariant().Contains(termLower))
            {
                positions.Add(i);
            }
        }

        return positions;
    }

    private int CountOccurrences(string text, string term)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term))
            return 0;

        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            count++;
            index += term.Length;
        }

        return count;
    }

    private int GetWordCount(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return text.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private int GetTermDocumentFrequency(string term)
    {
        // In a real implementation, this would query a term frequency database
        // For now, return a reasonable default
        return _documentFrequencies.TryGetValue(term, out var freq) ? freq : 100;
    }

    private string GenerateScoreExplanation(List<(string component, double score, double weight)> scores, double totalScore, SearchIntent? intent)
    {
        var explanations = new List<string>();

        foreach (var (component, score, weight) in scores.Where(s => s.score > 0.1))
        {
            var contribution = score * weight;
            explanations.Add($"{component}: {score:F2} (weight: {weight:F2}, contribution: {contribution:F2})");
        }

        if (intent.HasValue)
        {
            explanations.Add($"Intent boost applied for {intent.Value}");
        }

        var explanation = $"Total: {totalScore:F2}. " + string.Join("; ", explanations);
        
        return explanation.Length > 200 ? explanation.Substring(0, 197) + "..." : explanation;
    }
}

/// <summary>
/// Basic search indexer implementation
/// </summary>
public class SearchIndexer : ISearchIndexer
{
    private readonly ILogger<SearchIndexer> _logger;
    private readonly Dictionary<string, object> _index;

    public SearchIndexer(ILogger<SearchIndexer> logger)
    {
        _logger = logger;
        _index = new Dictionary<string, object>();
    }

    public async Task IndexConversationAsync(ConversationTurn turn, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Indexing conversation turn {ConversationId}:{TurnNumber}", 
                turn.ConversationId, turn.TurnNumber);

            // In a real implementation, this would add to a search index like Elasticsearch or Azure Search
            var key = $"{turn.ConversationId}:{turn.TurnNumber}";
            _index[key] = new
            {
                turn.ConversationId,
                turn.TurnNumber,
                turn.UtcTimestamp,
                turn.Prompt,
                turn.Response,
                turn.Model,
                turn.ToolsUsed,
                IndexedAt = DateTime.UtcNow
            };

            _logger.LogDebug("Successfully indexed conversation turn {ConversationId}:{TurnNumber}", 
                turn.ConversationId, turn.TurnNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index conversation turn {ConversationId}:{TurnNumber}", 
                turn.ConversationId, turn.TurnNumber);
        }
    }

    public async Task RecordInteractionAsync(SearchInteraction interaction, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Recording search interaction: {Type} for query: {Query}", 
                interaction.Type, interaction.Query);

            // In a real implementation, this would store interaction data for analytics
            var interactionKey = $"interaction:{interaction.UserId}:{interaction.Timestamp:yyyyMMddHHmmss}";
            _index[interactionKey] = interaction;

            _logger.LogDebug("Successfully recorded search interaction");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record search interaction for query: {Query}", interaction.Query);
        }
    }

    public async Task RebuildIndicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting search index rebuild");

            // In a real implementation, this would rebuild the search indices
            _index.Clear();

            _logger.LogInformation("Search index rebuild completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rebuild search indices");
            throw;
        }
    }
}