namespace StreamDuel.Core
{
    public interface INetTransport
    {
        void Send(string msg);
        bool TryReceive(out string msg);
    }
}
