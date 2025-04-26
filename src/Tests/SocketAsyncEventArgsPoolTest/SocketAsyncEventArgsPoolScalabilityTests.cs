using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using AsyncSocket;

namespace Tests.SocketAsyncEventArgsPoolTest;

[TestClass]
public class SocketAsyncEventArgsPoolScalabilityTests
{
    [DataTestMethod]
    [DataRow(10, 1, DisplayName = "Small pool, single thread")]
    [DataRow(100, 1, DisplayName = "Medium pool, single thread")]
    [DataRow(1000, 1, DisplayName = "Large pool, single thread")]
    [DataRow(10, 4, DisplayName = "Small pool, few threads")]
    [DataRow(100, 8, DisplayName = "Medium pool, moderate threads")]
    [DataRow(1000, 16, DisplayName = "Large pool, many threads")]
    [DataRow(10000, 32, DisplayName = "Very large pool, many threads")]
    public async Task ScaleTest_PoolSizes_ShouldMaintainPerformance(int poolSize, int threadCount)
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        var stopwatch = new Stopwatch();
        var operationsPerThread = poolSize / threadCount;
        var allTimes = new List<double>();
            
        // Pre-populate the pool to desired size
        var initialItems = new List<SocketAsyncEventArgs>();
        for (int i = 0; i < poolSize; i++)
        {
            initialItems.Add(pool.Get());
        }
        foreach (var item in initialItems)
        {
            pool.Return(item);
        }
            
        // Act - Measure performance across different operations
        stopwatch.Start();
            
        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                var threadTimes = new List<double>();
                var items = new List<SocketAsyncEventArgs>();
                    
                for (int i = 0; i < operationsPerThread; i++)
                {
                    var sw = Stopwatch.StartNew();
                    items.Add(pool.Get());
                    sw.Stop();
                    threadTimes.Add(sw.Elapsed.TotalMilliseconds);
                }
                    
                for (int i = 0; i < items.Count; i++)
                {
                    var sw = Stopwatch.StartNew();
                    pool.Return(items[i]);
                    sw.Stop();
                    threadTimes.Add(sw.Elapsed.TotalMilliseconds);
                }
                    
