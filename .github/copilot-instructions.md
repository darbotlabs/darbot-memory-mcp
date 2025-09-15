# GitHub Copilot Instructions for Darbot Memory MCP

## Project Overview

Darbot Memory MCP is an **enterprise-grade Model Context Protocol server** that provides persistent conversational audit trails. It captures every conversational turn—prompts, model responses, tool usage, and metadata—into tamper-evident Markdown files with cryptographic integrity verification.

### Key Features
- Multiple storage backends (FileSystem, Git, Azure Blob)
- Cryptographic integrity with SHA256 hashing
- Comprehensive REST API following MCP protocol
- Health monitoring and structured logging
- Browser history integration (optional)
- Schema versioning for data evolution

## Architecture

The solution follows a layered architecture pattern:

```
src/
├── Darbot.Memory.Mcp.Api/          # Web API layer with controllers, health checks
├── Darbot.Memory.Mcp.Core/         # Business logic, services, domain models
├── Darbot.Memory.Mcp.Storage/      # Data access layer, storage providers
└── Darbot.Memory.Mcp.Tests/        # Unit and integration tests
```

### Core Components
- **API Layer**: ASP.NET Core Web API with OpenAPI/Swagger
- **Core Layer**: Business services, domain models, search functionality
- **Storage Layer**: Pluggable storage providers (FileSystem, Git, Azure Blob)
- **Health Monitoring**: Liveness and readiness probes

## Development Guidelines

### Code Style and Conventions

1. **C# Conventions**:
   - Follow Microsoft C# coding conventions
   - Use PascalCase for public members, camelCase for private fields
   - Prefer `var` for obvious types, explicit types for clarity
   - Use meaningful names that describe intent

2. **Async/Await Patterns**:
   - Always use async/await for I/O operations
   - Suffix async methods with `Async`
   - Use `ConfigureAwait(false)` in library code
   - Handle async method warnings (CS1998) appropriately

3. **Dependency Injection**:
   - Register services in `Program.cs` using extension methods
   - Prefer constructor injection over service locator pattern
   - Use interfaces for testability and loose coupling

4. **Error Handling**:
   - Use structured logging with Serilog
   - Throw specific exceptions, not generic ones
   - Include context in error messages
   - Use `IResult` pattern for API responses

### Project Structure Patterns

#### Controllers
- Keep controllers thin, delegate to services
- Use action filters for cross-cutting concerns
- Follow REST conventions for endpoints
- Use DTOs for request/response models

#### Services
- Implement business logic in service classes
- Use dependency injection for testability
- Follow single responsibility principle
- Return domain models, not DTOs

#### Storage Providers
- Implement `IConversationStorageProvider` interface
- Support batch operations for performance
- Include health check implementations
- Handle storage-specific exceptions gracefully

#### Models
- Use record types for DTOs and value objects
- Include validation attributes where appropriate
- Implement `IEquatable<T>` for value semantics
- Use nullable reference types consistently

### Testing Requirements

#### Unit Tests
- Test all public methods and edge cases
- Use xUnit as the testing framework
- Mock external dependencies using Moq
- Follow AAA pattern (Arrange, Act, Assert)
- Aim for >90% code coverage on core logic

#### Integration Tests
- Test API endpoints end-to-end
- Use `TestServer` for in-memory testing
- Test storage provider implementations
- Include health check endpoints

#### Test Organization
```csharp
// Example test class structure
public class ConversationServiceTests
{
    private readonly Mock<IConversationStorageProvider> _mockStorage;
    private readonly ConversationService _service;

    public ConversationServiceTests()
    {
        _mockStorage = new Mock<IConversationStorageProvider>();
        _service = new ConversationService(_mockStorage.Object);
    }

    [Fact]
    public async Task WriteConversationTurn_ValidInput_ReturnsSuccess()
    {
        // Arrange
        var turn = new ConversationTurn { /* test data */ };
        
        // Act
        var result = await _service.WriteConversationTurnAsync(turn);
        
        // Assert
        result.Should().NotBeNull();
        _mockStorage.Verify(/* verification */);
    }
}
```

### Configuration Management

#### appsettings.json Structure
```json
{
  "Darbot": {
    "Storage": {
      "Provider": "FileSystem|Git|AzureBlob",
      "FileSystem": { "RootPath": "./conversations" },
      "Git": { "RepositoryPath": "./conversations", "AutoCommit": true },
      "AzureBlob": { "ConnectionString": "", "ContainerName": "conversations" }
    },
    "Auth": {
      "Mode": "None|ApiKey|AzureAD"
    },
    "BrowserHistory": {
      "Enabled": false
    }
  }
}
```

