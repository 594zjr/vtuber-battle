// T-32 验收测试：先承诺后公开，防止偷看对手行动。实现后把本文件移到 Engine.Tests/ 下。
using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T32_CommitRevealTests
{
    sealed class Spy : INetTransport
    {
        readonly INetTransport inner; readonly Func<string, string> f; public readonly List<string> Sent = new List<string>();
        public Spy(INetTransport inner, Func<string, string> f = null) { this.inner = inner; this.f = f ?? (m => m); }
        public void Send(string msg) { Sent.Add(msg); inner.Send(f(msg)); }
        public bool TryReceive(out string msg) { return inner.TryReceive(out msg); }
    }

    static readonly string[] HostTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] GuestTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };

    static void Drive(NetSession a, NetSession b, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            a.Poll(); b.Poll();
            foreach (var s in new[] { a, b })
            {
                if (s.State == NetState.Choose && !s.HasSubmitted) s.Submit(Ai.Choose(s.Battle, s.MySide));
                if (s.State == NetState.Replace && !s.HasSubmitted) s.SubmitReplace(Ai.ChooseReplacement(s.Battle, s.MySide));
            }
            if (a.State == NetState.Over || a.State == NetState.Error || b.State == NetState.Error) return;
        }
    }

    [Fact]
    public void NoPlainActionIsSentBeforeBothCommits()
    {
        LoopbackPair.Create(out var ta, out var tb);
        var spyA = new Spy(ta);
        var host = new NetSession(BattleTests.LoadPack("original"), spyA, true, HostTeam, "主机", 5);
        var guest = new NetSession(BattleTests.LoadPack("original"), tb, false, GuestTeam, "客机", null);
        Drive(host, guest, 5000);
        Assert.Equal(NetState.Over, host.State);
        Assert.DoesNotContain(spyA.Sent, m => m.Contains("\"type\":\"action\""));
        Assert.Contains(spyA.Sent, m => m.Contains("\"type\":\"commit\""));
        Assert.Contains(spyA.Sent, m => m.Contains("\"type\":\"reveal\""));
        // 每个 reveal 之前都有同一回合的 commit
        int firstReveal = spyA.Sent.FindIndex(m => m.Contains("\"type\":\"reveal\""));
        int firstCommit = spyA.Sent.FindIndex(m => m.Contains("\"type\":\"commit\""));
        Assert.True(firstCommit >= 0 && firstCommit < firstReveal);
    }

    [Fact]
    public void RevealThatDoesNotMatchCommitIsAnError()
    {
        LoopbackPair.Create(out var ta, out var tb);
        // 客机公开时把行动换成别的（跳过 ↔ 技能 0），与承诺不符
        var cheat = new Spy(tb, m => !m.Contains("\"type\":\"reveal\"") ? m :
            System.Text.RegularExpressions.Regex.Replace(m, "\"action\":\"([^\"]*)\"", x => x.Groups[1].Value == "P" ? "\"action\":\"S0\"" : "\"action\":\"P\""));
        var host = new NetSession(BattleTests.LoadPack("original"), ta, true, HostTeam, "主机", 5);
        var guest = new NetSession(BattleTests.LoadPack("original"), cheat, false, GuestTeam, "客机", null);
        Drive(host, guest, 5000);
        Assert.Equal(NetState.Error, host.State);
        Assert.Contains("承诺", host.Error);
    }
}
