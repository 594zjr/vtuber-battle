using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamDuel.Core;
using Xunit;

public class T31_TcpTransportEdgeTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    sealed class TcpPair : IDisposable
    {
        public readonly TcpTransport Host;
        public readonly TcpTransport Guest;

        public TcpPair()
        {
            using (var listener = TcpTransport.Listen(0, out int port))
            {
                // 已监听端口的握手不依赖 Accept，避免测试后台线程异常逃逸。
                Guest = TcpTransport.Connect("127.0.0.1", port, 5000);
                try
                {
                    Host = listener.Accept(5000);
                    Assert.NotNull(Host);
                }
                catch
                {
                    Guest.Dispose();
                    throw;
                }
            }
        }

        public void Dispose()
        {
            Host.Dispose();
            Guest.Dispose();
        }
    }

    sealed class ObservedTransport : INetTransport
    {
        readonly TcpTransport inner;
        public bool HoldHashes;
        public int HeldHashes;
        public long LastSentSequence;
        public bool HoldIncomingHashes;
        public string HeldIncomingHash;
        public IOException ReceiveFailure;
        string releasedIncomingHash;

        public ObservedTransport(TcpTransport inner)
        {
            this.inner = inner;
        }

        public void Send(string message)
        {
            LastSentSequence = Math.Max(LastSentSequence, NetWireTestTransport.Sequence(message));
            if (HoldHashes)
            {
                using (var json = JsonDocument.Parse(message))
                {
                    if (json.RootElement.GetProperty("type").GetString() == "hash")
                    {
                        HeldHashes++;
                        return;
                    }
                }
            }
            inner.Send(message);
        }

        public bool TryReceive(out string message)
        {
            if (releasedIncomingHash != null)
            {
                message = releasedIncomingHash;
                releasedIncomingHash = null;
                return true;
            }
            if (HeldIncomingHash != null)
            {
                message = null;
                return false;
            }
            try
            {
                if (!inner.TryReceive(out message)) return false;
                if (HoldIncomingHashes)
                {
                    using (var json = JsonDocument.Parse(message))
                    {
                        if (json.RootElement.GetProperty("type").GetString() == "hash")
                        {
                            HeldIncomingHash = message;
                            message = null;
                            return false;
                        }
                    }
                }
                return true;
            }
            catch (IOException ex)
            {
                ReceiveFailure = ex;
                throw;
            }
        }

        public void ReleaseIncomingHash(Func<string, string> transform)
        {
            Assert.NotNull(HeldIncomingHash);
            releasedIncomingHash = transform(HeldIncomingHash);
            HeldIncomingHash = null;
            HoldIncomingHashes = false;
        }
    }

    sealed class SessionPair : IDisposable
    {
        public readonly TcpPair Tcp = new TcpPair();
        public readonly ObservedTransport HostTransport;
        public readonly ObservedTransport GuestTransport;
        public readonly NetSession Host;
        public readonly NetSession Guest;

        public SessionPair()
        {
            try
            {
                HostTransport = new ObservedTransport(Tcp.Host);
                GuestTransport = new ObservedTransport(Tcp.Guest);
                Host = new NetSession(BattleTests.LoadPack("original"), HostTransport, true, HostTeam, "主机😀", 31);
                Guest = new NetSession(BattleTests.LoadPack("original"), GuestTransport, false, GuestTeam, "客机中文", null);
                Until(() => Host.State == NetState.Choose && Guest.State == NetState.Choose, Pump);
            }
            catch
            {
                Tcp.Dispose();
                throw;
            }
        }

        public void Pump()
        {
            Host.Poll();
            Guest.Poll();
            Assert.NotEqual(NetState.Error, Host.State);
            Assert.NotEqual(NetState.Error, Guest.State);
            Assert.NotEqual(NetState.Desync, Host.State);
            Assert.NotEqual(NetState.Desync, Guest.State);
        }

        public void SubmitFinalTurn()
        {
            Host.Battle.Turn = Battle.TurnCap;
            Guest.Battle.Turn = Battle.TurnCap;
            Host.Submit(BattleAction.Pass());
            Guest.Submit(BattleAction.Pass());
        }

        public void Finish()
        {
            SubmitFinalTurn();
            Until(() => Host.State == NetState.Over && Guest.State == NetState.Over
                && !Host.HasPendingReliableMessages && !Guest.HasPendingReliableMessages, Pump);
            Assert.Equal(Host.Battle.StateHash(), Guest.Battle.StateHash());
        }

        public void Dispose()
        {
            Tcp.Dispose();
        }
    }

    static void Until(Func<bool> complete, Action poll)
    {
        var timer = Stopwatch.StartNew();
        while (!complete() && timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            poll();
            if (!complete()) Thread.Sleep(2);
        }
        Assert.True(complete(), "在宽松的本机等待上限内没有完成预期状态变化。");
    }

    static string[] Receive(TcpTransport transport, int count)
    {
        var messages = new List<string>();
        Until(() => messages.Count == count, () =>
        {
            string message;
            if (transport.TryReceive(out message)) messages.Add(message);
        });
        return messages.ToArray();
    }

    [Fact]
    public void Utf8LinesRemainFifoInBothDirections()
    {
        using (var pair = new TcpPair())
        {
            var outgoing = Enumerable.Range(0, 32)
                .Select(index => "第" + index + "行：中文😀，转义换行\\n和引号\"").ToArray();
            // 跨越常见缓冲区大小，让多字节字符也经过分段读取。
            outgoing[15] = string.Concat(Enumerable.Repeat("汉字😀", 4096));
            foreach (string message in outgoing) pair.Host.Send(message);
            Assert.Equal(outgoing, Receive(pair.Guest, outgoing.Length));

            var replies = outgoing.Reverse().ToArray();
            foreach (string message in replies) pair.Guest.Send(message);
            Assert.Equal(replies, Receive(pair.Host, replies.Length));
        }
    }

    [Fact]
    public async Task WireUsesUtf8NewlineFramingWithAnIndependentPeer()
    {
        using (var listener = TcpTransport.Listen(0, out int port))
        using (var peer = new TcpClient())
        {
            await peer.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(10));
            using (var host = listener.Accept(5000))
            {
                Assert.NotNull(host);
                var stream = peer.GetStream();
                byte[] payload = Encoding.UTF8.GetBytes("第一行😀\n第二行中文\n");
                await stream.WriteAsync(payload, 0, payload.Length);
                Assert.Equal(new[] { "第一行😀", "第二行中文" }, Receive(host, 2));

                host.Send("回复😀");
                byte[] expected = Encoding.UTF8.GetBytes("回复😀\n");
                var received = new byte[expected.Length];
                await stream.ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(expected, received);
            }
        }
    }

    [Fact]
    public async Task Utf8LineFillingTheByteBufferArrivesWithoutAnotherMessage()
    {
        using (var listener = TcpTransport.Listen(0, out int port))
        using (var peer = new TcpClient())
        {
            await peer.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(10));
            using (var host = listener.Accept(5000))
            {
                Assert.NotNull(host);
                string message = new string('汉', 1365);
                byte[] payload = Encoding.UTF8.GetBytes(message + "\n");
                Assert.Equal(4096, payload.Length);
                await peer.GetStream().WriteAsync(payload, 0, payload.Length);

                // 保持连接且不发送下一行，暴露字符读取等待填满缓冲区的问题。
                Assert.Equal(new[] { message }, Receive(host, 1));
            }
        }
    }

    [Fact]
    public async Task TryReceiveWithoutDataReturnsWithoutWaitingForThePeer()
    {
        using (var pair = new TcpPair())
        {
            var receive = Task.Run(() => Record.Exception(() =>
            {
                for (int index = 0; index < 100; index++)
                {
                    string message;
                    Assert.False(pair.Host.TryReceive(out message));
                }
            }));
            Assert.Null(await receive.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task AcceptTimeoutDoesNotLoseTheNextConnection()
    {
        using (var listener = TcpTransport.Listen(0, out int port))
        {
            TcpTransport unexpected = null;
            var firstAccept = Task.Run(() => Record.Exception(() =>
            {
                unexpected = listener.Accept(25);
            }));
            try
            {
                Assert.Null(await firstAccept.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Null(unexpected);
                using (var guest = TcpTransport.Connect("127.0.0.1", port, 5000))
                using (var host = listener.Accept(5000))
                {
                    Assert.NotNull(host);
                    guest.Send("超时后的连接仍可使用😀");
                    Assert.Equal(new[] { "超时后的连接仍可使用😀" }, Receive(host, 1));
                }
            }
            finally
            {
                unexpected?.Dispose();
            }
        }
    }

    [Fact]
    public async Task RefusedLoopbackConnectionFailsWithChineseMessageAndFiniteWait()
    {
        // 只绑定不监听，保留动态端口，避免释放端口后被其他测试抢占。
        using (var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            reserved.ExclusiveAddressUse = true;
            reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)reserved.LocalEndPoint).Port;
            var connect = Task.Run(() => Record.Exception(() =>
            {
                using (var transport = TcpTransport.Connect("127.0.0.1", port, 150)) { }
            }));
            var error = await connect.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(error);
            Assert.Matches("[\u4e00-\u9fff]", error.Message);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClosingEitherPeerDuringPlayReportsDisconnect(bool closeHost)
    {
        using (var pair = new SessionPair())
        {
            var closed = closeHost ? pair.Tcp.Host : pair.Tcp.Guest;
            var survivor = closeHost ? pair.Guest : pair.Host;
            closed.Dispose();
            closed.Dispose();

            Until(() => survivor.State == NetState.Error, survivor.Poll);
            Assert.Contains("对方已断开", survivor.Error);
        }
    }

    [Fact]
    public void DisconnectWhileWaitingForHelloDoesNotLeaveHandshakeHanging()
    {
        using (var pair = new TcpPair())
        {
            var host = new NetSession(BattleTests.LoadPack("original"), pair.Host, true, HostTeam, "主机", 31);
            pair.Guest.Dispose();

            Until(() => host.State == NetState.Error, host.Poll);
            Assert.Contains("对方已断开", host.Error);
        }
    }

    [Fact]
    public void TerminalBattleStillReportsDisconnectUntilItsFinalHashIsConfirmed()
    {
        using (var pair = new SessionPair())
        {
            pair.HostTransport.HoldHashes = true;
            pair.GuestTransport.HoldHashes = true;
            pair.SubmitFinalTurn();
            Until(() => pair.Host.Battle.Phase == Phase.Over && pair.Guest.Battle.Phase == Phase.Over, pair.Pump);
            Assert.Equal(NetState.Waiting, pair.Host.State);
            Assert.Equal(NetState.Waiting, pair.Guest.State);
            Assert.True(pair.HostTransport.HeldHashes > 0);
            Assert.True(pair.GuestTransport.HeldHashes > 0);

            pair.Tcp.Guest.Dispose();
            Until(() => pair.Host.State == NetState.Error, pair.Host.Poll);
            Assert.Contains("对方已断开", pair.Host.Error);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfirmedGameOverSurvivesNormalPeerClose(bool closeHost)
    {
        using (var pair = new SessionPair())
        {
            pair.Finish();
            var survivor = closeHost ? pair.Guest : pair.Host;
            var observed = closeHost ? pair.GuestTransport : pair.HostTransport;
            (closeHost ? pair.Tcp.Host : pair.Tcp.Guest).Dispose();

            // 等到实际观察到 EOF，避免只在读线程尚未发现断线时断言终局。
            Until(() => observed.ReceiveFailure != null, survivor.Poll);
            Assert.Contains("对方已断开", observed.ReceiveFailure.Message);
            Assert.Equal(NetState.Over, survivor.State);
            Assert.Null(survivor.Error);
            survivor.Poll();
            Assert.Equal(NetState.Over, survivor.State);
        }
    }

    [Fact]
    public void QueuedErrorAfterGameOverIsReceivedBeforePeerEof()
    {
        using (var pair = new SessionPair())
        {
            pair.Finish();
            pair.Tcp.Guest.Send(NetWireTestTransport.WithSequence("{\"type\":\"error\",\"message\":\"终局校验失败\"}", pair.GuestTransport.LastSentSequence + 1));
            pair.Tcp.Guest.Dispose();

            Until(() => pair.Host.State == NetState.Error, pair.Host.Poll);
            Assert.Contains("终局校验失败", pair.Host.Error);
        }
    }

    [Fact]
    public void PeerCanStillSendErrorAfterObservingTheOtherSidesHalfClose()
    {
        using (var pair = new TcpPair())
        {
            pair.Host.Send("关闭发送前的最后一行😀");
            pair.Host.CompleteSending();
            pair.Host.CompleteSending();
            Until(() => pair.Guest.PeerClosed, () => { });

            Assert.Equal(new[] { "关闭发送前的最后一行😀" }, Receive(pair.Guest, 1));
            var eof = Assert.ThrowsAny<IOException>(() =>
            {
                pair.Guest.TryReceive(out _);
            });
            Assert.Contains("对方已断开", eof.Message);

            const string error = "{\"type\":\"error\",\"message\":\"终局校验失败\"}";
            pair.Guest.Send(error);
            Assert.Equal(new[] { error }, Receive(pair.Host, 1));
        }
    }

    [Fact]
    public void BothConfirmedSessionsRemainOverAfterCompletingTheirSendDirections()
    {
        using (var pair = new SessionPair())
        {
            pair.Finish();
            pair.Tcp.Host.CompleteSending();
            Until(() => pair.Tcp.Guest.PeerClosed, pair.Pump);
            pair.Guest.Poll();
            Assert.Equal(NetState.Over, pair.Guest.State);

            pair.Tcp.Guest.CompleteSending();
            Until(() => pair.Tcp.Host.PeerClosed, pair.Pump);
            pair.Pump();
            Assert.NotNull(pair.HostTransport.ReceiveFailure);
            Assert.NotNull(pair.GuestTransport.ReceiveFailure);
            Assert.Equal(NetState.Over, pair.Host.State);
            Assert.Equal(NetState.Over, pair.Guest.State);
            Assert.Null(pair.Host.Error);
            Assert.Null(pair.Guest.Error);
            Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        }
    }

    [Fact]
    public void DelayedFinalHashFailureIsReportedBackAfterTheSuccessfulPeerHalfCloses()
    {
        using (var pair = new SessionPair())
        {
            // 扣住接收结果而不扣发送，保证最终哈希先于主机的 FIN 到达客机。
            pair.GuestTransport.HoldIncomingHashes = true;
            pair.SubmitFinalTurn();
            Until(() => pair.Host.State == NetState.Over && pair.GuestTransport.HeldIncomingHash != null, pair.Pump);
            Assert.Equal(NetState.Waiting, pair.Guest.State);
            Assert.Equal(Phase.Over, pair.Guest.Battle.Phase);
            string correct = pair.Guest.Battle.StateHash().ToString("x16");
            string tampered = (correct[0] == '0' ? "1" : "0") + correct.Substring(1);
            Assert.Contains(correct, pair.GuestTransport.HeldIncomingHash);

            pair.Tcp.Host.CompleteSending();
            Until(() => pair.Tcp.Guest.PeerClosed, pair.Pump);
            Assert.Equal(NetState.Over, pair.Host.State);
            Assert.Equal(NetState.Waiting, pair.Guest.State);

            pair.GuestTransport.ReleaseIncomingHash(message => message.Replace(correct, tampered));
            Until(() => pair.Guest.State == NetState.Desync, pair.Guest.Poll);
            Assert.Contains(correct, pair.Guest.Error);
            Assert.Contains(tampered, pair.Guest.Error);
            Until(() => pair.Host.State == NetState.Error, pair.Host.Poll);
            Assert.Contains(pair.Guest.Error, pair.Host.Error);
            Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        }
    }
}
