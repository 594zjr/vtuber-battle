// T-29 验收测试：联网对战的同步会话（不含真实网络）。实现后把本文件移到 Engine.Tests/ 下。
using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T29_NetSessionTests
{
    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    // 把一端发出的消息交给 tamper 处理后再送达（用于模拟数据被篡改）
    sealed class Tamper : INetTransport
    {
        readonly INetTransport inner; readonly Func<string, string> f;
        public Tamper(INetTransport inner, Func<string, string> f) { this.inner = inner; this.f = f; }
        public void Send(string msg) { inner.Send(f(msg)); }
        public bool TryReceive(out string msg) { return inner.TryReceive(out msg); }
    }

    static void Drive(NetSession a, NetSession b, int maxSteps = 5000)
    {
        for (int i = 0; i < maxSteps; i++)
        {
            a.Poll(); b.Poll();
            foreach (var s in new[] { a, b })
            {
                if (s.State == NetState.Choose && !s.HasSubmitted) s.Submit(Ai.Choose(s.Battle, s.MySide));
                if (s.State == NetState.Replace && !s.HasSubmitted) s.SubmitReplace(Ai.ChooseReplacement(s.Battle, s.MySide));
            }
            if (a.State == NetState.Over && b.State == NetState.Over) return;
            if (a.State == NetState.Desync || b.State == NetState.Desync || a.State == NetState.Error || b.State == NetState.Error) return;
        }
    }

    [Fact]
    public void TwoPeersPlayAFullMatchInSync()
    {
        LoopbackPair.Create(out var ta, out var tb);
        var host = new NetSession(BattleTests.LoadPack("original"), ta, true, HostTeam, "主机", 42);
        var guest = new NetSession(BattleTests.LoadPack("original"), tb, false, GuestTeam, "客机", null);
        Drive(host, guest);
        Assert.Equal(NetState.Over, host.State);
        Assert.Equal(NetState.Over, guest.State);
        Assert.Equal(0, host.MySide); Assert.Equal(1, guest.MySide);
        Assert.Equal(host.Battle.StateHash(), guest.Battle.StateHash());
        Assert.Equal(host.Battle.Winner, guest.Battle.Winner);
        Assert.Equal("bell", guest.Battle.Sides[0].Team[0].Def.Id);
    }

    [Fact]
    public void TamperedHashIsDetectedAsDesync()
    {
        LoopbackPair.Create(out var ta, out var tb);
        // 客机发出的哈希消息被改掉
        var bad = new Tamper(tb, m => m.Contains("\"hash\"") && m.Contains("\"type\":\"hash\"") ? m.Replace("\"hash\":\"", "\"hash\":\"f") : m);
        var host = new NetSession(BattleTests.LoadPack("original"), ta, true, HostTeam, "主机", 42);
        var guest = new NetSession(BattleTests.LoadPack("original"), bad, false, GuestTeam, "客机", null);
        Drive(host, guest);
        Assert.Equal(NetState.Desync, host.State);
        Assert.False(string.IsNullOrEmpty(host.Error));
    }

    [Fact]
    public void DifferentPackIsRejectedAtHandshake()
    {
        LoopbackPair.Create(out var ta, out var tb);
        var host = new NetSession(BattleTests.LoadPack("original"), ta, true, HostTeam, "主机", 1);
        var guest = new NetSession(BattleTests.LoadPack("fan_asoul_vr"), tb, false,
            BattleTests.LoadPack("fan_asoul_vr").Streamers.Take(6).Select(s => s.Id).ToList(), "客机", null);
        Drive(host, guest, 50);
        Assert.True(host.State == NetState.Error || guest.State == NetState.Error);
    }

    [Fact]
    public void InvalidTeamIsRejected()
    {
        LoopbackPair.Create(out var ta, out var tb);
        var host = new NetSession(BattleTests.LoadPack("original"), ta, true, HostTeam, "主机", 1);
        var guest = new NetSession(BattleTests.LoadPack("original"), tb, false, new[] { "kei", "kei", "kei", "kei", "kei", "kei" }, "客机", null);
        Drive(host, guest, 50);
        Assert.Equal(NetState.Error, host.State);
    }
}
