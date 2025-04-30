using System.Text;
using AsyncSocket;
using AsyncSocket.Framing;
using Microsoft.Extensions.Logging;
using Moq;

namespace Tests.FramingTest;

[TestClass]
public class CharDelimiterFramingTests
{
    private readonly Mock<ILogger<CharDelimiterFraming>> _loggerMock = new ();
    private const char DefaultDelimiter = ';';
    private const int DefaultMaxSize = 1024;
    
    [TestMethod]
    public void Constructor_NullLogger_DoesNotThrow()
    {
        // Act & Assert
        var framing = new CharDelimiterFraming(null, DefaultDelimiter, DefaultMaxSize);
        Assert.IsNotNull(framing);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(-100)]
    public void Constructor_InvalidMaxSize_ThrowsArgumentException(int maxLengthWithoutDelimiter)
    {
        // Act & Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => 
            new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, maxLengthWithoutDelimiter));
    }

    [TestMethod]
    public void Process_SingleMessage_ReturnsTrue()
    {
        // Arrange
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, DefaultMaxSize);
        string message = "Hello;";
        byte[] buffer = Encoding.UTF8.GetBytes(message);

        // Act
        bool result = framing.Process(buffer, buffer.Length);

        // Assert
        Assert.IsTrue(result);
        Assert.AreEqual(message, framing.Next());
        Assert.IsNull(framing.Next());
    }

    [TestMethod]
    public void Process_MultipleMessages_ReturnsCorrectSequence()
    {
        // Arrange
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, DefaultMaxSize);
        string messages = "Hello;World;Test;";
        byte[] buffer = Encoding.UTF8.GetBytes(messages);

        // Act
        bool result = framing.Process(buffer, buffer.Length);

        // Assert
        Assert.IsTrue(result);
        Assert.AreEqual("Hello;", framing.Next());
        Assert.AreEqual("World;", framing.Next());
        Assert.AreEqual("Test;", framing.Next());
        Assert.IsNull(framing.Next());
    }

    [TestMethod]
    public void Process_PartialMessage_ReturnsNullForNext()
    {
        // Arrange
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, DefaultMaxSize);
        string message = "Hello";  // No delimiter
        byte[] buffer = Encoding.UTF8.GetBytes(message);

        // Act
        bool result = framing.Process(buffer, buffer.Length);

        // Assert
        Assert.IsTrue(result);
        Assert.IsNull(framing.Next());
    }

    [TestMethod]
    public void Process_PartialMessageThenComplete_ReturnsMessageWhenComplete()
    {
        // Arrange
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, DefaultMaxSize);
            
        // First part
        string part1 = "Hello";
        byte[] buffer1 = Encoding.UTF8.GetBytes(part1);
            
        // Second part
        string part2 = " World;";
        byte[] buffer2 = Encoding.UTF8.GetBytes(part2);

        // Act & Assert - Part 1
        bool result1 = framing.Process(buffer1, buffer1.Length);
        Assert.IsTrue(result1);
        Assert.IsNull(framing.Next());

        // Act & Assert - Part 2
        bool result2 = framing.Process(buffer2, buffer2.Length);
        Assert.IsTrue(result2);
        Assert.AreEqual("Hello World;", framing.Next());
        Assert.IsNull(framing.Next());
    }

    [TestMethod]
    public void Process_ExceedsMaxSizeWithoutDelimiter_ReturnsFalse()
    {
        // Arrange
        int maxSize = 10;
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, maxSize);
        string message = "ThisMessageIsTooLongWithoutADelimiter";
        byte[] buffer = Encoding.UTF8.GetBytes(message);

        // Act
        bool result = framing.Process(buffer, buffer.Length);

        // Assert
        Assert.IsFalse(result);
        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString().Contains("Buffer exceeded maximum size without delimiter")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [TestMethod]
    public void Process_CloseToMaxSizeThenExceeds_ReturnsFalse()
    {
        // First part - just under max size
        string part1 = "1234567890";
        byte[] buffer1 = Encoding.UTF8.GetBytes(part1);
            
        // Second part - pushes over max size
        string part2 = "ABCDE";
        byte[] buffer2 = Encoding.UTF8.GetBytes(part2);
        
        // Arrange
        int maxSize = buffer1.Length + buffer2.Length -1;
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, maxSize);


        // Act & Assert - Part 1
        bool result1 = framing.Process(buffer1, buffer1.Length);
        Assert.IsTrue(result1);

        // Act & Assert - Part 2
        bool result2 = framing.Process(buffer2, buffer2.Length);
        Assert.IsFalse(result2);
    }

    [TestMethod]
    [DataRow('\0')]
    [DataRow('\n')]
    [DataRow('\r')]
    [DataRow('|')]
    [DataRow('$')]
    public void Process_DifferentDelimiters_HandlesCorrectly(char delimiter)
    {
        // Arrange
        var framing = new CharDelimiterFraming(_loggerMock.Object, delimiter, DefaultMaxSize);
        string message = $"Hello{delimiter}World{delimiter}";
        byte[] buffer = Encoding.UTF8.GetBytes(message);

        // Act
        bool result = framing.Process(buffer, buffer.Length);

        // Assert
        Assert.IsTrue(result);
        Assert.AreEqual($"Hello{delimiter}", framing.Next());
        Assert.AreEqual($"World{delimiter}", framing.Next());
        Assert.IsNull(framing.Next());
    }

    [TestMethod]
    public void Next_EmptyBuffer_ReturnsNull()
    {
        // Arrange
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, DefaultMaxSize);

        // Act & Assert
        Assert.IsNull(framing.Next());
    }

    [TestMethod]
    public void Process_NullBuffer_ThrowsArgumentNullException()
    {
        // Arrange
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, DefaultMaxSize);

        // Act & Assert
        Assert.ThrowsException<ArgumentNullException>(() => framing.Process(null, 10));
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(-100)]
    public void Process_InvalidBytesRead_ThrowsArgumentOutOfRangeException(int bytesRead)
    {
        // Arrange
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, DefaultMaxSize);
        byte[] buffer = Encoding.UTF8.GetBytes("Hello;");

        // Act & Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => framing.Process(buffer, bytesRead));
    }

    [TestMethod]
    public void Process_BytesReadExceedsBufferLength_ThrowsArgumentException()
    {
        // Arrange
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, DefaultMaxSize);
        byte[] buffer = "Hello;"u8.ToArray();

        // Act & Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => framing.Process(buffer, buffer.Length + 1));
    }

    [TestMethod]
    public void Process_ZeroBytesRead_ReturnsTrue()
    {
        // Arrange
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, DefaultMaxSize);
        byte[] buffer = Encoding.UTF8.GetBytes("Hello;");

        // Act
        bool result = framing.Process(buffer, 0);

        // Assert
        Assert.IsTrue(result);
        Assert.IsNull(framing.Next());
    }

    [TestMethod]
    public void StressTest_LargeNumberOfMessages()
    {
        // Arrange
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, 100000);
        StringBuilder sb = new StringBuilder();
        int messageCount = 1000;
            
        for (int i = 0; i < messageCount; i++)
        {
            sb.Append($"Message{i};");
        }
            
        byte[] buffer = Encoding.UTF8.GetBytes(sb.ToString());

        // Act
        bool result = framing.Process(buffer, buffer.Length);

        // Assert
        Assert.IsTrue(result);
            
        for (int i = 0; i < messageCount; i++)
        {
            string message = framing.Next();
            Assert.IsNotNull(message);
            Assert.AreEqual($"Message{i};", message);
        }
            
        Assert.IsNull(framing.Next());
    }

    [TestMethod]
    public void StressTest_LargeMessages()
    {
        // Arrange
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, 1000000);
            
        // Create a large message
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 10000; i++)
        {
            sb.Append("ABCDEFGHIJ");
        }
        sb.Append(DefaultDelimiter);
            
        string largeMessage = sb.ToString();
        byte[] buffer = Encoding.UTF8.GetBytes(largeMessage);

        // Act
        bool result = framing.Process(buffer, buffer.Length);

        // Assert
        Assert.IsTrue(result);
        Assert.AreEqual(largeMessage, framing.Next());
        Assert.IsNull(framing.Next());
    }

    [TestMethod]
    public void StressTest_FragmentedLargeMessage()
    {
        // Arrange
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, 200000);
            
        // Create a large message
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 10000; i++)
        {
            sb.Append("ABCDEFGHIJ");
        }
        sb.Append(DefaultDelimiter);
            
        string largeMessage = sb.ToString();
        byte[] allBytes = Encoding.UTF8.GetBytes(largeMessage);
            
        // Process in fragments
        const int fragmentSize = 1024;
        int remaining = allBytes.Length;
        int position = 0;
            
        while (remaining > 0)
        {
            int bytesToProcess = Math.Min(fragmentSize, remaining);
            byte[] fragment = new byte[bytesToProcess];
            Array.Copy(allBytes, position, fragment, 0, bytesToProcess);
                
            bool result = framing.Process(fragment, bytesToProcess);
            Assert.IsTrue(result);
                
            position += bytesToProcess;
            remaining -= bytesToProcess;
        }

        // Assert
        Assert.AreEqual(largeMessage, framing.Next());
        Assert.IsNull(framing.Next());
    }

    [TestMethod]
    public void StrangeTest_DelimiterAtStart()
    {
        // Arrange
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, DefaultMaxSize);
        string message = ";Hello;World;";
        byte[] buffer = Encoding.UTF8.GetBytes(message);

        // Act
        bool result = framing.Process(buffer, buffer.Length);

        // Assert
        Assert.IsTrue(result);
        Assert.AreEqual(";", framing.Next());  // Empty message with delimiter
        Assert.AreEqual("Hello;", framing.Next());
        Assert.AreEqual("World;", framing.Next());
        Assert.IsNull(framing.Next());
    }

    [TestMethod]
    public void StrangeTest_ConsecutiveDelimiters()
    {
        // Arrange
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, DefaultMaxSize);
        string message = "Hello;;World;";
        byte[] buffer = Encoding.UTF8.GetBytes(message);

        // Act
        bool result = framing.Process(buffer, buffer.Length);

        // Assert
        Assert.IsTrue(result);
        Assert.AreEqual("Hello;", framing.Next());
        Assert.AreEqual(";", framing.Next());  // Empty message
        Assert.AreEqual("World;", framing.Next());
        Assert.IsNull(framing.Next());
    }

    [TestMethod]
    public void StrangeTest_NonUtf8Characters()
    {
        // Arrange
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, DefaultMaxSize);
            
        // Create a buffer with some non-UTF8 bytes
        byte[] buffer = [0xFF, 0xFE, (byte)';', 0x48, 0x65, 0x6C, 0x6C, 0x6F, (byte)';'];

        // Act & Assert
        var result = framing.Process(buffer, buffer.Length);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void BoundaryTest_ExactlyMaxSize()
    {
        // Arrange
        int maxSize = 10;
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, maxSize);
            
        // String with exactly maxSize characters
        string message = "123456789";  // 9 chars (need 1 more for delimiter to be valid)
        byte[] buffer = Encoding.UTF8.GetBytes(message);

        // Act
        bool result = framing.Process(buffer, buffer.Length);

        // Assert
        Assert.IsTrue(result);
            
        // Add the delimiter to make it exactly at the boundary
        buffer = Encoding.UTF8.GetBytes(";");
        result = framing.Process(buffer, buffer.Length);
            
        Assert.IsTrue(result);
        Assert.AreEqual("123456789;", framing.Next());
    }

    [TestMethod]
    public void BoundaryTest_MaxSizePlusOne()
    {
        // Arrange
        int maxSize = 10;
        var framing = new CharDelimiterFraming(_loggerMock.Object, DefaultDelimiter, maxSize);
            
        // String with maxSize + 1 characters without delimiter
        string message = "1234567890A";  // 11 chars, no delimiter
        byte[] buffer = Encoding.UTF8.GetBytes(message);

        // Act
        bool result = framing.Process(buffer, buffer.Length);

        // Assert
        Assert.IsFalse(result);
    }
}