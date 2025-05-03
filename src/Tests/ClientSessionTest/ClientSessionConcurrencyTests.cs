using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AsyncSocket;
using AsyncSocket.Framing;

namespace Tests.ClientSessionTest
{
    [TestClass]
    public class ClientSessionConcurrencyTests
    {
        private const char Delimiter = '\n';
        private const int MaxSiteWithoutADelimiter = 1024;
        private const int BufferSize = 1024;
        private Socket ServerSocket { get; set; } = null!;
        private Socket ClientSocket { get; set; } = null!;
        private SocketAsyncEventArgsPool ArgsPool { get; set; } = null!;
        private ClientSession<string>  ClientSession { get; set; } = null!;
        private readonly CancellationTokenSource _cts = new (TimeSpan.FromSeconds(5));
        private readonly IPEndPoint _endpoint = new (IPAddress.Loopback, 0);
        private CharDelimiterFraming? Framing { get; set; } = null!;

        [TestInitialize]
        public void Setup()
        {
            // Create socket pair
            ServerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            ServerSocket.Bind(_endpoint);
            ServerSocket.Listen(1);

            ClientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            ClientSocket.Connect((IPEndPoint)ServerSocket.LocalEndPoint!);
            
            Socket acceptedSocket = ServerSocket.Accept();
            
            // Initialize args pool
            ArgsPool = new SocketAsyncEventArgsPool(10);

            // Initialize framing
            Framing = new CharDelimiterFraming(null, Delimiter, MaxSiteWithoutADelimiter);

            // Create client session
            ClientSession = new ClientSession<string>(null, Guid.NewGuid(), acceptedSocket, Framing, BufferSize, ArgsPool);
        }



        [TestCleanup]
        public void Cleanup()
        {
            _cts.Cancel();
            _cts.Dispose();
            
            ServerSocket?.Close();
            ClientSocket?.Close();
        }

        [TestMethod]
        public async Task ConcurrentSends_ShouldCompleteSuccessfully()
        {
            // Arrange
            var sessionTask = ClientSession.StartAsync(_cts.Token);
            
            // Allow session to start
            await Task.Delay(100);
            
            const int concurrentSends = 10;
            var sendTasks = new Task[concurrentSends];
            var receivedMessages = new ConcurrentBag<string>();
            
            // Prepare client to receive messages
            var receiveBuffer = new byte[BufferSize];
            var receiveTask = ReceiveMessagesAsync(ClientSocket, receiveBuffer, concurrentSends, receivedMessages);
            
            // Act
            for (int i = 0; i < concurrentSends; i++)
            {
                sendTasks[i] = ClientSession.SendAsync($"Message {i}{Delimiter}");
            }
            
            // Wait for all sends to complete
            await Task.WhenAll(sendTasks);
            
            // Wait for receives to complete (with timeout)
            var receiveCompletedTask = await Task.WhenAny(receiveTask, Task.Delay(5000));
            
            // Assert
            Assert.AreEqual(receiveTask, receiveCompletedTask, "Receive task should complete before timeout");
            Assert.AreEqual(concurrentSends, receivedMessages.Count, "All messages should be received");
            
            // Cleanup
            await ClientSession.StopAsync();
            await Task.WhenAny(sessionTask, Task.Delay(1000));
        }

        [TestMethod]
        public async Task ConcurrentReceives_ShouldTriggerMessageReceivedEvents()
        {
            // Arrange
            var receivedMessages = new ConcurrentBag<string>();
            Debug.Assert(ClientSession != null, nameof(ClientSession) + " != null");
            ClientSession.MessageReceived += (sender, message) =>
            {
                receivedMessages.Add(message);
            };
            
            var sessionTask = ClientSession.StartAsync(_cts.Token);
            
            // Allow session to start
            await Task.Delay(100);
            
            const int concurrentReceives = 10;
            var sendTasks = new Task[concurrentReceives];
            
            // Act
            for (int i = 0; i < concurrentReceives; i++)
            {
                int messageNum = i;
                sendTasks[i] = Task.Run(async () =>
                {
                    byte[] data = Encoding.UTF8.GetBytes($"Message {messageNum}{Delimiter}");
                    await ClientSocket.SendAsync(data, SocketFlags.None);
                    
                    // Small delay to simulate concurrent but not exactly simultaneous sends
                    await Task.Delay(10);
                });
            }
            
            // Wait for all sends to complete
            await Task.WhenAll(sendTasks);
            
            // Wait for all message received events (with timeout)
            int attempts = 0;
            while (receivedMessages.Count < concurrentReceives && attempts < 50)
            {
                await Task.Delay(100);
                attempts++;
            }
            
            // Assert
            Assert.AreEqual(concurrentReceives, receivedMessages.Count, 
                "All messages should trigger MessageReceived events");
            
            // Cleanup
            await ClientSession.StopAsync();
            await Task.WhenAny(sessionTask, Task.Delay(1000));
        }

