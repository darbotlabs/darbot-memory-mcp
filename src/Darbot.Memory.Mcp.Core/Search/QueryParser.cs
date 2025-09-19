using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Darbot.Memory.Mcp.Core.Search;

/// <summary>
/// AI-native query parser implementation inspired by darbot-browser-mcp intent parsing
/// </summary>
public class QueryParser : IQueryParser
{
    private readonly ILogger<QueryParser> _logger;
    private readonly List<QueryPattern> _patterns;

    public QueryParser(ILogger<QueryParser> logger)
    {
        _logger = logger;
        _patterns = InitializePatterns();
    }

    public async Task<ParsedQuery> ParseQueryAsync(string query, Dictionary<string, object> context, CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query.Trim();
        _logger.LogDebug("Parsing query: {Query}", normalizedQuery);

        // Detect query intent
        var intent = DetectIntent(normalizedQuery);

        // Extract search terms
        var terms = ExtractSearchTerms(normalizedQuery);

        // Process query based on intent
        var processedQuery = ProcessQueryByIntent(normalizedQuery, intent);

        // Calculate complexity
        var complexity = CalculateComplexity(normalizedQuery, terms);

        var parsedQuery = new ParsedQuery
        {
            OriginalQuery = query,
            ProcessedQuery = processedQuery,
            Terms = terms,
            Intent = intent,
            Interpretation = GenerateInterpretation(normalizedQuery, intent, terms),
            Complexity = complexity
        };

        _logger.LogDebug("Query parsed: Intent={Intent}, Terms={Terms}, Complexity={Complexity}",
            intent, string.Join(", ", terms), complexity);

        return parsedQuery;
    }

    private List<QueryPattern> InitializePatterns()
    {
        return new List<QueryPattern>
        {
            new QueryPattern
            {
                Pattern = new Regex(@"(?:how\s+to|how\s+do\s+i|how\s+can\s+i)\s+(.+)", RegexOptions.IgnoreCase),
                Intent = SearchIntent.HowTo,
                Confidence = 0.9
            },
            new QueryPattern
            {
                Pattern = new Regex(@"(?:error|exception|problem|issue|bug|fail|broken)\s*:?\s*(.+)", RegexOptions.IgnoreCase),
                Intent = SearchIntent.Troubleshooting,
                Confidence = 0.85
            },
            new QueryPattern
            {
                Pattern = new Regex(@"(?:what\s+is|define|definition\s+of|explain)\s+(.+)", RegexOptions.IgnoreCase),
                Intent = SearchIntent.Definition,
                Confidence = 0.8
            },
            new QueryPattern
            {
                Pattern = new Regex(@"(?:compare|vs|versus|difference\s+between)\s+(.+)", RegexOptions.IgnoreCase),
                Intent = SearchIntent.Comparison,
                Confidence = 0.85
            },
            new QueryPattern
            {
                Pattern = new Regex(@"(?:example|sample|demo)\s+(?:of\s+)?(.+)", RegexOptions.IgnoreCase),
                Intent = SearchIntent.Example,
                Confidence = 0.8
            }
        };
    }

    private SearchIntent DetectIntent(string query)
    {
        var queryLower = query.ToLowerInvariant();

        foreach (var pattern in _patterns)
        {
            if (pattern.Pattern.IsMatch(query))
            {
                _logger.LogDebug("Detected intent: {Intent} with confidence {Confidence}",
                    pattern.Intent, pattern.Confidence);
                return pattern.Intent;
            }
        }

        // Additional intent detection based on keywords
        if (queryLower.Contains("error") || queryLower.Contains("problem") || queryLower.Contains("issue"))
            return SearchIntent.Troubleshooting;

        if (queryLower.Contains("how") && (queryLower.Contains("to") || queryLower.Contains("do")))
            return SearchIntent.HowTo;

        if (queryLower.Contains("what") && queryLower.Contains("is"))
            return SearchIntent.Definition;

        return SearchIntent.General;
    }

