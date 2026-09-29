using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Darbot.Memory.Mcp.Core.Acp;

/// <summary>Shared serializer settings for the ACP wire format (camelCase, snake_case enums, nulls omitted).</summary>
public static class AcpJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowOutOfOrderMetadataProperties = true,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.Converters.Add(new McpServerConverter());
        return options;
    }
}

/// <summary>Base of every ACP object; carries the reserved <c>_meta</c> extension bag.</summary>
public abstract record AcpObject
{
    [JsonPropertyName("_meta")]
    public JsonObject? Meta { get; init; }
}

public static class AcpMethods
{
    public const string Initialize = "initialize";
    public const string Authenticate = "authenticate";
    public const string Logout = "logout";
    public const string SessionNew = "session/new";
    public const string SessionLoad = "session/load";
    public const string SessionList = "session/list";
    public const string SessionDelete = "session/delete";
    public const string SessionResume = "session/resume";
    public const string SessionClose = "session/close";
    public const string SessionSetMode = "session/set_mode";
    public const string SessionSetConfigOption = "session/set_config_option";
    public const string SessionPrompt = "session/prompt";
    public const string SessionCancel = "session/cancel";
    public const string SessionUpdate = "session/update";
    public const string SessionRequestPermission = "session/request_permission";
    public const string FsReadTextFile = "fs/read_text_file";
    public const string FsWriteTextFile = "fs/write_text_file";
    public const string TerminalCreate = "terminal/create";
    public const string TerminalOutput = "terminal/output";
    public const string TerminalWaitForExit = "terminal/wait_for_exit";
    public const string TerminalKill = "terminal/kill";
    public const string TerminalRelease = "terminal/release";
    public const string CancelRequest = "$/cancel_request";
}

// ---------------------------------------------------------------- enums

public enum ToolKind { Read, Edit, Delete, Move, Search, Execute, Think, Fetch, SwitchMode, Other }

public enum ToolCallStatus { Pending, InProgress, Completed, Failed }

public enum StopReason { EndTurn, MaxTokens, MaxTurnRequests, Refusal, Cancelled }

public enum PermissionOptionKind { AllowOnce, AllowAlways, RejectOnce, RejectAlways }

public enum PlanEntryPriority { High, Medium, Low }

public enum PlanEntryStatus { Pending, InProgress, Completed }

public enum AcpRole { Assistant, User }

// ---------------------------------------------------------------- content

public sealed record Annotations : AcpObject
{
    public List<AcpRole>? Audience { get; init; }
    public string? LastModified { get; init; }
    public double? Priority { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonDerivedType(typeof(AudioContent), "audio")]
[JsonDerivedType(typeof(ResourceLinkContent), "resource_link")]
[JsonDerivedType(typeof(EmbeddedResourceContent), "resource")]
public abstract record ContentBlock : AcpObject
{
    public Annotations? Annotations { get; init; }

    public static TextContent FromText(string text) => new() { Text = text };
}

public sealed record TextContent : ContentBlock
{
    public required string Text { get; init; }
}

public sealed record ImageContent : ContentBlock
{
    public required string Data { get; init; }
    public required string MimeType { get; init; }
    public string? Uri { get; init; }
}

public sealed record AudioContent : ContentBlock
{
    public required string Data { get; init; }
    public required string MimeType { get; init; }
}

public sealed record ResourceLinkContent : ContentBlock
{
    public required string Name { get; init; }
    public required string Uri { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? MimeType { get; init; }
    public long? Size { get; init; }
}

/// <summary>Text (<c>text</c>) or binary (<c>blob</c>) resource contents.</summary>
public sealed record ResourceContents : AcpObject
{
    public required string Uri { get; init; }
    public string? MimeType { get; init; }
    public string? Text { get; init; }
    public string? Blob { get; init; }
}

public sealed record EmbeddedResourceContent : ContentBlock
{
    public required ResourceContents Resource { get; init; }
}

// ---------------------------------------------------------------- tool calls

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ContentToolCallContent), "content")]
[JsonDerivedType(typeof(DiffToolCallContent), "diff")]
[JsonDerivedType(typeof(TerminalToolCallContent), "terminal")]
public abstract record ToolCallContent : AcpObject
{
    public static ContentToolCallContent Text(string text) => new() { Content = ContentBlock.FromText(text) };
}

