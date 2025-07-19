using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Darbot.Memory.Mcp.Core.Configuration;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Darbot.Memory.Mcp.Storage.Providers;

/// <summary>
/// Azure Blob Storage provider that saves conversation turns as Markdown files in Azure Blob Storage
/// </summary>
public class AzureBlobStorageProvider : IStorageProvider
{
    private readonly AzureBlobConfiguration _config;
    private readonly IConversationFormatter _formatter;
    private readonly ILogger<AzureBlobStorageProvider> _logger;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly BlobContainerClient _containerClient;

    // Regex patterns for parsing markdown content
    private static readonly Regex PromptRegex = new(@"## Prompt\s*\n> \*User:\* ""(.+?)""", RegexOptions.Singleline);
    private static readonly Regex ModelRegex = new(@"## Model\s*\n`(.+?)`", RegexOptions.Singleline);
    private static readonly Regex ResponseRegex = new(@"## Response\s*\n```\s*\n(.*?)\n```", RegexOptions.Singleline);
    private static readonly Regex ToolsSectionRegex = new(@"## Tools Used\s*\n((?:- `[^`]+`\s*\n)*)", RegexOptions.Multiline);
    private static readonly Regex ToolRegex = new(@"- `([^`]+)`", RegexOptions.Multiline);

    public AzureBlobStorageProvider(
        IOptions<DarbotConfiguration> options,
        IConversationFormatter formatter,
        ILogger<AzureBlobStorageProvider> logger)
    {
        _config = options.Value.Storage.AzureBlob;
        _formatter = formatter;
        _logger = logger;
        
        if (string.IsNullOrEmpty(_config.ConnectionString))
            throw new ArgumentException("Azure Blob Storage connection string is required", nameof(_config.ConnectionString));

        _blobServiceClient = new BlobServiceClient(_config.ConnectionString);
        _containerClient = _blobServiceClient.GetBlobContainerClient(_config.ContainerName);
    }

    public async Task<bool> WriteConversationTurnAsync(ConversationTurn turn, CancellationToken cancellationToken = default)
    {
        try
        {
            // Ensure container exists
            await _containerClient.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

            // Generate filename and content
            var fileName = _formatter.GenerateFileName(turn);
            var content = _formatter.FormatToMarkdown(turn);
            
            // Upload blob
            var blobClient = _containerClient.GetBlobClient(fileName);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            
            var uploadOptions = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = "text/markdown",
                    ContentEncoding = "utf-8"
                },
                Metadata = new Dictionary<string, string>
                {
                    { "ConversationId", turn.ConversationId },
                    { "TurnNumber", turn.TurnNumber.ToString() },
                    { "SchemaVersion", turn.SchemaVersion },
                    { "Model", turn.Model },
                    { "Timestamp", turn.UtcTimestamp.ToString("O") }
                },
                Tags = new Dictionary<string, string>
                {
                    { "Type", "ConversationTurn" },
                    { "ConversationId", SanitizeTagValue(turn.ConversationId) },
                    { "Model", SanitizeTagValue(turn.Model) }
                }
            };

            await blobClient.UploadAsync(stream, uploadOptions, cancellationToken);

