using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace AsyncSocket;

public class AsyncTcpServer : IAsyncDisposable
{
    private readonly ILogger<AsyncTcpServer>? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IPEndPoint _endpoint;
    private readonly Socket _listener;
    private readonly ConcurrentDictionary<Guid, ClientSession> _clients = new();
    private readonly SemaphoreSlim _maxConnectionsSemaphore;
    private readonly SocketAsyncEventArgsPool _argsPool = new();
    private readonly int _maxConnection;
    
    private const int BufferSize = 4096;
    
    public AsyncTcpServer(ILogger<AsyncTcpServer>? logger, ILoggerFactory? loggerFactory, string ipAddress, int port, int maxConnections = 1)
    {
        _maxConnection = maxConnections;
        _endpoint = new IPEndPoint(IPAddress.Parse(ipAddress), port);
        _maxConnectionsSemaphore = new SemaphoreSlim(_maxConnection, _maxConnection);
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public AsyncTcpServer(string ipAddress, int port, int maxConnections = 1)
        : this(null, null, ipAddress, port, maxConnections)
    {
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        
        _listener.Bind(_endpoint);
        _listener.Listen(backlog: _maxConnection);

        _logger?.LogDebug("Server started. Listening on {endpoint}", _endpoint);
        _logger?.LogDebug("Max connections {maxConnection}", _maxConnection);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _maxConnectionsSemaphore.WaitAsync(cancellationToken);

                var tcs = new TaskCompletionSource<Socket>(TaskCreationOptions.RunContinuationsAsynchronously);
                var acceptArgs = new SocketAsyncEventArgs();
                acceptArgs.UserToken = tcs;
                acceptArgs.Completed += OnAcceptArgsOnCompleted;

                bool isPending = _listener.AcceptAsync(acceptArgs);
                if (!isPending)
                {
                    OnAcceptArgsOnCompleted(this, acceptArgs);
                }

                // Handle new client in a separate task
                _ = AcceptClientAsync(tcs.Task, cancellationToken);
                
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("Error in server: {ex}", ex);
        }
    }
    
    void OnAcceptArgsOnCompleted(object? s, SocketAsyncEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e.UserToken, nameof(e.UserToken));
        
        var tcs = (TaskCompletionSource<Socket>)e.UserToken;
        if (e.SocketError == SocketError.Success
            && e.AcceptSocket != null)
        {
            tcs.TrySetResult(e.AcceptSocket);
        }
        else
        {
            tcs.TrySetException(new SocketException((int)e.SocketError));
        }
        e.Dispose();
    }

    private async Task AcceptClientAsync(Task<Socket> acceptTask, CancellationToken cancellationToken)
    {
        try
        {
            var clientSocket = await acceptTask;
            var clientId = Guid.NewGuid();
            var client = new ClientSession(_loggerFactory?.CreateLogger<ClientSession>(),clientId, clientSocket, '\n', BufferSize, _argsPool);

            await HandleConnectedAsync(client);

            _clients.TryAdd(clientId, client);
            client.MessageReceived += async (_, message) =>
            {
                await HandleMessageAsync(client, message);
            };
                
            client.Disconnected += async (sender, id) =>
            {
                await HandleDisconnectedAsync(client);

                _clients.TryRemove(id, out _);
                _maxConnectionsSemaphore.Release();
            };
                
            _logger?.LogDebug("Client connected: {remoteEndPoint} (ID: {clientId})", clientSocket.RemoteEndPoint, clientId);
                
            await client.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("Error accepting client: {ex}", ex);
            _maxConnectionsSemaphore.Release();
        }
    }

    private Task HandleConnectedAsync(ClientSession client)
    {
        _logger?.LogDebug("Client Connected {clientId}", client.Id);
        return Task.CompletedTask;
    }
    private Task HandleDisconnectedAsync(ClientSession client)
    {
        _logger?.LogDebug("Client Disconnected {clientId}", client.Id);
        return Task.CompletedTask;
    }
    
    private async Task HandleMessageAsync(ClientSession client, string message)
    {
        _logger?.LogDebug($"Received from {client.Id}: {message}");
            
        // Echo the message back with the delimiter
        string response = $"Server received: {message}";
        await client.SendAsync(response);
    }

    public async ValueTask DisposeAsync()
    {
        _logger?.LogDebug($"DisposeAsync");
        _listener.Close();

        var clientTasks = new List<Task>();
        foreach (var client in _clients.Values)
        {
            clientTasks.Add(client.StopAsync());
        }
        await Task.WhenAll(clientTasks);
        _clients.Clear();

        _maxConnectionsSemaphore.Dispose();
        _argsPool.Dispose();

        _logger?.LogDebug("Server stopped.");
    }
}