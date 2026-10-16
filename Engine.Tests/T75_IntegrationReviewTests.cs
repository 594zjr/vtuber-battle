using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public sealed class T75_IntegrationReviewTests
{
    static readonly string[] Team0 = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] Team1 = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    sealed class Link { public bool Broken; }

    sealed class Transport : INetTransport
    {
        readonly INetTransport inner;
        public readonly Link Link;
        public string CutType;
        public bool DeliverBeforeCut;

        public Transport(INetTransport inner, Link link) { this.inner = inner; Link = link; }

        public void Send(string message)
        {
            if (Link.Broken) throw new IOException("测试断线");
            if (CutType != null && MessageType(message) == CutType)
            {
                CutType = null;
                if (DeliverBeforeCut) inner.Send(message);
                Link.Broken = true;
                throw new IOException("测试发送中断");
            }
            inner.Send(message);
        }

        public bool TryReceive(out string message)
        {
            if (inner.TryReceive(out message)) return true;
            if (Link.Broken) throw new IOException("测试断线");
            return false;
        }
    }

    sealed class Pair
    {
        public readonly ContentDb Db = BattleTests.LoadPack("original");
        public readonly PlayerRating HostRating = new PlayerRating { Rating = 1700, Games = 12 };
        public readonly PlayerRating GuestRating = new PlayerRating { Rating = 1600, Games = 8 };
        public readonly NetSession Host, Guest;
        public Transport A, B;

        public Pair(bool termination = true)
        {
            NewLink(out A, out B);
            Host = new NetSession(Db, A, true, Team0, "主机", 75, 20, 30,
                enableTermination: termination, rating: HostRating, enableReconnect: true);
            Guest = new NetSession(Db, B, false, Team1, "客机", null,
                enableTermination: termination, rating: GuestRating, enableReconnect: true);
            Pump();
            Assert.Equal(6, Host.SessionProtocolVersion);
            Assert.Equal(6, Guest.SessionProtocolVersion);
            Assert.Equal(NetState.Choose, Host.State);
            Assert.Equal(NetState.Choose, Guest.State);
        }

        public void Pump()
        {
            for (int i = 0; i < 24; i++) { Host.Poll(); Guest.Poll(); }
            Healthy(Host);
            Healthy(Guest);
        }

        public void Disconnect()
        {
            A.Link.Broken = true;
            Pump();
            Waiting(Host);
            Waiting(Guest);
        }

        public void Attach()
        {
            NewLink(out A, out B);
            Host.Reconnect(A, Host.SessionId);
            Guest.Reconnect(B, Guest.SessionId);
        }

        public void Recover()
        {
            Attach();
            Pump();
            Assert.False(Host.IsDisconnected);
            Assert.False(Guest.IsDisconnected);
            Assert.False(Host.IsReconnecting);
            Assert.False(Guest.IsReconnecting);
        }

        public void Same()
        {
            Assert.Equal(Host.Battle.StateHash(), Guest.Battle.StateHash());
            Assert.Equal(Host.Battle.Record.ToJson(), Guest.Battle.Record.ToJson());
            Assert.Equal(FullLog(Host.Battle), FullLog(Guest.Battle));
            var replay = Replay.Run(Db, BattleRecord.FromJson(Host.Battle.Record.ToJson()));
            Assert.Equal(Host.Battle.StateHash(), replay.StateHash());
            Assert.Equal(FullLog(Host.Battle), FullLog(replay));
            Assert.Equal(Host.Battle.Clone().Random(), replay.Clone().Random());
        }
    }

    static void NewLink(out Transport a, out Transport b)
    {
        LoopbackPair.Create(out var left, out var right);
        var link = new Link();
        a = new Transport(left, link);
        b = new Transport(right, link);
    }

    static string MessageType(string message)
    {
        using var json = JsonDocument.Parse(message);
        string type = json.RootElement.GetProperty("type").GetString();
        if (type != "packet") return type;
        using var payload = JsonDocument.Parse(json.RootElement.GetProperty("payload").GetString());
        return payload.RootElement.GetProperty("type").GetString();
    }

    static void Healthy(NetSession session)
    {
        Assert.True(session.State != NetState.Error && session.State != NetState.Desync, session.Error);
    }

    static void Waiting(NetSession session)
    {
        Healthy(session);
        Assert.True(session.IsDisconnected);
        Assert.Equal(NetState.Waiting, session.State);
    }

    static string FullLog(Battle battle)
    {
        return JsonSerializer.Serialize(battle.Log, new JsonSerializerOptions { IncludeFields = true });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryPausesExistingTimersAndPreservesCommittedChoicesAndRatingSnapshots(bool termination)
    {
        var pair = new Pair(termination);
        pair.Host.Tick(25);
        pair.Guest.Tick(25);
        pair.Host.Submit(BattleAction.Pass());
        pair.Pump();
        pair.HostRating.Rating = 10;
        pair.GuestRating.Games = 999;
        pair.Host.OpponentRating.Rating = 20;
        pair.Guest.OpponentRating.Games = 999;
        var battle = pair.Host.Battle;
        var clock = pair.Host.Clock;
        pair.Disconnect();

        foreach (var session in new[] { pair.Host, pair.Guest })
        {
            session.Tick(1000);
            session.Poll(59.5);
            Waiting(session);
            Assert.Equal(0.5, session.ReconnectSecondsRemaining);
            Assert.Equal(0, session.Clock.TurnLeft(0));
            Assert.Equal(25, session.Clock.BankLeft(0));
            Assert.Equal(25, session.Clock.BankLeft(1));
            Assert.False(session.CanSurrender);
            Assert.False(session.CanOfferDraw);
            Assert.False(session.CanRespondDraw);
            Assert.False(session.SendQuickChat(0, 1));
        }
        pair.Recover();

        Assert.Same(battle, pair.Host.Battle);
        Assert.Same(clock, pair.Host.Clock);
        Assert.True(pair.Host.HasSubmitted);
        Assert.Throws<InvalidOperationException>(() => pair.Host.Submit(BattleAction.Skill(0)));
        Assert.Equal(1600, pair.Host.OpponentRating.Rating);
        Assert.Equal(8, pair.Host.OpponentRating.Games);
        Assert.Equal(1700, pair.Guest.OpponentRating.Rating);
        Assert.Equal(12, pair.Guest.OpponentRating.Games);
        pair.Guest.Tick(1);
        Assert.Equal(24, pair.Guest.Clock.BankLeft(1));
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        Assert.Single(pair.Host.Battle.Record.Events);
        Assert.Equal("P", pair.Host.Battle.Record.Events[0].A0.ToWire());
        pair.Same();
    }

    [Theory]
    [InlineData("termination-freeze", false)]
    [InlineData("termination-freeze", true)]
    [InlineData("termination-ready", false)]
    [InlineData("termination-ready", true)]
    [InlineData("termination-apply", false)]
    [InlineData("termination-apply", true)]
    [InlineData("hash", false)]
    [InlineData("hash", true)]
    public void ADisconnectedTerminationBarrierResumesExactlyOnceWithoutItsTenSecondTimeout(string cutType, bool delivered)
    {
        var pair = new Pair();
        var cut = cutType == "termination-ready" ? pair.B : pair.A;
        cut.CutType = cutType;
        cut.DeliverBeforeCut = delivered;
        pair.Host.Surrender();
        pair.Pump();
        Assert.Null(cut.CutType);
        Waiting(pair.Host);
        Waiting(pair.Guest);
        foreach (var session in new[] { pair.Host, pair.Guest })
        {
            session.Tick(40);
            session.Poll(40);
            Waiting(session);
            Assert.Equal(20, session.ReconnectSecondsRemaining);
        }

        pair.Recover();

        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(NetState.Over, pair.Guest.State);
        Assert.Equal(1, pair.Host.Battle.Winner);
        Assert.Equal(BattleRecordEventType.Surrender, Assert.Single(pair.Host.Battle.Record.Events).Type);
        pair.Same();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveredDrawRequestsPreserveThePendingCommitAndReachTheSameReplay(bool accept)
    {
        var pair = new Pair();
        pair.Host.Tick(7);
        pair.Guest.Tick(7);
        pair.Host.Submit(BattleAction.Pass());
        pair.Pump();
        pair.A.CutType = "termination-apply";
        pair.Host.OfferDraw();
        pair.Pump();
        Waiting(pair.Host);
        Waiting(pair.Guest);
        pair.Recover();
        Assert.True(pair.Host.HasSubmitted);
        Assert.True(pair.Guest.DrawOffered);
        Assert.Equal(13, pair.Guest.Clock.TurnLeft(1));
        Assert.Single(pair.Host.Battle.Record.Events);

        pair.B.CutType = "termination-ready";
        pair.Guest.RespondDraw(accept);
        pair.Pump();
        Waiting(pair.Host);
        Waiting(pair.Guest);
        pair.Recover();

        if (accept)
        {
            Assert.Equal(NetState.Over, pair.Host.State);
            Assert.Equal(NetState.Over, pair.Guest.State);
            Assert.Equal(-1, pair.Host.Battle.Winner);
            Assert.Equal(2, pair.Host.Battle.Record.Events.Count);
        }
        else
        {
            Assert.True(pair.Host.HasSubmitted);
            Assert.Equal(NetState.Choose, pair.Guest.State);
            Assert.Equal(13, pair.Guest.Clock.TurnLeft(1));
            Assert.Throws<InvalidOperationException>(() => pair.Host.Submit(BattleAction.Skill(0)));
            pair.Guest.Submit(BattleAction.Pass());
            pair.Pump();
            Assert.Equal(3, pair.Host.Battle.Record.Events.Count);
            Assert.Equal("P", pair.Host.Battle.Record.Events.Last().A0.ToWire());
        }
        pair.Same();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LostChatIsReplayedOnceWithoutChangingBattleOrBypassingItsSendQuota(bool delivered)
    {
        var pair = new Pair();
        string before = pair.Host.Battle.Record.ToJson();
        ulong hash = pair.Host.Battle.StateHash();
        pair.A.CutType = "chat";
        pair.A.DeliverBeforeCut = delivered;
        Assert.True(pair.Host.SendQuickChat(2, 0));
        pair.Pump();
        Waiting(pair.Host);
        Waiting(pair.Guest);

        pair.Recover();

        Assert.Equal(2, Assert.Single(pair.Guest.ReceivedChats).Index);
        Assert.Equal(before, pair.Host.Battle.Record.ToJson());
        Assert.Equal(before, pair.Guest.Battle.Record.ToJson());
        Assert.Equal(hash, pair.Host.Battle.StateHash());
        Assert.Equal(hash, pair.Guest.Battle.StateHash());
        Assert.True(pair.Host.SendQuickChat(1, 1));
        Assert.True(pair.Host.SendQuickChat(0, 2));
        Assert.False(pair.Host.SendQuickChat(3, 3));
        pair.Pump();
        Assert.Equal(new[] { 2, 1, 0 }, pair.Guest.ReceivedChats.Select(chat => chat.Index));
    }

    [Fact]
    public void SpectatorKeepsTheSameBattleAndHoldsTerminationUntilRecoveryIsConfirmed()
    {
        var pair = new Pair();
        LoopbackPair.Create(out var sender, out var receiver);
        var feed = new SpectatorFeed(pair.Host.Battle, 2);
        var viewer = new SpectatorClient(pair.Db, receiver);
        feed.AddViewer(sender);
        feed.Pump();
        viewer.Poll();
        pair.Host.OfferDraw();
        pair.Pump();
        pair.A.CutType = "termination-apply";
        pair.Guest.RespondDraw(true);
        pair.Pump();
        Waiting(pair.Host);
        Waiting(pair.Guest);
        feed.Pump();
        viewer.Poll();
        Assert.Null(viewer.Error);
        Assert.False(viewer.IsFinished);
        Assert.Empty(viewer.Battle.Record.Events);

        pair.Recover();
        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(NetState.Over, pair.Guest.State);
        feed.Finish();
        viewer.Poll();

        Assert.Null(viewer.Error);
        Assert.True(viewer.IsFinished);
        Assert.Equal(pair.Host.Battle.StateHash(), viewer.Battle.StateHash());
        Assert.Equal(pair.Host.Battle.Record.ToJson(), viewer.Battle.Record.ToJson());
        pair.Same();
    }
}
