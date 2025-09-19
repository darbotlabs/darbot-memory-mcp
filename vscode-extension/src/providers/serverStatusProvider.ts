import * as vscode from 'vscode';
import { McpServerManager } from '../mcpServerManager';

export class ServerStatusProvider implements vscode.TreeDataProvider<ServerStatusItem> {
    private _onDidChangeTreeData: vscode.EventEmitter<ServerStatusItem | undefined | null | void> = new vscode.EventEmitter<ServerStatusItem | undefined | null | void>();
    readonly onDidChangeTreeData: vscode.Event<ServerStatusItem | undefined | null | void> = this._onDidChangeTreeData.event;

    constructor(private mcpServerManager: McpServerManager) {}

    refresh(): void {
        this._onDidChangeTreeData.fire();
    }

    getTreeItem(element: ServerStatusItem): vscode.TreeItem {
        return element;
    }

    async getChildren(element?: ServerStatusItem): Promise<ServerStatusItem[]> {
        if (!element) {
            const status = await this.mcpServerManager.getServerStatus();
            return [
                new ServerStatusItem(
                    `Status: ${status.status}`,
                    status.status === 'running' ? 
                        vscode.TreeItemCollapsibleState.Expanded : 
                        vscode.TreeItemCollapsibleState.None
                )
            ];
        }

        const status = await this.mcpServerManager.getServerStatus();
        if (status.status === 'running') {
            return [
                new ServerStatusItem(`Health: ${status.health}`, vscode.TreeItemCollapsibleState.None),
                new ServerStatusItem(`Port: ${status.port}`, vscode.TreeItemCollapsibleState.None)
            ];
        }

        return [];
    }
}

class ServerStatusItem extends vscode.TreeItem {
    constructor(
        public readonly label: string,
        public readonly collapsibleState: vscode.TreeItemCollapsibleState
    ) {
        super(label, collapsibleState);
        
        if (label.includes('Status: running')) {
            this.iconPath = new vscode.ThemeIcon('check');
            this.contextValue = 'running';
        } else if (label.includes('Status: stopped')) {
            this.iconPath = new vscode.ThemeIcon('stop');
            this.contextValue = 'stopped';
        } else if (label.includes('Health: healthy')) {
            this.iconPath = new vscode.ThemeIcon('heart');
        } else if (label.includes('Health: unhealthy')) {
            this.iconPath = new vscode.ThemeIcon('warning');
        } else {
            this.iconPath = new vscode.ThemeIcon('info');
        }
    }
}