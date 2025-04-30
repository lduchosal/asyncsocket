using System.Text;
using Microsoft.Extensions.Logging;

namespace AsyncSocket;

public class CharDelimiterFraming(ILogger<CharDelimiterFraming>? logger, char delimiter, int maxSizeWithoutADelimiter) : IMessageFraming
{
    private readonly StringBuilder _stringBuffer = new();

    public bool Process(byte[] receiveBuffer, int bytesRead)
    {
        string receivedText = Encoding.UTF8.GetString(receiveBuffer, 0, bytesRead);
        _stringBuffer.Append(receivedText);
        if (_stringBuffer.Length > maxSizeWithoutADelimiter && 
            _stringBuffer.ToString().IndexOf(delimiter) == -1)
        {
            logger?.LogDebug("Buffer exceeded maximum size without delimiter.");
            return false;
        }

        return true;
    }

    public string? Next()
    {
        int delimiterPos = _stringBuffer.ToString().IndexOf(delimiter);
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