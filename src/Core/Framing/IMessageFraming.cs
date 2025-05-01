namespace AsyncSocket.Framing;

public interface IMessageFraming<out T>
{
    bool Process(byte[] receiveBuffer, int bytesRead);
    T? Next();
}