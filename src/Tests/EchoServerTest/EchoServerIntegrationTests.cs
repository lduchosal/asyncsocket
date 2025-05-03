using System.Net.Sockets;
using AsyncSocket;
using AsyncSocket.Framing;
using EchoServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tests.EchoServerTest;

[TestClass]
public class EchoServerIntegrationTests
{
    private IHost Host { get; set; } = null!;
    private CancellationTokenSource Cts { get; set; } = null!;
    
    [TestInitialize]
    public async Task Setup()
    {
        Cts = new CancellationTokenSource();
        
        var builder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder();
        builder.ConfigureServices((context, services) =>
        {
            var config = new AsyncServerConfig
            {
                IpAddress = "127.0.0.1",
                Port = 7778, // Use a different port for testing
                ProtocolType = ProtocolType.Tcp
            };
            services
                .AddSingleton<LoggerFactory>()
                .AddSingleton<AsyncEchoServer>()
                .AddSingleton<CharDelimiterFramingFactory>()
                .AddSingleton<AsyncServerConfig>(_ => config)
                .AddHostedService<EchoService>();
        });
        
        Host = builder.Build();
        
        // Start the server in background
        _ = Host.StartAsync(Cts.Token);
        
        // Allow time for server to start
        await Task.Delay(500);
    }
    
    [TestCleanup]
    public async Task Cleanup()
    {
        Cts.Cancel();
        await Host.StopAsync();
    }
    
    [TestMethod]
    public async Task EchoServer_SendMessage_ReceivesEcho()
    {
        // Arrange
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", 7778);
        
        using var stream = client.GetStream();
        var reader = new StreamReader(stream);
        var writer = new StreamWriter(stream) { AutoFlush = true };
        
        // Act
        await writer.WriteLineAsync("hello");
        
        // Assert
        var response = await reader.ReadLineAsync();
        Assert.AreEqual("hello", response);
    }
}