public sealed record ContentToolCallContent : ToolCallContent
{
    public required ContentBlock Content { get; init; }
}

public sealed record DiffToolCallContent : ToolCallContent
{
    public required string Path { get; init; }
    public string? OldText { get; init; }
    public required string NewText { get; init; }
}

public sealed record TerminalToolCallContent : ToolCallContent
{
    public required string TerminalId { get; init; }
}

public sealed record ToolCallLocation : AcpObject
{
    public required string Path { get; init; }
    public int? Line { get; init; }
}

/// <summary>Partial tool call description (used by <c>session/request_permission</c>).</summary>
public sealed record ToolCallRef : AcpObject
{
    public required string ToolCallId { get; init; }
    public ToolKind? Kind { get; init; }
    public ToolCallStatus? Status { get; init; }
    public string? Title { get; init; }
    public List<ToolCallContent>? Content { get; init; }
    public List<ToolCallLocation>? Locations { get; init; }
    public JsonNode? RawInput { get; init; }
    public JsonNode? RawOutput { get; init; }
}

public sealed record PermissionOption : AcpObject
{
    public required string OptionId { get; init; }
    public required string Name { get; init; }
    public required PermissionOptionKind Kind { get; init; }
}

public sealed record RequestPermissionRequest : AcpObject
{
    public required string SessionId { get; init; }
    public required ToolCallRef ToolCall { get; init; }
    public required List<PermissionOption> Options { get; init; }
}

public sealed record RequestPermissionResponse : AcpObject
{
    /// <summary><c>selected</c> or <c>cancelled</c>.</summary>
    public required PermissionOutcome Outcome { get; init; }
}

public sealed record PermissionOutcome : AcpObject
{
    public required string Outcome { get; init; }
    public string? OptionId { get; init; }
}

// ---------------------------------------------------------------- plan & commands

public sealed record PlanEntry : AcpObject
{
    public required string Content { get; init; }
    public PlanEntryPriority Priority { get; init; } = PlanEntryPriority.Medium;
    public PlanEntryStatus Status { get; init; } = PlanEntryStatus.Pending;
}

public sealed record AvailableCommandInput : AcpObject
{
    public required string Hint { get; init; }
}

public sealed record AvailableCommand : AcpObject
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public AvailableCommandInput? Input { get; init; }
}

// ---------------------------------------------------------------- session updates

[JsonPolymorphic(TypeDiscriminatorPropertyName = "sessionUpdate")]
[JsonDerivedType(typeof(UserMessageChunk), "user_message_chunk")]
[JsonDerivedType(typeof(AgentMessageChunk), "agent_message_chunk")]
[JsonDerivedType(typeof(AgentThoughtChunk), "agent_thought_chunk")]
[JsonDerivedType(typeof(ToolCallUpdateStart), "tool_call")]
[JsonDerivedType(typeof(ToolCallUpdateProgress), "tool_call_update")]
[JsonDerivedType(typeof(PlanUpdate), "plan")]
[JsonDerivedType(typeof(AvailableCommandsUpdate), "available_commands_update")]
[JsonDerivedType(typeof(CurrentModeUpdate), "current_mode_update")]
[JsonDerivedType(typeof(ConfigOptionUpdate), "config_option_update")]
[JsonDerivedType(typeof(SessionInfoUpdate), "session_info_update")]
public abstract record SessionUpdate : AcpObject;

