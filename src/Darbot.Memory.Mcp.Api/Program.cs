using Darbot.Memory.Mcp.Api.Authentication;
using Darbot.Memory.Mcp.Core.BrowserHistory;
using Darbot.Memory.Mcp.Core.Configuration;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Darbot.Memory.Mcp.Core.Services;
using Darbot.Memory.Mcp.Storage.Providers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

// Add configuration
builder.Services.Configure<DarbotConfiguration>(
    builder.Configuration.GetSection(DarbotConfiguration.SectionName));

// Add services to the container
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Darbot Memory MCP API",
        Version = "v1",
        Description = "MCP server for persisting conversational audit trails"
    });
});

// Add health checks
builder.Services.AddHealthChecks()
    .AddCheck<StorageHealthCheck>("storage");

// Add authentication
builder.Services.AddAuthentication(ApiKeyAuthenticationOptions.DefaultScheme)
    .AddApiKey();

// Add authorization
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("DarbotMemoryWriter", policy =>
        policy.RequireClaim("scope", "darbot.memory.writer"));
});

// Add CORS
var config = builder.Configuration.GetSection(DarbotConfiguration.SectionName).Get<DarbotConfiguration>() ?? new();
if (config.Cors.AllowedOrigins != "*")
{
    var origins = config.Cors.GetAllowedOriginsArray();
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins(origins)
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });
}
else
{
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });
}

// Register services
builder.Services.AddSingleton<IHashCalculator>(sp =>
    new HashCalculator(config.HashAlgorithm));
builder.Services.AddSingleton<IConversationFormatter>(sp =>
    new ConversationFormatter(config.FileNameTemplate));

// Register storage provider based on configuration
builder.Services.AddSingleton<IStorageProvider>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<IStorageProvider>>();
    var formatter = sp.GetRequiredService<IConversationFormatter>();
    var options = sp.GetRequiredService<IOptions<DarbotConfiguration>>();
    
    return config.Storage.Provider.ToLowerInvariant() switch
    {
        "git" => new GitStorageProvider(options, formatter, sp.GetRequiredService<ILogger<GitStorageProvider>>()),
        "filesystem" => new FileSystemStorageProvider(options, formatter, sp.GetRequiredService<ILogger<FileSystemStorageProvider>>()),
        "azureblob" => new AzureBlobStorageProvider(options, formatter, sp.GetRequiredService<ILogger<AzureBlobStorageProvider>>()),
        _ => throw new InvalidOperationException($"Unknown storage provider: {config.Storage.Provider}")
    };
});

builder.Services.AddScoped<IConversationService, ConversationService>();

// Register browser history services if enabled
if (config.BrowserHistory.Enabled)
{
    builder.Services.AddSingleton<IBrowserHistoryProvider, EdgeHistoryProvider>();
    builder.Services.AddSingleton<IBrowserHistoryStorage, BrowserHistoryFileStorage>();
    builder.Services.AddScoped<IBrowserHistoryService, BrowserHistoryService>();
}

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseSerilogRequestLogging();

// Health check endpoints
app.MapHealthChecks("/health/live", new()
{
    Predicate = _ => false // Only basic liveness check
});

app.MapHealthChecks("/health/ready"); // Full health checks including storage

// MCP Endpoints
app.MapPost("/v1/messages:batchWrite", async (
    BatchWriteRequest request,
    IConversationService conversationService,
    ILogger<Program> logger) =>
{
    logger.LogInformation("Received batch write request with {Count} messages", request.Messages.Count);

    var result = await conversationService.PersistBatchAsync(request.Messages);

    return result.Success
        ? Results.Ok(result)
        : Results.BadRequest(result);
})
.WithName("BatchWriteMessages")
.WithOpenApi()
.WithSummary("Batch write conversation messages")
.WithDescription("Writes multiple conversation turns to storage as Markdown files")
.RequireAuthorization("DarbotMemoryWriter");

app.MapPost("/v1/messages:write", async (
    ConversationTurn turn,
    IConversationService conversationService,
    ILogger<Program> logger) =>
{
    logger.LogInformation("Received single write request for conversation {ConversationId}, turn {Turn}",
        turn.ConversationId, turn.TurnNumber);

    var result = await conversationService.PersistTurnAsync(turn);

    return result
        ? Results.Ok(new { Success = true, Message = "Message persisted successfully" })
        : Results.BadRequest(new { Success = false, Message = "Failed to persist message" });
})
.WithName("WriteMessage")
.WithOpenApi()
.WithSummary("Write single conversation message")
.WithDescription("Writes a single conversation turn to storage as a Markdown file")
.RequireAuthorization("DarbotMemoryWriter");

// Info endpoint
app.MapGet("/info", () => new
{
    Name = "Darbot Memory MCP",
    Version = "1.0.0-preview",
    Description = "MCP server for persisting conversational audit trails and browser history",
    Endpoints = new
    {
        Health = new[] { "/health/live", "/health/ready" },
        Messages = new[] { "/v1/messages:write", "/v1/messages:batchWrite" },
        Search = new[] { "/v1/conversations:search", "/v1/conversations:list", "/v1/conversations/{conversationId}", "/v1/conversations/{conversationId}/turns/{turnNumber}" },
        BrowserHistory = new[] { "/v1/browser-history:sync", "/v1/browser-history:search", "/v1/browser-history/profiles" }
    }
})
.WithName("GetInfo")
.WithOpenApi();

