using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

[Collection("Localization current")]
public class T62_QuickChatEdgeTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    sealed class Transport : INetTransport
    {
        readonly INetTransport inner;
        readonly Queue<string> injected = new Queue<string>();
        public readonly List<string> Sent = new List<string>();
        public Func<string, string> RewriteNext;
        public NetWireTestTransport.PayloadSender Injector;

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
            if (injected.Count > 0) { message = injected.Dequeue(); return true; }
            return inner.TryReceive(out message);
        }
        public void Inject(string message) { Injector.Send(message); }
        public void InjectRaw(string message) { injected.Enqueue(message); }
        public string[] Chats()
        {
            return Sent.Where(message =>
            {
                using var parsed = JsonDocument.Parse(message);
                return parsed.RootElement.GetProperty("type").GetString() == "chat";
            }).ToArray();
        }
    }

    sealed class Pair
    {
        public readonly Transport A, B;
        public readonly NetSession Host, Guest;
        public Pair(bool handshake = true)
        {
            LoopbackPair.Create(out var a, out var b);
            A = new Transport(a);
            B = new Transport(b);
            Host = new NetSession(BattleTests.LoadPack("original"), A, true, HostTeam, "主机", 62);
            Guest = new NetSession(BattleTests.LoadPack("original"), B, false, GuestTeam, "客机", null);
            A.Injector = new NetWireTestTransport.PayloadSender(Guest, rewrite => B.RewriteNext = rewrite, A.InjectRaw);
            B.Injector = new NetWireTestTransport.PayloadSender(Host, rewrite => A.RewriteNext = rewrite, B.InjectRaw);
            if (handshake)
            {
                Pump();
                Assert.Equal(NetState.Choose, Host.State);
                Assert.Equal(NetState.Choose, Guest.State);
            }
        }
        public void Pump()
        {
            for (int i = 0; i < 8; i++) { Host.Poll(); Guest.Poll(); }
        }
        public void PassTurn()
        {
            Host.Submit(BattleAction.Pass());
            Guest.Submit(BattleAction.Pass());
            Pump();
        }
    }

    static string Chat(int index) { return JsonSerializer.Serialize(new { type = "chat", index }); }
    static string Log(Battle battle) { return JsonSerializer.Serialize(battle.Log, new JsonSerializerOptions { IncludeFields = true }); }

    [Fact]
    public void SenderUsesARollingWindowWithAnInclusiveTenSecondExpiry()
    {
        var pair = new Pair();
        Assert.True(pair.Host.SendQuickChat(0, 0));
        Assert.True(pair.Host.SendQuickChat(1, 1));
        Assert.True(pair.Host.SendQuickChat(2, 2));
        Assert.False(pair.Host.SendQuickChat(3, 9.999));
        Assert.True(pair.Host.SendQuickChat(3, 10));
        Assert.False(pair.Host.SendQuickChat(4, 10));
        Assert.True(pair.Host.SendQuickChat(4, 11));
        Assert.True(pair.Host.SendQuickChat(5, 12));
        Assert.False(pair.Host.SendQuickChat(6, 12));
        pair.Pump();

        Assert.Equal(6, pair.A.Chats().Length);
        Assert.Equal(Enumerable.Range(0, 6), pair.Guest.ReceivedChats.Select(c => c.Index));
        Assert.Empty(pair.Host.ReceivedChats);
    }

    [Fact]
    public void BadIndicesAndTimesDoNotConsumeQuotaOrPoisonTheClock()
    {
        var pair = new Pair();
        foreach (int index in new[] { int.MinValue, -1, 8, int.MaxValue })
            Assert.False(pair.Host.SendQuickChat(index, 999));
        foreach (double now in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1.0 })
            Assert.False(pair.Host.SendQuickChat(0, now));
        Assert.Empty(pair.A.Chats());

        Assert.True(pair.Host.SendQuickChat(0, 5));
        Assert.True(pair.Host.SendQuickChat(1, 5));
        Assert.True(pair.Host.SendQuickChat(2, 5));
        Assert.False(pair.Host.SendQuickChat(3, 8));
        Assert.False(pair.Host.SendQuickChat(3, 7));
        Assert.False(pair.Host.SendQuickChat(99, 1000));
        Assert.True(pair.Host.SendQuickChat(3, 15));
        Assert.False(pair.Host.SendQuickChat(4, 14.999));
        Assert.True(pair.Host.SendQuickChat(4, 15));
        Assert.True(pair.Host.SendQuickChat(5, 15));
        Assert.False(pair.Host.SendQuickChat(6, 15));
        Assert.Equal(6, pair.A.Chats().Length);
    }

    [Fact]
    public void SendingBeforeHandshakeDoesNotConsumeTheLaterSessionsTimeOrQuota()
    {
        var pair = new Pair(handshake: false);
        Assert.False(pair.Host.SendQuickChat(0, 100));
        Assert.Empty(pair.A.Chats());
        pair.Pump();

        Assert.True(pair.Host.SendQuickChat(0, 0));
        Assert.True(pair.Host.SendQuickChat(1, 0));
        Assert.True(pair.Host.SendQuickChat(2, 0));
        Assert.False(pair.Host.SendQuickChat(3, 0));
        pair.Pump();
        Assert.Equal(3, pair.Guest.ReceivedChats.Count);
    }

    [Fact]
    public void WireCarriesOnlyTheIndexAndReceivedMessagesUseTheOpponentsIdentity()
    {
        var pair = new Pair();
        Assert.True(NetSession.ProtocolVersion > 2);
        foreach (string hello in new[] { pair.A.Sent[0], pair.B.Sent[0] })
        {
            using var parsed = JsonDocument.Parse(hello);
            Assert.Equal(NetSession.ProtocolVersion, parsed.RootElement.GetProperty("version").GetInt32());
        }
        Assert.True(pair.Host.SendQuickChat(7, 0));
        Assert.True(pair.Guest.SendQuickChat(6, 0));
        pair.Pump();

        foreach (string message in pair.A.Chats().Concat(pair.B.Chats()))
        {
            using var parsed = JsonDocument.Parse(message);
            Assert.Equal(new[] { "index", "seq", "type" }, parsed.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
            Assert.InRange(parsed.RootElement.GetProperty("index").GetInt32(), 0, 7);
            Assert.True(parsed.RootElement.GetProperty("seq").GetInt64() > 0);
        }
        var hostReceived = Assert.Single(pair.Host.ReceivedChats);
        Assert.Equal((1, 6, 1), (hostReceived.Side, hostReceived.Index, hostReceived.Turn));
        var guestReceived = Assert.Single(pair.Guest.ReceivedChats);
        Assert.Equal((0, 7, 1), (guestReceived.Side, guestReceived.Index, guestReceived.Turn));
        if (pair.Guest.ReceivedChats is IList<QuickChatMessage> writable)
        {
            Assert.True(writable.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => writable.Add(guestReceived));
            Assert.Throws<NotSupportedException>(() => writable.Clear());
            Assert.Throws<NotSupportedException>(() => writable[0] = guestReceived);
        }
        pair.PassTurn();
        pair.B.Inject(Chat(2));
        pair.Pump();
        Assert.Equal((0, 7, 1), (guestReceived.Side, guestReceived.Index, guestReceived.Turn));
        Assert.Equal(2, pair.Guest.ReceivedChats.Last().Turn);
    }

    [Fact]
    public void VersionTwoHelloIsRejectedBeforeCreatingTheReceivingBattle()
    {
        var pair = new Pair(handshake: false);
        Assert.True(pair.B.TryReceive(out string hello));
        var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(hello);
        fields["version"] = 2;
        pair.B.Inject(JsonSerializer.Serialize(fields));

        pair.Pump();

        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.Null(pair.Guest.Battle);
        Assert.Contains("版本", pair.Guest.Error);
        Assert.Contains("2", pair.Guest.Error);
        Assert.Empty(pair.Guest.ReceivedChats);
    }

    [Fact]
    public void ReceiverDropsBeyondSixPerTurnAndStartsANewQuotaOnlyOnTheNextTurn()
    {
        var pair = new Pair();
        for (int i = 0; i < 10; i++) pair.B.Inject(Chat(i % 8));
        pair.Pump();
        Assert.Equal(Enumerable.Range(0, 6), pair.Guest.ReceivedChats.Select(c => c.Index));
        Assert.All(pair.Guest.ReceivedChats, c => Assert.Equal((0, 1), (c.Side, c.Turn)));
        pair.B.Inject(Chat(7));
        pair.A.Inject(Chat(3));
        pair.Pump();
        Assert.Equal(6, pair.Guest.ReceivedChats.Count);
        Assert.Single(pair.Host.ReceivedChats);

        pair.PassTurn();
        for (int i = 0; i < 8; i++) pair.B.Inject(Chat(i));
        pair.Pump();
        Assert.Equal(12, pair.Guest.ReceivedChats.Count);
        Assert.All(pair.Guest.ReceivedChats.Skip(6), c => Assert.Equal((0, 2), (c.Side, c.Turn)));
        Assert.Equal(NetState.Choose, pair.Guest.State);
    }

    [Fact]
    public void MutedMessagesStayHiddenButStillCountTowardTheReceiverLimit()
    {
        var pair = new Pair();
        pair.Guest.MuteOpponent = true;
        for (int i = 0; i < 3; i++) pair.B.Inject(Chat(i));
        pair.Pump();
        Assert.Empty(pair.Guest.ReceivedChats);
        pair.Guest.MuteOpponent = false;
        for (int i = 3; i < 8; i++) pair.B.Inject(Chat(i));
        pair.Pump();
        Assert.Equal(new[] { 3, 4, 5 }, pair.Guest.ReceivedChats.Select(c => c.Index));
        pair.Guest.MuteOpponent = true;
        pair.B.Inject(Chat(0));
        pair.Pump();
        Assert.Equal(3, pair.Guest.ReceivedChats.Count);
        pair.PassTurn();
        pair.Guest.MuteOpponent = false;
        pair.B.Inject(Chat(7));
        pair.Pump();
        Assert.Equal(4, pair.Guest.ReceivedChats.Count);
        Assert.Equal(2, pair.Guest.ReceivedChats.Last().Turn);
    }

    [Fact]
    public void InvalidChatPayloadsAreIgnoredWithoutUsingQuotaOrTouchingBattleData()
    {
        var pair = new Pair();
        ulong hash = pair.Guest.Battle.StateHash();
        string log = Log(pair.Guest.Battle), record = pair.Guest.Battle.Record.ToJson();
        var bad = new[]
        {
            "{\"type\":\"chat\"}",
            "{\"type\":\"chat\",\"index\":null}",
            "{\"type\":\"chat\",\"index\":\"0\"}",
            "{\"type\":\"chat\",\"index\":true}",
            "{\"type\":\"chat\",\"index\":1.5}",
            "{\"type\":\"chat\",\"index\":-1}",
            "{\"type\":\"chat\",\"index\":8}",
            "{\"type\":\"chat\",\"index\":1e100}",
            "{\"type\":\"chat\",\"index\":{}}",
            "{\"type\":\"chat\",\"index\":[]}",
            "{\"type\":\"chat\",\"index\":0,\"side\":1}",
            "{\"type\":\"chat\",\"index\":0,\"turn\":999}",
            "{\"type\":\"chat\",\"index\":0,\"text\":\"任意文字\"}",
            "{\"type\":\"chat\",\"index\":",
            "{\"type\":\"chat\",\"index\":1e999}",
            "{\"type\":\"chat\",\"index\":0} trailing",
            "{\"ty\\u0070e\":\"ch\\u0061t\",\"index\":",
            "{\"type\":\"chat\",\"index\":" + new string('[', 100) + "0" + new string(']', 100) + "}"
        };
        foreach (string message in bad)
        {
            pair.B.Inject(message);
            pair.Pump();
            Assert.Equal(NetState.Choose, pair.Guest.State);
            Assert.Null(pair.Guest.Error);
            Assert.Empty(pair.Guest.ReceivedChats);
        }
        Assert.Equal(hash, pair.Guest.Battle.StateHash());
        Assert.Equal(log, Log(pair.Guest.Battle));
        Assert.Equal(record, pair.Guest.Battle.Record.ToJson());
        for (int i = 0; i < 6; i++) pair.B.Inject("{\"index\":" + i + ",\"type\":\"chat\"}");
        pair.Pump();
        Assert.Equal(6, pair.Guest.ReceivedChats.Count);
    }

    [Theory]
    [InlineData("not JSON chat")]
    [InlineData("{\"type\":\"commit\",\"nested\":{\"type\":\"chat\"},")]
    [InlineData("{\"type\":\"hash\",\"text\":\"chat\",")]
    [InlineData("{\"text\":\"chat\",\"index\":")]
    public void UnrelatedMalformedProtocolMessagesStillFaultTheSession(string message)
    {
        var pair = new Pair();
        pair.B.Inject(message);
        pair.Pump();
        Assert.Equal(NetState.Error, pair.Guest.State);
        Assert.NotEmpty(pair.Guest.Error);
        Assert.Empty(pair.Guest.ReceivedChats);
        int sent = pair.B.Sent.Count;
        Assert.False(pair.Guest.SendQuickChat(0, 0));
        Assert.Equal(sent, pair.B.Sent.Count);
    }

    [Fact]
    public void ChatWhileWaitingForTheOpponentDoesNotAdvanceOrInvalidateTheAction()
    {
        var pair = new Pair();
        pair.Host.Submit(BattleAction.Pass());
        pair.Pump();
        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.Equal(NetState.Choose, pair.Guest.State);
        ulong hash = pair.Host.Battle.StateHash();
        string record = pair.Host.Battle.Record.ToJson();
        Assert.True(pair.Host.SendQuickChat(1, 0));
        Assert.True(pair.Guest.SendQuickChat(2, 0));
        pair.Pump();

        Assert.Equal(NetState.Waiting, pair.Host.State);
        Assert.True(pair.Host.HasSubmitted);
        Assert.False(pair.Guest.HasSubmitted);
        Assert.Equal(hash, pair.Host.Battle.StateHash());
        Assert.Equal(record, pair.Host.Battle.Record.ToJson());
        pair.Guest.Submit(BattleAction.Pass());
        pair.Pump();
        Assert.Equal(NetState.Choose, pair.Host.State);
        Assert.Equal(2, pair.Host.Battle.Turn);
        Assert.Equal(pair.Host.Battle.StateHash(), pair.Guest.Battle.StateHash());
    }

    [Fact]
    public void CompleteMatchHasIdenticalLogsRecordHashAndRngWithOrWithoutChat()
    {
        var chat = new Pair();
        var silent = new Pair();
        int step = 0;
        while (chat.Host.State != NetState.Over && step < Battle.TurnCap * 3)
        {
            ulong before = chat.Host.Battle.StateHash();
            double nextRandom = chat.Host.Battle.Clone().Random();
            Assert.True(chat.Host.SendQuickChat(step % 8, step * 10.0));
            Assert.True(chat.Guest.SendQuickChat((step + 1) % 8, step * 10.0));
            chat.Pump();
            Assert.Equal(before, chat.Host.Battle.StateHash());
            Assert.Equal(nextRandom, chat.Host.Battle.Clone().Random());
            if (chat.Host.Battle.Phase == Phase.Choose)
            {
                var a0 = Ai.Choose(chat.Host.Battle, 0, AiLevel.Normal);
                var a1 = Ai.Choose(chat.Guest.Battle, 1, AiLevel.Normal);
                chat.Host.Submit(a0);
                chat.Guest.Submit(a1);
                silent.Host.Submit(a0);
                silent.Guest.Submit(a1);
            }
            else
            {
                Assert.Equal(Phase.Replace, chat.Host.Battle.Phase);
                if (chat.Host.Battle.Need[0])
                {
                    int to = Ai.ChooseReplacement(chat.Host.Battle, 0, AiLevel.Normal);
                    chat.Host.SubmitReplace(to);
                    silent.Host.SubmitReplace(to);
                }
                if (chat.Guest.Battle.Need[1])
                {
                    int to = Ai.ChooseReplacement(chat.Guest.Battle, 1, AiLevel.Normal);
                    chat.Guest.SubmitReplace(to);
                    silent.Guest.SubmitReplace(to);
                }
            }
            chat.Pump();
            silent.Pump();
            Assert.Equal(silent.Host.Battle.StateHash(), chat.Host.Battle.StateHash());
            Assert.Equal(chat.Host.Battle.StateHash(), chat.Guest.Battle.StateHash());
            Assert.Equal(silent.Host.Battle.StateHash(), silent.Guest.Battle.StateHash());
            step++;
        }
        Assert.Equal(NetState.Over, chat.Host.State);
        Assert.Equal(NetState.Over, chat.Guest.State);
        Assert.Equal(NetState.Over, silent.Host.State);
        Assert.Equal(NetState.Over, silent.Guest.State);
        Assert.NotEmpty(chat.Host.ReceivedChats);
        Assert.NotEmpty(chat.Guest.ReceivedChats);
        Assert.Empty(silent.Host.ReceivedChats);
        foreach (var session in new[] { chat.Host, chat.Guest, silent.Guest })
        {
            Assert.Equal(Log(silent.Host.Battle), Log(session.Battle));
            Assert.Equal(silent.Host.Battle.Record.ToJson(), session.Battle.Record.ToJson());
            Assert.Equal(silent.Host.Battle.Clone().Random(), session.Battle.Clone().Random());
        }
        var replay = Replay.Run(chat.Host.Battle.Db, BattleRecord.FromJson(chat.Host.Battle.Record.ToJson()));
        Assert.Equal(chat.Host.Battle.StateHash(), replay.StateHash());
        Assert.Equal(Log(chat.Host.Battle), Log(replay));

        int previousChats = chat.Guest.ReceivedChats.Count;
        ulong endedHash = chat.Guest.Battle.StateHash();
        Assert.True(chat.Host.SendQuickChat(7, step * 10.0));
        chat.Pump();
        Assert.Equal(NetState.Over, chat.Host.State);
        Assert.Equal(NetState.Over, chat.Guest.State);
        Assert.Equal(previousChats + 1, chat.Guest.ReceivedChats.Count);
        Assert.Equal(endedHash, chat.Guest.Battle.StateHash());
        chat.A.Inject("{\"type\":\"error\",\"message\":\"对端终局检查失败\"}");
        chat.Pump();
        Assert.Equal(NetState.Error, chat.Host.State);
        Assert.Contains("对端终局检查失败", chat.Host.Error);
    }

    [Fact]
    public void GlobalLocalizationSuppliesAllEightLinesAndTextFollowsLaterLanguageChanges()
    {
        var previous = Loc.Current;
        try
        {
            Loc.Current = null;
            var chinese = Loc.Current;
            Assert.Equal(8, QuickChat.Lines.Length);
            for (int i = 0; i < 8; i++)
            {
                string key = "quickchat." + i;
                Assert.True(chinese.Has(key));
                Assert.False(string.IsNullOrWhiteSpace(QuickChat.Lines[i]));
                Assert.Equal(chinese.T(key), QuickChat.Text(i));
            }
            Loc.Current = Loc.FromJson("{\"language\":\"custom\",\"texts\":{\"quickchat.0\":\"自定义问候\",\"quickchat.7\":\"自定义晚安\"}}", chinese);
            Assert.Equal("自定义问候", QuickChat.Text(0));
            Assert.Equal("自定义晚安", QuickChat.Text(7));
            Assert.Equal(chinese.T("quickchat.1"), QuickChat.Text(1));
            Loc.Current = Loc.FromJson("{\"language\":\"next\",\"texts\":{\"quickchat.0\":\"第二种问候\"}}", chinese);
            Assert.Equal("第二种问候", QuickChat.Text(0));
        }
        finally { Loc.Current = previous; }
    }
}
