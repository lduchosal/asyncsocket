using AsyncSocket;
using AsyncSocket.Framing;
using Microsoft.Extensions.Logging;

namespace EchoServer;
public class AsyncEchoServer(
    ILogger<AsyncEchoServer>? logger,
    AsyncServerConfig config, 
    CharDelimiterFramingFactory framingFactory, 
    ILoggerFactory? loggerFactory) 
    : AsyncServer<string>(config, framingFactory, loggerFactory)
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
        logger?.LogDebug("Received from {Id}: {message}", client.Id, message);

        if (message == "quit\n")
        {
            await client.SendAsync("ciao\n");
            await client.StopAsync();
            return;
        }
        
        await client.SendAsync(message);
    }

}