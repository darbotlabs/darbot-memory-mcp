using Darbot.Memory.Mcp.Core.Models;
using Darbot.Memory.Mcp.Core.Services;

namespace Darbot.Memory.Mcp.Tests;

public class HashCalculatorTests
{
    [Fact]
    public void CalculateHash_ProducesConsistentResults()
    {
        // Arrange
        var calculator = new HashCalculator("SHA256");
        var turn = new ConversationTurn
        {
            ConversationId = "test-123",
            TurnNumber = 1,
            UtcTimestamp = new DateTime(2024, 6, 25, 17, 0, 0, DateTimeKind.Utc),
            Prompt = "Hello",
            Model = "gpt-4o",
            Response = "Hi there!",
            ToolsUsed = new[] { "tool1", "tool2" }
        };

        // Act
        var hash1 = calculator.CalculateHash(turn);
        var hash2 = calculator.CalculateHash(turn);

        // Assert
        Assert.Equal(hash1, hash2);
        Assert.StartsWith("sha256-", hash1);
    }

    [Fact]
    public void CalculateHash_DifferentInputsProduceDifferentHashes()
    {
        // Arrange
        var calculator = new HashCalculator("SHA256");
        var turn1 = new ConversationTurn
        {
            ConversationId = "test-123",
            TurnNumber = 1,
            UtcTimestamp = new DateTime(2024, 6, 25, 17, 0, 0, DateTimeKind.Utc),
            Prompt = "Hello",
            Model = "gpt-4o",
            Response = "Hi there!"
        };

        var turn2 = turn1 with { Response = "Hello back!" };

        // Act
        var hash1 = calculator.CalculateHash(turn1);
        var hash2 = calculator.CalculateHash(turn2);

        // Assert
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ValidateHash_ReturnsTrueForValidHash()
    {
        // Arrange
        var calculator = new HashCalculator("SHA256");
        var turn = new ConversationTurn
        {
            ConversationId = "test-123",
            TurnNumber = 1,
            UtcTimestamp = new DateTime(2024, 6, 25, 17, 0, 0, DateTimeKind.Utc),
            Prompt = "Hello",
            Model = "gpt-4o",
            Response = "Hi there!"
        };

        var hash = calculator.CalculateHash(turn);
        var turnWithHash = turn with { Hash = hash };

        // Act
        var isValid = calculator.ValidateHash(turnWithHash);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void ValidateHash_ReturnsFalseForInvalidHash()
    {
        // Arrange
        var calculator = new HashCalculator("SHA256");
        var turn = new ConversationTurn
        {
            ConversationId = "test-123",
            TurnNumber = 1,
            UtcTimestamp = new DateTime(2024, 6, 25, 17, 0, 0, DateTimeKind.Utc),
            Prompt = "Hello",
            Model = "gpt-4o",
            Response = "Hi there!",
            Hash = "sha256-invalid"
        };

        // Act
        var isValid = calculator.ValidateHash(turn);

        // Assert
        Assert.False(isValid);
    }
}