// T-41 验收测试：给画面用的只读视图模型。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T41_BattleViewModelTests
{
    static Battle Make()
    {
        var db = BattleTests.LoadPack("fan_asoul_vr");
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        return new Battle(db, new System.Collections.Generic.IList<string>[] { ids.Take(6).ToList(), ids.Skip(1).Take(6).ToList() }, new[] { "你", "电脑" }, 2);
    }

    [Fact]
    public void ViewMirrorsBattleState()
    {
        var b = Make();
        b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
        var v = BattleViewModel.From(b, 0);
        Assert.Equal(b.Turn, v.Turn);
        Assert.Equal(2, v.Sides.Length);
        Assert.Equal("你", v.Sides[0].Name);
        for (int s = 0; s < 2; s++)
        {
            Assert.Equal(b.Sides[s].Active, v.Sides[s].Active);
            for (int i = 0; i < 6; i++)
            {
                var f = b.Sides[s].Team[i]; var fv = v.Sides[s].Team[i];
                Assert.Equal(f.Name, fv.Name); Assert.Equal(f.Hp, fv.Hp); Assert.Equal(f.MaxHp, fv.MaxHp);
                Assert.Equal(f.Fainted, fv.Fainted);
                Assert.Equal(f.Statuses.Count, fv.Statuses.Count(c => c.IsStatus));
                Assert.Equal(f.Fields.Count, fv.Statuses.Count(c => !c.IsStatus));
                Assert.All(fv.Statuses, c => Assert.False(string.IsNullOrEmpty(c.Tooltip)));
            }
        }
        Assert.Contains("非官方同人作品", v.Notice);
    }

    [Fact]
    public void SkillButtonsFollowLegalMoves()
    {
        var b = Make();
        var v = BattleViewModel.From(b, 0);
        var legal = b.LegalFor(0);
        Assert.Equal(5, v.Skills.Count);
        Assert.True(v.Skills[4].Fifth);
        for (int i = 0; i < 5; i++)
        {
            var opt = legal.Skills.First(o => o.Index == i);
            Assert.Equal(opt.Usable, v.Skills[i].Usable);
            Assert.Equal(opt.Pp, v.Skills[i].Pp);
            Assert.Equal(opt.Prio.Value, v.Skills[i].Priority);
            Assert.False(string.IsNullOrEmpty(v.Skills[i].Name));
            Assert.All(v.Skills[i].Effects, e => Assert.True(e.Timing == "命中前" || e.Timing == "命中后"));
        }
        Assert.Equal(legal.Switches.Count, v.SwitchTargets.Count);
    }

    [Fact]
    public void BuildingViewDoesNotChangeBattle()
    {
        var b = Make();
        var h = b.StateHash();
        BattleViewModel.From(b, 0); BattleViewModel.From(b, 1);
        Assert.Equal(h, b.StateHash());
    }
}
