using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AsyncSocket;
using AsyncSocket.Framing;

namespace Tests.ClientSessionTest;

[TestClass]
public class ClientSessionScalabilityTests
{
    private const char Delimiter = '\n';
    private const int MaxSiteWithoutADelimiter = 1024;
    private const int BufferSize = 1024;
    private SocketAsyncEventArgsPool _argsPool;
    private CancellationTokenSource _globalCts;

    [TestInitialize]
    public void Setup()
    {
        _argsPool = new SocketAsyncEventArgsPool(500); // Larger pool for scalability tests
        _globalCts = new CancellationTokenSource();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _globalCts.Cancel();
        _globalCts.Dispose();
    }

    [TestMethod]
    [Timeout(60000)] // 60 seconds timeout
    public async Task ScaleTest_MultipleClientSessions()
    {
        // Arrange
        const int numSessions = 100;
        var sessionPairs = new List<(ClientSession<string> session, Socket clientSocket, Task sessionTask)>(numSessions);
        var messenger = new ConcurrentDictionary<Guid, ConcurrentBag<string>>();
            
        // Create multiple client/server socket pairs and sessions
        for (int i = 0; i < numSessions; i++)
        {
            var (session, clientSocket, sessionTask) = await CreateClientSessionPairAsync();
            sessionPairs.Add((session, clientSocket, sessionTask));
                
            var messageList = new ConcurrentBag<string>();
            messenger[session.Id] = messageList;
                
            session.MessageReceived += (sender, message) => 
            {
                if (sender is ClientSession<string> s && messenger.TryGetValue(s.Id, out var bag))
                {
                    bag.Add(message);
                }
            };
        }
            
        // Act - Send messages to all sessions
        const int messagesPerSession = 50;
        var stopwatch = new Stopwatch();
        stopwatch.Start();
            
        var allTasks = new List<Task>(numSessions);
        foreach (var (session, clientSocket, _) in sessionPairs)
        {
            allTasks.Add(Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < messagesPerSession; i++)
                    {
                        string message = $"Message {i} to {session.Id}{Delimiter}";
                        byte[] data = Encoding.UTF8.GetBytes(message);
                        await clientSocket.SendAsync(data, SocketFlags.None);
                            
                        // Send a message back through the session to the client
                        await session.SendAsync($"Reply {i} from {session.Id}{Delimiter}");
                            
                        // Set up client receive for the reply (to avoid deadlock)
                        byte[] buffer = new byte[BufferSize];
                        await clientSocket.ReceiveAsync(buffer, SocketFlags.None);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in session {session.Id}: {ex.Message}");
                }
            }));
        }
            
        // Wait for all operations to complete
        await Task.WhenAll(allTasks);
        stopwatch.Stop();
            
        // Give a bit more time for message processing
        await Task.Delay(1000);
            
        // Calculate metrics
        int totalMessagesSent = numSessions * messagesPerSession;
        int totalMessagesReceived = messenger.Values.Sum(bag => bag.Count);
        double elapsedSeconds = stopwatch.ElapsedMilliseconds / 1000.0;
        double overallThroughput = totalMessagesSent / elapsedSeconds;
            
        // Assert
        Console.WriteLine($"Scale test - Total sessions: {numSessions}");
        Console.WriteLine($"Scale test - Messages sent: {totalMessagesSent}");
        Console.WriteLine($"Scale test - Messages received: {totalMessagesReceived}");
        Console.WriteLine($"Scale test - Elapsed time: {elapsedSeconds:F2} seconds");
        Console.WriteLine($"Scale test - Overall throughput: {overallThroughput:F2} msgs/sec");
        Console.WriteLine($"Scale test - Per-session throughput: {overallThroughput / numSessions:F2} msgs/sec/session");
            
        Assert.IsTrue(totalMessagesReceived > 0, "Should receive messages across sessions");
            
        // Clean up all sessions
        foreach (var (session, clientSocket, sessionTask) in sessionPairs)
        {
            await session.StopAsync();
            clientSocket.Close();
                
            // Wait for session task to complete
            await Task.WhenAny(sessionTask, Task.Delay(500));
        }
    }

    [TestMethod]
    [Ignore("too slow")]
    [Timeout(120000)] // 120 seconds timeout
    public async Task ScaleTest_IncrementalLoad()
    {
        // Arrange
        const int maxSessions = 200;
        const int initialSessions = 10;
        const int sessionIncrement = 10;
        const int messagesPerSession = 100;
        const int messageSize = 1024; // 1 KB messages
            
        var sessionPairs = new List<(ClientSession<string> session, Socket clientSocket, Task sessionTask)>();
        var performanceData = new List<(int sessions, double throughput, double cpuUsage, long memoryUsage)>();
        var rand = new Random();
        string testMessage = new string('X', messageSize - 1) + Delimiter;
            
        // Create initial sessions
        for (int i = 0; i < initialSessions; i++)
        {
            var (session, clientSocket, sessionTask) = await CreateClientSessionPairAsync();
            sessionPairs.Add((session, clientSocket, sessionTask));
        }
            
        // Act - Test with increasing load
        for (int sessionCount = initialSessions; 
             sessionCount <= maxSessions; 
             sessionCount += sessionIncrement)
        {
            // Add more sessions if needed
            while (sessionPairs.Count < sessionCount)
            {
                var (session, clientSocket, sessionTask) = await CreateClientSessionPairAsync();
                sessionPairs.Add((session, clientSocket, sessionTask));
            }
                
            // Get baseline memory and CPU measurements
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long startMemory = GC.GetTotalMemory(true);
                
            var stopwatch = new Stopwatch();
            stopwatch.Start();
                
            // Send messages to all active sessions
            var allTasks = new List<Task>();
            for (int i = 0; i < sessionCount; i++)
            {
                var (session, clientSocket, _) = sessionPairs[i];
                    
                allTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        // Set up receive on client socket to avoid deadlock
                        var receiveTask = ReceiveAllAsync(clientSocket, messagesPerSession);
                            
                        // Send messages through the session
                        for (int j = 0; j < messagesPerSession; j++)
                        {
                            await session.SendAsync(testMessage);
                                
                            // Small random delay to simulate realistic traffic
                            if (rand.Next(10) == 0)
                            {
                                await Task.Delay(rand.Next(1, 5));
                            }
                        }
                            
                        // Wait for client to receive all messages
                        await receiveTask;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error in session {i}: {ex.Message}");
                    }
                }));
            }
                
            // Wait for all operations to complete
            await Task.WhenAll(allTasks);
            stopwatch.Stop();
                
            // Get final memory measurement
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long endMemory = GC.GetTotalMemory(true);
                
            // Calculate metrics
            int totalMessages = sessionCount * messagesPerSession;
            double elapsedSeconds = stopwatch.ElapsedMilliseconds / 1000.0;
            double throughput = totalMessages / elapsedSeconds;
            double cpuUsage = 0; // Would need Process.GetCurrentProcess().TotalProcessorTime in real implementation
            long memoryUsage = endMemory - startMemory;
                
            performanceData.Add((sessionCount, throughput, cpuUsage, memoryUsage));
                
            Console.WriteLine($"Sessions: {sessionCount}, Throughput: {throughput:F2} msgs/sec, " +
                              $"Memory delta: {memoryUsage / (1024 * 1024):F2} MB");
                
            // Optional: Allow system to stabilize between tests
            await Task.Delay(1000);
        }
            
        // Assert
        // Check scalability by comparing throughput across session counts
        var baseline = performanceData.First().throughput;
        var maxLoad = performanceData.Last().throughput;
            
        Console.WriteLine("\nScalability Results:");
        Console.WriteLine($"Baseline throughput ({initialSessions} sessions): {baseline:F2} msgs/sec");
        Console.WriteLine($"Maximum throughput ({maxSessions} sessions): {maxLoad:F2} msgs/sec");
        Console.WriteLine($"Scalability ratio: {maxLoad / baseline:F2}x");
            
        foreach (var data in performanceData)
        {
            Console.WriteLine($"Sessions: {data.sessions}, " +
                              $"Throughput: {data.throughput:F2} msgs/sec, " +
                              $"Memory per session: {data.memoryUsage / data.sessions:F2} bytes");
        }
            
        // Expect at least some scaling - throughput should increase with more sessions
        Assert.IsTrue(maxLoad > baseline * 0.5, 
            "System should maintain at least 50% throughput efficiency under increased load");
            
        // Memory usage per session should not grow significantly as sessions increase
        var memoryPerSessionStart = performanceData.First().memoryUsage / performanceData.First().sessions;
        var memoryPerSessionEnd = performanceData.Last().memoryUsage / performanceData.Last().sessions;
            
        Assert.IsTrue(memoryPerSessionEnd < memoryPerSessionStart * 2, 
            "Memory usage per session should not grow excessively");
            
        // Clean up
        foreach (var (session, clientSocket, sessionTask) in sessionPairs)
        {
            await session.StopAsync();
            clientSocket.Close();
            await Task.WhenAny(sessionTask, Task.Delay(500));
        }
    }

    [TestMethod]
    [Timeout(60000)] // 60 second timeout
    public async Task ScaleTest_ConnectionPoolUtilization()
    {
        // Arrange
        const int poolSize = 200;
        const int maxConcurrentSessions = 150;
        const int operationsPerSession = 50;
            
        var customPool = new SocketAsyncEventArgsPool(poolSize);
        var sessions = new ConcurrentBag<(ClientSession<string> session, Socket clientSocket, Task sessionTask)>();
        var random = new Random();
            
        // Act - Create and use sessions in batches to test pool utilization
        var stopwatch = new Stopwatch();
        stopwatch.Start();
            
        // Track pool usage over time
        var poolUsageData = new ConcurrentBag<(int activeSessionCount, int estimatedPoolUsage, long timestamp)>();
            
        await Task.Run(async () =>
        {
            // Create initial batch of sessions
            var createTasks = new List<Task>();
            for (int i = 0; i < maxConcurrentSessions; i++)
            {
                createTasks.Add(Task.Run(async () =>
                {
                    var pair = await CreateClientSessionPairAsync(customPool);
                    sessions.Add(pair);
                        
                    // Record pool usage
                    poolUsageData.Add((sessions.Count, poolSize - customPool.Count, stopwatch.ElapsedMilliseconds));
                        
                    // Randomly use the session
                    await Task.Delay(random.Next(10, 100));
                    await UseSessionRandomlyAsync(pair.session, pair.clientSocket, random.Next(1, operationsPerSession));
                }));
                    
                // Stagger creation slightly
                if (i % 10 == 0)
                    await Task.Delay(50);
            }
                
            await Task.WhenAll(createTasks);
                
            // Now randomly close some sessions and create new ones to test pool recycling
            for (int cycle = 0; cycle < 3; cycle++)
            {
                var currentSessions = sessions.ToArray();
                var sessionsToRemove = random.Next(maxConcurrentSessions / 4, maxConcurrentSessions / 2);
                    
                // Close some random sessions
                var closeTasks = new List<Task>();
                for (int i = 0; i < sessionsToRemove; i++)
                {
                    int index = random.Next(currentSessions.Length);
                    var (session, clientSocket, _) = currentSessions[index];
                        
                    closeTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await session.StopAsync();
                            clientSocket.Close();
                        }
                        catch { /* Ignore cleanup errors */ }
                    }));
                }
                    
                await Task.WhenAll(closeTasks);
                    
                // Record pool usage after closing
                poolUsageData.Add((sessions.Count - sessionsToRemove, poolSize - customPool.Count, stopwatch.ElapsedMilliseconds));
                    
                // Create new sessions to replace closed ones
                var newCreateTasks = new List<Task>();
                for (int i = 0; i < sessionsToRemove; i++)
                {
                    newCreateTasks.Add(Task.Run(async () =>
                    {
                        var pair = await CreateClientSessionPairAsync(customPool);
                        sessions.Add(pair);
                            
                        // Record pool usage
                        poolUsageData.Add((sessions.Count, poolSize - customPool.Count, stopwatch.ElapsedMilliseconds));
                            
                        // Use the new session
                        await Task.Delay(random.Next(10, 50));
                        await UseSessionRandomlyAsync(pair.session, pair.clientSocket, random.Next(1, operationsPerSession / 2));
                    }));
                        
                    // Stagger creation
                    if (i % 5 == 0)
                        await Task.Delay(20);
                }
                    
                await Task.WhenAll(newCreateTasks);
            }
        });
            
        stopwatch.Stop();
            
        // Analyze pool usage data
        var orderedPoolData = poolUsageData.OrderBy(d => d.timestamp).ToList();
        int maxPoolUsage = orderedPoolData.Max(d => d.estimatedPoolUsage);
        double avgPoolUsage = orderedPoolData.Average(d => d.estimatedPoolUsage);
            
        Console.WriteLine("\nPool Utilization Results:");
        Console.WriteLine($"Pool size: {poolSize}");
        Console.WriteLine($"Max pool usage: {maxPoolUsage} ({(double)maxPoolUsage / poolSize:P2})");
        Console.WriteLine($"Average pool usage: {avgPoolUsage:F2} ({avgPoolUsage / poolSize:P2})");
            
        // Chart pool usage over time (simplified)
        Console.WriteLine("\nPool usage over time:");
        for (int i = 0; i < orderedPoolData.Count; i += orderedPoolData.Count / 20 + 1) // Sample ~20 points
        {
            var data = orderedPoolData[i];
            Console.WriteLine($"Time: {data.timestamp}ms, Sessions: {data.activeSessionCount}, " +
                              $"Pool Usage: {data.estimatedPoolUsage} ({(double)data.estimatedPoolUsage / poolSize:P2})");
        }
            
        // Assert
        Assert.IsTrue(maxPoolUsage <= poolSize, "Pool usage should never exceed pool size");
            
        // Clean up remaining sessions
        var allCurrentSessions = sessions.ToArray();
        var cleanupTasks = new List<Task>();
            
        foreach (var (session, clientSocket, sessionTask) in allCurrentSessions)
        {
            cleanupTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await session.StopAsync();
                    clientSocket.Close();
                    await Task.WhenAny(sessionTask, Task.Delay(500));
                }
                catch { /* Ignore cleanup errors */ }
            }));
        }
            
        await Task.WhenAll(cleanupTasks);
    }

    [TestMethod]
    [Timeout(30000)] // 30 second timeout
    public async Task ScaleTest_ResourceReclamation()
    {
        // Arrange
        const int totalSessions = 100;
        const int batchSize = 20;
        const int operationsPerSession = 20;
            
        long baselineMemory = GC.GetTotalMemory(true);
        var memoryUsageData = new List<(int activeSessions, long memoryUsage)>();
            
        // Act
        for (int batch = 1; batch <= totalSessions / batchSize; batch++)
        {
            // Create a batch of sessions
            var sessionPairs = new List<(ClientSession<string> session, Socket clientSocket, Task sessionTask)>();
            for (int i = 0; i < batchSize; i++)
            {
                var pair = await CreateClientSessionPairAsync();
                sessionPairs.Add(pair);
            }
                
            // Use the sessions
            var useTasks = new List<Task>();
            foreach (var (session, clientSocket, _) in sessionPairs)
            {
                useTasks.Add(UseSessionRandomlyAsync(session, clientSocket, operationsPerSession));
            }
            await Task.WhenAll(useTasks);
                
            // Record memory with all sessions active
            int activeSessions = batch * batchSize;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long currentMemory = GC.GetTotalMemory(true);
            memoryUsageData.Add((activeSessions, currentMemory));
                
            // Close half of the sessions in the batch
            for (int i = 0; i < batchSize / 2; i++)
            {
                var (session, clientSocket, sessionTask) = sessionPairs[i];
                await session.StopAsync();
                clientSocket.Close();
                await Task.WhenAny(sessionTask, Task.Delay(100));
            }
                
            // Record memory with half sessions closed
            activeSessions -= batchSize / 2;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            currentMemory = GC.GetTotalMemory(true);
            memoryUsageData.Add((activeSessions, currentMemory));
                
            // Close the other half
            for (int i = batchSize / 2; i < batchSize; i++)
            {
                var (session, clientSocket, sessionTask) = sessionPairs[i];
                await session.StopAsync();
                clientSocket.Close();
                await Task.WhenAny(sessionTask, Task.Delay(100));
            }
                
            // Optional: Allow system to stabilize
            await Task.Delay(100);
        }
            
        // Record final memory usage
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long finalMemory = GC.GetTotalMemory(true);
        memoryUsageData.Add((0, finalMemory));
            
        // Analyze memory reclamation
        Console.WriteLine("\nResource Reclamation Results:");
        Console.WriteLine($"Baseline Memory: {baselineMemory / 1024:F2} KB");
            
        foreach (var (sessions, memory) in memoryUsageData)
        {
            double memoryDelta = (memory - baselineMemory) / 1024.0;
            Console.WriteLine($"Active Sessions: {sessions}, Memory: {memory / 1024:F2} KB, " +
                              $"Delta: {memoryDelta:F2} KB, Per Session: {(sessions > 0 ? memoryDelta / sessions : 0):F2} KB");
        }
            
        // Assert
        double memoryLeakage = (finalMemory - baselineMemory) / 1024.0;
        Console.WriteLine($"Potential memory leakage: {memoryLeakage:F2} KB");
            
        // We expect memory to be mostly reclaimed
        Assert.IsTrue(memoryLeakage < 1500, $"Memory should be substantially reclaimed after session cleanup {memoryLeakage}");
            
        // Check memory scaling with session count
        var maxMemory = memoryUsageData.Max(d => d.memoryUsage);
        var maxActiveSessions = memoryUsageData.Max(d => d.activeSessions);
            
        if (maxActiveSessions > 0)
        {
            double peakMemoryPerSession = (maxMemory - baselineMemory) / maxActiveSessions;
            Console.WriteLine($"Peak memory per session: {peakMemoryPerSession:F2} bytes");
                
            Assert.IsTrue(peakMemoryPerSession < 100000, 
                "Memory usage per session should be reasonable");
        }
    }

    // Helper method to create a client-server socket pair and client session
    private async Task<(ClientSession<string> session, Socket clientSocket, Task sessionTask)> 
        CreateClientSessionPairAsync(SocketAsyncEventArgsPool customPool = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
            
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var clientTask = Task.Run(() => {
            var client = new TcpClient();
            client.Connect(endpoint);
            return client;
        });
            
        Socket serverSocket = await Task.Run(() => listener.AcceptSocket());
        TcpClient client = await clientTask;
        Socket clientSocket = client.Client;
            
        listener.Stop();
            
        var framing = new CharDelimiterFraming(null, Delimiter, MaxSiteWithoutADelimiter);
        var pool = customPool ?? _argsPool;
        var session = new ClientSession<string>(null, Guid.NewGuid(), serverSocket, framing, BufferSize, pool);

        // Start the session
        var sessionTask = session.StartAsync(_globalCts.Token);
            
        // Short delay to ensure session is ready
        await Task.Delay(10);
            
        return (session, clientSocket, sessionTask);
    }

    // Helper method to use a session for random operations
    private async Task UseSessionRandomlyAsync(ClientSession<string> session, Socket clientSocket, int operations)
    {
        try
        {
            var random = new Random();
            var receiveBuffer = new byte[BufferSize * 2];
                
            for (int i = 0; i < operations; i++)
            {
                // Randomly choose between send and receive
                if (random.Next(2) == 0)
                {
                    // Session sends to client
                    string message = $"Test message {Guid.NewGuid()}{Delimiter}";
                    await session.SendAsync(message);
                        
                    // Client receives
                    await clientSocket.ReceiveAsync(receiveBuffer, SocketFlags.None);
                }
                else
                {
                    // Client sends to session
                    string message = $"Client message {Guid.NewGuid()}{Delimiter}";
                    byte[] data = Encoding.UTF8.GetBytes(message);
                    await clientSocket.SendAsync(data, SocketFlags.None);
                        
                    // Small delay to allow processing
                    await Task.Delay(random.Next(1, 5));
                }
            }
        }
        catch (Exception)
        {
            // Ignore errors during random testing
        }
    }

    // Helper method to receive messages until expected count
    private async Task ReceiveAllAsync(Socket socket, int expectedMessages)
    {
        var buffer = new byte[BufferSize * 2];
        int receivedCount = 0;
        StringBuilder messageBuilder = new StringBuilder();
            
        while (receivedCount < expectedMessages)
        {
            int bytesRead = await socket.ReceiveAsync(buffer, SocketFlags.None);
                
            if (bytesRead == 0)
                break; // Connection closed
                
            string received = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            messageBuilder.Append(received);
                
            // Count complete messages
            int lastPos = 0;
            int pos;
            while ((pos = messageBuilder.ToString().IndexOf(Delimiter, lastPos)) != -1)
            {
                receivedCount++;
                lastPos = pos + 1;
            }
                
            // Remove processed messages
            if (lastPos > 0)
            {
                messageBuilder.Remove(0, lastPos);
            }
                
            // Timeout protection
            if (receivedCount >= expectedMessages)
                break;
        }
    }
}