using AsyncSocket;
using AsyncSocket.Framing;
using Microsoft.Extensions.Logging;

namespace EchoServer;
public class AsyncEchoServer(
    ILogger<AsyncEchoServer>? logger,
    AsyncServerConfig config, 
    IMessageFramingFactory<string> framingFactory, 
    ILogger<AsyncServer<string>>? logger2, 
    ILoggerFactory? loggerFactory) : AsyncServer<string>(config, framingFactory, logger2, loggerFactory)
{
    protected override Task HandleConnectedAsync(ClientSession<string> client)
    {
        logger?.LogDebug("Client Connected {clientId}", client.Id);
        return Task.CompletedTask;
    }
    protected override Task HandleDisconnectedAsync(ClientSession<string> client)
    {
        logger?.LogDebug("Client Disconnected {clientId}", client.Id);
        return Task.CompletedTask;
    }
    protected override async Task HandleMessageAsync(ClientSession<string> client, string message)
    {
        logger?.LogDebug($"Received from {client.Id}: {message}");
        
        if (message == "quit\n")
        {
            await client.SendAsync("ciao\n");
            await client.StopAsync();
            return;
        }
        
        await client.SendAsync(message);
    }

}