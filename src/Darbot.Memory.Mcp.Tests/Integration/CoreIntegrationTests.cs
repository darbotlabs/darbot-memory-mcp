using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Darbot.Memory.Mcp.Core.Models;
using FluentAssertions;

namespace Darbot.Memory.Mcp.Tests.Integration;

/// <summary>
/// Simplified integration tests for core MCP functionality
/// </summary>
public class CoreIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions;

    public CoreIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<Core.Configuration.DarbotConfiguration>(config =>
                {
                    config.Storage.Provider = "FileSystem";
                    config.Storage.FileSystem.RootPath = Path.Combine(Path.GetTempPath(), "mcp-core-tests", Guid.NewGuid().ToString());
                    config.Auth.Mode = "None";
                });
            });
        });

        _client = _factory.CreateClient();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    [Fact]
    public async Task HealthChecks_Should_Return_Healthy_Status()
    {
        // Act
        var liveResponse = await _client.GetAsync("/health/live");
        var readyResponse = await _client.GetAsync("/health/ready");

        // Assert
        liveResponse.IsSuccessStatusCode.Should().BeTrue();
        readyResponse.IsSuccessStatusCode.Should().BeTrue();

        var liveContent = await liveResponse.Content.ReadAsStringAsync();
        var readyContent = await readyResponse.Content.ReadAsStringAsync();

        liveContent.Should().Be("Healthy");
        readyContent.Should().Be("Healthy");
    }

    [Fact]
    public async Task Info_Endpoint_Should_Return_ApiInformation()
    {
        // Act
        var response = await _client.GetAsync("/info");

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var content = await response.Content.ReadAsStringAsync();
        
        content.Should().Contain("Darbot Memory MCP");
        content.Should().Contain("endpoints");
    }

    [Fact]
    public async Task WriteMessage_Should_Successfully_Persist_ConversationTurn()
    {
        // Arrange
        var conversationTurn = new ConversationTurn
        {
            ConversationId = "integration-test-conversation",
            TurnNumber = 1,
            UtcTimestamp = DateTime.UtcNow,
            Prompt = "This is a test prompt for integration testing",
            Model = "gpt-4",
            Response = "This is a test response for integration testing",
            ToolsUsed = new List<string> { "test-tool" }.AsReadOnly()
        };

        var json = JsonSerializer.Serialize(conversationTurn, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/v1/messages:write", content);

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Success");
    }

    [Fact]
    public async Task BatchWrite_Should_Successfully_Persist_Multiple_ConversationTurns()
    {
        // Arrange
        var conversationTurns = new List<ConversationTurn>
        {
            new ConversationTurn
            {
                ConversationId = "batch-integration-test",
                TurnNumber = 1,
                UtcTimestamp = DateTime.UtcNow,
                Prompt = "First test prompt in batch",
                Model = "gpt-4",
                Response = "First test response in batch",
                ToolsUsed = new List<string>().AsReadOnly()
            },
            new ConversationTurn
            {
                ConversationId = "batch-integration-test",
                TurnNumber = 2,
                UtcTimestamp = DateTime.UtcNow.AddMinutes(1),
                Prompt = "Second test prompt in batch",
                Model = "gpt-4",
                Response = "Second test response in batch",
                ToolsUsed = new List<string> { "batch-tool" }.AsReadOnly()
            }
        };

        var batchRequest = new BatchWriteRequest
        {
            Messages = conversationTurns
        };

        var json = JsonSerializer.Serialize(batchRequest, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/v1/messages:batchWrite", content);

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Success");
    }

    [Fact]
    public async Task Complete_Workflow_Should_Work_EndToEnd()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        
        // Step 1: Write initial conversation
        var turn1 = new ConversationTurn
        {
            ConversationId = $"workflow-test-{testId}",
            TurnNumber = 1,
            UtcTimestamp = DateTime.UtcNow,
            Prompt = "How do I implement a microservices architecture?",
            Model = "gpt-4",
            Response = "To implement microservices architecture, you should consider service boundaries, communication patterns, and data consistency...",
            ToolsUsed = new List<string> { "architecture-advisor" }.AsReadOnly()
        };

        await WriteConversationTurn(turn1);

        // Step 2: Write follow-up conversation
        var turn2 = new ConversationTurn
        {
            ConversationId = $"workflow-test-{testId}",
            TurnNumber = 2,
            UtcTimestamp = DateTime.UtcNow.AddMinutes(5),
            Prompt = "What about containerization with Docker?",
            Model = "gpt-4",
            Response = "For containerization, create Dockerfiles for each microservice, use docker-compose for local development...",
            ToolsUsed = new List<string> { "docker-advisor", "container-helper" }.AsReadOnly()
        };

        await WriteConversationTurn(turn2);

        // Step 3: Retrieve full conversation to verify persistence
        var conversationResponse = await _client.GetAsync($"/v1/conversations/workflow-test-{testId}");
        conversationResponse.IsSuccessStatusCode.Should().BeTrue();

        var conversationContent = await conversationResponse.Content.ReadAsStringAsync();
        conversationContent.Should().Contain("microservices");
        conversationContent.Should().Contain("Docker");

        // Step 4: Retrieve specific turn
        var turnResponse = await _client.GetAsync($"/v1/conversations/workflow-test-{testId}/turns/1");
        turnResponse.IsSuccessStatusCode.Should().BeTrue();

        var turnContent = await turnResponse.Content.ReadAsStringAsync();
        var retrievedTurn = JsonSerializer.Deserialize<ConversationTurn>(turnContent, _jsonOptions);

        retrievedTurn.Should().NotBeNull();
        retrievedTurn!.ConversationId.Should().Be($"workflow-test-{testId}");
        retrievedTurn.TurnNumber.Should().Be(1);
        retrievedTurn.Prompt.Should().Be("How do I implement a microservices architecture?");

        // Workflow completed successfully
        turn1.ConversationId.Should().Be(retrievedTurn.ConversationId);
    }

    [Fact]
    public async Task System_Should_Handle_Multiple_Concurrent_Operations()
    {
        // Test concurrent writes
        var tasks = new List<Task>();
        var conversationCount = 5;
        var turnsPerConversation = 3;

        for (int conv = 0; conv < conversationCount; conv++)
        {
            for (int turn = 1; turn <= turnsPerConversation; turn++)
            {
                var conversationTurn = new ConversationTurn
                {
                    ConversationId = $"concurrent-test-{conv}",
                    TurnNumber = turn,
                    UtcTimestamp = DateTime.UtcNow,
                    Prompt = $"Concurrent test prompt {conv}-{turn}",
                    Model = "gpt-4",
                    Response = $"Concurrent test response {conv}-{turn}",
                    ToolsUsed = new List<string> { $"tool-{conv}" }.AsReadOnly()
                };

                tasks.Add(WriteConversationTurn(conversationTurn));
            }
        }

        // Execute all writes concurrently
        await Task.WhenAll(tasks);

        // Verify all conversations were written successfully
        for (int conv = 0; conv < conversationCount; conv++)
        {
            var response = await _client.GetAsync($"/v1/conversations/concurrent-test-{conv}");
            response.IsSuccessStatusCode.Should().BeTrue();
            
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain($"concurrent-test-{conv}");
        }
    }

    [Fact]
    public async Task System_Should_Maintain_Data_Integrity_Under_Load()
    {
        var testMessages = 20;
        var conversationId = $"integrity-test-{Guid.NewGuid().ToString("N")[..8]}";

        // Create multiple turns for the same conversation
        var turns = Enumerable.Range(1, testMessages).Select(i => new ConversationTurn
        {
            ConversationId = conversationId,
            TurnNumber = i,
            UtcTimestamp = DateTime.UtcNow.AddSeconds(i),
            Prompt = $"Integrity test prompt {i} with unique content {Guid.NewGuid()}",
            Model = "gpt-4", 
            Response = $"Integrity test response {i} with unique content {Guid.NewGuid()}",
            ToolsUsed = new List<string> { $"tool-{i}" }.AsReadOnly()
        }).ToList();

        // Write all turns
        var batchRequest = new BatchWriteRequest { Messages = turns };
        var json = JsonSerializer.Serialize(batchRequest, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        
        var batchResponse = await _client.PostAsync("/v1/messages:batchWrite", content);
        batchResponse.IsSuccessStatusCode.Should().BeTrue();

        // Verify all turns are retrievable and correct
        for (int i = 1; i <= testMessages; i++)
        {
            var turnResponse = await _client.GetAsync($"/v1/conversations/{conversationId}/turns/{i}");
            turnResponse.IsSuccessStatusCode.Should().BeTrue();

            var turnContent = await turnResponse.Content.ReadAsStringAsync();
            var retrievedTurn = JsonSerializer.Deserialize<ConversationTurn>(turnContent, _jsonOptions);

            retrievedTurn.Should().NotBeNull();
            retrievedTurn!.TurnNumber.Should().Be(i);
            retrievedTurn.ConversationId.Should().Be(conversationId);
            retrievedTurn.Prompt.Should().Contain($"Integrity test prompt {i}");
        }
    }

    private async Task WriteConversationTurn(ConversationTurn turn)
    {
        var json = JsonSerializer.Serialize(turn, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/v1/messages:write", content);
        response.EnsureSuccessStatusCode();
    }
}