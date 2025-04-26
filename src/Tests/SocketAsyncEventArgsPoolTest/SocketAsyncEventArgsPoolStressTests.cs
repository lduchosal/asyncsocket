using System.Diagnostics;
using System.Net.Sockets;
using AsyncSocket;

namespace Tests.SocketAsyncEventArgsPoolTest;

[TestClass]
public class SocketAsyncEventArgsPoolStressTests
{
    [DataTestMethod]
    [DataRow(20, 1000)]
    [DataRow(20, 10000)]
    [DataRow(20, 100000)]
    [DataRow(20, 1000000)]
    [DataRow(40, 2000)]
    [DataRow(40, 4000)]
    [DataRow(40, 6000)]
    [DataRow(40, 8000)]
    [DataRow(40, 10000)]
    [DataRow(40, 100000)]
    [DataRow(80, 2000)]
    [DataRow(80, 4000)]
    [DataRow(80, 6000)]
    [DataRow(80, 8000)]
    [DataRow(80, 10000)]
    [DataRow(80, 100000)] // 20 seconds
    public async Task ConcurrentGetAndReturn_ShouldHandleMultipleThreads(int numThreads, int operationsPerThread)
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        var tasks = new List<Task>();
        var allArgs = new List<SocketAsyncEventArgs>[numThreads];
            
        for (int i = 0; i < numThreads; i++)
        {
            allArgs[i] = new List<SocketAsyncEventArgs>();
            int threadId = i;
                
            // Act - Each task gets and returns objects from the pool
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < operationsPerThread; j++)
                {
                    var args = pool.Get();
                    allArgs[threadId].Add(args);
                        
                    // Simulate some work
                    if (j % 2 == 0)
                    {
                        pool.Return(args);
                    }
                }
                    
                // Return remaining args
                foreach (var args in allArgs[threadId])
                {
                    pool.Return(args);
                }
            }));
        }
            
        await Task.WhenAll(tasks);
            
        // Assert
        // We can't easily make specific assertions about the pool's internal state,
        // but we can verify no exceptions were thrown
    }
        
    [DataTestMethod]
    [DataRow(1000)]
    [DataRow(10000)]
    [DataRow(100000)]
    public void PerformanceTest_MeasurePoolOverhead(int iterations)
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
            
        // Warm up the pool
        for (int i = 0; i < 1000; i++)
        {
            var args = pool.Get();
            pool.Return(args);
        }
            
        // Act & Assert - Measure time with pool
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            var args = pool.Get();
            pool.Return(args);
        }
        stopwatch.Stop();
        var poolTime = stopwatch.ElapsedMilliseconds;
            
        // Compare with direct instantiation
        stopwatch.Restart();
        for (int i = 0; i < iterations; i++)
        {
            var args = new SocketAsyncEventArgs();
            args.Dispose();
        }
        stopwatch.Stop();
        var directTime = stopwatch.ElapsedMilliseconds;
            
        Console.WriteLine($"Pool time: {poolTime}ms, Direct time: {directTime}ms");
        Assert.IsTrue(poolTime <= directTime, 
            $"Pool should be faster than direct instantiation. Pool: {poolTime}ms, Direct: {directTime}ms");
    }
        
    [DataTestMethod]
    [DataRow(1000)]
    [DataRow(2000)]
    [DataRow(4000)]
    [DataRow(6000)]
    [DataRow(8000)]
    [DataRow(10000)]
    [DataRow(100000)]
    public void MemoryTest_ShouldReuseObjects(int loop)
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        var allCreatedArgs = new HashSet<SocketAsyncEventArgs>();
            
        // Act
        for (int i = 0; i < loop; i++)
        {
            var args = pool.Get();
            allCreatedArgs.Add(args);
            pool.Return(args);
        }
            
        // Assert
        // If the pool is working correctly, we should have created very few objects
        Assert.IsTrue(allCreatedArgs.Count <= 5, 
            $"Expected at most 5 objects created, but got {allCreatedArgs.Count}");
    }
}