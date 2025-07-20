namespace Darbot.Memory.Mcp.Core.Models;

/// <summary>
/// Represents a conversation turn in the MCP system
/// </summary>
public record ConversationTurn
{
    public required string ConversationId { get; init; }
    public required int TurnNumber { get; init; }
    public required DateTime UtcTimestamp { get; init; }
    public required string Prompt { get; init; }
    public required string Model { get; init; }
    public required string Response { get; init; }
    public IReadOnlyList<string> ToolsUsed { get; init; } = Array.Empty<string>();
    public string? Hash { get; init; }
    public string SchemaVersion { get; init; } = "v1.0.0";
}

/// <summary>
/// Represents the header metadata for a conversation turn
/// </summary>
public record ConversationMetadata
{
    public required string ConversationId { get; init; }
    public required int TurnNumber { get; init; }
    public required DateTime UtcTimestamp { get; init; }
    public required string Hash { get; init; }
    public string SchemaVersion { get; init; } = "v1.0.0";
}

/// <summary>
/// Request model for batch writing messages
/// </summary>
public record BatchWriteRequest
{
    public required IReadOnlyList<ConversationTurn> Messages { get; init; }
}

/// <summary>
/// Response model for batch write operations
/// </summary>
public record BatchWriteResponse
{
    public required bool Success { get; init; }
    public required int ProcessedCount { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public string? Message { get; init; }
}

/// <summary>
/// Request model for searching conversations
/// </summary>
public record ConversationSearchRequest
{
    public string? ConversationId { get; init; }
    public string? SearchText { get; init; }
    public string? Model { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public IReadOnlyList<string> ToolsUsed { get; init; } = Array.Empty<string>();
    public int Skip { get; init; } = 0;
    public int Take { get; init; } = 50;
    public string SortBy { get; init; } = "timestamp"; // timestamp, conversationId, turnNumber
    public bool SortDescending { get; init; } = true;
}

/// <summary>
/// Response model for conversation search results
/// </summary>
public record ConversationSearchResponse
{
    public required IReadOnlyList<ConversationTurn> Results { get; init; }
    public required int TotalCount { get; init; }
    public required bool HasMore { get; init; }
    public required int Skip { get; init; }
    public required int Take { get; init; }
}

/// <summary>
/// Request model for listing conversations
/// </summary>
public record ConversationListRequest
{
    public int Skip { get; init; } = 0;
    public int Take { get; init; } = 50;
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public string SortBy { get; init; } = "lastActivity"; // lastActivity, conversationId, turnCount
    public bool SortDescending { get; init; } = true;
}

/// <summary>
/// Summary of a conversation for listing purposes
/// </summary>
public record ConversationSummary
{
    public required string ConversationId { get; init; }
    public required int TurnCount { get; init; }
    public required DateTime FirstTurnTimestamp { get; init; }
    public required DateTime LastTurnTimestamp { get; init; }
    public required IReadOnlyList<string> ModelsUsed { get; init; }
    public required IReadOnlyList<string> ToolsUsed { get; init; }
    public string? LastPrompt { get; init; }
}

/// <summary>
/// Response model for conversation listing
/// </summary>
public record ConversationListResponse
{
    public required IReadOnlyList<ConversationSummary> Conversations { get; init; }
    public required int TotalCount { get; init; }
    public required bool HasMore { get; init; }
    public required int Skip { get; init; }
    public required int Take { get; init; }
}

/// <summary>
/// Represents a browser history entry
/// </summary>
public record BrowserHistoryEntry
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public required string Title { get; init; }
    public required DateTime VisitTime { get; init; }
    public required int VisitCount { get; init; }
    public required string ProfileName { get; init; }
    public required string ProfilePath { get; init; }
    public DateTime? LastSync { get; init; }
    public string? Hash { get; init; }
}

/// <summary>
/// Represents a browser profile
/// </summary>
public record BrowserProfile
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsDefault { get; init; }
    public DateTime? LastAccessed { get; init; }
    public DateTime? LastSyncTime { get; init; }
}

