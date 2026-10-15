// T-31 验收测试：本机 TCP 传输上打完一局。实现后把本文件移到 Engine.Tests/ 下。
using System;
using System.Threading;
using StreamDuel.Core;
using Xunit;

public class T31_TcpTransportTests
{
    [Fact]
    public void HostAndGuestPlayOverLocalhost()
    {
        using (var listener = TcpTransport.Listen(0, out int port))
        {
            TcpTransport guestT = null;
            var connect = new Thread(() => guestT = TcpTransport.Connect("127.0.0.1", port, 5000));
            connect.Start();
            var hostT = listener.Accept(5000);
            connect.Join(5000);
            Assert.NotNull(guestT);
            using (hostT) using (guestT)
            {
                var host = new NetSession(BattleTests.LoadPack("original"), hostT, true, new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" }, "主机", 7);
                var guest = new NetSession(BattleTests.LoadPack("original"), guestT, false, new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }, "客机", null);
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline && !(host.State == NetState.Over && guest.State == NetState.Over))
                {
                    host.Poll(); guest.Poll();
                    foreach (var s in new[] { host, guest })
                    {
                        if (s.State == NetState.Choose && !s.HasSubmitted) s.Submit(Ai.Choose(s.Battle, s.MySide));
                        if (s.State == NetState.Replace && !s.HasSubmitted) s.SubmitReplace(Ai.ChooseReplacement(s.Battle, s.MySide));
                    }
                    Assert.NotEqual(NetState.Error, host.State); Assert.NotEqual(NetState.Desync, host.State);
                    Thread.Sleep(1);
                }
                Assert.Equal(NetState.Over, host.State);
                Assert.Equal(host.Battle.StateHash(), guest.Battle.StateHash());
            }
        }
    }
}
