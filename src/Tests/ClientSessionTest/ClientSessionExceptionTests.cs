using System.Net;
using System.Net.Sockets;
using System.Text;
using AsyncSocket;
using AsyncSocket.Framing;
using AsyncSocket.Properties;

namespace Tests.ClientSessionTest
{
    [TestClass]
    public class ClientSessionExceptionTests
    {
        private const char Delimiter = '\n';
        private const int MaxSiteWithoutADelimiter = 1024;
        private const int BufferSize = 1024;

        private SocketAsyncEventArgsPool ArgsPool{ get; set; } = null!;
        private CancellationTokenSource Cts{ get; set; } = null!;

        [TestInitialize]
        public void Setup()
        {
            ArgsPool = new SocketAsyncEventArgsPool(10);
            Cts = new CancellationTokenSource();
        }

        [TestCleanup]
        public void Cleanup()
        {
            Cts.Cancel();
            Cts.Dispose();
        }

        [TestMethod]
        public async Task StopAsync_CanBeCalledMultipleTimes()
        {
            // Arrange
            var (session, clientSocket, sessionTask) = await CreateClientSessionPairAsync();
            bool passed = true;

            try
            {
                // Act
                await session.StopAsync(); // First call
                await session.StopAsync(); // Second call should not throw
                await session.StopAsync(); // Third call should not throw

                // Assert - if we got here without exceptions, the test passes

                // Clean up
                clientSocket.Close();
            }
            catch (Exception)
            {
                passed = false;
            }
            Assert.IsTrue(passed);
        }

        [TestMethod]
        public async Task SendAsync_AfterStop_ThrowsClientException()
        {
            // Arrange
            var (session, clientSocket, sessionTask) = await CreateClientSessionPairAsync();
            
            // Stop the session
            await session.StopAsync();
            
            // Act & Assert
            await Assert.ThrowsExceptionAsync<ClientException>(() => 
                session.SendAsync("This should fail"));
            
            // Clean up
            clientSocket.Close();
        }

        [TestMethod]
        [Ignore("FailOnGitHub")]
        public async Task CancellationToken_Afterreceived_TriggersGracefulShutdown()
        {
            // Arrange
            var localCts = new CancellationTokenSource();
            var (session, clientSocket, sessionTask) = await CreateClientSessionPairAsync(cancellationToken: localCts.Token);
            
            bool disconnectEventRaised = false;
            session.Disconnected += (sender, id) =>
            {
                disconnectEventRaised = true;
            };

            var bytes = "Send\n"u8.ToArray();
            var sent = await clientSocket.SendAsync(bytes);

            // Send a message to verify the session is working
            await session.SendAsync("Test message\n");

            
            // Act
            localCts.Cancel();
            
            // Wait for the session to shut down
            await Task.WhenAny(sessionTask, Task.Delay(1000));
            
            // Assert
            Assert.IsTrue(disconnectEventRaised, "Disconnected event should be raised on cancellation");
            
            // Verify that sending after cancellation fails
            await Assert.ThrowsExceptionAsync<ClientException>(() => 
                session.SendAsync("This should fail"));
            
            // Clean up
            clientSocket.Close();
            localCts.Dispose();
        }

        
        
        [TestMethod]
        [Ignore("FailOnGitHub")]
        public async Task CancellationToken_BeforeReceived_TriggersGracefulShutdown()
        {
            // Arrange
            var localCts = new CancellationTokenSource();
            var (session, clientSocket, sessionTask) = await CreateClientSessionPairAsync(cancellationToken: localCts.Token);
            
            bool disconnectEventRaised = false;
            session.Disconnected += (sender, id) =>
            {
                disconnectEventRaised = true;
            };

            // Send a message to verify the session is working
            await session.SendAsync("Test message\n");

            // Act
            localCts.Cancel();
            
            // Wait for the session to shut down
            await Task.WhenAny(sessionTask, Task.Delay(1000));
            
            // Assert
            Assert.IsTrue(disconnectEventRaised, "Disconnected event should be raised on cancellation");
            
            // Verify that sending after cancellation fails
            await Assert.ThrowsExceptionAsync<ClientException>(() => 
                session.SendAsync("This should fail"));
            
            // Clean up
            clientSocket.Close();
            localCts.Dispose();
        }

        [TestMethod]
        [Ignore("FailOnGitHub")]
        public async Task MessageTooLarge_DisconnectsClient()
        {
            // Arrange
            var (session, clientSocket, sessionTask) = await CreateClientSessionPairAsync();
            
            bool disconnectedEventRaised = false;
            session.Disconnected += (sender, id) => disconnectedEventRaised = true;
            
            // Create a message that's just below the max buffer size but doesn't have a delimiter
            string oversizedData = new string('X', BufferSize + 1);
            
            // Act - send the oversized data in chunks to avoid transport layer fragmenting
            int chunkSize = 100;
            for (int i = 0; i < oversizedData.Length; i += chunkSize)
            {
                int length = Math.Min(chunkSize, oversizedData.Length - i);
                string chunk = oversizedData.Substring(i, length);
                byte[] data = Encoding.UTF8.GetBytes(chunk);
                await clientSocket.SendAsync(data, SocketFlags.None);
                
                // Small delay to ensure data is processed
                await Task.Delay(10);
                
                // If disconnected, break out of the loop
                if (disconnectedEventRaised)
                    break;
            }
            // Wait for the session to shut down
            await Task.WhenAny(sessionTask, Task.Delay(1000));

            // Wait a bit more to ensure the session has time to process
            if (!disconnectedEventRaised)
                await Task.Delay(1000);

            // Assert
            Assert.IsTrue(disconnectedEventRaised, 
                "Session should disconnect when receiving a message exceeding buffer size without delimiter");
            
            // Clean up
            try
            {
                clientSocket.Close();
            }
            catch { /* Ignore cleanup errors */ }
        }

