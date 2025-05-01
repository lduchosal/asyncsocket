using Microsoft.Extensions.Logging;

namespace AsyncSocket.Framing;

public interface IMessageFramingFactory<T>
{
    public IMessageFraming<T> CreateFraming();
}
public class CharDelimiterFramingFactory(
    char delimiter = '\n', 
    int maxSizeWithoutADelimiter = 1024, 
    ILoggerFactory? loggerFactory = null) : IMessageFramingFactory<string>
{
    public IMessageFraming<string> CreateFraming()
    {
        var loggerFraming = loggerFactory?.CreateLogger<CharDelimiterFraming>();
        var framing = new CharDelimiterFraming(loggerFraming, delimiter, maxSizeWithoutADelimiter);
        return framing;
    }
}