    private IReadOnlyList<string> ExtractSearchTerms(string query)
    {
        // Remove common stop words and extract meaningful terms
        var stopWords = new HashSet<string>
        {
            "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with", "by",
            "is", "are", "was", "were", "be", "been", "have", "has", "had", "do", "does", "did",
            "will", "would", "could", "should", "can", "may", "might", "must", "this", "that",
            "these", "those", "i", "you", "he", "she", "it", "we", "they", "me", "him", "her", "us", "them",
            "how", "what", "where", "when", "why", "which", "who"
        };

        // Extract terms using regex
        var words = Regex.Matches(query, @"\b\w+\b", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .Distinct()
            .ToList();

        // Also extract quoted phrases
        var phrases = Regex.Matches(query, @"""([^""]+)""", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .ToList();

        var terms = new List<string>();
        terms.AddRange(words);
        terms.AddRange(phrases);

        return terms;
    }

    private string ProcessQueryByIntent(string query, SearchIntent intent)
    {
        switch (intent)
        {
            case SearchIntent.HowTo:
                // Remove "how to" and focus on the action
                return Regex.Replace(query, @"^(?:how\s+to|how\s+do\s+i|how\s+can\s+i)\s+", "", RegexOptions.IgnoreCase).Trim();

            case SearchIntent.Troubleshooting:
                // Emphasize error-related terms
                return query; // Keep original for now, could expand with error synonyms

            case SearchIntent.Definition:
                // Remove "what is" and focus on the term to define
                return Regex.Replace(query, @"^(?:what\s+is|define|definition\s+of|explain)\s+", "", RegexOptions.IgnoreCase).Trim();

            case SearchIntent.Comparison:
                // Extract items being compared
                var compareMatch = Regex.Match(query, @"(?:compare|vs|versus|difference\s+between)\s+(.+)", RegexOptions.IgnoreCase);
                return compareMatch.Success ? compareMatch.Groups[1].Value : query;

            case SearchIntent.Example:
                // Focus on what examples are needed for
                var exampleMatch = Regex.Match(query, @"(?:example|sample|demo)\s+(?:of\s+)?(.+)", RegexOptions.IgnoreCase);
                return exampleMatch.Success ? exampleMatch.Groups[1].Value : query;

            default:
                return query;
        }
    }

    private double CalculateComplexity(string query, IReadOnlyList<string> terms)
    {
        var complexity = 0.0;

        // Base complexity on term count
        complexity += Math.Min(terms.Count * 0.1, 0.5);

        // Add complexity for special characters and operators
        if (query.Contains("\"")) complexity += 0.1; // Quoted phrases
        if (query.Contains("AND") || query.Contains("OR")) complexity += 0.2; // Boolean operators
        if (query.Contains("*") || query.Contains("?")) complexity += 0.15; // Wildcards

        // Add complexity for query length
        complexity += Math.Min(query.Length / 100.0, 0.3);

        return Math.Min(complexity, 1.0);
    }

    private string GenerateInterpretation(string query, SearchIntent intent, IReadOnlyList<string> terms)
    {
        var interpretation = intent switch
        {
            SearchIntent.HowTo => $"Looking for instructions on: {string.Join(", ", terms.Take(3))}",
            SearchIntent.Troubleshooting => $"Seeking solutions for problems related to: {string.Join(", ", terms.Take(3))}",
            SearchIntent.Definition => $"Searching for definitions of: {string.Join(", ", terms.Take(3))}",
            SearchIntent.Comparison => $"Comparing different aspects of: {string.Join(", ", terms.Take(3))}",
            SearchIntent.Example => $"Looking for examples of: {string.Join(", ", terms.Take(3))}",
            _ => $"General search for: {string.Join(", ", terms.Take(3))}"
        };

        return interpretation;
    }
}

/// <summary>
/// Pattern for query intent detection
/// </summary>
public record QueryPattern
{
    public required Regex Pattern { get; init; }
    public required SearchIntent Intent { get; init; }
    public required double Confidence { get; init; }
}