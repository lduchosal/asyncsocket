using System.Diagnostics;
using System.Net.Sockets;
using AsyncSocket;

namespace Tests.SocketAsyncEventArgsPoolTest;

[TestClass]
public class SocketAsyncEventArgsPoolLeakTests
{
    [DataTestMethod]
    [DataRow(1000, 10, 100, 0.75, DisplayName = "Small pool with high return rate")]
    [DataRow(10000, 10, 1000, 0.9, DisplayName = "Medium pool with very high return rate")]
    [DataRow(10000, 20, 500, 0.5, DisplayName = "Medium pool with medium return rate")]
    [DataRow(50000, 5, 10000, 0.25, DisplayName = "Large pool with low return rate")]
    public async Task LeakDetection_VariousWorkloads_ShouldNotLeak(
        int totalOperations, 
        int concurrentThreads, 
        int operationsPerBatch,
        double returnRatio)
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        var weakReferences = new List<WeakReference>();
        var gcCollectionsRequired = 0;
            
        // Act
        await RunPoolOperations(pool, totalOperations, concurrentThreads, operationsPerBatch, returnRatio, weakReferences);
            
        // Force full garbage collection to ensure unreferenced objects are collected
        pool.Dispose();
            
        // Check for leaks by verifying weak references are collected
        while (true)
        {
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            gcCollectionsRequired++;
                
            // Count how many references are still alive
            int aliveCount = weakReferences.Count(wr => wr.IsAlive);
                
            // Output diagnostic information
            Console.WriteLine($"GC collections: {gcCollectionsRequired}, Alive objects: {aliveCount}/{weakReferences.Count}");
                
            // If most objects are collected or we've tried enough times, exit the loop
            if (aliveCount <= weakReferences.Count * 0.05 || gcCollectionsRequired >= 5)
                break;
        }
            
        // Assert
        int remainingAlive = weakReferences.Count(wr => wr.IsAlive);
        double leakPercentage = (double)remainingAlive / weakReferences.Count;
            
        Console.WriteLine($"Leak test completed: {remainingAlive} objects still alive out of {weakReferences.Count} ({leakPercentage:P2})");
        Assert.IsTrue(leakPercentage < 0.05, $"Expected less than 5% leakage, but found {leakPercentage:P2} ({remainingAlive} objects)");
    }
        
    private async Task RunPoolOperations(
        SocketAsyncEventArgsPool pool,
        int totalOperations,
        int concurrentThreads,
        int operationsPerBatch,
        double returnRatio,
        List<WeakReference> weakReferences)
    {
        var random = new Random(42); // Fixed seed for reproducibility
        var tasks = new List<Task>();
        int operationsPerThread = totalOperations / concurrentThreads;
            
        for (int t = 0; t < concurrentThreads; t++)
        {
            tasks.Add(Task.Run(() => {
                // Track objects that aren't returned to the pool
                var nonReturnedArgs = new List<SocketAsyncEventArgs>();
                    
                for (int i = 0; i < operationsPerThread; i += operationsPerBatch)
                {
                    int batchSize = Math.Min(operationsPerBatch, operationsPerThread - i);
                        
                    // Get operation
                    for (int j = 0; j < batchSize; j++)
                    {
                        var args = pool.Get();
                            
                        // Simulate some work with the args
                        SimulateWork(args);
                            
                        if (random.NextDouble() < returnRatio)
                        {
                            // Return to pool
                            pool.Return(args);
                        }
                        else
                        {
                            // Don't return to pool (simulating a "leak" in user code)
                            nonReturnedArgs.Add(args);
                                
                            // Create a weak reference to track if it gets GC'd
                            lock (weakReferences)
                            {
                                weakReferences.Add(new WeakReference(args));
                            }
                        }
                    }
                        
                    // Periodically clear some of our "leaked" objects to simulate a real app
                    // that would eventually drop references to some objects
                    if (nonReturnedArgs.Count > 100)
                    {
                        int toClear = nonReturnedArgs.Count / 2;
                        nonReturnedArgs.RemoveRange(0, toClear);
                    }
                }
                    
                // Clear remaining references to allow GC
                nonReturnedArgs.Clear();
            }));
        }
            
        await Task.WhenAll(tasks);
    }
        
    private void SimulateWork(SocketAsyncEventArgs args)
    {
        // Set some properties or buffers to simulate real usage
        byte[] buffer = new byte[1024];
        args.SetBuffer(buffer, 0, buffer.Length);
            
        // Maybe set some user token
        args.UserToken = Guid.NewGuid();
            
        // Simulate some processing time
        Thread.SpinWait(100);
    }
        
    [DataTestMethod]
    [DataRow(10000, 10, 64, DisplayName = "10K operations with 10 threads")]
    [DataRow(100000, 20, 64, DisplayName = "100K operations with 20 threads")]
    [DataRow(100000, 40, 128, DisplayName = "100K operations with 40 threads")]
    [DataRow(100000, 60, 128, DisplayName = "100K operations with 60 threads")]
    [DataRow(100000, 70, 128, DisplayName = "100K operations with 80 threads")]
    [DataRow(1000000, 40, 512, DisplayName = "1000K operations with 40 threads")]
    public async Task MemoryPressure_UnderLoad_ShouldNotExhaustMemory(int operations, int threads, int maxMemoryMb)
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        var memoryAtStart = GC.GetTotalMemory(true);
        var tasks = new List<Task>();
            
        // Act
        var stopwatch = Stopwatch.StartNew();
            
        for (int t = 0; t < threads; t++)
        {
            tasks.Add(Task.Run(() => {
                var instances = new List<SocketAsyncEventArgs>();
                    
                // Get a lot of instances
                for (int i = 0; i < operations / threads; i++)
                {
                    instances.Add(pool.Get());
                }
                    
                // Return them all
                foreach (var args in instances)
                {
                    pool.Return(args);
                }
            }));
        }
            
        await Task.WhenAll(tasks);
        stopwatch.Stop();
            
        // Force GC to get accurate memory measurements
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        var memoryAtEnd = GC.GetTotalMemory(true);
            
        // Assert
        Console.WriteLine($"Memory at start: {memoryAtStart / 1024 / 1024:F2} MB");
        Console.WriteLine($"Memory at end: {memoryAtEnd / 1024 / 1024:F2} MB");
        Console.WriteLine($"Memory difference: {(memoryAtEnd - memoryAtStart) / 1024 / 1024:F2} MB");
        Console.WriteLine($"Time taken: {stopwatch.ElapsedMilliseconds} ms");
            
        // Verify memory growth is reasonable
        // We can't be exact, but we can ensure it didn't grow by more than, say, 5MB
        // even with a large number of operations
        long maxAllowedGrowth = maxMemoryMb * 1024 * 1024; // 5MB
        Assert.IsTrue(
            memoryAtEnd - memoryAtStart <= maxAllowedGrowth,
            $"Memory growth exceeds threshold: {(memoryAtEnd - memoryAtStart) / 1024 / 1024:F2}MB > {maxAllowedGrowth / 1024 / 1024}MB"
        );
    }
}