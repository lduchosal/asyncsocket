using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AsyncSocket;

namespace Tests.ClientSessionTest;

[TestClass]
public class ClientSessionStressTests
{
    private const char Delimiter = '\n';
    private const int MaxSiteWithoutADelimiter = 8192;
    private const int BufferSize = 8192;
    private SocketAsyncEventArgsPool _argsPool;
    private CancellationTokenSource _globalCts;

    [TestInitialize]
    public void Setup()
    {
        _argsPool = new SocketAsyncEventArgsPool(1000); // Large pool for stress tests
        _globalCts = new CancellationTokenSource();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _globalCts.Cancel();
        _globalCts.Dispose();
    }

    [TestMethod]
    [Timeout(120000)] // 2 minute timeout
    public async Task StressTest_RapidConnectionCycles()
    {
        // Arrange
        const int iterations = 1000;
        const int maxConcurrentConnections = 50;
        var semaphore = new SemaphoreSlim(maxConcurrentConnections);
        var activeSessions = new ConcurrentBag<(ClientSession session, Socket clientSocket, Task sessionTask)>();
        int successfulConnections = 0;
        int failedConnections = 0;
            
        // Act
        var stopwatch = new Stopwatch();
        stopwatch.Start();
            
        var tasks = new Task[iterations];
            
        for (int i = 0; i < iterations; i++)
        {
            int iterationNumber = i;
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    await semaphore.WaitAsync();
                        
                    try
                    {
                        // Create session
                        var (session, clientSocket, sessionTask) = await CreateClientSessionPairAsync();
                        activeSessions.Add((session, clientSocket, sessionTask));
                            
                        // Use session briefly
                        await session.SendAsync($"Test message {iterationNumber}{Delimiter}");
                            
                        // Read from client socket
                        byte[] buffer = new byte[BufferSize];
                        await clientSocket.ReceiveAsync(buffer, SocketFlags.None);
                            
                        // Short random delay to simulate usage
                        await Task.Delay(new Random().Next(10, 50));
                            
                        // Clean up
                        await session.StopAsync();
                        clientSocket.Close();
                            
                        Interlocked.Increment(ref successfulConnections);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Connection cycle error: {ex.Message}");
                        Interlocked.Increment(ref failedConnections);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Fatal error in connection cycle: {ex.Message}");
                    Interlocked.Increment(ref failedConnections);
                }
            });
                
