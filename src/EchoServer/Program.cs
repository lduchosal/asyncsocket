using System.Net.Sockets;
using AsyncSocket;
using AsyncSocket.Framing;
using EchoServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateDefaultBuilder(args); // or console app
builder.ConfigureServices((context, services) =>
{
    var config = new AsyncServerConfig
    {
        IpAddress = "127.0.0.1",
        Port = 7777,
        ProtocolType = ProtocolType.Tcp
    };
    services
        .AddScoped<LoggerFactory>()
        .AddScoped<AsyncEchoServer>()
        .AddScoped<CharDelimiterFramingFactory>()
        .AddScoped<AsyncServerConfig>(_ => config)
        .AddHostedService<EchoService>();
});

await builder
    .Build()
    .RunAsync()
    ;