        [TestMethod]
        [Ignore]
        [TestCategory("FailOnGitHub")]
        public async Task ConcurrentSendsAndStopSession_ShouldHandleGracefully()
        {
            // Arrange
            Debug.Assert(ClientSession != null, nameof(ClientSession) + " != null");
            var sessionTask = ClientSession.StartAsync(_cts.Token);
            
            // Allow session to start
            await Task.Delay(100);
            
            const int concurrentSends = 5;
            var sendTasks = new Task[concurrentSends];
            
            // Act
            for (int i = 0; i < concurrentSends; i++)
            {
                sendTasks[i] = ClientSession.SendAsync($"Message {i}{Delimiter}");
            }
            
            // Stop the session while sends are in progress
            var stopTask = ClientSession.StopAsync();
            
            // Assert
            await Task.WhenAny(stopTask, Task.Delay(2000));
            Assert.IsTrue(stopTask.IsCompleted, "StopAsync should complete despite concurrent sends");
            
            // At this point, some sends might have completed, others might have thrown
            // We're testing that the process doesn't deadlock or crash
            
            // Wait for session task to complete
            await Task.WhenAny(sessionTask, Task.Delay(1000));
        }

        [TestMethod]
        public async Task MultipleConcurrentReceivesWithPartialDelimiters_ShouldAssembleCorrectly()
        {
            // Arrange
            var receivedMessages = new ConcurrentBag<string>();
            Debug.Assert(ClientSession != null, nameof(ClientSession) + " != null");
            ClientSession.MessageReceived += (sender, message) =>
            {
                receivedMessages.Add(message);
            };
            
            var sessionTask = ClientSession.StartAsync(_cts.Token);
            
            // Allow session to start
            await Task.Delay(100);
            
            // ACT: Send partial messages that will need to be assembled
            Debug.Assert(ClientSocket != null, nameof(ClientSocket) + " != null");
            await ClientSocket.SendAsync(Encoding.UTF8.GetBytes("First half of message"), SocketFlags.None);
            await Task.Delay(50); // Small delay to test buffering
            await ClientSocket.SendAsync(Encoding.UTF8.GetBytes($" and second half{Delimiter}"), SocketFlags.None);
            
            // Send another split message
            await ClientSocket.SendAsync(Encoding.UTF8.GetBytes("Another "), SocketFlags.None);
            await ClientSocket.SendAsync(Encoding.UTF8.GetBytes("split "), SocketFlags.None);
            await ClientSocket.SendAsync(Encoding.UTF8.GetBytes($"message{Delimiter}"), SocketFlags.None);
            
            // Wait for message processing
            await Task.Delay(500);
            
            // ASSERT
            Assert.AreEqual(2, receivedMessages.Count, "Should receive exactly two complete messages");
            
            bool foundFirstMessage = false;
            bool foundSecondMessage = false;
            
            foreach (var msg in receivedMessages)
            {
                if (msg == $"First half of message and second half{Delimiter}")
                    foundFirstMessage = true;
                if (msg == $"Another split message{Delimiter}")
                    foundSecondMessage = true;
            }
            
            Assert.IsTrue(foundFirstMessage, "First assembled message should be received correctly");
            Assert.IsTrue(foundSecondMessage, "Second assembled message should be received correctly");
            
            // Cleanup
            await ClientSession.StopAsync();
            await Task.WhenAny(sessionTask, Task.Delay(1000));
        }

        // Helper method to receive multiple messages
        private async Task ReceiveMessagesAsync(Socket? socket, byte[] buffer, int expectedMessageCount, 
            ConcurrentBag<string> receivedMessages)
        {
            int receivedCount = 0;
            StringBuilder messageBuilder = new StringBuilder();
            
            while (receivedCount < expectedMessageCount)
            {
                Debug.Assert(socket != null, nameof(socket) + " != null");
                int bytesRead = await socket.ReceiveAsync(buffer, SocketFlags.None);
                
                if (bytesRead == 0)
                    break; // Connection closed
                
                string received = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                messageBuilder.Append(received);
                
                // Process complete messages
                string accumulated = messageBuilder.ToString();
                int delimiterPos;
                while ((delimiterPos = accumulated.IndexOf(Delimiter)) != -1)
                {
                    string message = accumulated.Substring(0, delimiterPos + 1);
                    receivedMessages.Add(message);
                    receivedCount++;
                    
                    accumulated = accumulated.Substring(delimiterPos + 1);
                }
                
                messageBuilder.Clear();
                messageBuilder.Append(accumulated);
            }
        }
    }
}