public abstract record ContentChunkUpdate : SessionUpdate
{
    public required ContentBlock Content { get; init; }
    public string? MessageId { get; init; }
}

public sealed record UserMessageChunk : ContentChunkUpdate;

public sealed record AgentMessageChunk : ContentChunkUpdate;

public sealed record AgentThoughtChunk : ContentChunkUpdate;

/// <summary><c>sessionUpdate: tool_call</c>.</summary>
public sealed record ToolCallUpdateStart : SessionUpdate
{
    public required string ToolCallId { get; init; }
    public required string Title { get; init; }
    public string? Name { get; init; }
    public ToolKind? Kind { get; init; }
    public ToolCallStatus? Status { get; init; }
    public List<ToolCallContent>? Content { get; init; }
    public List<ToolCallLocation>? Locations { get; init; }
    public JsonNode? RawInput { get; init; }
    public JsonNode? RawOutput { get; init; }
}

/// <summary><c>sessionUpdate: tool_call_update</c>; only changed fields are sent.</summary>
public sealed record ToolCallUpdateProgress : SessionUpdate
{
    public required string ToolCallId { get; init; }
    public ToolKind? Kind { get; init; }
    public ToolCallStatus? Status { get; init; }
    public string? Title { get; init; }
    public List<ToolCallContent>? Content { get; init; }
    public List<ToolCallLocation>? Locations { get; init; }
    public JsonNode? RawInput { get; init; }
    public JsonNode? RawOutput { get; init; }
}

public sealed record PlanUpdate : SessionUpdate
{
    public required List<PlanEntry> Entries { get; init; }
}

public sealed record AvailableCommandsUpdate : SessionUpdate
{
    public required List<AvailableCommand> AvailableCommands { get; init; }
}

public sealed record CurrentModeUpdate : SessionUpdate
{
    public required string CurrentModeId { get; init; }
}

public sealed record ConfigOptionUpdate : SessionUpdate
{
    public required List<SessionConfigOption> ConfigOptions { get; init; }
}

public sealed record SessionInfoUpdate : SessionUpdate
{
    public string? Title { get; init; }
    public string? UpdatedAt { get; init; }
}

public sealed record SessionNotification : AcpObject
{
    public required string SessionId { get; init; }
    public required SessionUpdate Update { get; init; }
}

// ---------------------------------------------------------------- capabilities & lifecycle

public sealed record Implementation : AcpObject
{
    public required string Name { get; init; }
    public string? Title { get; init; }
    public required string Version { get; init; }
}

public sealed record FileSystemCapabilities : AcpObject
{
    public bool ReadTextFile { get; init; }
    public bool WriteTextFile { get; init; }
}

public sealed record ClientSessionCapabilities : AcpObject
{
    public JsonObject? ConfigOptions { get; init; }
}

public sealed record ClientCapabilities : AcpObject
{
    public FileSystemCapabilities? Fs { get; init; }
    public bool Terminal { get; init; }
    public ClientSessionCapabilities? Session { get; init; }
    public JsonObject? Auth { get; init; }
    public JsonObject? Elicitation { get; init; }
}

public sealed record InitializeRequest : AcpObject
{
    public required int ProtocolVersion { get; init; }
    public ClientCapabilities? ClientCapabilities { get; init; }
    public Implementation? ClientInfo { get; init; }
}

public sealed record PromptCapabilities : AcpObject
{
    public bool Image { get; init; }
    public bool Audio { get; init; }
    public bool EmbeddedContext { get; init; }
}

public sealed record McpCapabilities : AcpObject
{
    public bool Http { get; init; }
    public bool Sse { get; init; }
}

public sealed record EmptyCapability : AcpObject;

public sealed record SessionCapabilities : AcpObject
{
    public EmptyCapability? List { get; init; }
    public EmptyCapability? Delete { get; init; }
    public EmptyCapability? Resume { get; init; }
    public EmptyCapability? Close { get; init; }
}

