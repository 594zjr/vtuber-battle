using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T100_AiTakeoverTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    sealed class Transport : INetTransport
    {
        public Transport Peer;
        public bool Disconnected;
        public Func<string, string> Rewrite;
        public readonly Queue<string> Incoming = new Queue<string>();
        public readonly List<string> Sent = new List<string>();
        public readonly HashSet<string> HoldTypes = new HashSet<string>();

        public void Send(string message)
        {
            if (Disconnected) throw new IOException("模拟连接断开");
            if (Rewrite != null) message = Rewrite(message);
            Sent.Add(message);
            if (!HoldTypes.Contains(Text(message, "type"))) Peer.Incoming.Enqueue(message);
        }

        public bool TryReceive(out string message)
        {
            if (Incoming.Count != 0) { message = Incoming.Dequeue(); return true; }
            if (Disconnected) throw new IOException("模拟连接断开");
            message = null;
            return false;
        }

        public string[] Messages(string type)
        {
            return Sent.Where(message => Text(message, "type") == type).ToArray();
        }
    }

    sealed class Pair
    {
        public readonly ContentDb Db = BattleTests.LoadPack("original");
        public readonly Transport A = new Transport(), B = new Transport();
        public readonly NetSession Host, Guest;

        public Pair(bool enabled = true, bool handshake = true, bool holdReady = false,
            double? turn = 1, double? bank = 1, bool guestEnabled = false)
        {
            A.Peer = B;
            B.Peer = A;
            if (holdReady) B.HoldTypes.Add("takeover-ready");
            Host = new NetSession(Db, A, true, HostTeam, "主机", 100, turn, bank, enabled);
            Guest = new NetSession(Db, B, false, GuestTeam, "客机", null, null, null, guestEnabled);
            if (handshake)
            {
                Pump();
                Assert.Equal(NetState.Choose, Host.State);
                Assert.Equal(NetState.Choose, Guest.State);
            }
        }

        public void Pump(int count = 6)
        {
            for (int i = 0; i < count; i++) { Host.Poll(); Guest.Poll(); }
        }

        public void Complete()
        {
            for (int i = 0; i < 5000; i++)
            {
                Pump(1);
                AssertHealthy(Host);
                AssertHealthy(Guest);
                if (Host.State == NetState.Over && Guest.State == NetState.Over) return;
                SubmitHuman(Host);
                SubmitHuman(Guest);
            }
            Assert.Fail("代打对局未能在有限步骤内结束");
        }
    }

    static void SubmitHuman(NetSession session)
    {
        if (session.HasSubmitted || session.IsAiControlled(session.MySide)) return;
        if (session.State == NetState.Choose) session.Submit(Ai.Choose(session.Battle, session.MySide, AiLevel.Normal));
        else if (session.State == NetState.Replace)
            session.SubmitReplace(Ai.ChooseReplacement(session.Battle, session.MySide));
    }

    static void AssertHealthy(NetSession session)
    {
        Assert.True(session.State != NetState.Error && session.State != NetState.Desync, session.Error);
    }

    static string Text(string message, string field)
    {
        using var json = JsonDocument.Parse(message);
        return json.RootElement.GetProperty(field).GetString();
    }

    static string RewriteField(string message, string type, string key, object value)
    {
        if (Text(message, "type") != type) return message;
        var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
        fields[key] = value;
        return JsonSerializer.Serialize(fields);
    }

    static void AssertSameLog(Battle expected, Battle actual)
    {
        Assert.Equal(expected.Log.Count, actual.Log.Count);
        for (int i = 0; i < expected.Log.Count; i++)
        {
            var a = expected.Log[i];
            var b = actual.Log[i];
            Assert.Equal((a.Turn, a.Side, a.Node, a.Text, a.Kind), (b.Turn, b.Side, b.Node, b.Text, b.Kind));
            Assert.Equal(a.SnapActive, b.SnapActive);
            Assert.Equal(a.SnapHp[0], b.SnapHp[0]);
            Assert.Equal(a.SnapHp[1], b.SnapHp[1]);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TimedOutSidesFinishWithIdenticalRecordsLogsAndHashesWithoutSendingAiActions(int mask)
    {
        var pair = new Pair();
        if ((mask & 1) != 0) pair.Host.Tick(2);
        if ((mask & 2) != 0) pair.Guest.Tick(2);
        pair.Pump();
        pair.Complete();

        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Equal(pair.Host.Battle.Record.ToJson(), pair.Guest.Battle.Record.ToJson());
        AssertSameLog(pair.Host.Battle, pair.Guest.Battle);
        Assert.False(pair.Host.IsOfflineContinuation);
        Assert.False(pair.Guest.IsOfflineContinuation);
        Assert.Contains(pair.Host.Battle.Log, line => line.Text.Contains("电脑代打"));
        Assert.All(pair.Host.Battle.Record.Events.Where(e => e.Type == BattleRecordEventType.Turn),
            e => Assert.Equal(mask, e.AiTakeoverMask));
        foreach (int side in new[] { 0, 1 })
        {
            bool expected = (mask & (1 << side)) != 0;
            Assert.Equal(expected, pair.Host.IsAiControlled(side));
            Assert.Equal(expected, pair.Guest.IsAiControlled(side));
            var wire = side == 0 ? pair.A : pair.B;
            if (expected)
            {
                Assert.Single(wire.Messages("takeover"));
                Assert.Empty(wire.Messages("commit"));
                Assert.Empty(wire.Messages("reveal"));
            }
        }

        var record = BattleRecord.FromJson(pair.Host.Battle.Record.ToJson());
        Assert.Contains(record.Events, e => e.Type == BattleRecordEventType.Replace && e.AiTakeoverMask != 0);
        var replay = Replay.Run(pair.Db, record);
        Assert.Equal(pair.Host.Battle.StateHash(), replay.StateHash());
        AssertSameLog(pair.Host.Battle, replay);
        Assert.True(BattleSummary.From(replay).AiTakeover);
        Assert.Contains("电脑代打", BattleSummary.From(replay).ToText());
        var clone = replay.Clone();
        Assert.Equal(record.ToJson(), clone.Record.ToJson());
        AssertSameLog(replay, clone);
        var cursor = new ReplayCursor(pair.Db, record, 2);
        cursor.ToEnd();
        Assert.Equal(replay.StateHash(), cursor.Current.StateHash());
        AssertSameLog(replay, cursor.Current);
        cursor.Prev();
        cursor.Next();
        AssertSameLog(replay, cursor.Current);
        Assert.Equal(record.ToJson(), ShareCode.Decode(ShareCode.Encode(record)).ToJson());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AutomaticChoiceMatchesNormalAiFromSharedCheckpoint(int side)
    {
        var pair = new Pair();
        var expected = pair.Host.Battle.Clone();
        var expectedAction = Ai.Choose(expected, side, AiLevel.Normal);
        expected.ResolveTurn(side == 0 ? expectedAction : BattleAction.Pass(),
            side == 1 ? expectedAction : BattleAction.Pass());
        var automated = side == 0 ? pair.Host : pair.Guest;
        var human = side == 0 ? pair.Guest : pair.Host;
        automated.Tick(2);
        pair.Pump();
        // 本地建议的随机抽样不得污染正式代打与结算使用的共享检查点。
        Ai.Choose(pair.Host.Battle, 0, AiLevel.Normal);
        Ai.Choose(pair.Guest.Battle, 1, AiLevel.Normal);
        human.Submit(BattleAction.Pass());
        pair.Pump();
        AssertHealthy(pair.Host);
        AssertHealthy(pair.Guest);
        var entry = Assert.Single(pair.Host.Battle.Record.Events);
        Assert.Equal(expectedAction.Type, side == 0 ? entry.A0.Type : entry.A1.Type);
        Assert.Equal(expectedAction.Index, side == 0 ? entry.A0.Index : entry.A1.Index);
        Assert.Equal(expected.StateHash(), pair.Host.Battle.StateHash());
        Assert.Equal(expected.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Equal(1 << side, entry.AiTakeoverMask);
    }

    [Fact]
    public void DisabledOptionKeepsOldHelloAndTimeoutPassBehavior()
    {
        var pair = new Pair(enabled: false);
        var guestRequest = new Pair(enabled: false, guestEnabled: true);
        Assert.Equal(Assert.Single(pair.A.Messages("hello")), Assert.Single(guestRequest.A.Messages("hello")));
        Assert.Equal(Assert.Single(pair.B.Messages("hello")), Assert.Single(guestRequest.B.Messages("hello")));
        Assert.DoesNotContain("aiTakeover", Assert.Single(pair.A.Messages("hello")));
        Assert.Empty(pair.B.Messages("takeover-ready"));
        pair.Host.Tick(2);
        pair.Guest.Tick(2);
        pair.Pump();
        Assert.Equal("P", Text(Assert.Single(pair.A.Messages("reveal")), "action"));
        Assert.Equal("P", Text(Assert.Single(pair.B.Messages("reveal")), "action"));
        Assert.All(new[] { pair.Host, pair.Guest }, session =>
        {
            Assert.False(session.AiTakeoverOnTimeout);
            Assert.False(session.IsAiControlled(0));
            Assert.False(session.IsAiControlled(1));
            Assert.False(BattleSummary.From(session.Battle).AiTakeover);
            Assert.DoesNotContain("aiTakeoverMask", session.Battle.Record.ToJson());
            Assert.DoesNotContain("offlineContinuation", session.Battle.Record.ToJson());
            Assert.DoesNotContain(session.Battle.Log, line => line.Text.Contains("电脑代打"));
        });
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
    }

    [Fact]
    public void HostOptionIsNegotiatedAndCannotStartWithoutGuestCapabilityAcknowledgement()
    {
        var enabled = new Pair();
        Assert.True(enabled.Host.AiTakeoverOnTimeout);
        Assert.True(enabled.Guest.AiTakeoverOnTimeout);
        using var hello = JsonDocument.Parse(Assert.Single(enabled.A.Messages("hello")));
        Assert.True(hello.RootElement.GetProperty("aiTakeover").GetBoolean());
        Assert.Equal(1, hello.RootElement.GetProperty("aiTakeoverVersion").GetInt32());
        Assert.Single(enabled.B.Messages("takeover-ready"));

        var unsupported = new Pair(handshake: false, holdReady: true, turn: null, bank: null);
        unsupported.Pump();
        Assert.Equal(NetState.Handshake, unsupported.Host.State);
        Assert.Null(unsupported.Host.Battle);
        unsupported.Host.Tick(9.5);
        Assert.Equal(NetState.Handshake, unsupported.Host.State);
        unsupported.Host.Tick(0.5);
        Assert.Equal(NetState.Error, unsupported.Host.State);
        Assert.Contains("代打", unsupported.Host.Error);
        Assert.False(unsupported.Host.IsOfflineContinuation);
    }

    [Fact]
    public void TakeoverRequiresEntireBudgetAndCannotReplaceAnExistingCommitment()
    {
        var pair = new Pair();
        pair.Guest.Tick(1.5);
        Assert.False(pair.Guest.IsAiControlled(1));
        Assert.Empty(pair.B.Messages("takeover"));
        pair.Guest.Submit(BattleAction.Pass());
        pair.Guest.Tick(100);
        Assert.False(pair.Guest.IsAiControlled(1));
        Assert.Empty(pair.B.Messages("takeover"));
    }

    [Theory]
    [InlineData("reveal")]
    [InlineData("hash")]
    public void InvalidProtocolMessagesNeverFallBackToOfflineTakeover(string type)
    {
        var pair = new Pair();
        pair.B.Rewrite = message => RewriteField(message, type, type == "reveal" ? "action" : "hash",
            type == "reveal" ? "S0" : "ffffffffffffffff");
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        Assert.Equal(type == "reveal" ? NetState.Error : NetState.Desync, pair.Host.State);
        string error = pair.Host.Error;
        pair.A.Disconnected = true;
        pair.Host.Tick(100);
        pair.Host.Poll();
        Assert.False(pair.Host.WaitingForAiTakeover);
        Assert.False(pair.Host.IsOfflineContinuation);
        Assert.Equal(error, pair.Host.Error);
    }

    [Theory]
    [InlineData("reveal")]
    [InlineData("hash")]
    public void MissingCommittedRevealOrStateHashDoesNotAuthorizeOfflineResults(string type)
    {
        var pair = new Pair();
        pair.B.HoldTypes.Add(type);
        if (type == "reveal") pair.B.HoldTypes.Add("hash");
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        pair.Host.Tick(10);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains(type == "reveal" ? "公开" : "哈希", pair.Host.Error);
        Assert.False(pair.Host.IsOfflineContinuation);
        Assert.False(pair.Host.WaitingForAiTakeover);
    }

    [Fact]
    public void PhysicalDisconnectWaitsTenSecondsThenRollsBackUnconfirmedTurnBeforeOfflinePlay()
    {
        var pair = new Pair(turn: null, bank: null);
        pair.B.HoldTypes.Add("hash");
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        Assert.Single(pair.Host.Battle.Record.Events);
        pair.A.Disconnected = true;
        pair.Host.Poll();
        Assert.True(pair.Host.WaitingForAiTakeover);
        pair.Host.Tick(9.5);
        Assert.False(pair.Host.IsOfflineContinuation);
        pair.Host.Tick(0.5);
        Assert.True(pair.Host.IsOfflineContinuation);
        Assert.False(pair.Host.WaitingForAiTakeover);
        Assert.Empty(pair.Host.Battle.Record.Events);
        Assert.Equal(1, pair.Host.Battle.Turn);
        Assert.True(pair.Host.IsAiControlled(1));
        Assert.False(pair.Host.IsAiControlled(0));
        Assert.True(pair.Host.Battle.Record.IsOfflineContinuation);

        int beforeMessages = pair.A.Sent.Count;
        for (int i = 0; i < 5000 && pair.Host.State != NetState.Over; i++)
        {
            pair.Host.Poll();
            AssertHealthy(pair.Host);
            SubmitHuman(pair.Host);
        }
        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(beforeMessages, pair.A.Sent.Count);
        var summary = BattleSummary.From(pair.Host.Battle);
        Assert.True(summary.AiTakeover);
        Assert.True(summary.IsOfflineContinuation);
        var record = BattleRecord.FromJson(pair.Host.Battle.Record.ToJson());
        Assert.True(record.IsOfflineContinuation);
        var replay = Replay.Run(pair.Db, record);
        Assert.True(replay.Record.IsOfflineContinuation);
        Assert.True(BattleSummary.From(replay).IsOfflineContinuation);
        Assert.Equal(pair.Host.Battle.StateHash(), replay.StateHash());
        AssertSameLog(pair.Host.Battle, replay);
        Assert.True(replay.Clone().Record.IsOfflineContinuation);
        var cursor = new ReplayCursor(pair.Db, record);
        cursor.ToEnd();
        Assert.True(cursor.Current.Record.IsOfflineContinuation);
        Assert.Equal(replay.StateHash(), cursor.Current.StateHash());
    }

    [Fact]
    public void OfflineReportsPreserveUnconfirmedSourceBeforeResultsThroughReplay()
    {
        var pair = new Pair();
        pair.Host.Tick(2);
        pair.Pump();
        pair.Complete();
        var online = pair.Host.Battle;
        string onlineReport = BattleReport.Markdown(online);
        Assert.Contains("电脑代打", onlineReport);
        Assert.DoesNotContain("离线续局", onlineReport);

        var offline = online.Clone();
        offline.Record.IsOfflineContinuation = true;
        var record = ShareCode.Decode(ShareCode.Encode(offline.Record));
        var replay = Replay.Run(pair.Db, record);
        var cursor = new ReplayCursor(pair.Db, record);
        cursor.ToEnd();
        const string warning = "> 离线续局（未经双方终局确认）。\n\n";
        foreach (var battle in new[] { offline, replay, cursor.Current })
        {
            string report = BattleReport.Markdown(battle);
            Assert.StartsWith("# 对战战报\n\n" + warning, report);
            Assert.Equal(onlineReport, report.Replace(warning, string.Empty));
            Assert.Equal(online.StateHash(), battle.StateHash());
        }
        Assert.Equal(onlineReport, BattleReport.Markdown(online));
    }

    [Fact]
    public void DisabledTakeoverStillReportsDisconnectImmediately()
    {
        var pair = new Pair(enabled: false, turn: null, bank: null);
        pair.A.Disconnected = true;
        pair.Host.Poll();
        Assert.Equal(NetState.Error, pair.Host.State);
        pair.Host.Tick(100);
        Assert.False(pair.Host.WaitingForAiTakeover);
        Assert.False(pair.Host.IsOfflineContinuation);
        Assert.Empty(pair.Host.Battle.Record.Events);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("commit")]
    public void UncommittedTimeoutCannotSkipInvalidMessagesBehindOnePollBatch(string type)
    {
        var pair = new Pair();
        pair.Host.Submit(BattleAction.Pass());
        for (int i = 0; i < 64; i++) pair.A.Incoming.Enqueue("{\"type\":\"chat\",\"index\":0}");
        pair.A.Incoming.Enqueue(type == "error"
            ? "{\"type\":\"error\",\"message\":\"队列末尾错误\"}"
            : "{\"type\":\"commit\",\"turn\":1,\"kind\":\"action\",\"digest\":\"bad\"}");
        pair.Host.Poll();
        pair.Host.Tick(12);
        Assert.False(pair.Host.IsOfflineContinuation);
        pair.Host.Poll();
        pair.Host.Tick(100);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.False(pair.Host.IsOfflineContinuation);
        Assert.Empty(pair.Host.Battle.Record.Events);
    }

    [Fact]
    public void ValidCommitAndRevealBehindOnePollBatchPreventPrematureOfflineFork()
    {
        var pair = new Pair();
        pair.Host.Submit(BattleAction.Pass());
        for (int i = 0; i < 64; i++) pair.A.Incoming.Enqueue("{\"type\":\"chat\",\"index\":0}");
        pair.Guest.Submit(BattleAction.Pass());
        pair.Guest.Poll();
        Assert.Single(pair.B.Messages("commit"));
        Assert.Single(pair.B.Messages("reveal"));
        pair.Host.Poll();
        pair.Host.Tick(12);
        Assert.False(pair.Host.IsOfflineContinuation);
        pair.Pump();
        AssertHealthy(pair.Host);
        AssertHealthy(pair.Guest);
        Assert.False(pair.Host.IsOfflineContinuation);
        Assert.False(pair.Host.WaitingForAiTakeover);
        Assert.Equal(2, pair.Host.Battle.Turn);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Equal(0, Assert.Single(pair.Host.Battle.Record.Events).AiTakeoverMask);
    }

    [Fact]
    public void GenuineUncommittedDeadlineStillAllowsOfflineContinuationAfterQueueDrains()
    {
        var pair = new Pair();
        pair.Host.Submit(BattleAction.Pass());
        pair.Host.Tick(11.5);
        Assert.False(pair.Host.IsOfflineContinuation);
        pair.Host.Tick(0.5);
        pair.Host.Poll();
        pair.Host.Tick(0);
        Assert.True(pair.Host.IsOfflineContinuation);
        Assert.True(pair.Host.IsAiControlled(1));
        Assert.Empty(pair.Host.Battle.Record.Events);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("reveal")]
    public void SendFailureDoesNotDiscardQueuedProtocolErrorsBeforeOfflineContinuation(string type)
    {
        var pair = new Pair(turn: null, bank: null);
        pair.A.Disconnected = true;
        pair.Host.Submit(BattleAction.Pass());
        Assert.True(pair.Host.WaitingForAiTakeover);
        for (int i = 0; i < 64; i++) pair.A.Incoming.Enqueue("{\"type\":\"chat\",\"index\":0}");
        pair.A.Incoming.Enqueue(type == "error"
            ? "{\"type\":\"error\",\"message\":\"断线前最后错误\"}"
            : "{\"type\":\"reveal\",\"turn\":1,\"kind\":\"action\",\"action\":\"P\",\"nonce\":\"00000000000000000000000000000000\"}");
        pair.Host.Poll();
        pair.Host.Tick(10);
        Assert.False(pair.Host.IsOfflineContinuation);
        pair.Host.Poll();
        Assert.Equal(NetState.Error, pair.Host.State);
        pair.Host.Tick(10);
        Assert.False(pair.Host.IsOfflineContinuation);
        Assert.Empty(pair.Host.Battle.Record.Events);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("4")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("\"1\"")]
    public void ReplayRejectsMalformedTakeoverMasks(string value)
    {
        var pair = new Pair(enabled: false);
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        string json = pair.Host.Battle.Record.ToJson().Replace("\"type\":\"turn\"",
            "\"type\":\"turn\",\"aiTakeoverMask\":" + value);
        Assert.Throws<FormatException>(() => BattleRecord.FromJson(json));
    }

    [Fact]
    public void DisplayMetadataDoesNotChangeResultAndSurvivesIndependentSnapshots()
    {
        var pair = new Pair(enabled: false);
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        var record = BattleRecord.FromJson(pair.Host.Battle.Record.ToJson());
        record.Events[0].AiTakeoverMask = 2;
        var replay = Replay.Run(pair.Db, record);
        Assert.Equal(pair.Host.Battle.StateHash(), replay.StateHash());
        Assert.Equal(pair.Host.Battle.Random(), replay.Random());
        var clone = replay.Clone();
        var cursor = new ReplayCursor(pair.Db, record, 1);
        record.Events[0].AiTakeoverMask = 0;
        clone.Record.Events[0].AiTakeoverMask = 1;
        cursor.ToEnd();
        Assert.Equal(2, replay.Record.Events[0].AiTakeoverMask);
        Assert.Equal(2, cursor.Current.Record.Events[0].AiTakeoverMask);
        Assert.True(BattleSummary.From(cursor.Current).AiTakeover);
        Assert.Contains(cursor.Current.Log, entry => entry.Side == 1 && entry.Text.Contains("电脑代打"));
    }
}
