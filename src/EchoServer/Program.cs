using AsyncSocket;
using EchoServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateDefaultBuilder(args); // or console app
builder.ConfigureServices((context, services) =>
{
    services
        .AddScoped<LoggerFactory>()
        .AddScoped<AsyncTcpServer>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<AsyncTcpServer>>();
            var loggerFactory = serviceProvider.GetRequiredService<LoggerFactory>();
            return new AsyncTcpServer(logger, loggerFactory,"127.0.0.1", 7777);
        })
        .AddHostedService<EchoService>();
});

await builder
    .Build()
    .RunAsync()
    ;
