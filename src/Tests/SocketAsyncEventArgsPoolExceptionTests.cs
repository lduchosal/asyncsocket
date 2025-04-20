using System.Net.Sockets;
using AsyncSocket;

namespace Tests;

[TestClass]
public class SocketAsyncEventArgsPoolExceptionTests
{
    [TestMethod]
    public void Return_DisposedItem_ShouldHandleGracefully()
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        var args = new SocketAsyncEventArgs();
        args.Dispose();
            
        // Act & Assert - Should not throw an exception
        try
        {
            pool.Return(args);
            // Pass if no exception
        }
        catch (Exception ex)
        {
            Assert.Fail($"Returning a disposed item should be handled gracefully, but threw: {ex}");
        }
    }
        
    [TestMethod]
    public void Get_AfterPoolDisposed_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var pool = new SocketAsyncEventArgsPool();
        pool.Dispose();
            
        // Act & Assert
        Assert.ThrowsException<ObjectDisposedException>(() => 
        {
            pool.Get();
        });
    }
        
    [TestMethod]
    public void Return_AfterPoolDisposed_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var pool = new SocketAsyncEventArgsPool();
        var args = new SocketAsyncEventArgs();
        pool.Dispose();
            
        // Act & Assert
        Assert.ThrowsException<ObjectDisposedException>(() => 
        {
            pool.Return(args);
        });
    }
        
    [TestMethod]
    public void Dispose_CalledMultipleTimes_ShouldNotThrow()
    {
        // Arrange
        var pool = new SocketAsyncEventArgsPool();
            
        // Act & Assert
        pool.Dispose();
            
        // Second dispose should not throw
        try
        {
            pool.Dispose();
            // Test passes if no exception is thrown
        }
        catch (Exception ex)
        {
            Assert.Fail($"Second call to Dispose should not throw, but threw: {ex}");
        }
    }
        
    [TestMethod]
    public async Task ConcurrentOperations_WithExceptions_ShouldNotCorruptPool()
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        const int threadCount = 10;
        var tasks = new Task[threadCount];
        var exceptions = new Exception[threadCount];
            
        // Act - Have multiple threads use the pool, some will throw exceptions
        for (int i = 0; i < threadCount; i++)
        {
            int threadId = i;
            tasks[i] = Task.Run(() =>
            {
                try
                {
                    for (int j = 0; j < 100; j++)
                    {
                        var args = pool.Get();
                            
                        // Simulate work that might throw
                        if (j % 10 == 0 && threadId % 2 == 0)
                        {
                            throw new InvalidOperationException("Simulated exception during processing");
                        }
                            
                        // Return the item (only happens if no exception was thrown)
                        pool.Return(args);
                    }
                }
                catch (Exception ex)
                {
                    exceptions[threadId] = ex;
                }
            });
        }
            
        await Task.WhenAll(tasks);
            
        // Assert - Pool should still be usable
        var args1 = pool.Get();
        pool.Return(args1);
        var args2 = pool.Get();
            
        // Should be the same instance if pool is still functioning
        Assert.AreSame(args1, args2, "Pool is corrupted after handling exceptions");
            
        // Verify some exceptions were actually thrown during the test
        Assert.IsTrue(exceptions.Any(e => e != null), 
            "Test should have generated some exceptions to properly test exception handling");
    }
}
    
// Extension to the test class with more advanced tests that would require code changes
public static class SocketAsyncEventArgsPoolExceptionTestExtensions
{
    // Example of how to test that pool items are properly "sanitized" before reuse
    public static void TestItemSanitization(this SocketAsyncEventArgsPool pool)
    {
        // In a real implementation, you'd want to ensure that when items are returned to the pool:
        // 1. Event handlers are removed
        // 2. Buffers are cleared or reset
        // 3. UserToken is cleared
        // 4. Any other state is reset to default values
            
        var args = new SocketAsyncEventArgs();
        args.SetBuffer(new byte[1024], 0, 1024);
        args.UserToken = new object();
        args.Completed += (s, e) => { };
            
        // Return to pool
        pool.Return(args);
            
        // Get it back
        var retrieved = pool.Get();
            
        // Verify it's properly sanitized
        // Assert.IsNull(retrieved.UserToken);
        // Assert.IsNull(retrieved.Buffer);
        // Assert that no event handlers are attached (requires reflection)
    }
}