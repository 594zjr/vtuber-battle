using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T57_RatingIntegrationReviewTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    sealed class Transport : INetTransport
    {
        readonly Queue<string> incoming = new Queue<string>();
        public readonly List<string> Sent = new List<string>();
        public Transport Peer;

        public void Send(string message)
        {
            Sent.Add(message);
            Peer.incoming.Enqueue(message);
        }

        public bool TryReceive(out string message)
        {
            if (incoming.Count == 0) { message = null; return false; }
            message = incoming.Dequeue();
            return true;
        }
    }

    sealed class Pair
    {
        public readonly Transport A = new Transport(), B = new Transport();
        public readonly NetSession Host, Guest;

        public Pair(bool termination, bool timer, PlayerRating hostRating = null, PlayerRating guestRating = null)
        {
            A.Peer = B;
            B.Peer = A;
            var db = BattleTests.LoadPack("original");
            Host = new NetSession(db, A, true, HostTeam, "主机", 57,
                turnSeconds: timer ? 20 : (double?)null, bankSeconds: timer ? 30 : (double?)null,
                enableTermination: termination, rating: hostRating);
            Guest = new NetSession(db, B, false, GuestTeam, "客机", null,
                enableTermination: termination, rating: guestRating);
        }

        public void Pump()
        {
            for (int i = 0; i < 12; i++) { Host.Poll(); Guest.Poll(); }
            Assert.Null(Host.Error);
            Assert.Null(Guest.Error);
        }

        public void PassTurn()
        {
            Assert.Equal(NetState.Choose, Host.State);
            Assert.Equal(NetState.Choose, Guest.State);
            Host.Submit(BattleAction.Pass());
            Guest.Submit(BattleAction.Pass());
            Pump();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RatingSnapshotsCoexistWithCurrentProtocolAndHostTimerNegotiation(bool termination, bool timer)
    {
        var hostRating = new PlayerRating { Rating = 1777, Games = 12 };
        var guestRating = new PlayerRating { Rating = 1288, Games = 3 };
        var pair = new Pair(termination, timer, hostRating, guestRating);
        hostRating.Rating = 900;
        hostRating.Games = 80;
        guestRating.Rating = 2500;
        guestRating.Games = 60;
        pair.Pump();

        foreach (var session in new[] { pair.Host, pair.Guest })
        {
            Assert.Equal(NetState.Choose, session.State);
            Assert.Equal(termination ? 4 : 3, session.SessionProtocolVersion);
            Assert.Equal(termination, session.CanSurrender);
            if (timer)
            {
                Assert.NotNull(session.Clock);
                Assert.Equal(20.0, session.Clock.TurnLeft(session.MySide));
                Assert.Equal(30.0, session.Clock.BankLeft(session.MySide));
            }
            else Assert.Null(session.Clock);
        }

        using var hostHello = JsonDocument.Parse(pair.A.Sent[0]);
        using var guestHello = JsonDocument.Parse(pair.B.Sent[0]);
        foreach (var hello in new[] { hostHello.RootElement, guestHello.RootElement })
        {
            Assert.Equal(termination ? 4 : 3, hello.GetProperty("version").GetInt32());
            Assert.Equal(1, hello.GetProperty("timerVersion").GetInt32());
            Assert.Equal(termination, hello.TryGetProperty("terminationVersion", out var capability));
            if (termination) Assert.Equal(1, capability.GetInt32());
            Assert.True(hello.TryGetProperty("rating", out _));
        }
        Assert.Equal(timer, hostHello.RootElement.TryGetProperty("timer", out _));
        Assert.False(guestHello.RootElement.TryGetProperty("timer", out _));
        Assert.Equal(1288, pair.Host.OpponentRating.Rating);
        Assert.Equal(3, pair.Host.OpponentRating.Games);
        Assert.Equal(1777, pair.Guest.OpponentRating.Rating);
        Assert.Equal(12, pair.Guest.OpponentRating.Games);
        var hostView = pair.Host.OpponentRating;
        var guestView = pair.Guest.OpponentRating;
        hostView.Rating = 1;
        hostView.Games = 1;
        guestView.Rating = 2;
        guestView.Games = 2;
        Assert.Equal(1288, pair.Host.OpponentRating.Rating);
        Assert.Equal(3, pair.Host.OpponentRating.Games);
        Assert.Equal(1777, pair.Guest.OpponentRating.Rating);
        Assert.Equal(12, pair.Guest.OpponentRating.Games);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RatingMetadataDoesNotChangeTimeoutPlayOrConfirmedEndResults(bool termination, bool timer)
    {
        var hostRating = new PlayerRating { Rating = 1850, Games = 20 };
        var guestRating = new PlayerRating { Rating = 950, Games = 2 };
        var plain = new Pair(termination, timer);
        var rated = new Pair(termination, timer, hostRating, guestRating);
        plain.Pump();
        rated.Pump();
        foreach (var pair in new[] { plain, rated })
        {
            if (timer)
            {
                pair.Host.Tick(50);
                pair.Guest.Tick(50);
                pair.Pump();
                Assert.Equal(2, pair.Host.Battle.Turn);
                Assert.Equal(0.0, pair.Host.Clock.BankLeft(0));
                Assert.Equal(0.0, pair.Guest.Clock.BankLeft(1));
            }
            else pair.PassTurn();

            if (termination)
            {
                pair.Guest.Surrender();
                pair.Pump();
                Assert.Equal(0, pair.Host.Battle.Winner);
                Assert.Equal(BattleRecordEventType.Surrender, pair.Host.Battle.Record.Events.Last().Type);
            }
            else
            {
                for (int turns = 0; turns < 120 && pair.Host.State != NetState.Over; turns++) pair.PassTurn();
            }
            Assert.Equal(NetState.Over, pair.Host.State);
            Assert.Equal(NetState.Over, pair.Guest.State);
            Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
            Assert.Equal(pair.Host.Battle.Record.ToJson(), pair.Guest.Battle.Record.ToJson());
        }

        Assert.Equal(plain.Host.Battle.Winner, rated.Host.Battle.Winner);
        Assert.Equal(plain.Host.Battle.StateHash(), rated.Host.Battle.StateHash());
        Assert.Equal(plain.Host.Battle.Record.ToJson(), rated.Host.Battle.Record.ToJson());
        Assert.Equal(plain.Host.Battle.Log.Select(entry => entry.Text), rated.Host.Battle.Log.Select(entry => entry.Text));
        Assert.Equal(1850, hostRating.Rating);
        Assert.Equal(20, hostRating.Games);
        Assert.Equal(950, guestRating.Rating);
        Assert.Equal(2, guestRating.Games);
    }

    [Fact]
    public void ExistingSeventhPositionalNullStillMeansNoTimerAndCompilesWithoutOverloadAmbiguity()
    {
        var a = new Transport();
        var b = new Transport();
        a.Peer = b;
        b.Peer = a;
        var db = BattleTests.LoadPack("original");
        var host = new NetSession(db, a, true, HostTeam, "主机", 57, null);
        var guest = new NetSession(db, b, false, GuestTeam, "客机", null, null);
        for (int i = 0; i < 6; i++) { host.Poll(); guest.Poll(); }
        foreach (var session in new[] { host, guest })
        {
            Assert.Equal(NetState.Choose, session.State);
            Assert.Null(session.Clock);
            Assert.Equal(NetSession.ProtocolVersion, session.SessionProtocolVersion);
            Assert.Equal(1500, session.OpponentRating.Rating);
            Assert.Equal(0, session.OpponentRating.Games);
        }
        foreach (string message in new[] { a.Sent[0], b.Sent[0] })
        {
            using var hello = JsonDocument.Parse(message);
            Assert.False(hello.RootElement.TryGetProperty("rating", out _));
            Assert.False(hello.RootElement.TryGetProperty("timer", out _));
        }
    }
}
