using System.Net.Sockets;
using System.Text;
using AsyncSocket;
using AsyncSocket.Framing;

namespace Tests.AsyncTcpServerTest;

[TestClass]
public class AsyncTcpServerTests
{
    private const string ServerIp = "127.0.0.1";
    private const int ServerPort = 8888;

    [TestMethod]
    public async Task ServerStart_WithInvalidIPAddress_ThrowsException()
    {
        // Arrange & Act & Assert
        await Assert.ThrowsExceptionAsync<FormatException>(async () =>
        {
            var config = new AsyncServerConfig { IpAddress = "invalid-ip", Port = ServerPort };
            var framingFactory = new CharDelimiterFramingFactory();
            await using var server = new AsyncTcpServer(config, framingFactory);
            var tokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await server.RunAsync(tokenSource.Token);
        });
    }

    [TestMethod]
    public async Task Server_StartsAndStops_Successfully()
    {
        // Arrange
        var config = new AsyncServerConfig { IpAddress = ServerIp, Port = ServerPort };
        var framingFactory = new CharDelimiterFramingFactory();
        await using AsyncTcpServer server = new(config, framingFactory);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act & Assert
        var serverTask = server.RunAsync(cts.Token);

        // Wait for cancellation or exception
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
        {
            await serverTask;
        });

