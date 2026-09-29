using System.Text;
using System.Threading.Channels;

namespace Darbot.Memory.Mcp.Core.Acp;

/// <summary>
/// Message-oriented duplex transport carrying one complete JSON-RPC message per frame
/// (a line on stdio, a text frame on WebSocket).
/// </summary>
public interface IAcpConnection : IAsyncDisposable
{
    ValueTask SendAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>Returns the next inbound message, or null when the remote side closed the connection.</summary>
    ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>Newline-delimited JSON over a pair of streams (the ACP stdio transport).</summary>
public sealed class StreamAcpConnection : IAcpConnection
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly StreamReader _reader;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public StreamAcpConnection(Stream input, Stream output)
    {
        _reader = new StreamReader(input, Utf8NoBom, false, 64 * 1024, leaveOpen: true);
        _output = output;
    }

    public async ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length > 0 && !string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }
    }

    public async ValueTask SendAsync(string message, CancellationToken cancellationToken = default)
    {
        if (message.Contains('\n') || message.Contains('\r'))
        {
            throw new ArgumentException("Newline-delimited messages must not contain raw line breaks.", nameof(message));
        }

        var bytes = Utf8NoBom.GetBytes(message + "\n");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>In-memory paired connection, used to wire an agent to a client inside one process (tests, embedding).</summary>
public sealed class InMemoryAcpConnection : IAcpConnection
{
    private readonly ChannelReader<string> _incoming;
    private readonly ChannelWriter<string> _outgoing;

    private InMemoryAcpConnection(ChannelReader<string> incoming, ChannelWriter<string> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public static (InMemoryAcpConnection First, InMemoryAcpConnection Second) CreatePair()
    {
        var a = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var b = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        return (new InMemoryAcpConnection(a.Reader, b.Writer), new InMemoryAcpConnection(b.Reader, a.Writer));
    }

    public ValueTask SendAsync(string message, CancellationToken cancellationToken = default) =>
        _outgoing.TryWrite(message)
            ? ValueTask.CompletedTask
            : throw new IOException("The connection is closed.");

    public async ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _incoming.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>Signals end-of-stream to the remote side.</summary>
    public ValueTask DisposeAsync()
    {
        _outgoing.TryComplete();
        return ValueTask.CompletedTask;
    }
}
