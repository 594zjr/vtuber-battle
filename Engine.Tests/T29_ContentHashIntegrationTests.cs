using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T29_ContentHashIntegrationTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    sealed class Transport : INetTransport
    {
        readonly Queue<string> incoming = new Queue<string>();
        public Transport Peer;
        public Func<string, string> Rewrite;
        public readonly List<string> Sent = new List<string>();

        public void Send(string message)
        {
            if (Rewrite != null && MessageType(message) == "hello") message = Rewrite(message);
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
        public readonly Transport HostTransport = new Transport();
        public readonly Transport GuestTransport = new Transport();
        public readonly NetSession Host;
        public readonly NetSession Guest;

        public Pair(ContentDb hostDb, ContentDb guestDb, Func<string, string> rewriteGuest = null)
        {
            HostTransport.Peer = GuestTransport;
            GuestTransport.Peer = HostTransport;
            GuestTransport.Rewrite = rewriteGuest;
            Host = new NetSession(hostDb, HostTransport, true, HostTeam, "主机", 42);
            Guest = new NetSession(guestDb, GuestTransport, false, GuestTeam, "客机", null);
        }

        public void Pump()
        {
            for (int index = 0; index < 4; index++) { Host.Poll(); Guest.Poll(); }
        }
    }

    [Fact]
    public void BothHellosCarryContentHashAndIdenticalContentCompletesHandshake()
    {
        var hostDb = BattleTests.LoadPack("original");
        var guestDb = BattleTests.LoadPack("original");
        var pair = new Pair(hostDb, guestDb);
        Assert.Null(pair.Host.Battle);
        Assert.Null(pair.Guest.Battle);
        foreach (var transport in new[] { pair.HostTransport, pair.GuestTransport })
        {
            using var hello = JsonDocument.Parse(Assert.Single(transport.Sent));
            Assert.Equal("hello", hello.RootElement.GetProperty("type").GetString());
            Assert.Equal(6, hello.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(hostDb.ContentHash(), hello.RootElement.GetProperty("contentHash").GetString());
        }

        pair.Pump();

        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Equal(hostDb.ContentHash(), pair.Host.Battle.Record.ContentHash);
        Assert.Equal(guestDb.ContentHash(), pair.Guest.Battle.Record.ContentHash);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SamePackWithDifferentNumbersOrDisplayTextIsRejectedBeforeBattle(bool displayText)
    {
        var hostDb = BattleTests.LoadPack("original");
        var guestDb = BattleTests.LoadPack("original");
        if (displayText) guestDb.Streamer("bell").Name += "（不同显示名）";
        else guestDb.Streamer("bell").Stats["hp"]++;
        string hostHash = hostDb.ContentHash();
        string guestHash = guestDb.ContentHash();
        Assert.Equal(hostDb.PackId, guestDb.PackId);
        Assert.NotEqual(hostHash, guestHash);
        var pair = new Pair(hostDb, guestDb);

        pair.Pump();

        foreach (var session in new[] { pair.Host, pair.Guest })
        {
            Assert.Equal(NetState.Error, session.State);
            Assert.Null(session.Battle);
            Assert.Contains("内容不一致", session.Error);
            Assert.Contains(hostHash, session.Error);
            Assert.Contains(guestHash, session.Error);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong")]
    [InlineData("case")]
    [InlineData("number")]
    public void MissingOrIncorrectHelloHashIsRejectedAndNotifiesThePeer(string mode)
    {
        var hostDb = BattleTests.LoadPack("original");
        string hash = hostDb.ContentHash();
        if (mode == "case") Assert.NotEqual(hash, hash.ToUpperInvariant());
        var pair = new Pair(hostDb, BattleTests.LoadPack("original"), message =>
        {
            var hello = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
            if (mode == "missing") hello.Remove("contentHash");
            else hello["contentHash"] = mode == "number" ? (object)123
                : mode == "case" ? hash.ToUpperInvariant() : new string('0', hash.Length);
            return JsonSerializer.Serialize(hello);
        });

        pair.Host.Poll();

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Null(pair.Host.Battle);
        Assert.False(string.IsNullOrEmpty(pair.Host.Error));
        string notification = Assert.Single(pair.HostTransport.Sent, message => MessageType(message) == "error");
        using (var error = JsonDocument.Parse(notification))
            Assert.Equal(pair.Host.Error, error.RootElement.GetProperty("message").GetString());
        pair.Guest.Poll();
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Contains(pair.Host.Error, pair.Guest.Error);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void ContentChangedAfterHelloIsRejectedEvenIfItNowMatchesThePeer(int changedSide, bool nowMatchesPeer)
    {
        var databases = new[] { BattleTests.LoadPack("original"), BattleTests.LoadPack("original") };
        if (nowMatchesPeer) databases[1 - changedSide].Streamer("bell").Stats["hp"]++;
        string sentHash = databases[changedSide].ContentHash();
        var pair = new Pair(databases[0], databases[1]);

        // 当前内容与对端相同也不能掩盖已经发出的旧指纹。
        databases[changedSide].Streamer("bell").Stats["hp"]++;
        Assert.NotEqual(sentHash, databases[changedSide].ContentHash());
        if (nowMatchesPeer) Assert.Equal(databases[0].ContentHash(), databases[1].ContentHash());
        pair.Pump();

        Assert.Equal(NetState.Error, pair.Host.State);
        Assert.Equal(NetState.Error, pair.Guest.State);
        var changed = changedSide == 0 ? pair.Host : pair.Guest;
        Assert.Null(changed.Battle);
        Assert.False(string.IsNullOrEmpty(changed.Error));
    }

    [Fact]
    public void CompletedNetworkMatchReplaysWithIdenticalHashAndFullLog()
    {
        var pair = new Pair(BattleTests.LoadPack("original"), BattleTests.LoadPack("original"));
        for (int guard = 0; guard < 5000; guard++)
        {
            pair.Pump();
            foreach (var session in new[] { pair.Host, pair.Guest })
            {
                Assert.NotEqual(NetState.Error, session.State);
                Assert.NotEqual(NetState.Desync, session.State);
                if (session.State == NetState.Choose && !session.HasSubmitted)
                    session.Submit(Ai.Choose(session.Battle, session.MySide));
                if (session.State == NetState.Replace && !session.HasSubmitted)
                    session.SubmitReplace(Ai.ChooseReplacement(session.Battle, session.MySide));
            }
            if (pair.Host.State == NetState.Over && pair.Guest.State == NetState.Over) break;
        }
        Assert.Equal(NetState.Over, pair.Host.State);
        Assert.Equal(NetState.Over, pair.Guest.State);
        string json = pair.Host.Battle.Record.ToJson();
        Assert.Equal(json, pair.Guest.Battle.Record.ToJson());
        var replayDb = BattleTests.LoadPack("original");
        var record = BattleRecord.FromJson(json);
        Assert.Equal(replayDb.ContentHash(), record.ContentHash);
        Assert.NotEmpty(record.Events);

        var replayed = Replay.Run(replayDb, record);

        Assert.Equal(Phase.Over, replayed.Phase);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
        Assert.Equal(pair.Host.Battle.StateHash(), replayed.StateHash());
        Assert.Equal(FullLog(pair.Host.Battle), FullLog(pair.Guest.Battle));
        Assert.Equal(FullLog(pair.Host.Battle), FullLog(replayed));
        Assert.Equal(json, replayed.Record.ToJson());
    }

    static string MessageType(string message)
    {
        using var parsed = JsonDocument.Parse(message);
        return parsed.RootElement.GetProperty("type").GetString();
    }

    static string[] FullLog(Battle battle)
    {
        return battle.Log.Select(entry => string.Join("|", entry.Turn, entry.Node, entry.Kind, entry.Side, entry.Text,
            string.Join(",", entry.SnapActive), string.Join(",", entry.SnapHp[0]), string.Join(",", entry.SnapHp[1]))).ToArray();
    }
}
