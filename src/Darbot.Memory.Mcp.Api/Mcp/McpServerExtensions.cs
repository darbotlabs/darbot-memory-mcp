using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace Darbot.Memory.Mcp.Api.Mcp;

public static class McpServerExtensions
{
    /// <summary>
    /// Registers the stateless MCP server (Streamable HTTP) with all Darbot tools, resources and prompts.
    /// Requires the Darbot services (IConversationService, IWorkspaceService, IGraphMemoryService) and the
    /// "DarbotMemoryWriter" authorization policy when <see cref="McpServerOptions.RequireAuthorization"/> is true.
    /// </summary>
    public static IServiceCollection AddDarbotMcpServer(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(McpServerOptions.SectionName);
        services.Configure<McpServerOptions>(section);
        var options = section.Get<McpServerOptions>() ?? new McpServerOptions();

        services.AddMcpServer(server =>
            {
                server.ServerInfo = new Implementation
                {
                    Name = options.ServerName,
                    Title = options.ServerTitle,
                    Version = options.ServerVersion
                };
                server.ServerInstructions = options.Instructions;
            })
            .WithHttpTransport(transport => transport.Stateless = options.Stateless)
            .WithTools<ConversationTools>()
            .WithTools<WorkspaceTools>()
            .WithTools<GraphTools>()
            .WithResources<DarbotResources>()
            .WithPrompts<DarbotPrompts>();

        return services;
    }

    /// <summary>Maps the MCP endpoint at Darbot:Mcp:Path (default "/mcp").</summary>
    public static IEndpointRouteBuilder MapDarbotMcpServer(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var path = string.IsNullOrWhiteSpace(options.Path) ? "/mcp" : options.Path;

        var endpoint = app.MapMcp(path);
        if (options.RequireAuthorization)
        {
            endpoint.RequireAuthorization(McpServerOptions.AuthorizationPolicy);
        }

        return app;
    }
}
