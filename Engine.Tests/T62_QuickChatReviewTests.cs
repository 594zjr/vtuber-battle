using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T62_QuickChatReviewTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    sealed class Transport : INetTransport
    {
        readonly INetTransport inner;
        public readonly List<string> Received = new List<string>();
        public readonly List<string> Sent = new List<string>();
        public Func<string, string> RewriteNext;

        public Transport(INetTransport inner) { this.inner = inner; }

        public void Send(string message)
        {
            if (RewriteNext != null)
            {
                var rewrite = RewriteNext;
                RewriteNext = null;
                message = rewrite(message);
            }
            Sent.Add(message);
            inner.Send(message);
        }

        public bool TryReceive(out string message)
        {
            if (!inner.TryReceive(out message)) return false;
            Received.Add(message);
            return true;
        }
    }

    sealed class Pair
    {
        public readonly Transport A, B;
        public readonly NetSession Host, Guest;
        public readonly NetWireTestTransport.PayloadSender HostPayload;

        public Pair(bool zeroTimer = false)
        {
            LoopbackPair.Create(out var a, out var b);
            A = new Transport(a);
            B = new Transport(b);
            var db = BattleTests.LoadPack("original");
            Host = new NetSession(db, A, true, HostTeam, "主机", 62,
                zeroTimer ? 0 : (double?)null, zeroTimer ? 0 : (double?)null);
            Guest = new NetSession(db, B, false, GuestTeam, "客机", null);
            HostPayload = new NetWireTestTransport.PayloadSender(Host, rewrite => A.RewriteNext = rewrite, A.Send);
            for (int i = 0; i < 8; i++) { Host.Poll(); Guest.Poll(); }
            Assert.Equal(NetState.Choose, Host.State);
            Assert.Equal(NetState.Choose, Guest.State);
            A.Received.Clear();
            B.Received.Clear();
            A.Sent.Clear();
            B.Sent.Clear();
        }
    }

    static string Chat(int index) { return "{\"type\":\"chat\",\"index\":" + index + "}"; }

    [Theory]
    [InlineData("{\"index\":1e999,\"type\":\"chat\"}")]
    [InlineData("{\"index\":1e999,\"ty\\u0070e\":\"ch\\u0061t\"}")]
    [InlineData("{\"index\":1e999,\"type\":\"chat\",\"extra\":")]
    [InlineData("{\"index\":\"\\q\",\"type\":\"chat\"}")]
    [InlineData("{\"\\q\":0,\"type\":\"chat\",\"index\":")]
    public void MalformedChatWithARootTypeAfterOverflowingNumberIsIgnoredWithoutUsingQuota(string message)
    {
        var pair = new Pair();
        ulong hash = pair.Guest.Battle.StateHash();
        string record = pair.Guest.Battle.Record.ToJson();
        double nextRandom = pair.Guest.Battle.Clone().Random();
        pair.HostPayload.Send(message);
        pair.Guest.Poll();

        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Null(pair.Guest.Error);
        Assert.Empty(pair.Guest.ReceivedChats);
        for (int i = 0; i < 6; i++) pair.HostPayload.Send(Chat(i));
        pair.Guest.Poll();

        Assert.Equal(Enumerable.Range(0, 6), pair.Guest.ReceivedChats.Select(chat => chat.Index));
        Assert.Equal(hash, pair.Guest.Battle.StateHash());
        Assert.Equal(record, pair.Guest.Battle.Record.ToJson());
        Assert.Equal(nextRandom, pair.Guest.Battle.Clone().Random());
    }

    [Theory]
    [InlineData("{\"nested\":{\"type\":\"chat\"},\"index\":1e999}")]
    [InlineData("{\"text\":\"{\\\"type\\\":\\\"chat\\\"}\",\"index\":1e999}")]
    [InlineData("{\"type\":\"chat\",\"type\":\"commit\",\"index\":")]
    [InlineData("{\"type\":\"commit\",\"type\":\"chat\",\"index\":")]
    [InlineData("{\"type\":\"chat\",\"extra\":\"\\q\",\"type\":\"commit\"}")]
    [InlineData("{\"type\":\"chat\",\"\\q\":0,\"type\":\"commit\"}")]
    public void MalformedNonChatOrConflictingRootTypesStillFault(string message)
    {
        var pair = new Pair();
        ulong hash = pair.Guest.Battle.StateHash();
        string record = pair.Guest.Battle.Record.ToJson();
        pair.HostPayload.Send(message);

        pair.Guest.Poll();

        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.NotEmpty(pair.Guest.Error);
        Assert.Empty(pair.Guest.ReceivedChats);
        Assert.Equal(hash, pair.Guest.Battle.StateHash());
        Assert.Equal(record, pair.Guest.Battle.Record.ToJson());
    }

    [Fact]
    public void PollBoundsMixedChatWorkPreservesFifoAndLetsZeroBudgetTimeoutRun()
    {
        var pair = new Pair(zeroTimer: true);
        ulong hash = pair.Guest.Battle.StateHash();
        string record = pair.Guest.Battle.Record.ToJson();
        double nextRandom = pair.Guest.Battle.Clone().Random();
        var messages = new List<string>();
        for (int i = 0; i < 200; i++)
        {
            string message = i % 4 == 0 ? Chat(8)
                : i % 4 == 1 ? "{\"type\":\"chat\",\"index\":"
                : Chat(i % 8);
            // 独立的合法空白前缀让每条消息可区分，精确检查跨 Poll 的顺序。
            message = new string(' ', i) + message;
            messages.Add(pair.HostPayload.Send(message));
        }
        pair.Guest.MuteOpponent = true;

        pair.Guest.Poll();

        Assert.Equal(64, pair.B.Received.Count);
        Assert.Equal(messages.Take(64), pair.B.Received);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Empty(pair.Guest.ReceivedChats);
        Assert.Equal(hash, pair.Guest.Battle.StateHash());
        Assert.Equal(record, pair.Guest.Battle.Record.ToJson());
        Assert.Equal(nextRandom, pair.Guest.Battle.Clone().Random());

        pair.Guest.Tick(0);

        Assert.True(pair.Guest.HasSubmitted);
        Assert.True(pair.Guest.Clock.TimedOut(pair.Guest.MySide));
        Assert.Equal(NetState.Waiting, pair.Guest.State);
        string commit = Assert.Single(NetWireTestTransport.Business(pair.B.Sent));
        Assert.All(pair.B.Sent.Where(NetWireTestTransport.IsAck), ack => Assert.DoesNotContain("\"seq\"", ack));
        Assert.Contains("\"type\":\"commit\"", commit);
        var commitFields = Assert.IsType<Dictionary<string, object>>(MiniJson.Parse(commit));
        Assert.False(commitFields.ContainsKey("action"));
        Assert.False(commitFields.ContainsKey("nonce"));
        Assert.Empty(pair.Host.Battle.Record.Events);
        pair.Guest.MuteOpponent = false;
        foreach (int expected in new[] { 128, 192, 200 })
        {
            pair.Guest.Poll();
            Assert.Equal(expected, pair.B.Received.Count);
            Assert.Equal(messages.Take(expected), pair.B.Received);
            Assert.Equal(NetState.Waiting, pair.Guest.State);
        }
        pair.Guest.Poll();
        Assert.Equal(messages, pair.B.Received);
        Assert.Empty(pair.Guest.ReceivedChats);
        Assert.Equal(hash, pair.Guest.Battle.StateHash());
        Assert.Equal(record, pair.Guest.Battle.Record.ToJson());
        Assert.Equal(nextRandom, pair.Guest.Battle.Clone().Random());

        pair.Host.Tick(0);
        for (int i = 0; i < 8; i++) { pair.Host.Poll(); pair.Guest.Poll(); }
        Assert.Contains(pair.B.Sent, message => message.Contains("\"type\":\"reveal\"")
            && message.Contains("\"action\":\"P\""));
        Assert.Equal(NetState.Choose, pair.Guest.State);
        Assert.Equal(2, pair.Guest.Battle.Turn);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
    }
}
