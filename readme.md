# Darbot Memory MCP (darbot-memory-mcp)

> **Microsoft‑compliant Memory Connector for Darbot’s Conversational Audit Trail**

[![CI](https://img.shields.io/github/actions/workflow/status/your‑org/darbot-memory-mcp/ci.yml)](../../actions)
[![Container Image](https://img.shields.io/badge/container-ghcr.io%2Fyour‑org%2Fdarbot--memory--mcp-blue)](https://github.com/orgs/your‑org/packages?repo_name=darbot-memory-mcp)
[![License](https://img.shields.io/github/license/your‑org/darbot-memory-mcp)](LICENSE)

Darbot Memory MCP is a **Managed Connector Platform (MCP) server** that persists every conversational turn—prompt, model, tool usage, and response—into version‑controlled Markdown (`*.md`) files.  
It enables transparent, tamper‑evident conversation auditing that can be indexed, diffed, and shared just like source code.

---

## Table of Contents
1. [Solution Highlights](#solution-highlights)  
2. [Architecture](#architecture)  
3. [Directory Layout](#directory-layout)  
4. [Prerequisites](#prerequisites)  
5. [Installation](#installation)  
6. [Configuration](#configuration)  
7. [Running Locally](#running-locally)  
8. [Message File Format](#message-file-format-spec)  
9. [Logging & Telemetry](#logging--telemetry)  
10. [Security & Compliance](#security--compliance)  
11. [Deployment](#deployment-options)  
12. [CI/CD Pipeline](#cicd-pipeline)  
13. [Contribution Guide](#contribution-guide)  
14. [License](#license)

---

## Solution Highlights
| Capability | Description |
|-----------|-------------|
| **Full Audit Trail** | Archives every turn to Markdown with cryptographic timestamping. |
| **Microsoft‑native Standards** | Implements the [MCP Connector Runtime](https://learn.microsoft.com/) conventions for health probes, settings, and packaging. |
| **Pluggable Storage** | Local filesystem by default; swap in Azure Blob, SharePoint, or Git via a simple provider interface. |
| **Schema Stability** | Message header schema is version‑tagged to avoid future breaking changes. |
| **DevOps‑Ready** | Ships with Bicep/IaC templates, Helm chart, and a GitHub Actions workflow mirroring Microsoft’s reference pipelines. |



## Architecture


┌────────────┐   HTTP/gRPC   ┌────────────────┐   Markdown   ┌────────────┐
│ LLM Client │──────────────▶│ Darbot Memory  │─────────────▶│ Storage    │
│ / Darbot   │               │     MCP        │              │ Provider   │
└────────────┘               └────────────────┘              └────────────┘
                                    ▲  ▲
                                    │  │ Prom/OTLP
                                    │  └──▶ Observability Stack
                                    └─────▶ Azure App Insights (optional)


* **Inbound** – Exposes the \[MCP standard `/v1/messages:batchWrite`] endpoint.
* **Core** – Formats each turn, validates schema, and signs the record.
* **Outbound** – Writes to the configured provider (local path, Git repo, Blob, etc.).
* **Ops** – Liveness/readiness probes, OTLP metrics, structured logs.



## Directory Layout


darbot-memory-mcp/
├── charts/               # Helm chart (Kubernetes)
├── docs/                 # Spec PDFs, diagrams
├── src/
│   ├── Darbot.Memory.Mcp.Api/      # ASP.NET Core service
│   ├── Darbot.Memory.Mcp.Core/     # Domain logic & interfaces
│   ├── Darbot.Memory.Mcp.Storage/  # FileSystem, Blob, Git providers
│   └── Darbot.Memory.Mcp.Tests/
├── templates/            # Bicep & ARM infra
├── .devcontainer/        # VS Code Codespaces
├── .github/
│   └── workflows/        # CI/CD pipelines
├── CONTRIBUTING.md
└── README.md             # You are here




## Prerequisites

| Component                               | Minimum | Notes               |
| --------------------------------------- | ------- | ------------------- |
| .NET SDK                                | **8.0** | `dotnet --version`  |
| PowerShell                              | 7.x     | For scripts         |
| Docker                                  | 24.x    | Container build/run |
| **Optional**: Azure CLI 2.60, Helm 3.15 |         |                     |



## Installation

### 1. Clone & bootstrap

```bash
git clone https://github.com/your-org/darbot-memory-mcp.git
cd darbot-memory-mcp
pwsh ./scripts/bootstrap.ps1   # installs git hooks, tools/nbgv
```

### 2. Restore & build

```bash
dotnet restore
dotnet build --configuration Release
```

### 3. Build container image

```bash
docker build -t ghcr.io/your-org/darbot-memory-mcp:local .
```

---

## Configuration

All settings follow the **Microsoft.Extensions.Configuration** pattern and can be supplied via:

* `appsettings.json`
* Environment variables (`DARBOT__...`)
* Azure App Configuration / Key Vault

| Setting                       | Description                          | Default           |
| ----------------------------- | ------------------------------------ | ----------------- |
| `Storage:Provider`            | `FileSystem` \| `AzureBlob` \| `Git` | `FileSystem`      |
| `Storage:FileSystem:RootPath` | Folder where `.md` files are stored  | `./conversations` |
| `FileNameTemplate`            | `%utc%_%conversationId%_%turn%.md`   | —                 |
| `HashAlgorithm`               | `SHA256`                             | —                 |
| `Cors:AllowedOrigins`         | Comma‑separated list                 | `*`               |
| `Auth:Mode`                   | `None` \| `AAD` \| `APIKey`          | `None`            |

### Example `appsettings.Development.json`

```jsonc
{
  "Storage": {
    "Provider": "FileSystem",
    "FileSystem": {
      "RootPath": "C:\\Darbot\\Memory"
    }
  },
  "Auth": { "Mode": "AAD" }
}
```

Environment variable override:

```bash
export DARBOT__STORAGE__FILESYSTEM__ROOTPATH=/mnt/darbot
```

---

## Running Locally

```bash
docker run --rm -p 8080:80 \
  -e DARBOT__STORAGE__FILESYSTEM__ROOTPATH=/data \
  -v "$(pwd)/data":/data \
  ghcr.io/your-org/darbot-memory-mcp:local
```

Health probes:

* `GET /health/live`
* `GET /health/ready`

---

## Message File Format Spec

Each Markdown file captures a single turn:

```markdown
<!-- SchemaVersion: v1.0.0 -->
# Darbot Conversation Log
*ConversationId:* `92bf7...`  
*Turn:* `42`  
*Timestamp (UTC):* `2025‑06‑25T18:32:10Z`  
*Hash:* `sha256‑4a7d…`

---

## Prompt
> *User:* “How do I set up VLANs for my harvesters?”

## Model
`gpt‑4o` (OpenAI, temperature 0.25)

## Tools Used
- `search_query`
- `weather`

## Response
```

(assistant’s markdown response here)

```
```

*Lines above the horizontal rule (`---`) act as a tamper‑evident header.*
**Important:** Do **not** alter the header once committed.

---

## Logging & Telemetry

* **Logs** – Structured JSON @ `Information` level via **Serilog**, routable to Azure Monitor.
* **Metrics** – Prometheus/Otel counters: `messages_total`, `errors_total`, `write_latency_ms`.
* **Tracing** – Otel spans exported to Azure Monitor or Zipkin.

Enable diagnostics locally:

```bash
dotnet run --project src/Darbot.Memory.Mcp.Api \
  --Logging:LogLevel:Default=Debug \
  --Darbot:Diagnostics:Otel:Exporter=console
```

---

## Security & Compliance

| Area                   | Implementation                                    |
| ---------------------- | ------------------------------------------------- |
| **Authentication**     | Optional AAD JWT bearer or static API key.        |
| **Authorization**      | Policy‑based; sample role `Darbot.Memory.Writer`. |
| **Encryption at Rest** | Support for Azure Blob SSE or Git‑Crypt.          |
| **PII Scrubbing**      | Regex‑based filter pipeline before persist.       |
| **Audit**              | All writes emit an `AuditEvent` (OMS).            |

---

## Deployment Options

| Target                   | Artifact                   | Steps                                                    |
| ------------------------ | -------------------------- | -------------------------------------------------------- |
| **Azure Container Apps** | `aca.bicep`                | `az deployment sub create -f templates/aca.bicep`        |
| **AKS + Helm**           | `charts/darbot-memory-mcp` | `helm install darbot-memory ./charts`                    |
| **App Service (Linux)**  | Docker image               | `az webapp create --deployment-container-image-name ...` |

*Set secrets with `az keyvault secret set` or GitHub OIDC.*

---

## CI/CD Pipeline

**`.github/workflows/ci.yml`**

1. Lint & unit tests (`dotnet test`)
2. Build & push container image to GHCR tagged with `nbgv get-version`
3. OWASP dependency scan
4. Deploy to **Test** environment using environment‑scoped secrets
5. **Manual approval** gate → deploy to **Prod**

---

## Contribution Guide

* Fork → feature branch → PR
* Run `pwsh ./scripts/pre‑commit.ps1` to auto‑format.
* All new features require unit + integration tests.
* Respect [Microsoft Open Source Code of Conduct](CODE_OF_CONDUCT.md).

---

## License

Darbot Memory MCP is released under the **MIT License** – see [LICENSE](LICENSE) for details.

---

*Built with ❤ by the Darbot team – empowering transparent AI conversations.*
