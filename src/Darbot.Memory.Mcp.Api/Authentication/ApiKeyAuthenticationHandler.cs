using Darbot.Memory.Mcp.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace Darbot.Memory.Mcp.Api.Authentication;

/// <summary>
/// API Key authentication handler for securing MCP endpoints
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly DarbotConfiguration _config;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<DarbotConfiguration> config)
        : base(options, logger, encoder)
    {
        _config = config.Value;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Skip authentication if mode is None
        if (_config.Auth.Mode.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            var noneIdentity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "Anonymous"),
                new Claim("scope", "darbot.memory.writer")
            }, Scheme.Name);

            var nonePrincipal = new ClaimsPrincipal(noneIdentity);
            var noneTicket = new AuthenticationTicket(nonePrincipal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(noneTicket));
        }

        // Handle API Key authentication
        if (_config.Auth.Mode.Equals("APIKey", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(_config.Auth.ApiKey))
            {
                Logger.LogWarning("API Key authentication is enabled but no API key is configured");
                return Task.FromResult(AuthenticateResult.Fail("API Key not configured"));
            }

            // Check for API key in header
            if (!Request.Headers.TryGetValue("X-API-Key", out var apiKeyHeaderValues))
            {
                return Task.FromResult(AuthenticateResult.Fail("Missing X-API-Key header"));
            }

            var apiKey = apiKeyHeaderValues.FirstOrDefault();
            if (string.IsNullOrEmpty(apiKey))
            {
                return Task.FromResult(AuthenticateResult.Fail("Empty API key"));
            }

            if (!string.Equals(apiKey, _config.Auth.ApiKey, StringComparison.Ordinal))
            {
                Logger.LogWarning("Invalid API key provided from {RemoteIpAddress}",
                    Request.HttpContext.Connection.RemoteIpAddress);
                return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
            }

            // Create authenticated identity
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "ApiKeyUser"),
                new Claim("scope", "darbot.memory.writer"),
                new Claim("auth_method", "apikey")
            }, Scheme.Name);

            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            Logger.LogInformation("API Key authentication successful for {RemoteIpAddress}",
                Request.HttpContext.Connection.RemoteIpAddress);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        // AAD authentication would be handled here
        if (_config.Auth.Mode.Equals("AAD", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning("AAD authentication not yet implemented");
            return Task.FromResult(AuthenticateResult.Fail("AAD authentication not implemented"));
        }

        return Task.FromResult(AuthenticateResult.Fail($"Unknown authentication mode: {_config.Auth.Mode}"));
    }
}

/// <summary>
/// Options for API Key authentication
/// </summary>
public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string DefaultScheme = "ApiKey";
}

/// <summary>
/// Extension methods for adding API Key authentication
/// </summary>
public static class ApiKeyAuthenticationExtensions
{
    public static AuthenticationBuilder AddApiKey(this AuthenticationBuilder builder)
        => builder.AddApiKey(ApiKeyAuthenticationOptions.DefaultScheme);

    public static AuthenticationBuilder AddApiKey(this AuthenticationBuilder builder, string authenticationScheme)
        => builder.AddApiKey(authenticationScheme, configureOptions: null);

    public static AuthenticationBuilder AddApiKey(this AuthenticationBuilder builder, Action<ApiKeyAuthenticationOptions>? configureOptions)
        => builder.AddApiKey(ApiKeyAuthenticationOptions.DefaultScheme, configureOptions);

    public static AuthenticationBuilder AddApiKey(this AuthenticationBuilder builder, string authenticationScheme, Action<ApiKeyAuthenticationOptions>? configureOptions)
        => builder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(authenticationScheme, configureOptions);
}