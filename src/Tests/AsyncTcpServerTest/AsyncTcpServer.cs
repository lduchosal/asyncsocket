using AsyncSocket;
using Microsoft.Extensions.Logging;

namespace Tests.AsyncTcpServerTest;

public class AsyncTcpServer : AsyncServer<string>
{
    private readonly ILogger<AsyncTcpServer>? Logger;

    public AsyncTcpServer(ILogger<AsyncTcpServer>? logger,
        AsyncServerConfig config, 
        IMessageFramingFactory<string> framingFactory, 
        ILogger<AsyncServer<string>>? logger2, 
        ILoggerFactory? loggerFactory) : base(config, framingFactory, logger2, loggerFactory)
    {
        Logger = logger;
    }
    
    public AsyncTcpServer(AsyncServerConfig config, IMessageFramingFactory<string> framingFactory) : base(config, framingFactory, null, null)
    {
    }

    protected override Task HandleConnectedAsync(ClientSession<string> client)
    {
        Logger?.LogDebug("Client Connected {clientId}", client.Id);
        return Task.CompletedTask;
    }
    protected override Task HandleDisconnectedAsync(ClientSession<string> client)
    {
        Logger?.LogDebug("Client Disconnected {clientId}", client.Id);
        return Task.CompletedTask;
    }
    protected override async Task HandleMessageAsync(ClientSession<string> client, string message)
    {
        Logger?.LogDebug($"Received from {client.Id}: {message}");
        // Echo the message back with the delimiter
        string response = $"Server received: {message}";
        await client.SendAsync(response);
    }
}