/// <summary>
/// Request model for browser history search
/// </summary>
public record BrowserHistorySearchRequest
{
    public string? Url { get; init; }
    public string? Title { get; init; }
    public string? Domain { get; init; }
    public string? ProfileName { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public int Skip { get; init; } = 0;
    public int Take { get; init; } = 100;
    public string SortBy { get; init; } = "visitTime"; // visitTime, visitCount, title, url
    public bool SortDescending { get; init; } = true;
}

/// <summary>
/// Response model for browser history search
/// </summary>
public record BrowserHistorySearchResponse
{
    public required IReadOnlyList<BrowserHistoryEntry> Results { get; init; }
    public required int TotalCount { get; init; }
    public required bool HasMore { get; init; }
    public required int Skip { get; init; }
    public required int Take { get; init; }
}

/// <summary>
/// Request model for browser history sync (delta update)
/// </summary>
public record BrowserHistorySyncRequest
{
    public DateTime? LastSyncTime { get; init; }
    public IReadOnlyList<string> ProfileNames { get; init; } = Array.Empty<string>();
    public bool FullSync { get; init; } = false;
}

/// <summary>
/// Response model for browser history sync
/// </summary>
public record BrowserHistorySyncResponse
{
    public required bool Success { get; init; }
    public required int NewEntriesCount { get; init; }
    public required int UpdatedEntriesCount { get; init; }
    public required DateTime SyncTime { get; init; }
    public required IReadOnlyList<string> ProcessedProfiles { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public string? Message { get; init; }
}

// ===============================
// Workspace Context Models
// ===============================

/// <summary>
/// Represents information about the device where a workspace was captured
/// </summary>
public record DeviceInfo
{
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string OperatingSystem { get; init; }
    public required string Architecture { get; init; }
    public required string UserName { get; init; }
    public required DateTime CaptureTime { get; init; }
}

/// <summary>
/// Enhanced browser state that includes all browser-related context
/// </summary>
public record BrowserState
{
    public required IReadOnlyList<BrowserProfile> Profiles { get; init; }
    public required IReadOnlyList<BrowserTab> OpenTabs { get; init; }
    public required IReadOnlyList<BrowserBookmark> Bookmarks { get; init; }
    public required IReadOnlyList<BrowserHistoryEntry> RecentHistory { get; init; }
    public required IReadOnlyList<BrowserExtension> Extensions { get; init; }
    public required Dictionary<string, string> Settings { get; init; }
}

/// <summary>
/// Represents an open browser tab
/// </summary>
public record BrowserTab
{
    public required string Url { get; init; }
    public required string Title { get; init; }
    public required int TabIndex { get; init; }
    public required bool IsActive { get; init; }
    public required bool IsPinned { get; init; }
    public required string ProfileName { get; init; }
    public string? FaviconUrl { get; init; }
    public int ScrollPosition { get; init; } = 0;
    public Dictionary<string, object> FormData { get; init; } = new();
}

/// <summary>
/// Represents a browser bookmark
/// </summary>
public record BrowserBookmark
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required string FolderPath { get; init; }
    public required DateTime DateAdded { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Represents a browser extension
/// </summary>
public record BrowserExtension
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required bool IsEnabled { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Represents application state including various integrated applications
/// </summary>
public record ApplicationState
{
    public required IReadOnlyList<OneNoteNotebook> OneNoteNotebooks { get; init; }
    public required IReadOnlyList<StickyNote> StickyNotes { get; init; }
    public required IReadOnlyList<GitHubRepository> GitHubRepos { get; init; }
    public required IReadOnlyList<VSCodeWorkspace> VSCodeWorkspaces { get; init; }
    public required IReadOnlyList<OpenApplication> RunningApps { get; init; }
}

/// <summary>
/// Represents a OneNote notebook
/// </summary>
public record OneNoteNotebook
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DateTime CreatedTime { get; init; }
    public required DateTime LastModifiedTime { get; init; }
    public required IReadOnlyList<OneNoteSection> Sections { get; init; }
    public string? WebUrl { get; init; }
}

/// <summary>
/// Represents a OneNote section
/// </summary>
public record OneNoteSection
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<OneNotePage> Pages { get; init; }
    public string? ParentSectionGroup { get; init; }
}

/// <summary>
/// Represents a OneNote page
/// </summary>
public record OneNotePage
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required DateTime CreatedTime { get; init; }
    public required DateTime LastModifiedTime { get; init; }
    public string? ContentPreview { get; init; }
    public string? WebUrl { get; init; }
}

/// <summary>
/// Represents a sticky note
/// </summary>
public record StickyNote
{
    public required string Id { get; init; }
    public required string Content { get; init; }
    public required DateTime CreatedTime { get; init; }
    public required DateTime LastModifiedTime { get; init; }
    public required string Color { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

/// <summary>
/// Represents a GitHub repository reference
/// </summary>
public record GitHubRepository
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required string CloneUrl { get; init; }
    public required string DefaultBranch { get; init; }
    public required bool IsPrivate { get; init; }
    public required DateTime LastPushedAt { get; init; }
    public string? LocalPath { get; init; }
    public string? CurrentBranch { get; init; }
    public bool HasUncommittedChanges { get; init; }
    public IReadOnlyList<string> OpenIssues { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DraftPRs { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Represents a VS Code workspace
/// </summary>
public record VSCodeWorkspace
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required IReadOnlyList<string> OpenFiles { get; init; }
    public required IReadOnlyList<VSCodeExtension> Extensions { get; init; }
    public required Dictionary<string, object> Settings { get; init; }
    public string? ActiveFile { get; init; }
    public IReadOnlyList<string> TerminalHistory { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Represents a VS Code extension
/// </summary>
public record VSCodeExtension
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required bool IsEnabled { get; init; }
}

/// <summary>
/// Represents an open application
/// </summary>
public record OpenApplication
{
    public required string Name { get; init; }
    public required string ProcessName { get; init; }
    public required int ProcessId { get; init; }
    public required string WindowTitle { get; init; }
    public required DateTime StartTime { get; init; }
    public string? FilePath { get; init; }
    public Dictionary<string, object> StateData { get; init; } = new();
}

/// <summary>
/// Reference to a conversation in the context of a workspace
/// </summary>
public record ConversationReference
{
    public required string ConversationId { get; init; }
    public required DateTime StartTime { get; init; }
    public required DateTime LastActivity { get; init; }
    public required int TurnCount { get; init; }
    public required IReadOnlyList<string> ToolsUsed { get; init; }
    public string? Summary { get; init; }
}

/// <summary>
/// Complete workspace context that captures all relevant state
/// </summary>
public record WorkspaceContext
{
    public required string WorkspaceId { get; init; }
    public required string Name { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required DateTime LastAccessedUtc { get; init; }
    public required DeviceInfo Device { get; init; }
    public required BrowserState BrowserState { get; init; }
    public required ApplicationState ApplicationState { get; init; }
    public required IReadOnlyList<ConversationReference> Conversations { get; init; }
    public Dictionary<string, object> ExtensionData { get; init; } = new();
    public string? Hash { get; init; }
    public string SchemaVersion { get; init; } = "v1.0.0";
}

// ===============================
// Plugin Architecture Models
// ===============================

/// <summary>
/// Data container for plugin state
/// </summary>
public record PluginData
{
    public required string Type { get; init; }
    public required object Data { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();
    public IReadOnlyList<string> Links { get; init; } = Array.Empty<string>();
    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;
}

// ===============================
// Workspace Request/Response Models
// ===============================

/// <summary>
/// Options for capturing workspace state
/// </summary>
public record CaptureOptions
{
    public bool IncludeHistory { get; init; } = true;
    public bool IncludeSensitive { get; init; } = false;
    public bool IncludePasswords { get; init; } = false;
    public IReadOnlyList<string> IncludePlugins { get; init; } = Array.Empty<string>();
    public TimeSpan HistoryWindow { get; init; } = TimeSpan.FromDays(1);
}

/// <summary>
/// Options for restoring workspace state
/// </summary>
public record RestoreOptions
{
    public RestoreMode Mode { get; init; } = RestoreMode.Merge;
    public IReadOnlyList<string> Components { get; init; } = Array.Empty<string>();
    public bool OpenApps { get; init; } = true;
    public bool RestoreTabs { get; init; } = true;
    public bool RestorePosition { get; init; } = true;
}

/// <summary>
/// Mode for restoring workspace state
/// </summary>
public enum RestoreMode
{
    Merge,
    Replace,
    Selective
}

/// <summary>
/// Request for capturing workspace
/// </summary>
public record CaptureWorkspaceRequest
{
    public required string Name { get; init; }
    public CaptureOptions Options { get; init; } = new();
}

/// <summary>
/// Response for workspace capture
/// </summary>
public record CaptureWorkspaceResponse
{
    public required bool Success { get; init; }
    public required string WorkspaceId { get; init; }
    public required DateTime CapturedAt { get; init; }
    public required int ComponentsCount { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public string? Message { get; init; }
}

/// <summary>
/// Request for restoring workspace
/// </summary>
public record RestoreWorkspaceRequest
{
    public required string WorkspaceId { get; init; }
    public RestoreOptions Options { get; init; } = new();
}

/// <summary>
/// Response for workspace restore
/// </summary>
public record RestoreWorkspaceResponse
{
    public required bool Success { get; init; }
    public required string WorkspaceId { get; init; }
    public required DateTime RestoredAt { get; init; }
    public required int ComponentsRestored { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public string? Message { get; init; }
}

/// <summary>
/// Request for listing workspaces
/// </summary>
public record ListWorkspacesRequest
{
    public int Skip { get; init; } = 0;
    public int Take { get; init; } = 50;
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public string? DeviceFilter { get; init; }
    public string SortBy { get; init; } = "lastAccessed"; // lastAccessed, created, name
    public bool SortDescending { get; init; } = true;
}

/// <summary>
/// Summary of a workspace for listing purposes
/// </summary>
public record WorkspaceSummary
{
    public required string WorkspaceId { get; init; }
    public required string Name { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required DateTime LastAccessedUtc { get; init; }
    public required DeviceInfo Device { get; init; }
    public required int ConversationCount { get; init; }
    public required int BrowserTabCount { get; init; }
    public required int ApplicationCount { get; init; }
    public string? PreviewImage { get; init; }
}

/// <summary>
/// Response for listing workspaces
/// </summary>
public record ListWorkspacesResponse
{
    public required IReadOnlyList<WorkspaceSummary> Workspaces { get; init; }
    public required int TotalCount { get; init; }
    public required bool HasMore { get; init; }
    public required int Skip { get; init; }
    public required int Take { get; init; }
}
