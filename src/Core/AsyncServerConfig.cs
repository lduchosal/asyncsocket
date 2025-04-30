using System.Net.Sockets;

namespace AsyncSocket;

public class AsyncServerConfig
{
    public required string IpAddress { get; init; }
    public required int Port { get; init; }
    public ProtocolType ProtocolType { get; init; } = ProtocolType.Tcp;
    public int MaxConnections { get; init; } = 1;
    public int BufferSize { get; init; } = 4096;
}