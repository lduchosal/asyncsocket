using System.Collections.Concurrent;
using System.Net.Sockets;

namespace AsyncSocket;

public class SocketAsyncEventArgsPool : ISocketAsyncEventArgsPool, IDisposable
{
    private readonly ConcurrentStack<SocketAsyncEventArgs> _pool = new();
    private bool _disposed;

    public SocketAsyncEventArgsPool(int warmup = 0)
    {
        for (var i = 0; i < warmup; i++)
        {
            var socket = Get();
            Return(socket);
        }
    }

    public int Count => _pool.Count;

    public SocketAsyncEventArgs Get()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _pool.TryPop(out var args) 
            ? args 
            : new SocketAsyncEventArgs()
            ;
    }
    public void Return(SocketAsyncEventArgs item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(item);
        
        _pool.Push(item);
    }
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        // Cleanup
        if (!disposing)
        {
            return;
        }
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        while (_pool.TryPop(out var args))
        {
            args.Dispose();
        }
    }
}