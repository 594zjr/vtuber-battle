using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using StreamDuel.Core;
using Xunit;

public class T75_ReconnectTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    sealed class Link
    {
        public bool Broken;
    }

    sealed class CuttableTransport : INetTransport
    {
        readonly INetTransport inner;
        public readonly Link Link;
        public readonly List<string> Sent = new List<string>();
        public string CutOnType;
        public int SkipMatchingBeforeCut;
        public bool DeliverBeforeCut;
        public bool DropAll;
        public Func<string, string> Rewrite;
        public int Cuts;

        public CuttableTransport(INetTransport inner, Link link)
        {
            this.inner = inner;
            Link = link;
        }

        public void Send(string message)
        {
            Sent.Add(message);
            if (Link.Broken) throw new IOException("模拟对方已断开。");
            if (Rewrite != null) message = Rewrite(message);
            if (CutOnType != null && PayloadType(message) == CutOnType)
            {
                if (SkipMatchingBeforeCut > 0)
                {
                    SkipMatchingBeforeCut--;
                    inner.Send(message);
                    return;
                }
                CutOnType = null;
                Cuts++;
                // 写入后才报错也不能证明对端未收到，重连必须避免重复结算。
                if (DeliverBeforeCut) inner.Send(message);
                Link.Broken = true;
                throw new IOException("模拟发送期间断开。");
            }
            if (!DropAll) inner.Send(message);
        }

        public bool TryReceive(out string message)
        {
            // 与 TCP 一样先排空断开前已完整送达的消息。
            if (inner.TryReceive(out message)) return true;
            if (Link.Broken) throw new IOException("模拟对方已断开。");
            return false;
        }
    }

    sealed class Pair
    {
        public CuttableTransport A, B;
        public readonly NetSession Host, Guest;

        public Pair(bool reconnect = true)
        {
            NewTransport(out A, out B);
            var db = BattleTests.LoadPack("original");
            if (reconnect)
            {
                Host = new NetSession(db, A, true, HostTeam, "主机", 19, true);
                Guest = new NetSession(db, B, false, GuestTeam, "客机", null, true);
            }
            else
            {
                Host = new NetSession(db, A, true, HostTeam, "主机", 19);
                Guest = new NetSession(db, B, false, GuestTeam, "客机", null);
            }
            Pump();
            Assert.Equal(NetState.Choose, Host.State);
            Assert.Equal(NetState.Choose, Guest.State);
        }

        public void Pump(int count = 12)
        {
            for (int i = 0; i < count; i++)
            {
                Host.Poll();
                Guest.Poll();
            }
        }

        public void PassTurn()
        {
            Host.Submit(BattleAction.Pass());
            Guest.Submit(BattleAction.Pass());
            Pump();
            AssertHealthy(Host);
            AssertHealthy(Guest);
        }

        public void Disconnect()
        {
            A.Link.Broken = true;
            Pump();
            AssertWaitingForReconnect(Host);
            AssertWaitingForReconnect(Guest);
        }

        public void Attach(Action<CuttableTransport, CuttableTransport> configure = null)
        {
            NewTransport(out A, out B);
            configure?.Invoke(A, B);
            Host.Reconnect(A, Host.SessionId);
            Guest.Reconnect(B, Guest.SessionId);
        }

        public void Recover()
        {
            Attach();
            Pump();
            AssertRecovered(Host);
            AssertRecovered(Guest);
        }
    }

    static void NewTransport(out CuttableTransport a, out CuttableTransport b)
    {
        LoopbackPair.Create(out var left, out var right);
        var link = new Link();
        a = new CuttableTransport(left, link);
        b = new CuttableTransport(right, link);
    }

    static string PayloadType(string message)
    {
        using var json = JsonDocument.Parse(message);
        string type = json.RootElement.GetProperty("type").GetString();
        if (type != "packet") return type;
        using var payload = JsonDocument.Parse(json.RootElement.GetProperty("payload").GetString());
        return payload.RootElement.GetProperty("type").GetString();
    }

    static string RewriteControl(string message, string type, string field, string value)
    {
        using var json = JsonDocument.Parse(message);
        if (json.RootElement.GetProperty("type").GetString() != type) return message;
        var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
        fields[field] = value;
        return JsonSerializer.Serialize(fields);
    }

    static string OtherToken(string token)
    {
        return (token[0] == '0' ? "1" : "0") + token.Substring(1);
    }

    static void AssertHealthy(NetSession session)
    {
        Assert.True(session.State != NetState.Error && session.State != NetState.Desync,
            session.Error ?? session.State.ToString());
    }

    static void AssertWaitingForReconnect(NetSession session)
    {
        AssertHealthy(session);
        Assert.True(session.IsDisconnected);
        Assert.Equal(NetState.Waiting, session.State);
    }

    static void AssertRecovered(NetSession session)
    {
        AssertHealthy(session);
        Assert.False(session.IsDisconnected);
        Assert.False(session.IsReconnecting);
    }

    static string FullLog(Battle battle)
    {
        return JsonSerializer.Serialize(battle.Log, new JsonSerializerOptions { IncludeFields = true });
    }

    static void AssertSameBattle(Battle expected, Battle actual)
    {
        Assert.Equal(expected.StateHash(), actual.StateHash());
        Assert.Equal(expected.Winner, actual.Winner);
        Assert.Equal(expected.Record.ToJson(), actual.Record.ToJson());
        Assert.Equal(FullLog(expected), FullLog(actual));
        Assert.Equal(BattleSummary.From(expected).ToText(), BattleSummary.From(actual).ToText());
    }

    static void Finish(Pair pair)
    {
        for (int step = 0; step < 5000; step++)
        {
            pair.Pump(1);
            foreach (var session in new[] { pair.Host, pair.Guest })
            {
                AssertHealthy(session);
                if (session.State == NetState.Choose && !session.HasSubmitted)
                    session.Submit(Ai.Choose(session.Battle, session.MySide));
                else if (session.State == NetState.Replace && !session.HasSubmitted)
                    session.SubmitReplace(Ai.ChooseReplacement(session.Battle, session.MySide));
            }
            if (pair.Host.State == NetState.Over && pair.Guest.State == NetState.Over) return;
        }
        Assert.Fail("恢复后的对局未在有限步数内完成。");
    }

    [Fact]
    public void DisconnectedPeersResumeTheSameBattleAndFinishWithIdenticalReplay()
    {
        var pair = new Pair();
        Assert.Matches("^[0-9a-f]{32}$", pair.Host.SessionId);
        Assert.Equal(pair.Host.SessionId, pair.Guest.SessionId);
        var hostBattle = pair.Host.Battle;
        var guestBattle = pair.Guest.Battle;
        string sessionId = pair.Host.SessionId;
        for (int turn = 0; turn < 3; turn++) pair.PassTurn();

        pair.Disconnect();
        pair.Host.Poll(12);
        pair.Guest.Poll(12);
        pair.Recover();

        Assert.Same(hostBattle, pair.Host.Battle);
        Assert.Same(guestBattle, pair.Guest.Battle);
        Assert.Equal(sessionId, pair.Host.SessionId);
        Assert.Equal(sessionId, pair.Guest.SessionId);
        Finish(pair);
        AssertSameBattle(pair.Host.Battle, pair.Guest.Battle);
        var replay = Replay.Run(BattleTests.LoadPack("original"), BattleRecord.FromJson(pair.Host.Battle.Record.ToJson()));
        AssertSameBattle(pair.Host.Battle, replay);
    }

    [Fact]
    public void SixtySecondsWithoutRecoveryEndsTheSessionAndParameterlessPollDoesNotAdvanceTime()
    {
        var pair = new Pair();
        pair.Disconnect();
        for (int i = 0; i < 100; i++) pair.Host.Poll();
        Assert.Equal(60d, pair.Host.ReconnectSecondsRemaining);
        pair.Host.Poll(59.5);
        AssertWaitingForReconnect(pair.Host);
        Assert.Equal(0.5d, pair.Host.ReconnectSecondsRemaining);

        pair.Host.Poll(0.5);

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.False(string.IsNullOrEmpty(pair.Host.Error));
    }

    [Fact]
    public void WrongLocalSessionIdIsRejectedWithoutLosingTheRecoverableSession()
    {
        var pair = new Pair();
        pair.Disconnect();
        var battle = pair.Host.Battle;
        NewTransport(out var wrong, out _);

        Assert.Throws<ArgumentException>(() => pair.Host.Reconnect(wrong, OtherToken(pair.Host.SessionId)));

        AssertWaitingForReconnect(pair.Host);
        Assert.Same(battle, pair.Host.Battle);
        pair.Recover();
        pair.PassTurn();
        AssertSameBattle(pair.Host.Battle, pair.Guest.Battle);
    }

    [Fact]
    public void ForgedSessionIdInResumeIsRejectedByThePeer()
    {
        var pair = new Pair();
        pair.Disconnect();
        string record = pair.Host.Battle.Record.ToJson();
        pair.Attach((a, b) => b.Rewrite = message =>
            RewriteControl(message, "resume", "sessionId", OtherToken(pair.Guest.SessionId)));

        pair.Pump();

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(record, pair.Host.Battle.Record.ToJson());
    }

    [Fact]
    public void ACommittedActionCannotBeChangedDuringOrAfterReconnect()
    {
        var pair = new Pair();
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Poll();
        Assert.True(pair.Host.HasSubmitted);
        pair.Disconnect();
        Assert.Throws<InvalidOperationException>(() => pair.Host.Submit(BattleAction.Skill(0)));

        pair.Recover();

        Assert.True(pair.Host.HasSubmitted);
        Assert.Throws<InvalidOperationException>(() => pair.Host.Submit(BattleAction.Skill(0)));
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        AssertRecovered(pair.Host);
        AssertRecovered(pair.Guest);
        Assert.Equal("P", Assert.Single(pair.Host.Battle.Record.Events).A0.ToWire());
        AssertSameBattle(pair.Host.Battle, pair.Guest.Battle);
    }

    [Fact]
    public void ChangingTheJournaledRevealDuringRecoveryStillViolatesTheOriginalCommit()
    {
        var pair = new Pair();
        pair.A.CutOnType = "reveal";
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        AssertWaitingForReconnect(pair.Host);
        AssertWaitingForReconnect(pair.Guest);
        Assert.Empty(pair.Guest.Battle.Record.Events);
        pair.Attach((a, b) => a.Rewrite = message =>
        {
            if (PayloadType(message) != "reveal") return message;
            var packet = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
            var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(packet["payload"].ToString());
            payload["action"] = "S0";
            packet["payload"] = JsonSerializer.Serialize(payload);
            return JsonSerializer.Serialize(packet);
        });

        pair.Pump();

        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Contains("承诺", pair.Guest.Error);
        Assert.Empty(pair.Guest.Battle.Record.Events);
    }

    [Theory]
    [InlineData("reveal", false)]
    [InlineData("reveal", true)]
    [InlineData("hash", false)]
    [InlineData("hash", true)]
    public void InterruptedRevealOrHashIsRecoveredExactlyOnce(string type, bool delivered)
    {
        var pair = new Pair();
        var expected = pair.Host.Battle.Clone();
        expected.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        var beforeHost = pair.Host.Battle;
        var beforeGuest = pair.Guest.Battle;
        var interrupted = pair.A;
        interrupted.CutOnType = type;
        interrupted.DeliverBeforeCut = delivered;
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        Assert.Equal(1, interrupted.Cuts);
        AssertWaitingForReconnect(pair.Host);
        AssertWaitingForReconnect(pair.Guest);

        pair.Recover();

        Assert.Same(beforeHost, pair.Host.Battle);
        Assert.Same(beforeGuest, pair.Guest.Battle);
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Single(pair.Host.Battle.Record.Events);
        AssertSameBattle(expected, pair.Host.Battle);
        AssertSameBattle(expected, pair.Guest.Battle);
        pair.PassTurn();
        Assert.Equal(2, pair.Host.Battle.Record.Events.Count);
        AssertSameBattle(pair.Host.Battle, pair.Guest.Battle);
    }

    [Fact]
    public void TamperedResumeCheckpointIsDesyncRatherThanSilentlyReplacingLocalState()
    {
        var pair = new Pair();
        pair.PassTurn();
        pair.Disconnect();
        string record = pair.Host.Battle.Record.ToJson();
        string log = FullLog(pair.Host.Battle);
        string wrongHash = OtherToken(pair.Host.Battle.StateHash().ToString("x16"));
        pair.Attach((a, b) => b.Rewrite = message => RewriteControl(message, "resume", "hash", wrongHash));

        pair.Pump();

        Assert.Equal(NetState.Desync, pair.Host.State);
        Assert.Equal(record, pair.Host.Battle.Record.ToJson());
        Assert.Equal(log, FullLog(pair.Host.Battle));
    }

    [Fact]
    public void FailedReconnectDoesNotRestartTheOriginalSixtySecondWindow()
    {
        var pair = new Pair();
        pair.Disconnect();
        pair.Host.Poll(40);
        NewTransport(out var candidate, out _);
        candidate.DropAll = true;
        pair.Host.Reconnect(candidate, pair.Host.SessionId);
        Assert.True(pair.Host.IsReconnecting);
        AssertWaitingForReconnect(pair.Host);
        pair.Host.Poll(5);
        Assert.Equal(15d, pair.Host.ReconnectSecondsRemaining);
        candidate.Link.Broken = true;
        pair.Host.Poll();
        AssertWaitingForReconnect(pair.Host);
        Assert.Equal(15d, pair.Host.ReconnectSecondsRemaining);

        pair.Host.Poll(15);

        Assert.Equal(NetState.Error, pair.Host.State);
    }

    [Fact]
    public void ACompletedRecoveryAllowsAFreshTimeoutForALaterDisconnect()
    {
        var pair = new Pair();
        pair.Disconnect();
        pair.Host.Poll(45);
        pair.Guest.Poll(45);
        pair.Recover();
        pair.PassTurn();

        pair.Disconnect();

        Assert.Equal(60d, pair.Host.ReconnectSecondsRemaining);
        pair.Host.Poll(59);
        AssertWaitingForReconnect(pair.Host);
        pair.Host.Poll(1);
        Assert.Equal(NetState.Error, pair.Host.State);
    }

    [Fact]
    public void LocalRngConsumptionWhileDisconnectedDoesNotCorruptResumeHash()
    {
        var pair = new Pair();
        pair.PassTurn();
        pair.Disconnect();
        for (int i = 0; i < 17; i++) pair.Host.Battle.Random();
        for (int i = 0; i < 3; i++) pair.Guest.Battle.Random();

        pair.Recover();
        pair.PassTurn();

        AssertSameBattle(pair.Host.Battle, pair.Guest.Battle);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TwoReplacementsInTheSameTurnRetainBothHashCheckpoints(int interruptedHash)
    {
        var pair = new Pair();
        foreach (var battle in new[] { pair.Host.Battle, pair.Guest.Battle })
            for (int side = 0; side < 2; side++)
            {
                battle.Active(side).Hp = 0;
                battle.Active(side).Fainted = true;
            }
        pair.PassTurn();
        Assert.Equal(NetState.Replace, pair.Host.State);
        Assert.Equal(NetState.Replace, pair.Guest.State);
        var expected = pair.Host.Battle.Clone();
        expected.Replace(0, 1);
        expected.Replace(1, 1);
        var interrupted = pair.A;
        interrupted.CutOnType = "hash";
        interrupted.SkipMatchingBeforeCut = interruptedHash - 1;
        // 第一条可已被确认，第二条仍必须保留，不能按相同回合号覆盖。
        interrupted.DeliverBeforeCut = interruptedHash == 1;
        pair.Host.SubmitReplace(1);
        pair.Guest.SubmitReplace(1);
        pair.Pump();
        Assert.Equal(1, interrupted.Cuts);
        AssertWaitingForReconnect(pair.Host);
        AssertWaitingForReconnect(pair.Guest);

        pair.Recover();

        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Equal(2, pair.Host.Battle.Record.Events.Count(e => e.Type == BattleRecordEventType.Replace));
        AssertSameBattle(expected, pair.Host.Battle);
        AssertSameBattle(expected, pair.Guest.Battle);
        pair.PassTurn();
        Assert.Equal(4, pair.Host.Battle.Record.Events.Count);
        AssertSameBattle(pair.Host.Battle, pair.Guest.Battle);
    }

    [Fact]
    public void LostFinalHashCanBeRecoveredWithoutResolvingTheFinalTurnAgain()
    {
        var pair = new Pair();
        pair.Host.Battle.Turn = Battle.TurnCap;
        pair.Guest.Battle.Turn = Battle.TurnCap;
        var expected = pair.Host.Battle.Clone();
        expected.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        pair.A.CutOnType = "hash";
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        Assert.Equal(Phase.Over, pair.Host.Battle.Phase);
        Assert.Equal(Phase.Over, pair.Guest.Battle.Phase);
        AssertWaitingForReconnect(pair.Host);
        AssertWaitingForReconnect(pair.Guest);
        Assert.Throws<InvalidOperationException>(() => pair.Host.PrepareClose());

        pair.Recover();

        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(NetState.Over, pair.Guest.State);
        Assert.Single(pair.Host.Battle.Record.Events);
        AssertSameBattle(expected, pair.Host.Battle);
        AssertSameBattle(expected, pair.Guest.Battle);
    }

    [Fact]
    public void PrepareCloseAllowsNormalTcpEofAfterBothFinalHashesWereConfirmed()
    {
        using var listener = TcpTransport.Listen(0, out int port);
        using var guestTransport = TcpTransport.Connect("127.0.0.1", port, 5000);
        using var hostTransport = listener.Accept(5000);
        Assert.NotNull(hostTransport);
        var db = BattleTests.LoadPack("original");
        var host = new NetSession(db, hostTransport, true, HostTeam, "主机", 19, true);
        var guest = new NetSession(db, guestTransport, false, GuestTeam, "客机", null, true);
        Action pump = () =>
        {
            host.Poll();
            guest.Poll();
            AssertHealthy(host);
            AssertHealthy(guest);
        };
        Until(() => host.State == NetState.Choose && guest.State == NetState.Choose, pump);
        host.Battle.Turn = Battle.TurnCap;
        guest.Battle.Turn = Battle.TurnCap;
        host.Submit(BattleAction.Pass());
        guest.Submit(BattleAction.Pass());
        Until(() => host.State == NetState.Over && guest.State == NetState.Over, pump);

        host.PrepareClose();
        host.PrepareClose();
        hostTransport.CompleteSending();
        Until(() => guestTransport.PeerClosed, pump);
        pump();
        AssertRecovered(guest);
        Assert.Equal(NetState.Over, guest.State);
        guest.PrepareClose();
        guestTransport.CompleteSending();
        Until(() => hostTransport.PeerClosed, pump);
        pump();

        AssertRecovered(host);
        Assert.Equal(NetState.Over, host.State);
        AssertSameBattle(host.Battle, guest.Battle);
    }

    [Theory]
    [InlineData(true, "incomplete")]
    [InlineData(false, "incomplete")]
    [InlineData(true, "malformed")]
    [InlineData(true, "utf8")]
    public void OnlyIncompleteTcpLinesPauseForReconnectAndLegacyStillErrors(bool enableReconnect, string tail)
    {
        using var listener = TcpTransport.Listen(0, out int port);
        using var peer = new TcpClient();
        peer.Connect("127.0.0.1", port);
        using var transport = listener.Accept(5000);
        Assert.NotNull(transport);
        var db = BattleTests.LoadPack("original");
        var host = enableReconnect
            ? new NetSession(db, transport, true, HostTeam, "主机", 19, true)
            : new NetSession(db, transport, true, HostTeam, "主机", 19);
        string hello = JsonSerializer.Serialize(new
        {
            type = "hello",
            version = enableReconnect ? NetSession.ReconnectProtocolVersion : NetSession.ProtocolVersion,
            sessionId = (string)null,
            packId = db.PackId,
            contentHash = db.ContentHash(),
            seed = (uint?)null,
            name = "客机",
            team = GuestTeam
        });
        var stream = peer.GetStream();
        byte[] greeting = Encoding.UTF8.GetBytes(hello + "\n");
        stream.Write(greeting, 0, greeting.Length);
        Until(() => host.State == NetState.Choose, () =>
        {
            host.Poll();
            AssertHealthy(host);
        });
        var expected = host.Battle.Clone();
        // 只有正常 UTF-8 尚未收满一行时允许补发；完整坏 JSON 和坏编码仍须终止。
        byte[] broken = tail == "utf8" ? new byte[] { 0xff, 0x0a }
            : Encoding.UTF8.GetBytes(tail == "incomplete" ? "{\"type\":\"packet\",\"sessionId\":" : "{not-json}\n");

        stream.Write(broken, 0, broken.Length);
        peer.Client.Shutdown(SocketShutdown.Send);
        Until(() => host.IsDisconnected || host.State == NetState.Error, host.Poll);

        if (enableReconnect && tail == "incomplete")
        {
            AssertWaitingForReconnect(host);
            Assert.False(host.IsReconnecting);
            Assert.Equal(60d, host.ReconnectSecondsRemaining);
            Assert.Null(host.Error);
        }
        else
        {
            Assert.Equal(NetState.Error, host.State);
            Assert.False(host.IsDisconnected);
            Assert.False(string.IsNullOrEmpty(host.Error));
        }
        AssertSameBattle(expected, host.Battle);
    }

    static void Until(Func<bool> done, Action poll)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            poll();
            if (!done()) Thread.Sleep(1);
        }
        Assert.True(done(), "本机连接未在有限等待内完成预期状态变化。");
    }

    [Fact]
    public void LegacySixArgumentSessionsStillFailImmediatelyOnDisconnect()
    {
        var pair = new Pair(reconnect: false);
        pair.A.Link.Broken = true;

        pair.Pump();

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Contains("断开", pair.Host.Error);
        Assert.Contains("断开", pair.Guest.Error);
    }
}
