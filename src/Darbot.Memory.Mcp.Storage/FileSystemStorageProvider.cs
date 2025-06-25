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
    private readonly FileSystemConfiguration _config;
    private readonly IConversationFormatter _formatter;
    private readonly ILogger<FileSystemStorageProvider> _logger;

    public FileSystemStorageProvider(
        IOptions<DarbotConfiguration> options,
        IConversationFormatter formatter,
        ILogger<FileSystemStorageProvider> logger)
    {
        _config = options.Value.Storage.FileSystem;
        _formatter = formatter;
        _logger = logger;
    }

    public async Task<bool> WriteConversationTurnAsync(ConversationTurn turn, CancellationToken cancellationToken = default)
    {
        try
        {
            // Ensure directory exists
            var directory = new DirectoryInfo(_config.RootPath);
            if (!directory.Exists)
            {
                directory.Create();
                _logger.LogInformation("Created storage directory: {Path}", _config.RootPath);
            }

            // Generate filename and content
            var fileName = _formatter.GenerateFileName(turn);
            var filePath = Path.Combine(_config.RootPath, fileName);
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
            var directory = new DirectoryInfo(_config.RootPath);
            if (!directory.Exists)
            {
                directory.Create();
                _logger.LogInformation("Created storage directory: {Path}", _config.RootPath);
            }

            foreach (var turn in turnsList)
            {
                try
                {
                    var fileName = _formatter.GenerateFileName(turn);
                    var filePath = Path.Combine(_config.RootPath, fileName);
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
            var directory = new DirectoryInfo(_config.RootPath);
            if (!directory.Exists)
            {
                directory.Create();
            }

            // Try to write a test file
            var testFile = Path.Combine(_config.RootPath, ".health-check");
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
}
