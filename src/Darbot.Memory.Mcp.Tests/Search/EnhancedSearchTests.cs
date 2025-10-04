using Darbot.Memory.Mcp.Core.Models;
using Darbot.Memory.Mcp.Core.Search;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Darbot.Memory.Mcp.Tests.Search;

/// <summary>
/// Tests for the enhanced search functionality
/// </summary>
public class EnhancedSearchTests
{
    private readonly Mock<ILogger<QueryParser>> _mockQueryLogger;
    private readonly Mock<ILogger<RelevanceScorer>> _mockScorerLogger;
    private readonly QueryParser _queryParser;
    private readonly RelevanceScorer _relevanceScorer;

    public EnhancedSearchTests()
    {
        _mockQueryLogger = new Mock<ILogger<QueryParser>>();
        _mockScorerLogger = new Mock<ILogger<RelevanceScorer>>();
        _queryParser = new QueryParser(_mockQueryLogger.Object);
        _relevanceScorer = new RelevanceScorer(_mockScorerLogger.Object);
    }

    [Fact]
    public async Task QueryParser_ParsesHowToQuery_DetectsCorrectIntent()
    {
        // Arrange
        var query = "how to debug authentication errors";
        var context = new Dictionary<string, object>();

        // Act
        var result = await _queryParser.ParseQueryAsync(query, context);

        // Assert
        Assert.Equal(SearchIntent.HowTo, result.Intent);
        Assert.Equal("debug authentication errors", result.ProcessedQuery);
        Assert.Contains("debug", result.Terms);
        Assert.Contains("authentication", result.Terms);
        Assert.Contains("errors", result.Terms);
    }

    [Fact]
    public async Task QueryParser_ParsesTroubleshootingQuery_DetectsCorrectIntent()
    {
        // Arrange
        var query = "error connecting to database";
        var context = new Dictionary<string, object>();

        // Act
        var result = await _queryParser.ParseQueryAsync(query, context);

        // Assert
        Assert.Equal(SearchIntent.Troubleshooting, result.Intent);
        Assert.Contains("error", result.Terms);
        Assert.Contains("connecting", result.Terms);
        Assert.Contains("database", result.Terms);
    }

    [Fact]
    public async Task QueryParser_ParsesDefinitionQuery_DetectsCorrectIntent()
    {
        // Arrange
        var query = "what is microservices architecture";
        var context = new Dictionary<string, object>();

        // Act
        var result = await _queryParser.ParseQueryAsync(query, context);

        // Assert
        Assert.Equal(SearchIntent.Definition, result.Intent);
        Assert.Equal("microservices architecture", result.ProcessedQuery);
        Assert.Contains("microservices", result.Terms);
        Assert.Contains("architecture", result.Terms);
    }

    [Fact]
    public async Task QueryParser_ExtractsTermsCorrectly_FiltersStopWords()
    {
        // Arrange
        var query = "how to implement the authentication system";
        var context = new Dictionary<string, object>();

        // Act
        var result = await _queryParser.ParseQueryAsync(query, context);

        // Assert
        Assert.DoesNotContain("how", result.Terms);
        Assert.DoesNotContain("to", result.Terms);
        Assert.DoesNotContain("the", result.Terms);
        Assert.Contains("implement", result.Terms);
        Assert.Contains("authentication", result.Terms);
        Assert.Contains("system", result.Terms);
    }

    [Fact]
    public async Task RelevanceScorer_CalculatesHighScore_ForExactMatch()
    {
        // Arrange
        var turn = new ConversationTurn
        {
            ConversationId = "test-123",
            TurnNumber = 1,
            UtcTimestamp = DateTime.UtcNow,
            Prompt = "How to debug authentication errors in the system",
            Response = "To debug authentication errors, first check the log files for detailed error messages.",
            Model = "gpt-4",
            ToolsUsed = new[] { "debugger", "logs" }
        };

        var query = new ParsedQuery
        {
            OriginalQuery = "debug authentication errors",
            ProcessedQuery = "debug authentication errors",
            Terms = new[] { "debug", "authentication", "errors" },
            Intent = SearchIntent.Troubleshooting,
            Interpretation = "Looking for debugging help",
            Complexity = 0.3
        };

        // Act
        var result = await _relevanceScorer.CalculateRelevanceAsync(turn, query);

        // Assert
        Assert.True(result.Score > 0.5, $"Expected relevance score > 0.5 but got {result.Score}");
        Assert.NotNull(result.Explanation);
    }

