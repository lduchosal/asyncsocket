using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using AsyncSocket;

namespace Tests.SocketAsyncEventArgsPoolTest;

[TestClass]
public class SocketAsyncEventArgsPoolMultithreadingTests
{
    [TestMethod]
    public async Task ConcurrentGetReturn_MultipleThreads_ShouldNotThrow()
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        const int threadCount = 16;
        const int operationsPerThread = 10000;
        var tasks = new Task[threadCount];
        var exceptions = new List<Exception>();
            
        // Act
        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                try
                {
                    var items = new List<SocketAsyncEventArgs>();
                        
                    // Get a bunch of items
                    for (int j = 0; j < operationsPerThread; j++)
                    {
                        items.Add(pool.Get());
                    }
                        
                    // Return them all
                    foreach (var item in items)
                    {
                        pool.Return(item);
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            });
        }
            
        await Task.WhenAll(tasks);
            
        // Assert
        Assert.AreEqual(0, exceptions.Count, 
            $"Expected no exceptions, but got {exceptions.Count}: {string.Join(", ", exceptions.Select(e => e.Message))}");
    }
        
    [TestMethod]
    public async Task InterleavedGetReturn_MultipleThreads_ShouldBeCorrect()
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        const int threadCount = 8;
        const int iterations = 5000;
        var tasks = new Task[threadCount];
            
        // Pre-populate the pool with some items
        var initialItems = new List<SocketAsyncEventArgs>();
        for (int i = 0; i < 100; i++)
        {
            initialItems.Add(pool.Get());
        }
        foreach (var item in initialItems)
        {
            pool.Return(item);
        }
            
        // Track unique instances to verify we don't get duplicates
        var seenInstances = new HashSet<SocketAsyncEventArgs>();
        var reusedInstances = new HashSet<SocketAsyncEventArgs>();
        var lockObject = new object();
            
        // Act - Multiple threads performing interleaved Get/Return operations
        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                // Each thread maintains its own collection of items
                var threadItems = new List<SocketAsyncEventArgs>();
                var random = new Random(Thread.CurrentThread.ManagedThreadId);
                    
                for (int j = 0; j < iterations; j++)
                {
                    // Random mix of Get and Return operations
                    if (threadItems.Count > 0 && random.Next(2) == 0)
                    {
                        // Return an item
                        int index = random.Next(threadItems.Count);
                        var item = threadItems[index];
                        threadItems.RemoveAt(index);
                        pool.Return(item);
                    }
                    else
                    {
                        // Get an item
                        var item = pool.Get();
                            
                        // Check for duplicates
                        lock (lockObject)
                        {
                            if (!seenInstances.Add(item))
                            {
                                reusedInstances.Add(item);
                            }
                        }
                            
                        threadItems.Add(item);
                    }
                }
                    
                // Return any remaining items
                foreach (var item in threadItems)
                {
                    pool.Return(item);
                }
            });
        }
            
        await Task.WhenAll(tasks);
            
        // Assert
        Assert.IsTrue(reusedInstances.Count > 0, 
            $"Pool reusedInstances instances should be > 0 {reusedInstances.Count} reusedInstances.");
    }
        
    [TestMethod]
    public async Task ContentionTest_ManyConcurrentOperations_ShouldPerformWell()
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        const int concurrentThreads = 32;
        const int warmupOperations = 1000;
        const int measuredOperations = 10000;
            
        // Warm up
        await RunThroughputTest(pool, concurrentThreads, warmupOperations);
            
        // Act - Measure throughput
        var stopwatch = Stopwatch.StartNew();
        var metrics = await RunThroughputTest(pool, concurrentThreads, measuredOperations);
        stopwatch.Stop();
            
        // Calculate metrics
        double totalOperations = concurrentThreads * measuredOperations * 2; // Get + Return
        double operationsPerSecond = totalOperations / stopwatch.Elapsed.TotalSeconds;
            
        // Assert - Output metrics
        Console.WriteLine($"Contention Test Results:");
        Console.WriteLine($"Threads: {concurrentThreads}");
        Console.WriteLine($"Operations per thread: {measuredOperations}");
        Console.WriteLine($"Total operations: {totalOperations}");
        Console.WriteLine($"Total time: {stopwatch.Elapsed.TotalMilliseconds:N0}ms");
        Console.WriteLine($"Operations per second: {operationsPerSecond:N0}");
        Console.WriteLine($"Avg Get time: {metrics.GetTime:N3}ms");
        Console.WriteLine($"P95 Get time: {metrics.P95GetTime:N3}ms");
        Console.WriteLine($"Avg Return time: {metrics.ReturnTime:N3}ms");
        Console.WriteLine($"P95 Return time: {metrics.P95ReturnTime:N3}ms");
            
        // For a ConcurrentStack implementation, throughput should be decent
        Assert.IsTrue(operationsPerSecond > 10000, 
            $"Operation throughput should be at least 10,000 ops/sec, but was {operationsPerSecond:N0}");
    }
        
    [TestMethod]
    public void GetUnderHighContention_ShouldMaintainPerformance()
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        const int iterations = 10000;
        const int threadCount = 4;
            
        // Pre-populate the pool
        var items = new ConcurrentBag<SocketAsyncEventArgs>();
        for (int i = 0; i < 10000; i++)
        {
            items.Add(pool.Get());
        }
            
        // Return all items
        foreach (var item in items)
        {
            pool.Return(item);
        }
        items.Clear();
            
        // Act - All threads get items simultaneously
        var startSignal = new ManualResetEvent(false);
        var threads = new Thread[threadCount];
        var getTimesPerThread = new List<double>[threadCount];
            
        for (int i = 0; i < threadCount; i++)
        {
            getTimesPerThread[i] = new List<double>();
            int threadId = i;
                
            threads[i] = new Thread(() =>
            {
                // Wait for signal to ensure all threads start at once
                startSignal.WaitOne();
                    
                var threadItems = new List<SocketAsyncEventArgs>();
                for (int j = 0; j < iterations; j++)
                {
                    var sw = Stopwatch.StartNew();
                    var args = pool.Get();
                    sw.Stop();
                        
                    getTimesPerThread[threadId].Add(sw.Elapsed.TotalMilliseconds);
                    threadItems.Add(args);
                }
                    
                // Return items (not part of the performance measurement)
                foreach (var item in threadItems)
                {
                    pool.Return(item);
                }
            });
                
            threads[i].Start();
        }
            
        // Start all threads simultaneously
        startSignal.Set();
            
        // Wait for all threads to complete
        foreach (var thread in threads)
        {
            thread.Join();
        }
            
        // Calculate metrics
        var allGetTimes = getTimesPerThread.SelectMany(t => t).ToList();
        var avgGetTime = allGetTimes.Average();
        var p95GetTime = allGetTimes.OrderBy(t => t).ElementAt((int)(allGetTimes.Count * 0.95));
        var maxGetTime = allGetTimes.Max();
            
        // Compare first and last operations to detect degradation
        var firstOpsAvg = allGetTimes.Take(threadCount * 10).Average();
        var lastOpsAvg = allGetTimes.Skip(allGetTimes.Count - threadCount * 10).Average();
            
        // Assert - Output metrics
        Console.WriteLine($"High Contention Get Test Results:");
        Console.WriteLine($"Threads: {threadCount}");
        Console.WriteLine($"Operations per thread: {iterations}");
        Console.WriteLine($"Avg Get time: {avgGetTime:N3}ms");
        Console.WriteLine($"P95 Get time: {p95GetTime:N3}ms");
        Console.WriteLine($"Max Get time: {maxGetTime:N3}ms");
        Console.WriteLine($"First ops avg: {firstOpsAvg:N3}ms");
        Console.WriteLine($"Last ops avg: {lastOpsAvg:N3}ms");
        Console.WriteLine($"Performance ratio (last/first): {lastOpsAvg / firstOpsAvg:N2}x");

        // Performance should remain relatively stable
        Assert.IsTrue(maxGetTime < 20, 
            $"Performance should not degrade significantly over time: {maxGetTime}");
    }
        
    [TestMethod]
    public async Task ProducerConsumerPattern_MultipleThreads_ShouldBeThreadSafe()
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        const int producerThreads = 4;
        const int consumerThreads = 4;
        const int operationsPerProducer = 10000;
            
        var queue = new System.Collections.Concurrent.ConcurrentQueue<SocketAsyncEventArgs>();
        var producerTasks = new Task[producerThreads];
        var consumerTasks = new Task[consumerThreads];
        var allInstances = new HashSet<SocketAsyncEventArgs>();
        var cancellationTokenSource = new CancellationTokenSource();
        var lockObject = new object();
            
        // Act - Producers get items and add to queue
        for (int i = 0; i < producerThreads; i++)
        {
            producerTasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < operationsPerProducer; j++)
                {
                    var args = pool.Get();
                        
                    // Track unique instances
                    lock (lockObject)
                    {
                        allInstances.Add(args);
                    }
                        
                    queue.Enqueue(args);
                        
                    // Add some randomness
                    if (j % 10 == 0)
                    {
                        Task.Delay(1);
                    }
                }
            }, cancellationTokenSource.Token);
        }
            
        // Consumers take items and return to pool
        for (int i = 0; i < consumerThreads; i++)
        {
            consumerTasks[i] = Task.Run(() =>
            {
                while (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    if (queue.TryDequeue(out var args))
                    {
                        // Simulate processing
                        Thread.SpinWait(100);
                            
                        // Return to pool
                        pool.Return(args);
                    }
                    else
                    {
                        // Give producers time to add items
                        Thread.Yield();
                    }
                }
            });
        }
            
        // Wait for producers to finish
        await Task.WhenAll(producerTasks);
            
        // Give consumers time to process remaining items
        while (!queue.IsEmpty)
        {
            await Task.Delay(10);
        }
            
        // Stop consumers
        cancellationTokenSource.Cancel();
        await Task.WhenAll(consumerTasks);
            
        // Assert
        Console.WriteLine($"Producer-Consumer Test Results:");
        Console.WriteLine($"Total unique instances created: {allInstances.Count}");
            
        // Get a final item to verify pool is still working
        var finalItem = pool.Get();
        Assert.IsNotNull(finalItem, "Pool should still be functional after producer-consumer test");
    }
        
    [TestMethod]
    public void ThreadRace_GetAndDispose_ShouldBeThreadSafe()
    {
        // Arrange
        var pool = new SocketAsyncEventArgsPool();
        const int getThreads = 8;
        var getThreadsStarted = 0;
        var disposeCalled = false;
        var exceptions = new List<Exception>();
        var lockObject = new object();
            
        // Act - Start threads that continuously get items
        var barrier = new Barrier(getThreads + 1); // +1 for the main thread
        var threads = new Thread[getThreads];
            
        for (int i = 0; i < getThreads; i++)
        {
            threads[i] = new Thread(() =>
            {
                try
                {
                    // Signal thread is ready
                    Interlocked.Increment(ref getThreadsStarted);
                        
                    // Wait for all threads to be ready
                    barrier.SignalAndWait();
                        
                    // Get items until we're killed or pool is disposed
                    while (!disposeCalled)
                    {
                        try
                        {
                            var args = pool.Get();
                            // Don't return, simulate a leak
                        }
                        catch (ObjectDisposedException)
                        {
                            // Expected after dispose, exit the loop
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (lockObject)
                    {
                        exceptions.Add(ex);
                    }
                }
            });
                
            threads[i].Start();
        }
            
        // Wait for all get threads to start
        while (getThreadsStarted < getThreads)
        {
            Task.Delay(10);
        }
            
        // Wait for all threads to be at the barrier
        barrier.SignalAndWait();
            
        // Let threads run for a bit
        Task.Delay(100);

        // Dispose the pool while threads are getting items
        disposeCalled = true;
        pool.Dispose();
            
        // Wait for all threads to complete
        foreach (var thread in threads)
        {
            thread.Join(1000); // Give each thread 1 second to complete
        }
            
        // Assert
        if (exceptions.Count > 0)
        {
            Console.WriteLine($"Exceptions during thread race test:");
            foreach (var ex in exceptions)
            {
                Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
            }
        }
            
        Assert.IsTrue(exceptions.Count == 0 || 
                      exceptions.All(e => e is ObjectDisposedException), 
            "Only ObjectDisposedException should be thrown during disposal");
    }
        
    // Helper method to run throughput test and collect metrics
    private async Task<PerformanceMetrics> RunThroughputTest(
        SocketAsyncEventArgsPool pool, int threadCount, int operationsPerThread)
    {
        var tasks = new Task<ThreadMetrics>[threadCount];
            
        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                var getTimes = new List<double>(operationsPerThread);
                var returnTimes = new List<double>(operationsPerThread);
                var items = new List<SocketAsyncEventArgs>(operationsPerThread);
                    
                // Get operations
                for (int j = 0; j < operationsPerThread; j++)
                {
                    var sw = Stopwatch.StartNew();
                    var args = pool.Get();
                    sw.Stop();
                        
                    getTimes.Add(sw.Elapsed.TotalMilliseconds);
                    items.Add(args);
                }
                    
                // Return operations
                foreach (var item in items)
                {
                    var sw = Stopwatch.StartNew();
                    pool.Return(item);
                    sw.Stop();
                        
                    returnTimes.Add(sw.Elapsed.TotalMilliseconds);
                }
                    
                return new ThreadMetrics
                {
                    GetTimes = getTimes,
                    ReturnTimes = returnTimes
                };
            });
        }
            
        var results = await Task.WhenAll(tasks);
            
        // Combine all metrics
        var allGetTimes = results.SelectMany(r => r.GetTimes).ToList();
        var allReturnTimes = results.SelectMany(r => r.ReturnTimes).ToList();
            
        return new PerformanceMetrics
        {
            GetTime = allGetTimes.Average(),
            P95GetTime = allGetTimes.OrderBy(t => t).ElementAt((int)(allGetTimes.Count * 0.95)),
            ReturnTime = allReturnTimes.Average(),
            P95ReturnTime = allReturnTimes.OrderBy(t => t).ElementAt((int)(allReturnTimes.Count * 0.95))
        };
    }
        
    // Helper classes for metrics
    private class ThreadMetrics
    {
        public List<double> GetTimes { get; set; }
        public List<double> ReturnTimes { get; set; }
    }
        
    private class PerformanceMetrics
    {
        public double GetTime { get; set; }
        public double P95GetTime { get; set; }
        public double ReturnTime { get; set; }
        public double P95ReturnTime { get; set; }
    }
}