using System.Net;
using System.Net.Sockets;
using System.Text;
using AsyncSocket;
using AsyncSocket.Framing;

namespace Tests.AsyncTcpServerTest;

[TestClass]
public class AsyncTcpServerConcurrencyTests
{
    private const string ServerIp = "127.0.0.1";
    private int _port = 8989;

    [TestInitialize]
    public void Setup()
    {
        _port= new Random().Next(10000, 65000);
    }

    [TestMethod]
    [DataRow(5)]
    [DataRow(10)]
    [DataRow(20)]
    [Ignore("Flaky")]
    public async Task HandlesConcurrentConnections_WithinMaxConnections(int connectionCount)
    {
        // Arrange
        using CancellationTokenSource cts = new(1000);
        var config = new AsyncServerConfig { IpAddress = ServerIp, Port = _port, MaxConnections = connectionCount};
        var framingFactory = new CharDelimiterFramingFactory();
        await using var server = new AsyncTcpServer(config, framingFactory);
        var serverTask = server.RunAsync(cts.Token);

        // Wait for server to start
        await Task.Delay(200);

        // Act
        var clients = new List<TcpClient>();
        var connectionTasks = new List<Task>();

        for (int i = 0; i < connectionCount; i++)
        {
            connectionTasks.Add(Task.Run(async () =>
            {
                var client = new TcpClient();
                await client.ConnectAsync(ServerIp, _port, cts.Token);

                lock (clients)
                {
                    clients.Add(client);
                }

                // Send a message and verify response
                string message = $"Hello from client {i}";
                byte[] data = Encoding.UTF8.GetBytes(message + "\n");

                var stream = client.GetStream();
                await stream.WriteAsync(data, 0, data.Length);

                // Read response
                byte[] buffer = new byte[1024];
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                Assert.IsTrue(response.Contains(message));
            }, cts.Token));
        }

        // Assert
        await Task.WhenAll(connectionTasks);
        await Task.WhenAll(serverTask);

        foreach (var client in clients)
        {
            client.Close();
        }
    }

    [TestMethod]
    [Ignore("Flaky")]
    [DataRow(2)]
    [DataRow(5)]
    public async Task ReleasesConnectionSlots_WhenClientsDisconnect(int maxConnections)
    {
        // Arrange
        using CancellationTokenSource cts = new(1000);
        var config = new AsyncServerConfig { IpAddress = ServerIp, Port = _port, MaxConnections = maxConnections};
        var framingFactory = new CharDelimiterFramingFactory();
        await using var server = new AsyncTcpServer(config, framingFactory);
        var serverTask = server.RunAsync(cts.Token);

        // Wait for server to start
        await Task.Delay(200);

        // Act - Phase 1: Fill all connection slots
        var initialClients = new List<TcpClient>();
        for (int i = 0; i < maxConnections; i++)
        {
            var client = new TcpClient();
            await client.ConnectAsync(ServerIp, _port);
            initialClients.Add(client);
        }

        // Phase 2: Disconnect half the clients
        int disconnectCount = maxConnections / 2;
        for (int i = 0; i < disconnectCount; i++)
        {
            initialClients[i].Close();
        }

        initialClients.RemoveRange(0, disconnectCount);

        // Allow time for server to process disconnections
        await Task.Delay(200);

        // Phase 3: Connect new clients to fill newly available slots
        var newClients = new List<TcpClient>();
        var connectionTasks = new List<Task>();

        for (int i = 0; i < disconnectCount; i++)
        {
            connectionTasks.Add(Task.Run(async () =>
            {
                var client = new TcpClient();
                await client.ConnectAsync(ServerIp, _port);

                lock (newClients)
                {
                    newClients.Add(client);
                }
            }));
        }

        // Wait for all connect attempts with timeout
        await Task.WhenAll(connectionTasks);
        await Task.WhenAll(serverTask);

        // Assert
        Assert.AreEqual(disconnectCount, newClients.Count,
            "Should be able to connect exactly the number of clients that were disconnected");

        // Cleanup
        foreach (var client in initialClients.Concat(newClients))
        {
            client.Close();
        }
    }

