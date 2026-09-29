using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Darbot.Memory.Mcp.Core.Acp;

/// <summary>Standard JSON-RPC 2.0 error codes plus the codes ACP reserves.</summary>
public static class JsonRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int RequestCancelled = -32800;
    public const int AuthRequired = -32000;
    public const int ResourceNotFound = -32002;
}

/// <summary>Thrown by handlers to produce a JSON-RPC error response.</summary>
public sealed class JsonRpcException : Exception
{
    public JsonRpcException(int code, string message, JsonNode? data = null) : base(message)
    {
        Code = code;
        ErrorData = data;
    }

    public int Code { get; }
    public JsonNode? ErrorData { get; }
}

/// <summary>A JSON-RPC error response received from the remote peer for a request this side sent.</summary>
public sealed class JsonRpcRemoteException : Exception
{
    public JsonRpcRemoteException(int code, string message, JsonElement? data = null) : base(message)
    {
        Code = code;
        ErrorData = data;
    }

    public int Code { get; }
    public JsonElement? ErrorData { get; }
}

public sealed class JsonRpcConnectionClosedException : IOException
{
    public JsonRpcConnectionClosedException() : base("The JSON-RPC connection was closed.") { }
}

/// <summary>Empty JSON object result (<c>{}</c>).</summary>
public sealed record JsonRpcEmptyResult;

public sealed class JsonRpcRequestContext
{
    private readonly List<Func<Task>> _afterResponse = new();

    public JsonRpcRequestContext(string method, JsonElement parameters)
    {
        Method = method;
        Params = parameters;
    }

    public string Method { get; }
    public JsonElement Params { get; }
    internal IReadOnlyList<Func<Task>> AfterResponse => _afterResponse;

    /// <summary>Registers work (typically follow-up notifications) that must run only after the response was written.</summary>
    public void OnResponded(Func<Task> callback) => _afterResponse.Add(callback);

    public T GetParams<T>()
    {
        try
        {
            var json = Params.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : Params.GetRawText();
            return JsonSerializer.Deserialize<T>(json, AcpJson.Options)
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Invalid params");
        }
        catch (JsonException ex)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"Invalid params: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"Invalid params: {ex.Message}");
        }
    }
}

public interface IJsonRpcHandler
{
    Task<object?> HandleRequestAsync(JsonRpcRequestContext context, CancellationToken cancellationToken);
    Task HandleNotificationAsync(string method, JsonElement parameters, CancellationToken cancellationToken);
}

/// <summary>
/// Bidirectional JSON-RPC 2.0 endpoint. Inbound requests run concurrently (the handler is entered synchronously
/// from the read loop, so a notification that follows a request can always find the state the request registered).
/// Outbound requests are correlated by id and support timeouts and cancellation.
/// </summary>
public sealed class JsonRpcPeer : IAsyncDisposable
{
    private readonly IAcpConnection _connection;
    private readonly IJsonRpcHandler _handler;
    private readonly int _maxMessageBytes;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inbound = new();
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private readonly CancellationTokenSource _shutdown = new();
    private long _nextId;

    public JsonRpcPeer(IAcpConnection connection, IJsonRpcHandler handler, int maxMessageBytes = 0, ILogger? logger = null)
    {
        _connection = connection;
        _handler = handler;
        _maxMessageBytes = maxMessageBytes;
        _logger = logger;
    }

