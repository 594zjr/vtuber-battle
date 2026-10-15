using System.Collections.Generic;
using StreamDuel.Core;
using Xunit;

public class T103_CodeBehaviorTests
{
    static Battle Make()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["t103_neutral"] = new PersonaDef
        {
            Id = "t103_neutral", Name = "边界测试人设", Code = "T_LOWHP_POWER"
        };
        foreach (var streamer in db.Streamers) streamer.Persona = "t103_neutral";
        return new Battle(db, new IList<string>[]
        {
            new[] { "kei", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "bell", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "你", "对手" }, 103);
    }

    static void Equip(Battle battle, int power, params EffectDef[] effects)
    {
        var skill = new SkillDef
        {
            Id = "t103_boundary", Name = "边界测试技能", Type = "talk",
            Cat = power == 0 ? "status" : "interact", Power = power,
            Pp = 20, Acc = 100, Prio = 10
        };
        skill.Effects.AddRange(effects);
        battle.Db.Skills[skill.Id] = skill;
        battle.Active(0).Skills[0] = new SkillSlot { Id = skill.Id, Pp = 20, Max = 20 };
    }

    [Theory]
    [InlineData("pre")]
    [InlineData("post")]
    public void DispelTurnsIgnoresSelfTargetAndOnlyRemovesOpposingDispellableFields(string at)
    {
        var battle = Make();
        foreach (int side in new[] { 0, 1 })
        {
            battle.Active(side).Fields.Add(new FieldInst { Id = "early", Turns = 5 });
            battle.Active(side).Fields.Add(new FieldInst { Id = "hype", Turns = -1, Stacks = 2 });
        }
        Equip(battle, 0, new EffectDef { At = at, Code = "DISPEL_TURNS", Target = "self" });

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.NotNull(battle.Active(0).FieldOf("early"));
        Assert.Null(battle.Active(1).FieldOf("early"));
        Assert.Equal(2, battle.Active(0).FieldOf("hype").Stacks);
        Assert.Equal(2, battle.Active(1).FieldOf("hype").Stacks);
    }

    [Theory]
    [InlineData("pre")]
    [InlineData("post")]
    public void DispelBoostIgnoresSelfTargetAndPreservesOpposingNegativeStages(string at)
    {
        var battle = Make();
        battle.Active(0).Stages["atk"] = 2;
        battle.Active(0).Stages["def"] = -1;
        battle.Active(1).Stages["atk"] = 3;
        battle.Active(1).Stages["def"] = -2;
        Equip(battle, 0, new EffectDef { At = at, Code = "DISPEL_BOOST", Target = "self" });

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(2, battle.Active(0).Stages["atk"]);
        Assert.Equal(-1, battle.Active(0).Stages["def"]);
        Assert.Equal(0, battle.Active(1).Stages["atk"]);
        Assert.Equal(-2, battle.Active(1).Stages["def"]);
    }

    [Theory]
    [InlineData("RECOIL")]
    [InlineData("DRAIN")]
    public void RecoilAndDrainHaveNoDamageToUseBeforeTheOrdinaryHit(string code)
    {
        var before = Make();
        var after = Make();
        int initialHp = before.Active(0).MaxHp / 2;
        before.Active(0).Hp = after.Active(0).Hp = initialHp;
        Equip(before, 40, new EffectDef { At = "pre", Code = code, Frac = 0.5 });
        Equip(after, 40, new EffectDef { At = "post", Code = code, Frac = 0.5 });
        int targetHp = before.Active(1).Hp;

        before.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        after.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        int dealt = targetHp - before.Active(1).Hp;
        Assert.True(dealt >= 2);
        Assert.Equal(before.Active(1).Hp, after.Active(1).Hp);
        Assert.Equal(initialHp, before.Active(0).Hp);
        Assert.Equal(initialHp + (code == "DRAIN" ? dealt / 2 : -dealt / 2), after.Active(0).Hp);
    }

    [Theory]
    [InlineData("RECOIL")]
    [InlineData("DRAIN")]
    public void FixedDamageDoesNotSupplyTheAmountForFollowingRecoilOrDrain(string code)
    {
        var battle = Make();
        int initialHp = battle.Active(0).MaxHp / 2;
        battle.Active(0).Hp = initialHp;
        int targetHp = battle.Active(1).Hp;
        Equip(battle, 0,
            new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 37 },
            new EffectDef { At = "post", Code = code, Frac = 0.5 });

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(targetHp - 37, battle.Active(1).Hp);
        Assert.Equal(initialHp, battle.Active(0).Hp);
    }

    [Theory]
    [InlineData("POWER_MUL")]
    [InlineData("POWER_PER_MARK")]
    public void PostPowerEffectsDoNotChangeResolvedDamageButCanConsumeMarks(string code)
    {
        var baseline = Make();
        var withEffect = Make();
        baseline.Active(0).Fields.Add(new FieldInst { Id = "hype", Turns = -1, Stacks = 3 });
        withEffect.Active(0).Fields.Add(new FieldInst { Id = "hype", Turns = -1, Stacks = 3 });
        Equip(baseline, 40);
        Equip(withEffect, 40, new EffectDef
        {
            At = "post", Code = code, Mul = 4, Field = "hype", Per = 1, Consume = true
        });
        int targetHp = baseline.Active(1).Hp;

        baseline.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        withEffect.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.True(baseline.Active(1).Hp < targetHp);
        Assert.Equal(baseline.Active(1).Hp, withEffect.Active(1).Hp);
        Assert.Equal(3, baseline.Active(0).FieldOf("hype").Stacks);
        if (code == "POWER_PER_MARK")
        {
            Assert.Null(withEffect.Active(0).FieldOf("hype"));
            Assert.Contains(withEffect.Log, entry => entry.Text.Contains("全部消耗"));
        }
        else
        {
            Assert.Equal(3, withEffect.Active(0).FieldOf("hype").Stacks);
            Assert.Contains(withEffect.Log, entry => entry.Text.Contains("威力 ×4"));
        }
    }
}