public sealed record AgentAuthCapabilities : AcpObject
{
    public EmptyCapability? Logout { get; init; }
}

public sealed record AgentCapabilities : AcpObject
{
    public bool LoadSession { get; init; }
    public PromptCapabilities? PromptCapabilities { get; init; }
    public McpCapabilities? McpCapabilities { get; init; }
    public SessionCapabilities? SessionCapabilities { get; init; }
    public AgentAuthCapabilities? Auth { get; init; }
}

/// <summary>Agent-handled authentication method (no <c>type</c> discriminator on the wire).</summary>
public sealed record AuthMethod : AcpObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}

public sealed record InitializeResponse : AcpObject
{
    public required int ProtocolVersion { get; init; }
    public AgentCapabilities? AgentCapabilities { get; init; }
    public List<AuthMethod>? AuthMethods { get; init; }
    public Implementation? AgentInfo { get; init; }
}

public sealed record AuthenticateRequest : AcpObject
{
    public required string MethodId { get; init; }
}

// ---------------------------------------------------------------- MCP server descriptors

public sealed record EnvVariable : AcpObject
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}

public sealed record HttpHeader : AcpObject
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}

public abstract record McpServer : AcpObject
{
    public required string Name { get; init; }
}

public sealed record McpServerStdio : McpServer
{
    public required string Command { get; init; }
    public List<string> Args { get; init; } = new();
    public List<EnvVariable> Env { get; init; } = new();
}

public sealed record McpServerHttp : McpServer
{
    public required string Url { get; init; }
    public List<HttpHeader> Headers { get; init; } = new();
}

public sealed record McpServerSse : McpServer
{
    public required string Url { get; init; }
    public List<HttpHeader> Headers { get; init; } = new();
}

/// <summary>Stdio servers carry no <c>type</c>; http/sse are discriminated by <c>type</c>.</summary>
public sealed class McpServerConverter : JsonConverter<McpServer>
{
    public override McpServer? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("MCP server descriptor must be an object.");
        }

        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var raw = root.GetRawText();
        return type switch
        {
            "http" => JsonSerializer.Deserialize<McpServerHttp>(raw, options),
            "sse" => JsonSerializer.Deserialize<McpServerSse>(raw, options),
            null or "stdio" => JsonSerializer.Deserialize<McpServerStdio>(raw, options),
            _ => throw new JsonException($"Unknown MCP server type '{type}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, McpServer value, JsonSerializerOptions options)
    {
        JsonNode? node = value switch
        {
            McpServerHttp http => JsonSerializer.SerializeToNode(http, options),
            McpServerSse sse => JsonSerializer.SerializeToNode(sse, options),
            McpServerStdio stdio => JsonSerializer.SerializeToNode(stdio, options),
            _ => throw new JsonException("Unsupported MCP server descriptor.")
        };
        if (node is JsonObject obj)
        {
            if (value is McpServerHttp)
            {
                obj["type"] = "http";
            }
            else if (value is McpServerSse)
            {
                obj["type"] = "sse";
            }
        }

        node!.WriteTo(writer);
    }
}

// ---------------------------------------------------------------- sessions

public sealed record NewSessionRequest : AcpObject
{
    public required string Cwd { get; init; }
    public List<string>? AdditionalDirectories { get; init; }
    public List<McpServer> McpServers { get; init; } = new();
}

public sealed record LoadSessionRequest : AcpObject
{
    public required string SessionId { get; init; }
    public required string Cwd { get; init; }
    public List<string>? AdditionalDirectories { get; init; }
    public List<McpServer> McpServers { get; init; } = new();
}

public sealed record ResumeSessionRequest : AcpObject
{
    public required string SessionId { get; init; }
    public required string Cwd { get; init; }
    public List<string>? AdditionalDirectories { get; init; }
    public List<McpServer>? McpServers { get; init; }
}

public sealed record ListSessionsRequest : AcpObject
{
    public string? Cwd { get; init; }
    public string? Cursor { get; init; }
}

public sealed record SessionIdRequest : AcpObject
{
    public required string SessionId { get; init; }
}

public sealed record SetSessionModeRequest : AcpObject
{
    public required string SessionId { get; init; }
    public required string ModeId { get; init; }
}

public sealed record SetSessionConfigOptionRequest : AcpObject
{
    public required string SessionId { get; init; }
    public required string ConfigId { get; init; }
    public string? Type { get; init; }
    public required JsonElement Value { get; init; }
}

public sealed record PromptRequest : AcpObject
{
    public required string SessionId { get; init; }
    public required List<ContentBlock> Prompt { get; init; }
}

public sealed record PromptResponse : AcpObject
{
    public required StopReason StopReason { get; init; }
}

public sealed record SessionMode : AcpObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}

