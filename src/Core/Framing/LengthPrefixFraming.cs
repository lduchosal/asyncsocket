using System;
using System.ComponentModel;
using Microsoft.Extensions.Logging;

namespace AsyncSocket.Framing;

public class LengthPrefixFraming : IMessageFraming<byte[]>
{
    private readonly ILogger<LengthPrefixFraming>? Logger;
    private readonly int HeaderSize;
    private readonly int MaxMessageSize;
    
    private byte[] _buffer = [];
    private int _bufferPosition = 0;
    private int? _currentMessageLength = null;

    private bool Failed = false;

    public LengthPrefixFraming(ILogger<LengthPrefixFraming>? logger, int headerSize = 4, int maxMessageSize = 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(headerSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageSize);
        
        Logger = logger;
        HeaderSize = headerSize;
        MaxMessageSize = maxMessageSize;
    }

    public bool Process(byte[] receiveBuffer, int bytesRead)
    {

        
        
        // Resize buffer to accommodate new data
        int newSize = _bufferPosition + bytesRead;
        Array.Resize(ref _buffer, newSize);
        
        // Copy new data to buffer
        Array.Copy(receiveBuffer, 0, _buffer, _bufferPosition, bytesRead);
        _bufferPosition = newSize;

        // Check if we have a pending message length to process
        if (_currentMessageLength == null && _bufferPosition >= HeaderSize)
        {
            _currentMessageLength = ReadMessageLength(_buffer);
            
            // Validate message size
            if (_currentMessageLength <= 0 || _currentMessageLength > MaxMessageSize)
            {
                Logger?.LogDebug("Invalid message length: {Length}", _currentMessageLength);
                Failed = true;
                return false;
            }
        }

        return true;
    }

    public byte[]? Next()
    {
        // Need header first to determine message length
        if (_currentMessageLength == null)
        {
            if (_bufferPosition < HeaderSize)
            {
                return null;
            }
            
            _currentMessageLength = ReadMessageLength(_buffer);
            
            // Validate message size
            if (_currentMessageLength <= 0 || _currentMessageLength > MaxMessageSize)
            {
                Logger?.LogDebug("Invalid message length: {Length}", _currentMessageLength);
                return null;
            }
        }
        
        // Check if we have a complete message
        int totalMessageSize = HeaderSize + _currentMessageLength.Value;
        if (_bufferPosition < totalMessageSize)
        {
            return null;
        }
        
        // Extract the message (excluding the header)
        byte[] message = new byte[_currentMessageLength.Value];
        Array.Copy(_buffer, HeaderSize, message, 0, _currentMessageLength.Value);
        
        // Remove the processed message and header from the buffer
        int remainingDataSize = _bufferPosition - totalMessageSize;
        if (remainingDataSize > 0)
        {
            byte[] newBuffer = new byte[remainingDataSize];
            Array.Copy(_buffer, totalMessageSize, newBuffer, 0, remainingDataSize);
            _buffer = newBuffer;
        }
        else
        {
            _buffer = Array.Empty<byte>();
        }
        
        _bufferPosition = remainingDataSize;
        _currentMessageLength = null;
        
        return message;
    }
    
    private int ReadMessageLength(byte[] buffer)
    {
        // Default implementation uses big-endian (network byte order)
        int length = 0;
        for (int i = 0; i < HeaderSize; i++)
        {
            length = (length << 8) | buffer[i];
        }
        return length;
    }
}