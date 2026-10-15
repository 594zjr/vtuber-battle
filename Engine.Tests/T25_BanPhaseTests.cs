// T-25 验收测试：开局禁用（双方各禁对方 1 位）。实现后把本文件移到 Engine.Tests/ 下。
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T25_BanPhaseTests
{
    static readonly IList<string>[] Teams = {
        new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
        new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" } };

    static Battle Make(bool ban, bool lead, uint seed = 2)
    {
        return new Battle(BattleTests.LoadPack("original"), Teams, new[] { "A", "B" },
            new BattleOptions { Seed = seed, BanOne = ban, LeadPick = lead });
    }

    [Fact]
    public void BanRemovesOneFoeEachThenBattleStarts()
    {
        var b = Make(true, false);
        Assert.Equal(Phase.Ban, b.Phase);
        b.ChooseBan(0, 2); // A 禁 B 的 3 号位 nyako
        Assert.Equal(Phase.Ban, b.Phase);
        b.ChooseBan(1, 0); // B 禁 A 的 1 号位 bell
        Assert.Equal(Phase.Choose, b.Phase);
        Assert.Equal(5, b.Sides[0].Team.Count);
        Assert.Equal(5, b.Sides[1].Team.Count);
        Assert.DoesNotContain(b.Sides[1].Team, f => f.Def.Id == "nyako");
        Assert.DoesNotContain(b.Sides[0].Team, f => f.Def.Id == "bell");
        Assert.Equal("enka", b.Active(0).Def.Id);
        Assert.Contains(b.Log, l => l.Node == Node.Start && l.Text.Contains("禁用"));
    }

    [Fact]
    public void BanThenLeadPickBothWork()
    {
        var b = Make(true, true);
        b.ChooseBan(0, 0); b.ChooseBan(1, 0);
        Assert.Equal(Phase.Lead, b.Phase);
        b.ChooseLead(0, 2); b.ChooseLead(1, 1);
        Assert.Equal(Phase.Choose, b.Phase);
    }

    [Fact]
    public void OldConstructorHasNoBanPhase()
    {
        var b = new Battle(BattleTests.LoadPack("original"), Teams, new[] { "A", "B" }, 2);
        Assert.Equal(Phase.Choose, b.Phase);
        Assert.Equal(6, b.Sides[0].Team.Count);
    }

    [Fact]
    public void BanIsRecordedAndReplays()
    {
        var b = Make(true, true, 11);
        b.ChooseBan(0, Ai.ChooseBan(b, 0)); b.ChooseBan(1, Ai.ChooseBan(b, 1));
        b.ChooseLead(0, Ai.ChooseLead(b, 0)); b.ChooseLead(1, Ai.ChooseLead(b, 1));
        int guard = 0;
        while (b.Phase != Phase.Over && guard++ < 500)
        {
            if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
            else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
        }
        var again = Replay.Run(b.Db, BattleRecord.FromJson(b.Record.ToJson()));
        Assert.Equal(string.Join("\n", b.Log.Select(l => l.Node + l.Text)), string.Join("\n", again.Log.Select(l => l.Node + l.Text)));
    }

    [Fact]
    public void AiBanIsDeterministicAndValid()
    {
        var x = Make(true, false, 5); var y = Make(true, false, 5);
        int bx = Ai.ChooseBan(x, 0), by = Ai.ChooseBan(y, 0);
        Assert.Equal(bx, by);
        Assert.InRange(bx, 0, 5);
    }
}
