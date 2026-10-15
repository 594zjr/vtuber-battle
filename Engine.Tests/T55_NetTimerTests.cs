using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T55_NetTimerTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    sealed class Transport : INetTransport
    {
        readonly Queue<string> incoming = new Queue<string>();
        public Transport Peer;
        public Func<string, string> Rewrite;
        public readonly HashSet<string> HoldTypes = new HashSet<string>();
        public readonly List<string> Sent = new List<string>();

        public void Send(string message)
        {
            if (Rewrite != null) message = Rewrite(message);
            Sent.Add(message);
            if (!HoldTypes.Contains(Field(message, "type"))) Peer.incoming.Enqueue(message);
        }

        public bool TryReceive(out string message)
        {
            if (incoming.Count == 0) { message = null; return false; }
            message = incoming.Dequeue();
            return true;
        }

        public string[] Messages(string type)
        {
            return Sent.Where(message => Field(message, "type") == type).ToArray();
        }
    }

    sealed class Pair
    {
        public readonly Transport A = new Transport(), B = new Transport();
        public readonly NetSession Host, Guest;

        public Pair(double? turn = 5, double? bank = 20, bool handshake = true,
            Func<string, string> hostRewrite = null, Func<string, string> guestRewrite = null,
            double? guestTurn = null, double? guestBank = null)
        {
            A.Peer = B;
            B.Peer = A;
            A.Rewrite = hostRewrite;
            B.Rewrite = guestRewrite;
            Host = new NetSession(BattleTests.LoadPack("original"), A, true, HostTeam, "主机", 19, turn, bank);
            Guest = new NetSession(BattleTests.LoadPack("original"), B, false, GuestTeam, "客机", 99, guestTurn, guestBank);
            if (handshake)
            {
                Pump();
                Assert.Equal(NetState.Choose, Host.State);
                Assert.Equal(NetState.Choose, Guest.State);
            }
        }

        public void Pump()
        {
            for (int i = 0; i < 6; i++) { Host.Poll(); Guest.Poll(); }
        }

        public void EnterReplacement(params int[] sides)
        {
            foreach (var session in new[] { Host, Guest })
                foreach (int side in sides)
                {
                    session.Battle.Active(side).Hp = 0;
                    session.Battle.Active(side).Fainted = true;
                }
            Host.Submit(BattleAction.Pass());
            Guest.Submit(BattleAction.Pass());
            Pump();
        }
    }

    static string Field(string message, string field)
    {
        using var json = JsonDocument.Parse(message);
        return json.RootElement.GetProperty(field).GetString();
    }

    static string EditHello(string message, Action<Dictionary<string, object>> edit)
    {
        if (Field(message, "type") != "hello") return message;
        var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
        edit(fields);
        return JsonSerializer.Serialize(fields);
    }

    [Fact]
    public void HostNegotiatesTimerAndGuestLocalOptionsCannotOverrideIt()
    {
        var pair = new Pair(2.5, 7.25, guestTurn: 99, guestBank: 88);
        foreach (var session in new[] { pair.Host, pair.Guest })
        {
            Assert.NotNull(session.Clock);
            Assert.Equal(2.5, session.Clock.TurnLeft(session.MySide));
            Assert.Equal(7.25, session.Clock.BankLeft(session.MySide));
            Assert.Equal(19u, session.Battle.Seed);
        }
        using var hostHello = JsonDocument.Parse(Assert.Single(pair.A.Messages("hello")));
        using var guestHello = JsonDocument.Parse(Assert.Single(pair.B.Messages("hello")));
        Assert.Equal(2, hostHello.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(1, hostHello.RootElement.GetProperty("timerVersion").GetInt32());
        Assert.Equal(2.5, hostHello.RootElement.GetProperty("timer").GetProperty("turnSeconds").GetDouble());
        Assert.Equal(7.25, hostHello.RootElement.GetProperty("timer").GetProperty("bankSeconds").GetDouble());
        Assert.Equal(1, guestHello.RootElement.GetProperty("timerVersion").GetInt32());
        Assert.False(guestHello.RootElement.TryGetProperty("timer", out _));
        pair.Pump();
        Assert.Equal(2.5, pair.Host.Clock.TurnLeft(0));
    }

    [Fact]
    public void DisabledTimerAcceptsOriginalVersionTwoHelloAndNeverAutoSubmits()
    {
        Func<string, string> oldHello = message => EditHello(message, fields => fields.Remove("timerVersion"));
        var pair = new Pair(null, null, hostRewrite: oldHello, guestRewrite: oldHello, guestTurn: 1, guestBank: 1);
        Assert.Null(pair.Host.Clock);
        Assert.Null(pair.Guest.Clock);
        pair.Host.Tick(double.MaxValue);
        pair.Guest.Tick(double.MaxValue);
        Assert.False(pair.Host.HasSubmitted);
        Assert.False(pair.Guest.HasSubmitted);
        Assert.Empty(pair.A.Messages("commit"));
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        Assert.Equal(2, pair.Host.Battle.Turn);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
    }

    [Fact]
    public void EnabledTimerRejectsPeerWithoutTimerCapability()
    {
        var pair = new Pair(handshake: false,
            guestRewrite: message => EditHello(message, fields => fields.Remove("timerVersion")));
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Contains("计时", pair.Host.Error);
        Assert.Null(pair.Host.Battle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GuestCannotSupplyTimerEvenWhenHostDisabledIt(bool enabled)
    {
        var pair = new Pair(enabled ? 5 : (double?)null, enabled ? 20 : (double?)null, false,
            guestRewrite: message => EditHello(message,
                fields => fields["timer"] = new { turnSeconds = 1, bankSeconds = 2 }));
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("主机", pair.Host.Error);
        Assert.Null(pair.Host.Battle);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"turnSeconds\":-1,\"bankSeconds\":2}")]
    [InlineData("{\"turnSeconds\":2,\"bankSeconds\":\"3\"}")]
    [InlineData("{\"turnSeconds\":1e999,\"bankSeconds\":2}")]
    public void MalformedHostTimerIsRejectedBeforeGuestConstructsBattle(string timer)
    {
        var pair = new Pair(handshake: false, hostRewrite: message => EditHello(message, fields =>
        {
            using var value = JsonDocument.Parse(timer);
            fields["timer"] = value.RootElement.Clone();
        }));
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Null(pair.Guest.Battle);
        Assert.False(string.IsNullOrWhiteSpace(pair.Guest.Error));
    }

    [Theory]
    [InlineData(null, 1.0)]
    [InlineData(1.0, null)]
    [InlineData(-1.0, 1.0)]
    [InlineData(1.0, double.PositiveInfinity)]
    [InlineData(double.NaN, 1.0)]
    public void InvalidConstructorTimerReportsErrorWithoutSendingHello(double? turn, double? bank)
    {
        var pair = new Pair(turn, bank, false);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("计时", pair.Host.Error);
        Assert.Null(pair.Host.Battle);
        Assert.Empty(pair.A.Messages("hello"));
        Assert.Single(pair.A.Messages("error"));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidTickDoesNotMutateClockOrBattle(double seconds)
    {
        var pair = new Pair();
        ulong before = pair.Host.Battle.StateHash();
        Assert.Throws<ArgumentOutOfRangeException>(() => pair.Host.Tick(seconds));
        Assert.Equal(5, pair.Host.Clock.TurnLeft(0));
        Assert.Equal(20, pair.Host.Clock.BankLeft(0));
        Assert.Equal(before, pair.Host.Battle.StateHash());
        Assert.Empty(pair.A.Messages("commit"));
    }

    [Fact]
    public void LocalTimeoutOnlyCommitsItsOwnPassWithoutExposingOrInventingPeerAction()
    {
        var pair = new Pair(2, 3);
        ulong before = pair.Host.Battle.StateHash();
        pair.Host.Tick(5);
        pair.Host.Tick(0);
        Assert.True(pair.Host.HasSubmitted);
        Assert.True(pair.Host.Clock.TimedOut(0));
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Single(pair.A.Messages("commit"));
        Assert.Empty(pair.A.Messages("reveal"));
        Assert.Empty(pair.B.Messages("commit"));
        Assert.Empty(pair.Host.Battle.Record.Events);
        Assert.Equal(before, pair.Host.Battle.StateHash());
    }

    [Fact]
    public void TwoLocalTimeoutsResolveSamePassTurnAndKeepSpentBankAcrossTurns()
    {
        var pair = new Pair(2, 3);
        pair.Host.Tick(5);
        pair.Guest.Tick(5);
        pair.Pump();
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Equal(2, pair.Host.Battle.Turn);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Equal(pair.Host.Battle.Record.ToJson(), pair.Guest.Battle.Record.ToJson());
        foreach (var session in new[] { pair.Host, pair.Guest })
        {
            Assert.Equal(2, session.Clock.TurnLeft(session.MySide));
            Assert.Equal(0, session.Clock.BankLeft(session.MySide));
            Assert.False(session.Clock.TimedOut(session.MySide));
            Assert.False(session.HasSubmitted);
        }
        Assert.Equal("P", Field(Assert.Single(pair.A.Messages("reveal")), "action"));
        Assert.Equal("P", Field(Assert.Single(pair.B.Messages("reveal")), "action"));
    }

    [Fact]
    public void EarlyPeerCommitWaitsForLocalFullBudgetAndCommittedClocksStop()
    {
        var pair = new Pair();
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        pair.Guest.Tick(24);
        pair.Host.Tick(24);
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.False(pair.Host.HasSubmitted);
        Assert.Equal(1, pair.Host.Clock.BankLeft(0));
        Assert.Equal(20, pair.Host.Clock.BankLeft(1));
        Assert.Equal(20, pair.Guest.Clock.BankLeft(1));
        Assert.Equal(5, pair.Guest.Clock.TurnLeft(1));
        pair.Host.Tick(1);
        Assert.True(pair.Host.HasSubmitted);
        pair.Host.Tick(9);
        Assert.Equal(NetState.Waiting, pair.Host.State);
        pair.Host.Tick(1);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("公开", pair.Host.Error);
        Assert.Empty(pair.Host.Battle.Record.Events);
    }

    [Fact]
    public void LargeTickCannotBackdateRevealWaitOpenedByItsOwnAutomaticCommit()
    {
        var pair = new Pair(2, 3);
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        pair.Host.Tick(100);
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.True(pair.Host.HasSubmitted);
        Assert.Single(pair.A.Messages("reveal"));
        Assert.Empty(pair.B.Messages("reveal"));
        pair.Pump();
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
    }

    [Fact]
    public void TimerRevisionChangesOnlyWhenEarlyCommitCanActuallyStartRevealWait()
    {
        var pair = new Pair();
        long choosing = pair.Host.TimerRevision;
        pair.Guest.Submit(BattleAction.Pass());
        pair.Host.Poll();
        Assert.Equal(choosing, pair.Host.TimerRevision);
        pair.Host.Submit(BattleAction.Pass());
        Assert.Equal(choosing + 1, pair.Host.TimerRevision);
        pair.Host.Tick(0);
        Assert.Equal(choosing + 1, pair.Host.TimerRevision);
        pair.Host.Tick(10);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("公开", pair.Host.Error);
    }

    [Fact]
    public void SinglePeerReplacementCommitStartsANewRevealWaitRevisionWhileStateStaysWaiting()
    {
        var pair = new Pair();
        pair.EnterReplacement(1);
        long replacing = pair.Host.TimerRevision;
        Assert.Equal(NetState.Waiting, pair.Host.State);
        pair.B.HoldTypes.Add("reveal");
        pair.B.HoldTypes.Add("hash");
        pair.Guest.SubmitReplace(1);
        pair.Host.Poll();
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Equal(replacing + 1, pair.Host.TimerRevision);
        pair.Host.Tick(10);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("公开", pair.Host.Error);
    }

    [Fact]
    public void OneLargeTickIncludesPeerUncommittedBudgetAndTenSecondGrace()
    {
        var pair = new Pair(2, 3);
        pair.Host.Tick(15);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("承诺", pair.Host.Error);
        Assert.Single(pair.A.Messages("commit"));
        Assert.Empty(pair.A.Messages("reveal"));
        Assert.Empty(pair.Host.Battle.Record.Events);
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Guest.State);
    }

    [Fact]
    public void MissingCommitIsAllowedUntilTheExactGraceBoundary()
    {
        var pair = new Pair(2, 3);
        pair.Host.Submit(BattleAction.Pass());
        pair.Host.Tick(14);
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Equal(2, pair.Host.Clock.TurnLeft(0));
        Assert.Equal(3, pair.Host.Clock.BankLeft(0));
        pair.Host.Tick(1);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("承诺", pair.Host.Error);
    }

    [Fact]
    public void AutomaticPassStillRejectsARevealThatBreaksItsCommitment()
    {
        var pair = new Pair(2, 3);
        pair.B.Rewrite = message =>
        {
            if (Field(message, "type") != "reveal") return message;
            var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
            fields["action"] = "S0";
            return JsonSerializer.Serialize(fields);
        };
        pair.Host.Tick(5);
        pair.Guest.Tick(5);
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("承诺", pair.Host.Error);
        Assert.Empty(pair.Host.Battle.Record.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SingleReplacementTimeoutSelectsLowestAvailableSlotAndIdleSideSpendsNothing(int side)
    {
        var pair = new Pair(2, 3);
        pair.EnterReplacement(side);
        foreach (var session in new[] { pair.Host, pair.Guest })
        {
            session.Battle.Sides[side].Team[1].Hp = 0;
            session.Battle.Sides[side].Team[1].Fainted = true;
        }
        var activeSession = side == 0 ? pair.Host : pair.Guest;
        var idleSession = side == 0 ? pair.Guest : pair.Host;
        var activeTransport = side == 0 ? pair.A : pair.B;
        idleSession.Tick(5);
        Assert.Equal(2, idleSession.Clock.TurnLeft(1 - side));
        Assert.Equal(3, idleSession.Clock.BankLeft(1 - side));
        activeSession.Tick(100);
        Assert.Equal(NetState.Waiting, activeSession.State);
        Assert.Equal("W2", Field(activeTransport.Messages("reveal").Last(), "action"));
        pair.Pump();
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Equal(2, pair.Host.Battle.Sides[side].Active);
        Assert.Equal(2, activeSession.Clock.TurnLeft(side));
        Assert.Equal(0, activeSession.Clock.BankLeft(side));
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
    }

    [Fact]
    public void MissingHashHasAnIndependentDeadlineAfterBothSelectionsWereVerified()
    {
        var pair = new Pair();
        pair.B.HoldTypes.Add("hash");
        pair.Host.Submit(BattleAction.Pass());
        pair.Guest.Submit(BattleAction.Pass());
        pair.Host.Poll();
        long revealWait = pair.Host.TimerRevision;
        pair.Guest.Poll();
        pair.Host.Poll();
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Equal(revealWait + 1, pair.Host.TimerRevision);
        ulong settledHash = pair.Host.Battle.StateHash();
        pair.Host.Tick(9);
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Equal(5, pair.Host.Clock.TurnLeft(0));
        Assert.Equal(20, pair.Host.Clock.BankLeft(0));
        pair.Host.Tick(1);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("哈希", pair.Host.Error);
        Assert.Equal(settledHash, pair.Host.Battle.StateHash());
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Guest.State);
    }

    [Fact]
    public void ZeroBudgetsSubmitOnZeroTickButDoNotInventTheRemoteReveal()
    {
        var pair = new Pair(0, 0);
        pair.Host.Tick(0);
        Assert.True(pair.Host.HasSubmitted);
        Assert.Single(pair.A.Messages("commit"));
        Assert.Empty(pair.A.Messages("reveal"));
        Assert.Equal(NetState.Waiting, pair.Host.State);
        pair.Guest.Tick(0);
        pair.Pump();
        Assert.Equal(2, pair.Host.Battle.Turn);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
    }
}