    /// <summary>Reads and dispatches messages until the remote side closes or <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var registration = cancellationToken.Register(() => _shutdown.Cancel());
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                string? message;
                try
                {
                    message = await _connection.ReceiveAsync(_shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                if (message is null)
                {
                    break;
                }

                await ProcessAsync(message).ConfigureAwait(false);
            }
        }
        finally
        {
            _shutdown.Cancel();
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new JsonRpcConnectionClosedException());
            }

            try
            {
                await Task.WhenAll(_running.Keys).ConfigureAwait(false);
            }
            catch
            {
                // handler failures were already reported per request
            }
        }
    }

    public Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        var node = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
        {
            node["params"] = Serialize(parameters);
        }

        return SendNodeAsync(node, cancellationToken);
    }

    public async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;

        using var timeoutCts = new CancellationTokenSource();
        if (timeout is { } t && t > TimeSpan.Zero)
        {
            timeoutCts.CancelAfter(t);
        }

        using var timeoutReg = timeoutCts.Token.Register(() =>
            tcs.TrySetException(new TimeoutException($"Request '{method}' timed out after {timeout}.")));
        using var cancelReg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        try
        {
            var node = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null)
            {
                node["params"] = Serialize(parameters);
            }

            await SendNodeAsync(node, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SendNotificationAsync("$/cancel_request", new { requestId = id }).ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }

            throw;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    public async Task<T> SendRequestAsync<T>(
        string method,
        object? parameters,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await SendRequestAsync(method, parameters, timeout, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(result.GetRawText(), AcpJson.Options)
                ?? throw new JsonRpcRemoteException(JsonRpcErrorCodes.InvalidParams, $"Empty result for '{method}'.");
        }
        catch (JsonException ex)
        {
            throw new JsonRpcRemoteException(JsonRpcErrorCodes.InvalidParams, $"Malformed result for '{method}': {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ProcessAsync(string message)
    {
        if (_maxMessageBytes > 0 && Encoding.UTF8.GetByteCount(message) > _maxMessageBytes)
        {
            await SendErrorAsync(null, JsonRpcErrorCodes.InvalidRequest, "Message exceeds the maximum allowed size").ConfigureAwait(false);
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            await SendErrorAsync(null, JsonRpcErrorCodes.ParseError, "Parse error").ConfigureAwait(false);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await SendErrorAsync(null, JsonRpcErrorCodes.InvalidRequest, "Invalid Request: batches and non-object messages are not supported").ConfigureAwait(false);
                return;
            }

            JsonElement? id = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : null;
            if (id is { ValueKind: not (JsonValueKind.Number or JsonValueKind.String or JsonValueKind.Null) })
            {
                await SendErrorAsync(null, JsonRpcErrorCodes.InvalidRequest, "Invalid Request: id must be a string, number or null").ConfigureAwait(false);
                return;
            }

            if (!root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
            {
                await SendErrorAsync(id, JsonRpcErrorCodes.InvalidRequest, "Invalid Request: 'jsonrpc' must be \"2.0\"").ConfigureAwait(false);
                return;
            }

            if (root.TryGetProperty("method", out var methodElement))
            {
                if (methodElement.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(methodElement.GetString()))
                {
                    await SendErrorAsync(id, JsonRpcErrorCodes.InvalidRequest, "Invalid Request: 'method' must be a non-empty string").ConfigureAwait(false);
                    return;
                }

                var parameters = default(JsonElement);
                if (root.TryGetProperty("params", out var p))
                {
                    if (p.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null))
                    {
                        await SendErrorAsync(id, JsonRpcErrorCodes.InvalidRequest, "Invalid Request: 'params' must be structured").ConfigureAwait(false);
                        return;
                    }

                    parameters = p.Clone();
                }

                var method = methodElement.GetString()!;
                if (id is null)
                {
                    await HandleNotificationAsync(method, parameters).ConfigureAwait(false);
                }
                else
                {
                    Track(RunRequestAsync(id.Value, method, parameters));
                }

                return;
            }

            if (id is not null && (root.TryGetProperty("result", out var result) | root.TryGetProperty("error", out var error)))
            {
                CompleteResponse(id.Value, root);
                return;
            }

            await SendErrorAsync(id, JsonRpcErrorCodes.InvalidRequest, "Invalid Request").ConfigureAwait(false);
        }
    }

    private void CompleteResponse(JsonElement id, JsonElement root)
    {
        var key = id.GetRawText();
        if (!_pending.TryRemove(key, out var tcs))
        {
            _logger?.LogDebug("Ignoring response for unknown id {Id}", key);
            return;
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : JsonRpcErrorCodes.InternalError;
            var msg = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "Error";
            JsonElement? data = error.TryGetProperty("data", out var d) ? d.Clone() : null;
            tcs.TrySetException(new JsonRpcRemoteException(code, msg, data));
        }
        else
        {
            tcs.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default);
        }
    }

    private async Task HandleNotificationAsync(string method, JsonElement parameters)
    {
        try
        {
            if (method == "$/cancel_request")
            {
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("requestId", out var rid)
                    && _inbound.TryGetValue(rid.GetRawText(), out var cts))
                {
                    cts.Cancel();
                }

                return;
            }

            await _handler.HandleNotificationAsync(method, parameters, _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Notification handler for {Method} failed", method);
        }
    }

    private async Task RunRequestAsync(JsonElement id, string method, JsonElement parameters)
    {
        var key = id.GetRawText();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _inbound[key] = cts;
        try
        {
            var context = new JsonRpcRequestContext(method, parameters);
            var result = await _handler.HandleRequestAsync(context, cts.Token).ConfigureAwait(false);

            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["result"] = result is null ? null : Serialize(result)
            };
            await SendNodeAsync(response, CancellationToken.None).ConfigureAwait(false);

            foreach (var callback in context.AfterResponse)
            {
                try
                {
                    await callback().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Post-response callback for {Method} failed", method);
                }
            }
        }
        catch (JsonRpcException ex)
        {
            await TrySendErrorAsync(id, ex.Code, ex.Message, ex.ErrorData).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (!_shutdown.IsCancellationRequested)
            {
                await TrySendErrorAsync(id, JsonRpcErrorCodes.RequestCancelled, "Request cancelled").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Request handler for {Method} failed", method);
            await TrySendErrorAsync(id, JsonRpcErrorCodes.InternalError, "Internal error", new JsonObject { ["details"] = ex.Message }).ConfigureAwait(false);
        }
        finally
        {
            _inbound.TryRemove(key, out _);
        }
    }

    private void Track(Task task)
    {
        if (task.IsCompleted)
        {
            return;
        }

        _running[task] = 0;
        task.ContinueWith(t => _running.TryRemove(t, out _), TaskScheduler.Default);
    }

    private static JsonNode? Serialize(object value) =>
        JsonSerializer.SerializeToNode(value, value.GetType(), AcpJson.Options);

    private Task SendErrorAsync(JsonElement? id, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null)
        {
            error["data"] = data;
        }

        var node = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id is null ? null : JsonNode.Parse(id.Value.GetRawText()),
            ["error"] = error
        };
        return SendNodeAsync(node, CancellationToken.None);
    }

    private async Task TrySendErrorAsync(JsonElement id, int code, string message, JsonNode? data = null)
    {
        try
        {
            await SendErrorAsync(id, code, message, data).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not deliver error response");
        }
    }

    private async Task SendNodeAsync(JsonObject node, CancellationToken cancellationToken)
    {
        var text = node.ToJsonString();
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _connection.SendAsync(text, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}

