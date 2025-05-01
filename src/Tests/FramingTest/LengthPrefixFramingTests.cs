using AsyncSocket.Framing;
using Microsoft.Extensions.Logging;
using Moq;

namespace Tests.FramingTest;

[TestClass]
public class LengthPrefixFramingTests
{
    private readonly Mock<ILogger<LengthPrefixFraming>> _loggerMock = new ();

    [TestMethod]
    public void Constructor_ValidParameters_CreatesInstance()
    {
        // Act
        var framing = new LengthPrefixFraming(_loggerMock.Object, 4, 1024);
            
        // Assert
        Assert.IsNotNull(framing);
    }

    [TestMethod]
    public void Constructor_NullLogger_CreatesInstance()
    {
        // Act
        var framing = new LengthPrefixFraming(null, 4, 1024);
            
        // Assert
        Assert.IsNotNull(framing);
    }

    [DataTestMethod]
    [DataRow(0, 1024)]
    [DataRow(-1, 1024)]
    [DataRow(4, 0)]
    [DataRow(4, -1)]
    public void TestConstructorThrowsArgumentOutOfRangeException(int headerSize, int maxMessageSize)
    {
        // Act & Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => 
            new LengthPrefixFraming(_loggerMock.Object, headerSize, maxMessageSize));
    }

    [TestMethod]
    public void Process_EmptyBuffer_ReturnsTrue()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object);
        var buffer = Array.Empty<byte>();
            
        // Act
        var result = framing.Process(buffer, 0);
            
        // Assert
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Process_NullBuffer_ThrowsArgumentNullException()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object);
            
        // Act & Assert
        Assert.ThrowsException<ArgumentNullException>(() => framing.Process(null, 0));
    }

    [TestMethod]
    public void Process_NegativeBytesRead_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object);
        var buffer = new byte[10];
            
        // Act & Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => framing.Process(buffer, -1));
    }

    [TestMethod]
    public void Process_InsufficientHeaderData_ReturnsTrue()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object);
        var buffer = new byte[] { 0, 0, 1 }; // Only 3 bytes, header is 4
            
        // Act
        var result = framing.Process(buffer, buffer.Length);
            
        // Assert
        Assert.IsTrue(result);
        Assert.IsNull(framing.Next()); // No message yet
    }

    [TestMethod]
    public void Process_InvalidMessageLength_ReturnsFalse()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object, 4, 1024);
        // Header indicates a negative length
        byte[] buffer = [0xFF, 0xFF, 0xFF, 0xFF];
            
        // Act
        var result = framing.Process(buffer, buffer.Length);
            
        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void Process_MessageLengthExceedsMaxSize_ReturnsFalse()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object, 4, 10);
        // Header indicates length larger than max (100)
        byte[] buffer = [0, 0, 0, 100];
            
        // Act
        var result = framing.Process(buffer, buffer.Length);
            
        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void Next_NoData_ReturnsNull()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object);
            
        // Act
        var result = framing.Next();
            
        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Next_CompleteMessage_ReturnsMessage()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object);
        // Header: 0,0,0,3 (length 3), followed by payload: 1,2,3
        var buffer = new byte[] { 0, 0, 0, 3, 1, 2, 3 };
        framing.Process(buffer, buffer.Length);
            
        // Act
        var result = framing.Next();
            
        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(3, result.Length);
        Assert.AreEqual(1, result[0]);
        Assert.AreEqual(2, result[1]);
        Assert.AreEqual(3, result[2]);
    }

    [TestMethod]
    public void Next_PartialMessage_ReturnsNull()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object);
        // Header: 0,0,0,5 (length 5), followed by only 3 bytes: 1,2,3
        var buffer = new byte[] { 0, 0, 0, 5, 1, 2, 3 };
        framing.Process(buffer, buffer.Length);
            
        // Act
        var result = framing.Next();
            
        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Next_MultipleMessages_ReturnsMessagesSequentially()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object);
        // Two messages: [0,0,0,2,10,20] and [0,0,0,3,30,40,50]
        var buffer = new byte[] { 0, 0, 0, 2, 10, 20, 0, 0, 0, 3, 30, 40, 50 };
        framing.Process(buffer, buffer.Length);
            
        // Act & Assert - First message
        var result1 = framing.Next();
        Assert.IsNotNull(result1);
        Assert.AreEqual(2, result1.Length);
        Assert.AreEqual(10, result1[0]);
        Assert.AreEqual(20, result1[1]);
            
        // Act & Assert - Second message
        var result2 = framing.Next();
        Assert.IsNotNull(result2);
        Assert.AreEqual(3, result2.Length);
        Assert.AreEqual(30, result2[0]);
        Assert.AreEqual(40, result2[1]);
        Assert.AreEqual(50, result2[2]);
            
        // Act & Assert - No more messages
        var result3 = framing.Next();
        Assert.IsNull(result3);
    }

    [TestMethod]
    public void Process_MultipleFragmentedCalls_CombinesData()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object);
            
        // First fragment: part of the header
        var buffer1 = new byte[] { 0, 0 };
        framing.Process(buffer1, buffer1.Length);
        Assert.IsNull(framing.Next());
            
        // Second fragment: rest of the header and part of payload
        var buffer2 = new byte[] { 0, 4, 1, 2 };
        framing.Process(buffer2, buffer2.Length);
        Assert.IsNull(framing.Next());
            
        // Third fragment: rest of the payload
        var buffer3 = new byte[] { 3, 4 };
        framing.Process(buffer3, buffer3.Length);
            
        // Act
        var result = framing.Next();
            
        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(4, result.Length);
        Assert.AreEqual(1, result[0]);
        Assert.AreEqual(2, result[1]);
        Assert.AreEqual(3, result[2]);
        Assert.AreEqual(4, result[3]);
    }

    [DataTestMethod]
    [DataRow(1, 5)]    // Minimum header size
    [DataRow(2, 10)]   // Small header size
    [DataRow(4, 15)]   // Standard header size
    [DataRow(8, 20)]   // Large header size
    public void Process_DifferentHeaderSizes_HandlesCorrectly(int headerSize, int payloadSize)
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object, headerSize, 1024);
            
        // Create a message with the specified header size
        var buffer = new byte[headerSize + payloadSize];
            
        // Set the header to represent the payload size (in big-endian format)
        for (int i = 0; i < headerSize; i++)
        {
            buffer[i] = (byte)(payloadSize >> (8 * (headerSize - i - 1)) & 0xFF);
        }
            
        // Fill the payload with sequential numbers
        for (int i = 0; i < payloadSize; i++)
        {
            buffer[headerSize + i] = (byte)(i + 1);
        }
            
        // Act
        framing.Process(buffer, buffer.Length);
        var result = framing.Next();
            
        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(payloadSize, result.Length);
        for (int i = 0; i < payloadSize; i++)
        {
            Assert.AreEqual(i + 1, result[i]);
        }
    }

    [TestMethod]
    public void StressTest_LargeNumberOfMessages_HandlesCorrectly()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object);
        var random = new Random(42); // Fixed seed for reproducibility
        var expectedMessages = new List<byte[]>();
            
        // Create a large buffer with multiple messages of varied sizes
        using var ms = new MemoryStream();
            
        // Generate 1000 messages with random sizes
        for (int i = 0; i < 1000; i++)
        {
            int messageSize = random.Next(1, 1000); // Random size between 1 and 1000 bytes
            byte[] message = new byte[messageSize];
            random.NextBytes(message);
            expectedMessages.Add(message);
                
            // Write the message length header (4 bytes, big-endian)
            ms.WriteByte((byte)(messageSize >> 24));
            ms.WriteByte((byte)(messageSize >> 16));
            ms.WriteByte((byte)(messageSize >> 8));
            ms.WriteByte((byte)messageSize);
                
            // Write the message content
            ms.Write(message, 0, message.Length);
        }
            
        byte[] completeBuffer = ms.ToArray();
            
        // Process the data in chunks to simulate network fragmentation
        const int chunkSize = 1024;
        for (int offset = 0; offset < completeBuffer.Length; offset += chunkSize)
        {
            int size = Math.Min(chunkSize, completeBuffer.Length - offset);
            byte[] chunk = new byte[size];
            Array.Copy(completeBuffer, offset, chunk, 0, size);
            framing.Process(chunk, chunk.Length);
        }
            
        // Act & Assert: Extract and verify each message
        for (int i = 0; i < expectedMessages.Count; i++)
        {
            var result = framing.Next();
            Assert.IsNotNull(result, $"Message {i} should be available");
            CollectionAssert.AreEqual(expectedMessages[i], result, $"Message {i} content doesn't match");
        }
            
        // Verify no more messages
        Assert.IsNull(framing.Next());
    }

    [TestMethod]
    public void BoundaryTest_ExactMaxMessageSize_HandlesCorrectly()
    {
        // Arrange
        int maxSize = 1024;
        var framing = new LengthPrefixFraming(_loggerMock.Object, 4, maxSize);
            
        // Create a message of exactly the maximum size
        var payload = new byte[maxSize];
        for (int i = 0; i < maxSize; i++)
        {
            payload[i] = (byte)(i % 256);
        }
            
        var buffer = new byte[4 + maxSize];
        buffer[0] = (byte)(maxSize >> 24);
        buffer[1] = (byte)(maxSize >> 16);
        buffer[2] = (byte)(maxSize >> 8);
        buffer[3] = (byte)maxSize;
        Array.Copy(payload, 0, buffer, 4, maxSize);
            
        // Act
        framing.Process(buffer, buffer.Length);
        var result = framing.Next();
            
        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(maxSize, result.Length);
        for (int i = 0; i < maxSize; i++)
        {
            Assert.AreEqual(i % 256, result[i]);
        }
    }

    [TestMethod]
    public void BoundaryTest_OneByteOverMaxMessageSize_ReturnsFalse()
    {
        // Arrange
        const int maxSize = 1024;
        var framing = new LengthPrefixFraming(_loggerMock.Object, 4, maxSize);
            
        // Create a message one byte over the maximum size
        var buffer = new byte[4];
        int oversize = maxSize + 1;
        buffer[0] = (byte)(oversize >> 24);
        buffer[1] = (byte)(oversize >> 16);
        buffer[2] = (byte)(oversize >> 8);
        buffer[3] = (byte)oversize;
            
        // Act
        var result = framing.Process(buffer, buffer.Length);
            
        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void StrangeCase_ZeroLengthMessage_ProcessesCorrectly()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object);
        // A message with length 0 (should be considered invalid)
        var buffer = new byte[] { 0, 0, 0, 0 };
            
        // Act
        var result = framing.Process(buffer, buffer.Length);
            
        // Assert
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void StrangeCase_InterleaveMultipleProcessCalls_HandlesCorrectly()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object);
            
        // Process first part of message 1
        var buffer1 = new byte[] { 0, 0, 0, 3, 1 };
        framing.Process(buffer1, buffer1.Length);
            
        // Process first part of message 2
        var buffer2 = new byte[] { 2, 3, 0, 0 };
        framing.Process(buffer2, buffer2.Length);
            
        // Complete message 2
        var buffer3 = new byte[] { 0, 2, 4, 5 };
        framing.Process(buffer3, buffer3.Length);
            
        // Act & Assert
        // Message 1 should be complete
        var result1 = framing.Next();
        Assert.IsNotNull(result1);
        Assert.AreEqual(3, result1.Length);
        Assert.AreEqual(1, result1[0]);
        Assert.AreEqual(2, result1[1]);
        Assert.AreEqual(3, result1[2]);
            
        // Message 2 should be complete
        var result2 = framing.Next();
        Assert.IsNotNull(result2);
        Assert.AreEqual(2, result2.Length);
        Assert.AreEqual(4, result2[0]);
        Assert.AreEqual(5, result2[1]);
            
        // No more messages
        Assert.IsNull(framing.Next());
    }

    [TestMethod]
    public void StressTest_AlternatingLargeAndSmallMessages_HandlesCorrectly()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object, 4, 100000);
        var random = new Random(42);
            
        for (int i = 0; i < 100; i++)
        {
            // Small message (1-10 bytes)
            int smallSize = random.Next(1, 11);
            byte[] smallMessage = CreateMessage(smallSize, i * 2);
                
            // Large message (1000-10000 bytes)
            int largeSize = random.Next(1000, 10001);
            byte[] largeMessage = CreateMessage(largeSize, i * 2 + 1);
                
            // Process both messages
            framing.Process(smallMessage, smallMessage.Length);
            framing.Process(largeMessage, largeMessage.Length);
                
            // Verify small message
            var resultSmall = framing.Next();
            Assert.IsNotNull(resultSmall);
            Assert.AreEqual(smallSize, resultSmall.Length);
            Assert.AreEqual(i * 2, resultSmall[0]); // Check identifier
                
            // Verify large message
            var resultLarge = framing.Next();
            Assert.IsNotNull(resultLarge);
            Assert.AreEqual(largeSize, resultLarge.Length);
            Assert.AreEqual(i * 2 + 1, resultLarge[0]); // Check identifier
        }
            
        // No more messages
        Assert.IsNull(framing.Next());
    }

    // Helper method to create a message with header and payload
    private byte[] CreateMessage(int payloadSize, int identifier)
    {
        var message = new byte[4 + payloadSize];
        // Set header (big-endian)
        message[0] = (byte)(payloadSize >> 24);
        message[1] = (byte)(payloadSize >> 16);
        message[2] = (byte)(payloadSize >> 8);
        message[3] = (byte)payloadSize;
            
        // Set payload (first byte is identifier, rest are sequential)
        message[4] = (byte)(identifier % 256);
        for (int i = 1; i < payloadSize; i++)
        {
            message[4 + i] = (byte)(i % 256);
        }
            
        return message;
    }

    [TestMethod]
    public void Recovery_AfterInvalidMessage_CanContinueProcessing()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object, 4, 100);
            
        // Process an invalid message (too large)
        var invalidBuffer = new byte[] { 0, 0, 1, 0 }; // Length 256, max is 100
        var invalidResult = framing.Process(invalidBuffer, invalidBuffer.Length);
        Assert.IsFalse(invalidResult);
            
        // Process a valid message
        var validBuffer = new byte[] { 0, 0, 0, 2, 42, 43 };
        var validResult = framing.Process(validBuffer, validBuffer.Length);
        Assert.IsTrue(validResult);
            
        // Act
        var message = framing.Next();
            
        // Assert
        Assert.IsNull(message);
    }

    [TestMethod]
    public void StrangeCase_EmptyProcessCallsFollowedByValidData_HandlesCorrectly()
    {
        // Arrange
        var framing = new LengthPrefixFraming(_loggerMock.Object);
            
        // Several empty process calls
        framing.Process([], 0);
        framing.Process([], 0);
        framing.Process([], 0);
            
        // Valid data
        var buffer = new byte[] { 0, 0, 0, 3, 1, 2, 3 };
        framing.Process(buffer, buffer.Length);
            
        // Act
        var result = framing.Next();
            
        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(3, result.Length);
        Assert.AreEqual(1, result[0]);
        Assert.AreEqual(2, result[1]);
        Assert.AreEqual(3, result[2]);
    }

    [TestMethod]
    public void BoundaryTest_VeryLargeMessage_ProcessesCorrectly()
    {
        // Arrange
        int largeSize = 1024 * 1024; // 1MB
        var framing = new LengthPrefixFraming(_loggerMock.Object);
            
        // Create header for large message
        var header = new byte[] { 
            (byte)(largeSize >> 24), 
            (byte)(largeSize >> 16), 
            (byte)(largeSize >> 8), 
            (byte)largeSize 
        };
            
        framing.Process(header, header.Length);
            
        // Create and process payload in chunks
        const int chunkSize = 16384; // 16KB chunks
        for (int offset = 0; offset < largeSize; offset += chunkSize)
        {
            int size = Math.Min(chunkSize, largeSize - offset);
            byte[] chunk = new byte[size];
                
            // Fill with recognizable pattern
            for (int i = 0; i < size; i++)
            {
                chunk[i] = (byte)((offset + i) % 256);
            }
                
            framing.Process(chunk, chunk.Length);
        }
            
        // Act
        var result = framing.Next();
            
        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(largeSize, result.Length);
            
        // Verify pattern in samples of the result (checking every byte would be too slow)
        for (int i = 0; i < largeSize; i += 10000)
        {
            Assert.AreEqual((byte)(i % 256), result[i], $"Mismatch at position {i}");
        }
    }
}