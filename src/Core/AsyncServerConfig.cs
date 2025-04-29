namespace AsyncSocket;

public class AsyncServerConfig
{
    public required string IpAddress { get; init; }
    public required int Port { get; init; }
    public int MaxConnections { get; init; } = 1;
    public int BufferSize { get; init; } = 4096;
}