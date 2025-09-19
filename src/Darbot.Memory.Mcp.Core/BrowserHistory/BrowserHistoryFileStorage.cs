using System.Text.Json;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Darbot.Memory.Mcp.Core.Configuration;

namespace Darbot.Memory.Mcp.Core.BrowserHistory;

/// <summary>
/// File system-based storage for browser history entries
/// </summary>
public class BrowserHistoryFileStorage : IBrowserHistoryStorage
{
    private readonly ILogger<BrowserHistoryFileStorage> _logger;
    private readonly DarbotConfiguration _config;
    private readonly string _historyBasePath;
    private readonly string _syncStatePath;

    public BrowserHistoryFileStorage(
        IOptions<DarbotConfiguration> config,
        ILogger<BrowserHistoryFileStorage> logger)
    {
        _logger = logger;
        _config = config.Value;
        _historyBasePath = Path.Combine(_config.Storage.BasePath, "browser-history");
        _syncStatePath = Path.Combine(_config.Storage.BasePath, "sync-state");

        EnsureDirectoriesExist();
    }

    public async Task<bool> StoreBrowserHistoryAsync(IEnumerable<BrowserHistoryEntry> entries, CancellationToken cancellationToken = default)
    {
        try
        {
            var groupedByProfile = entries.GroupBy(e => e.ProfileName);

            foreach (var profileGroup in groupedByProfile)
            {
                var profilePath = Path.Combine(_historyBasePath, SanitizeFileName(profileGroup.Key));
                Directory.CreateDirectory(profilePath);

                // Store entries in monthly files to avoid huge files
                var monthlyGroups = profileGroup.GroupBy(e => new { e.VisitTime.Year, e.VisitTime.Month });

                foreach (var monthGroup in monthlyGroups)
                {
                    var fileName = $"{monthGroup.Key.Year:D4}-{monthGroup.Key.Month:D2}.json";
                    var filePath = Path.Combine(profilePath, fileName);

                    var existingEntries = new List<BrowserHistoryEntry>();
                    if (File.Exists(filePath))
                    {
                        var existingJson = await File.ReadAllTextAsync(filePath, cancellationToken);
                        var options = new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                        };
                        existingEntries = JsonSerializer.Deserialize<List<BrowserHistoryEntry>>(existingJson, options) ?? new List<BrowserHistoryEntry>();
                    }

                    // Merge new entries with existing ones (avoid duplicates based on ID)
                    var existingIds = existingEntries.Select(e => e.Id).ToHashSet();
                    var newEntries = monthGroup.Where(e => !existingIds.Contains(e.Id)).ToList();

                    if (newEntries.Any())
                    {
                        existingEntries.AddRange(newEntries);
                        existingEntries.Sort((a, b) => b.VisitTime.CompareTo(a.VisitTime)); // Sort by visit time descending

                        var options = new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                        };

                        var json = JsonSerializer.Serialize(existingEntries, options);
                        await File.WriteAllTextAsync(filePath, json, cancellationToken);

                        _logger.LogInformation("Stored {Count} new browser history entries for profile {Profile} in {File}",
                            newEntries.Count, profileGroup.Key, fileName);
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing browser history entries");
            return false;
        }
    }

    public async Task<BrowserHistorySearchResponse> SearchBrowserHistoryAsync(BrowserHistorySearchRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var allEntries = new List<BrowserHistoryEntry>();

            // Determine which profiles to search
            var profilesToSearch = string.IsNullOrEmpty(request.ProfileName)
                ? Directory.GetDirectories(_historyBasePath)
                : new[] { Path.Combine(_historyBasePath, SanitizeFileName(request.ProfileName)) }.Where(Directory.Exists);

            foreach (var profileDir in profilesToSearch)
            {
                var profileEntries = await LoadProfileEntriesAsync(profileDir, request.FromDate, request.ToDate, cancellationToken);
                allEntries.AddRange(profileEntries);
            }

            // Apply filters
            var filteredEntries = allEntries.AsEnumerable();

            if (!string.IsNullOrEmpty(request.Url))
            {
                filteredEntries = filteredEntries.Where(e => e.Url.Contains(request.Url, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(request.Title))
            {
                filteredEntries = filteredEntries.Where(e => e.Title.Contains(request.Title, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(request.Domain))
            {
                filteredEntries = filteredEntries.Where(e =>
                {
                    try
                    {
                        var uri = new Uri(e.Url);
                        return uri.Host.Contains(request.Domain, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                });
            }

            if (request.FromDate.HasValue)
            {
                filteredEntries = filteredEntries.Where(e => e.VisitTime >= request.FromDate.Value);
            }

            if (request.ToDate.HasValue)
            {
                filteredEntries = filteredEntries.Where(e => e.VisitTime <= request.ToDate.Value);
            }

            // Apply sorting
            filteredEntries = request.SortBy.ToLowerInvariant() switch
            {
                "visitcount" => request.SortDescending
                    ? filteredEntries.OrderByDescending(e => e.VisitCount)
                    : filteredEntries.OrderBy(e => e.VisitCount),
                "title" => request.SortDescending
                    ? filteredEntries.OrderByDescending(e => e.Title)
                    : filteredEntries.OrderBy(e => e.Title),
                "url" => request.SortDescending
                    ? filteredEntries.OrderByDescending(e => e.Url)
                    : filteredEntries.OrderBy(e => e.Url),
                _ => request.SortDescending
                    ? filteredEntries.OrderByDescending(e => e.VisitTime)
                    : filteredEntries.OrderBy(e => e.VisitTime)
            };

            var totalCount = filteredEntries.Count();
            var results = filteredEntries.Skip(request.Skip).Take(request.Take).ToList();

            return new BrowserHistorySearchResponse
            {
                Results = results,
                TotalCount = totalCount,
                HasMore = request.Skip + request.Take < totalCount,
                Skip = request.Skip,
                Take = request.Take
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching browser history");
            return new BrowserHistorySearchResponse
            {
                Results = Array.Empty<BrowserHistoryEntry>(),
                TotalCount = 0,
                HasMore = false,
                Skip = request.Skip,
                Take = request.Take
            };
        }
    }

    public async Task<DateTime?> GetLastSyncTimeAsync(string profilePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var syncFileName = SanitizeFileName(profilePath) + ".sync";
            var syncFilePath = Path.Combine(_syncStatePath, syncFileName);

            if (!File.Exists(syncFilePath))
                return null;

            var syncData = await File.ReadAllTextAsync(syncFilePath, cancellationToken);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            var syncInfo = JsonSerializer.Deserialize<SyncInfo>(syncData, options);

            return syncInfo?.LastSyncTime;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting last sync time for profile {ProfilePath}", profilePath);
            return null;
        }
    }

    public async Task<bool> UpdateLastSyncTimeAsync(string profilePath, DateTime syncTime, CancellationToken cancellationToken = default)
    {
        try
        {
            var syncFileName = SanitizeFileName(profilePath) + ".sync";
            var syncFilePath = Path.Combine(_syncStatePath, syncFileName);

            var syncInfo = new SyncInfo
            {
                ProfilePath = profilePath,
                LastSyncTime = syncTime
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(syncInfo, options);
            await File.WriteAllTextAsync(syncFilePath, json, cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating last sync time for profile {ProfilePath}", profilePath);
            return false;
        }
    }

    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(Directory.Exists(_historyBasePath) && Directory.Exists(_syncStatePath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking browser history storage health");
            return Task.FromResult(false);
        }
    }

    private async Task<List<BrowserHistoryEntry>> LoadProfileEntriesAsync(string profileDir, DateTime? fromDate, DateTime? toDate, CancellationToken cancellationToken)
    {
        var entries = new List<BrowserHistoryEntry>();

        try
        {
            var jsonFiles = Directory.GetFiles(profileDir, "*.json");

            foreach (var jsonFile in jsonFiles)
            {
                // Parse the filename to get year/month
                var fileName = Path.GetFileNameWithoutExtension(jsonFile);
                if (!TryParseMonthFileName(fileName, out var year, out var month))
                    continue;

                // Skip files outside the date range
                if (fromDate.HasValue || toDate.HasValue)
                {
                    var fileMonth = new DateTime(year, month, 1);
                    var fileMonthEnd = fileMonth.AddMonths(1).AddDays(-1);

                    if (fromDate.HasValue && fileMonthEnd < fromDate.Value)
                        continue;
                    if (toDate.HasValue && fileMonth > toDate.Value)
                        continue;
                }

                var json = await File.ReadAllTextAsync(jsonFile, cancellationToken);
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                var fileEntries = JsonSerializer.Deserialize<List<BrowserHistoryEntry>>(json, options) ?? new List<BrowserHistoryEntry>();
                entries.AddRange(fileEntries);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading entries from profile directory {ProfileDir}", profileDir);
        }

        return entries;
    }

    private static bool TryParseMonthFileName(string fileName, out int year, out int month)
    {
        year = 0;
        month = 0;

        var parts = fileName.Split('-');
        if (parts.Length != 2)
            return false;

        return int.TryParse(parts[0], out year) && int.TryParse(parts[1], out month);
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", fileName.Split(invalid));
    }

    private void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(_historyBasePath);
        Directory.CreateDirectory(_syncStatePath);
    }

    private record SyncInfo
    {
        public required string ProfilePath { get; init; }
        public required DateTime LastSyncTime { get; init; }
    }
}