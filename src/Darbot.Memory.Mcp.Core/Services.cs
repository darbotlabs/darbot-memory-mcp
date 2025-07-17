using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;

namespace Darbot.Memory.Mcp.Core.Services;

/// <summary>
/// Service for calculating cryptographic hashes of conversation content
/// </summary>
public class HashCalculator : IHashCalculator
{
    private readonly string _algorithm;

    public HashCalculator(string algorithm = "SHA256")
    {
        _algorithm = algorithm;
    }

    public string CalculateHash(ConversationTurn turn)
    {
        // Create a consistent representation for hashing
        var hashContent = new
        {
            conversationId = turn.ConversationId,
            turnNumber = turn.TurnNumber,
            utcTimestamp = turn.UtcTimestamp.ToString("O"), // ISO 8601 format
            prompt = turn.Prompt,
            model = turn.Model,
            response = turn.Response,
            toolsUsed = turn.ToolsUsed,
            schemaVersion = turn.SchemaVersion
        };

        var json = JsonSerializer.Serialize(hashContent, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        var bytes = Encoding.UTF8.GetBytes(json);

        using (var hasher = _factory.Create(_algorithm))
        {
            var hashBytes = hasher.ComputeHash(bytes);
            return $"{_algorithm.ToLowerInvariant()}-{Convert.ToHexString(hashBytes).ToLowerInvariant()}";
        }
    }

    public bool ValidateHash(ConversationTurn turn)
    {
        if (string.IsNullOrEmpty(turn.Hash))
            return false;

        var calculatedHash = CalculateHash(turn);
        return string.Equals(turn.Hash, calculatedHash, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Service for formatting conversation turns into markdown
/// </summary>
public class ConversationFormatter : IConversationFormatter
{
    private readonly string _fileNameTemplate;

    public ConversationFormatter(string fileNameTemplate = "%utc%_%conversationId%_%turn%.md")
    {
        _fileNameTemplate = fileNameTemplate;
    }

    public string FormatToMarkdown(ConversationTurn turn)
    {
        var sb = new StringBuilder();

        // Header with metadata (tamper-evident)
        sb.AppendLine($"<!-- SchemaVersion: {turn.SchemaVersion} -->");
        sb.AppendLine("# Darbot Conversation Log");
        sb.AppendLine($"*ConversationId:* `{turn.ConversationId}`");
        sb.AppendLine($"*Turn:* `{turn.TurnNumber}`");
        sb.AppendLine($"*Timestamp (UTC):* `{turn.UtcTimestamp:O}`");
        if (!string.IsNullOrEmpty(turn.Hash))
        {
            sb.AppendLine($"*Hash:* `{turn.Hash}`");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // Prompt section
        sb.AppendLine("## Prompt");
        sb.AppendLine($"> *User:* \"{turn.Prompt}\"");
        sb.AppendLine();

        // Model section
        sb.AppendLine("## Model");
        sb.AppendLine($"`{turn.Model}`");
        sb.AppendLine();

        // Tools section (if any)
        if (turn.ToolsUsed.Any())
        {
            sb.AppendLine("## Tools Used");
            foreach (var tool in turn.ToolsUsed)
            {
                sb.AppendLine($"- `{tool}`");
            }
            sb.AppendLine();
        }

        // Response section
        sb.AppendLine("## Response");
        sb.AppendLine("```");
        sb.AppendLine(turn.Response);
        sb.AppendLine("```");
        sb.AppendLine();

        // Footer warning
        sb.AppendLine("*Lines above the horizontal rule (`---`) act as a tamper‑evident header.*");
        sb.AppendLine("**Important:** Do **not** alter the header once committed.");

        return sb.ToString();
    }

    public string GenerateFileName(ConversationTurn turn)
    {
        var fileName = _fileNameTemplate
            .Replace("%utc%", turn.UtcTimestamp.ToString("yyyyMMdd-HHmmss"))
            .Replace("%conversationId%", SanitizeForFileName(turn.ConversationId))
            .Replace("%turn%", turn.TurnNumber.ToString("D3"));

        return fileName;
    }

    private static string SanitizeForFileName(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "unknown";

        // Take first 8 characters of conversation ID for filename
        var sanitized = input.Length > 8 ? input[..8] : input;

        // Replace invalid filename characters
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in invalidChars)
        {
            sanitized = sanitized.Replace(c, '_');
        }

        return sanitized;
    }
}