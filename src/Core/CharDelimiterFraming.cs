using System.Text;
using Microsoft.Extensions.Logging;

namespace AsyncSocket;

public class CharDelimiterFraming : IMessageFraming<string>
{
    private readonly StringBuilder _stringBuffer = new();
    private readonly ILogger<CharDelimiterFraming>? Logger;
    private readonly char Delimiter;
    private readonly int MaxSizeWithoutADelimiter;

    public CharDelimiterFraming(ILogger<CharDelimiterFraming>? logger, char delimiter, int maxSizeWithoutADelimiter)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSizeWithoutADelimiter);
        
        Logger = logger;
        Delimiter = delimiter;
        MaxSizeWithoutADelimiter = maxSizeWithoutADelimiter;
    }

    public bool Process(byte[] receiveBuffer, int bytesRead)
    {
        string receivedText = Encoding.UTF8.GetString(receiveBuffer, 0, bytesRead);
        _stringBuffer.Append(receivedText);
        if (_stringBuffer.Length > MaxSizeWithoutADelimiter && 
            _stringBuffer.ToString().IndexOf(Delimiter) == -1)
        {
            Logger?.LogDebug("Buffer exceeded maximum size without delimiter.");
            return false;
        }

        return true;
    }

    public string? Next()
    {
        int delimiterPos = _stringBuffer.ToString().IndexOf(Delimiter);
        if (delimiterPos == -1)
        {
            return null;
        }
         
         // Extract the message up to the delimiter
         string message = _stringBuffer.ToString(0, delimiterPos + 1);
                
         // Remove the processed message and delimiter from the buffer
         _stringBuffer.Remove(0, delimiterPos + 1);

         return message;
    }
}