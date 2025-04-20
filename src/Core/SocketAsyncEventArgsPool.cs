using System.Collections.Concurrent;
using System.Net.Sockets;

namespace AsyncSocket;

public class SocketAsyncEventArgsPool : IDisposable
{
    private readonly ConcurrentStack<SocketAsyncEventArgs> _pool = new();
    private bool _disposed;

    public SocketAsyncEventArgs Get()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pool.TryPop(out var args))
        {
            return args;
        }
        return new SocketAsyncEventArgs();
    }

    public void Return(SocketAsyncEventArgs item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(item, nameof(item));
        
        _pool.Push(item);
    }

    public void Dispose()
    {
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