using Darbot.Memory.Mcp.Core.Configuration;
using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Darbot.Memory.Mcp.Core.Services;
using Darbot.Memory.Mcp.Storage.Providers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
builder.Services.AddSingleton<IStorageProvider, FileSystemStorageProvider>();
builder.Services.AddScoped<IConversationService, ConversationService>();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
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
.WithDescription("Writes multiple conversation turns to storage as Markdown files");

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
.WithDescription("Writes a single conversation turn to storage as a Markdown file");

// Info endpoint
app.MapGet("/info", () => new
{
    Name = "Darbot Memory MCP",
    Version = "1.0.0-preview",
    Description = "MCP server for persisting conversational audit trails",
    Endpoints = new
    {
        Health = new[] { "/health/live", "/health/ready" },
        Messages = new[] { "/v1/messages:write", "/v1/messages:batchWrite" }
    }
})
.WithName("GetInfo")
.WithOpenApi();

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
