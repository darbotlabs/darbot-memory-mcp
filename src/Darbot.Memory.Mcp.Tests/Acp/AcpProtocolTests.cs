using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Darbot.Memory.Mcp.Api.Acp;
using Darbot.Memory.Mcp.Core.Acp;
using Darbot.Memory.Mcp.Core.Graph;
using Darbot.Memory.Mcp.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Darbot.Memory.Mcp.Tests.Acp;

/// <summary>Talks raw JSON to the agent to check protocol-level error handling.</summary>
public class AcpJsonRpcTests : IAsyncLifetime
{
    private readonly CancellationTokenSource _cts = new();
    private readonly InMemoryAcpConnection _raw;
    private readonly Task _server;

    public AcpJsonRpcTests()
    {
        var (serverSide, clientSide) = InMemoryAcpConnection.CreatePair();
        _raw = clientSide;
        var services = new ServiceCollection()
            .AddSingleton(Mock.Of<IGraphMemoryService>())
            .AddSingleton(Mock.Of<IConversationService>())
            .BuildServiceProvider();
        _server = AcpAgentHost.RunAsync(serverSide, services, new AcpSessionStore(), new AcpOptions { MaxMessageBytes = 4096 }, NullLogger.Instance, false, _cts.Token);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        await _raw.DisposeAsync();
        try
        {
            await _server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // shutdown race
        }
    }

    private async Task<JsonElement> SendAsync(string raw)
    {
        await _raw.SendAsync(raw);
        var reply = await _raw.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        reply.Should().NotBeNull();
        return JsonDocument.Parse(reply!).RootElement.Clone();
    }

    private static int Code(JsonElement response) => response.GetProperty("error").GetProperty("code").GetInt32();

