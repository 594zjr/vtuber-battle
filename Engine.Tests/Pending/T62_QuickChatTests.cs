// T-62 验收测试：对战快捷语。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T62_QuickChatTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    static (NetSession host, NetSession guest) Pair()
    {
        LoopbackPair.Create(out var ta, out var tb);
        var host = new NetSession(BattleTests.LoadPack("original"), ta, true, HostTeam, "主机", 42);
        var guest = new NetSession(BattleTests.LoadPack("original"), tb, false, GuestTeam, "客机", null);
        for (int i = 0; i < 50 && (host.State == NetState.Handshake || guest.State == NetState.Handshake); i++) { host.Poll(); guest.Poll(); }
        return (host, guest);
    }

    [Fact]
    public void LinesExistAndChatArrives()
    {
        Assert.Equal(8, QuickChat.Lines.Length);
        var (h, g) = Pair();
        Assert.True(h.SendQuickChat(1, 0));
        for (int i = 0; i < 10; i++) { h.Poll(); g.Poll(); }
        Assert.Single(g.ReceivedChats);
        Assert.Equal(1, g.ReceivedChats[0].Index);
        Assert.Equal(h.MySide, g.ReceivedChats[0].Side);
        Assert.Equal(NetState.Choose, g.State);
    }

    [Fact]
    public void RateLimitAndBadIndex()
    {
        var (h, g) = Pair();
        Assert.False(h.SendQuickChat(99, 0));
        Assert.True(h.SendQuickChat(0, 0));
        Assert.True(h.SendQuickChat(0, 1));
        Assert.True(h.SendQuickChat(0, 2));
        Assert.False(h.SendQuickChat(0, 3));
        Assert.True(h.SendQuickChat(0, 10.5));
        for (int i = 0; i < 10; i++) { h.Poll(); g.Poll(); }
        Assert.Equal(4, g.ReceivedChats.Count);
    }

    [Fact]
    public void MuteAndNoEffectOnBattle()
    {
        var (h, g) = Pair();
        var before = h.Battle.StateHash();
        g.MuteOpponent = true;
        h.SendQuickChat(2, 0);
        for (int i = 0; i < 10; i++) { h.Poll(); g.Poll(); }
        Assert.Empty(g.ReceivedChats);
        Assert.Equal(before, h.Battle.StateHash());
        Assert.Equal(before, g.Battle.StateHash());
    }
}
