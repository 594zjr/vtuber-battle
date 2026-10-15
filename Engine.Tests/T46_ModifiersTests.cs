// T-46 验收测试：规则修饰器。实现后把本文件移到 Engine.Tests/ 下。
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T46_ModifiersTests
{
    static readonly IList<string>[] Teams = {
        new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
        new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" } };

    static Battle Make(params string[] mods)
    {
        return new Battle(BattleTests.LoadPack("original"), Teams, new[] { "A", "B" },
            new BattleOptions { Seed = 3, Modifiers = mods.ToList() });
    }

    [Fact]
    public void AllModifiersAreListedWithChineseText()
    {
        var all = BattleModifiers.All;
        Assert.True(all.Count >= 6);
        foreach (var m in all) { Assert.False(string.IsNullOrEmpty(m.Name)); Assert.False(string.IsNullOrEmpty(m.Desc)); }
        Assert.Throws<KeyError>(() => Make("no_such_mod"));
    }

    [Fact]
    public void HalfPpHalvesMaxPpRoundingUp()
    {
        var plain = Make(); var half = Make("half_pp");
        for (int i = 0; i < plain.Active(0).Skills.Count; i++)
            Assert.Equal((plain.Active(0).Skills[i].Max + 1) / 2, half.Active(0).Skills[i].Max);
    }

    [Fact]
    public void StageCapLimitsStages()
    {
        var b = Make("stage_cap_3");
        b.Active(0).Stages["atk"] = 0;
        var db = b.Db;
        db.Skills["test_up"] = new SkillDef { Id = "test_up", Name = "测试强化", Type = "song", Cat = "status", Pp = 10, Prio = 5,
            Effects = { new EffectDef { At = "post", Code = "STAGE", Target = "self", Changes = { { "atk", 2 } } } } };
        b.Active(0).Skills[0] = new SkillSlot { Id = "test_up", Pp = 10, Max = 10 };
        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.Equal(3, b.Active(0).Stages["atk"]);
    }

    [Fact]
    public void SuddenDeathDrainsBothFromTurnTen()
    {
        var b = Make("sudden_death");
        b.Active(0).Hp = b.Active(0).MaxHp = 1600; b.Active(1).Hp = b.Active(1).MaxHp = 1600;
        for (int i = 0; i < 9; i++) b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(1600, b.Active(0).Hp);
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(1500, b.Active(0).Hp);
        Assert.Equal(1500, b.Active(1).Hp);
    }

    [Fact]
    public void ModifiersAreRecordedAndReplayed()
    {
        var b = Make("half_pp", "sudden_death", "hype_start");
        Assert.Equal(1, b.Active(0).FieldOf("hype").Stacks);
        for (int i = 0; i < 12 && b.Phase == Phase.Choose; i++) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
        var again = Replay.Run(b.Db, BattleRecord.FromJson(b.Record.ToJson()));
        Assert.Equal(b.StateHash(), again.StateHash());
    }
}