    [Fact]
    public async Task MalformedJson_ReturnsParseErrorWithNullId()
    {
        var response = await SendAsync("{not json");
        Code(response).Should().Be(-32700);
        response.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        response.GetProperty("jsonrpc").GetString().Should().Be("2.0");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}]")]
    public async Task NonObjectOrBatch_IsInvalidRequest(string raw)
    {
        Code(await SendAsync(raw)).Should().Be(-32600);
    }

    [Fact]
    public async Task WrongVersionOrMissingMethod_IsInvalidRequestAndKeepsId()
    {
        var wrongVersion = await SendAsync("{\"jsonrpc\":\"1.0\",\"id\":7,\"method\":\"initialize\"}");
        Code(wrongVersion).Should().Be(-32600);
        wrongVersion.GetProperty("id").GetInt32().Should().Be(7);

        var noMethod = await SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"abc\"}");
        Code(noMethod).Should().Be(-32600);
        noMethod.GetProperty("id").GetString().Should().Be("abc");

        var badMethod = await SendAsync("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":5}");
        Code(badMethod).Should().Be(-32600);

        var badParams = await SendAsync("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"initialize\",\"params\":3}");
        Code(badParams).Should().Be(-32600);
    }

    [Fact]
    public async Task UnknownMethod_IsMethodNotFound()
    {
        var response = await SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"session/teleport\"}");
        Code(response).Should().Be(-32601);
        response.GetProperty("id").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task InvalidParams_IsReportedAsMinus32602()
    {
        var response = await SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"one\"}}");
        Code(response).Should().Be(-32602);
    }

    [Fact]
    public async Task OversizedMessage_IsRejected()
    {
        var huge = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":1,\"pad\":\"" + new string('x', 5000) + "\"}}";
        Code(await SendAsync(huge)).Should().Be(-32600);
    }

    [Fact]
    public async Task StringIds_AreEchoedVerbatim()
    {
        var response = await SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"req-1\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":1}}");
        response.GetProperty("id").GetString().Should().Be("req-1");
        response.GetProperty("result").GetProperty("protocolVersion").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Notifications_NeverProduceResponses()
    {
        await _raw.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"session/cancel\",\"params\":{\"sessionId\":\"nope\"}}");
        await _raw.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"totally/unknown\"}");
        var response = await SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":1}}");
        response.GetProperty("id").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task CancelRequest_AbortsInFlightRequestWithMinus32800()
    {
        var (a, b) = InMemoryAcpConnection.CreatePair();
        var started = new TaskCompletionSource();
        var handler = new DelegateHandler(async (ctx, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });
        await using var server = new JsonRpcPeer(a, handler);
        var serverLoop = server.RunAsync();
        await b.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"slow\"}");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await b.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancel_request\",\"params\":{\"requestId\":5}}");

        var reply = JsonDocument.Parse((await b.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))!).RootElement;
        Code(reply).Should().Be(-32800);
        await b.DisposeAsync();
        await serverLoop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class DelegateHandler(Func<JsonRpcRequestContext, CancellationToken, Task<object?>> onRequest) : IJsonRpcHandler
    {
        public Task<object?> HandleRequestAsync(JsonRpcRequestContext context, CancellationToken cancellationToken) => onRequest(context, cancellationToken);

        public Task HandleNotificationAsync(string method, JsonElement parameters, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public class AcpPeerTests
{
    [Fact]
    public async Task OutgoingRequests_TimeOutAndFailWhenConnectionCloses()
    {
        var (a, b) = InMemoryAcpConnection.CreatePair();
        await using var peer = new JsonRpcPeer(a, new SilentHandler());
        var loop = peer.RunAsync();

        var timeout = () => peer.SendRequestAsync("never/answered", null, TimeSpan.FromMilliseconds(100));
        await timeout.Should().ThrowAsync<TimeoutException>();

        var pending = peer.SendRequestAsync("also/pending", null, null);
        await b.DisposeAsync();
        var act = () => pending;
        await act.Should().ThrowAsync<JsonRpcConnectionClosedException>();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OutgoingRequestCancellation_SendsCancelRequestNotification()
    {
        var (a, b) = InMemoryAcpConnection.CreatePair();
        await using var peer = new JsonRpcPeer(a, new SilentHandler());
        _ = peer.RunAsync();
        using var cts = new CancellationTokenSource();

        var call = peer.SendRequestAsync("long/running", null, null, cts.Token);
        var request = JsonDocument.Parse((await b.ReceiveAsync())!).RootElement;
        await cts.CancelAsync();
        await call.Invoking(c => c).Should().ThrowAsync<OperationCanceledException>();

        var cancel = JsonDocument.Parse((await b.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))!).RootElement;
        cancel.GetProperty("method").GetString().Should().Be("$/cancel_request");
        cancel.GetProperty("params").GetProperty("requestId").GetInt64().Should().Be(request.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task ErrorResponses_SurfaceAsRemoteExceptions()
    {
        var (a, b) = InMemoryAcpConnection.CreatePair();
        await using var peer = new JsonRpcPeer(a, new SilentHandler());
        _ = peer.RunAsync();

        var call = peer.SendRequestAsync("x", null, TimeSpan.FromSeconds(5));
        var request = JsonDocument.Parse((await b.ReceiveAsync())!).RootElement;
        await b.SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{request.GetProperty("id").GetInt64()},\"error\":{{\"code\":-32002,\"message\":\"gone\",\"data\":{{\"uri\":\"a\"}}}}}}");

        var ex = (await call.Invoking(c => c).Should().ThrowAsync<JsonRpcRemoteException>()).Which;
        ex.Code.Should().Be(-32002);
        ex.Message.Should().Be("gone");
        ex.ErrorData!.Value.GetProperty("uri").GetString().Should().Be("a");
    }

    [Fact]
    public async Task ClientHelpers_AreGatedByCapability()
    {
        var (agentSide, clientSide) = InMemoryAcpConnection.CreatePair();
        var client = new AcpTestClient();
        var clientPeer = new JsonRpcPeer(clientSide, client);
        client.Attach(clientPeer);
        client.OnTerminalCreate = _ => new { terminalId = "term-1" };
        _ = clientPeer.RunAsync();

        await using var agentPeer = new JsonRpcPeer(agentSide, new SilentHandler());
        _ = agentPeer.RunAsync();
        var acpClient = new AcpClient(agentPeer, new AcpOptions());

        var denied = () => acpClient.CreateTerminalAsync("s1", "dotnet");
        await denied.Should().ThrowAsync<NotSupportedException>();
        var deniedRead = () => acpClient.ReadTextFileAsync("s1", "/a");
        await deniedRead.Should().ThrowAsync<NotSupportedException>();

        acpClient.Capabilities = new ClientCapabilities { Terminal = true, Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true } };
        (await acpClient.CreateTerminalAsync("s1", "dotnet", new[] { "test" })).Should().Be("term-1");

        client.Files["/a"] = "content";
        (await acpClient.ReadTextFileAsync("s1", "/a")).Should().Be("content");
        await acpClient.WriteTextFileAsync("s1", "/b", "written");
        client.Writes.Should().ContainSingle().Which.Should().Be(("/b", "written"));
    }

    private sealed class SilentHandler : IJsonRpcHandler
    {
        public Task<object?> HandleRequestAsync(JsonRpcRequestContext context, CancellationToken cancellationToken) =>
            throw new JsonRpcException(JsonRpcErrorCodes.MethodNotFound, "none");

        public Task HandleNotificationAsync(string method, JsonElement parameters, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public class AcpTransportTests
{
    [Fact]
    public async Task StreamConnection_RoundTripsNewlineDelimitedJson()
    {
        var pipe = new Pipe();
        var output = new MemoryStream();
        await using var connection = new StreamAcpConnection(pipe.Reader.AsStream(), output);

        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("\n{\"a\":1}\r\n\n{\"b\":2}\n"));
        (await connection.ReceiveAsync()).Should().Be("{\"a\":1}");
        (await connection.ReceiveAsync()).Should().Be("{\"b\":2}");

        await connection.SendAsync("{\"ok\":true}");
        Encoding.UTF8.GetString(output.ToArray()).Should().Be("{\"ok\":true}\n");

        var multiline = () => connection.SendAsync("{\"a\":\n1}").AsTask();
        await multiline.Should().ThrowAsync<ArgumentException>();

        await pipe.Writer.CompleteAsync();
        (await connection.ReceiveAsync()).Should().BeNull();
    }

    [Fact]
    public void ShouldRun_DetectsTheAcpFlag()
    {
        AcpStdioHost.ShouldRun(new[] { "--urls", "http://x", "--acp" }).Should().BeTrue();
        AcpStdioHost.ShouldRun(new[] { "--ACP" }).Should().BeTrue();
        AcpStdioHost.ShouldRun(Array.Empty<string>()).Should().BeFalse();
    }

    [Fact]
    public async Task StdioHost_RunsTheAgentOverStreams()
    {
        var services = new ServiceCollection()
            .AddSingleton(Mock.Of<IGraphMemoryService>())
            .AddSingleton(Mock.Of<IConversationService>())
            .AddSingleton<IOptions<AcpOptions>>(Options.Create(new AcpOptions()))
            .AddLogging()
            .BuildServiceProvider();

        var stdin = new Pipe();
        var stdout = new Pipe();
        var run = AcpStdioHost.RunAsync(services, stdin.Reader.AsStream(), stdout.Writer.AsStream());

        await stdin.Writer.WriteAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":1}}\n"));
        using var reader = new StreamReader(stdout.Reader.AsStream(), new UTF8Encoding(false));
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var response = JsonDocument.Parse(line!).RootElement;
        response.GetProperty("id").GetInt32().Should().Be(1);
        response.GetProperty("result").GetProperty("agentInfo").GetProperty("name").GetString().Should().Be("darbot-memory");

        await stdin.Writer.CompleteAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
