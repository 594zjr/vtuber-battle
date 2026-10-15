// T-18 验收测试：三条规则澄清。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T18_RuleClarificationTests
{
    static Battle Make(ContentDb db)
    {
        return new Battle(db, new System.Collections.Generic.IList<string>[] {
            new[] { "kei", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "bell", "yuan", "nyako", "buck", "kasumi", "bee" } }, new[] { "你", "对手" }, 1);
    }

    [Fact]
    public void SkillWhosePpWasDrainedBeforeItsTurnFails()
    {
        var db = BattleTests.LoadPack("original");
        db.Skills["test_pp"] = new SkillDef { Id = "test_pp", Name = "测试减PP", Type = "talk", Cat = "status", Pp = 10, Prio = 5,
            Effects = { new EffectDef { At = "post", Code = "PP_REDUCE", Target = "foe", N = 3 } } };
        var b = Make(db);
        b.Active(0).Skills[0] = new SkillSlot { Id = "test_pp", Pp = 10, Max = 10 };
        b.Active(1).Hp = b.Active(1).MaxHp = 99999;
        b.Active(0).Hp = b.Active(0).MaxHp = 99999;
        // 第 1 回合：对手用技能 0，成为它“最后用过的技能”
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        b.Active(1).Skills[0].Pp = 1;
        int from = b.Log.Count;
        // 第 2 回合：对手选了技能 0（还剩 1 PP），我方先制 +5 先把它削到 0
        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        var lines = b.Log.Skip(from).ToList();
        Assert.Equal(0, b.Active(1).Skills[0].Pp);
        Assert.Contains(lines, l => l.Side == 1 && l.Text.Contains("PP 已耗尽"));
        Assert.DoesNotContain(lines, l => l.Kind == "act" && l.Side == 1);
    }

    [Fact]
    public void FaintingFromOwnPreEffectStopsTheRestOfTheSkill()
    {
        var db = BattleTests.LoadPack("original");
        db.Skills["test_self_ko"] = new SkillDef { Id = "test_self_ko", Name = "测试自爆", Type = "tech", Cat = "interact", Power = 80, Pp = 5, Prio = 5,
            Effects = {
                new EffectDef { At = "pre", Code = "FIXED_DAMAGE", Target = "self", N = 999999 },
                new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 10 } } };
        var b = Make(db);
        b.Active(0).Skills[0] = new SkillSlot { Id = "test_self_ko", Pp = 5, Max = 5 };
        int foeHp = b.Active(1).Hp;
        int from = b.Log.Count;
        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.True(b.Sides[0].Team[0].Fainted);
        Assert.Equal(foeHp, b.Sides[1].Team[0].Hp);
        Assert.Contains(b.Log.Skip(from), l => l.Side == 0 && l.Text.Contains("技能中断"));
    }
}
