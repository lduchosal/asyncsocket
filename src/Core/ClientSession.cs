using System.Net.Sockets;
using System.Text;
using AsyncSocket.Properties;
using Microsoft.Extensions.Logging;

namespace AsyncSocket;

public class ClientSession
{
    public Guid Id { get; }
    private readonly ILogger<ClientSession>? _logger;
    private readonly Socket _socket;
    private readonly char _delimiter;
    private readonly byte[] _receiveBuffer;
    private readonly ISocketAsyncEventArgsPool _argsPool;
    private readonly StringBuilder _stringBuffer;
    private CancellationTokenSource Cts { get; set; } = new();

    private bool IsRunning { get; set; }

    private readonly int _maxBufferSizeWithoutDelimiter;

    public event EventHandler<string> MessageReceived = delegate { };
    public event EventHandler<Guid> Disconnected = delegate { };

    public ClientSession(Guid id, Socket socket, char delimiter, int bufferSize, ISocketAsyncEventArgsPool argsPool)
    :this(null, id, socket, delimiter,bufferSize,argsPool)
    {
        
    }

    public ClientSession(ILogger<ClientSession>? logger, Guid id, Socket socket, char delimiter, int bufferSize, ISocketAsyncEventArgsPool argsPool)
    {
        Id = id;
        _socket = socket;
        _delimiter = delimiter;
        _receiveBuffer = new byte[bufferSize];
        _argsPool = argsPool;
        _maxBufferSizeWithoutDelimiter = bufferSize; // Max size allowed without finding a delimiter
        _stringBuffer = new();
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IsRunning = true;
        _logger?.LogDebug("Client StartAsync {Id}: {isRunning}", Id, IsRunning);
        
        Cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Cts.Token.Register(() => _ = StopAsync());

        try
        {
            await ReceiveLoopAsync(Cts.Token);
        }
        catch (OperationCanceledException e)
        {
            _logger?.LogDebug("Operation Canceled {Id}: {exception}", Id, e);
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("Error in client {Id}: {exception}", Id, ex);
        }
        finally
        {
            await StopAsync();
        }
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            _logger?.LogDebug("StopAsync non running client {Id}: {isRunning}", Id, IsRunning);
            return;
        }
            
        IsRunning = false;
        await Cts.CancelAsync();

        try
        {
            await _socket.DisconnectAsync(true);
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception e)
        {
            _logger?.LogDebug("DisconnectAsync and shutdown {e}", e);
        }
        _socket.Close();
        _socket.Dispose();
            
        Disconnected.Invoke(this, Id);
            
        await Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var args = _argsPool.Get();
        args.SetBuffer(_receiveBuffer, 0, _receiveBuffer.Length);
            
        try
        {
            while (IsRunning && !cancellationToken.IsCancellationRequested)
            {
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

                args.UserToken = tcs;
                args.Completed += OnReceiveCompleted;
                    
                bool isPending = _socket.ReceiveAsync(args);
                if (!isPending)
                {
                    OnReceiveCompleted(_socket, args);
                }
                    
                int bytesRead = await tcs.Task;
                if (bytesRead == 0)
                {
                    // Connection closed gracefully
                    break;
                }
                    
                string receivedText = Encoding.UTF8.GetString(_receiveBuffer, 0, bytesRead);
                _stringBuffer.Append(receivedText);
                    
                // Check if buffer exceeds limit without finding a delimiter
                if (_stringBuffer.Length > _maxBufferSizeWithoutDelimiter && 
                    _stringBuffer.ToString().IndexOf(_delimiter) == -1)
                {
                    _logger?.LogDebug("Client {Id}: Buffer exceeded maximum size without delimiter. Disconnecting.", Id);
                    break;  // This will trigger StopAsync() in the finally block
                }
                    
                await ProcessDelimitedMessagesAsync();
                    
                args.Completed -= OnReceiveCompleted;
            }
        }
        finally
        {
            args.Completed -= OnReceiveCompleted;
            _argsPool.Return(args);
        }
    }

    private void OnReceiveCompleted(object? sender, SocketAsyncEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e.UserToken, nameof(e.UserToken));
        
        var tcs = (TaskCompletionSource<int>)e.UserToken;
        if (e.SocketError == SocketError.Success)
        {
            tcs.TrySetResult(e.BytesTransferred);
        }
        else
        {
            tcs.TrySetException(new SocketException((int)e.SocketError));
        }
    }

    private async Task ProcessDelimitedMessagesAsync()
    {
        int delimiterPos;
        while ((delimiterPos = _stringBuffer.ToString().IndexOf(_delimiter)) != -1)
        {
            // Extract the message up to the delimiter
            string message = _stringBuffer.ToString(0, delimiterPos + 1);
                
            // Remove the processed message and delimiter from the buffer
            _stringBuffer.Remove(0, delimiterPos + 1);
                
            // Raise event with the message
            MessageReceived.Invoke(this, message);
                
            // Allow event handler to complete
            await Task.Yield();
        }
    }

    public async Task SendAsync(string message)
    {
        ClientException.ThrowIf(!IsRunning, "not running");
            
        byte[] data = Encoding.UTF8.GetBytes(message);
        var args = _argsPool.Get();
        args.SetBuffer(data, 0, data.Length);
            
        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            args.Completed += OnSendCompleted;
            args.UserToken = tcs;
                
            bool isPending = _socket.SendAsync(args);
            if (!isPending)
            {
                OnSendCompleted(_socket, args);
            }
                
            await tcs.Task;
        }
        finally
        {
            args.Completed -= OnSendCompleted;
            _argsPool.Return(args);
        }
    }

    private void OnSendCompleted(object? _, SocketAsyncEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e.UserToken, nameof(e.UserToken));
        
        var tcs = (TaskCompletionSource<bool>)e.UserToken;
            
        if (e.SocketError == SocketError.Success)
        {
            tcs.TrySetResult(true);
        }
        else
        {
            tcs.TrySetException(new SocketException((int)e.SocketError));
        }
    }
}