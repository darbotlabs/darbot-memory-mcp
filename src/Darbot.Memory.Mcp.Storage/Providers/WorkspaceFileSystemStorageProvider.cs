using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Darbot.Memory.Mcp.Core.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Darbot.Memory.Mcp.Storage.Providers;

/// <summary>
/// File system-based workspace storage provider that extends conversation storage
/// </summary>
public class WorkspaceFileSystemStorageProvider : FileSystemStorageProvider, IWorkspaceStorageProvider
{
    private readonly string _workspacesPath;
    private readonly IPluginRegistry? _pluginRegistry;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<WorkspaceFileSystemStorageProvider> _logger;

    public WorkspaceFileSystemStorageProvider(
        IOptions<DarbotConfiguration> options,
        IConversationFormatter formatter,
        ILogger<WorkspaceFileSystemStorageProvider> logger,
        IPluginRegistry? pluginRegistry = null)
        : base(options, formatter, logger)
    {
        _workspacesPath = Path.Combine(options.Value.Storage.FileSystem.RootPath, "workspaces");
        _pluginRegistry = pluginRegistry;
        _logger = logger;
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Ensure workspaces directory exists
        Directory.CreateDirectory(_workspacesPath);
    }

    public async Task<WorkspaceContext> CaptureCurrentWorkspaceAsync(CaptureOptions options, CancellationToken cancellationToken = default)
    {
        var workspaceId = $"ws-{Guid.NewGuid()}";
        
        var deviceInfo = await CaptureDeviceInfoAsync(cancellationToken);
        var browserState = await CaptureBrowserStateAsync(cancellationToken);
        var applicationState = await CaptureApplicationStateAsync(cancellationToken);
        var conversations = await CaptureConversationReferencesAsync(options.HistoryWindow, cancellationToken);

        var workspace = new WorkspaceContext
        {
            WorkspaceId = workspaceId,
            Name = $"Workspace {DateTime.UtcNow:yyyy-MM-dd HH:mm}",
            CreatedUtc = DateTime.UtcNow,
            LastAccessedUtc = DateTime.UtcNow,
            Device = deviceInfo,
            BrowserState = browserState,
            ApplicationState = applicationState,
            Conversations = conversations
        };

        return workspace;
    }

    public async Task<bool> StoreWorkspaceAsync(WorkspaceContext workspace, CancellationToken cancellationToken = default)
    {
        try
        {
            var workspaceFile = Path.Combine(_workspacesPath, $"{workspace.WorkspaceId}.json");
            var workspaceJson = JsonSerializer.Serialize(workspace, _jsonOptions);
            
            await File.WriteAllTextAsync(workspaceFile, workspaceJson, cancellationToken);
            
            // Also create a markdown summary for human readability
            var markdownFile = Path.Combine(_workspacesPath, $"{workspace.WorkspaceId}.md");
            var markdown = GenerateWorkspaceMarkdown(workspace);
            await File.WriteAllTextAsync(markdownFile, markdown, cancellationToken);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store workspace: {WorkspaceId}", workspace.WorkspaceId);
            return false;
        }
    }

