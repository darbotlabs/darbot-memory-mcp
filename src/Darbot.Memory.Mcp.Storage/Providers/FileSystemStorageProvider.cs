using System.Globalization;
using System.Text.RegularExpressions;
using Darbot.Memory.Mcp.Core.Configuration;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Darbot.Memory.Mcp.Storage.Providers;

/// <summary>
/// File system storage provider that saves conversation turns as Markdown files
/// </summary>
public class FileSystemStorageProvider : IStorageProvider
{
    private readonly string _conversationsPath;
    private readonly IConversationFormatter _formatter;
    private readonly ILogger<FileSystemStorageProvider> _logger;

    public FileSystemStorageProvider(
        IOptions<DarbotConfiguration> options,
        IConversationFormatter formatter,
        ILogger<FileSystemStorageProvider> logger)
    {
        var config = options.Value;
        // Use the configured root path or fall back to storage.filesystem.rootpath for backward compatibility
        var basePath = !string.IsNullOrEmpty(config.Storage.BasePath) 
            ? config.Storage.BasePath 
            : config.Storage.FileSystem.RootPath;
        _conversationsPath = Path.Combine(basePath, "conversations");
        _formatter = formatter;
        _logger = logger;
    }

    public async Task<bool> WriteConversationTurnAsync(ConversationTurn turn, CancellationToken cancellationToken = default)
    {
        try
        {
            // Ensure directory exists
            var directory = new DirectoryInfo(_conversationsPath);
            if (!directory.Exists)
            {
                directory.Create();
                _logger.LogInformation("Created storage directory: {Path}", _conversationsPath);
            }

            // Generate filename and content
            var fileName = _formatter.GenerateFileName(turn);
            var filePath = Path.Combine(_conversationsPath, fileName);
            var content = _formatter.FormatToMarkdown(turn);

            // Write to file
            await File.WriteAllTextAsync(filePath, content, cancellationToken);

            _logger.LogDebug("Wrote conversation turn to file: {FilePath}", filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write conversation turn to file system");
            return false;
        }
    }

    public async Task<BatchWriteResponse> WriteBatchAsync(IEnumerable<ConversationTurn> turns, CancellationToken cancellationToken = default)
    {
        var turnsList = turns.ToList();
        var processedCount = 0;
        var errors = new List<string>();

        try
        {
            // Ensure directory exists
            var directory = new DirectoryInfo(_conversationsPath);
            if (!directory.Exists)
            {
                directory.Create();
                _logger.LogInformation("Created storage directory: {Path}", _conversationsPath);
            }

            foreach (var turn in turnsList)
            {
                try
                {
                    var fileName = _formatter.GenerateFileName(turn);
                    var filePath = Path.Combine(_conversationsPath, fileName);
                    var content = _formatter.FormatToMarkdown(turn);

                    await File.WriteAllTextAsync(filePath, content, cancellationToken);
                    processedCount++;

                    _logger.LogDebug("Wrote conversation turn to file: {FilePath}", filePath);
                }
                catch (Exception ex)
                {
                    var errorMsg = $"Failed to write turn {turn.ConversationId}:{turn.TurnNumber} - {ex.Message}";
                    errors.Add(errorMsg);
                    _logger.LogError(ex, "Failed to write conversation turn {ConversationId}:{Turn}",
                        turn.ConversationId, turn.TurnNumber);
                }
            }

            var success = errors.Count == 0;
            var message = success
                ? $"Successfully wrote {processedCount} conversation turns"
                : $"Wrote {processedCount}/{turnsList.Count} turns with {errors.Count} errors";

            return new BatchWriteResponse
            {
                Success = success,
                ProcessedCount = processedCount,
                Errors = errors,
                Message = message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch write operation failed");
            return new BatchWriteResponse
            {
                Success = false,
                ProcessedCount = processedCount,
                Errors = errors.Concat(new[] { ex.Message }).ToList(),
                Message = "Batch write operation failed"
            };
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Check if we can create the directory
            var directory = new DirectoryInfo(_conversationsPath);
            if (!directory.Exists)
            {
                directory.Create();
            }

            // Try to write a test file
            var testFile = Path.Combine(_conversationsPath, ".health-check");
            await File.WriteAllTextAsync(testFile, "health-check", cancellationToken);

            // Clean up test file
            if (File.Exists(testFile))
            {
                File.Delete(testFile);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FileSystem storage health check failed");
            return false;
        }
    }

    public async Task<ConversationSearchResponse> SearchConversationsAsync(ConversationSearchRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = new DirectoryInfo(_conversationsPath);
            if (!directory.Exists)
            {
                return new ConversationSearchResponse
                {
                    Results = Array.Empty<ConversationTurn>(),
                    TotalCount = 0,
                    HasMore = false,
                    Skip = request.Skip,
                    Take = request.Take
                };
            }

            // Get all markdown files
            var files = directory.GetFiles("*.md", SearchOption.TopDirectoryOnly);
            var conversations = new List<ConversationTurn>();

            foreach (var file in files)
            {
                try
                {
                    var conversation = await ParseMarkdownFileAsync(file.FullName, cancellationToken);
                    if (conversation != null && MatchesSearchCriteria(conversation, request))
                    {
                        conversations.Add(conversation);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse markdown file: {FilePath}", file.FullName);
                }
            }

            // Sort results
            conversations = SortConversations(conversations, request.SortBy, request.SortDescending);

            // Apply pagination
            var totalCount = conversations.Count;
            var results = conversations.Skip(request.Skip).Take(request.Take).ToList();
            var hasMore = request.Skip + request.Take < totalCount;

            return new ConversationSearchResponse
            {
                Results = results,
                TotalCount = totalCount,
                HasMore = hasMore,
                Skip = request.Skip,
                Take = request.Take
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search conversations");
            return new ConversationSearchResponse
            {
                Results = Array.Empty<ConversationTurn>(),
                TotalCount = 0,
                HasMore = false,
                Skip = request.Skip,
                Take = request.Take
            };
        }
    }

    public async Task<ConversationListResponse> ListConversationsAsync(ConversationListRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = new DirectoryInfo(_conversationsPath);
            if (!directory.Exists)
            {
                return new ConversationListResponse
                {
                    Conversations = Array.Empty<ConversationSummary>(),
                    TotalCount = 0,
                    HasMore = false,
                    Skip = request.Skip,
                    Take = request.Take
                };
            }

            // Get all markdown files and group by conversation ID
            var files = directory.GetFiles("*.md", SearchOption.TopDirectoryOnly);
            var conversationGroups = new Dictionary<string, List<ConversationTurn>>();

            foreach (var file in files)
            {
                try
                {
                    var conversation = await ParseMarkdownFileAsync(file.FullName, cancellationToken);
                    if (conversation != null)
                    {
                        // Apply date filtering
                        if (request.FromDate.HasValue && conversation.UtcTimestamp < request.FromDate.Value)
                            continue;
                        if (request.ToDate.HasValue && conversation.UtcTimestamp > request.ToDate.Value)
                            continue;

                        if (!conversationGroups.ContainsKey(conversation.ConversationId))
                        {
                            conversationGroups[conversation.ConversationId] = new List<ConversationTurn>();
                        }
                        conversationGroups[conversation.ConversationId].Add(conversation);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse markdown file: {FilePath}", file.FullName);
                }
            }

            // Create conversation summaries
            var summaries = conversationGroups.Select(group => CreateConversationSummary(group.Key, group.Value)).ToList();

            // Sort summaries
            summaries = SortConversationSummaries(summaries, request.SortBy, request.SortDescending);

            // Apply pagination
            var totalCount = summaries.Count;
            var results = summaries.Skip(request.Skip).Take(request.Take).ToList();
            var hasMore = request.Skip + request.Take < totalCount;

            return new ConversationListResponse
            {
                Conversations = results,
                TotalCount = totalCount,
                HasMore = hasMore,
                Skip = request.Skip,
                Take = request.Take
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list conversations");
            return new ConversationListResponse
            {
                Conversations = Array.Empty<ConversationSummary>(),
                TotalCount = 0,
                HasMore = false,
                Skip = request.Skip,
                Take = request.Take
            };
        }
    }

    public async Task<ConversationTurn?> GetConversationTurnAsync(string conversationId, int turnNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = new DirectoryInfo(_conversationsPath);
            if (!directory.Exists)
                return null;

            // Find files that match the conversation ID and turn number pattern
            var pattern = $"*{SanitizeForFileName(conversationId)}*{turnNumber:D3}*.md";
            var files = directory.GetFiles(pattern, SearchOption.TopDirectoryOnly);

            foreach (var file in files)
            {
                try
                {
                    var conversation = await ParseMarkdownFileAsync(file.FullName, cancellationToken);
                    if (conversation?.ConversationId == conversationId && conversation.TurnNumber == turnNumber)
                    {
                        return conversation;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse markdown file: {FilePath}", file.FullName);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get conversation turn {ConversationId}:{TurnNumber}", conversationId, turnNumber);
            return null;
        }
    }

    public async Task<IReadOnlyList<ConversationTurn>> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = new DirectoryInfo(_conversationsPath);
            if (!directory.Exists)
                return Array.Empty<ConversationTurn>();

            // Find all files that match the conversation ID pattern
            var pattern = $"*{SanitizeForFileName(conversationId)}*.md";
            var files = directory.GetFiles(pattern, SearchOption.TopDirectoryOnly);
            var conversations = new List<ConversationTurn>();

            foreach (var file in files)
            {
                try
                {
                    var conversation = await ParseMarkdownFileAsync(file.FullName, cancellationToken);
                    if (conversation?.ConversationId == conversationId)
                    {
                        conversations.Add(conversation);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse markdown file: {FilePath}", file.FullName);
                }
            }

            // Sort by turn number
            return conversations.OrderBy(c => c.TurnNumber).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get conversation {ConversationId}", conversationId);
            return Array.Empty<ConversationTurn>();
        }
    }

    private async Task<ConversationTurn?> ParseMarkdownFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        return ParseMarkdownContent(content);
    }

    private ConversationTurn? ParseMarkdownContent(string content)
    {
        try
        {
            // Extract metadata from the header
            var schemaVersionMatch = Regex.Match(content, @"<!-- SchemaVersion: (.+?) -->");
            var conversationIdMatch = Regex.Match(content, @"\*ConversationId:\* `(.+?)`");
            var turnMatch = Regex.Match(content, @"\*Turn:\* `(\d+)`");
            var timestampMatch = Regex.Match(content, @"\*Timestamp \(UTC\):\* `(.+?)`");
            var hashMatch = Regex.Match(content, @"\*Hash:\* `(.+?)`");

            if (!conversationIdMatch.Success || !turnMatch.Success || !timestampMatch.Success)
            {
                return null;
            }

            // Extract content sections
            var promptMatch = Regex.Match(content, @"## Prompt\s*\n> \*User:\* ""(.+?)""", RegexOptions.Singleline);
            var modelMatch = Regex.Match(content, @"## Model\s*\n`(.+?)`");
            var responseMatch = Regex.Match(content, @"## Response\s*\n```\s*\n(.*?)\n```", RegexOptions.Singleline);

            // Extract tools used
            var toolsSection = Regex.Match(content, @"## Tools Used\s*\n((?:- `[^`]+`\s*\n)*)", RegexOptions.Multiline);
            var toolsUsed = new List<string>();
            if (toolsSection.Success)
            {
                var toolMatches = Regex.Matches(toolsSection.Groups[1].Value, @"- `([^`]+)`");
                toolsUsed.AddRange(toolMatches.Cast<Match>().Select(m => m.Groups[1].Value));
            }

            return new ConversationTurn
            {
                ConversationId = conversationIdMatch.Groups[1].Value,
                TurnNumber = int.Parse(turnMatch.Groups[1].Value),
                UtcTimestamp = DateTime.Parse(timestampMatch.Groups[1].Value, null, DateTimeStyles.RoundtripKind),
                Prompt = promptMatch.Success ? promptMatch.Groups[1].Value : string.Empty,
                Model = modelMatch.Success ? modelMatch.Groups[1].Value : string.Empty,
                Response = responseMatch.Success ? responseMatch.Groups[1].Value : string.Empty,
                ToolsUsed = toolsUsed,
                Hash = hashMatch.Success ? hashMatch.Groups[1].Value : null,
                SchemaVersion = schemaVersionMatch.Success ? schemaVersionMatch.Groups[1].Value : "v1.0.0"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse markdown content");
            return null;
        }
    }

    private bool MatchesSearchCriteria(ConversationTurn conversation, ConversationSearchRequest request)
    {
        // Filter by conversation ID
        if (!string.IsNullOrEmpty(request.ConversationId) &&
            !conversation.ConversationId.Contains(request.ConversationId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Filter by search text (searches in prompt and response)
        if (!string.IsNullOrEmpty(request.SearchText))
        {
            var searchText = request.SearchText.ToLowerInvariant();
            if (!conversation.Prompt.ToLowerInvariant().Contains(searchText) &&
                !conversation.Response.ToLowerInvariant().Contains(searchText))
            {
                return false;
            }
        }

        // Filter by model
        if (!string.IsNullOrEmpty(request.Model) &&
            !conversation.Model.Contains(request.Model, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Filter by date range
        if (request.FromDate.HasValue && conversation.UtcTimestamp < request.FromDate.Value)
            return false;
        if (request.ToDate.HasValue && conversation.UtcTimestamp > request.ToDate.Value)
            return false;

        // Filter by tools used
        if (request.ToolsUsed.Any())
        {
            if (!request.ToolsUsed.Any(tool => conversation.ToolsUsed.Any(used => 
                used.Contains(tool, StringComparison.OrdinalIgnoreCase))))
            {
                return false;
            }
        }

        return true;
    }

    private List<ConversationTurn> SortConversations(List<ConversationTurn> conversations, string sortBy, bool descending)
    {
        return sortBy.ToLowerInvariant() switch
        {
            "conversationid" => descending
                ? conversations.OrderByDescending(c => c.ConversationId).ToList()
                : conversations.OrderBy(c => c.ConversationId).ToList(),
            "turnnumber" => descending
                ? conversations.OrderByDescending(c => c.TurnNumber).ToList()
                : conversations.OrderBy(c => c.TurnNumber).ToList(),
            _ => descending // timestamp (default)
                ? conversations.OrderByDescending(c => c.UtcTimestamp).ToList()
                : conversations.OrderBy(c => c.UtcTimestamp).ToList()
        };
    }

    private ConversationSummary CreateConversationSummary(string conversationId, List<ConversationTurn> turns)
    {
        var orderedTurns = turns.OrderBy(t => t.TurnNumber).ToList();
        var firstTurn = orderedTurns.First();
        var lastTurn = orderedTurns.Last();

        return new ConversationSummary
        {
            ConversationId = conversationId,
            TurnCount = turns.Count,
            FirstTurnTimestamp = firstTurn.UtcTimestamp,
            LastTurnTimestamp = lastTurn.UtcTimestamp,
            ModelsUsed = turns.Select(t => t.Model).Distinct().ToList(),
            ToolsUsed = turns.SelectMany(t => t.ToolsUsed).Distinct().ToList(),
            LastPrompt = lastTurn.Prompt
        };
    }

    private List<ConversationSummary> SortConversationSummaries(List<ConversationSummary> summaries, string sortBy, bool descending)
    {
        return sortBy.ToLowerInvariant() switch
        {
            "conversationid" => descending
                ? summaries.OrderByDescending(s => s.ConversationId).ToList()
                : summaries.OrderBy(s => s.ConversationId).ToList(),
            "turncount" => descending
                ? summaries.OrderByDescending(s => s.TurnCount).ToList()
                : summaries.OrderBy(s => s.TurnCount).ToList(),
            _ => descending // lastactivity (default)
                ? summaries.OrderByDescending(s => s.LastTurnTimestamp).ToList()
                : summaries.OrderBy(s => s.LastTurnTimestamp).ToList()
        };
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
