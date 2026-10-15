using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T23_BattleSummaryEdgeTests
{
    static ContentDb NeutralDb()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["summary_none"] = new PersonaDef { Id = "summary_none", Name = "统计测试人设", Code = "TEST_NONE" };
        foreach (var streamer in db.Streamers)
        {
            streamer.Persona = "summary_none";
            streamer.Stats["hp"] = 1000;
        }
        return db;
    }

    static Battle Make(ContentDb db = null, bool leadPick = false)
    {
        db = db ?? NeutralDb();
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        return new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "你", "对手" }, 73, leadPick);
    }

    static SkillDef Equip(Battle battle, int side, string id, int slot = 0, params EffectDef[] effects)
    {
        var skill = new SkillDef { Id = id, Name = "统计测试技能", Type = "talk", Cat = "status", Pp = 20, Prio = 10 };
        skill.Effects.AddRange(effects);
        battle.Db.Skills[id] = skill;
        battle.Active(side).Skills[slot] = new SkillSlot { Id = id, Pp = 20, Max = 20 };
        return skill;
    }

    static EffectDef Fixed(int amount, string target = "foe")
    {
        return new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = target, N = amount };
    }

    [Fact]
    public void StatusAndFieldDamageAccumulateAcrossHealingWithoutDealtCredit()
    {
        var battle = Make();
        var fighter = battle.Active(0);
        fighter.Hp = fighter.MaxHp = 100;
        battle.Db.Statuses["summary_dot"] = new StatusDef { Id = "summary_dot", Name = "测试弱化", Cat = "weak", Dot = 0.2 };
        battle.Db.Fields["summary_field"] = new FieldDef { Id = "summary_field", Name = "测试持续效果", Kind = "turn", Dispel = true, Dot = 0.1 };
        fighter.Statuses.Add(new StatusInst { Id = "summary_dot", Left = 2 });
        fighter.Fields.Add(new FieldInst { Id = "summary_field", Turns = 2 });
        Equip(battle, 0, "summary_heal", 0, new EffectDef { At = "post", Code = "HEAL", Target = "self", Frac = 0.5 });

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(70, fighter.Hp);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        var summary = BattleSummary.From(battle);
        Assert.Equal(70, fighter.Hp);
        Assert.Equal(60, summary.Sides[0][0].DamageTaken);
        Assert.Equal(0, summary.Sides[0][0].DamageDealt);
        Assert.Equal(0, summary.Sides[1][0].DamageDealt);
        Assert.Equal(0, summary.Sides[1][0].DamageTaken);
        Assert.Equal(2, summary.Sides[0][0].TurnsActive);
        Assert.Equal(2, summary.Sides[1][0].TurnsActive);
    }

    [Fact]
    public void OverkillAndLethalRecoilCountActualLossAndCreditTheAlreadyFaintedOpponent()
    {
        var battle = Make();
        battle.Active(0).Hp = 7;
        battle.Active(1).Hp = 20;
        var skill = Equip(battle, 0, "summary_recoil", 0,
            new EffectDef { At = "post", Code = "RECOIL", Target = "self", Frac = 0.5 });
        skill.Cat = "interact";
        skill.Power = 10000;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));

        var summary = BattleSummary.From(battle);
        Assert.Equal(Phase.Replace, battle.Phase);
        Assert.Equal(20, summary.Sides[0][0].DamageDealt);
        Assert.Equal(7, summary.Sides[0][0].DamageTaken);
        Assert.Equal(0, summary.Sides[1][0].DamageDealt);
        Assert.Equal(20, summary.Sides[1][0].DamageTaken);
        for (int side = 0; side < 2; side++)
        {
            Assert.True(summary.Sides[side][0].Fainted);
            Assert.Equal(1, summary.Sides[side][0].Kos);
            Assert.Equal(1, summary.Sides[side][0].TurnsActive);
        }
        Assert.Equal(1, summary.Sides[0][0].SkillUses[skill.Id]);
        Assert.Empty(summary.Sides[1][0].SkillUses);
    }

    [Fact]
    public void MirrorCreditsItsOwnerAndCapsReflectionAtTheAttackersRemainingHp()
    {
        var battle = Make();
        battle.Active(0).Hp = 9;
        int defenderHp = battle.Active(1).Hp;
        battle.Active(0).Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        battle.Active(1).Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        var skill = Equip(battle, 0, "summary_reflected");
        skill.Cat = "interact";
        skill.Type = "tech";
        skill.Power = 200;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        var summary = BattleSummary.From(battle);
        int actualDamage = defenderHp - battle.Active(1).Hp;
        Assert.InRange(actualDamage, 34, defenderHp - 1);
        Assert.Equal(actualDamage, summary.Sides[0][0].DamageDealt);
        Assert.Equal(9, summary.Sides[0][0].DamageTaken);
        Assert.Equal(9, summary.Sides[1][0].DamageDealt);
        Assert.Equal(actualDamage, summary.Sides[1][0].DamageTaken);
        Assert.Equal(1, summary.Sides[1][0].Kos);
        Assert.True(summary.Sides[0][0].Fainted);
        Assert.False(summary.Sides[1][0].Fainted);
        Assert.Empty(summary.Sides[1][0].SkillUses);
    }

    [Fact]
    public void UsesKeepSkillIdsForSameNamesMissesAndStruggleButExcludeControlledActions()
    {
        var battle = Make();
        var miss = Equip(battle, 0, "summary_miss", 0, Fixed(10));
        miss.Acc = 0;
        var hit = Equip(battle, 0, "summary_hit", 1, Fixed(7));
        Assert.Equal(miss.Name, hit.Name);

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        battle.Active(0).Statuses.Add(new StatusInst { Id = "stall", Left = 1 });
        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        foreach (var slot in battle.Active(0).Skills) slot.Pp = 0;
        battle.Db.Struggle.Power = 10000;
        battle.Active(1).Hp = 20;
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        miss.Name = "改名后的未命中技能";
        hit.Name = "改名后的命中技能";

        var summary = BattleSummary.From(battle);
        var uses = summary.Sides[0][0].SkillUses;
        Assert.Equal(3, uses.Count);
        Assert.Equal(1, uses["summary_miss"]);
        Assert.Equal(1, uses["summary_hit"]);
        Assert.Equal(1, uses["struggle"]);
        Assert.Equal(27, summary.Sides[0][0].DamageDealt);
        Assert.Equal(5, summary.Sides[0][0].DamageTaken);
        Assert.Equal(27, summary.Sides[1][0].DamageTaken);
        Assert.Equal(0, summary.Sides[1][0].DamageDealt);
        Assert.Equal(3, battle.Log.Count(entry => entry.Kind == "act"));
    }

    [Fact]
    public void SwitchingSelfDamageAndReplacementKeepCountersOnTheCorrectFighter()
    {
        var battle = Make();
        Equip(battle, 1, "summary_incoming_hit", 0, Fixed(13));
        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Skill(0));
        var afterSwitch = BattleSummary.From(battle);
        Assert.Equal(0, afterSwitch.Sides[0][0].TurnsActive);
        Assert.Equal(0, afterSwitch.Sides[0][0].DamageTaken);
        Assert.Equal(1, afterSwitch.Sides[0][1].TurnsActive);
        Assert.Equal(13, afterSwitch.Sides[0][1].DamageTaken);

        Equip(battle, 0, "summary_self", 0, Fixed(10000, "self"));
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        var beforeReplacement = BattleSummary.From(battle);
        Assert.Equal(1000, beforeReplacement.Sides[0][1].DamageTaken);
        Assert.Equal(0, beforeReplacement.Sides[0][1].DamageDealt);
        Assert.Equal(13, beforeReplacement.Sides[1][0].DamageDealt);
        Assert.Equal(1, beforeReplacement.Sides[1][0].Kos);
        Assert.Equal(2, beforeReplacement.Sides[0][1].TurnsActive);
        battle.Replace(0, 2);
        Assert.Equal(0, BattleSummary.From(battle).Sides[0][2].TurnsActive);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        var summary = BattleSummary.From(battle);
        Assert.Equal(new[] { 0, 2, 1, 0, 0, 0 }, summary.Sides[0].Select(stats => stats.TurnsActive));
        Assert.Equal(3, summary.Sides[1][0].TurnsActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalKnockoutCountsBothActivesOnlyIfTheTurnEndNodeWasEntered(bool fromDot)
    {
        var battle = Make();
        foreach (var fighter in battle.Sides[1].Team.Skip(1))
        {
            fighter.Hp = 0;
            fighter.Fainted = true;
        }
        battle.Active(1).Hp = 25;
        if (fromDot)
        {
            battle.Active(1).Def.Stats["spd"] = 10000;
            battle.Db.Statuses["summary_lethal_dot"] = new StatusDef { Id = "summary_lethal_dot", Name = "测试持续伤害", Cat = "weak", Dot = 1 };
            battle.Active(1).Statuses.Add(new StatusInst { Id = "summary_lethal_dot", Left = 1 });
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        }
        else
        {
            Equip(battle, 0, "summary_finish", 0, Fixed(10000));
            battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        }

        var summary = BattleSummary.From(battle);
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(25, summary.Sides[1][0].DamageTaken);
        Assert.Equal(fromDot ? 0 : 25, summary.Sides[0][0].DamageDealt);
        Assert.Equal(1, summary.Sides[0][0].Kos);
        Assert.Equal(fromDot ? 1 : 0, summary.Sides[0][0].TurnsActive);
        Assert.Equal(fromDot ? 1 : 0, summary.Sides[1][0].TurnsActive);
    }

    [Fact]
    public void LeadReorderingAndMutableSnapshotsDoNotChangeBattleCounters()
    {
        var battle = Make(leadPick: true);
        battle.ChooseLead(0, 2);
        battle.ChooseLead(1, 4);
        Equip(battle, 0, "summary_snapshot", 0, Fixed(11));
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        var snapshot = BattleSummary.From(battle);
        string text = snapshot.ToText();
        for (int side = 0; side < 2; side++)
            Assert.Equal(battle.Sides[side].Team.Select(fighter => fighter.Def.Id), snapshot.Sides[side].Select(stats => stats.StreamerId));

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.Equal(text, snapshot.ToText());
        Assert.Equal(11, snapshot.Sides[0][0].DamageDealt);
        Assert.Equal(1, snapshot.Sides[0][0].SkillUses["summary_snapshot"]);
        snapshot.Sides[0][0].DamageDealt = -1;
        snapshot.Sides[0][0].SkillUses.Clear();
        snapshot.Sides[1].Clear();

        var fresh = BattleSummary.From(battle);
        Assert.Equal(22, fresh.Sides[0][0].DamageDealt);
        Assert.Equal(2, fresh.Sides[0][0].SkillUses["summary_snapshot"]);
        Assert.Equal(6, fresh.Sides[1].Count);
    }

    [Fact]
    public void CompletedLeadBattleReplaysEveryStatisticAndSummaryDoesNotConsumeRandomness()
    {
        var db = NeutralDb();
        db.Skills["summary_replay"] = new SkillDef
        {
            Id = "summary_replay", Name = "回放测试技能", Type = "talk", Cat = "status", Pp = 20,
            Effects = { Fixed(25) }
        };
        foreach (var streamer in db.Streamers)
        {
            streamer.Stats["hp"] = 50;
            streamer.Skills[0] = "summary_replay";
        }
        var battle = Make(db, true);
        battle.ChooseLead(0, 2);
        battle.ChooseLead(1, 4);
        for (int guard = 0; battle.Phase != Phase.Over && guard < 50; guard++)
        {
            if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
            else
                for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    if (battle.Need[side]) battle.Replace(side, battle.LegalFor(side).Switches[0]);
        }
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Contains(battle.Record.Events, entry => entry.Type == BattleRecordEventType.Replace);
        var replayed = Replay.Run(db, BattleRecord.FromJson(battle.Record.ToJson()));
        var actual = BattleSummary.From(battle);
        var expected = BattleSummary.From(replayed);
        Assert.Equal(expected.ToText(), actual.ToText());
        for (int side = 0; side < 2; side++)
            for (int slot = 0; slot < 6; slot++)
            {
                var left = expected.Sides[side][slot];
                var right = actual.Sides[side][slot];
                Assert.Equal(left.StreamerId, right.StreamerId);
                Assert.Equal(left.DamageDealt, right.DamageDealt);
                Assert.Equal(left.DamageTaken, right.DamageTaken);
                Assert.Equal(left.Kos, right.Kos);
                Assert.Equal(left.TurnsActive, right.TurnsActive);
                Assert.Equal(left.Fainted, right.Fainted);
                Assert.Equal(left.SkillUses.OrderBy(pair => pair.Key), right.SkillUses.OrderBy(pair => pair.Key));
            }
        for (int i = 0; i < 3; i++) BattleSummary.From(battle).ToText();
        for (int i = 0; i < 8; i++) Assert.Equal(replayed.Random(), battle.Random());
    }
}
