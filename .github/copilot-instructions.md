# GitHub Copilot Instructions for Darbot Memory MCP

Always reference these instructions first and fallback to search or bash commands only when you encounter unexpected information that does not match the info here.

## Project Overview

Darbot Memory MCP is an **enterprise-grade Model Context Protocol server** that provides persistent conversational audit trails. It captures every conversational turn—prompts, model responses, tool usage, and metadata—into tamper-evident Markdown files with cryptographic integrity verification.

### Key Features
- Multiple storage backends (FileSystem, Git, Azure Blob)
- Cryptographic integrity with SHA256 hashing
- Comprehensive REST API following MCP protocol
- Health monitoring and structured logging
- Browser history integration (optional)
- Schema versioning for data evolution

## Working Effectively

### Bootstrap and Setup
Run these commands in order for initial setup:
- `pwsh ./scripts/bootstrap.ps1` -- takes ~47 seconds. NEVER CANCEL. Set timeout to 90+ minutes.
  - Installs .NET tools (nbgv, dotnet-format, dotnet-outdated-tool)
  - Sets up git hooks
  - Restores NuGet packages
  - Builds solution (Debug configuration)
  - Runs all tests
  - Creates version.json if needed

### Build Commands
- `dotnet restore` -- ~13 seconds for full restore. Set timeout to 30+ minutes.
- `dotnet build --configuration Release` -- ~2.9 seconds (when restored). NEVER CANCEL. Set timeout to 60+ minutes.
- `dotnet build --configuration Debug` -- similar timing to Release
- Fresh builds from clean state take ~12-15 seconds

### Testing
- `dotnet test --configuration Release --verbosity normal` -- ~9.8 seconds, runs 51 tests. NEVER CANCEL. Set timeout to 30+ minutes.
- `dotnet test --no-build --verbosity quiet` -- faster when already built
- All tests must pass before committing