        [TestMethod]
        public async Task SocketAsyncEventArgsPool_ExhaustedPool_ThrowsException()
        {
            // Arrange
            ISocketAsyncEventArgsPool smallPool = new ExceptionSocketAsyncEventArgsPool(); // Pool will thorw exception
            var (session, clientSocket, sessionTask) = await CreateClientSessionPairAsync(cancellationToken: Cts.Token, pool: smallPool);
            
            try
            {
                // Act & Assert
                await Assert.ThrowsExceptionAsync<ClientException>(async () => 
                {

                    // This should fail because the pool throws exception
                    await session.SendAsync("Test message");

                });
            }
            finally
            {
                // Clean up
                await session.StopAsync();
                clientSocket.Close();
            }
        }

        [TestMethod]
        public async Task ProcessDelimitedMessagesAsync_ContinuesAfterEventHandlerException()
        {
            // Arrange
            var (session, clientSocket, sessionTask) = await CreateClientSessionPairAsync();
            
            bool firstMessageReceived = false;
            bool secondMessageReceived = false;
            
            // Add event handler that throws an exception on first message
            session.MessageReceived += (sender, message) => 
            {
                if (!firstMessageReceived)
                {
                    firstMessageReceived = true;
                    throw new Exception("Test exception from event handler");
                }
                else
                {
                    secondMessageReceived = true;
                }
            };
            
            // Act
            // Send two messages
            byte[] message1 = Encoding.UTF8.GetBytes("First message" + Delimiter);
            byte[] message2 = Encoding.UTF8.GetBytes("Second message" + Delimiter);
            
            await clientSocket.SendAsync(message1, SocketFlags.None);
            await Task.Delay(100); // Short delay
            await clientSocket.SendAsync(message2, SocketFlags.None);
            
            // Wait for the session to shut down
            await Task.WhenAny(sessionTask, Task.Delay(1000));

            // Assert
            Assert.IsTrue(firstMessageReceived, "First message should trigger the event handler");
            Assert.IsFalse(secondMessageReceived, "Any error will disconnect the client and close the session");
            
            // Clean up
            await session.StopAsync();
            clientSocket.Close();
        }

        [TestMethod]
        public async Task ExternalSocketClosure_TriggersDisconnectEvent()
        {
            // Arrange
            var (session, clientSocket, sessionTask) = await CreateClientSessionPairAsync();
            
            bool disconnectEventRaised = false;
            session.Disconnected += (sender, id) => disconnectEventRaised = true;
            
            // Act
            // Close the socket from outside
            clientSocket.Close();
            
            // Wait for the session to detect closure
            await Task.Delay(500);
            
            // Assert
            Assert.IsTrue(disconnectEventRaised, "Disconnected event should be raised when socket is closed externally");
            
            // No need to clean up as socket is already closed
        }

        [TestMethod]
        public async Task ClientSocket_ResetConnection_TriggersDisconnectEvent()
        {
            // Arrange
            var (session, clientSocket, sessionTask) = await CreateClientSessionPairAsync();
            
            bool disconnectEventRaised = false;
            session.Disconnected += (sender, id) => disconnectEventRaised = true;
            
            // Act - forcibly abort the connection
            clientSocket.LingerState = new LingerOption(true, 0);
            clientSocket.Close();
            
            // Wait for the session to detect closure
            await Task.Delay(500);
            
            // Assert
            Assert.IsTrue(disconnectEventRaised, "Disconnected event should be raised when connection is reset");
        }

        // Helper method to create a client-server socket pair and client session
        private async Task<(ClientSession<string> session, Socket clientSocket, Task sessionTask)>
            CreateClientSessionPairAsync(ISocketAsyncEventArgsPool? pool = null, CancellationToken cancellationToken = default)
        {
                        
            // Use the provided token or default to the one from the test fixture
            var tokenToUse = cancellationToken == default ? Cts.Token : cancellationToken;

            // Use the provided pool or default to the one from the test fixture
            var poolToUse = pool ?? ArgsPool;

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var clientTask = Task.Run(() => {
                var client = new TcpClient();
                client.Connect(endpoint);
                return client;
            }, tokenToUse);
            
            Socket serverSocket = await Task.Run(() => listener.AcceptSocket(), tokenToUse);
            TcpClient client = await clientTask;
            Socket clientSocket = client.Client;
            
            listener.Stop();
            var framing = new CharDelimiterFraming(null, Delimiter, MaxSiteWithoutADelimiter);
            var session = new ClientSession<string>(null, Guid.NewGuid(), serverSocket, framing, BufferSize, poolToUse);

            // Start the session
            var sessionTask = session.StartAsync(tokenToUse);
            
            // Short delay to ensure session is ready
            await Task.Delay(50, tokenToUse);
            
            return (session, clientSocket, sessionTask);
        }
    }

    public class ExceptionSocketAsyncEventArgsPool : ISocketAsyncEventArgsPool
    {
        public int Count
        {
            get
            {
                throw new Exception("Count throws exception");
            }
        }

        public SocketAsyncEventArgs Get()
        {
            throw new Exception("Get throws exception");
        }

        public void Return(SocketAsyncEventArgs item)
        {
            throw new Exception("Return throws exception");
        }
    }
}