    [Fact]
    public async Task RelevanceScorer_CalculatesLowScore_ForUnrelatedContent()
    {
        // Arrange
        var turn = new ConversationTurn
        {
            ConversationId = "test-456",
            TurnNumber = 1,
            UtcTimestamp = DateTime.UtcNow,
            Prompt = "How to bake a chocolate cake",
            Response = "Start by preheating your oven to 350 degrees.",
            Model = "gpt-4",
            ToolsUsed = Array.Empty<string>()
        };

        var query = new ParsedQuery
        {
            OriginalQuery = "debug authentication errors",
            ProcessedQuery = "debug authentication errors",
            Terms = new[] { "debug", "authentication", "errors" },
            Intent = SearchIntent.Troubleshooting,
            Interpretation = "Looking for debugging help",
            Complexity = 0.3
        };

        // Act
        var result = await _relevanceScorer.CalculateRelevanceAsync(turn, query);

        // Assert
        Assert.True(result.Score < 0.3, $"Expected relevance score < 0.3 but got {result.Score}");
    }

    [Fact]
    public async Task RelevanceScorer_AppliesIntentBoost_ForTroubleshootingQueries()
    {
        // Arrange - Two turns with same base content but different intent-relevant keywords
        var troubleshootingTurn = new ConversationTurn
        {
            ConversationId = "test-789",
            TurnNumber = 1,
            UtcTimestamp = DateTime.UtcNow,
            Prompt = "API connection test",
            Response = "This error usually occurs when debugging. Here's how to fix the problem and solve the issue...",
            Model = "gpt-4",
            ToolsUsed = Array.Empty<string>()
        };

        var regularTurn = new ConversationTurn
        {
            ConversationId = "test-101",
            TurnNumber = 1,
            UtcTimestamp = DateTime.UtcNow,
            Prompt = "API connection test",
            Response = "This topic usually comes up when discussing. Here's how to understand the concept and learn the method...",
            Model = "gpt-4",
            ToolsUsed = Array.Empty<string>()
        };

        var troubleshootingQuery = new ParsedQuery
        {
            OriginalQuery = "API connection test",
            ProcessedQuery = "API connection test",
            Terms = new[] { "API", "connection", "test" },
            Intent = SearchIntent.Troubleshooting,
            Interpretation = "Looking for troubleshooting help",
            Complexity = 0.3
        };

        // Act
        var troubleshootingScore = await _relevanceScorer.CalculateRelevanceAsync(troubleshootingTurn, troubleshootingQuery);
        var regularScore = await _relevanceScorer.CalculateRelevanceAsync(regularTurn, troubleshootingQuery);

        // Assert - The troubleshooting turn contains error keywords which should boost its score
        Assert.True(troubleshootingScore.Score > regularScore.Score,
            $"Troubleshooting content (score: {troubleshootingScore.Score:F3}) should score higher than regular content (score: {regularScore.Score:F3}) for troubleshooting queries due to error keyword boost");
    }

    [Theory]
    [InlineData("how to", SearchIntent.HowTo)]
    [InlineData("what is", SearchIntent.Definition)]
    [InlineData("error occurred", SearchIntent.Troubleshooting)]
    [InlineData("example of", SearchIntent.Example)]
    [InlineData("compare vs", SearchIntent.Comparison)]
    public async Task QueryParser_DetectsIntents_ForVariousQueryTypes(string queryPrefix, SearchIntent expectedIntent)
    {
        // Arrange
        var query = $"{queryPrefix} testing something";
        var context = new Dictionary<string, object>();

        // Act
        var result = await _queryParser.ParseQueryAsync(query, context);

        // Assert
        Assert.Equal(expectedIntent, result.Intent);
    }

