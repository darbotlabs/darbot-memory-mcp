import * as vscode from 'vscode';
import { McpServerManager } from '../mcpServerManager';

export class WorkspaceProvider implements vscode.TreeDataProvider<WorkspaceItem> {
    private _onDidChangeTreeData: vscode.EventEmitter<WorkspaceItem | undefined | null | void> = new vscode.EventEmitter<WorkspaceItem | undefined | null | void>();
    readonly onDidChangeTreeData: vscode.Event<WorkspaceItem | undefined | null | void> = this._onDidChangeTreeData.event;

    constructor(private mcpServerManager: McpServerManager) {}

    refresh(): void {
        this._onDidChangeTreeData.fire();
    }

    getTreeItem(element: WorkspaceItem): vscode.TreeItem {
        return element;
    }

    async getChildren(element?: WorkspaceItem): Promise<WorkspaceItem[]> {
        if (!this.mcpServerManager.isRunning()) {
            return [new WorkspaceItem('Server not running', '', new Date(), vscode.TreeItemCollapsibleState.None)];
        }

        try {
            const workspaces = await this.mcpServerManager.listWorkspaces();
            
            if (!workspaces?.workspaces || workspaces.workspaces.length === 0) {
                return [new WorkspaceItem('No workspaces found', '', new Date(), vscode.TreeItemCollapsibleState.None)];
            }

            return workspaces.workspaces.map((workspace: any) => 
                new WorkspaceItem(
                    workspace.name,
                    workspace.description || 'No description',
                    new Date(workspace.capturedAt),
                    vscode.TreeItemCollapsibleState.None
                )
            );
        } catch (error) {
            return [new WorkspaceItem(`Error: ${error}`, '', new Date(), vscode.TreeItemCollapsibleState.None)];
        }
    }
}

class WorkspaceItem extends vscode.TreeItem {
    constructor(
        public readonly workspaceName: string,
        public readonly description: string,
        public readonly capturedAt: Date,
        public readonly collapsibleState: vscode.TreeItemCollapsibleState
    ) {
        super(workspaceName, collapsibleState);
        
        this.tooltip = `${this.workspaceName}: ${description}`;
        this.description = `Captured ${capturedAt.toLocaleDateString()}`;
        this.iconPath = new vscode.ThemeIcon('folder');
        this.contextValue = 'workspace';
        
        this.command = {
            command: 'darbotMemoryMcp.viewWorkspace',
            title: 'View Workspace',
            arguments: [this.workspaceName]
        };
    }
}