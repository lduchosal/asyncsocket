using AsyncSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EchoServer;

public sealed class EchoService(
    ILogger<EchoService> logger,
    AsyncTcpServer server) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ExecuteAsync");
        await server.RunAsync(stoppingToken);
        logger.LogInformation("ExecuteAsync Ended");
    }
}