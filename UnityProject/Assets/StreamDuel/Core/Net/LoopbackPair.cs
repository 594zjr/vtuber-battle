using System.Collections.Concurrent;

namespace StreamDuel.Core
{
    public static class LoopbackPair
    {
        public static void Create(out INetTransport a, out INetTransport b)
        {
            var toA = new ConcurrentQueue<string>();
            var toB = new ConcurrentQueue<string>();
            a = new Endpoint(toA, toB);
            b = new Endpoint(toB, toA);
        }

        sealed class Endpoint : INetTransport
        {
            readonly ConcurrentQueue<string> incoming;
            readonly ConcurrentQueue<string> outgoing;

            public Endpoint(ConcurrentQueue<string> incoming, ConcurrentQueue<string> outgoing)
            {
                this.incoming = incoming;
                this.outgoing = outgoing;
            }

            public void Send(string msg)
            {
                outgoing.Enqueue(msg);
            }

            public bool TryReceive(out string msg)
            {
                return incoming.TryDequeue(out msg);
            }
        }
    }
}
