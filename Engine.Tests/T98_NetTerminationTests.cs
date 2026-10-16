using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T98_NetTerminationTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    sealed class Transport : INetTransport
    {
        readonly Queue<string> incoming = new Queue<string>(), held = new Queue<string>();
        public readonly List<string> Sent = new List<string>();
        public Transport Peer;
        public bool Pause;
        public Func<string, string> Rewrite;

        public void Send(string message)
        {
            if (Rewrite != null) message = Rewrite(message);
            Sent.Add(message);
            if (Pause || held.Count != 0) held.Enqueue(message);
            else Peer.incoming.Enqueue(message);
        }
        public bool TryReceive(out string message)
        {
            if (incoming.Count == 0) { message = null; return false; }
            message = incoming.Dequeue();
            return true;
        }
        public void ReleaseNext(string type)
        {
            Assert.NotEmpty(held);
            string message = held.Dequeue();
            Assert.Equal(type, Field(message, "type"));
            Peer.incoming.Enqueue(message);
        }
        public void ReleaseAll()
        {
            Pause = false;
            while (held.Count != 0) Peer.incoming.Enqueue(held.Dequeue());
        }
        public void Inject(string message) { incoming.Enqueue(message); }
        public string[] Messages(string type) { return Sent.Where(message => Field(message, "type") == type).ToArray(); }
    }

    sealed class Pair
    {
        public readonly ContentDb Db = BattleTests.LoadPack("original");
        public readonly Transport A = new Transport(), B = new Transport();
        public readonly NetSession Host, Guest;
        public Pair(bool handshake = true, bool enabled = true, bool? guestEnabled = null, bool timer = false)
        {
            A.Peer = B;
            B.Peer = A;
            Host = new NetSession(Db, A, true, HostTeam, "主机", 98,
                timer ? 20 : (double?)null, timer ? 30 : (double?)null, enableTermination: enabled);
            Guest = new NetSession(Db, B, false, GuestTeam, "客机", null, enableTermination: guestEnabled ?? enabled);
            if (handshake)
            {
                Pump();
                Assert.Equal(NetState.Choose, Host.State);
                Assert.Equal(NetState.Choose, Guest.State);
            }
        }
        public void Pump(bool guestFirst = false, int count = 12)
        {
            for (int i = 0; i < count; i++)
            {
                if (guestFirst) { Guest.Poll(); Host.Poll(); }
                else { Host.Poll(); Guest.Poll(); }
            }
        }
        public void PassTurn()
        {
            Host.Submit(BattleAction.Pass());
            Guest.Submit(BattleAction.Pass());
            Pump();
            Assert.Equal(NetState.Choose, Host.State);
            Assert.Equal(NetState.Choose, Guest.State);
        }
        public void Same(bool replay = true)
        {
            Assert.Null(Host.Error);
            Assert.Null(Guest.Error);
            Assert.Equal(Host.Battle.StateHash(), Guest.Battle.StateHash());
            Assert.Equal(Host.Battle.Record.ToJson(), Guest.Battle.Record.ToJson());
            Assert.Equal(Log(Host.Battle), Log(Guest.Battle));
            if (!replay) return;
            var restored = Replay.Run(Db, BattleRecord.FromJson(Host.Battle.Record.ToJson()));
            Assert.Equal(Host.Battle.StateHash(), restored.StateHash());
            Assert.Equal(Log(Host.Battle), Log(restored));
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
            Assert.Equal(Phase.Replace, Host.Battle.Phase);
            Assert.Equal(Phase.Replace, Guest.Battle.Phase);
        }
    }

    static string Field(string message, string name)
    {
        using var document = JsonDocument.Parse(message);
        return document.RootElement.GetProperty(name).GetString();
    }
    static string Log(Battle battle) { return JsonSerializer.Serialize(battle.Log, new JsonSerializerOptions { IncludeFields = true }); }
    static string GoldenDirectory([CallerFilePath] string file = "") { return Path.Combine(Path.GetDirectoryName(file), "Golden", "net"); }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void SingleSurrenderDoesNotWaitForAnOpponentActionAndWaitsForFinalHashes(int side, bool guestFirst)
    {
        var pair = new Pair();
        var surrendering = side == 0 ? pair.Host : pair.Guest;
        double random = pair.Host.Battle.Clone().Random();
        surrendering.Submit(BattleAction.Surrender());
        Assert.Equal(NetState.Waiting, surrendering.State);
        Assert.NotEqual(Phase.Over, surrendering.Battle.Phase);
        pair.Pump(guestFirst);
        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(NetState.Over, pair.Guest.State);
        Assert.Equal(1 - side, pair.Host.Battle.Winner);
        Assert.Equal(1, pair.Host.Battle.Turn);
        Assert.Equal(random, pair.Host.Battle.Clone().Random());
        Assert.Equal(BattleRecordEventType.Surrender, Assert.Single(pair.Host.Battle.Record.Events).Type);
        pair.Same();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothSurrendersBeforePollingAreADrawRegardlessOfPollOrder(bool guestFirst)
    {
        var pair = new Pair();
        pair.Host.Surrender();
        pair.Guest.Surrender();
        pair.Pump(guestFirst);
        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(NetState.Over, pair.Guest.State);
        Assert.Equal(-1, pair.Host.Battle.Winner);
        Assert.Equal(-1, Assert.Single(pair.Host.Battle.Record.Events).Side);
        pair.Same();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SubmittedActionCanBeSupersededBySurrenderWithoutResolvingTheTurn(int side)
    {
        var pair = new Pair();
        var selected = side == 0 ? pair.Host : pair.Guest;
        selected.Submit(BattleAction.Skill(0));
        pair.Pump();
        Assert.True(selected.HasSubmitted);
        Assert.True(selected.CanSurrender);
        selected.Surrender();
        pair.Pump();
        Assert.Equal(1 - side, pair.Host.Battle.Winner);
        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(BattleRecordEventType.Surrender, Assert.Single(pair.Host.Battle.Record.Events).Type);
        pair.Same();
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public void EitherSideCanSurrenderDuringSingleOrDoubleReplacement(bool both, int surrenderSide)
    {
        var pair = new Pair();
        pair.EnterReplacement(both ? new[] { 0, 1 } : new[] { 0 });
        var session = surrenderSide == 0 ? pair.Host : pair.Guest;
        Assert.True(session.CanSurrender);
        session.Submit(BattleAction.Surrender());
        pair.Pump();
        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(NetState.Over, pair.Guest.State);
        Assert.Equal(1 - surrenderSide, pair.Host.Battle.Winner);
        pair.Same(replay: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DrawAcceptanceAndRejectionHaveMatchingRecordsAndHashes(bool accept)
    {
        var pair = new Pair();
        pair.Guest.OfferDraw();
        pair.Pump();
        Assert.True(pair.Host.DrawOffered);
        Assert.True(pair.Guest.DrawOfferPending);
        Assert.False(pair.Guest.DrawOffered);
        pair.Host.RespondDraw(accept);
        pair.Pump();
        Assert.Equal(accept ? NetState.Over : NetState.Choose, pair.Host.State);
        Assert.Equal(pair.Host.State, pair.Guest.State);
        Assert.False(pair.Host.DrawOffered);
        Assert.False(pair.Guest.DrawOfferPending);
        Assert.Equal(new[] { BattleRecordEventType.DrawOffer, BattleRecordEventType.DrawResponse },
            pair.Host.Battle.Record.Events.Select(item => item.Type));
        if (accept) Assert.Equal(-1, pair.Host.Battle.Winner);
        else pair.PassTurn();
        pair.Same();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossedOffersAndRejectionsUseSideOrderNotArrivalOrder(bool guestFirst)
    {
        var pair = new Pair();
        pair.Host.OfferDraw();
        pair.Guest.OfferDraw();
        pair.Pump(guestFirst);
        Assert.True(pair.Host.DrawOffered && pair.Guest.DrawOffered);
        pair.Host.RespondDraw(false);
        pair.Guest.RespondDraw(false);
        pair.Pump(guestFirst);
        Assert.Equal(new[] { 0, 1, 0, 1 }, pair.Host.Battle.Record.Events.Select(item => item.Side));
        Assert.False(pair.Host.DrawOffered || pair.Guest.DrawOffered);
        pair.PassTurn();
        pair.Same();
    }

    [Fact]
    public void RejectingDrawPreservesAnAlreadyCommittedActionAndDoesNotResetItsClock()
    {
        var pair = new Pair(timer: true);
        pair.Host.Tick(3);
        pair.Guest.Tick(3);
        pair.Host.Submit(BattleAction.Skill(0));
        pair.Pump();
        pair.Guest.OfferDraw();
        pair.Pump();
        pair.Host.RespondDraw(false);
        pair.Pump();
        Assert.True(pair.Host.HasSubmitted);
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Equal(17, pair.Guest.Clock.TurnLeft(1));
        Assert.Single(pair.A.Messages("commit"));
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(BattleAction.Skill(0).ToWire(), pair.Host.Battle.Record.Events.Last().A0.ToWire());
        Assert.Single(pair.A.Messages("commit"));
        pair.Same();
    }

    [Fact]
    public void OfferRateLimitIsPerSideAndAllowsTheExactFifthTurn()
    {
        var pair = new Pair();
        pair.Host.OfferDraw();
        pair.Pump();
        pair.Guest.RespondDraw(false);
        pair.Pump();
        Assert.False(pair.Host.CanOfferDraw);
        Assert.True(pair.Guest.CanOfferDraw);
        Assert.Throws<InvalidOperationException>(() => pair.Host.OfferDraw());
        for (int i = 0; i < 4; i++) pair.PassTurn();
        Assert.Equal(5, pair.Host.Battle.Turn);
        Assert.False(pair.Host.CanOfferDraw);
        pair.PassTurn();
        Assert.Equal(6, pair.Host.Battle.Turn);
        Assert.True(pair.Host.CanOfferDraw);
        pair.Host.OfferDraw();
        pair.Pump();
        pair.Guest.RespondDraw(true);
        pair.Pump();
        Assert.Equal(NetState.Over, pair.Host.State);
        pair.Same();
    }

    [Fact]
    public void ADrawOfferCrossingACommitAndRevealCannotReorderTheRecord()
    {
        var pair = new Pair();
        pair.Host.Submit(BattleAction.Skill(0));
        pair.Guest.Poll();
        pair.B.Pause = true;
        pair.Guest.Submit(BattleAction.Pass());
        pair.Host.OfferDraw();
        pair.Guest.Poll();
        pair.B.ReleaseNext("commit");
        pair.Host.Poll();
        pair.B.ReleaseNext("reveal");
        pair.Host.Poll();
        pair.B.ReleaseNext("termination-ready");
        pair.Host.Poll();
        pair.B.ReleaseAll();
        pair.Pump();
        Assert.True(pair.Guest.DrawOffered);
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(new[] { BattleRecordEventType.DrawOffer, BattleRecordEventType.Turn },
            pair.Host.Battle.Record.Events.Select(item => item.Type));
        pair.Guest.RespondDraw(true);
        pair.Pump();
        pair.Same();
    }

    [Fact]
    public void FinalHashIsRequiredAndCannotBeBypassedByMoreControlInput()
    {
        var pair = new Pair();
        pair.Host.Surrender();
        pair.Guest.Poll();
        pair.Host.Poll();
        pair.B.Pause = true;
        pair.Guest.Poll();
        Assert.Equal(Phase.Over, pair.Host.Battle.Phase);
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.False(pair.Host.CanSurrender || pair.Host.CanOfferDraw || pair.Host.CanRespondDraw);
        Assert.Throws<InvalidOperationException>(() => pair.Host.Surrender());
        pair.B.ReleaseNext("hash");
        pair.Pump();
        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(NetState.Over, pair.Guest.State);
        pair.Same();
    }

    [Fact]
    public void CapabilityAndProtocolVersionAreExplicitAndLegacyTrafficStaysLegacy()
    {
        var old = new Pair(enabled: false);
        using (var hello = JsonDocument.Parse(old.A.Messages("hello").Single()))
        {
            Assert.Equal(6, hello.RootElement.GetProperty("version").GetInt32());
            Assert.False(hello.RootElement.TryGetProperty("terminationVersion", out _));
        }
        Assert.False(old.Host.CanSurrender);
        Assert.Throws<InvalidOperationException>(() => old.Host.Submit(BattleAction.Surrender()));
        old.PassTurn();
        Assert.DoesNotContain(old.A.Sent, message => Field(message, "type").StartsWith("termination-", StringComparison.Ordinal));
        var modern = new Pair();
        using var newHello = JsonDocument.Parse(modern.A.Messages("hello").Single());
        Assert.Equal(6, newHello.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(1, newHello.RootElement.GetProperty("terminationVersion").GetInt32());
        Assert.Equal(new[] { "contentHash", "name", "packId", "seedCommit", "side", "team", "terminationVersion", "timerVersion", "type", "version" },
            newHello.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        var mismatch = new Pair(handshake: false, guestEnabled: false);
        mismatch.Pump();
        Assert.Equal(NetState.Error, mismatch.Host.State);
        Assert.Equal(NetState.Error, mismatch.Guest.State);
        Assert.Null(mismatch.Host.Battle);
        Assert.Null(mismatch.Guest.Battle);
    }

    [Fact]
    public void VersionSixWithoutTerminationCapabilityIsRejected()
    {
        var pair = new Pair(handshake: false);
        Assert.True(pair.A.TryReceive(out string hello));
        var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(hello);
        Assert.True(fields.Remove("terminationVersion"));
        pair.A.Inject(JsonSerializer.Serialize(fields));
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("terminationVersion", pair.Host.Error);
        Assert.Null(pair.Host.Battle);
    }

    [Fact]
    public void TimedBarrierReportsMissingPeerInsteadOfAutoSubmittingDuringTheFreeze()
    {
        var pair = new Pair(timer: true);
        pair.Host.OfferDraw();
        pair.Host.Tick(9.9);
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Empty(pair.A.Messages("commit"));
        pair.Host.Tick(0.2);
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("同步", pair.Host.Error);
        Assert.Empty(pair.Host.Battle.Record.Events);
    }

    [Fact]
    public void HostCannotForgeTheGuestsConfirmedControlRequest()
    {
        var pair = new Pair();
        pair.A.Rewrite = message => Field(message, "type") == "termination-apply"
            ? message.Replace("\"requests\":[\"none\",\"offer\"]", "\"requests\":[\"none\",\"surrender\"]") : message;
        pair.Guest.OfferDraw();
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Contains("改变", pair.Guest.Error);
        Assert.Empty(pair.Guest.Battle.Record.Events);
    }

    [Fact]
    public void NegotiatedControlMessagesMatchTheirFixedWireExamples()
    {
        var pair = new Pair();
        pair.Guest.OfferDraw();
        pair.Pump();
        foreach (string kind in new[] { "wake", "freeze", "ready", "apply" })
        {
            var sender = kind == "wake" || kind == "ready" ? pair.B : pair.A;
            string actual = Assert.Single(sender.Messages("termination-" + kind));
            string expected = File.ReadAllText(Path.Combine(GoldenDirectory(), "t98-termination-" + kind + ".json")).Trim();
            Assert.Equal(expected, actual);
        }
        pair.Same();
    }

    [Theory]
    [InlineData("phase", "missing")]
    [InlineData("turn", "missing")]
    [InlineData("seq", "missing")]
    [InlineData("phase", "wrong")]
    [InlineData("seq", "wrong")]
    [InlineData("seq", "text")]
    [InlineData("type", "unknown")]
    public void InvalidFreezeHeadersFailBeforeMutatingTheBattle(string field, string mutation)
    {
        var pair = new Pair();
        ulong before = pair.Guest.Battle.StateHash();
        pair.Host.OfferDraw();
        Assert.True(pair.B.TryReceive(out string message));
        Assert.Equal("termination-freeze", Field(message, "type"));
        var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
        if (mutation == "missing") Assert.True(fields.Remove(field));
        else fields[field] = mutation == "unknown" ? (object)"termination-other" : mutation == "text" ? "1" : 2;
        pair.B.Inject(JsonSerializer.Serialize(fields));
        pair.Guest.Poll();
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Equal(before, pair.Guest.Battle.StateHash());
        Assert.Empty(pair.Guest.Battle.Record.Events);
    }

    [Theory]
    [InlineData("request", "missing")]
    [InlineData("request", "unknown")]
    [InlineData("resolve", "missing")]
    [InlineData("resolve", "text")]
    public void InvalidReadinessCannotApplyAControlOrResolveAnAction(string field, string mutation)
    {
        var pair = new Pair();
        ulong before = pair.Host.Battle.StateHash();
        pair.Host.OfferDraw();
        pair.Guest.Poll();
        Assert.True(pair.A.TryReceive(out string message));
        Assert.Equal("termination-ready", Field(message, "type"));
        var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
        if (mutation == "missing") Assert.True(fields.Remove(field));
        else fields[field] = mutation == "unknown" ? "bad-request" : "false";
        pair.A.Inject(JsonSerializer.Serialize(fields));
        pair.Host.Poll();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(before, pair.Host.Battle.StateHash());
        Assert.Empty(pair.Host.Battle.Record.Events);
    }

    [Fact]
    public void AFinalControlHashMismatchNeverBecomesAnAcceptedResult()
    {
        var pair = new Pair();
        pair.B.Rewrite = message => Field(message, "type") == "hash"
            ? message.Replace("\"hash\":\"", "\"hash\":\"f") : message;
        pair.Host.Surrender();
        pair.Pump();
        Assert.Equal(NetState.Desync, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Contains("哈希", pair.Host.Error);
    }

    [Fact]
    public void ARealKnockoutCanBeFollowedByNetworkSurrenderAndReplayed()
    {
        var pair = new Pair();
        for (int step = 0; step < 120 && pair.Host.Battle.Phase == Phase.Choose; step++)
        {
            pair.Host.Submit(Ai.Choose(pair.Host.Battle.Clone(), 0));
            pair.Guest.Submit(Ai.Choose(pair.Guest.Battle.Clone(), 1));
            pair.Pump();
        }
        Assert.Equal(Phase.Replace, pair.Host.Battle.Phase);
        Assert.Equal(Phase.Replace, pair.Guest.Battle.Phase);
        int surrendering = pair.Host.Battle.Need[0] ? 0 : 1;
        (surrendering == 0 ? pair.Host : pair.Guest).Surrender();
        pair.Pump();
        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(NetState.Over, pair.Guest.State);
        Assert.Equal(1 - surrendering, pair.Host.Battle.Winner);
        pair.Same();
    }

    [Fact]
    public void ADrawOfferBeforeTheNaturalLastTurnDoesNotLoseThatTurnOrItsFinalHash()
    {
        var pair = new Pair();
        for (int step = 0; step < 1000; step++)
        {
            if (pair.Host.Battle.Phase == Phase.Choose)
            {
                var action0 = Ai.Choose(pair.Host.Battle.Clone(), 0);
                var action1 = Ai.Choose(pair.Guest.Battle.Clone(), 1);
                var expected = pair.Host.Battle.Clone();
                expected.ResolveTurn(action0, action1);
                pair.Host.Submit(action0);
                pair.Guest.Submit(action1);
                if (expected.Phase == Phase.Over)
                {
                    pair.Host.OfferDraw();
                    pair.Pump();
                    Assert.Equal(NetState.Over, pair.Host.State);
                    Assert.Equal(NetState.Over, pair.Guest.State);
                    Assert.Equal(expected.Winner, pair.Host.Battle.Winner);
                    var events = pair.Host.Battle.Record.Events;
                    Assert.Equal(BattleRecordEventType.DrawOffer, events[events.Count - 2].Type);
                    Assert.Equal(BattleRecordEventType.Turn, events.Last().Type);
                    pair.Same();
                    return;
                }
            }
            else
            {
                Assert.Equal(Phase.Replace, pair.Host.Battle.Phase);
                foreach (var session in new[] { pair.Host, pair.Guest })
                    if (session.State == NetState.Replace) session.SubmitReplace(Ai.ChooseReplacement(session.Battle.Clone(), session.MySide));
            }
            pair.Pump();
        }
        Assert.Fail("完整对局没有到达可验证的自然终局。");
    }

    [Fact]
    public void LegacyVersionThreeRejectsANewSurrenderWireEvenWithAValidCommitment()
    {
        var pair = new Pair(enabled: false);
        pair.Host.Submit(BattleAction.Pass());
        string wire = BattleAction.Surrender().ToWire(), nonce = "00000000000000000000000000000000";
        using var sha = SHA256.Create();
        string digest = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(wire + "|" + nonce))).ToLowerInvariant();
        pair.A.Inject(JsonSerializer.Serialize(new { type = "commit", turn = 1, kind = "action", digest }));
        pair.A.Inject(JsonSerializer.Serialize(new { type = "reveal", turn = 1, kind = "action", action = wire, nonce }));
        pair.Host.Poll();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Contains("终止协议", pair.Host.Error);
        Assert.Empty(pair.Host.Battle.Record.Events);
    }
}
