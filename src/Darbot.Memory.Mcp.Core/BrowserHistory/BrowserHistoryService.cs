using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Microsoft.Extensions.Logging;

namespace Darbot.Memory.Mcp.Core.BrowserHistory;

/// <summary>
/// Main service for managing browser history operations
/// </summary>
public class BrowserHistoryService : IBrowserHistoryService
{
    private readonly IBrowserHistoryProvider _browserProvider;
    private readonly IBrowserHistoryStorage _historyStorage;
    private readonly ILogger<BrowserHistoryService> _logger;

    public BrowserHistoryService(
        IBrowserHistoryProvider browserProvider,
        IBrowserHistoryStorage historyStorage,
        ILogger<BrowserHistoryService> logger)
    {
        _browserProvider = browserProvider;
        _historyStorage = historyStorage;
        _logger = logger;
    }

    public async Task<BrowserHistorySyncResponse> SyncBrowserHistoryAsync(BrowserHistorySyncRequest request, CancellationToken cancellationToken = default)
    {
        var syncTime = DateTime.UtcNow;
        var processedProfiles = new List<string>();
        var errors = new List<string>();
        var totalNewEntries = 0;
        var totalUpdatedEntries = 0;

        try
        {
            _logger.LogInformation("Starting browser history sync. FullSync: {FullSync}", request.FullSync);

            // Check if browser is available
            if (!await _browserProvider.IsBrowserAvailableAsync(cancellationToken))
            {
                var error = $"{_browserProvider.BrowserName} is not available or installed";
                _logger.LogWarning(error);
                return new BrowserHistorySyncResponse
                {
                    Success = false,
                    NewEntriesCount = 0,
                    UpdatedEntriesCount = 0,
                    SyncTime = syncTime,
                    ProcessedProfiles = Array.Empty<string>(),
                    Errors = new[] { error },
                    Message = error
                };
            }

            // Get all available profiles
            var profiles = await _browserProvider.GetProfilesAsync(cancellationToken);
            if (!profiles.Any())
            {
                var error = "No browser profiles found";
                _logger.LogWarning(error);
                return new BrowserHistorySyncResponse
                {
                    Success = false,
                    NewEntriesCount = 0,
                    UpdatedEntriesCount = 0,
                    SyncTime = syncTime,
                    ProcessedProfiles = Array.Empty<string>(),
                    Errors = new[] { error },
                    Message = error
                };
            }

            // Filter profiles if specific ones were requested
            if (request.ProfileNames.Any())
            {
                profiles = profiles.Where(p => request.ProfileNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            }

            // Process each profile
            foreach (var profile in profiles)
            {
                try
                {
                    _logger.LogInformation("Processing profile: {ProfileName} ({ProfilePath})", profile.Name, profile.Path);

                    // Determine the "since" timestamp for delta sync
                    DateTime? since = null;
                    if (!request.FullSync)
                    {
                        since = request.LastSyncTime ?? await _historyStorage.GetLastSyncTimeAsync(profile.Path, cancellationToken);
                    }

                    // Read history from the browser
                    var historyEntries = await _browserProvider.ReadHistoryAsync(profile.Path, since, cancellationToken);
                    
                    if (historyEntries.Any())
                    {
                        // Store the history entries
                        var stored = await _historyStorage.StoreBrowserHistoryAsync(historyEntries, cancellationToken);
                        
                        if (stored)
                        {
                            // Update the last sync time for this profile
                            await _historyStorage.UpdateLastSyncTimeAsync(profile.Path, syncTime, cancellationToken);
                            
                            totalNewEntries += historyEntries.Count;
                            processedProfiles.Add(profile.Name);
                            
                            _logger.LogInformation("Successfully synced {Count} entries from profile {ProfileName}", 
                                historyEntries.Count, profile.Name);
                        }
                        else
                        {
                            var error = $"Failed to store history entries for profile {profile.Name}";
                            errors.Add(error);
                            _logger.LogError(error);
                        }
                    }
                    else
                    {
                        processedProfiles.Add(profile.Name);
                        _logger.LogInformation("No new entries found for profile {ProfileName}", profile.Name);
                    }
                }
                catch (Exception ex)
                {
                    var error = $"Error processing profile {profile.Name}: {ex.Message}";
                    errors.Add(error);
                    _logger.LogError(ex, "Error processing profile {ProfileName}", profile.Name);
                }
            }

            var success = processedProfiles.Any() && !errors.Any();
            var message = success 
                ? $"Successfully synced browser history from {processedProfiles.Count} profiles"
                : errors.Any() 
                    ? $"Sync completed with {errors.Count} errors"
                    : "No profiles were processed";

            return new BrowserHistorySyncResponse
            {
                Success = success,
                NewEntriesCount = totalNewEntries,
                UpdatedEntriesCount = totalUpdatedEntries,
                SyncTime = syncTime,
                ProcessedProfiles = processedProfiles,
                Errors = errors,
                Message = message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during browser history sync");
            
            return new BrowserHistorySyncResponse
            {
                Success = false,
                NewEntriesCount = totalNewEntries,
                UpdatedEntriesCount = totalUpdatedEntries,
                SyncTime = syncTime,
                ProcessedProfiles = processedProfiles,
                Errors = new[] { ex.Message },
                Message = "Browser history sync failed"
            };
        }
    }

    public async Task<BrowserHistorySearchResponse> SearchBrowserHistoryAsync(BrowserHistorySearchRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Searching browser history with criteria");
            return await _historyStorage.SearchBrowserHistoryAsync(request, cancellationToken);
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

    public async Task<IReadOnlyList<BrowserProfile>> GetBrowserProfilesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Getting browser profiles");
            return await _browserProvider.GetProfilesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting browser profiles");
            return Array.Empty<BrowserProfile>();
        }
    }
}