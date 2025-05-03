using AsyncSocket.Framing;

namespace Tests.AsyncTcpServerTest;

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AsyncSocket;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class AsyncTcpServerExceptionTests
{
    [TestMethod]
    public async Task RunAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        var config = new AsyncServerConfig { IpAddress = "127.0.0.1", Port = 8080, ProtocolType = ProtocolType.Tcp };
        var framingFactory = new CharDelimiterFramingFactory();
        var server = new AsyncTcpServer(config, framingFactory);
        using var cts = new CancellationTokenSource();

        // Act & Assert
        var serverTask = server.RunAsync(cts.Token);

        // Trigger cancellation after a short delay
        await Task.Delay(100);
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            async () => await serverTask);
    }

    [TestMethod]
    public async Task RunAsync_InvalidIPAddress_ThrowsFormatException()
    {
        // Act & Assert
        await Assert.ThrowsExceptionAsync<FormatException>(
            async () =>
            {
                // Arrange
                var config = new AsyncServerConfig { IpAddress = "invalid_ip", Port = 8080, ProtocolType = ProtocolType.Tcp };
                var framingFactory = new CharDelimiterFramingFactory();
                await using AsyncTcpServer server = new(config, framingFactory);
            });
    }

    [TestMethod]
    public async Task RunAsync_PortAlreadyInUse_ThrowsSocketException()
    {
        // Arrange
        // First server to occupy the port
        var config = new AsyncServerConfig { IpAddress = "127.0.0.1", Port = 8081 };
        var framingFactory = new CharDelimiterFramingFactory();
        var server1 = new AsyncTcpServer(config, framingFactory);
        using var cts1 = new CancellationTokenSource();
        var task1 = server1.RunAsync(cts1.Token);

        // Wait a moment for the first server to start
        await Task.Delay(100);

        // Second server trying to use the same port
        AsyncTcpServer server2 = new(config, framingFactory);
        using var cts2 = new CancellationTokenSource();

        // Act & Assert
        await Assert.ThrowsExceptionAsync<SocketException>(
            async () => await server2.RunAsync(cts2.Token));

        // Cleanup
        cts1.Cancel();
        try
        {
            await task1;
        }
        catch (OperationCanceledException)
        {
        }

        await server1.DisposeAsync();
        await server2.DisposeAsync();
    }

    [TestMethod]
    [Ignore("FailOnGitHub")]
    public async Task RunAsync_MaximumConnectionsExceeded_ClientsWaitForAvailableSlot()
    {
        // Arrange
        var config = new AsyncServerConfig { IpAddress = "127.0.0.1", Port = 8082, MaxConnections = 1};
        var framingFactory = new CharDelimiterFramingFactory();
        await using var server = new AsyncTcpServer(config, framingFactory);
        using var cts = new CancellationTokenSource(5000);
        var serverTask = server.RunAsync(cts.Token);

        // Wait for server to start
        await Task.Delay(200, cts.Token);

        // Act - Connect a client
        var client1 = new TcpClient();
        await client1.ConnectAsync("127.0.0.1", 8082, cts.Token);
        NetworkStream nwStream = client1.GetStream();
        await nwStream.WriteAsync("hello\n"u8.ToArray(), cts.Token);
        var buffer = new Memory<byte>();
        var read = await nwStream.ReadAsync(buffer, cts.Token);
        Console.WriteLine(buffer[..read]);
        
        // Assert - Second client connection attempt should time out waiting for semaphore
        var client2 = new TcpClient();
        using var timeoutCts = new CancellationTokenSource(500); // 500ms timeout

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            async () =>
            {
                await client2.ConnectAsync("127.0.0.1", 8082, timeoutCts.Token);
                NetworkStream nwStream2 = client2.GetStream();
                await nwStream2.WriteAsync("hello\n"u8.ToArray(), timeoutCts.Token);
                var buffer2 = new Memory<byte>();
                var read2 = await nwStream2.ReadAsync(buffer, timeoutCts.Token);
                Console.WriteLine(buffer2[..read2]);

            });

        // Cleanup
        Task.WaitAll(serverTask);

        client1.Close();
        client2.Close();
        await cts.CancelAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Ensure we don't leave TCP connections in TIME_WAIT state
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
