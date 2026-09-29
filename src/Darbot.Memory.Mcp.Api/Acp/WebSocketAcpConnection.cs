using System.Net.WebSockets;
using System.Text;
using Darbot.Memory.Mcp.Core.Acp;

namespace Darbot.Memory.Mcp.Api.Acp;

/// <summary>ACP transport over a WebSocket: one JSON-RPC message per text frame.</summary>
public sealed class WebSocketAcpConnection : IAcpConnection
{
    private readonly WebSocket _socket;
    private readonly int _maxMessageBytes;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WebSocketAcpConnection(WebSocket socket, int maxMessageBytes)
    {
        _socket = socket;
        _maxMessageBytes = maxMessageBytes > 0 ? maxMessageBytes : int.MaxValue;
    }

    public async ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                switch (result.MessageType)
                {
                    case WebSocketMessageType.Close:
                        if (_socket.State == WebSocketState.CloseReceived)
                        {
                            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
                        }

                        return null;
                    case WebSocketMessageType.Binary:
                        await _socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "ACP uses text frames", CancellationToken.None).ConfigureAwait(false);
                        return null;
                }

                if (message.Length + result.Count > _maxMessageBytes)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", CancellationToken.None).ConfigureAwait(false);
                    return null;
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                }
            }
        }
        catch (WebSocketException)
        {
            return null;
        }
    }

    public async ValueTask SendAsync(string message, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                throw new IOException("The WebSocket is not open.");
            }

            await _socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            throw new IOException(ex.Message, ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // the peer is already gone
        }

        _socket.Dispose();
        _sendLock.Dispose();
    }
}