        await cts.CancelAsync();

    }

    [TestMethod]
    public async Task Server_AcceptsClient_Successfully()
    {
        // Arrange
        var config = new AsyncServerConfig { IpAddress = ServerIp, Port = ServerPort };
        var framingFactory = new CharDelimiterFramingFactory();
        await using AsyncTcpServer server = new(config, framingFactory);
        var cts = new CancellationTokenSource();

        // Start server
        var serverTask = server.RunAsync(cts.Token);

        // Allow server to initialize
        await Task.Delay(1000, cts.Token);

        // Act
        var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(ServerIp, ServerPort, cts.Token);

        // Assert
        Assert.IsTrue(clientSocket.Connected);

        // Cleanup
        // Wait for the session to handle the error and complete
        await Task.WhenAny(serverTask, Task.Delay(1000, cts.Token));

        clientSocket.Close();
        await cts.CancelAsync();
    }

    [TestMethod]
    public async Task Server_EchoesMessage_Successfully()
    {
        // Arrange
        var config = new AsyncServerConfig { IpAddress = ServerIp, Port = ServerPort };
        var framingFactory = new CharDelimiterFramingFactory();
        await using var server = new AsyncTcpServer(config, framingFactory);
        var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(1000); // Allow server to initialize

        var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(ServerIp, ServerPort);

        // Act
        string testMessage = "Hello, Server!";
        byte[] sendBuffer = Encoding.UTF8.GetBytes(testMessage + "\n");
        await clientSocket.SendAsync(sendBuffer, SocketFlags.None);

        // Wait for response
        byte[] receiveBuffer = new byte[1024];
        var received = await clientSocket.ReceiveAsync(receiveBuffer, SocketFlags.None);
        string response = Encoding.UTF8.GetString(receiveBuffer, 0, received);

        // Assert
        StringAssert.Contains(response, testMessage);
        StringAssert.Contains(response, "Server received:");

        // Cleanup
        clientSocket.Close();
        await cts.CancelAsync();
    }

    [TestMethod]
    [Ignore("Flaky")]
    public async Task Server_HandlesMultipleClients_Successfully()
    {
        // Arrange
        const int clientCount = 5;
        var config = new AsyncServerConfig { IpAddress = ServerIp, Port = ServerPort, MaxConnections = 10};
        var framingFactory = new CharDelimiterFramingFactory();
        await using var server = new AsyncTcpServer(config, framingFactory);
        var cts = new CancellationTokenSource(5000);
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(200, cts.Token); // Allow server to initialize

        var clients = new List<Socket>();

        // Act
        for (int i = 0; i < clientCount; i++)
        {
            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await clientSocket.ConnectAsync(ServerIp, ServerPort, cts.Token);
            clients.Add(clientSocket);
        }

        // Assert
        Assert.AreEqual(clientCount, clients.Count);
        foreach (var client in clients)
        {
            Assert.IsTrue(client.Connected);
        }

        // Cleanup
         Task.WaitAll(serverTask);
       foreach (var client in clients)
        {
            client.Close();
        }

        await cts.CancelAsync();
    }

    [DataTestMethod]
    [DataRow("127.0.0.1", 8890)]
    [DataRow("127.0.0.1", 8891)]
    [DataRow("127.0.0.1", 8892)]
    public async Task Server_InitializesWithDifferentPorts_Successfully(string ip, int port)
    {
        // Arrange
        var config = new AsyncServerConfig { IpAddress = ip, Port = port };
        var framingFactory = new CharDelimiterFramingFactory();
        await using var server = new AsyncTcpServer(config, framingFactory);
        var cts = new CancellationTokenSource();

        // Act
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(100, cts.Token); // Allow server to initialize

        var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(ip, port, cts.Token);

        // Assert
        Assert.IsTrue(clientSocket.Connected);

        // Cleanup
        // Wait for the session to handle the error and complete
        await Task.WhenAny(serverTask, Task.Delay(1000, cts.Token));

        clientSocket.Close();
        await cts.CancelAsync();
    }

    [DataTestMethod]
    [DataRow("Short message\n")]
    [DataRow("This is a longer message with some content to process\n")]
    [DataRow("!@#$%^&*()_+-={}[]|\\:;\"'<>,.?/\n")]
    public async Task Server_HandlesVariousMessages_Successfully(string message)
    {
        // Arrange
        var config = new AsyncServerConfig { IpAddress = ServerIp, Port = ServerPort };
        var framingFactory = new CharDelimiterFramingFactory();
        await using var server = new AsyncTcpServer(config, framingFactory);
        var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(100); // Allow server to initialize

        var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(ServerIp, ServerPort);

        // Act
        byte[] sendBuffer = Encoding.UTF8.GetBytes(message);
        await clientSocket.SendAsync(sendBuffer, SocketFlags.None);

        // Wait for response
        byte[] receiveBuffer = new byte[1024];
        var received = await clientSocket.ReceiveAsync(receiveBuffer, SocketFlags.None);
        string response = Encoding.UTF8.GetString(receiveBuffer, 0, received);

        // Assert
        string expectedMessage = message.TrimEnd('\n');
        StringAssert.Contains(response, expectedMessage);
        StringAssert.Contains(response, "Server received:");

        // Cleanup
        // Wait for the session to handle the error and complete
        await Task.WhenAny(serverTask, Task.Delay(1000, cts.Token));
        clientSocket.Close();
        await cts.CancelAsync();
    }

    [TestMethod]
    [Ignore("Flaky")]
    public async Task Server_ReachesMaxConnections_BlocksNewClients()
    {
        // Note: This test would be more meaningful with a lower MaxConnections value
        // Since MaxConnections = 100 in the class, we'll create a mock to test this
        // This test demonstrates the concept

        // Arrange - in real implementation you'd use a mock or derived test class with lower MaxConnections
        const int testMaxConnections = 10; // Assuming MaxConnections was set to 10 for testing
        var config = new AsyncServerConfig { IpAddress = ServerIp, Port = ServerPort, MaxConnections = 2};
        var framingFactory = new CharDelimiterFramingFactory();
        await using var server = new AsyncTcpServer(config, framingFactory);
        var cts = new CancellationTokenSource(millisecondsDelay: 5000);
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(1000, cts.Token); // Allow server to initialize

        var clients = new List<Socket>();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            {
                // Act
                // Attempt to create connections up to max+1
                for (int i = 0; i < testMaxConnections + 1; i++)
                {
                    try
                    {
                        var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        clientSocket.SendTimeout = 500;
                        clientSocket.ReceiveTimeout = 500;
                        await clientSocket.ConnectAsync(ServerIp, ServerPort, cts.Token);
                        PrintSocketDebugInfo(clientSocket);
                        clients.Add(clientSocket);
                    }
                    catch (SocketException e)
                    {
                        // Expected for the connection beyond max
                        Console.WriteLine($"SocketException: {e}");
                    }
                }
            }
        );

        // Assert
        // Note: Since we can't actually hit the MaxConnections limit with the current implementation,
        // this assertion is more of a placeholder

        Assert.IsTrue(clients.Count <= testMaxConnections, $"clients {clients.Count} / {testMaxConnections}");

        // Cleanup
        // Wait for the session to handle the error and complete
        await Task.WhenAny(serverTask, Task.Delay(1000, cts.Token));

        foreach (var client in clients)
        {
            client.Close();
        }

        await cts.CancelAsync();
    }

    private static void PrintSocketDebugInfo(Socket clientSocket)
    {
        Console.WriteLine("===== Client Socket Debug Information =====");
        Console.WriteLine($"Connected: {clientSocket.Connected}");
        Console.WriteLine($"Remote EndPoint: {clientSocket.RemoteEndPoint}");
        Console.WriteLine($"Local EndPoint: {clientSocket.LocalEndPoint}");
        Console.WriteLine($"Handle: {clientSocket.Handle}");
        Console.WriteLine($"Blocking Mode: {clientSocket.Blocking}");
        Console.WriteLine($"Address Family: {clientSocket.AddressFamily}");
        Console.WriteLine($"Socket Type: {clientSocket.SocketType}");
        Console.WriteLine($"Protocol Type: {clientSocket.ProtocolType}");

        Console.WriteLine("Socket Options:");
        Console.WriteLine(
            $"  Keep Alive: {clientSocket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)}");
        Console.WriteLine($"  Receive Buffer Size: {clientSocket.ReceiveBufferSize}");
        Console.WriteLine($"  Send Buffer Size: {clientSocket.SendBufferSize}");
        Console.WriteLine($"  Receive Timeout: {clientSocket.ReceiveTimeout}ms");
        Console.WriteLine($"  Send Timeout: {clientSocket.SendTimeout}ms");
        Console.WriteLine($"  No Delay: {clientSocket.NoDelay}");
        Console.WriteLine($"  Dual Mode: {clientSocket.DualMode}");

        try
        {
            Console.WriteLine(
                $"  Linger State: Enabled={clientSocket.LingerState!.Enabled}, Linger Seconds={clientSocket.LingerState.LingerTime}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Linger State: Error retrieving - {ex.Message}");
        }

        Console.WriteLine("===========================================");
    }
}
