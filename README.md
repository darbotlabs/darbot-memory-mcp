# Darbot Memory MCP

> **Enterprise-grade Model Context Protocol server for persistent conversational audit trails**

[![CI](https://img.shields.io/github/actions/workflow/status/darbotlabs/darbot-memory-mcp/ci.yml)](../../actions)
[![Container Image](https://img.shields.io/badge/container-ghcr.io%2Fdarbotlabs%2Fdarbot--memory--mcp-blue)](https://github.com/orgs/darbotlabs/packages?repo_name=darbot-memory-mcp)
[![License](https://img.shields.io/github/license/darbotlabs/darbot-memory-mcp)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue)](https://dotnet.microsoft.com/download/dotnet/10.0)

Darbot Memory MCP is a **Model Context Protocol (MCP) server** that provides enterprise-grade conversational memory persistence. It captures every conversational turn—prompts, model responses, tool usage, and metadata—into tamper-evident Markdown files with cryptographic integrity verification.

Built for production environments, it offers pluggable storage backends, comprehensive health monitoring, and a complete REST API for conversational intelligence platforms.

---

## 🚀 Key Features

| Feature | Description |
|---------|-------------|
| **Multiple Storage Backends** | FileSystem, Git version control, and Azure Blob Storage with metadata tagging |
| **Cryptographic Integrity** | SHA256 hashing for tamper-evident audit trails |
| **Comprehensive API** | Full MCP protocol implementation with search, retrieval, and batch operations |
| **Enterprise Ready** | Health checks, structured logging, OpenAPI documentation, and monitoring |
| **Browser History Integration** | Optional Edge browser history sync and search capabilities |
| **Schema Versioning** | Future-proof data evolution with semantic versioning |

---

## 📋 Table of Contents

1. [Architecture](#architecture)
2. [Quick Start](#quick-start)
3. [Configuration](#configuration)
4. [API Endpoints](#api-endpoints)
5. [Storage Providers](#storage-providers)
6. [Deployment](#deployment)
7. [Development](#development)
8. [Contributing](#contributing)

---

## 🏗️ Architecture

```
┌─────────────┐   HTTP/REST   ┌─────────────────┐   Markdown   ┌─────────────┐
│ AI Clients  │──────────────▶│ Darbot Memory   │─────────────▶│ Storage     │
│ (LLMs/Apps) │               │     MCP API     │              │ Provider    │
└─────────────┘               └─────────────────┘              └─────────────┘
                                     ▲  ▲
                                     │  │ Observability
                                     │  └──▶ Structured Logs
                                     └─────▶ Health Checks

Storage Providers:
├── FileSystemStorageProvider    # Local markdown files
├── GitStorageProvider          # Version-controlled storage
└── AzureBlobStorageProvider    # Cloud storage with metadata
```

**Core Components:**
- **MCP API Layer**: RESTful endpoints following MCP specification
- **Conversation Service**: Business logic for conversation management
- **Storage Abstraction**: Pluggable provider interface
- **Health Monitoring**: Liveness and readiness probes
- **Authentication**: API key and Azure AD support

---

## ⚡ Quick Start

### Prerequisites

- .NET 10.0 SDK
- Docker (optional)

### 1. Run with Docker

```bash
docker run --rm -p 8080:80 \
  -e DARBOT__STORAGE__FILESYSTEM__ROOTPATH=/data \
  -v "$(pwd)/conversations":/data \
  ghcr.io/darbotlabs/darbot-memory-mcp:latest
```

### 2. Run from Source

```bash
git clone https://github.com/darbotlabs/darbot-memory-mcp.git
cd darbot-memory-mcp
dotnet run --project src/Darbot.Memory.Mcp.Api
```

### 3. Verify Installation

```bash
curl http://localhost:8080/health/ready
curl http://localhost:8080/info
```

Access Swagger UI at: `http://localhost:8080/swagger`

---

## ⚙️ Configuration

Configuration follows the ASP.NET Core pattern and supports multiple sources:

### Environment Variables

```bash
# Storage Provider
export DARBOT__STORAGE__PROVIDER=FileSystem
export DARBOT__STORAGE__FILESYSTEM__ROOTPATH=/data/conversations

# Authentication  
export DARBOT__AUTH__MODE=ApiKey
export DARBOT__AUTH__APIKEY=your-secret-key

# Azure Blob Storage (when using AzureBlob provider)
export DARBOT__STORAGE__AZUREBLOB__CONNECTIONSTRING="DefaultEndpointsProtocol=https;..."
export DARBOT__STORAGE__AZUREBLOB__CONTAINERNAME=conversations
```

### Configuration File (appsettings.json)

```json
{
  "Darbot": {
    "Storage": {
      "Provider": "FileSystem",
      "FileSystem": {
        "RootPath": "./conversations"
      },
      "AzureBlob": {
        "ConnectionString": "DefaultEndpointsProtocol=https;...",
        "ContainerName": "conversations"
      },
      "Git": {
        "RepositoryPath": "./conversations",
        "AutoCommit": true,
        "AutoPush": false
      }
    },
    "Auth": {
      "Mode": "None"
    },
    "Mcp": {
      "MaxConcurrentClients": 10,
      "ConnectionTimeout": "00:01:00",
      "KeepAliveInterval": "00:00:30"
    },
    "BrowserHistory": {
      "Enabled": false
    }
  },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://localhost:5093" }
    },
    "Limits": {
      "MaxConcurrentConnections": 100,
      "KeepAliveTimeout": "00:02:00",
      "RequestHeadersTimeout": "00:00:30"
    }
  }
}
```

The `Kestrel` section controls the listening port (default `5093`) and connection limits; `Darbot:Mcp` limits concurrent MCP clients and connection timeouts.

---

## 🔌 API Endpoints

### Core MCP Operations

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/v1/messages:write` | Write single conversation turn |
| `POST` | `/v1/messages:batchWrite` | Batch write multiple turns |
| `POST` | `/v1/conversations:search` | Search conversations with filters |
| `POST` | `/v1/conversations:list` | List conversations with pagination |
| `GET` | `/v1/conversations/{id}` | Get all turns for a conversation |
| `GET` | `/v1/conversations/{id}/turns/{number}` | Get specific turn |

### Health & Monitoring

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/health/live` | Liveness probe |
| `GET` | `/health/ready` | Readiness probe (includes storage checks) |
| `GET` | `/info` | API information and capabilities |
| `GET` | `/swagger` | Interactive API documentation |

### Browser History (Optional)

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/v1/browser-history:sync` | Sync browser history |
| `POST` | `/v1/browser-history:search` | Search browser history |
| `GET` | `/v1/browser-history/profiles` | Get available browser profiles |

### Example Usage

```bash
# Write a conversation turn
curl -X POST http://localhost:8080/v1/messages:write \
  -H "Content-Type: application/json" \
  -d '{
    "conversationId": "conv-123",
    "turnNumber": 1,
    "prompt": "What is machine learning?",
    "response": "Machine learning is...",
    "model": "gpt-4",
    "timestamp": "2024-01-01T12:00:00Z"
  }'

# Search conversations
curl -X POST http://localhost:8080/v1/conversations:search \
  -H "Content-Type: application/json" \
  -d '{
    "textQuery": "machine learning",
    "dateRange": {
      "start": "2024-01-01T00:00:00Z",
      "end": "2024-01-31T23:59:59Z"
    }
  }'
```

---

## 💾 Storage Providers

### FileSystem Provider (Default)

Stores conversations as Markdown files in a local directory structure:

```
conversations/
├── 2024/
│   ├── 01/
│   │   └── conv-123_turn-1_20240101T120000Z.md
│   └── 02/
└── index/
```

### Git Provider

Version-controlled storage with automatic commits:

```json
{
  "Storage": {
    "Provider": "Git",
    "Git": {
      "RepositoryPath": "./conversations",
      "AutoCommit": true,
      "AutoPush": false,
      "CommitMessage": "Add conversation turn {conversationId}:{turnNumber}"
    }
  }
}
```

### Azure Blob Storage Provider

Cloud-native storage with rich metadata and tagging:

```json
{
  "Storage": {
    "Provider": "AzureBlob",
    "AzureBlob": {
      "ConnectionString": "DefaultEndpointsProtocol=https;...",
      "ContainerName": "conversations"
    }
  }
}
```

Features:
- Automatic blob metadata for searchability
- Tags for categorization
- Batch operations support
- Health monitoring

---

## 🚀 Deployment

### Azure Container Apps

```bash
az deployment group create \
  --resource-group darbot-rg \
  --template-file templates/aca.bicep \
  --parameters containerImage=ghcr.io/darbotlabs/darbot-memory-mcp:latest
```

### Kubernetes with Helm

```bash
helm install darbot-memory ./charts/darbot-memory-mcp \
  --set image.repository=ghcr.io/darbotlabs/darbot-memory-mcp \
  --set image.tag=latest \
  --set storage.provider=AzureBlob
```

### Docker Compose

```yaml
version: '3.8'
services:
  darbot-memory:
    image: ghcr.io/darbotlabs/darbot-memory-mcp:latest
    ports:
      - "8080:80"
    environment:
      - DARBOT__STORAGE__PROVIDER=FileSystem
      - DARBOT__STORAGE__FILESYSTEM__ROOTPATH=/data
    volumes:
      - ./conversations:/data
```

---

## 🔒 Security

| Feature | Implementation |
|---------|----------------|
| **Authentication** | API Key or Azure AD JWT tokens |
| **Authorization** | Role-based access control |
| **Data Integrity** | SHA256 cryptographic hashing |
| **Encryption at Rest** | Storage provider dependent (Azure SSE, etc.) |
| **Audit Logging** | Structured logs for all operations |

---

## 📁 Message Format

Each conversation turn is stored as a Markdown file with cryptographic integrity:

```markdown
<!-- SchemaVersion: v1.0.0 -->
# Darbot Conversation Log

**ConversationId:** `conv-123`  
**Turn:** `1`  
**Timestamp (UTC):** `2024-01-01T12:00:00Z`  
**Model:** `gpt-4`  
**Hash:** `sha256-4a7d9c2f...`

---

## Prompt
What is machine learning?

## Response
Machine learning is a subset of artificial intelligence...

## Tools Used
- search_query
- code_execution

## Metadata
- **Temperature:** 0.7
- **Max Tokens:** 2048
- **Duration:** 1.2s
```

---

## 🛠️ Development

### Build from Source

```bash
git clone https://github.com/darbotlabs/darbot-memory-mcp.git
cd darbot-memory-mcp
dotnet restore
dotnet build --configuration Release
```

### Run Tests

```bash
dotnet test --configuration Release --verbosity normal
```

### Local Development

```bash
dotnet run --project src/Darbot.Memory.Mcp.Api
```

The API will be available at `http://localhost:5000` with Swagger UI at `/swagger`.

---

## 📊 Monitoring

### Health Checks

- **Liveness** (`/health/live`): Basic service availability
- **Readiness** (`/health/ready`): Full health including storage connectivity

### Logging

Structured JSON logging with Serilog:

```json
{
  "@t": "2024-01-01T12:00:00.000Z",
  "@l": "Information", 
  "@m": "Conversation turn persisted",
  "ConversationId": "conv-123",
  "TurnNumber": 1,
  "StorageProvider": "FileSystem"
}
```

### Metrics (Future)

- Conversation persistence rate
- Storage provider health
- API response times
- Error rates

---

## 🤝 Contributing

We welcome contributions! Please see our [Contributing Guide](CONTRIBUTING.md) for details.

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

---

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---

## 🔗 Related Projects

- [Model Context Protocol Specification](https://modelcontextprotocol.io)
- [Darbot AI Platform](https://github.com/darbotlabs)

---

**Built with ❤️ by the Darbot Labs team - Empowering transparent AI conversations through persistent memory.**