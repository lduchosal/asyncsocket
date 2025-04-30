using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace AsyncSocket;

public abstract class AsyncServer<T> : IAsyncDisposable
{
    private readonly ILogger<AsyncServer<T>>? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IMessageFramingFactory<T> _framingFactory;
    
    private readonly IPEndPoint _endpoint;
    private readonly Socket _listener;
    private readonly ConcurrentDictionary<Guid, ClientSession<T>> _clients = new();
    private readonly SemaphoreSlim _maxConnectionsSemaphore;
    private readonly SocketAsyncEventArgsPool _argsPool = new();
    private readonly int _maxConnection;
    private readonly int _bufferSize;

    internal AsyncServer(AsyncServerConfig config, IMessageFramingFactory<T> framingFactory) : this(config, framingFactory, null, null)
    {
    }

    public AsyncServer(AsyncServerConfig config, IMessageFramingFactory<T> framingFactory, ILogger<AsyncServer<T>>? logger, ILoggerFactory? loggerFactory)
    {
        _maxConnection = config.MaxConnections;
        _bufferSize = config.BufferSize;
        _endpoint = new IPEndPoint(IPAddress.Parse(config.IpAddress), config.Port);
        _maxConnectionsSemaphore = new SemaphoreSlim(_maxConnection, _maxConnection);
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, config.ProtocolType);
        _logger = logger;
        _loggerFactory = loggerFactory;
        _framingFactory = framingFactory;
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
            var loggerClient = _loggerFactory?.CreateLogger<ClientSession<T>>();

            var framing = _framingFactory.CreateFraming();
            var client = new ClientSession<T>(loggerClient ,clientId, clientSocket, framing, _bufferSize, _argsPool);

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

    protected abstract Task HandleDisconnectedAsync(ClientSession<T> client);
    protected abstract Task HandleMessageAsync(ClientSession<T> client, T message);
    protected abstract Task HandleConnectedAsync(ClientSession<T> client);

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