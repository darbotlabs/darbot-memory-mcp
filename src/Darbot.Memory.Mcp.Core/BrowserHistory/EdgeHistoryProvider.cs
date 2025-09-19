using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Darbot.Memory.Mcp.Core.BrowserHistory;

/// <summary>
/// Provider for reading Microsoft Edge browser history
/// </summary>
public class EdgeHistoryProvider : IBrowserHistoryProvider
{
    private readonly ILogger<EdgeHistoryProvider> _logger;

    public EdgeHistoryProvider(ILogger<EdgeHistoryProvider> logger)
    {
        _logger = logger;
    }

    public string BrowserName => "Microsoft Edge";

    public async Task<IReadOnlyList<BrowserProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var userDataPath = GetEdgeUserDataPath();
            if (!Directory.Exists(userDataPath))
            {
                _logger.LogWarning("Edge user data directory not found: {Path}", userDataPath);
                return Array.Empty<BrowserProfile>();
            }

            var profiles = new List<BrowserProfile>();

            // Check for default profile
            var defaultProfilePath = Path.Combine(userDataPath, "Default");
            if (Directory.Exists(defaultProfilePath))
            {
                var defaultProfile = await CreateBrowserProfileAsync(defaultProfilePath, "Default", "Default Profile", true, cancellationToken);
                if (defaultProfile != null)
                    profiles.Add(defaultProfile);
            }

            // Check for additional profiles
            var profileDirs = Directory.GetDirectories(userDataPath, "Profile *");
            foreach (var profileDir in profileDirs)
            {
                var profileName = Path.GetFileName(profileDir);
                var displayName = await GetProfileDisplayNameAsync(profileDir, cancellationToken) ?? profileName;

                var profile = await CreateBrowserProfileAsync(profileDir, profileName, displayName, false, cancellationToken);
                if (profile != null)
                    profiles.Add(profile);
            }

            _logger.LogInformation("Found {Count} Edge profiles", profiles.Count);
            return profiles;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Edge profiles");
            return Array.Empty<BrowserProfile>();
        }
    }

    public async Task<IReadOnlyList<BrowserHistoryEntry>> ReadHistoryAsync(string profilePath, DateTime? since = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var historyDbPath = Path.Combine(profilePath, "History");
            if (!File.Exists(historyDbPath))
            {
                _logger.LogWarning("Edge history database not found: {Path}", historyDbPath);
                return Array.Empty<BrowserHistoryEntry>();
            }

            // Create a temporary copy of the history database to avoid locking issues
            var tempDbPath = Path.GetTempFileName();
            File.Copy(historyDbPath, tempDbPath, true);

            try
            {
                var entries = new List<BrowserHistoryEntry>();
                var connectionString = $"Data Source={tempDbPath};Mode=ReadOnly;";

                using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync(cancellationToken);

                var query = @"
                    SELECT 
                        u.id,
                        u.url,
                        u.title,
                        v.visit_time,
                        u.visit_count
                    FROM urls u
                    JOIN visits v ON u.id = v.url
                    WHERE u.hidden = 0";

                var parameters = new List<SqliteParameter>();

                if (since.HasValue)
                {
                    // Chrome/Edge stores timestamps as microseconds since Windows epoch (1601-01-01)
                    var windowsEpoch = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    var microsecondsSinceEpoch = (long)((since.Value - windowsEpoch).TotalMicroseconds);

                    query += " AND v.visit_time > @since";
                    parameters.Add(new SqliteParameter("@since", microsecondsSinceEpoch));
                }

                query += " ORDER BY v.visit_time DESC";

                using var command = new SqliteCommand(query, connection);
                foreach (var param in parameters)
                    command.Parameters.Add(param);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                var profileName = Path.GetFileName(profilePath);
                var windowsEpochForConversion = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetInt64(0).ToString(); // id column
                    var url = reader.GetString(1); // url column
                    var title = reader.IsDBNull(2) ? url : reader.GetString(2); // title column
                    var visitTimeMicroseconds = reader.GetInt64(3); // visit_time column
                    var visitCount = reader.GetInt32(4); // visit_count column

                    // Convert Chrome timestamp to DateTime
                    var visitTime = windowsEpochForConversion.AddMicroseconds(visitTimeMicroseconds);

                    var entry = new BrowserHistoryEntry
                    {
                        Id = id,
                        Url = url,
                        Title = title,
                        VisitTime = visitTime,
                        VisitCount = visitCount,
                        ProfileName = profileName,
                        ProfilePath = profilePath,
                        Hash = CalculateEntryHash(url, title, visitTime, visitCount)
                    };

                    entries.Add(entry);
                }

                _logger.LogInformation("Read {Count} history entries from Edge profile {Profile}", entries.Count, profileName);
                return entries;
            }
            finally
            {
                // Clean up temporary database file
                try
                {
                    File.Delete(tempDbPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temporary database file: {Path}", tempDbPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading Edge history from {ProfilePath}", profilePath);
            return Array.Empty<BrowserHistoryEntry>();
        }
    }

    public Task<bool> IsBrowserAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var userDataPath = GetEdgeUserDataPath();
            return Task.FromResult(Directory.Exists(userDataPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking Edge availability");
            return Task.FromResult(false);
        }
    }

    private static string GetEdgeUserDataPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Microsoft", "Edge", "User Data");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "Microsoft Edge");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".config", "microsoft-edge");
        }
        else
        {
            throw new PlatformNotSupportedException("Unsupported operating system");
        }
    }

    private Task<BrowserProfile?> CreateBrowserProfileAsync(string profilePath, string profileName, string displayName, bool isDefault, CancellationToken cancellationToken)
    {
        try
        {
            var lastAccessed = Directory.GetLastWriteTime(profilePath);

            var profile = new BrowserProfile
            {
                Name = profileName,
                Path = profilePath,
                DisplayName = displayName,
                IsDefault = isDefault,
                LastAccessed = lastAccessed
            };

            return Task.FromResult<BrowserProfile?>(profile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error creating browser profile for {Path}", profilePath);
            return Task.FromResult<BrowserProfile?>(null);
        }
    }

    private async Task<string?> GetProfileDisplayNameAsync(string profilePath, CancellationToken cancellationToken)
    {
        try
        {
            var preferencesPath = Path.Combine(profilePath, "Preferences");
            if (!File.Exists(preferencesPath))
                return null;

            var json = await File.ReadAllTextAsync(preferencesPath, cancellationToken);
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("profile", out var profile) &&
                profile.TryGetProperty("name", out var name))
            {
                return name.GetString();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading profile display name from {Path}", profilePath);
            return null;
        }
    }

    private static string CalculateEntryHash(string url, string title, DateTime visitTime, int visitCount)
    {
        var content = $"{url}|{title}|{visitTime:O}|{visitCount}";
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
        return "sha256-" + Convert.ToBase64String(hashBytes);
    }
}