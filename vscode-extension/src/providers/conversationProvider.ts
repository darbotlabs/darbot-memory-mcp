import * as vscode from 'vscode';
import { McpServerManager } from '../mcpServerManager';

export class ConversationProvider implements vscode.TreeDataProvider<ConversationItem> {
    private _onDidChangeTreeData: vscode.EventEmitter<ConversationItem | undefined | null | void> = new vscode.EventEmitter<ConversationItem | undefined | null | void>();
    readonly onDidChangeTreeData: vscode.Event<ConversationItem | undefined | null | void> = this._onDidChangeTreeData.event;

    constructor(private mcpServerManager: McpServerManager) {}

    refresh(): void {
        this._onDidChangeTreeData.fire();
    }

    getTreeItem(element: ConversationItem): vscode.TreeItem {
        return element;
    }

    async getChildren(element?: ConversationItem): Promise<ConversationItem[]> {
        if (!this.mcpServerManager.isRunning()) {
            return [new ConversationItem('Server not running', '', 0, vscode.TreeItemCollapsibleState.None)];
        }

        try {
            const conversations = await this.mcpServerManager.listConversations();
            
            if (!conversations?.conversations || conversations.conversations.length === 0) {
                return [new ConversationItem('No conversations found', '', 0, vscode.TreeItemCollapsibleState.None)];
            }

            return conversations.conversations.map((conv: any) => 
                new ConversationItem(
                    conv.conversationId,
                    conv.summary || 'No summary',
                    conv.turnCount || 0,
                    vscode.TreeItemCollapsibleState.None
                )
            );
        } catch (error) {
            return [new ConversationItem(`Error: ${error}`, '', 0, vscode.TreeItemCollapsibleState.None)];
        }
    }
}

class ConversationItem extends vscode.TreeItem {
    constructor(
        public readonly conversationId: string,
        public readonly summary: string,
        public readonly turnCount: number,
        public readonly collapsibleState: vscode.TreeItemCollapsibleState
    ) {
        super(conversationId, collapsibleState);
        
        this.tooltip = `${this.conversationId}: ${summary}`;
        this.description = `${turnCount} turns`;
        this.iconPath = new vscode.ThemeIcon('comment-discussion');
        this.contextValue = 'conversation';
        
        this.command = {
            command: 'darbotMemoryMcp.viewConversation',
            title: 'View Conversation',
            arguments: [this.conversationId]
        };
    }
}