import * as vscode from 'vscode';
import { McpServerManager } from './mcpServerManager';
import { ConversationProvider } from './providers/conversationProvider';
import { WorkspaceProvider } from './providers/workspaceProvider';
import { ServerStatusProvider } from './providers/serverStatusProvider';

let mcpServerManager: McpServerManager;
let serverStatusProvider: ServerStatusProvider;
let conversationProvider: ConversationProvider;
let workspaceProvider: WorkspaceProvider;

export function activate(context: vscode.ExtensionContext) {
    console.log('Darbot Memory MCP extension is now active!');

    // Initialize providers and managers
    mcpServerManager = new McpServerManager();
    serverStatusProvider = new ServerStatusProvider(mcpServerManager);
    conversationProvider = new ConversationProvider(mcpServerManager);
    workspaceProvider = new WorkspaceProvider(mcpServerManager);

    // Register tree data providers
    vscode.window.registerTreeDataProvider('darbotMemoryMcp.serverStatus', serverStatusProvider);
    vscode.window.registerTreeDataProvider('darbotMemoryMcp.conversations', conversationProvider);
    vscode.window.registerTreeDataProvider('darbotMemoryMcp.workspaces', workspaceProvider);

    // Register commands
    context.subscriptions.push(
        vscode.commands.registerCommand('darbotMemoryMcp.startServer', async () => {
            try {
                await mcpServerManager.startServer();
                vscode.window.showInformationMessage('Darbot Memory MCP Server started successfully');
                refreshProviders();
            } catch (error) {
                vscode.window.showErrorMessage(`Failed to start MCP server: ${error}`);
            }
        }),

        vscode.commands.registerCommand('darbotMemoryMcp.stopServer', async () => {
            try {
                await mcpServerManager.stopServer();
                vscode.window.showInformationMessage('Darbot Memory MCP Server stopped');
                refreshProviders();
            } catch (error) {
                vscode.window.showErrorMessage(`Failed to stop MCP server: ${error}`);
            }
        }),

        vscode.commands.registerCommand('darbotMemoryMcp.captureWorkspace', async () => {
            try {
                const workspaceName = await vscode.window.showInputBox({
                    prompt: 'Enter workspace name',
                    value: `workspace-${new Date().toISOString().split('T')[0]}`
                });

                if (workspaceName) {
                    await mcpServerManager.captureWorkspace(workspaceName);
                    vscode.window.showInformationMessage(`Workspace '${workspaceName}' captured successfully`);
                    workspaceProvider.refresh();
                }
            } catch (error) {
                vscode.window.showErrorMessage(`Failed to capture workspace: ${error}`);
            }
        }),

        vscode.commands.registerCommand('darbotMemoryMcp.searchConversations', async () => {
            try {
                const searchQuery = await vscode.window.showInputBox({
                    prompt: 'Enter search query',
                    placeHolder: 'Search conversations...'
                });

                if (searchQuery) {
                    const results = await mcpServerManager.searchConversations(searchQuery);
                    await showSearchResults(results);
                }
            } catch (error) {
                vscode.window.showErrorMessage(`Failed to search conversations: ${error}`);
            }
        }),

        vscode.commands.registerCommand('darbotMemoryMcp.viewConversations', async () => {
            try {
                const conversations = await mcpServerManager.listConversations();
                await showConversationList(conversations);
            } catch (error) {
                vscode.window.showErrorMessage(`Failed to load conversations: ${error}`);
            }
        }),

        vscode.commands.registerCommand('darbotMemoryMcp.openConfig', () => {
            vscode.commands.executeCommand('workbench.action.openSettings', 'darbotMemoryMcp');
        }),

        vscode.commands.registerCommand('darbotMemoryMcp.refresh', () => {
            refreshProviders();
        })
    );

    // Auto-start server if configured
    const config = vscode.workspace.getConfiguration('darbotMemoryMcp');
    if (config.get<boolean>('autoStart')) {
        mcpServerManager.startServer().catch(error => {
            console.error('Failed to auto-start MCP server:', error);
        });
    }

    // Set up periodic refresh
    const refreshInterval = setInterval(() => {
        if (mcpServerManager.isRunning()) {
            refreshProviders();
        }
    }, 30000); // Refresh every 30 seconds

    context.subscriptions.push({
        dispose: () => {
            clearInterval(refreshInterval);
            mcpServerManager.dispose();
        }
    });
}

function refreshProviders() {
    serverStatusProvider.refresh();
    conversationProvider.refresh();
    workspaceProvider.refresh();
}

async function showSearchResults(results: any) {
    const panel = vscode.window.createWebviewPanel(
        'darbotSearchResults',
        'Conversation Search Results',
        vscode.ViewColumn.One,
        {
            enableScripts: true
        }
    );

    panel.webview.html = generateSearchResultsHtml(results);
}

async function showConversationList(conversations: any) {
    const panel = vscode.window.createWebviewPanel(
        'darbotConversations',
        'Conversations',
        vscode.ViewColumn.One,
        {
            enableScripts: true
        }
    );

    panel.webview.html = generateConversationListHtml(conversations);
}

function generateSearchResultsHtml(results: any): string {
    return `
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Search Results</title>
            <style>
                body { font-family: var(--vscode-font-family); padding: 20px; }
                .result { margin: 20px 0; padding: 15px; border: 1px solid var(--vscode-panel-border); border-radius: 5px; }
                .result-header { font-weight: bold; margin-bottom: 10px; }
                .result-content { margin: 10px 0; }
                .result-metadata { color: var(--vscode-descriptionForeground); font-size: 0.9em; }
            </style>
        </head>
        <body>
            <h1>Search Results</h1>
            <div id="results">
                ${results?.results?.map((result: any) => `
                    <div class="result">
                        <div class="result-header">Conversation: ${result.conversationId}</div>
                        <div class="result-content">${result.prompt}</div>
                        <div class="result-metadata">
                            Model: ${result.model} | 
                            Turn: ${result.turnNumber} | 
                            Date: ${new Date(result.utcTimestamp).toLocaleDateString()}
                        </div>
                    </div>
                `).join('') || '<p>No results found</p>'}
            </div>
        </body>
        </html>
    `;
}

function generateConversationListHtml(conversations: any): string {
    return `
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Conversations</title>
            <style>
                body { font-family: var(--vscode-font-family); padding: 20px; }
                .conversation { margin: 15px 0; padding: 15px; border: 1px solid var(--vscode-panel-border); border-radius: 5px; }
                .conversation-header { font-weight: bold; margin-bottom: 10px; }
                .conversation-summary { margin: 10px 0; }
                .conversation-metadata { color: var(--vscode-descriptionForeground); font-size: 0.9em; }
            </style>
        </head>
        <body>
            <h1>Recent Conversations</h1>
            <div id="conversations">
                ${conversations?.conversations?.map((conv: any) => `
                    <div class="conversation">
                        <div class="conversation-header">${conv.conversationId}</div>
                        <div class="conversation-summary">${conv.summary || 'No summary available'}</div>
                        <div class="conversation-metadata">
                            Turns: ${conv.turnCount} | 
                            Last Activity: ${new Date(conv.lastActivity).toLocaleDateString()}
                        </div>
                    </div>
                `).join('') || '<p>No conversations found</p>'}
            </div>
        </body>
        </html>
    `;
}

export function deactivate() {
    if (mcpServerManager) {
        mcpServerManager.dispose();
    }
}