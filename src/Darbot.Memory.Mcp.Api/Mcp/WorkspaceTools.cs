using System.ComponentModel;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Darbot.Memory.Mcp.Api.Mcp;

[McpServerToolType]
public sealed class WorkspaceTools(IWorkspaceService workspaces)
{
    [McpServerTool(Name = "workspace_capture", Title = "Capture workspace", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Capture the current workspace (browser, applications, conversations) as a named snapshot.")]
    public async Task<string> Capture(
        [Description("Name for the snapshot.")] string name,
        [Description("Include recent browser history.")] bool includeHistory = true,
        [Description("Include sensitive data. Passwords are never captured.")] bool includeSensitive = false,
        CancellationToken cancellationToken = default)
    {
        var result = await workspaces.CaptureWorkspaceAsync(new CaptureWorkspaceRequest
        {
            Name = name,
            Options = new CaptureOptions { IncludeHistory = includeHistory, IncludeSensitive = includeSensitive }
        }, cancellationToken);
        return result.Success
            ? McpJson.Serialize(result)
            : throw new McpException(result.Message ?? $"Workspace capture failed: {string.Join("; ", result.Errors)}");
    }

    [McpServerTool(Name = "workspace_list", Title = "List workspaces", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List captured workspace snapshots, newest first.")]
    public async Task<string> List(
        [Description("Number of workspaces to skip.")] int skip = 0,
        [Description("Maximum number of workspaces to return (1-200).")] int take = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await workspaces.ListWorkspacesAsync(new ListWorkspacesRequest
        {
            Skip = Math.Max(0, skip),
            Take = Math.Clamp(take, 1, 200)
        }, cancellationToken);
        return McpJson.Serialize(result);
    }

    [McpServerTool(Name = "workspace_get", Title = "Get workspace", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Retrieve the full context of one workspace snapshot.")]
    public async Task<string> Get(
        [Description("Identifier of the workspace.")] string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var workspace = await workspaces.GetWorkspaceAsync(workspaceId, cancellationToken);
        return workspace is null
            ? throw new McpException($"Workspace '{workspaceId}' was not found.")
            : McpJson.Serialize(workspace);
    }

    [McpServerTool(Name = "workspace_restore", Title = "Restore workspace", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Restore a workspace snapshot on this device: reopens browser tabs and applications. Replace mode overwrites current state.")]
    public async Task<string> Restore(
        [Description("Identifier of the workspace to restore.")] string workspaceId,
        [Description("Restore mode: Merge (default), Replace or Selective.")] RestoreMode mode = RestoreMode.Merge,
        [Description("Reopen the saved browser tabs.")] bool restoreTabs = true,
        [Description("Relaunch the saved applications.")] bool openApps = true,
        CancellationToken cancellationToken = default)
    {
        var result = await workspaces.RestoreWorkspaceAsync(new RestoreWorkspaceRequest
        {
            WorkspaceId = workspaceId,
            Options = new RestoreOptions { Mode = mode, RestoreTabs = restoreTabs, OpenApps = openApps }
        }, cancellationToken);
        return result.Success
            ? McpJson.Serialize(result)
            : throw new McpException(result.Message ?? $"Workspace restore failed: {string.Join("; ", result.Errors)}");
    }

    [McpServerTool(Name = "workspace_delete", Title = "Delete workspace", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Permanently delete a workspace snapshot and its data.")]
    public async Task<string> Delete(
        [Description("Identifier of the workspace to delete.")] string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var deleted = await workspaces.DeleteWorkspaceAsync(workspaceId, cancellationToken);
        return deleted
            ? McpJson.Serialize(new { success = true, workspaceId })
            : throw new McpException($"Workspace '{workspaceId}' was not found or could not be deleted.");
    }
}
