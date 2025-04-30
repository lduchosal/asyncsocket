using System.Net.Sockets;
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
        .AddScoped<AsyncServer, AsyncEchoServer>()
        .AddScoped<IMessageFramingFactory, CharDelimiterFramingFactory>()
        .AddScoped<AsyncServerConfig>(_ => new AsyncServerConfig
        {
            IpAddress = "127.0.0.1", 
            Port = 7777, 
            ProtocolType = ProtocolType.Tcp
        })
        .AddHostedService<EchoService>();
});

await builder
    .Build()
    .RunAsync()
    ;