    [Fact]
    public async Task QueryParser_CalculatesComplexity_BasedOnQueryCharacteristics()
    {
        // Arrange
        var simpleQuery = "error";
        var complexQuery = "how to debug \"authentication errors\" AND configure oauth OR implement jwt tokens";
        var context = new Dictionary<string, object>();

        // Act
        var simpleResult = await _queryParser.ParseQueryAsync(simpleQuery, context);
        var complexResult = await _queryParser.ParseQueryAsync(complexQuery, context);

        // Assert
        Assert.True(complexResult.Complexity > simpleResult.Complexity,
            "Complex query should have higher complexity score");
    }

    [Fact]
    public async Task RelevanceScorer_ConsidersModelMatch_InScoring()
    {
        // Arrange
        var gptTurn = new ConversationTurn
        {
            ConversationId = "test-model-1",
            TurnNumber = 1,
            UtcTimestamp = DateTime.UtcNow,
            Prompt = "Test prompt about machine learning",
            Response = "Machine learning is a subset of AI...",
            Model = "gpt-4",
            ToolsUsed = Array.Empty<string>()
        };

        var claudeTurn = new ConversationTurn
        {
            ConversationId = "test-model-2",
            TurnNumber = 1,
            UtcTimestamp = DateTime.UtcNow,
            Prompt = "Test prompt about machine learning",
            Response = "Machine learning is a subset of AI...",
            Model = "claude-2",
            ToolsUsed = Array.Empty<string>()
        };

        var gptQuery = new ParsedQuery
        {
            OriginalQuery = "machine learning gpt",
            ProcessedQuery = "machine learning gpt",
            Terms = new[] { "machine", "learning", "gpt" },
            Intent = SearchIntent.General,
            Interpretation = "General search",
            Complexity = 0.3
        };

        // Act
        var gptScore = await _relevanceScorer.CalculateRelevanceAsync(gptTurn, gptQuery);
        var claudeScore = await _relevanceScorer.CalculateRelevanceAsync(claudeTurn, gptQuery);

        // Assert
        Assert.True(gptScore.Score > claudeScore.Score,
            "GPT turn should score higher for GPT-related query");
    }

    [Fact]
    public async Task RelevanceScorer_ConsidersToolUsage_InScoring()
    {
        // Arrange
        var toolMatchTurn = new ConversationTurn
        {
            ConversationId = "test-tools-1",
            TurnNumber = 1,
            UtcTimestamp = DateTime.UtcNow,
            Prompt = "How to use debugger for testing",
            Response = "The debugger is essential for testing...",
            Model = "gpt-4",
            ToolsUsed = new[] { "debugger", "testing-framework" }
        };

        var noToolMatchTurn = new ConversationTurn
        {
            ConversationId = "test-tools-2",
            TurnNumber = 1,
            UtcTimestamp = DateTime.UtcNow,
            Prompt = "How to use debugger for testing",
            Response = "The debugger is essential for testing...",
            Model = "gpt-4",
            ToolsUsed = new[] { "calculator", "notepad" }
        };

        var debuggerQuery = new ParsedQuery
        {
            OriginalQuery = "debugger testing",
            ProcessedQuery = "debugger testing",
            Terms = new[] { "debugger", "testing" },
            Intent = SearchIntent.HowTo,
            Interpretation = "How to use tools",
            Complexity = 0.2
        };

        // Act
        var toolMatchScore = await _relevanceScorer.CalculateRelevanceAsync(toolMatchTurn, debuggerQuery);
        var noToolMatchScore = await _relevanceScorer.CalculateRelevanceAsync(noToolMatchTurn, debuggerQuery);

        // Assert
        Assert.True(toolMatchScore.Score > noToolMatchScore.Score,
            "Turn with matching tools should score higher");
    }
}