#### Environment Variables
- Prefix all environment variables with `DARBOT__`
- Use double underscores (`__`) for nested configuration
- Support multiple deployment environments

### API Design Patterns

#### Endpoint Conventions
- Use versioned routes: `/v1/messages:write`
- Follow MCP protocol specifications
- Use HTTP methods appropriately (POST for commands, GET for queries)
- Return consistent response formats

#### Response Models
```csharp
public record ApiResponse<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public string? Error { get; init; }
    public string? RequestId { get; init; }
}
```

#### Health Checks
- Implement both liveness (`/health/live`) and readiness (`/health/ready`) probes
- Include storage provider health in readiness checks
- Return detailed information in development mode

### Security Considerations

1. **Authentication & Authorization**:
   - Support API key and Azure AD authentication
   - Use minimal required permissions
   - Validate all inputs

2. **Data Integrity**:
   - Generate SHA256 hashes for all conversation files
   - Store hashes in file metadata
   - Verify integrity on read operations

3. **Sensitive Data**:
   - Never log sensitive conversation content
   - Use structured logging to avoid accidental exposure
   - Implement data retention policies

### Build and Deployment

#### Local Development
```bash
# Restore packages
dotnet restore

# Build solution
dotnet build --configuration Release

# Run tests
dotnet test --verbosity normal

# Run API locally
dotnet run --project src/Darbot.Memory.Mcp.Api
```

#### Docker Deployment
- Use multi-stage Dockerfile for optimized images
- Set appropriate health check intervals
- Configure persistent volumes for data storage

#### CI/CD Pipeline
- Run linting with `dotnet format --verify-no-changes`
- Execute all tests with coverage reporting
- Build and push container images on successful tests
- Deploy to test/production environments

### Storage Provider Implementation

When implementing new storage providers:

1. **Interface Implementation**:
   ```csharp
   public class MyStorageProvider : IConversationStorageProvider
   {
       public async Task<ConversationTurn> WriteConversationTurnAsync(ConversationTurn turn)
       {
           // Implementation with proper error handling
       }
       
       public async Task<IEnumerable<ConversationTurn>> SearchConversationsAsync(SearchRequest request)
       {
           // Implementation with filtering and pagination
       }
   }
   ```

2. **Health Check Support**:
   - Implement `IHealthCheck` interface
   - Test connectivity and basic operations
   - Return meaningful error messages

3. **Configuration**:
   - Add configuration section for new provider
   - Support environment variable overrides
   - Include connection string validation

### Logging and Monitoring

#### Structured Logging
```csharp
_logger.LogInformation("Conversation turn persisted",
    new { ConversationId = turn.ConversationId, TurnNumber = turn.TurnNumber });
```

#### Key Metrics to Log
- Conversation persistence operations
- Search query performance
- Storage provider health status
- Authentication events
- Error rates and types

### Common Patterns to Follow

1. **Repository Pattern**: Use for data access abstraction
2. **Options Pattern**: For configuration management
3. **Result Pattern**: For operation outcomes with error details
4. **Factory Pattern**: For storage provider instantiation
5. **Strategy Pattern**: For different storage implementations

### Code Generation Guidelines

When generating new code:

1. **Follow existing patterns** in the codebase
2. **Include appropriate error handling** and logging
3. **Add corresponding unit tests** for new functionality
4. **Update documentation** when adding new features
5. **Consider performance implications** of new code
6. **Validate input parameters** and return meaningful errors
7. **Use dependency injection** for external dependencies
8. **Include XML documentation** for public APIs

### Performance Considerations

- Use asynchronous operations for I/O-bound work
- Implement pagination for large result sets
- Consider caching for frequently accessed data
- Monitor memory usage with large conversation histories
- Use connection pooling for database operations

### Troubleshooting Common Issues

1. **Async method warnings (CS1998)**: Either add await operations or remove async
2. **Storage provider failures**: Check connection strings and permissions
3. **Health check failures**: Verify storage provider connectivity
4. **High memory usage**: Review conversation batch sizes and caching
5. **Slow search performance**: Consider indexing strategies

This project prioritizes data integrity, performance, and enterprise-grade reliability. Always consider these aspects when implementing new features or making changes.