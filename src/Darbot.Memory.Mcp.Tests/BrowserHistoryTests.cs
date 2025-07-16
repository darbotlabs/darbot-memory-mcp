using Darbot.Memory.Mcp.Core.BrowserHistory;
using Darbot.Memory.Mcp.Core.Configuration;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Darbot.Memory.Mcp.Tests;

public class BrowserHistoryTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly Mock<ILogger<BrowserHistoryFileStorage>> _mockStorageLogger;
    private readonly Mock<ILogger<BrowserHistoryService>> _mockServiceLogger;
    private readonly Mock<IBrowserHistoryProvider> _mockProvider;
    private readonly IOptions<DarbotConfiguration> _options;

    public BrowserHistoryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);

        _mockStorageLogger = new Mock<ILogger<BrowserHistoryFileStorage>>();
        _mockServiceLogger = new Mock<ILogger<BrowserHistoryService>>();
        _mockProvider = new Mock<IBrowserHistoryProvider>();

        var config = new DarbotConfiguration
        {
            Storage = new StorageConfiguration
            {
                BasePath = _tempDirectory
            },
            BrowserHistory = new BrowserHistoryConfiguration
            {
                Enabled = true,
                SupportedBrowsers = "Edge",
                MaxEntriesPerSync = 1000
            }
        };
        _options = Options.Create(config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task BrowserHistoryFileStorage_StoreBrowserHistoryAsync_CreatesFilesSuccessfully()
    {
        // Arrange
        var storage = new BrowserHistoryFileStorage(_options, _mockStorageLogger.Object);
        var testEntries = new[]
        {
            new BrowserHistoryEntry
            {
                Id = "1",
                Url = "https://example.com",
                Title = "Example Website",
                VisitTime = DateTime.UtcNow,
                VisitCount = 1,
                ProfileName = "Default",
                ProfilePath = "/path/to/profile",
                Hash = "test-hash-1"
            },
            new BrowserHistoryEntry
            {
                Id = "2",
                Url = "https://google.com",
                Title = "Google",
                VisitTime = DateTime.UtcNow.AddMinutes(-10),
                VisitCount = 5,
                ProfileName = "Default",
                ProfilePath = "/path/to/profile",
                Hash = "test-hash-2"
            }
        };

        // Act
        var result = await storage.StoreBrowserHistoryAsync(testEntries);

        // Assert
        Assert.True(result);
        
        // Check that files were created
        var profileDir = Path.Combine(_tempDirectory, "browser-history", "Default");
        Assert.True(Directory.Exists(profileDir));
        
        var currentMonth = DateTime.UtcNow.ToString("yyyy-MM");
        var monthlyFile = Path.Combine(profileDir, $"{currentMonth}.json");
        Assert.True(File.Exists(monthlyFile));
    }

    [Fact]
    public async Task BrowserHistoryFileStorage_SearchBrowserHistoryAsync_ReturnsCorrectResults()
    {
        // Arrange
        var storage = new BrowserHistoryFileStorage(_options, _mockStorageLogger.Object);
        var testEntries = new[]
        {
            new BrowserHistoryEntry
            {
                Id = "1",
                Url = "https://example.com/page1",
                Title = "Example Page 1",
                VisitTime = DateTime.UtcNow,
                VisitCount = 1,
                ProfileName = "Default",
                ProfilePath = "/path/to/profile",
                Hash = "test-hash-1"
            },
            new BrowserHistoryEntry
            {
                Id = "2",
                Url = "https://google.com",
                Title = "Google Search",
                VisitTime = DateTime.UtcNow.AddMinutes(-10),
                VisitCount = 5,
                ProfileName = "Default",
                ProfilePath = "/path/to/profile",
                Hash = "test-hash-2"
            }
        };

        await storage.StoreBrowserHistoryAsync(testEntries);

        var searchRequest = new BrowserHistorySearchRequest
        {
            Url = "example.com",
            Take = 10
        };

        // Act
        var result = await storage.SearchBrowserHistoryAsync(searchRequest);

        // Assert
        Assert.Equal(1, result.Results.Count);
        Assert.Equal("https://example.com/page1", result.Results[0].Url);
        Assert.Equal("Example Page 1", result.Results[0].Title);
    }

    [Fact]
    public async Task BrowserHistoryFileStorage_GetLastSyncTimeAsync_ReturnsCorrectTime()
    {
        // Arrange
        var storage = new BrowserHistoryFileStorage(_options, _mockStorageLogger.Object);
        var profilePath = "/path/to/profile";
        var syncTime = DateTime.UtcNow;

        // Act - Update sync time
        var updateResult = await storage.UpdateLastSyncTimeAsync(profilePath, syncTime);
        Assert.True(updateResult);

        // Act - Get sync time
        var retrievedTime = await storage.GetLastSyncTimeAsync(profilePath);

        // Assert
        Assert.NotNull(retrievedTime);
        Assert.Equal(syncTime.ToString("O"), retrievedTime.Value.ToString("O"));
    }

    [Fact]
    public async Task BrowserHistoryService_SyncBrowserHistoryAsync_CallsProviderCorrectly()
    {
        // Arrange
        var storage = new BrowserHistoryFileStorage(_options, _mockStorageLogger.Object);
        var service = new BrowserHistoryService(_mockProvider.Object, storage, _mockServiceLogger.Object);

        var testProfiles = new[]
        {
            new BrowserProfile
            {
                Name = "Default",
                Path = "/path/to/default",
                DisplayName = "Default Profile",
                IsDefault = true,
                LastAccessed = DateTime.UtcNow
            }
        };

        var testEntries = new[]
        {
            new BrowserHistoryEntry
            {
                Id = "1",
                Url = "https://example.com",
                Title = "Example",
                VisitTime = DateTime.UtcNow,
                VisitCount = 1,
                ProfileName = "Default",
                ProfilePath = "/path/to/default",
                Hash = "test-hash"
            }
        };

        _mockProvider.Setup(p => p.IsBrowserAvailableAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(true);
        _mockProvider.Setup(p => p.GetProfilesAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(testProfiles);
        _mockProvider.Setup(p => p.ReadHistoryAsync(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(testEntries);

        var syncRequest = new BrowserHistorySyncRequest
        {
            FullSync = true
        };

        // Act
        var result = await service.SyncBrowserHistoryAsync(syncRequest);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(1, result.NewEntriesCount);
        Assert.Single(result.ProcessedProfiles);
        Assert.Equal("Default", result.ProcessedProfiles[0]);

        // Verify provider methods were called
        _mockProvider.Verify(p => p.IsBrowserAvailableAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockProvider.Verify(p => p.GetProfilesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockProvider.Verify(p => p.ReadHistoryAsync("/path/to/default", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BrowserHistoryService_SearchBrowserHistoryAsync_ReturnsStorageResults()
    {
        // Arrange
        var storage = new BrowserHistoryFileStorage(_options, _mockStorageLogger.Object);
        var service = new BrowserHistoryService(_mockProvider.Object, storage, _mockServiceLogger.Object);

        // First, store some test data
        var testEntries = new[]
        {
            new BrowserHistoryEntry
            {
                Id = "1",
                Url = "https://github.com",
                Title = "GitHub",
                VisitTime = DateTime.UtcNow,
                VisitCount = 10,
                ProfileName = "Default",
                ProfilePath = "/path/to/profile",
                Hash = "test-hash"
            }
        };

        await storage.StoreBrowserHistoryAsync(testEntries);

        var searchRequest = new BrowserHistorySearchRequest
        {
            Domain = "github.com",
            Take = 10
        };

        // Act
        var result = await service.SearchBrowserHistoryAsync(searchRequest);

        // Assert
        Assert.Single(result.Results);
        Assert.Equal("https://github.com", result.Results[0].Url);
        Assert.Equal("GitHub", result.Results[0].Title);
    }

    [Fact]
    public async Task BrowserHistoryService_GetBrowserProfilesAsync_ReturnsProviderProfiles()
    {
        // Arrange
        var storage = new BrowserHistoryFileStorage(_options, _mockStorageLogger.Object);
        var service = new BrowserHistoryService(_mockProvider.Object, storage, _mockServiceLogger.Object);

        var testProfiles = new[]
        {
            new BrowserProfile
            {
                Name = "Default",
                Path = "/path/to/default",
                DisplayName = "Default Profile",
                IsDefault = true,
                LastAccessed = DateTime.UtcNow
            },
            new BrowserProfile
            {
                Name = "Profile 1",
                Path = "/path/to/profile1",
                DisplayName = "Work Profile",
                IsDefault = false,
                LastAccessed = DateTime.UtcNow.AddDays(-1)
            }
        };

        _mockProvider.Setup(p => p.GetProfilesAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(testProfiles);

        // Act
        var result = await service.GetBrowserProfilesAsync();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.Name == "Default" && p.IsDefault);
        Assert.Contains(result, p => p.Name == "Profile 1" && !p.IsDefault);

        _mockProvider.Verify(p => p.GetProfilesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}