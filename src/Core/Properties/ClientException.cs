namespace AsyncSocket.Properties;

public class ClientException(string message) : Exception(message)
{
    public static void ThrowIf(bool b, string message)
    {
        if (b)
        {
            throw new ClientException(message);
        }
    }
}