            // Occasionally introduce bursts of connections
            if (i % 100 == 0)
            {
                await Task.Delay(200);  // Pause to build up queued operations
            }
            else if (i % 10 == 0)
            {
                await Task.Delay(5);   // Slight delay between most connections
            }
        }
            
        await Task.WhenAll(tasks);
        stopwatch.Stop();
            
        // Assert
        double successRate = (double)successfulConnections / iterations * 100;
        double connectionsPerSecond = successfulConnections / (stopwatch.ElapsedMilliseconds / 1000.0);
            
        Console.WriteLine($"Rapid connection cycles:");
        Console.WriteLine($"Total iterations: {iterations}");
        Console.WriteLine($"Successful connections: {successfulConnections}");
        Console.WriteLine($"Failed connections: {failedConnections}");
        Console.WriteLine($"Success rate: {successRate:F2}%");
        Console.WriteLine($"Connections per second: {connectionsPerSecond:F2}");
        Console.WriteLine($"Total elapsed time: {stopwatch.ElapsedMilliseconds / 1000.0:F2} seconds");
            
        Assert.IsTrue(successRate > 95, "At least 95% of connections should succeed");
            
        // Clean up any remaining sessions (should be none, but just in case)
        var remainingSessions = activeSessions.ToArray();
        foreach (var (session, clientSocket, _) in remainingSessions)
        {
            try
            {
                await session.StopAsync();
                clientSocket.Close();
            }
            catch { /* Ignore cleanup errors */ }
        }
    }

    [TestMethod]
    [Timeout(120000)] // 2 minute timeout
    public async Task StressTest_LargeMessageVolume()
    {
        // Arrange
        var (session, clientSocket, sessionTask) = await CreateClientSessionPairAsync();
            
        const int totalMessages = 100000;   // 100K messages
        const int messageSize = 1024;       // 1KB each
        const int batchSize = 1000;         // Send in batches
            
        string message = new string('X', messageSize - 1) + Delimiter;
        long totalBytesSent = 0;
        int sentMessages = 0;
        int receivedMessages = 0;
            
        // Set up receiver task
        var receiverCts = new CancellationTokenSource();
        var receivedBytes = new ConcurrentBag<byte[]>();
            
        Task receiverTask = Task.Run(async () =>
        {
            byte[] buffer = new byte[BufferSize * 2];
            StringBuilder accumulated = new StringBuilder();
                
            try
            {
                while (!receiverCts.Token.IsCancellationRequested)
                {
                    int bytesRead = await clientSocket.ReceiveAsync(buffer, SocketFlags.None);
                        
                    if (bytesRead == 0)
                        break;
                            
                    string received = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    accumulated.Append(received);
                        
                    // Count delimiters
                    int lastDelimiterPos = 0;
                    int delimiterPos;
                    while ((delimiterPos = accumulated.ToString().IndexOf(Delimiter, lastDelimiterPos)) != -1)
                    {
                        Interlocked.Increment(ref receivedMessages);
                        lastDelimiterPos = delimiterPos + 1;
                    }
                        
                    // Trim the processed parts
                    if (lastDelimiterPos > 0)
                    {
                        accumulated.Remove(0, lastDelimiterPos);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Receiver error: {ex.Message}");
            }
        }, receiverCts.Token);
            
        // Act
        var stopwatch = new Stopwatch();
        stopwatch.Start();
            
        // Send messages in batches
        for (int batch = 0; batch < totalMessages / batchSize; batch++)
        {
            var batchTasks = new Task[batchSize];
                
            for (int i = 0; i < batchSize; i++)
            {
                batchTasks[i] = session.SendAsync(message);
                Interlocked.Increment(ref sentMessages);
                Interlocked.Add(ref totalBytesSent, messageSize);
            }
                
            await Task.WhenAll(batchTasks);
                
            // Print progress every 10 batches
            if (batch % 10 == 0)
            {
                Console.WriteLine($"Sent {sentMessages:N0} messages, received {receivedMessages:N0} messages");
            }
        }
            
        stopwatch.Stop();
            
        // Give receiver time to process remaining messages
        await Task.Delay(2000);
        receiverCts.Cancel();
            
        // Assert
        double elapsedSeconds = stopwatch.ElapsedMilliseconds / 1000.0;
        double throughputMbps = (totalBytesSent * 8) / (elapsedSeconds * 1_000_000);
        double messagesPerSecond = sentMessages / elapsedSeconds;
            
        Console.WriteLine("\nLarge message volume test results:");
        Console.WriteLine($"Messages sent: {sentMessages:N0}");
        Console.WriteLine($"Messages received: {receivedMessages:N0}");
        Console.WriteLine($"Total bytes sent: {totalBytesSent:N0} ({totalBytesSent / (1024 * 1024):F2} MB)");
        Console.WriteLine($"Elapsed time: {elapsedSeconds:F2} seconds");
        Console.WriteLine($"Throughput: {throughputMbps:F2} Mbps");
        Console.WriteLine($"Messages per second: {messagesPerSecond:F2}");
            
        Assert.IsTrue(receivedMessages >= sentMessages * 0.95, "At least 95% of messages should be received");
            
        // Clean up
        await session.StopAsync();
        clientSocket.Close();
        await Task.WhenAny(sessionTask, Task.Delay(1000));
    }

    [TestMethod]
    [Timeout(180000)] // 3 minute timeout
    public async Task StressTest_ResourceExhaustion()
    {
        // Arrange
        const int maxConcurrentSessions = 200;
        const int messagesPerSession = 50;
        const int messageSize = 4096; // 4KB
            
        var activeConnections = new ConcurrentBag<(ClientSession session, Socket clientSocket, Task sessionTask)>();
        int sessionsCreated = 0;
        int sessionsErrored = 0;
            
        // Monitor starting resources
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long startingMemory = GC.GetTotalMemory(true);
            
        // Act
        var stopwatch = new Stopwatch();
        stopwatch.Start();
            
        try
        {
            // Create sessions until we hit a limit or max count
            while (sessionsCreated < maxConcurrentSessions)
            {
                try
                {
                    var (session, clientSocket, sessionTask) = await CreateClientSessionPairAsync();
                    activeConnections.Add((session, clientSocket, sessionTask));
                    Interlocked.Increment(ref sessionsCreated);
                        
                    // Start using the session
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string message = new string('X', messageSize - 1) + Delimiter;
                            byte[] receiveBuffer = new byte[BufferSize];
                                
                            for (int i = 0; i < messagesPerSession; i++)
                            {
                                // Send a message
                                await session.SendAsync(message);
                                    
                                // Receive the message
                                await clientSocket.ReceiveAsync(receiveBuffer, SocketFlags.None);
                                    
                                // Small delay
                                await Task.Delay(new Random().Next(1, 10));
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Session use error: {ex.Message}");
                        }
                    });
                        
                    // Print progress
                    if (sessionsCreated % 10 == 0)
                    {
                        var currentMemory = GC.GetTotalMemory(false);
                        Console.WriteLine($"Created {sessionsCreated} sessions. " +
                                          $"Memory: {currentMemory / (1024 * 1024):F2} MB, " +
                                          $"Delta: {(currentMemory - startingMemory) / (1024 * 1024):F2} MB");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to create session: {ex.Message}");
                    Interlocked.Increment(ref sessionsErrored);
                        
                    // If we hit multiple errors, we might be at resource limits
                    if (sessionsErrored > 5)
                    {
                        Console.WriteLine("Multiple session creation errors - likely reaching system limits");
                        break;
                    }
                }
            }
                
            Console.WriteLine($"Created {sessionsCreated} sessions before stopping");
                
            // Keep sessions active for a period
            await Task.Delay(Math.Min(5000, 5000 / Math.Max(1, sessionsCreated / 50)));
        }
        finally
        {
            // Clean up all sessions
            var cleanup = activeConnections.ToArray();
            Console.WriteLine($"Cleaning up {cleanup.Length} sessions...");
                
            var cleanupTasks = new List<Task>();
            foreach (var (session, clientSocket, _) in cleanup)
            {
                cleanupTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await session.StopAsync();
                        clientSocket.Close();
                    }
                    catch { /* Ignore cleanup errors */ }
                }));
            }
                
            await Task.WhenAll(cleanupTasks);
        }
            
        stopwatch.Stop();
            
        // Measure final memory usage
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long endingMemory = GC.GetTotalMemory(true);
            
        // Calculate metrics
        double memoryDeltaMB = (endingMemory - startingMemory) / (1024.0 * 1024.0);
        double memoryPerSessionKB = sessionsCreated > 0 ? 
            (endingMemory - startingMemory) / (sessionsCreated * 1024.0) : 0;
            
        // Assert
        Console.WriteLine("\nResource Exhaustion Test Results:");
        Console.WriteLine($"Sessions successfully created: {sessionsCreated}");
        Console.WriteLine($"Sessions failed to create: {sessionsErrored}");
        Console.WriteLine($"Starting memory: {startingMemory / (1024 * 1024):F2} MB");
        Console.WriteLine($"Ending memory: {endingMemory / (1024 * 1024):F2} MB");
        Console.WriteLine($"Memory usage delta: {memoryDeltaMB:F2} MB");
        Console.WriteLine($"Memory per session: {memoryPerSessionKB:F2} KB");
        Console.WriteLine($"Test duration: {stopwatch.ElapsedMilliseconds / 1000.0:F2} seconds");
            
        // Even with resource constraints, we should create a significant number of sessions
        Assert.IsTrue(sessionsCreated > 0, "Should create at least some sessions");
            
        // Memory should be reclaimed after cleanup
        Assert.IsTrue(memoryDeltaMB < 50, "Most memory should be reclaimed after session cleanup");
    }

    [TestMethod]
    [Timeout(120000)] // 2 minute timeout
    public async Task StressTest_MessageBoundaries()
    {
        // Arrange
        var (session, clientSocket, sessionTask) = await CreateClientSessionPairAsync();
            
        const int totalMessages = 10000;
        var receivedMessages = new ConcurrentBag<string>();
        var messageSizes = new[] { 1, 10, 100, 1000, 4096, 8192 };
            
        session.MessageReceived += (sender, message) =>
        {
            receivedMessages.Add(message);
        };
            
        // Act
        var stopwatch = new Stopwatch();
        stopwatch.Start();
            
        for (int i = 0; i < totalMessages; i++)
        {
            try
            {
                // Select a random message size
                int sizeIndex = i % messageSizes.Length;
                int size = messageSizes[sizeIndex];
                    
                // Create a message with a unique identifier
                string messageId = $"MSG{i:D5}";
                int messageSize = size - messageId.Length - 1;
                string padding = new string('X', messageSize >= 0 ? messageSize : 0);
                string message = messageId + padding + Delimiter;
                    
                // Send from client socket to session
                byte[] data = Encoding.UTF8.GetBytes(message);
                    
                // Occasionally split the message to test boundary handling
                if (i % 10 == 0 && size > 20)
                {
                    int splitPoint = data.Length / 2;
                        
                    byte[] firstPart = new byte[splitPoint];
                    byte[] secondPart = new byte[data.Length - splitPoint];
                        
                    Buffer.BlockCopy(data, 0, firstPart, 0, splitPoint);
                    Buffer.BlockCopy(data, splitPoint, secondPart, 0, data.Length - splitPoint);
                        
                    await clientSocket.SendAsync(firstPart, SocketFlags.None);
                    await Task.Delay(1); // Tiny delay to ensure separation
                    await clientSocket.SendAsync(secondPart, SocketFlags.None);
                }
                else
                {
                    await clientSocket.SendAsync(data, SocketFlags.None);
                }
                    
                // Occasionally send multiple messages without delay
                if (i % 50 == 0 && i < totalMessages - 3)
                {
                    byte[] burst1 = Encoding.UTF8.GetBytes($"BURST1-{i}{Delimiter}");
                    byte[] burst2 = Encoding.UTF8.GetBytes($"BURST2-{i}{Delimiter}");
                    byte[] burst3 = Encoding.UTF8.GetBytes($"BURST3-{i}{Delimiter}");
                        
                    await clientSocket.SendAsync(burst1, SocketFlags.None);
                    await clientSocket.SendAsync(burst2, SocketFlags.None);
                    await clientSocket.SendAsync(burst3, SocketFlags.None);
                        
                    i += 3; // Adjust counter since we sent extra messages
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending message {i}: {ex.Message}");
            }
                
            // Print progress
            if (i % 1000 == 0 && i > 0)
            {
                Console.WriteLine($"Sent {i} messages, received {receivedMessages.Count}");
            }
        }
            
        // Wait for message processing to complete
        int waitIterations = 0;
        while (receivedMessages.Count < totalMessages && waitIterations < 100)
        {
            await Task.Delay(100);
            waitIterations++;
        }
            
        stopwatch.Stop();
            
        // Assert
        double receivedPercentage = (double)receivedMessages.Count / totalMessages * 100;
            
        Console.WriteLine("\nMessage Boundaries Test Results:");
        Console.WriteLine($"Messages sent: {totalMessages}");
        Console.WriteLine($"Messages received: {receivedMessages.Count}");
        Console.WriteLine($"Success rate: {receivedPercentage:F2}%");
        Console.WriteLine($"Test duration: {stopwatch.ElapsedMilliseconds / 1000.0:F2} seconds");
            
        // Verify some messages from each size category
        bool foundSmall = false;
        bool foundMedium = false;
        bool foundLarge = false;
            
        foreach (var msg in receivedMessages)
        {
            if (msg.Length <= 20) foundSmall = true;
            else if (msg.Length > 20 && msg.Length <= 1000) foundMedium = true;
            else if (msg.Length > 1000) foundLarge = true;
                
            if (foundSmall && foundMedium && foundLarge) break;
        }
            
        Assert.IsTrue(receivedPercentage >= 99, $"Should receive at least 99% of messages ({receivedPercentage})");
        Assert.IsTrue(foundSmall && foundMedium && foundLarge, 
            "Should receive messages of all size categories");
            
        // Clean up
        await session.StopAsync();
        clientSocket.Close();
        await Task.WhenAny(sessionTask, Task.Delay(1000));
    }
        
    // Helper method to create a client-server socket pair and client session
    private async Task<(ClientSession session, Socket clientSocket, Task sessionTask)> 
        CreateClientSessionPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var clientTask = Task.Run(() => {
            var client = new TcpClient();
            client.NoDelay = true; // Disable Nagle's algorithm for testing
            client.Connect(endpoint);
            return client;
        });
            
        Socket serverSocket = listener.AcceptSocket();
        serverSocket.NoDelay = true; // Disable Nagle's algorithm
            
        TcpClient client = await clientTask;
        Socket clientSocket = client.Client;
            
        listener.Stop();
        
        var framing = new CharDelimiterFraming(null, Delimiter, MaxSiteWithoutADelimiter);
        var session = new ClientSession(null, Guid.NewGuid(), serverSocket, framing, BufferSize, _argsPool);

        // Start the session
        var sessionTask = session.StartAsync(_globalCts.Token);
            
        // Short delay to ensure session is ready
        await Task.Delay(10);
            
        return (session, clientSocket, sessionTask);
    }
}