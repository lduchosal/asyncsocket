namespace AsyncSocket;

public interface IMessageFraming
{
    bool Process(byte[] receiveBuffer, int bytesRead);
    string? Next();
}