### Pre-commit Validation
- `pwsh ./scripts/pre-commit.ps1` -- ~20 seconds. NEVER CANCEL. Set timeout to 60+ minutes.
  - Builds solution
  - Checks code formatting (dotnet format --verify-no-changes)
  - Runs static analysis
  - Runs all tests
  - Checks for TODO/FIXME comments (warns but doesn't fail)
  - Checks for large files (>1MB)
- `pwsh ./scripts/pre-commit.ps1 -Fix` -- auto-fixes formatting issues

### Running the Application
- `dotnet run --project src/Darbot.Memory.Mcp.Api` -- starts API on http://localhost:5093
- Application starts in ~5 seconds
- Access Swagger UI at: http://localhost:5093/swagger
- Health endpoints: http://localhost:5093/health/live and http://localhost:5093/health/ready

## Validation Scenarios

### ALWAYS Manually Test These Scenarios After Making Changes

#### 1. Basic Health Check
```bash
curl http://localhost:5093/health/ready
# Expected: "Healthy"

curl http://localhost:5093/info
# Expected: JSON with endpoints and version info
```

#### 2. Conversation Storage and Retrieval
```bash
# Write a conversation turn (requires utcTimestamp)
curl -X POST http://localhost:5093/v1/messages:write -H "Content-Type: application/json" -d '{
  "conversationId": "test-validation-conv-001",
  "turnNumber": 1,
  "utcTimestamp": "2025-09-19T05:50:00Z",
  "prompt": "Hello! Can you explain what Darbot Memory MCP does?",
  "model": "test-model",
  "response": "Darbot Memory MCP is an enterprise-grade Model Context Protocol server...",
  "toolsUsed": ["conversation_storage", "documentation_lookup"]
}'
# Expected: {"success":true,"message":"Message persisted successfully"}

# Verify file creation
find . -name "*.md" -path "*/conversations/*" -exec ls -la {} \;
# Expected: Shows created conversation file with timestamp and conversation ID
```

#### 3. Search Functionality  
```bash
curl -X GET "http://localhost:5093/v1/conversations:search?q=Darbot"
# Expected: JSON response with search results
```

## Architecture and Project Structure

### Solution Structure
```
src/
├── Darbot.Memory.Mcp.Api/          # Web API layer - controllers, health checks, authentication
├── Darbot.Memory.Mcp.Core/         # Business logic - services, domain models, search
├── Darbot.Memory.Mcp.Storage/      # Data access - storage providers (FileSystem, Git, Azure)
└── Darbot.Memory.Mcp.Tests/        # Unit and integration tests (51 total tests)
```

### Key Entry Points
- `src/Darbot.Memory.Mcp.Api/Program.cs` -- Application startup and DI configuration
- `src/Darbot.Memory.Mcp.Core/Services/ConversationService.cs` -- Main business logic
- `src/Darbot.Memory.Mcp.Storage/Providers/` -- Storage implementations
- `src/Darbot.Memory.Mcp.Tests/` -- Test files, especially SearchFunctionalityTests.cs

### Configuration Files
- `src/Darbot.Memory.Mcp.Api/appsettings.json` -- Main configuration
- `src/Darbot.Memory.Mcp.Api/appsettings.Development.json` -- Development overrides
- `version.json` -- NerdBank GitVersioning configuration
- `scripts/bootstrap.ps1` -- Development environment setup
- `scripts/pre-commit.ps1` -- Pre-commit validation

## Known Issues and Gotchas

### Build Warnings (Expected)
The build produces 16 warnings related to async methods without await operators in search functionality:
- `/Search/RelevanceScorer.cs` -- CS1998 warnings on lines 25, 365, 396, 415
- `/Search/EnhancedSearchService.cs` -- CS1998 warnings on lines 311, 341, 373, 406, 487
- `/Search/ConversationContextManager.cs` -- CS1998 warnings on lines 114, 151, 207, 251, 293
- `/Search/QueryParser.cs` -- CS1998 warning on line 20
- `/Search/RelevanceScorer.cs` -- CS0414 warning on line 15 (unused field)

These are expected and should not be "fixed" as they represent future async extension points.

### Version Tool Issues
The `nbgv` tool fails in shallow clones with "Shallow clone lacks the objects required to calculate version height" error. This is expected in CI/PR environments and does not affect functionality.

### API Requirements
- All conversation turns MUST include a valid `utcTimestamp` in ISO 8601 format
- Content-Type must be `application/json` for POST requests
- The API uses file-based storage by default in `./src/Darbot.Memory.Mcp.Api/data/conversations/`

## Common Development Tasks

### Adding New Features
1. Always run `pwsh ./scripts/bootstrap.ps1` first if working in a fresh clone
2. Make minimal changes following the existing patterns
3. Add corresponding unit tests in the Tests project
4. Run `dotnet test` to ensure all tests pass
5. Run `pwsh ./scripts/pre-commit.ps1 -Fix` to format code and validate
6. Test manually using the validation scenarios above
7. Always validate that the API starts and responds to health checks

### Debugging Issues
1. Check application logs in `./logs/darbot-memory-mcp-*.txt`
2. Verify conversation files are created in `./src/Darbot.Memory.Mcp.Api/data/conversations/`
3. Use Swagger UI at http://localhost:5093/swagger for API exploration
4. Check health endpoints for storage provider status

### CI/CD Pipeline Compatibility
The project uses GitHub Actions with:
- .NET 8.0 SDK requirement
- PowerShell 7.x for scripts
- Docker support via included Dockerfile
- Code formatting checks with `dotnet format --verify-no-changes`
- Full test suite execution

## Dependencies and Tools

### Required Tools
- .NET 8.0 SDK (minimum requirement)
- PowerShell 7.x (for scripts)
- Git (for version control and GitVersioning)

### Auto-installed Tools (via bootstrap script)
- `nbgv` (NerdBank GitVersioning)
- `dotnet-format` (code formatting)
- `dotnet-outdated-tool` (dependency analysis)

### Key NuGet Packages
- ASP.NET Core 8.0 (web framework)
- Serilog (structured logging)
- xUnit (testing framework)
- Microsoft.Extensions.* (configuration, DI, health checks)
- Azure SDK (for blob storage provider)

## Performance Expectations

All timing measurements from validation on Linux environment:
- Bootstrap (cold start): ~47 seconds
- Package restore: ~13 seconds
- Build (Release): ~2.9 seconds (when packages restored)
- Test suite (51 tests): ~9.8 seconds
- Pre-commit checks: ~20 seconds
- Application startup: ~5 seconds
- API response time: <100ms for simple operations

Set timeouts generously to account for varying system performance:
- Build operations: 60+ minutes timeout
- Test operations: 30+ minutes timeout
- Bootstrap: 90+ minutes timeout

## Security and Data Integrity

### File Storage
- All conversation files include SHA256 hashes for tamper detection
- Files are created with specific naming convention: `YYYYMMDD-HHMMSS_conversationId_turnNumber.md`
- Header metadata acts as tamper-evident seal

### Authentication
- Supports None, ApiKey, and AzureAD authentication modes
- Default configuration uses None for development
- Production deployments should configure appropriate auth mode

### Logging
- Structured logging via Serilog
- Separate log levels for Development vs Production
- Log files rotate daily
- Avoid logging sensitive conversation content

## Development Guidelines Summary

Follow these key principles when working with the codebase:

### Code Standards
- Follow Microsoft C# coding conventions
- Use meaningful names and include XML documentation for public APIs
- Use dependency injection for testability
- Include unit tests for all new functionality (currently 51 tests)
- Use Serilog for structured logging

### Pattern Guidelines
- Controllers should be thin, delegate to services
- Implement storage providers using `IConversationStorageProvider` interface
- Use record types for DTOs and value objects
- Follow async/await patterns consistently
- Handle the expected CS1998 warnings in search functionality (these are intentional)

### Configuration
- Main config: `src/Darbot.Memory.Mcp.Api/appsettings.json`
- Environment variables use `DARBOT__` prefix with double underscores for nesting
- Storage providers: FileSystem (default), Git, AzureBlob
- Authentication modes: None (default), ApiKey, AzureAD

Always prioritize data integrity, performance, and enterprise-grade reliability when implementing new features or making changes.