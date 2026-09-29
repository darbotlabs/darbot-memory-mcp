using Darbot.Memory.Mcp.Core.Acp;
using Darbot.Memory.Mcp.Core.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Darbot.Memory.Mcp.Api.Acp;

public static class AcpExtensions
{
    private const string AuthorizationPolicy = "DarbotMemoryWriter";

    /// <summary>Registers ACP options (Darbot:Acp) and the shared in-memory session store.</summary>
    public static IServiceCollection AddDarbotAcp(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AcpOptions>()
            .Bind(configuration.GetSection(AcpOptions.SectionName))
            .Configure<IOptions<DarbotConfiguration>>((acp, darbot) =>
            {
                if (string.IsNullOrEmpty(acp.ApiKey))
                {
                    acp.ApiKey = darbot.Value.Auth.ApiKey;
                }
            });
        services.AddSingleton<AcpSessionStore>();
        return services;
    }

    /// <summary>Enables WebSocket support; call before <see cref="MapDarbotAcp"/> endpoints run (before UseAuthorization is fine).</summary>
    public static IApplicationBuilder UseDarbotAcp(this IApplicationBuilder app) =>
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

    /// <summary>Maps the ACP WebSocket endpoint (default "/acp") and a capability description at "{path}/info".</summary>
    public static IEndpointRouteBuilder MapDarbotAcp(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<IOptions<AcpOptions>>().Value;
        if (!options.Enabled)
        {
            return app;
        }

        var path = string.IsNullOrWhiteSpace(options.Path) ? "/acp" : "/" + options.Path.Trim('/');

        var socket = app.MapGet(path, HandleWebSocketAsync);
        if (options.RequireAuthorization)
        {
            socket.RequireAuthorization(AuthorizationPolicy);
        }

        app.MapGet(path + "/info", (IOptions<AcpOptions> acp) =>
        {
            var current = acp.Value;
            var agent = AcpAgent.DescribeAgent(current);
            return Results.Json(new
            {
                name = current.AgentName,
                title = current.AgentTitle,
                protocol = "agent-client-protocol",
                protocolVersion = AcpAgent.SupportedProtocolVersion,
                transports = new { websocket = path, stdio = "dotnet run --project src/Darbot.Memory.Mcp.Api -- " + AcpStdioHost.Flag },
                agentCapabilities = agent.AgentCapabilities,
                authMethods = agent.AuthMethods,
                modes = AcpModes.All,
                commands = AcpAgent.Commands,
                extensionMethods = new[] { "_darbot/graph/import", "_darbot/graph/export", "_darbot/graph/list", "_darbot/graph/search" },
                requiresAuthorization = current.RequireAuthorization
            }, AcpJson.Options);
        });

        return app;
    }

    private static async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            context.Response.Headers.Upgrade = "websocket";
            await context.Response.WriteAsync("This endpoint speaks the Agent Client Protocol over WebSocket.");
            return;
        }

        var options = context.RequestServices.GetRequiredService<IOptions<AcpOptions>>().Value;
        var sessions = context.RequestServices.GetRequiredService<AcpSessionStore>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Darbot.Acp.WebSocket");

        // The HTTP upgrade already went through the authorization policy when it is enabled.
        var preAuthenticated = options.RequireAuthorization && context.User.Identity?.IsAuthenticated == true;

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await using var scope = context.RequestServices.CreateAsyncScope();
        await using var connection = new WebSocketAcpConnection(socket, options.MaxMessageBytes);
        await AcpAgentHost.RunAsync(connection, scope.ServiceProvider, sessions, options, logger, preAuthenticated, context.RequestAborted);
    }
}
