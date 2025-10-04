using System.Text.Json;
using Darbot.Memory.Mcp.Core.Configuration;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Darbot.Memory.Mcp.Core.Services;
using Darbot.Memory.Mcp.Storage.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Darbot.Memory.Mcp.Tests;

public class SearchFunctionalityTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly FileSystemStorageProvider _storageProvider;
    private readonly IConversationFormatter _formatter;
    private readonly IHashCalculator _hashCalculator;

    public SearchFunctionalityTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);

        var config = new DarbotConfiguration
        {
            Storage = new StorageConfiguration
            {
                BasePath = _tempDirectory,
                FileSystem = new FileSystemConfiguration
                {
                    RootPath = _tempDirectory
                }
            }
        };

        var options = Options.Create(config);
        var logger = Mock.Of<ILogger<FileSystemStorageProvider>>();

        _formatter = new ConversationFormatter();
        _hashCalculator = new HashCalculator();
        _storageProvider = new FileSystemStorageProvider(options, _formatter, logger);

        // Setup some test data
        SetupTestData().Wait();
    }

    private async Task SetupTestData()
    {
        var testTurns = new[]
        {
            new ConversationTurn
            {
                ConversationId = "test-conv-1",
                TurnNumber = 1,
                UtcTimestamp = DateTime.UtcNow.AddDays(-2),
                Prompt = "Hello, can you help me with Python programming?",
                Model = "gpt-4o",
                Response = "Sure! I'd be happy to help you with Python programming. What specific topic would you like to learn about?",
                ToolsUsed = new[] { "python_executor" }
            },
            new ConversationTurn
            {
                ConversationId = "test-conv-1",
                TurnNumber = 2,
                UtcTimestamp = DateTime.UtcNow.AddDays(-2).AddMinutes(5),
                Prompt = "Show me how to create a list in Python",
                Model = "gpt-4o",
                Response = "Here's how to create a list in Python:\n\n```python\nmy_list = [1, 2, 3, 4, 5]\n```",
                ToolsUsed = new[] { "python_executor", "code_formatter" }
            },
            new ConversationTurn
            {
                ConversationId = "test-conv-2",
                TurnNumber = 1,
                UtcTimestamp = DateTime.UtcNow.AddDays(-1),
                Prompt = "What's the weather like today?",
                Model = "gpt-3.5-turbo",
                Response = "I don't have access to real-time weather data. Please check a weather service.",
                ToolsUsed = Array.Empty<string>()
            },
            new ConversationTurn
            {
                ConversationId = "test-conv-3",
                TurnNumber = 1,
                UtcTimestamp = DateTime.UtcNow,
                Prompt = "Explain machine learning concepts",
                Model = "claude-3",
                Response = "Machine learning is a subset of artificial intelligence that focuses on algorithms that can learn from and make predictions on data.",
                ToolsUsed = new[] { "search_knowledge" }
            }
        };

        foreach (var turn in testTurns)
        {
            var turnWithHash = turn with { Hash = _hashCalculator.CalculateHash(turn) };
            await _storageProvider.WriteConversationTurnAsync(turnWithHash);
        }
    }

    [Fact]
    public async Task SearchConversations_WithTextSearch_ReturnsMatchingResults()
    {
        // Arrange
        var request = new ConversationSearchRequest
        {
            SearchText = "Python",
            Take = 10
        };

        // Act
        var result = await _storageProvider.SearchConversationsAsync(request);

        // Assert
        Assert.True(result.Results.Count >= 2);
        Assert.All(result.Results, r =>
            Assert.True(r.Prompt.Contains("Python", StringComparison.OrdinalIgnoreCase) ||
                       r.Response.Contains("Python", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task SearchConversations_WithConversationIdFilter_ReturnsCorrectConversation()
    {
        // Arrange
        var request = new ConversationSearchRequest
        {
            ConversationId = "test-conv-1",
            Take = 10
        };

        // Act
        var result = await _storageProvider.SearchConversationsAsync(request);

        // Assert
        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, r => Assert.Equal("test-conv-1", r.ConversationId));
    }

    [Fact]
    public async Task SearchConversations_WithModelFilter_ReturnsCorrectResults()
    {
        // Arrange
        var request = new ConversationSearchRequest
        {
            Model = "gpt-4o",
            Take = 10
        };

        // Act
        var result = await _storageProvider.SearchConversationsAsync(request);

        // Assert
        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, r => Assert.Contains("gpt-4o", r.Model));
    }

    [Fact]
    public async Task SearchConversations_WithToolsFilter_ReturnsCorrectResults()
    {
        // Arrange
        var request = new ConversationSearchRequest
        {
            ToolsUsed = new[] { "python_executor" },
            Take = 10
        };

        // Act
        var result = await _storageProvider.SearchConversationsAsync(request);

        // Assert
        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, r => Assert.Contains("python_executor", r.ToolsUsed));
    }

    [Fact]
    public async Task SearchConversations_WithDateRange_ReturnsCorrectResults()
    {
        // Arrange
        var request = new ConversationSearchRequest
        {
            FromDate = DateTime.UtcNow.AddDays(-1).AddHours(-1),
            ToDate = DateTime.UtcNow.AddHours(1),
            Take = 10
        };

        // Act
        var result = await _storageProvider.SearchConversationsAsync(request);

        // Assert
        Assert.Equal(2, result.Results.Count); // test-conv-2 and test-conv-3
    }

    [Fact]
    public async Task SearchConversations_WithPagination_RespectsLimits()
    {
        // Arrange
        var request = new ConversationSearchRequest
        {
            Skip = 1,
            Take = 2
        };

        // Act
        var result = await _storageProvider.SearchConversationsAsync(request);

        // Assert
        Assert.True(result.Results.Count <= 2);
        Assert.Equal(1, result.Skip);
        Assert.Equal(2, result.Take);
        Assert.Equal(4, result.TotalCount); // Total number of test turns
    }

    [Fact]
    public async Task ListConversations_ReturnsConversationSummaries()
    {
        // Arrange
        var request = new ConversationListRequest
        {
            Take = 10
        };

        // Act
        var result = await _storageProvider.ListConversationsAsync(request);

        // Assert
        Assert.Equal(3, result.Conversations.Count); // 3 unique conversations

        var conv1 = result.Conversations.FirstOrDefault(c => c.ConversationId == "test-conv-1");
        Assert.NotNull(conv1);
        Assert.Equal(2, conv1.TurnCount);
        Assert.Contains("gpt-4o", conv1.ModelsUsed);
        Assert.Contains("python_executor", conv1.ToolsUsed);
    }

    [Fact]
    public async Task GetConversation_ReturnsAllTurnsInOrder()
    {
        // Act
        var result = await _storageProvider.GetConversationAsync("test-conv-1");

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[0].TurnNumber);
        Assert.Equal(2, result[1].TurnNumber);
        Assert.All(result, r => Assert.Equal("test-conv-1", r.ConversationId));
    }

    [Fact]
    public async Task GetConversationTurn_ReturnsSpecificTurn()
    {
        // Act
        var result = await _storageProvider.GetConversationTurnAsync("test-conv-1", 2);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test-conv-1", result.ConversationId);
        Assert.Equal(2, result.TurnNumber);
        Assert.Contains("list", result.Prompt.ToLowerInvariant());
    }

    [Fact]
    public async Task GetConversationTurn_ReturnsNullForNonExistentTurn()
    {
        // Act
        var result = await _storageProvider.GetConversationTurnAsync("non-existent", 1);

        // Assert
        Assert.Null(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }
}