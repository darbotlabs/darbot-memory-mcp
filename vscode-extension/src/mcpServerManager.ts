import * as vscode from 'vscode';
import * as cp from 'child_process';
import * as path from 'path';
import axios from 'axios';

export class McpServerManager {
    private serverProcess: cp.ChildProcess | undefined;
    private readonly config = vscode.workspace.getConfiguration('darbotMemoryMcp');

    constructor() {}

    public async startServer(): Promise<void> {
        if (this.isRunning()) {
            throw new Error('MCP Server is already running');
        }

        const serverPath = this.config.get<string>('serverPath');
        if (!serverPath) {
            throw new Error('MCP Server path not configured. Please set darbotMemoryMcp.serverPath in settings.');
        }

        const serverPort = this.config.get<number>('serverPort', 5093);
        const storagePath = this.config.get<string>('storagePath', './conversations');
        const authMode = this.config.get<string>('authMode', 'None');

        try {
            const env = {
                ...process.env,
                ASPNETCORE_URLS: `http://localhost:${serverPort}`,
                DARBOT__STORAGE__PROVIDER: 'FileSystem',
                DARBOT__STORAGE__FILESYSTEM__ROOTPATH: storagePath,
                DARBOT__AUTH__MODE: authMode
            };

            this.serverProcess = cp.spawn(serverPath, [], {
                env,
                detached: false,
                stdio: ['pipe', 'pipe', 'pipe']
            });

            this.serverProcess.stdout?.on('data', (data) => {
                console.log(`MCP Server: ${data}`);
            });

            this.serverProcess.stderr?.on('data', (data) => {
                console.error(`MCP Server Error: ${data}`);
            });

            this.serverProcess.on('error', (error) => {
                console.error('MCP Server Process Error:', error);
                this.serverProcess = undefined;
            });

            this.serverProcess.on('exit', (code, signal) => {
                console.log(`MCP Server exited with code ${code} and signal ${signal}`);
                this.serverProcess = undefined;
            });

            // Wait a bit for the server to start
            await this.waitForServer(serverPort);
        } catch (error) {
            this.serverProcess = undefined;
            throw error;
        }
    }

    public async stopServer(): Promise<void> {
        if (!this.isRunning()) {
            return;
        }

        return new Promise<void>((resolve) => {
            if (this.serverProcess) {
                this.serverProcess.on('exit', () => {
                    this.serverProcess = undefined;
                    resolve();
                });

                this.serverProcess.kill('SIGTERM');
                
                // Force kill after 5 seconds
                setTimeout(() => {
                    if (this.serverProcess) {
                        this.serverProcess.kill('SIGKILL');
                    }
                }, 5000);
            } else {
                resolve();
            }
        });
    }

    public isRunning(): boolean {
        return this.serverProcess !== undefined && !this.serverProcess.killed;
    }

    public async getServerStatus(): Promise<any> {
        if (!this.isRunning()) {
            return { status: 'stopped' };
        }

        try {
            const serverPort = this.config.get<number>('serverPort', 5093);
            const response = await axios.get(`http://localhost:${serverPort}/health/live`, {
                timeout: 5000
            });
            
            return {
                status: 'running',
                health: response.status === 200 ? 'healthy' : 'unhealthy',
                port: serverPort
            };
        } catch (error) {
            return {
                status: 'running',
                health: 'unhealthy',
                error: error
            };
        }
    }

    public async captureWorkspace(name: string): Promise<any> {
        const serverPort = this.config.get<number>('serverPort', 5093);
        const apiKey = this.config.get<string>('apiKey');

        const headers: any = {
            'Content-Type': 'application/json'
        };

        if (apiKey) {
            headers['X-API-Key'] = apiKey;
        }

        const captureRequest = {
            name: name,
            description: `Workspace captured from VS Code at ${new Date().toISOString()}`,
            captureOptions: {
                includeBrowserHistory: false,
                includeApplicationState: true,
                includeConversationReferences: true
            }
        };

        const response = await axios.post(
            `http://localhost:${serverPort}/v1/workspaces:capture`,
            captureRequest,
            { headers, timeout: 30000 }
        );

        return response.data;
    }

    public async searchConversations(query: string): Promise<any> {
        const serverPort = this.config.get<number>('serverPort', 5093);
        const apiKey = this.config.get<string>('apiKey');

        const headers: any = {
            'Content-Type': 'application/json'
        };

        if (apiKey) {
            headers['X-API-Key'] = apiKey;
        }

        const searchRequest = {
            query: query,
            limit: 50,
            includeContent: true
        };

        const response = await axios.post(
            `http://localhost:${serverPort}/v1/conversations:search`,
            searchRequest,
            { headers, timeout: 10000 }
        );

        return response.data;
    }

    public async listConversations(): Promise<any> {
        const serverPort = this.config.get<number>('serverPort', 5093);
        const apiKey = this.config.get<string>('apiKey');

        const headers: any = {
            'Content-Type': 'application/json'
        };

        if (apiKey) {
            headers['X-API-Key'] = apiKey;
        }

        const listRequest = {
            limit: 20,
            sortBy: 'lastActivity',
            sortOrder: 'desc'
        };

        const response = await axios.post(
            `http://localhost:${serverPort}/v1/conversations:list`,
            listRequest,
            { headers, timeout: 10000 }
        );

        return response.data;
    }

    public async listWorkspaces(): Promise<any> {
        const serverPort = this.config.get<number>('serverPort', 5093);
        const apiKey = this.config.get<string>('apiKey');

        const headers: any = {
            'Content-Type': 'application/json'
        };

        if (apiKey) {
            headers['X-API-Key'] = apiKey;
        }

        const listRequest = {
            limit: 20,
            sortBy: 'capturedAt',
            sortOrder: 'desc'
        };

        const response = await axios.post(
            `http://localhost:${serverPort}/v1/workspaces:list`,
            listRequest,
            { headers, timeout: 10000 }
        );

        return response.data;
    }

    public dispose(): void {
        if (this.isRunning()) {
            this.stopServer();
        }
    }

    private async waitForServer(port: number, timeoutMs: number = 10000): Promise<void> {
        const startTime = Date.now();
        
        while (Date.now() - startTime < timeoutMs) {
            try {
                await axios.get(`http://localhost:${port}/health/live`, { timeout: 2000 });
                return; // Server is ready
            } catch (error) {
                // Server not ready yet, wait a bit
                await new Promise(resolve => setTimeout(resolve, 500));
            }
        }

        throw new Error(`Server failed to start within ${timeoutMs}ms`);
    }
}