using Darbot.Memory.Mcp.Core.Models;

namespace Darbot.Memory.Mcp.Core.Interfaces;

/// <summary>
/// Interface for storage providers that persist conversation turns
/// </summary>
public interface IStorageProvider
{
    /// <summary>
    /// Writes a conversation turn to storage
    /// </summary>
    Task<bool> WriteConversationTurnAsync(ConversationTurn turn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes multiple conversation turns to storage
    /// </summary>
    Task<BatchWriteResponse> WriteBatchAsync(IEnumerable<ConversationTurn> turns, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for conversation turns based on criteria
    /// </summary>
    Task<ConversationSearchResponse> SearchConversationsAsync(ConversationSearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists conversations with summary information
    /// </summary>
    Task<ConversationListResponse> ListConversationsAsync(ConversationListRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a specific conversation turn by ID and turn number
    /// </summary>
    Task<ConversationTurn?> GetConversationTurnAsync(string conversationId, int turnNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all turns for a specific conversation
    /// </summary>
    Task<IReadOnlyList<ConversationTurn>> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the storage provider is healthy and accessible
    /// </summary>
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for formatting conversation turns into markdown
/// </summary>
public interface IConversationFormatter
{
    /// <summary>
    /// Formats a conversation turn into markdown content
    /// </summary>
    string FormatToMarkdown(ConversationTurn turn);

    /// <summary>
    /// Generates a filename for a conversation turn
    /// </summary>
    string GenerateFileName(ConversationTurn turn);
}

/// <summary>
/// Interface for calculating hashes of conversation content
/// </summary>
public interface IHashCalculator
{
    /// <summary>
    /// Calculates a cryptographic hash of the conversation turn content
    /// </summary>
    string CalculateHash(ConversationTurn turn);

    /// <summary>
    /// Validates that a conversation turn's hash is correct
    /// </summary>
    bool ValidateHash(ConversationTurn turn);
}

/// <summary>
/// Interface for the main conversation service
/// </summary>
public interface IConversationService
{
    /// <summary>
    /// Persists a single conversation turn
    /// </summary>
    Task<bool> PersistTurnAsync(ConversationTurn turn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists multiple conversation turns in a batch
    /// </summary>
    Task<BatchWriteResponse> PersistBatchAsync(IEnumerable<ConversationTurn> turns, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for conversation turns based on criteria
    /// </summary>
    Task<ConversationSearchResponse> SearchConversationsAsync(ConversationSearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists conversations with summary information
    /// </summary>
    Task<ConversationListResponse> ListConversationsAsync(ConversationListRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a specific conversation turn by ID and turn number
    /// </summary>
    Task<ConversationTurn?> GetConversationTurnAsync(string conversationId, int turnNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all turns for a specific conversation
    /// </summary>
    Task<IReadOnlyList<ConversationTurn>> GetConversationAsync(string conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for browser history providers that can read from different browsers
/// </summary>
public interface IBrowserHistoryProvider
{
    /// <summary>
    /// Gets all available browser profiles
    /// </summary>
    Task<IReadOnlyList<BrowserProfile>> GetProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads browser history from a specific profile since a given timestamp
    /// </summary>
    Task<IReadOnlyList<BrowserHistoryEntry>> ReadHistoryAsync(string profilePath, DateTime? since = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the browser is installed and accessible
    /// </summary>
    Task<bool> IsBrowserAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the browser name (e.g., "Microsoft Edge", "Google Chrome")
    /// </summary>
    string BrowserName { get; }
}

/// <summary>
/// Interface for browser history storage that persists history entries
/// </summary>
public interface IBrowserHistoryStorage
{
    /// <summary>
    /// Stores browser history entries
    /// </summary>
    Task<bool> StoreBrowserHistoryAsync(IEnumerable<BrowserHistoryEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches stored browser history based on criteria
    /// </summary>
    Task<BrowserHistorySearchResponse> SearchBrowserHistoryAsync(BrowserHistorySearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the last sync time for a profile
    /// </summary>
    Task<DateTime?> GetLastSyncTimeAsync(string profilePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the last sync time for a profile
    /// </summary>
    Task<bool> UpdateLastSyncTimeAsync(string profilePath, DateTime syncTime, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the storage is healthy and accessible
    /// </summary>
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for the main browser history service
/// </summary>
public interface IBrowserHistoryService
{
    /// <summary>
    /// Syncs browser history from all available profiles (delta update)
    /// </summary>
    Task<BrowserHistorySyncResponse> SyncBrowserHistoryAsync(BrowserHistorySyncRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches stored browser history
    /// </summary>
    Task<BrowserHistorySearchResponse> SearchBrowserHistoryAsync(BrowserHistorySearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all available browser profiles
    /// </summary>
    Task<IReadOnlyList<BrowserProfile>> GetBrowserProfilesAsync(CancellationToken cancellationToken = default);
}

// ===============================
// Plugin Architecture Interfaces
// ===============================

/// <summary>
/// Interface for extensible memory plugins
/// </summary>
public interface IMemoryPlugin
{
    /// <summary>
    /// Name of the plugin
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Version of the plugin
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Captures the current state managed by this plugin
    /// </summary>
    Task<PluginData> CaptureStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores state from previously captured data
    /// </summary>
    Task<bool> RestoreStateAsync(PluginData data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that the provided data is valid for this plugin
    /// </summary>
    Task<bool> ValidateStateAsync(PluginData data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the plugin is available and functional
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Registry for managing memory plugins
/// </summary>
public interface IPluginRegistry
{
    /// <summary>
    /// Registers a plugin with the system
    /// </summary>
    void RegisterPlugin(IMemoryPlugin plugin);

    /// <summary>
    /// Gets all registered plugins
    /// </summary>
    IReadOnlyList<IMemoryPlugin> GetAllPlugins();

    /// <summary>
    /// Gets a plugin by name
    /// </summary>
    IMemoryPlugin? GetPlugin(string name);

    /// <summary>
    /// Gets all available plugins (registered and functional)
    /// </summary>
    Task<IReadOnlyList<IMemoryPlugin>> GetAvailablePluginsAsync(CancellationToken cancellationToken = default);
}

// ===============================
// Workspace Interfaces
// ===============================

/// <summary>
/// Enhanced storage provider interface that supports workspace operations
/// </summary>
public interface IWorkspaceStorageProvider : IStorageProvider
{
    // Core Workspace Operations
    /// <summary>
    /// Captures the current workspace state
    /// </summary>
    Task<WorkspaceContext> CaptureCurrentWorkspaceAsync(CaptureOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a workspace context
    /// </summary>
    Task<bool> StoreWorkspaceAsync(WorkspaceContext workspace, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a workspace by ID
    /// </summary>
    Task<bool> RestoreWorkspaceAsync(string workspaceId, RestoreOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a workspace by ID
    /// </summary>
    Task<WorkspaceContext?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists workspaces with filtering and pagination
    /// </summary>
    Task<ListWorkspacesResponse> ListWorkspacesAsync(ListWorkspacesRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a workspace by ID
    /// </summary>
    Task<bool> DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);

    // Browser Integration
    /// <summary>
    /// Captures current browser state from all browsers
    /// </summary>
    Task<BrowserState> CaptureBrowserStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores browser state
    /// </summary>
    Task<bool> RestoreBrowserStateAsync(BrowserState state, CancellationToken cancellationToken = default);

    // Application Integration  
    /// <summary>
    /// Captures current application state
    /// </summary>
    Task<ApplicationState> CaptureApplicationStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores application state
    /// </summary>
    Task<bool> RestoreApplicationStateAsync(ApplicationState state, CancellationToken cancellationToken = default);
}

/// <summary>
/// Service for managing workspace contexts
/// </summary>
public interface IWorkspaceService
{
    /// <summary>
    /// Captures the current workspace state
    /// </summary>
    Task<CaptureWorkspaceResponse> CaptureWorkspaceAsync(CaptureWorkspaceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a workspace by ID
    /// </summary>
    Task<RestoreWorkspaceResponse> RestoreWorkspaceAsync(RestoreWorkspaceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all workspaces
    /// </summary>
    Task<ListWorkspacesResponse> ListWorkspacesAsync(ListWorkspacesRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific workspace by ID
    /// </summary>
    Task<WorkspaceContext?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a workspace by ID
    /// </summary>
    Task<bool> DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Service for capturing browser state across different browsers
/// </summary>
public interface IBrowserIntegrationService
{
    /// <summary>
    /// Captures browser state from all installed browsers
    /// </summary>
    Task<BrowserState> CaptureAllBrowsersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures state from a specific browser
    /// </summary>
    Task<BrowserState> CaptureBrowserAsync(string browserName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores browser state to a specific browser
    /// </summary>
    Task<bool> RestoreBrowserStateAsync(BrowserState state, string browserName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all available browser adapters
    /// </summary>
    IReadOnlyList<string> GetAvailableBrowsers();
}

/// <summary>
/// Adapter interface for different browser types
/// </summary>
public interface IBrowserAdapter
{
    /// <summary>
    /// Name of the browser this adapter supports
    /// </summary>
    string BrowserName { get; }

    /// <summary>
    /// Checks if the browser is installed and accessible
    /// </summary>
    Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures the current browser state
    /// </summary>
    Task<BrowserState> CaptureStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores browser state
    /// </summary>
    Task<bool> RestoreStateAsync(BrowserState state, CancellationToken cancellationToken = default);
}