using System.Net.Sockets;

namespace AsyncSocket;

public interface ISocketAsyncEventArgsPool
{
    
    public int Count { get; }
    public SocketAsyncEventArgs Get();
    public void Return(SocketAsyncEventArgs item);
}