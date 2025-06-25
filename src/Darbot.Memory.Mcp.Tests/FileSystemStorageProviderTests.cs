using Darbot.Memory.Mcp.Core.Configuration;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Darbot.Memory.Mcp.Storage.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Darbot.Memory.Mcp.Tests;

public class FileSystemStorageProviderTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly Mock<IConversationFormatter> _mockFormatter;
    private readonly Mock<ILogger<FileSystemStorageProvider>> _mockLogger;
    private readonly IOptions<DarbotConfiguration> _options;

    public FileSystemStorageProviderTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);

        _mockFormatter = new Mock<IConversationFormatter>();
        _mockLogger = new Mock<ILogger<FileSystemStorageProvider>>();

        var config = new DarbotConfiguration
        {
            Storage = new StorageConfiguration
            {
                FileSystem = new FileSystemConfiguration
                {
                    RootPath = _tempDirectory
                }
            }
        };
        _options = Options.Create(config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task WriteConversationTurnAsync_CreatesFileSuccessfully()
    {
        // Arrange
        var provider = new FileSystemStorageProvider(_options, _mockFormatter.Object, _mockLogger.Object);
        var turn = CreateTestTurn();

        _mockFormatter.Setup(f => f.GenerateFileName(turn))
                     .Returns("test-file.md");
        _mockFormatter.Setup(f => f.FormatToMarkdown(turn))
                     .Returns("# Test Content");

        // Act
        var result = await provider.WriteConversationTurnAsync(turn);

        // Assert
        Assert.True(result);
        var filePath = Path.Combine(_tempDirectory, "test-file.md");
        Assert.True(File.Exists(filePath));
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Equal("# Test Content", content);
    }

    [Fact]
    public async Task WriteBatchAsync_WritesMultipleFiles()
    {
        // Arrange
        var provider = new FileSystemStorageProvider(_options, _mockFormatter.Object, _mockLogger.Object);
        var turns = new[]
        {
            CreateTestTurn("conv1", 1),
            CreateTestTurn("conv1", 2)
        };

        _mockFormatter.Setup(f => f.GenerateFileName(It.Is<ConversationTurn>(t => t.TurnNumber == 1)))
                     .Returns("conv1-turn1.md");
        _mockFormatter.Setup(f => f.GenerateFileName(It.Is<ConversationTurn>(t => t.TurnNumber == 2)))
                     .Returns("conv1-turn2.md");
        _mockFormatter.Setup(f => f.FormatToMarkdown(It.IsAny<ConversationTurn>()))
                     .Returns("# Content");

        // Act
        var result = await provider.WriteBatchAsync(turns);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(2, result.ProcessedCount);
        Assert.Empty(result.Errors);
        Assert.True(File.Exists(Path.Combine(_tempDirectory, "conv1-turn1.md")));
        Assert.True(File.Exists(Path.Combine(_tempDirectory, "conv1-turn2.md")));
    }

    [Fact]
    public async Task IsHealthyAsync_ReturnsTrueWhenDirectoryAccessible()
    {
        // Arrange
        var provider = new FileSystemStorageProvider(_options, _mockFormatter.Object, _mockLogger.Object);

        // Act
        var result = await provider.IsHealthyAsync();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task IsHealthyAsync_ReturnsFalseWhenDirectoryNotAccessible()
    {
        // Arrange
        var config = new DarbotConfiguration
        {
            Storage = new StorageConfiguration
            {
                FileSystem = new FileSystemConfiguration
                {
                    RootPath = "/invalid/path/that/should/not/exist"
                }
            }
        };
        var invalidOptions = Options.Create(config);
        var provider = new FileSystemStorageProvider(invalidOptions, _mockFormatter.Object, _mockLogger.Object);

        // Act
        var result = await provider.IsHealthyAsync();

        // Assert
        Assert.False(result);
    }

    private static ConversationTurn CreateTestTurn(string conversationId = "test-123", int turnNumber = 1)
    {
        return new ConversationTurn
        {
            ConversationId = conversationId,
            TurnNumber = turnNumber,
            UtcTimestamp = DateTime.UtcNow,
            Prompt = "Test prompt",
            Model = "test-model",
            Response = "Test response"
        };
    }
}