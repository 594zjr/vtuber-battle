using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T122_SpectatorChatReviewTests
{
    sealed class ObservedTransport : INetTransport
    {
        readonly INetTransport inner;
        public readonly List<string> Sent = new List<string>();
        public int Received;

        public ObservedTransport(INetTransport inner) { this.inner = inner; }

        public void Send(string message)
        {
            inner.Send(message);
            Sent.Add(message);
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
        public readonly ObservedTransport HostWire, ClientWire;
        public readonly SpectatorClient Client;

        public Viewer(ContentDb db, SpectatorFeed feed, string name, bool enabled = true)
        {
            LoopbackPair.Create(out var host, out var client);
            HostWire = new ObservedTransport(host);
            ClientWire = new ObservedTransport(client);
            Client = new SpectatorClient(db, ClientWire, enabled, name);
            feed.AddViewer(HostWire);
        }
    }

    static Battle Make(ContentDb db)
    {
        return new Battle(db, new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "主机", "客机" }, 122);
    }

    static void Pump(SpectatorFeed feed, params Viewer[] viewers)
    {
        for (int iteration = 0; iteration < 2; iteration++)
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

    static string Message(int index, int turn)
    {
        return JsonSerializer.Serialize(new
        {
            type = "spectator_chat", version = 1, kind = "message", name = "排队观众", index, turn
        });
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

    static string[] BattleFrames(ObservedTransport transport)
    {
        var types = new[] { "spectator_start", "spectator_event", "spectator_end" };
        return transport.Sent.Where(message => types.Any(type =>
            message.StartsWith("{\"type\":\"" + type + "\"", StringComparison.Ordinal))).ToArray();
    }

    static void AssertCompleteSequence(ObservedTransport transport, Battle source)
    {
        var frames = BattleFrames(transport);
        Assert.Equal(source.Record.Events.Count + 2, frames.Length);
        for (int index = 0; index < frames.Length; index++)
        {
            using var frame = JsonDocument.Parse(frames[index]);
            Assert.Equal(index, frame.RootElement.GetProperty("seq").GetInt32());
        }
        using var ending = JsonDocument.Parse(frames[frames.Length - 1]);
        Assert.Equal("spectator_end", ending.RootElement.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClientDrainsChatBacklogWithinBoundedPollsAndPreservesTheCompleteBattle(bool enabled)
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db);
        var feed = new SpectatorFeed(source, 0, chatEnabled: true);
        var viewer = new Viewer(db, feed, "积压接收者", enabled);
        Pump(feed, viewer);
        var confirmed = viewer.Client.Battle;
        var beforeChat = confirmed.Clone();

        // 合成下行积压只测试客户端预算；主机每位观众限额另由真实上传测试覆盖。
        for (int index = 0; index < 131; index++)
        {
            viewer.HostWire.Send(index == 64
                ? "{\"index\":1e999,\"type\":\"spectator_chat\",\"version\":1,\"kind\":\"message\"}"
                : Message(index % QuickChat.Count, 1));
        }
        SameBattle(beforeChat, source);
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        source.Surrender(0);
        Assert.Equal(Phase.Over, source.Phase);
        var final = source.Clone();
        feed.Finish();
        AssertCompleteSequence(viewer.HostWire, source);

        // 开关关闭或坏扩展也必须占预算，不能一次无限吞掉积压。
        for (int poll = 0; poll < 2; poll++)
        {
            int received = viewer.ClientWire.Received;
            viewer.Client.Poll();
            Assert.Equal(64, viewer.ClientWire.Received - received);
            Assert.Null(viewer.Client.Error);
            Assert.False(viewer.Client.IsFinished);
            Assert.Same(confirmed, viewer.Client.Battle);
            SameBattle(beforeChat, viewer.Client.Battle);
            SameBattle(final, source);
        }

        int beforeFinalPoll = viewer.ClientWire.Received;
        viewer.Client.Poll();
        Assert.Equal(3 + source.Record.Events.Count + 1, viewer.ClientWire.Received - beforeFinalPoll);
        Assert.Null(viewer.Client.Error);
        Assert.True(viewer.Client.IsFinished);
        Assert.Equal(enabled ? 130 : 0, viewer.Client.ReceivedChats.Count);
        SameBattle(final, viewer.Client.Battle);
        SameBattle(final, source);
        AssertCompleteSequence(viewer.HostWire, source);

        // 终局后的空轮询保持已确认实例、记录和随机流，不重新结算。
        var finished = viewer.Client.Battle;
        viewer.Client.Poll();
        Assert.Same(finished, viewer.Client.Battle);
        Assert.Null(viewer.Client.Error);
        Assert.True(viewer.Client.IsFinished);
        SameBattle(final, viewer.Client.Battle);
    }

    [Fact]
    public void HiddenRealEndingDoesNotResetVisibleChatQuotaAndFinishStopsForwarding()
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db);
        var feed = new SpectatorFeed(source, 2, chatEnabled: true);
        var a = new Viewer(db, feed, "观众甲");
        var b = new Viewer(db, feed, "观众乙");
        Pump(feed, a, b);
        for (int turn = 0; turn < 3; turn++)
            source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Pump(feed, a, b);
        Assert.Equal(4, source.Turn);
        Assert.Equal(2, a.Client.Battle.Turn);
        Assert.Equal(2, b.Client.Battle.Turn);
        Assert.Equal(Phase.Choose, a.Client.Battle.Phase);
        var visible = a.Client.Battle.Clone();
        SameBattle(visible, b.Client.Battle);
        for (int index = 0; index < 3; index++) Assert.True(a.Client.SendQuickChat(index));
        Pump(feed, a, b);
        Assert.Equal(new[] { 0, 1, 2 }, b.Client.ReceivedChats.Select(chat => chat.Index));
        Assert.All(b.Client.ReceivedChats, chat => Assert.Equal(2, chat.Turn));

        source.Surrender(0);
        Assert.Equal(Phase.Over, source.Phase);
        Assert.Equal(4, source.Turn);
        var final = source.Clone();
        Pump(feed, a, b);
        Assert.False(a.Client.SendQuickChat(3));
        a.ClientWire.Send(Send(3));
        // 另一观众尚未用配额；真实终局不可提前关闭其仍处于可见第 2 回合的聊天。
        for (int index = 4; index < 7; index++) Assert.True(b.Client.SendQuickChat(index));
        Assert.False(b.Client.SendQuickChat(7));
        b.ClientWire.Send(Send(7));
        Pump(feed, a, b);
        for (int iteration = 0; iteration < 3; iteration++)
        {
            a.ClientWire.Send(Send(7));
            b.ClientWire.Send(Send(7));
            Pump(feed, a, b);
        }

        Assert.Equal(new[] { 4, 5, 6 }, a.Client.ReceivedChats.Select(chat => chat.Index));
        Assert.Equal(new[] { 0, 1, 2 }, b.Client.ReceivedChats.Select(chat => chat.Index));
        foreach (var viewer in new[] { a, b })
        {
            Assert.False(viewer.Client.IsFinished);
            Assert.Null(viewer.Client.Error);
            Assert.All(viewer.Client.ReceivedChats, chat => Assert.Equal(2, chat.Turn));
            Assert.All(viewer.Client.ReceivedChats, chat =>
                Assert.Equal(ReferenceEquals(viewer, a) ? "观众乙" : "观众甲", chat.SenderName));
            SameBattle(visible, viewer.Client.Battle);
            // 此时只发布了开局和第一回合，未发布真实终局及被延迟的两回合。
            var frames = BattleFrames(viewer.HostWire);
            Assert.Equal(2, frames.Length);
            Assert.DoesNotContain(frames, message => message.Contains("\"type\":\"spectator_end\""));
            Assert.DoesNotContain(frames, message => message.Contains("\"event\":\"surrender\""));
        }
        SameBattle(final, source);

        feed.Finish();
        // 绕过客户端限额，确认 Finish 后主机也不再发送快捷语。
        a.ClientWire.Send(Send(0));
        b.ClientWire.Send(Send(1));
        Pump(feed, a, b);
        foreach (var viewer in new[] { a, b })
        {
            Assert.True(viewer.Client.IsFinished);
            Assert.Null(viewer.Client.Error);
            Assert.False(viewer.Client.SendQuickChat(0));
            Assert.Equal(3, viewer.Client.ReceivedChats.Count);
            SameBattle(final, viewer.Client.Battle);
            AssertCompleteSequence(viewer.HostWire, source);
        }
        Assert.Equal(3, a.HostWire.Sent.Count(message => message.StartsWith("{\"type\":\"spectator_chat\"", StringComparison.Ordinal)));
        Assert.Equal(3, b.HostWire.Sent.Count(message => message.StartsWith("{\"type\":\"spectator_chat\"", StringComparison.Ordinal)));
        SameBattle(final, source);
    }
}
