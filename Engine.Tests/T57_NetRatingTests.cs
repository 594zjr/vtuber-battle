using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T57_NetRatingTests
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

        public void Inject(string message) { incoming.Enqueue(message); }
    }

    sealed class Pair
    {
        public readonly Transport A = new Transport(), B = new Transport();
        public readonly NetSession Host, Guest;

        public Pair(PlayerRating hostRating = null, PlayerRating guestRating = null)
        {
            A.Peer = B;
            B.Peer = A;
            Host = new NetSession(BattleTests.LoadPack("original"), A, true, HostTeam, "主机", 19, hostRating);
            Guest = new NetSession(BattleTests.LoadPack("original"), B, false, GuestTeam, "客机", null, guestRating);
        }

        public void Pump()
        {
            for (int i = 0; i < 6; i++) { Host.Poll(); Guest.Poll(); }
        }

        public void PassTurn()
        {
            Host.Submit(BattleAction.Pass());
            Guest.Submit(BattleAction.Pass());
            Pump();
        }
    }

    [Fact]
    public void RatingsAreHandshakeSnapshotsAndNeverAffectBattleHashRecordOrRandomSequence()
    {
        var plain = new Pair();
        var local = new PlayerRating { Rating = 1800, Games = 23 };
        var remote = new PlayerRating { Rating = -40, Games = 3 };
        var rated = new Pair(local, remote);
        Assert.Null(rated.Host.OpponentRating);
        Assert.Null(rated.Guest.OpponentRating);
        Assert.Null(rated.Host.Battle);
        Assert.Null(rated.Guest.Battle);
        local.Rating = 99;
        local.Games = 999;
        remote.Rating = 77;
        remote.Games = 777;

        rated.Pump();
        plain.Pump();
        Assert.Equal(NetState.Choose, rated.Host.State);
        Assert.Equal(NetState.Choose, rated.Guest.State);
        Assert.Equal(-40, rated.Host.OpponentRating.Rating);
        Assert.Equal(3, rated.Host.OpponentRating.Games);
        Assert.Equal(1800, rated.Guest.OpponentRating.Rating);
        Assert.Equal(23, rated.Guest.OpponentRating.Games);
        var snapshot = rated.Host.OpponentRating;
        snapshot.Rating = int.MaxValue;
        snapshot.Games = int.MaxValue;
        Assert.NotSame(snapshot, rated.Host.OpponentRating);
        Assert.Equal(-40, rated.Host.OpponentRating.Rating);
        Assert.Equal(3, rated.Host.OpponentRating.Games);

        Assert.Equal(plain.Host.Battle.StateHash(), rated.Host.Battle.StateHash());
        Assert.Equal(plain.Host.Battle.Record.ToJson(), rated.Host.Battle.Record.ToJson());
        plain.PassTurn();
        rated.PassTurn();
        Assert.Equal(NetState.Choose, rated.Host.State);
        Assert.Equal(NetState.Choose, rated.Guest.State);
        Assert.Equal(plain.Host.Battle.StateHash(), rated.Host.Battle.StateHash());
        Assert.Equal(rated.Host.Battle.StateHash(), rated.Guest.Battle.StateHash());
        Assert.Equal(plain.Host.Battle.Record.ToJson(), rated.Host.Battle.Record.ToJson());
        Assert.Equal(plain.Host.Battle.Log.Select(e => e.Text), rated.Host.Battle.Log.Select(e => e.Text));
        Assert.Equal(99, local.Rating);
        Assert.Equal(999, local.Games);
        Assert.Equal(77, remote.Rating);
        Assert.Equal(777, remote.Games);
        for (int i = 0; i < 4; i++) Assert.Equal(plain.Host.Battle.Random(), rated.Host.Battle.Random());
        using var hello = JsonDocument.Parse(rated.A.Sent[0]);
        Assert.Equal(2, hello.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(1800, hello.RootElement.GetProperty("rating").GetProperty("rating").GetInt32());
        Assert.Equal(23, hello.RootElement.GetProperty("rating").GetProperty("games").GetInt32());
    }

    [Fact]
    public void OldSixArgumentPeerOmitsMetadataAndReceivesTheDefaultRating()
    {
        var a = new Transport();
        var b = new Transport();
        a.Peer = b;
        b.Peer = a;
        var host = new NetSession(BattleTests.LoadPack("original"), a, true, HostTeam, "主机", 19,
            new PlayerRating { Rating = 1900, Games = 50 });
        var guest = new NetSession(BattleTests.LoadPack("original"), b, false, GuestTeam, "客机", null);
        Assert.Null(host.OpponentRating);
        for (int i = 0; i < 4; i++) { host.Poll(); guest.Poll(); }
        Assert.Equal(NetState.Choose, host.State);
        Assert.Equal(NetState.Choose, guest.State);
        Assert.Equal(1500, host.OpponentRating.Rating);
        Assert.Equal(0, host.OpponentRating.Games);
        Assert.Equal(1900, guest.OpponentRating.Rating);
        Assert.Equal(50, guest.OpponentRating.Games);
        using var hello = JsonDocument.Parse(b.Sent[0]);
        Assert.False(hello.RootElement.TryGetProperty("rating", out _));
        Assert.Equal(2, hello.RootElement.GetProperty("version").GetInt32());
    }

    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void HelloAcceptsAllSignedRatingsAndNonnegativeGameCounts(int rating, int games)
    {
        var pair = new Pair(new PlayerRating { Rating = rating, Games = games });
        pair.Pump();
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Equal(rating, pair.Guest.OpponentRating.Rating);
        Assert.Equal(games, pair.Guest.OpponentRating.Games);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("1500")]
    [InlineData("\"1500\"")]
    [InlineData("{}")]
    [InlineData("{\"rating\":1500}")]
    [InlineData("{\"games\":0}")]
    [InlineData("{\"rating\":1500,\"games\":-1}")]
    [InlineData("{\"rating\":1500,\"games\":0.5}")]
    [InlineData("{\"rating\":1500,\"games\":2147483648}")]
    [InlineData("{\"rating\":1500,\"games\":\"0\"}")]
    [InlineData("{\"rating\":1500.5,\"games\":0}")]
    [InlineData("{\"rating\":2147483648,\"games\":0}")]
    [InlineData("{\"rating\":-2147483649,\"games\":0}")]
    [InlineData("{\"rating\":null,\"games\":0}")]
    public void MalformedRemoteRatingStopsHandshakeBeforeCreatingBattleOrPublishingSnapshot(string rating)
    {
        var pair = new Pair();
        Assert.True(pair.A.TryReceive(out string hello));
        var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(hello);
        fields["rating"] = JsonSerializer.Deserialize<object>(rating);
        pair.A.Inject(JsonSerializer.Serialize(fields));
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Null(pair.Host.Battle);
        Assert.Null(pair.Host.OpponentRating);
        Assert.Matches("[\u4e00-\u9fff]", pair.Host.Error);
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Null(pair.Host.Battle);
    }

    [Fact]
    public void InvalidLocalRatingIsRejectedBeforeSendingAnyHello()
    {
        var transport = new Transport();
        transport.Peer = new Transport();
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetSession(BattleTests.LoadPack("original"),
            transport, true, HostTeam, "主机", 19, new PlayerRating { Games = -1 }));
        Assert.Empty(transport.Sent);
    }
}
