namespace AsyncSocket.Framing;

public interface IMessageFraming<T>
{
    bool Process(byte[] receiveBuffer, int bytesRead);
    T? Next();
}