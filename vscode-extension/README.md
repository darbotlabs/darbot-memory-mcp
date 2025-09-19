# Darbot Memory MCP - VS Code Extension

This VS Code extension provides seamless integration with the Darbot Memory MCP server, enabling you to manage conversational audit trails, capture workspace states, and search through conversation history directly from your development environment.

## Features

### 🚀 Server Management
- **Start/Stop MCP Server**: Control the Darbot Memory MCP server directly from VS Code
- **Server Status Monitoring**: Real-time health and status monitoring
- **Auto-start**: Optionally start the server automatically when VS Code launches

### 💬 Conversation Management  
- **View Recent Conversations**: Browse and search through conversation history
- **Search Conversations**: Powerful search across all stored conversations
- **Conversation Details**: View individual conversation turns and metadata

### 🏗️ Workspace Capture
- **Capture Current Workspace**: Save the current state of your development environment
- **Workspace History**: View and manage previously captured workspaces
- **Context Preservation**: Maintain development context across sessions

### ⚙️ Configuration
- **Flexible Setup**: Configure server path, port, storage location, and authentication
- **Authentication Support**: API Key and Azure AD authentication modes
- **Storage Options**: FileSystem, Git, and Azure Blob storage providers

## Installation

1. Install the extension from the VS Code marketplace
2. Configure the MCP server path in settings
3. Start the server and begin capturing your development context

## Configuration

The extension can be configured through VS Code settings:

- `darbotMemoryMcp.serverPath`: Path to the Darbot Memory MCP server executable
- `darbotMemoryMcp.serverPort`: Port for the MCP server (default: 5093)
- `darbotMemoryMcp.autoStart`: Auto-start server on VS Code startup
- `darbotMemoryMcp.storagePath`: Path for conversation storage
- `darbotMemoryMcp.authMode`: Authentication mode (None, ApiKey, AzureAD)
- `darbotMemoryMcp.apiKey`: API key for authentication (when using ApiKey mode)

## Commands

- **Darbot Memory: Start MCP Server** - Start the MCP server
- **Darbot Memory: Stop MCP Server** - Stop the MCP server  
- **Darbot Memory: Capture Current Workspace** - Save current workspace state
- **Darbot Memory: Search Conversations** - Search through conversation history
- **Darbot Memory: View Conversations** - Browse recent conversations
- **Darbot Memory: Open Configuration** - Open extension settings

## Views

The extension adds a new activity bar section with three views:

### Server Status
- Current server status (running/stopped)
- Health monitoring
- Port information

### Recent Conversations
- List of recent conversations
- Turn counts and summaries
- Click to view conversation details

### Captured Workspaces  
- Previously captured workspace states
- Capture dates and descriptions
- Click to view workspace details

## Usage

### Getting Started

1. **Configure Server Path**: Set the path to your Darbot Memory MCP server executable in settings
2. **Start Server**: Use the "Start MCP Server" command or enable auto-start
3. **Capture Workspace**: Use "Capture Current Workspace" to save your development state
4. **Search & Browse**: Use the conversation search and browsing features to find information

### Typical Workflow

1. **Development Session Start**: VS Code starts and optionally auto-starts the MCP server
2. **Workspace Capture**: Capture your workspace state at key development milestones
3. **Conversation Tracking**: The server automatically tracks AI conversations and tool usage
4. **Search & Reference**: Use the search functionality to find previous conversations and solutions
5. **Context Restoration**: Restore previous workspace states as needed

## Requirements

- VS Code 1.85.0 or higher
- Darbot Memory MCP server executable
- Node.js runtime (for extension execution)

## Architecture

The extension communicates with the MCP server via HTTP REST API:

```
VS Code Extension  ←→  Darbot Memory MCP Server  ←→  Storage Provider
     (UI/Commands)           (REST API)              (FileSystem/Git/Azure)
```

## Development

To develop and contribute to this extension:

1. Clone the repository
2. Run `npm install` in the extension directory
3. Open in VS Code and press F5 to launch extension development host
4. Make changes and test in the development instance

## Support

For issues, questions, or contributions:

- GitHub Issues: [Report bugs or request features](https://github.com/darbotlabs/darbot-memory-mcp/issues)
- Documentation: [Full documentation](https://github.com/darbotlabs/darbot-memory-mcp/blob/main/README.md)

## License

This extension is licensed under the MIT License. See [LICENSE](../LICENSE) for details.