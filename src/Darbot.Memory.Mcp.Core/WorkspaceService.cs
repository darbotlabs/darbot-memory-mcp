using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace Darbot.Memory.Mcp.Core.Services;

/// <summary>
/// Service for managing workspace contexts and their lifecycle
/// </summary>
public class WorkspaceService : IWorkspaceService
{
    private readonly IWorkspaceStorageProvider _storageProvider;
    private readonly IPluginRegistry _pluginRegistry;
    private readonly ILogger<WorkspaceService> _logger;

    public WorkspaceService(
        IWorkspaceStorageProvider storageProvider,
        IPluginRegistry pluginRegistry,
        ILogger<WorkspaceService> logger)
    {
        _storageProvider = storageProvider;
        _pluginRegistry = pluginRegistry;
        _logger = logger;
    }

    public async Task<CaptureWorkspaceResponse> CaptureWorkspaceAsync(CaptureWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting workspace capture: {WorkspaceName}", request.Name);

        try
        {
            var workspace = await _storageProvider.CaptureCurrentWorkspaceAsync(request.Options, cancellationToken);

            // Override the name from the request
            workspace = workspace with { Name = request.Name };

            var success = await _storageProvider.StoreWorkspaceAsync(workspace, cancellationToken);

            if (success)
            {
                _logger.LogInformation("Successfully captured workspace: {WorkspaceId}", workspace.WorkspaceId);

                return new CaptureWorkspaceResponse
                {
                    Success = true,
                    WorkspaceId = workspace.WorkspaceId,
                    CapturedAt = workspace.CreatedUtc,
                    ComponentsCount = CalculateComponentCount(workspace),
                    Message = $"Workspace '{workspace.Name}' captured successfully"
                };
            }
            else
            {
                _logger.LogWarning("Failed to store workspace: {WorkspaceId}", workspace.WorkspaceId);
                return new CaptureWorkspaceResponse
                {
                    Success = false,
                    WorkspaceId = workspace.WorkspaceId,
                    CapturedAt = workspace.CreatedUtc,
                    ComponentsCount = 0,
                    Errors = new[] { "Failed to store workspace data" }.AsReadOnly(),
                    Message = "Workspace capture failed during storage"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during workspace capture: {WorkspaceName}", request.Name);

            return new CaptureWorkspaceResponse
            {
                Success = false,
                WorkspaceId = string.Empty,
                CapturedAt = DateTime.UtcNow,
                ComponentsCount = 0,
                Errors = new[] { ex.Message }.AsReadOnly(),
                Message = "Workspace capture failed due to an error"
            };
        }
    }

    public async Task<RestoreWorkspaceResponse> RestoreWorkspaceAsync(RestoreWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting workspace restore: {WorkspaceId}", request.WorkspaceId);

        try
        {
            var success = await _storageProvider.RestoreWorkspaceAsync(request.WorkspaceId, request.Options, cancellationToken);

            if (success)
            {
                _logger.LogInformation("Successfully restored workspace: {WorkspaceId}", request.WorkspaceId);

                return new RestoreWorkspaceResponse
                {
                    Success = true,
                    WorkspaceId = request.WorkspaceId,
                    RestoredAt = DateTime.UtcNow,
                    ComponentsRestored = 1, // This would be calculated based on what was restored
                    Message = "Workspace restored successfully"
                };
            }
            else
            {
                _logger.LogWarning("Failed to restore workspace: {WorkspaceId}", request.WorkspaceId);
                return new RestoreWorkspaceResponse
                {
                    Success = false,
                    WorkspaceId = request.WorkspaceId,
                    RestoredAt = DateTime.UtcNow,
                    ComponentsRestored = 0,
                    Errors = new[] { "Failed to restore workspace" }.AsReadOnly(),
                    Message = "Workspace restoration failed"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during workspace restore: {WorkspaceId}", request.WorkspaceId);

            return new RestoreWorkspaceResponse
            {
                Success = false,
                WorkspaceId = request.WorkspaceId,
                RestoredAt = DateTime.UtcNow,
                ComponentsRestored = 0,
                Errors = new[] { ex.Message }.AsReadOnly(),
                Message = "Workspace restoration failed due to an error"
            };
        }
    }

    public async Task<ListWorkspacesResponse> ListWorkspacesAsync(ListWorkspacesRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Listing workspaces with skip: {Skip}, take: {Take}", request.Skip, request.Take);

        try
        {
            return await _storageProvider.ListWorkspacesAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during workspace listing");

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

    public async Task<WorkspaceContext?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting workspace: {WorkspaceId}", workspaceId);

        try
        {
            return await _storageProvider.GetWorkspaceAsync(workspaceId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting workspace: {WorkspaceId}", workspaceId);
            return null;
        }
    }

    public async Task<bool> DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deleting workspace: {WorkspaceId}", workspaceId);

        try
        {
            return await _storageProvider.DeleteWorkspaceAsync(workspaceId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting workspace: {WorkspaceId}", workspaceId);
            return false;
        }
    }

    private static int CalculateComponentCount(WorkspaceContext workspace)
    {
        return workspace.BrowserState.OpenTabs.Count +
               workspace.BrowserState.Bookmarks.Count +
               workspace.ApplicationState.OneNoteNotebooks.Count +
               workspace.ApplicationState.StickyNotes.Count +
               workspace.ApplicationState.GitHubRepos.Count +
               workspace.ApplicationState.VSCodeWorkspaces.Count +
               workspace.ApplicationState.RunningApps.Count +
               workspace.Conversations.Count;
    }
}