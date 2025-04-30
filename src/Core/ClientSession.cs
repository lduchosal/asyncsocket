using System.Net.Sockets;
using System.Text;
using AsyncSocket.Framing;
using AsyncSocket.Properties;
using Microsoft.Extensions.Logging;

namespace AsyncSocket;

public class ClientSession<T>(
    ILogger<ClientSession<T>>? logger,
    Guid id,
    Socket socket,
    IMessageFraming<T> messageFraming,
    int bufferSize,
    ISocketAsyncEventArgsPool argsPool)
{
    public Guid Id { get; } = id;
    private readonly byte[] _receiveBuffer = new byte[bufferSize];
    private CancellationTokenSource Cts { get; set; } = new();

    private bool IsRunning { get; set; }


    public event EventHandler<T> MessageReceived = delegate { };
    public event EventHandler<Guid> Disconnected = delegate { };

    public ClientSession(Guid id, Socket socket, IMessageFraming<T> messageFraming, int bufferSize, ISocketAsyncEventArgsPool argsPool)
    :this(null, id, socket, messageFraming, bufferSize, argsPool)
    {
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IsRunning = true;
        logger?.LogDebug("Client StartAsync {Id}: {isRunning}", Id, IsRunning);
        
        Cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Cts.Token.Register(() => _ = StopAsync());

        try
        {
            await ReceiveLoopAsync(Cts.Token);
        }
        catch (OperationCanceledException e)
        {
            logger?.LogDebug("Operation Canceled {Id}: {exception}", Id, e);
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            logger?.LogDebug("Error in client {Id}: {exception}", Id, ex);
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
            logger?.LogDebug("StopAsync non running client {Id}: {isRunning}", Id, IsRunning);
            return;
        }
            
        IsRunning = false;
        await Cts.CancelAsync();

        try
        {
            await socket.DisconnectAsync(true);
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception e)
        {
            logger?.LogDebug("DisconnectAsync and shutdown {e}", e);
        }
        socket.Close();
        socket.Dispose();
            
        Disconnected.Invoke(this, Id);
            
        await Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var args = argsPool.Get();
        args.SetBuffer(_receiveBuffer, 0, _receiveBuffer.Length);
            
        try
        {
            while (IsRunning && !cancellationToken.IsCancellationRequested)
            {
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

                args.UserToken = tcs;
                args.Completed += OnReceiveCompleted;
                    
                bool isPending = socket.ReceiveAsync(args);
                if (!isPending)
                {
                    OnReceiveCompleted(socket, args);
                }
                    
                int bytesRead = await tcs.Task;
                if (bytesRead == 0)
                {
                    // Connection closed gracefully
                    break;
                }

                bool processSucceed = messageFraming.Process(_receiveBuffer, bytesRead);
                // Check if buffer exceeds limit without finding a framing delimiter
                if (!processSucceed)
                {
                    logger?.LogDebug("Client {Id}: Buffer exceeded maximum size without delimiter. Disconnecting.", Id);
                    break;  // This will trigger StopAsync() in the finally block
                }
                    
                await ProcessDelimitedMessagesAsync();
                    
                args.Completed -= OnReceiveCompleted;
            }
        }
        finally
        {
            args.Completed -= OnReceiveCompleted;
            argsPool.Return(args);
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
        T? message;
        while ((message = messageFraming.Next()) != null) 
        {
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
        var args = argsPool.Get();
        args.SetBuffer(data, 0, data.Length);
            
        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            args.Completed += OnSendCompleted;
            args.UserToken = tcs;
                
            bool isPending = socket.SendAsync(args);
            if (!isPending)
            {
                OnSendCompleted(socket, args);
            }
                
            await tcs.Task;
        }
        finally
        {
            args.Completed -= OnSendCompleted;
            argsPool.Return(args);
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