using Darbot.Memory.Mcp.Core.Models;
using Darbot.Memory.Mcp.Core.Services;

namespace Darbot.Memory.Mcp.Tests;

public class ConversationFormatterTests
{
    [Fact]
    public void FormatToMarkdown_ProducesExpectedFormat()
    {
        // Arrange
        var formatter = new ConversationFormatter();
        var turn = new ConversationTurn
        {
            ConversationId = "test-123",
            TurnNumber = 1,
            UtcTimestamp = new DateTime(2024, 6, 25, 17, 0, 0, DateTimeKind.Utc),
            Prompt = "Hello, how can you help me?",
            Model = "gpt-4o",
            Response = "I can help you with various tasks.",
            ToolsUsed = new[] { "search", "calculator" },
            Hash = "sha256-abc123",
            SchemaVersion = "v1.0.0"
        };

        // Act
        var markdown = formatter.FormatToMarkdown(turn);

        // Assert
        Assert.Contains("<!-- SchemaVersion: v1.0.0 -->", markdown);
        Assert.Contains("# Darbot Conversation Log", markdown);
        Assert.Contains("*ConversationId:* `test-123`", markdown);
        Assert.Contains("*Turn:* `1`", markdown);
        Assert.Contains("*Hash:* `sha256-abc123`", markdown);
        Assert.Contains("## Prompt", markdown);
        Assert.Contains("> *User:* \"Hello, how can you help me?\"", markdown);
        Assert.Contains("## Model", markdown);
        Assert.Contains("`gpt-4o`", markdown);
        Assert.Contains("## Tools Used", markdown);
        Assert.Contains("- `search`", markdown);
        Assert.Contains("- `calculator`", markdown);
        Assert.Contains("## Response", markdown);
        Assert.Contains("I can help you with various tasks.", markdown);
        Assert.Contains("---", markdown);
    }

    [Fact]
    public void FormatToMarkdown_WithoutTools_SkipsToolsSection()
    {
        // Arrange
        var formatter = new ConversationFormatter();
        var turn = new ConversationTurn
        {
            ConversationId = "test-123",
            TurnNumber = 1,
            UtcTimestamp = new DateTime(2024, 6, 25, 17, 0, 0, DateTimeKind.Utc),
            Prompt = "Hello",
            Model = "gpt-4o",
            Response = "Hi there!",
            ToolsUsed = Array.Empty<string>()
        };

        // Act
        var markdown = formatter.FormatToMarkdown(turn);

        // Assert
        Assert.DoesNotContain("## Tools Used", markdown);
    }

    [Fact]
    public void GenerateFileName_ProducesExpectedFormat()
    {
        // Arrange
        var formatter = new ConversationFormatter("%utc%_%conversationId%_%turn%.md");
        var turn = new ConversationTurn
        {
            ConversationId = "test-conversation-123",
            TurnNumber = 42,
            UtcTimestamp = new DateTime(2024, 6, 25, 17, 30, 45, DateTimeKind.Utc),
            Prompt = "Hello",
            Model = "gpt-4o",
            Response = "Hi!"
        };

        // Act
        var fileName = formatter.GenerateFileName(turn);

        // Assert
        Assert.Equal("20240625-173045_test-con_042.md", fileName);
    }

    [Fact]
    public void GenerateFileName_WithSpecialCharacters_SanitizesFileName()
    {
        // Arrange
        var formatter = new ConversationFormatter();
        var turn = new ConversationTurn
        {
            ConversationId = "test/conv*123?",
            TurnNumber = 1,
            UtcTimestamp = new DateTime(2024, 6, 25, 17, 0, 0, DateTimeKind.Utc),
            Prompt = "Hello",
            Model = "gpt-4o",
            Response = "Hi!"
        };

        // Act
        var fileName = formatter.GenerateFileName(turn);

        // Assert
        Assert.DoesNotContain("/", fileName);
        Assert.DoesNotContain("*", fileName);
        Assert.DoesNotContain("?", fileName);
        Assert.Contains("test_con", fileName);
    }
}