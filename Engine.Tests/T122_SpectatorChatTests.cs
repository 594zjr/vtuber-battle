using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T122_SpectatorChatTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    sealed class Transport : INetTransport
    {
        readonly INetTransport inner;
        public readonly List<string> Sent = new List<string>();
        public int Received;
        public bool FailSend;

        public Transport(INetTransport inner) { this.inner = inner; }
        public void Send(string message)
        {
            if (FailSend) throw new InvalidOperationException("测试发送失败");
            Sent.Add(message);
            inner.Send(message);
        }
        public bool TryReceive(out string message)
        {
            if (!inner.TryReceive(out message)) return false;
            Received++;
            return true;
        }
    }

    sealed class Viewer
    {
        public readonly Transport HostWire, ClientWire;
        public readonly SpectatorClient Client;

        public Viewer(ContentDb db, SpectatorFeed feed, string name = "观众", bool enabled = true)
        {
            LoopbackPair.Create(out var host, out var client);
            HostWire = new Transport(host);
            ClientWire = new Transport(client);
            Client = new SpectatorClient(db, ClientWire, enabled, name);
            feed.AddViewer(HostWire);
        }
    }

    static Battle Make(ContentDb db)
    {
        return new Battle(db, new IList<string>[] { HostTeam, GuestTeam }, new[] { "主机", "客机" }, 122);
    }

    static void Pump(SpectatorFeed feed, params Viewer[] viewers)
    {
        for (int i = 0; i < 2; i++)
        {
            feed.Pump();
            foreach (var viewer in viewers) viewer.Client.Poll();
        }
        Assert.All(viewers, viewer => Assert.Null(viewer.Client.Error));
    }

    static string Send(int index)
    {
        return JsonSerializer.Serialize(new { type = "spectator_chat", version = 1, kind = "send", index });
    }

    static string Join(string name)
    {
        return JsonSerializer.Serialize(new { type = "spectator_chat", version = 1, kind = "join", name });
    }

    static string Message(string name, int index, int turn)
    {
        return JsonSerializer.Serialize(new { type = "spectator_chat", version = 1, kind = "message", name, index, turn });
    }

    static string TypeOf(string message)
    {
        using var parsed = JsonDocument.Parse(message);
        return parsed.RootElement.GetProperty("type").GetString();
    }

    static string Log(Battle battle)
    {
        return JsonSerializer.Serialize(battle.Log, new JsonSerializerOptions { IncludeFields = true });
    }

    static void SameBattle(Battle expected, Battle actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.StateHash(), actual.StateHash());
        Assert.Equal(expected.Record.ToJson(), actual.Record.ToJson());
        Assert.Equal(Log(expected), Log(actual));
        Assert.Equal(expected.Clone().Random(), actual.Clone().Random());
    }

    [Fact]
    public void AViewerChatsOnlyToOtherSubscribedViewersAndNeverToEitherPlayer()
    {
        var db = BattleTests.LoadPack("original");
        LoopbackPair.Create(out var hostEndpoint, out var guestEndpoint);
        var hostWire = new Transport(hostEndpoint);
        var guestWire = new Transport(guestEndpoint);
        var host = new NetSession(db, hostWire, true, HostTeam, "主机", 122);
        var guest = new NetSession(db, guestWire, false, GuestTeam, "客机", null);
        for (int i = 0; i < 12; i++) { host.Poll(); guest.Poll(); }
        Assert.Equal(NetState.Choose, host.State);
        Assert.Equal(NetState.Choose, guest.State);
        var feed = new SpectatorFeed(host.Battle, 0, chatEnabled: true);
        var a = new Viewer(db, feed, "观众甲");
        var b = new Viewer(db, feed, "观众乙");
        var legacy = new Viewer(db, feed, enabled: false);
        Pump(feed, a, b, legacy);
        var before = host.Battle.Clone();
        int hostSent = hostWire.Sent.Count, guestSent = guestWire.Sent.Count;

        Assert.True(a.Client.SendQuickChat(1));
        Pump(feed, a, b, legacy);
        host.Poll();
        guest.Poll();

        var received = Assert.Single(b.Client.ReceivedChats);
        Assert.Equal(("观众甲", 1, 1), (received.SenderName, received.Index, received.Turn));
        Assert.Empty(a.Client.ReceivedChats);
        Assert.Empty(legacy.Client.ReceivedChats);
        Assert.Empty(host.ReceivedChats);
        Assert.Empty(guest.ReceivedChats);
        Assert.Equal(hostSent, hostWire.Sent.Count);
        Assert.Equal(guestSent, guestWire.Sent.Count);
        Assert.Equal(NetState.Choose, host.State);
        Assert.Equal(NetState.Choose, guest.State);
        SameBattle(before, host.Battle);
        SameBattle(before, guest.Battle);
    }

    [Fact]
    public void InvalidIndicesDoNotSendOrConsumeTheHostsValidMessageQuota()
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db);
        var feed = new SpectatorFeed(source, 0, chatEnabled: true);
        var a = new Viewer(db, feed);
        var b = new Viewer(db, feed);
        Pump(feed, a, b);
        int sent = a.ClientWire.Sent.Count;
        foreach (int index in new[] { int.MinValue, -1, QuickChat.Count, int.MaxValue })
            Assert.False(a.Client.SendQuickChat(index));
        Assert.Equal(sent, a.ClientWire.Sent.Count);
        foreach (int index in new[] { int.MinValue, -1, QuickChat.Count, int.MaxValue }) a.ClientWire.Send(Send(index));
        for (int index = 0; index < 3; index++) a.ClientWire.Send(Send(index));
        Pump(feed, a, b);

        Assert.Equal(new[] { 0, 1, 2 }, b.Client.ReceivedChats.Select(chat => chat.Index));
        Assert.All(b.Client.ReceivedChats, chat => Assert.Equal(source.Turn, chat.Turn));
    }

    [Fact]
    public void HostLimitsEachViewerToThreePerVisibleTurnWithoutLeakingTheLiveTurn()
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db);
        var feed = new SpectatorFeed(source, 2, chatEnabled: true);
        var a = new Viewer(db, feed, "甲");
        var b = new Viewer(db, feed, "乙");
        Pump(feed, a, b);
        for (int i = 0; i < 8; i++) a.ClientWire.Send(Send(i));
        for (int i = 3; i < 8; i++) b.ClientWire.Send(Send(i));
        Pump(feed, a, b);
        Assert.Equal(new[] { 0, 1, 2 }, b.Client.ReceivedChats.Select(chat => chat.Index));
        Assert.Equal(new[] { 3, 4, 5 }, a.Client.ReceivedChats.Select(chat => chat.Index));

        a.ClientWire.Send(Join("改名不能重置配额"));
        a.ClientWire.Send(Send(7));
        Pump(feed, a, b);
        Assert.Equal(3, b.Client.ReceivedChats.Count);
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(2, source.Turn);
        Pump(feed, a, b);
        Assert.Equal(1, a.Client.Battle.Turn);
        a.ClientWire.Send(Send(7));
        Pump(feed, a, b);
        Assert.Equal(3, b.Client.ReceivedChats.Count);
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Pump(feed, a, b);
        Assert.Equal(4, source.Turn);
        Assert.Equal(2, a.Client.Battle.Turn);
        for (int i = 4; i < 8; i++) a.ClientWire.Send(Send(i));
        Pump(feed, a, b);

        Assert.Equal(new[] { 0, 1, 2, 4, 5, 6 }, b.Client.ReceivedChats.Select(chat => chat.Index));
        Assert.All(b.Client.ReceivedChats.Skip(3), chat => Assert.Equal(2, chat.Turn));
        Assert.Equal(3, a.Client.ReceivedChats.Count);
        Assert.All(b.Client.ReceivedChats, chat => Assert.Equal("甲", chat.SenderName));
    }

    [Fact]
    public void ClientSendsAtMostThreePerVisibleTurnAndInvalidIndicesDoNotUseItsQuota()
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db);
        var feed = new SpectatorFeed(source, 2, chatEnabled: true);
        var sender = new Viewer(db, feed);
        var receiver = new Viewer(db, feed);
        Pump(feed, sender, receiver);
        sender.ClientWire.FailSend = true;
        for (int i = 0; i < 3; i++) Assert.False(sender.Client.SendQuickChat(i));
        sender.ClientWire.FailSend = false;
        Assert.False(sender.Client.SendQuickChat(-1));
        Assert.True(sender.Client.SendQuickChat(0));
        Assert.False(sender.Client.SendQuickChat(8));
        Assert.True(sender.Client.SendQuickChat(1));
        Assert.True(sender.Client.SendQuickChat(2));
        int sent = sender.ClientWire.Sent.Count;
        Assert.False(sender.Client.SendQuickChat(3));
        Assert.Equal(sent, sender.ClientWire.Sent.Count);
        Pump(feed, sender, receiver);
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Pump(feed, sender, receiver);
        Assert.Equal(1, sender.Client.Battle.Turn);
        Assert.False(sender.Client.SendQuickChat(3));
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Pump(feed, sender, receiver);
        Assert.Equal(2, sender.Client.Battle.Turn);
        Assert.True(sender.Client.SendQuickChat(3));
        Assert.True(sender.Client.SendQuickChat(4));
        Assert.True(sender.Client.SendQuickChat(5));
        Assert.False(sender.Client.SendQuickChat(6));
        Pump(feed, sender, receiver);

        Assert.Equal(Enumerable.Range(0, 6), receiver.Client.ReceivedChats.Select(chat => chat.Index));
    }

    [Fact]
    public void DisabledHostDropsChatButStillDeliversTheCompleteBattle()
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db);
        var feed = new SpectatorFeed(source, 0, chatEnabled: false);
        var a = new Viewer(db, feed);
        var b = new Viewer(db, feed);
        Pump(feed, a, b);
        Assert.False(feed.ChatEnabled);
        a.Client.SendQuickChat(0);
        b.ClientWire.Send(Send(1));
        Pump(feed, a, b);
        source.Surrender(0);
        feed.Finish();
        a.Client.Poll();
        b.Client.Poll();

        Assert.All(new[] { a, b }, viewer =>
        {
            Assert.Null(viewer.Client.Error);
            Assert.True(viewer.Client.IsFinished);
            Assert.Empty(viewer.Client.ReceivedChats);
            Assert.DoesNotContain(viewer.HostWire.Sent, message => TypeOf(message) == "spectator_chat");
            SameBattle(source, viewer.Client.Battle);
        });
    }

    [Fact]
    public void LegacyOptOutReceivesOnlyOriginalFramesWithContiguousSequences()
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db);
        var feed = new SpectatorFeed(source, 0, chatEnabled: true);
        var sender = new Viewer(db, feed);
        var receiver = new Viewer(db, feed);
        var legacy = new Viewer(db, feed, enabled: false);
        Pump(feed, sender, receiver, legacy);
        Assert.False(legacy.Client.ChatEnabled);
        Assert.False(legacy.Client.SendQuickChat(0));
        Assert.Empty(legacy.ClientWire.Sent);
        Assert.True(sender.Client.SendQuickChat(2));
        Pump(feed, sender, receiver, legacy);
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Pump(feed, sender, receiver, legacy);
        source.Surrender(1);
        feed.Finish();
        legacy.Client.Poll();

        Assert.Single(receiver.Client.ReceivedChats);
        Assert.True(legacy.Client.IsFinished);
        Assert.Null(legacy.Client.Error);
        SameBattle(source, legacy.Client.Battle);
        for (int i = 0; i < legacy.HostWire.Sent.Count; i++)
        {
            using var frame = JsonDocument.Parse(legacy.HostWire.Sent[i]);
            Assert.Contains(frame.RootElement.GetProperty("type").GetString(),
                new[] { "spectator_start", "spectator_event", "spectator_end" });
            Assert.Equal(i, frame.RootElement.GetProperty("seq").GetInt32());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalMessagesBeforeOpeningBetweenEventsAndAfterEndNeverConsumeBattleSequence(bool enabled)
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db);
        var feed = new SpectatorFeed(source, 0, chatEnabled: true);
        var viewer = new Viewer(db, feed, enabled: enabled);
        viewer.HostWire.Send(Message("甲", 0, 1));
        Pump(feed, viewer);
        viewer.HostWire.Send(Message("甲", 1, 1));
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Pump(feed, viewer);
        source.Surrender(0);
        feed.Finish();
        viewer.HostWire.Send(Message("乙", 2, source.Turn));
        viewer.Client.Poll();

        Assert.Null(viewer.Client.Error);
        Assert.True(viewer.Client.IsFinished);
        SameBattle(source, viewer.Client.Battle);
        if (!enabled) Assert.Empty(viewer.Client.ReceivedChats);
    }

    [Fact]
    public void UnsubscribedViewerCannotSendAndLateJoinDoesNotReplayOldChats()
    {
        var db = BattleTests.LoadPack("original");
        var feed = new SpectatorFeed(Make(db), 0, chatEnabled: true);
        var a = new Viewer(db, feed);
        var b = new Viewer(db, feed);
        var unregistered = new Viewer(db, feed, enabled: false);
        Assert.False(a.Client.SendQuickChat(0));
        unregistered.ClientWire.Send(Send(0));
        Pump(feed, a, b, unregistered);
        Assert.Empty(a.Client.ReceivedChats);
        Assert.Empty(b.Client.ReceivedChats);
        Assert.True(a.Client.SendQuickChat(3));
        Pump(feed, a, b, unregistered);
        var late = new Viewer(db, feed, "后来者");
        Pump(feed, a, b, unregistered, late);
        Assert.Empty(late.Client.ReceivedChats);
        Assert.True(a.Client.SendQuickChat(4));
        Pump(feed, a, b, unregistered, late);
        Assert.Equal(4, Assert.Single(late.Client.ReceivedChats).Index);
    }

    [Fact]
    public void DisplayNamesUseExistingFilteringAndAreBoundToTheViewerAtJoin()
    {
        const string rawName = "  甲\n\u202e  乙😀丙丁戊己庚辛壬癸子丑寅卯  ";
        string expected = TextInput.Clean(rawName, TextInputKind.PlayerName).Text;
        var db = BattleTests.LoadPack("original");
        var feed = new SpectatorFeed(Make(db), 0, chatEnabled: true);
        var sender = new Viewer(db, feed, rawName);
        var receiver = new Viewer(db, feed);
        var raw = new Viewer(db, feed, enabled: false);
        Assert.Equal(expected, sender.Client.DisplayName);
        Pump(feed, sender, receiver, raw);
        raw.ClientWire.Send(Join(rawName));
        raw.ClientWire.Send(Send(1));
        Assert.True(sender.Client.SendQuickChat(2));
        Pump(feed, sender, receiver, raw);

        Assert.Equal(2, receiver.Client.ReceivedChats.Count);
        Assert.All(receiver.Client.ReceivedChats, chat => Assert.Equal(expected, chat.SenderName));
        var first = receiver.Client.ReceivedChats[0];
        if (receiver.Client.ReceivedChats is IList<SpectatorChatMessage> mutable)
        {
            Assert.True(mutable.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => mutable.Clear());
            Assert.Throws<NotSupportedException>(() => mutable[0] = first);
        }
        receiver.HostWire.Send(Message(rawName, 3, 1));
        receiver.Client.Poll();
        Assert.Null(receiver.Client.Error);
        Assert.Equal(expected, receiver.Client.ReceivedChats.Last().SenderName);
    }

    [Fact]
    public void SendCarriesOnlyTheFixedPhraseIndexAndNoFreeTextOrClaimedIdentity()
    {
        var db = BattleTests.LoadPack("original");
        var feed = new SpectatorFeed(Make(db), 0, chatEnabled: true);
        var viewer = new Viewer(db, feed);
        Pump(feed, viewer);
        Assert.True(viewer.Client.SendQuickChat(QuickChat.Count - 1));
        using var sent = JsonDocument.Parse(viewer.ClientWire.Sent.Last());
        Assert.Equal(new[] { "index", "kind", "type", "version" },
            sent.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
        Assert.Equal("spectator_chat", sent.RootElement.GetProperty("type").GetString());
        Assert.Equal("send", sent.RootElement.GetProperty("kind").GetString());
        Assert.Equal(QuickChat.Count - 1, sent.RootElement.GetProperty("index").GetInt32());
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        const string prefix = "{\"type\":\"spectator_chat\",\"version\":1,\"kind\":\"send\"";
        foreach (string value in new[] { "null", "true", "\"1\"", "1.5", "-1", "8", "1e999", "{}", "[]" })
            yield return new object[] { prefix + ",\"index\":" + value + "}" };
        yield return new object[] { prefix + "}" };
        yield return new object[] { prefix + ",\"index\":0,\"text\":\"自由文字\"}" };
        yield return new object[] { prefix + ",\"index\":0,\"turn\":999}" };
        yield return new object[] { prefix + ",\"index\":0,\"name\":\"冒名\"}" };
        yield return new object[] { prefix + ",\"index\":0,\"index\":1}" };
        yield return new object[] { prefix + ",\"index\":" };
        yield return new object[] { prefix + ",\"index\":0} trailing" };
        yield return new object[] { "{\"type\":\"spectator_chat\",\"version\":999,\"kind\":\"send\",\"index\":0}" };
        yield return new object[] { Message("冒名", 0, 1) };
        yield return new object[] { "not json" };
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void HostDiscardsMalformedOrSpoofedRequestsWithoutUsingTheThreeValidSlots(string invalid)
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db);
        var feed = new SpectatorFeed(source, 0, chatEnabled: true);
        var a = new Viewer(db, feed);
        var b = new Viewer(db, feed);
        Pump(feed, a, b);
        var before = source.Clone();
        a.ClientWire.Send(invalid);
        for (int index = 0; index < 3; index++) a.ClientWire.Send(Send(index));
        Pump(feed, a, b);

        Assert.Equal(new[] { 0, 1, 2 }, b.Client.ReceivedChats.Select(chat => chat.Index));
        SameBattle(before, source);
        SameBattle(before, b.Client.Battle);
    }

    [Fact]
    public void HostDiscardsLargeFreeTextPayloadWithoutUsingTheThreeValidSlots()
    {
        string invalid = "{\"type\":\"spectator_chat\",\"version\":1,\"kind\":\"send\",\"index\":0,\"text\":\""
            + new string('x', 65536) + "\"}";
        HostDiscardsMalformedOrSpoofedRequestsWithoutUsingTheThreeValidSlots(invalid);
    }

    [Theory]
    [InlineData("{\"type\":\"spectator_chat\",\"index\":")]
    [InlineData("{\"type\":\"spectator_chat\",\"version\":1,\"kind\":\"message\",\"index\":1e999}")]
    [InlineData("{\"ty\\u0070e\":\"spectator_chat\",\"index\":")]
    [InlineData("{\"type\":\"spectator_chat\",\"version\":1,\"kind\":\"message\",\"name\":\"甲\",\"index\":0,\"turn\":1,\"text\":\"任意文字\"}")]
    public void BadOptionalMessagesDoNotStopValidBattleFrames(string invalid)
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db);
        var feed = new SpectatorFeed(source, 0, chatEnabled: true);
        var viewer = new Viewer(db, feed);
        Pump(feed, viewer);
        viewer.HostWire.Send(invalid);
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Pump(feed, viewer);

        Assert.Empty(viewer.Client.ReceivedChats);
        SameBattle(source, viewer.Client.Battle);
    }

    [Fact]
    public void NonChatProtocolCorruptionStillFaultsAndPreservesTheConfirmedBattle()
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db);
        var feed = new SpectatorFeed(source, 0, chatEnabled: true);
        var viewer = new Viewer(db, feed);
        Pump(feed, viewer);
        var confirmed = viewer.Client.Battle.Clone();
        viewer.HostWire.Send("{\"type\":\"spectator_event\",\"nested\":{\"type\":\"spectator_chat\"},");
        viewer.Client.Poll();

        Assert.NotNull(viewer.Client.Error);
        Assert.False(viewer.Client.SendQuickChat(0));
        SameBattle(confirmed, viewer.Client.Battle);
    }

    [Fact]
    public void IncomingFloodIsBoundedPerPumpAndCannotStarveAnotherViewer()
    {
        var db = BattleTests.LoadPack("original");
        var feed = new SpectatorFeed(Make(db), 0, chatEnabled: true);
        var noisy = new Viewer(db, feed, "刷屏者");
        var quiet = new Viewer(db, feed, "正常观众");
        Pump(feed, noisy, quiet);
        int received = noisy.HostWire.Received;
        for (int i = 0; i < 200; i++) noisy.ClientWire.Send(Send(i % QuickChat.Count));
        Assert.True(quiet.Client.SendQuickChat(7));

        feed.Pump();
        noisy.Client.Poll();
        quiet.Client.Poll();

        Assert.InRange(noisy.HostWire.Received - received, 1, 64);
        Assert.Equal(7, Assert.Single(noisy.Client.ReceivedChats).Index);
        Assert.Equal(3, quiet.Client.ReceivedChats.Count);
        Assert.Null(noisy.Client.Error);
        Assert.Null(quiet.Client.Error);
    }

    [Fact]
    public void EntireBattleHasIdenticalHashRecordLogAndRandomStreamWithOrWithoutSpectatorChats()
    {
        var db = BattleTests.LoadPack("original");
        var chatting = Make(db);
        var silent = Make(db);
        var feed = new SpectatorFeed(chatting, 2, chatEnabled: true);
        var a = new Viewer(db, feed, "甲");
        var b = new Viewer(db, feed, "乙");
        Pump(feed, a, b);
        int steps = 0;
        while (chatting.Phase != Phase.Over && steps++ < Battle.TurnCap * 4)
        {
            var before = chatting.Clone();
            a.Client.SendQuickChat(steps % QuickChat.Count);
            b.Client.SendQuickChat((steps + 1) % QuickChat.Count);
            Pump(feed, a, b);
            SameBattle(before, chatting);
            if (chatting.Phase == Phase.Choose)
            {
                chatting.ResolveTurn(Ai.Choose(chatting, 0), Ai.Choose(chatting, 1));
                silent.ResolveTurn(Ai.Choose(silent, 0), Ai.Choose(silent, 1));
            }
            else
            {
                Assert.Equal(Phase.Replace, chatting.Phase);
                for (int side = 0; side < 2; side++)
                {
                    if (!chatting.Need[side]) continue;
                    chatting.Replace(side, Ai.ChooseReplacement(chatting, side));
                    silent.Replace(side, Ai.ChooseReplacement(silent, side));
                }
            }
            SameBattle(silent, chatting);
            Pump(feed, a, b);
        }
        Assert.Equal(Phase.Over, chatting.Phase);
        Assert.Equal(Phase.Over, silent.Phase);
        feed.Finish();
        for (int i = 0; i < 20 && (!a.Client.IsFinished || !b.Client.IsFinished); i++)
        {
            a.Client.Poll();
            b.Client.Poll();
        }
        Assert.NotEmpty(a.Client.ReceivedChats);
        Assert.NotEmpty(b.Client.ReceivedChats);
        foreach (var viewer in new[] { a, b })
        {
            Assert.Null(viewer.Client.Error);
            Assert.True(viewer.Client.IsFinished);
            SameBattle(silent, viewer.Client.Battle);
        }
    }
}

