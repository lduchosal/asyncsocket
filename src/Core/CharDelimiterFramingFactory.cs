using Microsoft.Extensions.Logging;

namespace AsyncSocket;

public interface IMessageFramingFactory
{
    public IMessageFraming CreateFraming();
}
public class CharDelimiterFramingFactory(char delimiter = '\n', int maxSizeWithoutADelimiter = 1024, ILoggerFactory? loggerFactory = null) : IMessageFramingFactory
{
    public IMessageFraming CreateFraming()
    {
        var loggerFraming = loggerFactory?.CreateLogger<CharDelimiterFraming>();
        var framing = new CharDelimiterFraming(loggerFraming, delimiter, maxSizeWithoutADelimiter);
        return framing;
    }
}