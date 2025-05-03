using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AsyncSocket;
using AsyncSocket.Framing;
using AsyncSocket.Properties;

namespace Tests.ClientSessionTest;

[TestClass]
public class ClientSessionTests
{
    private const int BufferSize = 1024;
    private const int MaxSiteWithoutADelimiter = 1024;
    private const char Delimiter = '\n';
    private Socket? _serverSocket;
    private Socket? _clientSocket;
    private SocketAsyncEventArgsPool? _argsPool;
    private ClientSession<string>? _clientSession;
    private readonly IPEndPoint _endpoint = new (IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new (TimeSpan.FromSeconds(5));

    [TestInitialize]
    public void Setup()
    {
        // Create socket pair
        _serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _serverSocket.Bind(_endpoint);
        _serverSocket.Listen(1);

        _clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _clientSocket.Connect((IPEndPoint)_serverSocket.LocalEndPoint!);
            
        Socket acceptedSocket = _serverSocket.Accept();
            
        // Initialize args pool
        _argsPool = new SocketAsyncEventArgsPool(10);
        
        // Initialize framing
        var framing = new CharDelimiterFraming(null, Delimiter, MaxSiteWithoutADelimiter);

        // Create client session
        _clientSession = new ClientSession<string>(null, Guid.NewGuid(), acceptedSocket, framing, BufferSize, _argsPool);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _clientSession?.StopAsync().Wait();
        _clientSocket?.Close();
        _serverSocket?.Close();
    }
    [TestMethod]
    public async Task SendAsync_ShouldSendMessageToClient()
    {
        // Arrange
        string testMessage = "Hello, world!\n";
        byte[] buffer = new byte[BufferSize];

        // Start the client session and wait for it to be ready
        Debug.Assert(_clientSession != null);
        var clientTask = _clientSession.StartAsync(_cts.Token);
    
        // Act
        await _clientSession.SendAsync(testMessage);
    
        // Use async receive with timeout instead of blocking call
        var receiveTask = Task.Run(() => {
            int bytesRead = _clientSocket!.Receive(buffer);
            return Encoding.UTF8.GetString(buffer, 0, bytesRead);
        }, _cts.Token);
    
        string receivedMessage = await receiveTask;

        // Assert
        Assert.AreEqual(testMessage, receivedMessage);
    }
    [TestMethod]
    public async Task MessageReceived_ShouldTriggerWhenDelimitedMessageIsReceived()
    {
        // Arrange
        string testMessage = "Test message\n";
        var receiveTcs = new TaskCompletionSource<string>();
            
        _clientSession!.MessageReceived += (sender, message) => 
        {
            receiveTcs.TrySetResult(message);
        };

        // Start the client session in the background
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = Task.Run(() => _clientSession.StartAsync(cts.Token));

        // Act
        byte[] messageBytes = Encoding.UTF8.GetBytes(testMessage);
        _clientSocket!.Send(messageBytes);

        // Wait for the message to be processed with timeout
        Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
        Task completedTask = await Task.WhenAny(receiveTcs.Task, timeoutTask);

        // Assert
        Assert.AreNotEqual(timeoutTask, completedTask, "Test timed out waiting for MessageReceived event");
        Assert.AreEqual(testMessage, await receiveTcs.Task);
    }

    [TestMethod]
    public async Task Disconnected_ShouldTriggerWhenClientDisconnects()
    {
        // Arrange
        var disconnectTcs = new TaskCompletionSource<Guid>();
            
        _clientSession!.Disconnected += (sender, id) => 
        {
            disconnectTcs.TrySetResult(id);
        };

        // Start the client session in the background
        var cts = new CancellationTokenSource();
        _ = Task.Run(() => _clientSession.StartAsync(cts.Token));

        // Act
        _clientSocket!.Close(); // Force disconnect

        // Wait for disconnect event with timeout
        Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
        Task completedTask = await Task.WhenAny(disconnectTcs.Task, timeoutTask);

        // Assert
        Assert.AreNotEqual(timeoutTask, completedTask, "Test timed out waiting for Disconnected event");
        Assert.AreEqual(_clientSession.Id, await disconnectTcs.Task);
    }

    [TestMethod]
    public async Task ProcessDelimitedMessages_ShouldHandleMultipleMessagesCorrectly()
    {
        // Arrange
        var messages = new string[] { "Message1\n", "Message2\n", "Message3\n" };
        var receivedMessages = new List<string>();
        var allMessagesTcs = new TaskCompletionSource<bool>();

        Debug.Assert(_clientSession != null);
        _clientSession.MessageReceived += (sender, message) => 
        {
            lock (receivedMessages)
            {
                receivedMessages.Add(message);
                if (receivedMessages.Count == messages.Length)
                    allMessagesTcs.TrySetResult(true);
            }
        };

        // Start the client session in the background
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = Task.Run(() => _clientSession.StartAsync(cts.Token));

        // Act - send all messages at once
        byte[] messageBytes = Encoding.UTF8.GetBytes(string.Concat(messages));
        _clientSocket!.Send(messageBytes);

        // Wait for all messages with timeout
        Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
        Task completedTask = await Task.WhenAny(allMessagesTcs.Task, timeoutTask);

        // Assert
        Assert.AreNotEqual(timeoutTask, completedTask, "Test timed out waiting for all messages");
        Assert.AreEqual(messages.Length, receivedMessages.Count);
        CollectionAssert.AreEqual(messages, receivedMessages);
    }
    
    
    [TestMethod]
    public async Task StopAsync_ShouldCleanupResourcesAndPreventFurtherSends()
    {
        // Arrange
        string testMessage = "Test message\n";
        
        // Start the session
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = Task.Run(() =>
        {
            Debug.Assert(_clientSession != null);
            return _clientSession.StartAsync(cts.Token);
        });
        
        // Give the session time to start
        await Task.Delay(100);
        
        // Act - Stop the session
        Debug.Assert(_clientSession != null);
        await _clientSession.StopAsync();

        await Assert.ThrowsExceptionAsync<ClientException>(async () =>
        {
            // Try to send a message after stopping
            await _clientSession.SendAsync(testMessage);
        });
        
        // Assert - The client socket should receive nothing
        byte[] buffer = new byte[BufferSize];
        Debug.Assert(_clientSocket != null);
        _clientSocket.ReceiveTimeout = 1000; // 1 second timeout
        
        try
        {
            int bytesRead = _clientSocket.Receive(buffer);
            Assert.AreEqual(0, bytesRead, "Should have timed out as no data should be sent after stopping");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            // Expected behavior - the socket timed out waiting for data
        }
    }

    [TestMethod]
    public async Task BufferOverflow_ShouldDisconnectWhenNoDelimiterFound()
    {
        // Arrange
        // Create a message that's larger than the buffer without a delimiter
        string oversizedMessage = new string('A', BufferSize + 100); // No delimiter
        
        var disconnectedTcs = new TaskCompletionSource<Guid>();

        Debug.Assert(_clientSession != null);
        _clientSession.Disconnected += (sender, id) => 
        {
            disconnectedTcs.SetResult(id);
        };

        // Start the session
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = Task.Run(() => _clientSession.StartAsync(cts.Token));
        
        // Give the session time to start
        await Task.Delay(100);
        
        // Act - Send the oversized message
        Debug.Assert(_clientSocket != null);
        _clientSocket.Send(Encoding.UTF8.GetBytes(oversizedMessage));
        
        // Assert - Session should disconnect due to buffer overflow
        Guid disconnectedId = await disconnectedTcs.Task;
        Assert.AreEqual(_clientSession.Id, disconnectedId);
    }
}