    public async Task<bool> RestoreWorkspaceAsync(string workspaceId, RestoreOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            var workspace = await GetWorkspaceAsync(workspaceId, cancellationToken);
            if (workspace == null)
            {
                _logger.LogWarning("Workspace not found for restore: {WorkspaceId}", workspaceId);
                return false;
            }

            // In a real implementation, this would actually restore the workspace state
            // For now, we'll just log the restoration attempt
            _logger.LogInformation("Simulating workspace restoration: {WorkspaceId} with mode {Mode}", 
                workspaceId, options.Mode);

            await Task.Delay(100, cancellationToken); // Simulate restoration work
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore workspace: {WorkspaceId}", workspaceId);
            return false;
        }
    }

    public async Task<WorkspaceContext?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var workspaceFile = Path.Combine(_workspacesPath, $"{workspaceId}.json");
            if (!File.Exists(workspaceFile))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(workspaceFile, cancellationToken);
            return JsonSerializer.Deserialize<WorkspaceContext>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get workspace: {WorkspaceId}", workspaceId);
            return null;
        }
    }

    public async Task<ListWorkspacesResponse> ListWorkspacesAsync(ListWorkspacesRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var workspaceFiles = Directory.GetFiles(_workspacesPath, "*.json")
                .Where(f => !Path.GetFileName(f).StartsWith("temp_"))
                .ToArray();

            var summaries = new List<WorkspaceSummary>();

            foreach (var file in workspaceFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file, cancellationToken);
                    var workspace = JsonSerializer.Deserialize<WorkspaceContext>(json, _jsonOptions);
                    
                    if (workspace != null)
                    {
                        var summary = new WorkspaceSummary
                        {
                            WorkspaceId = workspace.WorkspaceId,
                            Name = workspace.Name,
                            CreatedUtc = workspace.CreatedUtc,
                            LastAccessedUtc = workspace.LastAccessedUtc,
                            Device = workspace.Device,
                            ConversationCount = workspace.Conversations.Count,
                            BrowserTabCount = workspace.BrowserState.OpenTabs.Count,
                            ApplicationCount = workspace.ApplicationState.RunningApps.Count
                        };
                        
                        summaries.Add(summary);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process workspace file: {File}", file);
                }
            }

            // Apply filtering and sorting
            var filtered = ApplyWorkspaceFiltering(summaries, request);
            var sorted = ApplyWorkspaceSorting(filtered, request);
            var paged = sorted.Skip(request.Skip).Take(request.Take).ToList();

            return new ListWorkspacesResponse
            {
                Workspaces = paged.AsReadOnly(),
                TotalCount = sorted.Count(),
                HasMore = sorted.Count() > request.Skip + request.Take,
                Skip = request.Skip,
                Take = request.Take
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list workspaces");
            return new ListWorkspacesResponse
            {
                Workspaces = Array.Empty<WorkspaceSummary>().AsReadOnly(),
                TotalCount = 0,
                HasMore = false,
                Skip = request.Skip,
                Take = request.Take
            };
        }
    }

    public async Task<bool> DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var workspaceFile = Path.Combine(_workspacesPath, $"{workspaceId}.json");
            var markdownFile = Path.Combine(_workspacesPath, $"{workspaceId}.md");

            var deleted = false;
            if (File.Exists(workspaceFile))
            {
                File.Delete(workspaceFile);
                deleted = true;
            }

            if (File.Exists(markdownFile))
            {
                File.Delete(markdownFile);
            }

            return deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete workspace: {WorkspaceId}", workspaceId);
            return false;
        }
    }

    public async Task<BrowserState> CaptureBrowserStateAsync(CancellationToken cancellationToken = default)
    {
        // Simulate browser state capture
        await Task.Delay(50, cancellationToken);
        
        return new BrowserState
        {
            Profiles = new List<BrowserProfile>
            {
                new()
                {
                    Name = "Default",
                    Path = GetBrowserProfilePath(),
                    DisplayName = "Default Profile",
                    IsDefault = true,
                    LastAccessed = DateTime.UtcNow
                }
            }.AsReadOnly(),
            OpenTabs = new List<BrowserTab>
            {
                new()
                {
                    Url = "https://github.com/darbotlabs/darbot-memory-mcp",
                    Title = "Enhanced Memory MCP Implementation",
                    TabIndex = 0,
                    IsActive = true,
                    IsPinned = false,
                    ProfileName = "Default"
                }
            }.AsReadOnly(),
            Bookmarks = Array.Empty<BrowserBookmark>().AsReadOnly(),
            RecentHistory = Array.Empty<BrowserHistoryEntry>().AsReadOnly(),
            Extensions = Array.Empty<BrowserExtension>().AsReadOnly(),
            Settings = new Dictionary<string, string>()
        };
    }

    public async Task<bool> RestoreBrowserStateAsync(BrowserState state, CancellationToken cancellationToken = default)
    {
        // Simulate browser restoration
        _logger.LogInformation("Simulating browser state restoration with {TabCount} tabs", state.OpenTabs.Count);
        await Task.Delay(100, cancellationToken);
        return true;
    }

    public async Task<ApplicationState> CaptureApplicationStateAsync(CancellationToken cancellationToken = default)
    {
        // Use plugins if available
        var oneNoteNotebooks = await CaptureOneNoteDataAsync(cancellationToken);
        var gitHubRepos = await CaptureGitHubDataAsync(cancellationToken);
        
        return new ApplicationState
        {
            OneNoteNotebooks = oneNoteNotebooks,
            StickyNotes = Array.Empty<StickyNote>().AsReadOnly(),
            GitHubRepos = gitHubRepos,
            VSCodeWorkspaces = Array.Empty<VSCodeWorkspace>().AsReadOnly(),
            RunningApps = await CaptureRunningApplicationsAsync(cancellationToken)
        };
    }

    public async Task<bool> RestoreApplicationStateAsync(ApplicationState state, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Simulating application state restoration");
        await Task.Delay(100, cancellationToken);
        return true;
    }

    private async Task<DeviceInfo> CaptureDeviceInfoAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(10, cancellationToken);
        
        return new DeviceInfo
        {
            DeviceId = Environment.MachineName,
            DeviceName = Environment.MachineName,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            UserName = Environment.UserName,
            CaptureTime = DateTime.UtcNow
        };
    }

    private async Task<IReadOnlyList<ConversationReference>> CaptureConversationReferencesAsync(
        TimeSpan historyWindow, CancellationToken cancellationToken)
    {
        await Task.Delay(20, cancellationToken);
        
        // In a real implementation, this would scan the conversations directory
        // For now, return empty list
        return Array.Empty<ConversationReference>().AsReadOnly();
    }

    private async Task<IReadOnlyList<OneNoteNotebook>> CaptureOneNoteDataAsync(CancellationToken cancellationToken)
    {
        if (_pluginRegistry != null)
        {
            var oneNotePlugin = _pluginRegistry.GetPlugin("OneNote");
            if (oneNotePlugin != null && await oneNotePlugin.IsAvailableAsync(cancellationToken))
            {
                try
                {
                    var data = await oneNotePlugin.CaptureStateAsync(cancellationToken);
                    if (data.Data is IReadOnlyList<OneNoteNotebook> notebooks)
                    {
                        return notebooks;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to capture OneNote data via plugin");
                }
            }
        }
        
        return Array.Empty<OneNoteNotebook>().AsReadOnly();
    }

    private async Task<IReadOnlyList<GitHubRepository>> CaptureGitHubDataAsync(CancellationToken cancellationToken)
    {
        if (_pluginRegistry != null)
        {
            var gitHubPlugin = _pluginRegistry.GetPlugin("GitHub");
            if (gitHubPlugin != null && await gitHubPlugin.IsAvailableAsync(cancellationToken))
            {
                try
                {
                    var data = await gitHubPlugin.CaptureStateAsync(cancellationToken);
                    if (data.Data is IReadOnlyList<GitHubRepository> repositories)
                    {
                        return repositories;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to capture GitHub data via plugin");
                }
            }
        }
        
        return Array.Empty<GitHubRepository>().AsReadOnly();
    }

    private const int MaxProcessesLimit = 10; // Limit to avoid overwhelming data

    private async Task<IReadOnlyList<OpenApplication>> CaptureRunningApplicationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var processes = Process.GetProcesses()
                .Where(p => !string.IsNullOrEmpty(p.MainWindowTitle))
                .Take(MaxProcessesLimit)
                .Select(p => new OpenApplication
                {
                    Name = p.ProcessName,
                    ProcessName = p.ProcessName,
                    ProcessId = p.Id,
                    WindowTitle = p.MainWindowTitle,
                    StartTime = p.StartTime.ToUniversalTime()
                })
                .ToList();

            await Task.Delay(10, cancellationToken);
            return processes.AsReadOnly();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture running applications");
            return Array.Empty<OpenApplication>().AsReadOnly();
        }
    }

    private static string GetBrowserProfilePath()
    {
        // Return a simulated browser profile path
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) 
            ? @"C:\Users\Default\AppData\Local\Microsoft\Edge\User Data\Default"
            : "/home/user/.config/microsoft-edge/Default";
    }

    private static IEnumerable<WorkspaceSummary> ApplyWorkspaceFiltering(IEnumerable<WorkspaceSummary> workspaces, ListWorkspacesRequest request)
    {
        var filtered = workspaces.AsEnumerable();

        if (request.FromDate.HasValue)
        {
            filtered = filtered.Where(w => w.CreatedUtc >= request.FromDate.Value);
        }

        if (request.ToDate.HasValue)
        {
            filtered = filtered.Where(w => w.CreatedUtc <= request.ToDate.Value);
        }

        if (!string.IsNullOrEmpty(request.DeviceFilter))
        {
            filtered = filtered.Where(w => w.Device.DeviceName.Contains(request.DeviceFilter, StringComparison.OrdinalIgnoreCase));
        }

        return filtered;
    }

    private static IEnumerable<WorkspaceSummary> ApplyWorkspaceSorting(IEnumerable<WorkspaceSummary> workspaces, ListWorkspacesRequest request)
    {
        return request.SortBy.ToLowerInvariant() switch
        {
            "created" => request.SortDescending 
                ? workspaces.OrderByDescending(w => w.CreatedUtc)
                : workspaces.OrderBy(w => w.CreatedUtc),
            "name" => request.SortDescending
                ? workspaces.OrderByDescending(w => w.Name)
                : workspaces.OrderBy(w => w.Name),
            _ => request.SortDescending
                ? workspaces.OrderByDescending(w => w.LastAccessedUtc)
                : workspaces.OrderBy(w => w.LastAccessedUtc)
        };
    }

    private static string GenerateWorkspaceMarkdown(WorkspaceContext workspace)
    {
        var markdown = $@"# Workspace: {workspace.Name}
**ID:** `{workspace.WorkspaceId}`
**Device:** {workspace.Device.DeviceName} ({workspace.Device.OperatingSystem})
**Created:** {workspace.CreatedUtc:yyyy-MM-dd HH:mm:ss} UTC
**Last Active:** {workspace.LastAccessedUtc:yyyy-MM-dd HH:mm:ss} UTC

## Browser State

### Open Tabs ({workspace.BrowserState.OpenTabs.Count})
{string.Join("\n", workspace.BrowserState.OpenTabs.Select((tab, i) => 
    $"{i + 1}. **{tab.Title}**\n   - URL: {tab.Url}\n   - Active: {tab.IsActive}\n   - Pinned: {tab.IsPinned}"))}

### Bookmarks ({workspace.BrowserState.Bookmarks.Count})
{string.Join("\n", workspace.BrowserState.Bookmarks.Select(b => $"- **{b.Title}**: {b.Url}"))}

## Application State

### OneNote Notebooks ({workspace.ApplicationState.OneNoteNotebooks.Count})
{string.Join("\n", workspace.ApplicationState.OneNoteNotebooks.Select(n => 
    $"- **{n.Name}** ({n.Sections.Count} sections)"))}

### GitHub Repositories ({workspace.ApplicationState.GitHubRepos.Count})
{string.Join("\n", workspace.ApplicationState.GitHubRepos.Select(r => 
    $"- **{r.FullName}** (branch: {r.CurrentBranch ?? r.DefaultBranch})"))}

### Running Applications ({workspace.ApplicationState.RunningApps.Count})
{string.Join("\n", workspace.ApplicationState.RunningApps.Select(a => 
    $"- **{a.Name}**: {a.WindowTitle}"))}

## Conversations ({workspace.Conversations.Count})
{string.Join("\n", workspace.Conversations.Select(c => 
    $"- **{c.ConversationId}**: {c.TurnCount} turns, last active {c.LastActivity:yyyy-MM-dd HH:mm}"))}

## Restore Commands
```bash
# Restore entire workspace
darbot-memory restore --workspace ""{workspace.WorkspaceId}""

# Selective restore
darbot-memory restore --workspace ""{workspace.WorkspaceId}"" --only browser,onenote
```
";

        return markdown;
    }
}