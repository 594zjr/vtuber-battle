using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T96_LossyNetTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    [Fact]
    public void TwentyMatchesFinishThroughLossDelayReorderingAndDuplicatesWithoutChangingBattleRandomness()
    {
        long dropped = 0;
        long duplicated = 0;
        for (uint game = 1; game <= 20; game++)
        {
            uint seed = 9600 + game;
            var lossy = new LossyPair(seed, 100 + game, 200 + game, 0.2, 0.25, 1, 5);
            Finish(lossy.Host, lossy.Guest);
            var reliable = new ScriptedPair(seed);
            Finish(reliable.Host, reliable.Guest);

            Assert.Equal(reliable.Host.Battle.StateHash(), lossy.Host.Battle.StateHash());
            Assert.Equal(reliable.Host.Battle.Record.ToJson(), lossy.Host.Battle.Record.ToJson());
            Assert.Equal(Log(reliable.Host.Battle), Log(lossy.Host.Battle));
            for (int i = 0; i < 3; i++) Assert.Equal(reliable.Host.Battle.Random(), lossy.Host.Battle.Random());
            dropped += lossy.A.DroppedCount + lossy.B.DroppedCount;
            duplicated += lossy.A.DuplicatedCount + lossy.B.DuplicatedCount;
            Assert.True(lossy.A.PollCount > 0);
            Assert.True(lossy.B.PollCount > 0);
        }
        Assert.True(dropped > 0, "验收网络必须实际丢包");
        Assert.True(duplicated > 0, "验收网络必须实际产生重复包");
    }

    [Theory]
    [InlineData(1u, 96u)]
    [InlineData(2026u, 42u)]
    public void SameBattleAndNetworkSeedsReproduceResultsAndFaultSchedule(uint battleSeed, uint networkSeed)
    {
        var first = new LossyPair(battleSeed, networkSeed, networkSeed + 1, 0.2, 0.3, 1, 4);
        var second = new LossyPair(battleSeed, networkSeed, networkSeed + 1, 0.2, 0.3, 1, 4);
        int firstPolls = Finish(first.Host, first.Guest);
        int secondPolls = Finish(second.Host, second.Guest);

        Assert.Equal(firstPolls, secondPolls);
        Assert.Equal(first.Host.Battle.StateHash(), second.Host.Battle.StateHash());
        Assert.Equal(first.Host.Battle.Record.ToJson(), second.Host.Battle.Record.ToJson());
        Assert.Equal(Log(first.Host.Battle), Log(second.Host.Battle));
        Assert.Equal(Counters(first.A), Counters(second.A));
        Assert.Equal(Counters(first.B), Counters(second.B));
        // 承诺 nonce 使用密码学随机数；只比较传输调度，不能要求秘密随机串相同。
        Assert.Equal(first.HostWire.Sent.Select(WireIdentity), second.HostWire.Sent.Select(WireIdentity));
        Assert.Equal(first.GuestWire.Sent.Select(WireIdentity), second.GuestWire.Sent.Select(WireIdentity));
    }

    [Fact]
    public void CompletePacketLossTimesOutDuringHandshakeWithoutCreatingBattle()
    {
        var pair = new LossyPair(96, 1, 2, 1, 0, 0, 0);
        for (int i = 0; i < NetSession.DeliveryTimeoutPolls - 1; i++) Pump(pair.Host, pair.Guest);
        Assert.Equal(NetState.Handshake, pair.Host.State);
        Assert.Equal(NetState.Handshake, pair.Guest.State);

        Pump(pair.Host, pair.Guest);

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        AssertDeliveryTimeout(pair.Host);
        AssertDeliveryTimeout(pair.Guest);
        Assert.Null(pair.Host.Battle);
        Assert.Null(pair.Guest.Battle);
        Assert.True(pair.A.DroppedCount > 1);
        Assert.True(pair.B.DroppedCount > 1);
    }

    [Fact]
    public void CompleteStreamCutAfterHandshakeTimesOutWithoutResolvingUnrevealedActions()
    {
        var pair = Ready();
        pair.A.Drop = _ => true;
        pair.B.Drop = _ => true;
        ulong hash = pair.Host.Battle.StateHash();
        pair.Host.Submit(BattleAction.Skill(0));
        pair.Guest.Submit(BattleAction.Skill(0));

        for (int i = 0; i < NetSession.DeliveryTimeoutPolls + 4; i++) pair.Pump();

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        AssertDeliveryTimeout(pair.Host);
        AssertDeliveryTimeout(pair.Guest);
        Assert.Equal(hash, pair.Host.Battle.StateHash());
        Assert.Equal(hash, pair.Guest.Battle.StateHash());
        Assert.Empty(pair.Host.Battle.Record.Events);
        Assert.Empty(pair.Guest.Battle.Record.Events);
    }

    [Fact]
    public void AcknowledgedLocalHelloStillTimesOutWhenPeerHelloNeverArrives()
    {
        var pair = new ScriptedPair(configure: (host, guest) =>
            guest.Drop = message => Type(message) == "hello");
        pair.Pump();
        pair.Host.Poll();
        Assert.Equal(NetState.Handshake, pair.Host.State);
        Assert.Null(pair.Host.Battle);
        Assert.Equal(0, pair.Host.PendingReliableCount);
        Assert.False(pair.Host.HasPendingReliableMessages);
        Assert.Contains(pair.B.Messages("ack"), message => Ack(message) == Sequence(pair.A.Messages("hello").First()));

        for (int i = 0; i < NetSession.DeliveryTimeoutPolls; i++) pair.Host.Poll();

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains(NetSession.DeliveryTimeoutPolls.ToString(), pair.Host.Error);
        Assert.Null(pair.Host.Battle);
    }

    [Fact]
    public void AcknowledgedLocalRevealStillTimesOutWhenBothCommittedButPeerNeverReveals()
    {
        var pair = Ready();
        pair.B.Drop = message => Type(message) == "reveal" || Type(message) == "hash";
        ulong hash = pair.Host.Battle.StateHash();
        string log = Log(pair.Host.Battle);
        pair.Host.Submit(BattleAction.Skill(0));
        pair.Guest.Submit(BattleAction.Skill(0));
        for (int i = 0; i < 3; i++) pair.Pump();
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Equal(0, pair.Host.PendingReliableCount);
        Assert.False(pair.Host.HasPendingReliableMessages);
        string reveal = Assert.Single(pair.A.Messages("reveal"));
        Assert.Contains(pair.B.Messages("ack"), message => Ack(message) >= Sequence(reveal));
        Assert.Single(pair.B.Messages("commit"));
        Assert.NotEmpty(pair.B.Messages("reveal"));
        Assert.Empty(pair.Host.Battle.Record.Events);

        for (int i = 0; i < NetSession.DeliveryTimeoutPolls; i++) pair.Host.Poll();

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains(NetSession.DeliveryTimeoutPolls.ToString(), pair.Host.Error);
        Assert.Equal(hash, pair.Host.Battle.StateHash());
        Assert.Equal(log, Log(pair.Host.Battle));
        Assert.Empty(pair.Host.Battle.Record.Events);
    }

    [Fact]
    public void MissingSequenceTimesOutEvenWithOnlyAReceiveBufferAndNoUnacknowledgedSends()
    {
        var pair = Ready();
        int next = pair.B.Sent.Where(message => Type(message) != "ack").Max(Sequence) + 1;
        pair.A.Inject("{\"type\":\"chat\",\"index\":0,\"seq\":" + (next + 1) + "}");
        ulong hash = pair.Host.Battle.StateHash();
        string record = pair.Host.Battle.Record.ToJson();
        pair.Host.Poll();
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(0, pair.Host.PendingReliableCount);
        Assert.True(pair.Host.HasPendingReliableMessages);
        Assert.Empty(pair.Host.ReceivedChats);

        for (int i = 0; i < NetSession.DeliveryTimeoutPolls; i++) pair.Host.Poll();

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains(NetSession.DeliveryTimeoutPolls.ToString(), pair.Host.Error);
        Assert.Equal(hash, pair.Host.Battle.StateHash());
        Assert.Equal(record, pair.Host.Battle.Record.ToJson());
        Assert.Empty(pair.Host.ReceivedChats);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void LostHelloAckIsRecoveredWithoutRepeatingHandshakeOrCreatingAnAckLoop(int ackSender)
    {
        var pair = new ScriptedPair();
        var sender = ackSender == 0 ? pair.A : pair.B;
        var retried = ackSender == 0 ? pair.B : pair.A;
        bool dropped = false;
        sender.Drop = message => Type(message) == "ack" && !dropped && (dropped = true);
        pair.Pump();
        Assert.True(dropped);
        ulong hash = pair.Host.Battle.StateHash();

        for (int i = 0; i < NetSession.ResendAfterPolls * 3; i++) pair.Pump();

        AssertReady(pair);
        Assert.True(retried.Messages("hello").Count() >= 2);
        Assert.Single(retried.Messages("hello").Distinct());
        Assert.Equal(hash, pair.Host.Battle.StateHash());
        Assert.Equal(hash, pair.Guest.Battle.StateHash());
        int sent = pair.A.Sent.Count + pair.B.Sent.Count;
        for (int i = 0; i < NetSession.ResendAfterPolls * 2; i++) pair.Pump();
        Assert.Equal(sent, pair.A.Sent.Count + pair.B.Sent.Count);
    }

    [Fact]
    public void ReorderedRevealWaitsForMissingCommitAndDuplicateMessagesResolveOnlyOnce()
    {
        var pair = Ready();
        pair.A.Hold = message => Type(message) == "commit" || Type(message) == "reveal";
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        pair.Host.Poll();
        string commit = Assert.Single(pair.A.Held, message => Type(message) == "commit");
        string reveal = Assert.Single(pair.A.Held, message => Type(message) == "reveal");
        Assert.True(Sequence(reveal) > Sequence(commit));
        pair.B.Inject(reveal);
        pair.B.Inject(reveal);

        pair.Guest.Poll();

        AssertHealthy(pair.Guest);
        Assert.Equal(NetState.Waiting, pair.Guest.State);
        Assert.Empty(pair.Guest.Battle.Record.Events);
        pair.A.Hold = null;
        pair.B.Inject(commit);
        pair.B.Inject(commit);
        for (int i = 0; i < NetSession.ResendAfterPolls * 2; i++) pair.Pump();
        AssertReady(pair);
        Assert.Single(pair.Host.Battle.Record.Events);
        Assert.Single(pair.Guest.Battle.Record.Events);
        Assert.Equal(2, pair.Host.Battle.Turn);
        ulong hash = pair.Guest.Battle.StateHash();
        string record = pair.Guest.Battle.Record.ToJson();
        string log = Log(pair.Guest.Battle);
        foreach (string message in new[] { commit, reveal, pair.A.Messages("hash").First() })
        {
            pair.B.Inject(message);
            pair.B.Inject(message);
        }
        pair.Pump();

        AssertReady(pair);
        Assert.Equal(hash, pair.Guest.Battle.StateHash());
        Assert.Equal(record, pair.Guest.Battle.Record.ToJson());
        Assert.Equal(log, Log(pair.Guest.Battle));
    }

    [Fact]
    public void NewerCumulativeAckCanArriveBeforeOlderAndDuplicateAcksWithoutRegressingProgress()
    {
        var pair = Ready();
        pair.B.Hold = message => Type(message) == "ack";
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        for (int i = 0; i < 3; i++) pair.Pump();
        string[] acks = pair.B.Held.OrderByDescending(Ack).ToArray();
        Assert.True(acks.Select(Ack).Distinct().Count() >= 2);
        Assert.True(pair.Host.HasPendingReliableMessages);

        foreach (string ack in acks)
        {
            pair.A.Inject(ack);
            pair.A.Inject(ack);
        }
        pair.B.Hold = null;
        pair.Pump();

        AssertReady(pair);
        Assert.Single(pair.Host.Battle.Record.Events);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        int sent = pair.A.Sent.Count + pair.B.Sent.Count;
        for (int i = 0; i < NetSession.ResendAfterPolls * 2; i++) pair.Pump();
        Assert.Equal(sent, pair.A.Sent.Count + pair.B.Sent.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FinalHashAckLossIsRecoveredWhileOverWithoutReResolvingTheBattle(int ackSender)
    {
        var pair = Ready();
        var sender = ackSender == 0 ? pair.A : pair.B;
        var retried = ackSender == 0 ? pair.B : pair.A;
        var pending = ackSender == 0 ? pair.Guest : pair.Host;
        bool drop = true;
        sender.Drop = message => drop && IsAckForLatestHash(message, retried);
        pair.Host.Battle.Turn = pair.Guest.Battle.Turn = Battle.TurnCap;
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        for (int i = 0; i < 4; i++) pair.Pump();
        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(NetState.Over, pair.Guest.State);
        Assert.True(pending.HasPendingReliableMessages);
        Assert.True(pending.PendingReliableCount > 0);
        ulong hash = pair.Host.Battle.StateHash();
        string record = pair.Host.Battle.Record.ToJson();
        string log = Log(pair.Host.Battle);

        drop = false;
        for (int i = 0; i < NetSession.ResendAfterPolls * 3; i++) pair.Pump();

        AssertFinished(pair.Host, pair.Guest);
        Assert.True(retried.Messages("hash").Count() >= 2);
        Assert.Single(retried.Messages("hash").Distinct());
        Assert.Equal(hash, pair.Host.Battle.StateHash());
        Assert.Equal(record, pair.Host.Battle.Record.ToJson());
        Assert.Equal(log, Log(pair.Host.Battle));
        Assert.Single(pair.Host.Battle.Record.Events);
        Assert.Single(pair.Guest.Battle.Record.Events);
    }

    [Fact]
    public void PermanentlyLostFinalAckCannotBeMistakenForSuccessfulDelivery()
    {
        var pair = Ready();
        pair.A.Drop = message => IsAckForLatestHash(message, pair.B);
        pair.Host.Battle.Turn = pair.Guest.Battle.Turn = Battle.TurnCap;
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        for (int i = 0; i < 4; i++) pair.Pump();
        Assert.Equal(NetState.Over, pair.Guest.State);
        Assert.True(pair.Guest.HasPendingReliableMessages);
        ulong hash = pair.Guest.Battle.StateHash();
        string record = pair.Guest.Battle.Record.ToJson();

        for (int i = 0; i < NetSession.DeliveryTimeoutPolls + 4; i++) pair.Pump();

        Assert.Equal(NetState.Error, pair.Guest.State);
        AssertDeliveryTimeout(pair.Guest);
        Assert.Equal(hash, pair.Guest.Battle.StateHash());
        Assert.Equal(record, pair.Guest.Battle.Record.ToJson());
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains(pair.Guest.Error, pair.Host.Error);
        Assert.Equal(hash, pair.Host.Battle.StateHash());
        Assert.Equal(record, pair.Host.Battle.Record.ToJson());
    }

    static bool IsAckForLatestHash(string message, ScriptedTransport sender)
    {
        if (Type(message) != "ack") return false;
        string hash = sender.Messages("hash").LastOrDefault();
        return hash != null && Ack(message) == Sequence(hash);
    }

    static int Finish(NetSession host, NetSession guest)
    {
        for (int polls = 1; polls <= 30000; polls++)
        {
            Pump(host, guest);
            AssertHealthy(host);
            AssertHealthy(guest);
            foreach (var session in new[] { host, guest })
            {
                if (session.State == NetState.Choose && !session.HasSubmitted)
                    session.Submit(Ai.Choose(session.Battle.Clone(), session.MySide));
                else if (session.State == NetState.Replace && !session.HasSubmitted)
                    session.SubmitReplace(Ai.ChooseReplacement(session.Battle.Clone(), session.MySide));
            }
            if (host.State == NetState.Over && guest.State == NetState.Over
                && !host.HasPendingReliableMessages && !guest.HasPendingReliableMessages)
            {
                AssertFinished(host, guest);
                return polls;
            }
        }
        Assert.Fail("模拟网络在 30000 次 Poll 内没有完成双方终局和可靠消息收尾");
        return -1;
    }

    static void AssertFinished(NetSession host, NetSession guest)
    {
        Assert.Equal(NetState.Over, host.State);
        Assert.Equal(NetState.Over, guest.State);
        Assert.Equal(0, host.PendingReliableCount);
        Assert.Equal(0, guest.PendingReliableCount);
        Assert.False(host.HasPendingReliableMessages);
        Assert.False(guest.HasPendingReliableMessages);
        Assert.Null(host.Error);
        Assert.Null(guest.Error);
        Assert.Equal(host.Battle.StateHash(), guest.Battle.StateHash());
        Assert.Equal(host.Battle.Record.ToJson(), guest.Battle.Record.ToJson());
        Assert.Equal(host.Battle.Winner, guest.Battle.Winner);
        Assert.Equal(Log(host.Battle), Log(guest.Battle));
    }

    static void AssertHealthy(NetSession session)
    {
        Assert.True(session.State != NetState.Error && session.State != NetState.Desync,
            session.State + ": " + session.Error);
    }

    static void AssertDeliveryTimeout(NetSession session)
    {
        Assert.Contains("未获确认", session.Error);
        Assert.Contains(NetSession.DeliveryTimeoutPolls.ToString(), session.Error);
    }

    static void AssertReady(ScriptedPair pair)
    {
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Equal(0, pair.Host.PendingReliableCount);
        Assert.Equal(0, pair.Guest.PendingReliableCount);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
    }

    static ScriptedPair Ready()
    {
        var pair = new ScriptedPair();
        for (int i = 0; i < 3; i++) pair.Pump();
        AssertReady(pair);
        return pair;
    }

    static void Pump(NetSession host, NetSession guest) { host.Poll(); guest.Poll(); }
    static string Log(Battle battle) => JsonSerializer.Serialize(battle.Log, new JsonSerializerOptions { IncludeFields = true });
    static string Type(string message)
    {
        using var document = JsonDocument.Parse(message);
        return document.RootElement.GetProperty("type").GetString();
    }

    static int Sequence(string message)
    {
        using var document = JsonDocument.Parse(message);
        return document.RootElement.GetProperty("seq").GetInt32();
    }

    static int Ack(string message)
    {
        using var document = JsonDocument.Parse(message);
        return document.RootElement.GetProperty("ack").GetInt32();
    }
    static string WireIdentity(string message) => Type(message) + ":" + (Type(message) == "ack" ? Ack(message) : Sequence(message));
    static long[] Counters(LossyTransport transport) => new[]
    {
        transport.PollCount, transport.SentCount, transport.DroppedCount, transport.DuplicatedCount, transport.DeliveredCount
    };

    sealed class ScriptedTransport : INetTransport
    {
        readonly Queue<string> incoming = new Queue<string>();
        public ScriptedTransport Peer;
        public Func<string, bool> Drop;
        public Func<string, bool> Hold;
        public readonly List<string> Sent = new List<string>();
        public readonly List<string> Held = new List<string>();

        public void Send(string message)
        {
            Sent.Add(message);
            if (Drop != null && Drop(message)) return;
            if (Hold != null && Hold(message)) Held.Add(message);
            else Peer.incoming.Enqueue(message);
        }

        public bool TryReceive(out string message)
        {
            if (incoming.Count == 0) { message = null; return false; }
            message = incoming.Dequeue();
            return true;
        }

        public void Inject(string message) { incoming.Enqueue(message); }
        public IEnumerable<string> Messages(string type) => Sent.Where(message => Type(message) == type);
    }

    sealed class ScriptedPair
    {
        public readonly ScriptedTransport A = new ScriptedTransport();
        public readonly ScriptedTransport B = new ScriptedTransport();
        public readonly NetSession Host;
        public readonly NetSession Guest;

        public ScriptedPair(uint seed = 96, Action<ScriptedTransport, ScriptedTransport> configure = null)
        {
            A.Peer = B;
            B.Peer = A;
            configure?.Invoke(A, B);
            var db = BattleTests.LoadPack("original");
            Host = new NetSession(db, A, true, HostTeam, "主机", seed);
            Guest = new NetSession(db, B, false, GuestTeam, "客机", null);
        }

        public void Pump() { T96_LossyNetTests.Pump(Host, Guest); }
    }

    sealed class LossyPair
    {
        public readonly ScriptedTransport HostWire = new ScriptedTransport();
        public readonly ScriptedTransport GuestWire = new ScriptedTransport();
        public readonly LossyTransport A;
        public readonly LossyTransport B;
        public readonly NetSession Host;
        public readonly NetSession Guest;

        public LossyPair(uint battleSeed, uint hostNetworkSeed, uint guestNetworkSeed,
            double loss, double duplicates, int minDelay, int maxDelay)
        {
            HostWire.Peer = GuestWire;
            GuestWire.Peer = HostWire;
            A = new LossyTransport(HostWire, hostNetworkSeed, dropRate: loss, minDelayPolls: minDelay,
                maxDelayPolls: maxDelay, duplicateRate: duplicates, reorder: true);
            B = new LossyTransport(GuestWire, guestNetworkSeed, dropRate: loss, minDelayPolls: minDelay,
                maxDelayPolls: maxDelay, duplicateRate: duplicates, reorder: true);
            var db = BattleTests.LoadPack("original");
            Host = new NetSession(db, A, true, HostTeam, "主机", battleSeed);
            Guest = new NetSession(db, B, false, GuestTeam, "客机", null);
        }
    }
}
