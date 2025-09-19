using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Darbot.Memory.Mcp.Core.Configuration;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Darbot.Memory.Mcp.Storage.Providers;

/// <summary>
/// Git storage provider that saves conversation turns as Markdown files in a Git repository
/// with automatic commit and optional push functionality
/// </summary>
public class GitStorageProvider : IStorageProvider
{
    private readonly GitConfiguration _config;
    private readonly IConversationFormatter _formatter;
    private readonly ILogger<GitStorageProvider> _logger;
    private readonly string _workingDirectory;

    public GitStorageProvider(
        IOptions<DarbotConfiguration> options,
        IConversationFormatter formatter,
        ILogger<GitStorageProvider> logger)
    {
        _config = options.Value.Storage.Git;
        _formatter = formatter;
        _logger = logger;
        _workingDirectory = Path.GetFullPath(_config.RepositoryPath);
    }

    public static async Task<GitStorageProvider> CreateAsync(
        IOptions<DarbotConfiguration> options,
        IConversationFormatter formatter,
        ILogger<GitStorageProvider> logger)
    {
        var provider = new GitStorageProvider(options, formatter, logger);
        await provider.InitializeRepositoryAsync();
        return provider;
    }

    public async Task<bool> WriteConversationTurnAsync(ConversationTurn turn, CancellationToken cancellationToken = default)
    {
        try
        {
            // Ensure repository is ready
            await EnsureRepositoryReadyAsync(cancellationToken);

            // Generate filename and content
            var fileName = _formatter.GenerateFileName(turn);
            var filePath = Path.Combine(_workingDirectory, fileName);
            var content = _formatter.FormatToMarkdown(turn);

            // Write to file
            await File.WriteAllTextAsync(filePath, content, cancellationToken);

            // Git operations
            await ExecuteGitCommandAsync("add", fileName);

            if (_config.AutoCommit)
            {
                var commitMessage = $"Add conversation turn {turn.ConversationId}:{turn.TurnNumber}";
                await ExecuteGitCommandAsync("commit", "-m", $"\"{commitMessage}\"");

                if (_config.AutoPush)
                {
                    await ExecuteGitCommandAsync("push", "origin", _config.Branch);
                }
            }

            _logger.LogDebug("Wrote conversation turn to Git repository: {FilePath}", filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write conversation turn to Git repository");
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
            // Ensure repository is ready
            await EnsureRepositoryReadyAsync(cancellationToken);

            var addedFiles = new List<string>();

            foreach (var turn in turnsList)
            {
                try
                {
                    var fileName = _formatter.GenerateFileName(turn);
                    var filePath = Path.Combine(_workingDirectory, fileName);
                    var content = _formatter.FormatToMarkdown(turn);

                    await File.WriteAllTextAsync(filePath, content, cancellationToken);
                    addedFiles.Add(fileName);
                    processedCount++;

                    _logger.LogDebug("Wrote conversation turn to Git repository: {FilePath}", filePath);
                }
                catch (Exception ex)
                {
                    var errorMsg = $"Failed to write turn {turn.ConversationId}:{turn.TurnNumber} - {ex.Message}";
                    errors.Add(errorMsg);
                    _logger.LogError(ex, "Failed to write conversation turn {ConversationId}:{Turn}",
                        turn.ConversationId, turn.TurnNumber);
                }
            }

            // Git operations for batch
            if (addedFiles.Any())
            {
                await ExecuteGitCommandAsync("add", string.Join(" ", addedFiles.Select(f => $"\"{f}\"")));

                if (_config.AutoCommit)
                {
                    var commitMessage = $"Add batch of {processedCount} conversation turns";
                    await ExecuteGitCommandAsync("commit", "-m", $"\"{commitMessage}\"");

                    if (_config.AutoPush)
                    {
                        await ExecuteGitCommandAsync("push", "origin", _config.Branch);
                    }
                }
            }

            var success = errors.Count == 0;
            var message = success
                ? $"Successfully wrote {processedCount} conversation turns to Git repository"
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

    public async Task<ConversationSearchResponse> SearchConversationsAsync(ConversationSearchRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = new DirectoryInfo(_workingDirectory);
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
            var directory = new DirectoryInfo(_workingDirectory);
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
            var directory = new DirectoryInfo(_workingDirectory);
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
            var directory = new DirectoryInfo(_workingDirectory);
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

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Check if repository directory exists or can be created
            var directory = new DirectoryInfo(_workingDirectory);
            if (!directory.Exists)
            {
                directory.Create();
            }

            // Check if it's a Git repository
            if (!Directory.Exists(Path.Combine(_workingDirectory, ".git")))
            {
                _logger.LogWarning("Directory is not a Git repository: {Path}", _workingDirectory);
                return false;
            }

            // Try a simple Git command
            var result = await ExecuteGitCommandAsync("status", "--porcelain");

            // Try to write a test file
            var testFile = Path.Combine(_workingDirectory, ".health-check");
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
            _logger.LogError(ex, "Git storage health check failed");
            return false;
        }
    }

    private async Task InitializeRepositoryAsync()
    {
        try
        {
            var directory = new DirectoryInfo(_workingDirectory);
            if (!directory.Exists)
            {
                directory.Create();
                _logger.LogInformation("Created repository directory: {Path}", _workingDirectory);
            }

            // Check if it's already a Git repository
            if (!Directory.Exists(Path.Combine(_workingDirectory, ".git")))
            {
                await ExecuteGitCommandAsync("init");
                _logger.LogInformation("Initialized Git repository: {Path}", _workingDirectory);

                // Set up remote if specified
                if (!string.IsNullOrEmpty(_config.RemoteUrl))
                {
                    await ExecuteGitCommandAsync("remote", "add", "origin", _config.RemoteUrl);
                    _logger.LogInformation("Added remote origin: {RemoteUrl}", _config.RemoteUrl);
                }

                // Create and checkout the specified branch
                if (_config.Branch != "main")
                {
                    await ExecuteGitCommandAsync("checkout", "-b", _config.Branch);
                    _logger.LogInformation("Created and switched to branch: {Branch}", _config.Branch);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Git repository");
            throw;
        }
    }

    private async Task EnsureRepositoryReadyAsync(CancellationToken cancellationToken)
    {
        // Pull latest changes if remote is configured and auto-pull is enabled
        if (!string.IsNullOrEmpty(_config.RemoteUrl))
        {
            try
            {
                await ExecuteGitCommandAsync("pull", "origin", _config.Branch);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to pull latest changes, continuing anyway");
            }
        }
    }

    private async Task<string> ExecuteGitCommandAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = string.Join(" ", arguments),
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start git process");
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git command failed: {string.Join(" ", arguments)}\nError: {error}");
        }

        return output;
    }

    // Helper methods (same as FileSystemStorageProvider)
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