public sealed record SessionModeState : AcpObject
{
    public required string CurrentModeId { get; init; }
    public required List<SessionMode> AvailableModes { get; init; }
}

public sealed record SessionConfigSelectOption : AcpObject
{
    public required string Value { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}

/// <summary>A <c>select</c> or <c>boolean</c> session configuration option.</summary>
public sealed record SessionConfigOption : AcpObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public required string Type { get; init; }
    public required object CurrentValue { get; init; }
    public List<SessionConfigSelectOption>? Options { get; init; }
}

public sealed record NewSessionResponse : AcpObject
{
    public required string SessionId { get; init; }
    public SessionModeState? Modes { get; init; }
    public List<SessionConfigOption>? ConfigOptions { get; init; }
}

public sealed record LoadSessionResponse : AcpObject
{
    public SessionModeState? Modes { get; init; }
    public List<SessionConfigOption>? ConfigOptions { get; init; }
}

public sealed record SetSessionConfigOptionResponse : AcpObject
{
    public required List<SessionConfigOption> ConfigOptions { get; init; }
}

public sealed record SessionInfo : AcpObject
{
    public required string SessionId { get; init; }
    public required string Cwd { get; init; }
    public string? Title { get; init; }
    public string? UpdatedAt { get; init; }
}

public sealed record ListSessionsResponse : AcpObject
{
    public required List<SessionInfo> Sessions { get; init; }
    public string? NextCursor { get; init; }
}

// ---------------------------------------------------------------- client-side calls made by the agent

public sealed record ReadTextFileRequest : AcpObject
{
    public required string SessionId { get; init; }
    public required string Path { get; init; }
    public int? Line { get; init; }
    public int? Limit { get; init; }
}

public sealed record ReadTextFileResponse : AcpObject
{
    public required string Content { get; init; }
}

public sealed record WriteTextFileRequest : AcpObject
{
    public required string SessionId { get; init; }
    public required string Path { get; init; }
    public required string Content { get; init; }
}

public sealed record CreateTerminalRequest : AcpObject
{
    public required string SessionId { get; init; }
    public required string Command { get; init; }
    public List<string>? Args { get; init; }
    public List<EnvVariable>? Env { get; init; }
    public string? Cwd { get; init; }
    public long? OutputByteLimit { get; init; }
}

public sealed record CreateTerminalResponse : AcpObject
{
    public required string TerminalId { get; init; }
}

public sealed record TerminalRef : AcpObject
{
    public required string SessionId { get; init; }
    public required string TerminalId { get; init; }
}

public sealed record TerminalExitStatus : AcpObject
{
    public int? ExitCode { get; init; }
    public string? Signal { get; init; }
}

public sealed record TerminalOutputResponse : AcpObject
{
    public required string Output { get; init; }
    public bool Truncated { get; init; }
    public TerminalExitStatus? ExitStatus { get; init; }
}

public sealed record WaitForTerminalExitResponse : AcpObject
{
    public int? ExitCode { get; init; }
    public string? Signal { get; init; }
}