// Query endpoints
app.MapPost("/v1/conversations:search", async (
    ConversationSearchRequest request,
    IConversationService conversationService,
    ILogger<Program> logger) =>
{
    logger.LogInformation("Received search request");

    var result = await conversationService.SearchConversationsAsync(request);
    return Results.Ok(result);
})
.WithName("SearchConversations")
.WithOpenApi()
.WithSummary("Search conversation turns")
.WithDescription("Searches conversation turns based on various criteria like text, date range, model, etc.")
.RequireAuthorization("DarbotMemoryWriter");

app.MapPost("/v1/conversations:list", async (
    ConversationListRequest request,
    IConversationService conversationService,
    ILogger<Program> logger) =>
{
    logger.LogInformation("Received list conversations request");

    var result = await conversationService.ListConversationsAsync(request);
    return Results.Ok(result);
})
.WithName("ListConversations")
.WithOpenApi()
.WithSummary("List conversations")
.WithDescription("Lists conversations with summary information")
.RequireAuthorization("DarbotMemoryWriter");

app.MapGet("/v1/conversations/{conversationId}", async (
    string conversationId,
    IConversationService conversationService,
    ILogger<Program> logger) =>
{
    logger.LogInformation("Received get conversation request for {ConversationId}", conversationId);

    var result = await conversationService.GetConversationAsync(conversationId);
    
    if (!result.Any())
    {
        return Results.NotFound(new { Message = $"Conversation {conversationId} not found" });
    }

    return Results.Ok(new { ConversationId = conversationId, Turns = result });
})
.WithName("GetConversation")
.WithOpenApi()
.WithSummary("Get conversation")
.WithDescription("Retrieves all turns for a specific conversation")
.RequireAuthorization("DarbotMemoryWriter");

app.MapGet("/v1/conversations/{conversationId}/turns/{turnNumber:int}", async (
    string conversationId,
    int turnNumber,
    IConversationService conversationService,
    ILogger<Program> logger) =>
{
    logger.LogInformation("Received get conversation turn request for {ConversationId}:{TurnNumber}", conversationId, turnNumber);

    var result = await conversationService.GetConversationTurnAsync(conversationId, turnNumber);
    
    if (result == null)
    {
        return Results.NotFound(new { Message = $"Conversation turn {conversationId}:{turnNumber} not found" });
    }

    return Results.Ok(result);
})
.WithName("GetConversationTurn")
.WithOpenApi()
.WithSummary("Get conversation turn")
.WithDescription("Retrieves a specific conversation turn by ID and turn number")
.RequireAuthorization("DarbotMemoryWriter");

// Browser History endpoints (only available if browser history is enabled)
if (config.BrowserHistory.Enabled)
{
    app.MapPost("/v1/browser-history:sync", async (
        BrowserHistorySyncRequest request,
        IBrowserHistoryService browserHistoryService,
        ILogger<Program> logger) =>
    {
        logger.LogInformation("Received browser history sync request");

        var result = await browserHistoryService.SyncBrowserHistoryAsync(request);
        
        return result.Success
            ? Results.Ok(result)
            : Results.BadRequest(result);
    })
    .WithName("SyncBrowserHistory")
    .WithOpenApi()
    .WithSummary("Sync browser history")
    .WithDescription("Syncs browser history from Edge profiles with delta update support")
    .RequireAuthorization("DarbotMemoryWriter");

    app.MapPost("/v1/browser-history:search", async (
        BrowserHistorySearchRequest request,
        IBrowserHistoryService browserHistoryService,
        ILogger<Program> logger) =>
    {
        logger.LogInformation("Received browser history search request");

        var result = await browserHistoryService.SearchBrowserHistoryAsync(request);
        return Results.Ok(result);
    })
    .WithName("SearchBrowserHistory")
    .WithOpenApi()
    .WithSummary("Search browser history")
    .WithDescription("Searches stored browser history based on various criteria like URL, title, domain, date range, etc.")
    .RequireAuthorization("DarbotMemoryWriter");

    app.MapGet("/v1/browser-history/profiles", async (
        IBrowserHistoryService browserHistoryService,
        ILogger<Program> logger) =>
    {
        logger.LogInformation("Received get browser profiles request");

        var result = await browserHistoryService.GetBrowserProfilesAsync();
        return Results.Ok(new { Profiles = result });
    })
    .WithName("GetBrowserProfiles")
    .WithOpenApi()
    .WithSummary("Get browser profiles")
    .WithDescription("Retrieves all available browser profiles for history sync")
    .RequireAuthorization("DarbotMemoryWriter");
}

app.Run();

// Health check for storage provider
public class StorageHealthCheck : IHealthCheck
{
    private readonly IStorageProvider _storageProvider;

    public StorageHealthCheck(IStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var isHealthy = await _storageProvider.IsHealthyAsync(cancellationToken);
            return isHealthy
                ? HealthCheckResult.Healthy("Storage provider is healthy")
                : HealthCheckResult.Unhealthy("Storage provider is not healthy");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Storage provider health check failed", ex);
        }
    }
}