            _logger.LogDebug("Wrote conversation turn to Azure Blob Storage: {BlobName}", fileName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write conversation turn to Azure Blob Storage");
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
            // Ensure container exists
            await _containerClient.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

            foreach (var turn in turnsList)
            {
                try
                {
                    var fileName = _formatter.GenerateFileName(turn);
                    var content = _formatter.FormatToMarkdown(turn);
                    
                    var blobClient = _containerClient.GetBlobClient(fileName);
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                    
                    var uploadOptions = new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders
                        {
                            ContentType = "text/markdown",
                            ContentEncoding = "utf-8"
                        },
                        Metadata = new Dictionary<string, string>
                        {
                            { "ConversationId", turn.ConversationId },
                            { "TurnNumber", turn.TurnNumber.ToString() },
                            { "SchemaVersion", turn.SchemaVersion },
                            { "Model", turn.Model },
                            { "Timestamp", turn.UtcTimestamp.ToString("O") }
                        },
                        Tags = new Dictionary<string, string>
                        {
                            { "Type", "ConversationTurn" },
                            { "ConversationId", SanitizeTagValue(turn.ConversationId) },
                            { "Model", SanitizeTagValue(turn.Model) }
                        }
                    };

                    await blobClient.UploadAsync(stream, uploadOptions, cancellationToken);
                    processedCount++;

                    _logger.LogDebug("Wrote conversation turn to Azure Blob Storage: {BlobName}", fileName);
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
                ? $"Successfully wrote {processedCount} conversation turns to Azure Blob Storage"
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
            var conversations = new List<ConversationTurn>();

            await foreach (var blobItem in _containerClient.GetBlobsAsync(
                traits: BlobTraits.Metadata | BlobTraits.Tags,
                prefix: null,
                cancellationToken: cancellationToken))
            {
                if (!blobItem.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var conversation = await ParseBlobAsync(blobItem.Name, cancellationToken);
                    if (conversation != null && MatchesSearchCriteria(conversation, request))
                    {
                        conversations.Add(conversation);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse blob: {BlobName}", blobItem.Name);
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
            var conversationGroups = new Dictionary<string, List<ConversationTurn>>();

            await foreach (var blobItem in _containerClient.GetBlobsAsync(
                traits: BlobTraits.Metadata | BlobTraits.Tags,
                prefix: null,
                cancellationToken: cancellationToken))
            {
                if (!blobItem.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var conversation = await ParseBlobAsync(blobItem.Name, cancellationToken);
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
                    _logger.LogWarning(ex, "Failed to parse blob: {BlobName}", blobItem.Name);
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
            await foreach (var blobItem in _containerClient.GetBlobsAsync(
                traits: BlobTraits.Metadata,
                prefix: null,
                cancellationToken: cancellationToken))
            {
                if (!blobItem.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Quick check using metadata
                if (blobItem.Metadata.TryGetValue("ConversationId", out var metaConversationId) &&
                    blobItem.Metadata.TryGetValue("TurnNumber", out var metaTurnNumber) &&
                    metaConversationId == conversationId &&
                    metaTurnNumber == turnNumber.ToString())
                {
                    return await ParseBlobAsync(blobItem.Name, cancellationToken);
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
            var conversations = new List<ConversationTurn>();

            await foreach (var blobItem in _containerClient.GetBlobsAsync(
                traits: BlobTraits.Metadata,
                prefix: null,
                cancellationToken: cancellationToken))
            {
                if (!blobItem.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Quick check using metadata
                if (blobItem.Metadata.TryGetValue("ConversationId", out var metaConversationId) &&
                    metaConversationId == conversationId)
                {
                    var conversation = await ParseBlobAsync(blobItem.Name, cancellationToken);
                    if (conversation != null)
                    {
                        conversations.Add(conversation);
                    }
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
            // Try to get blob service properties
            await _blobServiceClient.GetPropertiesAsync(cancellationToken);

            // Ensure container exists
            await _containerClient.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

            // Try to write a test blob
            var testBlobClient = _containerClient.GetBlobClient(".health-check");
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("health-check"));
            await testBlobClient.UploadAsync(stream, overwrite: true, cancellationToken: cancellationToken);

            // Clean up test blob
            await testBlobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure Blob Storage health check failed");
            return false;
        }
    }

    private async Task<ConversationTurn?> ParseBlobAsync(string blobName, CancellationToken cancellationToken)
    {
        try
        {
            var blobClient = _containerClient.GetBlobClient(blobName);
            var response = await blobClient.DownloadContentAsync(cancellationToken);
            var content = response.Value.Content.ToString();
            return ParseMarkdownContent(content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse blob content: {BlobName}", blobName);
            return null;
        }
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
            var promptMatch = PromptRegex.Match(content);
            var modelMatch = ModelRegex.Match(content);
            var responseMatch = ResponseRegex.Match(content);

            // Extract tools used
            var toolsSection = ToolsSectionRegex.Match(content);
            var toolsUsed = new List<string>();
            if (toolsSection.Success)
            {
                var toolMatches = ToolRegex.Matches(toolsSection.Groups[1].Value);
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

    private static string SanitizeTagValue(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "unknown";

        // Azure blob tags have restrictions: alphanumeric, space, plus, minus, period, colon, equals, underscore, forward slash
        // Take first 50 characters and replace invalid characters
        var sanitized = input.Length > 50 ? input[..50] : input;
        return Regex.Replace(sanitized, @"[^a-zA-Z0-9 +\-.:=_/]", "_");
    }
}