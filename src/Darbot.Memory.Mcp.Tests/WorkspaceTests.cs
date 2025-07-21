using Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Darbot.Memory.Mcp.Core.Configuration;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Darbot.Memory.Mcp.Core.Services;
using Darbot.Memory.Mcp.Core.Plugins;
using Darbot.Memory.Mcp.Storage.Providers;
using Moq;

namespace Darbot.Memory.Mcp.Tests;

public class WorkspaceTests : IDisposable
{
    private readonly Mock<ILogger<WorkspaceFileSystemStorageProvider>> _storageLoggerMock;
    private readonly Mock<ILogger<WorkspaceService>> _serviceLoggerMock;
    private readonly Mock<ILogger<PluginRegistry>> _pluginLoggerMock;
    private readonly Mock<ILogger<OneNotePlugin>> _oneNoteLoggerMock;
    private readonly Mock<ILogger<GitHubPlugin>> _githubLoggerMock;
    private readonly Mock<IConversationFormatter> _formatterMock;
    private readonly IOptions<DarbotConfiguration> _configOptions;
    private readonly string _tempPath;

    public WorkspaceTests()
    {
        _storageLoggerMock = new Mock<ILogger<WorkspaceFileSystemStorageProvider>>();
        _serviceLoggerMock = new Mock<ILogger<WorkspaceService>>();
        _pluginLoggerMock = new Mock<ILogger<PluginRegistry>>();
        _oneNoteLoggerMock = new Mock<ILogger<OneNotePlugin>>();
        _githubLoggerMock = new Mock<ILogger<GitHubPlugin>>();
        _formatterMock = new Mock<IConversationFormatter>();

        _tempPath = Path.Combine(Path.GetTempPath(), "darbot-workspace-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempPath);

        var config = new DarbotConfiguration
        {
            Storage = new StorageConfiguration
            {
                Provider = "FileSystem",
                FileSystem = new FileSystemConfiguration
                {
                    RootPath = _tempPath
                }
            }
        };

        _configOptions = Options.Create(config);
    }

    [Fact]
    public async Task PluginRegistry_RegisterPlugin_AddsPluginSuccessfully()
    {
        // Arrange
        var registry = new PluginRegistry(_pluginLoggerMock.Object);
        var plugin = new OneNotePlugin(_oneNoteLoggerMock.Object);

        // Act
        registry.RegisterPlugin(plugin);
        await Task.Delay(10); // Satisfy async warning

        // Assert
        var plugins = registry.GetAllPlugins();
        Assert.Single(plugins);
        Assert.Equal("OneNote", plugins[0].Name);
    }

    [Fact]
    public async Task PluginRegistry_GetAvailablePlugins_ReturnsOnlyAvailablePlugins()
    {
        // Arrange
        var registry = new PluginRegistry(_pluginLoggerMock.Object);
        var oneNotePlugin = new OneNotePlugin(_oneNoteLoggerMock.Object);
        var githubPlugin = new GitHubPlugin(_githubLoggerMock.Object);
        
        registry.RegisterPlugin(oneNotePlugin);
        registry.RegisterPlugin(githubPlugin);

        // Act
        var availablePlugins = await registry.GetAvailablePluginsAsync();

        // Assert
        Assert.Equal(2, availablePlugins.Count);
        Assert.Contains(availablePlugins, p => p.Name == "OneNote");
        Assert.Contains(availablePlugins, p => p.Name == "GitHub");
    }

    [Fact]
    public async Task OneNotePlugin_CaptureState_ReturnsValidPluginData()
    {
        // Arrange
        var plugin = new OneNotePlugin(_oneNoteLoggerMock.Object);

        // Act
        var result = await plugin.CaptureStateAsync();

        // Assert
        Assert.Equal("OneNote", result.Type);
        Assert.NotNull(result.Data);
        Assert.True(result.Metadata.ContainsKey("Count"));
        
        // Verify the data is actually OneNote notebooks
        Assert.IsAssignableFrom<IReadOnlyList<OneNoteNotebook>>(result.Data);
        var notebooks = (IReadOnlyList<OneNoteNotebook>)result.Data;
        Assert.True(notebooks.Count > 0);
        Assert.Contains(notebooks, n => n.Name == "Project Notes");
    }

    [Fact]
    public async Task GitHubPlugin_CaptureState_ReturnsValidPluginData()
    {
        // Arrange
        var plugin = new GitHubPlugin(_githubLoggerMock.Object);

        // Act
        var result = await plugin.CaptureStateAsync();

        // Assert
        Assert.Equal("GitHub", result.Type);
        Assert.NotNull(result.Data);
        Assert.True(result.Metadata.ContainsKey("Count"));
        Assert.NotEmpty(result.Links);
        
        // Verify the data is actually GitHub repositories
        Assert.IsAssignableFrom<IReadOnlyList<GitHubRepository>>(result.Data);
        var repos = (IReadOnlyList<GitHubRepository>)result.Data;
        Assert.True(repos.Count > 0);
        Assert.Contains(repos, r => r.Name == "darbot-memory-mcp");
    }

    [Fact]
    public async Task WorkspaceFileSystemStorageProvider_CaptureCurrentWorkspace_CreatesValidWorkspace()
    {
        // Arrange
        var registry = new PluginRegistry(_pluginLoggerMock.Object);
        registry.RegisterPlugin(new OneNotePlugin(_oneNoteLoggerMock.Object));
        registry.RegisterPlugin(new GitHubPlugin(_githubLoggerMock.Object));
        
        var provider = new WorkspaceFileSystemStorageProvider(
            _configOptions, _formatterMock.Object, _storageLoggerMock.Object, registry);

        var options = new CaptureOptions
        {
            IncludeHistory = true,
            IncludeSensitive = false
        };

        // Act
        var workspace = await provider.CaptureCurrentWorkspaceAsync(options);

        // Assert
        Assert.NotNull(workspace);
        Assert.NotEmpty(workspace.WorkspaceId);
        Assert.NotEmpty(workspace.Name);
        Assert.NotNull(workspace.Device);
        Assert.NotNull(workspace.BrowserState);
        Assert.NotNull(workspace.ApplicationState);
        Assert.NotNull(workspace.Conversations);
        
        // Verify device info is captured
        Assert.Equal(Environment.MachineName, workspace.Device.DeviceName);
        Assert.Equal(Environment.UserName, workspace.Device.UserName);
        
        // Verify some data is captured from plugins
        Assert.True(workspace.ApplicationState.OneNoteNotebooks.Count > 0 || 
                   workspace.ApplicationState.GitHubRepos.Count > 0);
    }

    [Fact]
    public async Task WorkspaceFileSystemStorageProvider_StoreAndGetWorkspace_RoundTrip()
    {
        // Arrange
        var provider = new WorkspaceFileSystemStorageProvider(
            _configOptions, _formatterMock.Object, _storageLoggerMock.Object);

        var workspace = new WorkspaceContext
        {
            WorkspaceId = "test-workspace-123",
            Name = "Test Workspace",
            CreatedUtc = DateTime.UtcNow,
            LastAccessedUtc = DateTime.UtcNow,
            Device = new DeviceInfo
            {
                DeviceId = "test-device",
                DeviceName = "Test Device",
                OperatingSystem = "Test OS",
                Architecture = "x64",
                UserName = "TestUser",
                CaptureTime = DateTime.UtcNow
            },
            BrowserState = new BrowserState
            {
                Profiles = Array.Empty<BrowserProfile>().AsReadOnly(),
                OpenTabs = Array.Empty<BrowserTab>().AsReadOnly(),
                Bookmarks = Array.Empty<BrowserBookmark>().AsReadOnly(),
                RecentHistory = Array.Empty<BrowserHistoryEntry>().AsReadOnly(),
                Extensions = Array.Empty<BrowserExtension>().AsReadOnly(),
                Settings = new Dictionary<string, string>()
            },
            ApplicationState = new ApplicationState
            {
                OneNoteNotebooks = Array.Empty<OneNoteNotebook>().AsReadOnly(),
                StickyNotes = Array.Empty<StickyNote>().AsReadOnly(),
                GitHubRepos = Array.Empty<GitHubRepository>().AsReadOnly(),
                VSCodeWorkspaces = Array.Empty<VSCodeWorkspace>().AsReadOnly(),
                RunningApps = Array.Empty<OpenApplication>().AsReadOnly()
            },
            Conversations = Array.Empty<ConversationReference>().AsReadOnly()
        };

        // Act
        var storeResult = await provider.StoreWorkspaceAsync(workspace);
        var retrievedWorkspace = await provider.GetWorkspaceAsync(workspace.WorkspaceId);

        // Assert
        Assert.True(storeResult);
        Assert.NotNull(retrievedWorkspace);
        Assert.Equal(workspace.WorkspaceId, retrievedWorkspace.WorkspaceId);
        Assert.Equal(workspace.Name, retrievedWorkspace.Name);
        Assert.Equal(workspace.Device.DeviceName, retrievedWorkspace.Device.DeviceName);
    }

    [Fact]
    public async Task WorkspaceService_CaptureWorkspace_ReturnsSuccessfulResponse()
    {
        // Arrange
        var registry = new PluginRegistry(_pluginLoggerMock.Object);
        var provider = new WorkspaceFileSystemStorageProvider(
            _configOptions, _formatterMock.Object, _storageLoggerMock.Object, registry);
        var service = new WorkspaceService(provider, registry, _serviceLoggerMock.Object);

        var request = new CaptureWorkspaceRequest
        {
            Name = "Test Capture Workspace",
            Options = new CaptureOptions { IncludeHistory = true }
        };

        // Act
        var response = await service.CaptureWorkspaceAsync(request);

        // Assert
        Assert.True(response.Success);
        Assert.NotEmpty(response.WorkspaceId);
        Assert.NotNull(response.Message);
        Assert.True(response.Message.Contains("successfully"));
        Assert.True(response.Message.Contains(request.Name));
        Assert.True(response.ComponentsCount >= 0);
    }

    [Fact]
    public async Task WorkspaceService_ListWorkspaces_ReturnsWorkspaces()
    {
        // Arrange
        var registry = new PluginRegistry(_pluginLoggerMock.Object);
        var provider = new WorkspaceFileSystemStorageProvider(
            _configOptions, _formatterMock.Object, _storageLoggerMock.Object, registry);
        var service = new WorkspaceService(provider, registry, _serviceLoggerMock.Object);

        // Create a test workspace first
        var captureRequest = new CaptureWorkspaceRequest
        {
            Name = "List Test Workspace",
            Options = new CaptureOptions()
        };
        
        await service.CaptureWorkspaceAsync(captureRequest);

        var listRequest = new ListWorkspacesRequest
        {
            Skip = 0,
            Take = 10
        };

        // Act
        var response = await service.ListWorkspacesAsync(listRequest);

        // Assert
        Assert.NotNull(response);
        Assert.True(response.Workspaces.Count > 0);
        Assert.True(response.TotalCount > 0);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            Directory.Delete(_tempPath, recursive: true);
        }
    }
}