[Collection("TCP loopback")]
public class T122_SpectatorChatTcpTests
{
    static void Until(Func<bool> condition, Action pump)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < 5000)
        {
            pump();
            System.Threading.Thread.Sleep(1);
        }
        Assert.True(condition(), "等待 TCP 观战消息超时。");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingOnlyTheUploadDirectionKeepsChatEventsAndTheEndingAvailable(bool chatEnabled)
    {
        var db = BattleTests.LoadPack("original");
        var source = new Battle(db, new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "主机", "客机" }, 122);
        var feed = new SpectatorFeed(source, 0, chatEnabled);
        using var listener = TcpTransport.Listen(0, out int port);
        using var clientTransport = TcpTransport.Connect("127.0.0.1", port, 5000);
        using var hostTransport = listener.Accept(5000);
        Assert.NotNull(hostTransport);
        var viewer = new SpectatorClient(db, clientTransport, chatEnabled, "停止上传的观众");
        feed.AddViewer(hostTransport);
        LoopbackPair.Create(out var otherHost, out var otherClient);
        var otherViewer = new SpectatorClient(db, otherClient, chatEnabled, "仍在聊天的观众");
        feed.AddViewer(otherHost);
        Action pump = () =>
        {
            feed.Pump();
            viewer.Poll();
            otherViewer.Poll();
            Assert.Null(viewer.Error);
            Assert.Null(otherViewer.Error);
        };
        Until(() => viewer.Battle != null && otherViewer.Battle != null, pump);

        clientTransport.CompleteSending();
        Until(() => hostTransport.PeerClosed && hostTransport.ReceiveDrained, pump);
        // 上传 EOF 也可能在队列恰好清空后才可见，单独再轮询一次确保已处理。
        feed.Pump();
        if (chatEnabled)
        {
            Assert.True(otherViewer.SendQuickChat(6));
            Until(() => viewer.ReceivedChats.Count == 1, pump);
            var chat = Assert.Single(viewer.ReceivedChats);
            Assert.Equal(("仍在聊天的观众", 6, 1), (chat.SenderName, chat.Index, chat.Turn));
        }

        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Until(() => viewer.Battle.Turn == source.Turn, pump);
        Assert.Equal(source.StateHash(), viewer.Battle.StateHash());
        source.Surrender(0);
        feed.Finish();
        Until(() => viewer.IsFinished, pump);

        Assert.Null(viewer.Error);
        Assert.True(viewer.IsFinished);
        Assert.Equal(source.StateHash(), viewer.Battle.StateHash());
        Assert.Equal(source.Record.ToJson(), viewer.Battle.Record.ToJson());
        if (!chatEnabled) Assert.Empty(viewer.ReceivedChats);
    }
}
