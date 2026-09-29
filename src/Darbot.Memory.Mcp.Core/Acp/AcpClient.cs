using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Darbot.Memory.Mcp.Core.Acp;

public sealed record PermissionDecision(bool Cancelled, string? OptionId, PermissionOptionKind? Kind)
{
    public bool Allowed => !Cancelled && Kind is PermissionOptionKind.AllowOnce or PermissionOptionKind.AllowAlways;
}

/// <summary>The agent's view of the connected client: notifications plus the fs, terminal and permission calls.</summary>
public sealed class AcpClient
{
    private readonly JsonRpcPeer _peer;
    private readonly TimeSpan _requestTimeout;

    public AcpClient(JsonRpcPeer peer, AcpOptions options)
    {
        _peer = peer;
        _requestTimeout = TimeSpan.FromSeconds(Math.Max(1, options.ClientRequestTimeoutSeconds));
    }

    public ClientCapabilities Capabilities { get; set; } = new();
    public Implementation? Info { get; set; }

    public bool CanReadFiles => Capabilities.Fs?.ReadTextFile == true;
    public bool CanWriteFiles => Capabilities.Fs?.WriteTextFile == true;
    public bool CanUseTerminal => Capabilities.Terminal;
    public bool SupportsBooleanConfig => Capabilities.Session?.ConfigOptions?["boolean"] is not null;

    public Task SendUpdateAsync(string sessionId, SessionUpdate update, CancellationToken cancellationToken = default) =>
        _peer.SendNotificationAsync(
            AcpMethods.SessionUpdate,
            new SessionNotification { SessionId = sessionId, Update = update },
            cancellationToken);

    public async Task<PermissionDecision> RequestPermissionAsync(
        string sessionId,
        ToolCallRef toolCall,
        IReadOnlyList<PermissionOption> options,
        CancellationToken cancellationToken)
    {
        var response = await _peer.SendRequestAsync<RequestPermissionResponse>(
            AcpMethods.SessionRequestPermission,
            new RequestPermissionRequest { SessionId = sessionId, ToolCall = toolCall, Options = options.ToList() },
            timeout: null,
            cancellationToken).ConfigureAwait(false);

        if (!string.Equals(response.Outcome.Outcome, "selected", StringComparison.Ordinal))
        {
            return new PermissionDecision(true, null, null);
        }

        var selected = options.FirstOrDefault(o => o.OptionId == response.Outcome.OptionId);
        return selected is null
            ? new PermissionDecision(true, response.Outcome.OptionId, null)
            : new PermissionDecision(false, selected.OptionId, selected.Kind);
    }

    public async Task<string> ReadTextFileAsync(string sessionId, string path, int? line = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        RequireCapability(CanReadFiles, "fs.readTextFile");
        var response = await _peer.SendRequestAsync<ReadTextFileResponse>(
            AcpMethods.FsReadTextFile,
            new ReadTextFileRequest { SessionId = sessionId, Path = path, Line = line, Limit = limit },
            _requestTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Content;
    }

    public async Task WriteTextFileAsync(string sessionId, string path, string content, CancellationToken cancellationToken = default)
    {
        RequireCapability(CanWriteFiles, "fs.writeTextFile");
        await _peer.SendRequestAsync(
            AcpMethods.FsWriteTextFile,
            new WriteTextFileRequest { SessionId = sessionId, Path = path, Content = content },
            _requestTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> CreateTerminalAsync(
        string sessionId,
        string command,
        IEnumerable<string>? args = null,
        string? cwd = null,
        long? outputByteLimit = null,
        CancellationToken cancellationToken = default)
    {
        RequireCapability(CanUseTerminal, "terminal");
        var response = await _peer.SendRequestAsync<CreateTerminalResponse>(
            AcpMethods.TerminalCreate,
            new CreateTerminalRequest
            {
                SessionId = sessionId,
                Command = command,
                Args = args?.ToList(),
                Cwd = cwd,
                OutputByteLimit = outputByteLimit
            },
            _requestTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.TerminalId;
    }

    public Task<TerminalOutputResponse> GetTerminalOutputAsync(string sessionId, string terminalId, CancellationToken cancellationToken = default)
    {
        RequireCapability(CanUseTerminal, "terminal");
        return _peer.SendRequestAsync<TerminalOutputResponse>(
            AcpMethods.TerminalOutput, new TerminalRef { SessionId = sessionId, TerminalId = terminalId }, _requestTimeout, cancellationToken);
    }

    /// <summary>Waits for the command to exit; no client timeout applies because commands may run for long.</summary>
    public Task<WaitForTerminalExitResponse> WaitForTerminalExitAsync(string sessionId, string terminalId, CancellationToken cancellationToken = default)
    {
        RequireCapability(CanUseTerminal, "terminal");
        return _peer.SendRequestAsync<WaitForTerminalExitResponse>(
            AcpMethods.TerminalWaitForExit, new TerminalRef { SessionId = sessionId, TerminalId = terminalId }, null, cancellationToken);
    }

    public async Task KillTerminalAsync(string sessionId, string terminalId, CancellationToken cancellationToken = default)
    {
        RequireCapability(CanUseTerminal, "terminal");
        await _peer.SendRequestAsync(
            AcpMethods.TerminalKill, new TerminalRef { SessionId = sessionId, TerminalId = terminalId }, _requestTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseTerminalAsync(string sessionId, string terminalId, CancellationToken cancellationToken = default)
    {
        RequireCapability(CanUseTerminal, "terminal");
        await _peer.SendRequestAsync(
            AcpMethods.TerminalRelease, new TerminalRef { SessionId = sessionId, TerminalId = terminalId }, _requestTimeout, cancellationToken).ConfigureAwait(false);
    }

    private static void RequireCapability(bool supported, string name)
    {
        if (!supported)
        {
            throw new NotSupportedException($"The client did not advertise the '{name}' capability.");
        }
    }

    internal static bool KeysEqual(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || actual is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    }
}
