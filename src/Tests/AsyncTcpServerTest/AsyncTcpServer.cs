using AsyncSocket;
using Microsoft.Extensions.Logging;

namespace Tests.AsyncTcpServerTest;

public class AsyncTcpServer : AsyncServer
{
    private readonly ILogger<AsyncTcpServer>? Logger;

    public AsyncTcpServer(ILogger<AsyncTcpServer>? logger,
        AsyncServerConfig config, 
        IMessageFramingFactory framingFactory, 
        ILogger<AsyncServer>? logger2, 
        ILoggerFactory? loggerFactory) : base(config, framingFactory, logger2, loggerFactory)
    {
        Logger = logger;
    }
    
    public AsyncTcpServer(AsyncServerConfig config, IMessageFramingFactory framingFactory) : base(config, framingFactory, null, null)
    {
    }

    protected override Task HandleConnectedAsync(ClientSession client)
    {
        Logger?.LogDebug("Client Connected {clientId}", client.Id);
        return Task.CompletedTask;
    }
    protected override Task HandleDisconnectedAsync(ClientSession client)
    {
        Logger?.LogDebug("Client Disconnected {clientId}", client.Id);
        return Task.CompletedTask;
    }
    protected override async Task HandleMessageAsync(ClientSession client, string message)
    {
        Logger?.LogDebug($"Received from {client.Id}: {message}");
        // Echo the message back with the delimiter
        string response = $"Server received: {message}";
        await client.SendAsync(response);
    }
}