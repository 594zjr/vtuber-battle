// T-03 验收测试：新效果码 PP_REDUCE 与 FIXED_DAMAGE。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T03_NewEffectCodesTests
{
    static Battle Make(ContentDb db)
    {
        return new Battle(db, new System.Collections.Generic.IList<string>[] {
            new[] { "kei", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "bell", "yuan", "nyako", "buck", "kasumi", "bee" } }, new[] { "你", "对手" }, 1);
    }

    [Fact]
    public void PpReduceHitsTheFoesLastUsedSkill()
    {
        var db = BattleTests.LoadPack("original");
        db.Skills["test_pp"] = new SkillDef { Id = "test_pp", Name = "测试减PP", Type = "talk", Cat = "status", Pp = 10,
            Effects = { new EffectDef { At = "post", Code = "PP_REDUCE", Target = "foe", N = 3 } } };
        var b = Make(db);
        b.Active(0).Skills[0] = new SkillSlot { Id = "test_pp", Pp = 10, Max = 10 };
        // 第 1 回合：对手先用技能 0（开嗓，先制 +2），我方后手减它最后用过的技能 PP
        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        var foeSlot = b.Active(1).Skills[0];
        Assert.Equal(foeSlot.Max - 1 - 3, foeSlot.Pp);
    }

    [Fact]
    public void PpReduceWithNoLastSkillDoesNothingAndLogsIt()
    {
        var db = BattleTests.LoadPack("original");
        db.Skills["test_pp"] = new SkillDef { Id = "test_pp", Name = "测试减PP", Type = "talk", Cat = "status", Pp = 10, Prio = 5,
            Effects = { new EffectDef { At = "post", Code = "PP_REDUCE", Target = "foe", N = 3 } } };
        var b = Make(db);
        b.Active(0).Skills[0] = new SkillSlot { Id = "test_pp", Pp = 10, Max = 10 };
        int from = b.Log.Count;
        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.True(b.Active(1).Skills.All(s => s.Pp == s.Max));
        Assert.Contains(b.Log.Skip(from), l => l.Text.Contains("还没有用过技能"));
    }

    [Fact]
    public void FixedDamageIgnoresStatsAndTypes()
    {
        var db = BattleTests.LoadPack("original");
        db.Skills["test_fixed"] = new SkillDef { Id = "test_fixed", Name = "测试固伤", Type = "talk", Cat = "status", Pp = 10,
            Effects = { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 77 } } };
        var b = Make(db);
        b.Active(0).Skills[0] = new SkillSlot { Id = "test_fixed", Pp = 10, Max = 10 };
        b.Active(1).Fields.Add(new FieldInst { Id = "shield", Turns = 3 });
        int hp = b.Active(1).Hp;
        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.Equal(hp - 77, b.Active(1).Hp);
    }
}
