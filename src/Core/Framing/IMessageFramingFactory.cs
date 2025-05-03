namespace AsyncSocket.Framing;

public interface IMessageFramingFactory<out T>
{
    public IMessageFraming<T> CreateFraming();
}