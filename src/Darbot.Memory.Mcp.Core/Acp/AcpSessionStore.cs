using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Darbot.Memory.Mcp.Core.Acp;

public sealed record AcpHistoryEntry(bool IsUser, string Text, DateTimeOffset Timestamp);

/// <summary>State of one ACP session: working directory, mode, transcript and the active turn.</summary>
public sealed class AcpSession
{
    private readonly object _gate = new();
    private readonly List<AcpHistoryEntry> _history = new();
    private readonly HashSet<string> _alwaysAllow = new(StringComparer.Ordinal);
    private readonly HashSet<string> _alwaysReject = new(StringComparer.Ordinal);
    private CancellationTokenSource? _activeTurn;

    public AcpSession(string id, string cwd)
    {
        Id = id;
        Cwd = cwd;
        CreatedAt = UpdatedAt = DateTimeOffset.UtcNow;
    }

    public string Id { get; }
    public string Cwd { get; set; }
    public IReadOnlyList<string> AdditionalDirectories { get; set; } = Array.Empty<string>();
    public IReadOnlyList<McpServer> McpServers { get; set; } = Array.Empty<McpServer>();
    public string ModeId { get; set; } = AcpModes.Recall;
    public string Graph { get; set; } = "default";
    public bool PersistTurns { get; set; } = true;
    public string? Title { get; set; }
    public JsonObject? Meta { get; set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public int TurnCount { get; set; }
    public bool CancelRequested { get; private set; }

    public IReadOnlyList<AcpHistoryEntry> History
    {
        get
        {
            lock (_gate)
            {
                return _history.ToArray();
            }
        }
    }

    public void AddHistory(bool isUser, string text)
    {
        lock (_gate)
        {
            _history.Add(new AcpHistoryEntry(isUser, text, DateTimeOffset.UtcNow));
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public void ReplaceHistory(IEnumerable<AcpHistoryEntry> entries)
    {
        lock (_gate)
        {
            _history.Clear();
            _history.AddRange(entries);
        }
    }

    /// <summary>Registers the active turn. Returns false if a turn is already running.</summary>
    public bool TryBeginTurn(CancellationToken parent, out CancellationTokenSource turn)
    {
        lock (_gate)
        {
            if (_activeTurn is not null)
            {
                turn = null!;
                return false;
            }

            CancelRequested = false;
            turn = CancellationTokenSource.CreateLinkedTokenSource(parent);
            _activeTurn = turn;
            return true;
        }
    }

    public void EndTurn(CancellationTokenSource turn)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeTurn, turn))
            {
                _activeTurn = null;
            }
        }

        turn.Dispose();
    }

    /// <summary>Cancels the running turn (if any). Returns true if a turn was running.</summary>
    public bool Cancel()
    {
        lock (_gate)
        {
            if (_activeTurn is null)
            {
                return false;
            }

            CancelRequested = true;
            try
            {
                _activeTurn.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            return true;
        }
    }

    public void RememberChoice(string operation, bool allow)
    {
        lock (_gate)
        {
            (allow ? _alwaysAllow : _alwaysReject).Add(operation);
        }
    }

    public bool? RememberedChoice(string operation)
    {
        lock (_gate)
        {
            if (_alwaysReject.Contains(operation))
            {
                return false;
            }

            return _alwaysAllow.Contains(operation) ? true : null;
        }
    }
}

public static class AcpModes
{
    public const string Recall = "recall";
    public const string Capture = "capture";
    public const string Graph = "graph";

    public static readonly IReadOnlyList<SessionMode> All = new[]
    {
        new SessionMode { Id = Recall, Name = "Recall", Description = "Answer plain messages from stored memories and past conversations." },
        new SessionMode { Id = Capture, Name = "Capture", Description = "Store plain messages as new memories in the knowledge graph." },
        new SessionMode { Id = Graph, Name = "Graph", Description = "Explore the knowledge graph: plain messages search nodes and show their neighborhood." }
    };
}

/// <summary>Thread-safe in-memory registry of ACP sessions (shared across connections in a process).</summary>
public sealed class AcpSessionStore
{
    private readonly ConcurrentDictionary<string, AcpSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _deleted = new(StringComparer.Ordinal);

    public int Count => _sessions.Count;

    public AcpSession Create(string cwd, string graph, bool persist)
    {
        var session = new AcpSession("acp-" + Guid.NewGuid().ToString("N"), cwd) { Graph = graph, PersistTurns = persist };
        _sessions[session.Id] = session;
        return session;
    }

    public AcpSession GetOrAdd(string id, Func<AcpSession> factory) => _sessions.GetOrAdd(id, _ => factory());

    public bool TryGet(string id, out AcpSession session) => _sessions.TryGetValue(id, out session!);

    /// <summary>Hides a session (and its persisted audit trail) from listing and loading.</summary>
    public void MarkDeleted(string id)
    {
        _deleted[id] = 0;
        Remove(id);
    }

    public bool IsDeleted(string id) => _deleted.ContainsKey(id);

    public bool Remove(string id)
    {
        if (!_sessions.TryRemove(id, out var session))
        {
            return false;
        }

        session.Cancel();
        return true;
    }

    public IReadOnlyList<AcpSession> List(string? cwd = null) =>
        _sessions.Values
            .Where(s => cwd is null || string.Equals(s.Cwd, cwd, StringComparison.Ordinal))
            .OrderByDescending(s => s.UpdatedAt)
            .ToArray();
}