    [TestMethod]
    [Ignore("FailOnGitHub")]
    public async Task ServerDispose_ClosesAllClientConnections()
    {
        // Arrange
        using CancellationTokenSource cts = new(5000);
        cts.Token.Register(() => { Console.WriteLine("Canceled token"); });
        const int connectionCount = 5;
        var config = new AsyncServerConfig { IpAddress = ServerIp, Port = _port, MaxConnections = connectionCount};
        var framingFactory = new CharDelimiterFramingFactory();
        await using var server = new AsyncTcpServer(config, framingFactory);
        var serverTask = server.RunAsync(cts.Token);

        // Wait for server to start
        await Task.Delay(500, cts.Token);

        // Connect clients
        var clients = new List<TcpClient>();
        for (int i = 0; i < connectionCount; i++)
        {
            var client = new TcpClient();
            await client.ConnectAsync(ServerIp, _port, cts.Token);
            clients.Add(client);
        }

        // Act
        await server.DisposeAsync();
        Task.WaitAll(serverTask);

        // Wait to ensure sockets have time to recognize disconnection
        await Task.Delay(1000, cts.Token);

        // Assert
        foreach (var client in clients)
        {
            var isSocketConnected = IsSocketConnected(client.Client);
            Assert.IsFalse(isSocketConnected, "Socket should be disconnected after server disposal");
        }
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(65536)]
    public async Task ServerStart_WithInvalidPort_ThrowsException(int invalidPort)
    {
        // Act & Assert
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(async () =>
        {
            var config = new AsyncServerConfig { IpAddress = ServerIp, Port = invalidPort };
            var framingFactory = new CharDelimiterFramingFactory();
            await using var server = new AsyncTcpServer(config, framingFactory);
            using var tokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000));
            await server.RunAsync(tokenSource.Token);
        });
    }

    [TestMethod]
    [Ignore("Flaky")]
    public async Task ConcurrentMessagesFromSameClient_HandledCorrectly()
    {
        // Arrange
        using CancellationTokenSource cts = new(1000);
        var config = new AsyncServerConfig { IpAddress = ServerIp, Port = _port, MaxConnections = 1};
        var framingFactory = new CharDelimiterFramingFactory();
        await using var server = new AsyncTcpServer(config, framingFactory);
        var serverTask = server.RunAsync(cts.Token);

        // Wait for server to start
        await Task.Delay(200, cts.Token);

        var client = new TcpClient();
        await client.ConnectAsync(ServerIp, _port, cts.Token);
        var stream = client.GetStream();

        const int messageCount = 100;
        var sendTasks = new List<Task>();
        var sentMessages = new List<string>();
        var receivedResponses = new List<string>();
        var messageLock = new object();

        // Act
        for (int i = 0; i < messageCount; i++)
        {
            var message = $"Message {i}";
            sentMessages.Add(message);

            sendTasks.Add(Task.Run(async () =>
            {
                byte[] data = Encoding.UTF8.GetBytes(message + "\n");

                await stream.WriteAsync(data, 0, data.Length);

                byte[] buffer = new byte[1024];
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                lock (messageLock)
                {
                    receivedResponses.Add(response);
                }
            }, cts.Token));
        }

        await Task.WhenAll(sendTasks);
        await Task.WhenAll(serverTask);

        // Assert
        Assert.AreEqual(messageCount, receivedResponses.Count, "Should receive responses for all sent messages");

        foreach (var message in sentMessages)
        {
            Assert.IsTrue(receivedResponses.Any(r => r.Contains(message)),
                $"Response for message '{message}' not found");
        }

        client.Close();
    }
    
    public static bool IsSocketConnected(Socket socket)
    {
        if (!socket.Connected)
            return false;
        
        // Check if the socket is readable and has no data, which indicates disconnection
        bool part1 = socket.Poll(1000, SelectMode.SelectRead);
        bool part2 = (socket.Available == 0);
    
        if (part1 && part2)
            return false; // Connection is closed
        
        return true; // Connection is still open
    }
}
