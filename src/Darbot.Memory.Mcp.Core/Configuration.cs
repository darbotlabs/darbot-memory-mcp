namespace Darbot.Memory.Mcp.Core.Configuration;

/// <summary>
/// Root configuration for Darbot Memory MCP
/// </summary>
public class DarbotConfiguration
{
    public const string SectionName = "Darbot";

    public StorageConfiguration Storage { get; set; } = new();
    public string FileNameTemplate { get; set; } = "%utc%_%conversationId%_%turn%.md";
    public string HashAlgorithm { get; set; } = "SHA256";
    public CorsConfiguration Cors { get; set; } = new();
    public AuthConfiguration Auth { get; set; } = new();
    public DiagnosticsConfiguration Diagnostics { get; set; } = new();
    public BrowserHistoryConfiguration BrowserHistory { get; set; } = new();
}

/// <summary>
/// Storage provider configuration
/// </summary>
public class StorageConfiguration
{
    public string Provider { get; set; } = "FileSystem";
    public string BasePath { get; set; } = "./data";
    public FileSystemConfiguration FileSystem { get; set; } = new();
    public AzureBlobConfiguration AzureBlob { get; set; } = new();
    public GitConfiguration Git { get; set; } = new();
}

/// <summary>
/// FileSystem storage provider configuration
/// </summary>
public class FileSystemConfiguration
{
    public string RootPath { get; set; } = "./conversations";
}

/// <summary>
/// Azure Blob storage provider configuration
/// </summary>
public class AzureBlobConfiguration
{
    public string ConnectionString { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "conversations";
}

/// <summary>
/// Git storage provider configuration
/// </summary>
public class GitConfiguration
{
    public string RepositoryPath { get; set; } = string.Empty;
    public string RemoteUrl { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public bool AutoCommit { get; set; } = true;
    public bool AutoPush { get; set; } = false;
}

/// <summary>
/// CORS configuration
/// </summary>
public class CorsConfiguration
{
    public string AllowedOrigins { get; set; } = "*";
    public string[] GetAllowedOriginsArray() =>
        AllowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries)
                     .Select(o => o.Trim())
                     .ToArray();
}

/// <summary>
/// Authentication configuration
/// </summary>
public class AuthConfiguration
{
    public string Mode { get; set; } = "None"; // None, AAD, APIKey
    public string ApiKey { get; set; } = string.Empty;
    public AadConfiguration Aad { get; set; } = new();
}

/// <summary>
/// Azure Active Directory configuration
/// </summary>
public class AadConfiguration
{
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
}

/// <summary>
/// Diagnostics and telemetry configuration
/// </summary>
public class DiagnosticsConfiguration
{
    public OtelConfiguration Otel { get; set; } = new();
    public ApplicationInsightsConfiguration ApplicationInsights { get; set; } = new();
}

/// <summary>
/// OpenTelemetry configuration
/// </summary>
public class OtelConfiguration
{
    public string Exporter { get; set; } = "none"; // none, console, otlp, zipkin
    public string Endpoint { get; set; } = string.Empty;
}

/// <summary>
/// Application Insights configuration
/// </summary>
public class ApplicationInsightsConfiguration
{
    public string ConnectionString { get; set; } = string.Empty;
    public bool Enabled { get; set; } = false;
}

/// <summary>
/// Browser history configuration
/// </summary>
public class BrowserHistoryConfiguration
{
    public bool Enabled { get; set; } = true;
    public string SupportedBrowsers { get; set; } = "Edge"; // Edge, Chrome, Firefox (future)
    public int SyncIntervalMinutes { get; set; } = 60;
    public int MaxEntriesPerSync { get; set; } = 10000;
    public bool AutoSyncOnStartup { get; set; } = false;
    public string[] IncludeProfiles { get; set; } = Array.Empty<string>(); // Empty means all profiles
    public string[] ExcludeDomains { get; set; } = Array.Empty<string>(); // Domains to exclude from sync
}