                lock (allTimes)
                {
                    allTimes.AddRange(threadTimes);
                }
            });
        }
            
        await Task.WhenAll(tasks);
        stopwatch.Stop();
            
        // Calculate statistics
        var totalTime = stopwatch.ElapsedMilliseconds;
        var avgOperationTime = allTimes.Average();
        var p95OperationTime = allTimes.OrderBy(t => t).ElementAt((int)(allTimes.Count * 0.95));
        var p99OperationTime = allTimes.OrderBy(t => t).ElementAt((int)(allTimes.Count * 0.99));
        var maxOperationTime = allTimes.Max();
        var opsPerSecond = allTimes.Count / (totalTime / 1000.0);
            
        // Assert - Output metrics
        Console.WriteLine($"Pool Size: {poolSize}, Threads: {threadCount}");
        Console.WriteLine($"Total operations: {allTimes.Count}");
        Console.WriteLine($"Total time: {totalTime}ms");
        Console.WriteLine($"Operations per second: {opsPerSecond:N0}");
        Console.WriteLine($"Avg operation time: {avgOperationTime:N3}ms");
        Console.WriteLine($"P95 operation time: {p95OperationTime:N3}ms");
        Console.WriteLine($"P99 operation time: {p99OperationTime:N3}ms");
        Console.WriteLine($"Max operation time: {maxOperationTime:N3}ms");
            
        // Not making specific assertions as performance will vary by environment,
        // but we can make sure operations don't exceed a reasonable threshold
        Assert.IsTrue(p95OperationTime < 10, 
            $"95% of operations should complete in under 10ms, but took {p95OperationTime:N3}ms");
    }
        
    [TestMethod]
    public async Task ScaleTest_GrowAndShrink_ShouldMaintainPerformance()
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        var stopwatch = new Stopwatch();
            
        // Initialize metrics collection
        long emptyGet = 0;
        int emptyGetCount = 0;

        long fullGet = 0;
        int fullGetCount = 0;
        
        long retur = 0;
        int returCount = 0;
            
        // Act - Test pool under different fill conditions
        stopwatch.Start();
        ConcurrentBag<SocketAsyncEventArgs> collector = new();
        // Phase 1: Empty pool - measure Get performance
        await RunConcurrentOperations(10, 100, () =>
        {
            var sw = Stopwatch.StartNew();
            var args = pool.Get();
            sw.Stop();

            Interlocked.Add(ref emptyGet, (long)sw.Elapsed.TotalMilliseconds) ;
            Interlocked.Increment(ref emptyGetCount) ;
            
            return args;
        }, collector);
            
        // Fill the pool
        var items = new ConcurrentBag<SocketAsyncEventArgs>();
        for (int i = 0; i < 1000; i++)
        {
            items.Add(pool.Get());
        }
        foreach (var item in items)
        {
            pool.Return((SocketAsyncEventArgs)item);
        }
            
        // Phase 2: Full pool - measure Get performance
        items.Clear();
        await RunConcurrentOperations(10, 100, () =>
        {
            var sw = Stopwatch.StartNew();
            var args = pool.Get();
            sw.Stop();
                
            Interlocked.Add(ref fullGet, (long)sw.Elapsed.TotalMilliseconds) ;
            Interlocked.Increment(ref fullGetCount) ;

            return args;
        }, items);
            
        // Phase 3: Return performance
        await RunConcurrentOperations(10, 100, () =>
        {
            items.TryPeek(out var args);
            items.TakeLast(1);
                
            var sw = Stopwatch.StartNew();
            pool.Return(args);
            sw.Stop();
                
            Interlocked.Add(ref retur, (long)sw.Elapsed.TotalMilliseconds) ;
            Interlocked.Increment(ref returCount) ;

            return null;
        }, collector);
            
        stopwatch.Stop();
            
        // Compute and display metrics
        Console.WriteLine("Performance across pool states:");
        Dictionary<string, (long Value, int Count)> list = new ()
        {
            { "EmptyGet", (Value: emptyGet, Count: emptyGetCount) },
            { "FullGet", (Value: fullGet, Count: fullGetCount) },
            { "Return", (Value: retur, Count: returCount) },
        };
        foreach (var metric in list)
        {
            if (metric.Value.Value == 0) continue;
                
            Console.WriteLine($"{metric.Key}:");
            Console.WriteLine($"  Count: {metric.Value.Value}");
            Console.WriteLine($"  Avg: {metric.Value.Value / metric.Value.Count:N3}ms");
        }
            
        // Assert - Verify full pool Get is faster than empty pool Get
        var emptyGetAvg = list["EmptyGet"].Value / list["EmptyGet"].Count ;
        var fullGetAvg = list["FullGet"].Value / list["FullGet"].Count ;
            
        Console.WriteLine($"Empty/Full performance ratio: {emptyGetAvg / (fullGetAvg+0.001):N2}x");
        Assert.IsTrue(fullGetAvg <= emptyGetAvg, 
            $"Getting from a populated pool should be faster than an empty pool, but got Empty: {emptyGetAvg:N3}ms, Full: {fullGetAvg:N3}ms");
    }
        
    [TestMethod]
    public async Task ScaleTest_BurstyTraffic_ShouldHandleEfficiently()
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        const int burstSize = 1000;
        const int numBursts = 5;
        const int cooldownMs = 500;
            
        // Act - Simulate bursty traffic patterns
        var allGetTimes = new List<double>();
        var allReturnTimes = new List<double>();
            
        for (int burst = 0; burst < numBursts; burst++)
        {
            Console.WriteLine($"Starting burst {burst + 1}/{numBursts}");
                
            // Create a burst of Get operations
            var items = new List<SocketAsyncEventArgs>();
            var tasks = new Task[10];
                
            for (int t = 0; t < tasks.Length; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    var threadItems = new List<SocketAsyncEventArgs>();
                    var threadGetTimes = new List<double>();
                        
                    for (int i = 0; i < burstSize / tasks.Length; i++)
                    {
                        var sw = Stopwatch.StartNew();
                        var args = pool.Get();
                        sw.Stop();
                            
                        threadGetTimes.Add(sw.Elapsed.TotalMilliseconds);
                        threadItems.Add(args);
                    }
                        
                    lock (items)
                    {
                        items.AddRange(threadItems);
                    }
                        
                    lock (allGetTimes)
                    {
                        allGetTimes.AddRange(threadGetTimes);
                    }
                });
            }
                
            await Task.WhenAll(tasks);
                
            // Cooldown period
            await Task.Delay(cooldownMs);
                
            // Return all items
            for (int t = 0; t < tasks.Length; t++)
            {
                int threadIndex = t;
                tasks[t] = Task.Run(() =>
                {
                    var startIdx = (burstSize / tasks.Length) * threadIndex;
                    var endIdx = Math.Min(startIdx + (burstSize / tasks.Length), items.Count);
                    var threadReturnTimes = new List<double>();
                        
                    for (int i = startIdx; i < endIdx; i++)
                    {
                        var sw = Stopwatch.StartNew();
                        pool.Return(items[i]);
                        sw.Stop();
                            
                        threadReturnTimes.Add(sw.Elapsed.TotalMilliseconds);
                    }
                        
                    lock (allReturnTimes)
                    {
                        allReturnTimes.AddRange(threadReturnTimes);
                    }
                });
            }
                
            await Task.WhenAll(tasks);
                
            // Cooldown before next burst
            await Task.Delay(cooldownMs);
        }
            
        // Calculate statistics
        var avgGetTime = allGetTimes.Average();
        var p95GetTime = allGetTimes.OrderBy(t => t).ElementAt((int)(allGetTimes.Count * 0.95));
        var maxGetTime = allGetTimes.Max();
            
        var avgReturnTime = allReturnTimes.Average();
        var p95ReturnTime = allReturnTimes.OrderBy(t => t).ElementAt((int)(allReturnTimes.Count * 0.95));
        var maxReturnTime = allReturnTimes.Max();
            
        // Analyze performance across bursts
        var getTimesByBurst = new List<double>[numBursts];
        var returnTimesByBurst = new List<double>[numBursts];
            
        for (int i = 0; i < numBursts; i++)
        {
            getTimesByBurst[i] = new List<double>();
            returnTimesByBurst[i] = new List<double>();
                
            int startIdx = i * burstSize;
            int endIdx = Math.Min(startIdx + burstSize, allGetTimes.Count);
                
            for (int j = startIdx; j < endIdx; j++)
            {
                if (j < allGetTimes.Count)
                    getTimesByBurst[i].Add(allGetTimes[j]);
                    
                if (j < allReturnTimes.Count)
                    returnTimesByBurst[i].Add(allReturnTimes[j]);
            }
        }
            
        // Assert - Output metrics
        Console.WriteLine("Bursty Traffic Performance:");
        Console.WriteLine($"Total Get operations: {allGetTimes.Count}");
        Console.WriteLine($"Avg Get time: {avgGetTime:N3}ms");
        Console.WriteLine($"P95 Get time: {p95GetTime:N3}ms");
        Console.WriteLine($"Max Get time: {maxGetTime:N3}ms");
        Console.WriteLine();
        Console.WriteLine($"Total Return operations: {allReturnTimes.Count}");
        Console.WriteLine($"Avg Return time: {avgReturnTime:N3}ms");
        Console.WriteLine($"P95 Return time: {p95ReturnTime:N3}ms");
        Console.WriteLine($"Max Return time: {maxReturnTime:N3}ms");
            
        // Analyze performance consistency across bursts
        Console.WriteLine("\nPerformance by burst:");
        for (int i = 0; i < numBursts; i++)
        {
            if (getTimesByBurst[i].Count == 0) continue;
                
            Console.WriteLine($"Burst {i+1}:");
            Console.WriteLine($"  Get Avg: {getTimesByBurst[i].Average():N3}ms");
            Console.WriteLine($"  Return Avg: {returnTimesByBurst[i].Average():N3}ms");
        }
            
        // Verify performance is consistent across bursts
        var firstBurstGetAvg = getTimesByBurst[0].Average();
        var lastBurstGetAvg = getTimesByBurst[numBursts - 1].Average();
            
        Assert.IsTrue(lastBurstGetAvg / firstBurstGetAvg < 3, 
            $"Performance should remain consistent across bursts, but degraded significantly: " +
            $"First burst: {firstBurstGetAvg:N3}ms, Last burst: {lastBurstGetAvg:N3}ms");
    }
        
    [TestMethod]
    public void ScaleTest_UnderMemoryPressure_ShouldRemainResponsive()
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        var baseline = MeasureOperationTime(pool, 1000);
            
        // Generate memory pressure by allocating large amounts of memory
        var memoryPressure = new List<byte[]>();
        try
        {
            // Allocate ~500MB to create GC pressure
            for (int i = 0; i < 100; i++)
            {
                memoryPressure.Add(new byte[5 * 1024 * 1024]);
            }
                
            // Act - Measure performance under memory pressure
            var underPressure = MeasureOperationTime(pool, 1000);
                
            // Assert
            Console.WriteLine("Memory Pressure Test Results:");
            Console.WriteLine($"Baseline Get: {baseline.GetTime:N3}ms");
            Console.WriteLine($"Baseline Return: {baseline.ReturnTime:N3}ms");
            Console.WriteLine($"Under Pressure Get: {underPressure.GetTime:N3}ms");
            Console.WriteLine($"Under Pressure Return: {underPressure.ReturnTime:N3}ms");
            Console.WriteLine($"Get Performance Ratio: {underPressure.GetTime / baseline.GetTime:N2}x");
            Console.WriteLine($"Return Performance Ratio: {underPressure.ReturnTime / baseline.ReturnTime:N2}x");
                
            // Performance may degrade under pressure, but shouldn't be catastrophic
            Assert.IsTrue(underPressure.GetTime / baseline.GetTime < 10, 
                "Performance under memory pressure shouldn't degrade by more than 10x");
        }
        finally
        {
            // Clean up
            memoryPressure.Clear();
            GC.Collect();
        }
    }
        
    // Helper method to run concurrent operations
    private async Task RunConcurrentOperations(int threadCount, int opsPerThread, 
        Func<SocketAsyncEventArgs> operation, ConcurrentBag<SocketAsyncEventArgs> collector)
    {
        var tasks = new Task[threadCount];
            
        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                var threadItems = new List<SocketAsyncEventArgs>();
                    
                for (int i = 0; i < opsPerThread; i++)
                {
                    var args = operation();
                    threadItems.Add(args);
                }
                    
                lock (collector)
                {
                    foreach (var threadItem in threadItems)
                    {
                        collector.Add(threadItem);
                    }
                }
            });
        }
            
        await Task.WhenAll(tasks);
    }
        
    // Helper struct to track operation metrics
    private struct OperationMetrics
    {
        public double GetTime;
        public double ReturnTime;
    }
        
    // Helper method to measure baseline operation times
    private OperationMetrics MeasureOperationTime(SocketAsyncEventArgsPool pool, int operations)
    {
        var getTimes = new List<double>();
        var returnTimes = new List<double>();
        var items = new List<SocketAsyncEventArgs>();
            
        // Measure Get operations
        for (int i = 0; i < operations; i++)
        {
            var sw = Stopwatch.StartNew();
            var args = pool.Get();
            sw.Stop();
                
            getTimes.Add(sw.Elapsed.TotalMilliseconds);
            items.Add(args);
        }
            
        // Measure Return operations
        foreach (var item in items)
        {
            var sw = Stopwatch.StartNew();
            pool.Return(item);
            sw.Stop();
                
            returnTimes.Add(sw.Elapsed.TotalMilliseconds);
        }
            
        return new OperationMetrics
        {
            GetTime = getTimes.Average(),
            ReturnTime = returnTimes.Average()
        };
    }
}