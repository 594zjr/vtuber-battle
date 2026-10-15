using System.Collections.Generic;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T23_PersonaAiIntegrationTests
{
    const string RiskySkill = "summary_ai_risky";
    const string SafeSkill = "summary_ai_safe";

    [Fact]
    public void EndureCountsOnlyActualDamageAndCreditsKoOnTheNextHit()
    {
        var battle = Make(100, 30);
        battle.Active(1).Def.Persona = "summary_endure";
        Equip(battle, 0, 0, "summary_lethal", Fixed(77));

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        var survived = BattleSummary.From(battle);
        Assert.Equal(1, battle.Active(1).Hp);
        Assert.True(battle.Active(1).EndureUsed);
        Assert.False(survived.Sides[1][0].Fainted);
        Assert.Equal(29, survived.Sides[0][0].DamageDealt);
        Assert.Equal(29, survived.Sides[1][0].DamageTaken);
        Assert.Equal(0, survived.Sides[0][0].Kos);

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        var finished = BattleSummary.From(battle);
        Assert.Equal(0, battle.Active(1).Hp);
        Assert.True(finished.Sides[1][0].Fainted);
        Assert.Equal(30, finished.Sides[0][0].DamageDealt);
        Assert.Equal(30, finished.Sides[1][0].DamageTaken);
        Assert.Equal(1, finished.Sides[0][0].Kos);
        Assert.Equal(2, finished.Sides[0][0].SkillUses["summary_lethal"]);
        Assert.Equal(29, survived.Sides[1][0].DamageTaken);
        Assert.Equal(0, survived.Sides[0][0].Kos);
    }

    [Fact]
    public void KoHealDoesNotSubtractFromAccumulatedDamageTaken()
    {
        var battle = Make(100, 40);
        battle.Active(0).Def.Persona = "summary_ko_heal";
        Equip(battle, 1, 0, "summary_incoming", Fixed(60));
        Equip(battle, 0, 0, "summary_finish", Fixed(77));
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        Assert.Equal(40, battle.Active(0).Hp);
        Assert.Equal(60, BattleSummary.From(battle).Sides[0][0].DamageTaken);

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        var summary = BattleSummary.From(battle);
        Assert.Equal(65, battle.Active(0).Hp);
        Assert.Contains(battle.Log, entry => entry.Kind == "heal" && entry.Text.Contains("击倒对手后"));
        Assert.Equal(60, summary.Sides[0][0].DamageTaken);
        Assert.Equal(60, summary.Sides[1][0].DamageDealt);
        Assert.Equal(40, summary.Sides[0][0].DamageDealt);
        Assert.Equal(40, summary.Sides[1][0].DamageTaken);
        Assert.Equal(1, summary.Sides[0][0].Kos);
        Assert.True(summary.Sides[1][0].Fainted);
        Assert.Equal(1, summary.Sides[0][0].SkillUses["summary_finish"]);
        Assert.Equal(1, summary.Sides[1][0].SkillUses["summary_incoming"]);
    }

    [Fact]
    public void HardSimulationCannotChangeExistingStatisticsStateOrLogs()
    {
        var battle = MakeHardBattle();
        var forced = MakeHardBattle();
        var untouched = MakeHardBattle();
        var initial = BattleSummary.From(battle);
        Assert.Equal(199, initial.Sides[0][0].DamageTaken);
        Assert.Equal(11, initial.Sides[0][0].DamageDealt);
        Assert.Equal(1, initial.Sides[0][0].TurnsActive);
        Assert.Equal(1, initial.Sides[0][0].SkillUses["summary_ai_prepare"]);
        Assert.True(battle.Active(0).EndureUsed);
        Assert.True(battle.Active(1).FirstControlImmunityUsed);

        // 危险分支先造成伤害再自伤下播，让评估副本确实经过伤害与击倒计数入口。
        forced.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        var forcedSummary = BattleSummary.From(forced);
        Assert.True(forced.Active(0).Fainted);
        Assert.Equal(21, forcedSummary.Sides[0][0].DamageDealt);
        Assert.Equal(399, forcedSummary.Sides[0][0].DamageTaken);
        Assert.Equal(1, forcedSummary.Sides[1][0].Kos);
        Assert.Contains(forced.Log, entry => entry.Text.Contains("技能中断"));

        string before = Snapshot(battle);
        string beforeSummary = Snapshot(initial);
        string beforeText = initial.ToText();
        var action = Ai.Choose(battle, 0, AiLevel.Hard);

        Assert.Equal(ActionType.Skill, action.Type);
        Assert.Equal(1, action.Index);
        Assert.Equal(SafeSkill, battle.Active(0).Skills[action.Index].Id);
        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(beforeSummary, Snapshot(BattleSummary.From(battle)));
        Assert.Equal(beforeText, BattleSummary.From(battle).ToText());
        for (int i = 0; i < 8; i++) Assert.Equal(untouched.Random(), battle.Random());
    }

    static Battle Make(int myHp, int foeHp)
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["summary_none"] = new PersonaDef { Id = "summary_none", Name = "统计测试人设", Code = "TEST_NONE" };
        db.Personas["summary_endure"] = new PersonaDef { Id = "summary_endure", Name = "统计测试保命", Code = "T_ENDURE" };
        db.Personas["summary_first_control"] = new PersonaDef { Id = "summary_first_control", Name = "统计测试免控", Code = "T_FIRST_CONTROL_IMMUNE" };
        db.Personas["summary_ko_heal"] = new PersonaDef
        {
            Id = "summary_ko_heal", Name = "统计测试回复", Code = "T_KO_HEAL", Params = { ["frac"] = 0.25 }
        };
        foreach (var streamer in db.Streamers)
        {
            streamer.Persona = "summary_none";
            streamer.Stats["hp"] = 1000;
            foreach (var stat in ContentDb.StatKeys) streamer.Stats[stat] = 100;
        }
        db.Streamer("bell").Stats["hp"] = myHp;
        db.Streamer("bell").Stats["spd"] = 1000;
        db.Streamer("kei").Stats["hp"] = foeHp;
        db.Streamer("kei").Stats["spd"] = 1;
        return new Battle(db, new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "你", "对手" }, 7);
    }

    static Battle MakeHardBattle()
    {
        var battle = Make(200, 1000);
        battle.Active(0).Def.Persona = "summary_endure";
        battle.Active(1).Def.Persona = "summary_first_control";
        foreach (var side in battle.Sides)
            foreach (var slot in side.Team[side.Active].Skills) slot.Pp = 0;
        Equip(battle, 1, 0, SafeSkill);
        Equip(battle, 0, 2, "summary_ai_prepare",
            Fixed(250, "self", "pre"), Fixed(11),
            new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "stall" },
            new EffectDef { At = "post", Code = "HEAL", Target = "self", Frac = 1 });
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        battle.Active(0).Skills[2].Pp = 0;
        var risky = Equip(battle, 0, 0, RiskySkill, Fixed(10, "foe", "pre"), Fixed(250, "self", "pre"));
        risky.Cat = "talent";
        risky.Power = 5000;
        var safe = Equip(battle, 0, 1, SafeSkill);
        safe.Cat = "talent";
        safe.Power = 20;
        return battle;
    }

    static SkillDef Equip(Battle battle, int side, int index, string id, params EffectDef[] effects)
    {
        var skill = new SkillDef { Id = id, Name = "统计整合测试技能", Type = "tech", Cat = "status", Pp = 10, Prio = 10 };
        skill.Effects.AddRange(effects);
        battle.Db.Skills[id] = skill;
        battle.Active(side).Skills[index] = new SkillSlot { Id = id, Pp = 10, Max = 10 };
        return skill;
    }

    static EffectDef Fixed(int amount, string target = "foe", string at = "post")
    {
        return new EffectDef { At = at, Code = "FIXED_DAMAGE", Target = target, N = amount };
    }

    static string Snapshot<T>(T value)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true });
    }
}
