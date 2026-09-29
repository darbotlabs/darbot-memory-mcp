using Darbot.Memory.Mcp.Core.Acp;
using Darbot.Memory.Mcp.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Darbot.Memory.Mcp.Api.Acp;

/// <summary>
/// Runs the ACP agent over stdio (newline-delimited JSON-RPC). In this mode stdout carries protocol frames only, so
/// Program.cs must (before building the host) call <see cref="ConfigureLogging"/> instead of wiring Serilog's console
/// sink, must not write anything else to stdout, and must run <see cref="RunAsync"/> instead of starting Kestrel.
/// </summary>
public static class AcpStdioHost
{
    public const string Flag = "--acp";
    public const string ApiKeyEnvironmentVariable = "DARBOT_API_KEY";

    public static bool ShouldRun(string[] args) =>
        args.Any(a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sends all logging to stderr so stdout stays clean for the protocol.</summary>
    public static void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    }

    public static async Task RunAsync(IServiceProvider services, Stream stdin, Stream stdout, CancellationToken cancellationToken = default)
    {
        var options = (services.GetService<IOptions<AcpOptions>>()?.Value ?? new AcpOptions()).Clone();
        var preAuthenticated = false;

        // A local process is trusted, but when the server is configured for API-key auth the agent asks for the key
        // (via DARBOT_API_KEY or the authenticate method) instead of exposing the memory unauthenticated.
        var darbot = services.GetService<IOptions<DarbotConfiguration>>()?.Value;
        if (darbot is not null && string.Equals(darbot.Auth.Mode, "APIKey", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(darbot.Auth.ApiKey))
        {
            options.ApiKey ??= darbot.Auth.ApiKey;
            options.RequireAgentAuth = true;
            preAuthenticated = string.Equals(Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable), options.ApiKey, StringComparison.Ordinal);
        }

        var sessions = services.GetService<AcpSessionStore>() ?? new AcpSessionStore();
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Darbot.Acp.Stdio");

        await using var scope = services.CreateAsyncScope();
        await using var connection = new StreamAcpConnection(stdin, stdout);
        await AcpAgentHost.RunAsync(connection, scope.ServiceProvider, sessions, options, logger, preAuthenticated, cancellationToken).ConfigureAwait(false);
    }
}
