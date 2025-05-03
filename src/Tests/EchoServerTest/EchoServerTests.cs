using System.Net.Sockets;
using AsyncSocket;
using AsyncSocket.Framing;
using EchoServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tests.EchoServerTest;

[TestClass]
public class EchoServerTests
{
    private IHost Host { get; set; } = null!;
    private readonly int _port = 7779; // Use a unique port for testing
    
    [TestInitialize]
    public async Task Setup()
    {
        // Start the server in a similar way to how Program.cs does it
        var builder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder();
        builder.ConfigureServices((context, services) =>
        {
            var config = new AsyncServerConfig
            {
                IpAddress = "127.0.0.1",
                Port = _port,
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
        await Host.StartAsync();
        
        // Allow time for server to start
        await Task.Delay(500);
    }
    
    [TestCleanup]
    public async Task Cleanup()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
    
    [TestMethod]
    public async Task EchoServer_SendReceive_Success()
    {
        // Create a client
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _port);

        await using var stream = client.GetStream();
        var reader = new StreamReader(stream);
        var writer = new StreamWriter(stream) { AutoFlush = true };
        
        // Send a message
        string testMessage = "Hello Server\n";
        await writer.WriteAsync(testMessage);
        
        // Read the response
        string response = await reader.ReadLineAsync() + "\n";
        
        // Verify echo
        Assert.AreEqual(testMessage, response);
    }
    
    [TestMethod]
    public async Task EchoServer_QuitCommand_DisconnectsClient()
    {
        // Create a client
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _port);

        await using var stream = client.GetStream();
        var reader = new StreamReader(stream);
        var writer = new StreamWriter(stream) { AutoFlush = true };
        
        // Send quit command
        await writer.WriteAsync("quit\n");
        
        // Read the response
        string? response = await reader.ReadLineAsync();
        
        // Verify response
        Assert.AreEqual("ciao", response);

        await Task.Delay(500);
        
        var response2 = await reader.ReadLineAsync();
        Assert.IsNull(response2);

        var response3 = await reader.ReadLineAsync();
        Assert.IsNull(response3);

        var response4 = await reader.ReadLineAsync();
        Assert.IsNull(response4);

    }
}