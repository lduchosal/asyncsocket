using AsyncSocket;
using AsyncSocket.Framing;
using Microsoft.Extensions.Logging;

namespace Tests.AsyncTcpServerTest;

public class AsyncTcpServer(
    AsyncServerConfig config,
    IMessageFramingFactory<string> framingFactory,
    ILoggerFactory? loggerFactory = null)
    : AsyncServer<string>(config, framingFactory, loggerFactory)
{
    private readonly ILogger<AsyncTcpServer>? Logger = loggerFactory?.CreateLogger<AsyncTcpServer>();

    protected internal override Task HandleConnectedAsync(ClientSession<string> client)
    {
        Logger?.LogDebug("Client Connected {clientId}", client.Id);
        return Task.CompletedTask;
    }
    protected internal override Task HandleDisconnectedAsync(ClientSession<string> client)
    {
        Logger?.LogDebug("Client Disconnected {clientId}", client.Id);
        return Task.CompletedTask;
    }
    protected internal override async Task HandleMessageAsync(ClientSession<string> client, string message)
    {
        Logger?.LogDebug("Received from {Id}: {message}", client.Id, message);
        // Echo the message back with the delimiter
        string response = $"Server received: {message}";
        await client.SendAsync(response);
    }
}