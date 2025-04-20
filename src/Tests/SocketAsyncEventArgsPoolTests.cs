using System.Net.Sockets;
using AsyncSocket;

namespace Tests;

[TestClass]
public class SocketAsyncEventArgsPoolTests
{
    [TestMethod]
    public void Get_ShouldReturnNewInstance_WhenPoolIsEmpty()
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
            
        // Act
        var args = pool.Get();
            
        // Assert
        Assert.IsNotNull(args);
        Assert.IsInstanceOfType(args, typeof(SocketAsyncEventArgs));
    }
        
    [TestMethod]
    public void Get_ShouldReturnExistingInstance_WhenPoolHasItems()
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        var original = pool.Get();
        pool.Return(original);
            
        // Act
        var retrieved = pool.Get();
            
        // Assert
        Assert.IsNotNull(retrieved);
        Assert.AreSame(original, retrieved);
    }
        
    [TestMethod]
    public void Return_ShouldAddItemToPool()
    {
        // Arrange
        using var pool = new SocketAsyncEventArgsPool();
        var args1 = pool.Get();
        var args2 = pool.Get();
            
        // Act
        pool.Return(args1);
        var retrieved = pool.Get();
            
        // Assert
        Assert.AreSame(args1, retrieved);
    }
        
    [TestMethod]
    public void Dispose_ShouldDisposeAllPooledItems()
    {
        // Arrange
        var pool = new SocketAsyncEventArgsPool();
        var args = new SocketAsyncEventArgs();
        bool disposeCalled = false;
            
        // We need to track when Dispose is called on our args
        // This is a bit tricky as SocketAsyncEventArgs doesn't have a simple way to check if disposed
        // For a real test, you might use a wrapper or a mock
        pool.Return(args);
            
        // Act
        pool.Dispose();
            
        // Assert - We can verify indirectly by trying to use it after disposal
        // which would typically throw an ObjectDisposedException
        try
        {
            args.SetBuffer(new byte[10], 0, 10);
            // If we get here, the object was not disposed
            Assert.Fail("SocketAsyncEventArgs was not disposed");
        }
        catch (ObjectDisposedException)
        {
            // Expected exception
        }
    }
}