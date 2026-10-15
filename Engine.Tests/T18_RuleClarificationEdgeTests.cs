using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T18_RuleClarificationEdgeTests
{
    private static Battle Make(string lead0 = "kei", string lead1 = "bell")
    {
        var db = BattleTests.LoadPack("original");
        IList<string> Team(string lead)
        {
            return new[] { lead }.Concat(db.Streamers.Select(streamer => streamer.Id).Where(id => id != lead)).Take(6).ToArray();
        }
        return new Battle(db, new[] { Team(lead0), Team(lead1) }, new[] { "甲方", "乙方" }, 73);
    }

    private static SkillDef Equip(Battle battle, int side, int index, params EffectDef[] effects)
    {
        string id = "test_t18_" + side + "_" + index;
        var skill = new SkillDef { Id = id, Name = "规则边界技能", Type = "talk", Cat = "status", Pp = 10 };
        skill.Effects.AddRange(effects);
        battle.Db.Skills[id] = skill;
        battle.Active(side).Skills[index] = new SkillSlot { Id = id, Pp = 10, Max = 10, Fifth = index == 4 };
        return skill;
    }

    [Theory]
    [InlineData("field")]
    [InlineData("status")]
    public void MirrorDoesNotReflectPersistentFieldOrWeakStatusDamage(string source)
    {
        var battle = Make();
        var target = battle.Active(0);
        target.Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        double fraction;
        if (source == "field")
        {
            target.Fields.Add(new FieldInst { Id = "blackhist", Turns = -1 });
            fraction = battle.Db.Fields["blackhist"].Dot;
        }
        else
        {
            target.Statuses.Add(new StatusInst { Id = "hater", Left = 3 });
            fraction = battle.Db.Statuses["hater"].Dot;
        }
        int targetHp = target.Hp;
        int otherHp = battle.Active(1).Hp;
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        int expectedDamage = Math.Max(1, (int)(target.MaxHp * fraction));
        Assert.Equal(targetHp - expectedDamage, target.Hp);
        Assert.Equal(otherHp, battle.Active(1).Hp);
        var damage = Assert.Single(battle.Log.Skip(from), entry => entry.Kind == "dmg");
        Assert.Equal(Node.TurnEnd, damage.Node);
        Assert.Equal(0, damage.Side);
        Assert.Equal(targetHp - expectedDamage, damage.SnapHp[0][0]);
        Assert.True(target.HasField("mirror"));
    }

    [Fact]
    public void MidTurnPpExhaustionPreservesTheLastSkillAndDoesNotBecomeStruggle()
    {
        var battle = Make();
        var chosenSkill = Equip(battle, 0, 3);
        Equip(battle, 1, 0, new EffectDef { At = "post", Code = "PP_REDUCE", Target = "foe", N = 3 }).Prio = 10;
        battle.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());
        var fighter = battle.Active(0);
        Assert.Equal(3, fighter.LastSkillIndex);
        foreach (var slot in fighter.Skills) slot.Pp = 0;
        fighter.Skills[3].Pp = 1;
        int ownHp = fighter.Hp;
        int otherHp = battle.Active(1).Hp;
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Skill(3), BattleAction.Skill(0));

        Assert.Equal(0, fighter.Skills[3].Pp);
        Assert.Equal(3, fighter.LastSkillIndex);
        Assert.Equal(ownHp, fighter.Hp);
        Assert.Equal(otherHp, battle.Active(1).Hp);
        Assert.DoesNotContain(battle.Log.Skip(from), entry => entry.Kind == "act" && entry.Side == 0);
        Assert.Contains(battle.Log.Skip(from), entry => entry.Side == 0 && entry.Node == Node.Second
            && entry.Text.Contains(chosenSkill.Name) && entry.Text.Contains("PP 已耗尽") && entry.Text.Contains("行动失败"));

        // 下一回合开始时已经没有 PP，仍应走原有的硬撑规则。
        from = battle.Log.Count;
        battle.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());

        Assert.Contains(battle.Log.Skip(from), entry => entry.Kind == "act" && entry.Side == 0 && entry.Text.Contains(battle.Db.Struggle.Name));
        Assert.Equal(3, fighter.LastSkillIndex);
        Assert.True(battle.Active(1).Hp < otherHp);
    }

    [Fact]
    public void LastFighterSelfFaintLogsInterruptionBeforeTheFinalResult()
    {
        var battle = Make();
        foreach (var teammate in battle.Sides[0].Team.Skip(1))
        {
            teammate.Hp = 0;
            teammate.Fainted = true;
        }
        Equip(battle, 0, 0,
            new EffectDef { At = "pre", Code = "FIXED_DAMAGE", Target = "self", N = 999999 },
            new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 10 }).Prio = 10;
        int otherHp = battle.Active(1).Hp;
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(1, battle.Winner);
        Assert.Equal(otherHp, battle.Active(1).Hp);
        var actionLog = battle.Log.Skip(from).ToList();
        var interruption = Assert.Single(actionLog, entry => entry.Text.Contains("技能中断"));
        Assert.Equal(Node.First, interruption.Node);
        Assert.True(actionLog.FindIndex(entry => entry.Kind == "faint" && entry.Side == 0) < actionLog.IndexOf(interruption));
        Assert.Equal(Node.Over, actionLog.Last().Node);
        Assert.True(actionLog.IndexOf(interruption) < actionLog.FindIndex(entry => entry.Node == Node.Over));
        Assert.DoesNotContain(actionLog, entry => entry.Node == Node.TurnEnd || entry.Node == Node.Second);
    }

    [Fact]
    public void TeamSpiritStillUsesAZeroPpSkillWhileItsSixthTeammateIsAlive()
    {
        var battle = Make("nyako", "kei");
        var skill = Equip(battle, 0, 4, new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 37 });
        var fighter = battle.Active(0);
        foreach (var slot in fighter.Skills) slot.Pp = 0;
        Assert.False(battle.Sides[0].Team[5].Fainted);
        Assert.True(battle.LegalFor(0).FreePp);
        Assert.True(battle.LegalFor(0).Skills[4].Usable);
        Assert.False(battle.LegalFor(0).Struggle);
        int otherHp = battle.Active(1).Hp;
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());

        Assert.Equal(otherHp - 37, battle.Active(1).Hp);
        Assert.Equal(0, fighter.Skills[4].Pp);
        Assert.Equal(4, fighter.LastSkillIndex);
        Assert.Contains(battle.Log.Skip(from), entry => entry.Kind == "act" && entry.Side == 0 && entry.Text.Contains(skill.Name));
        Assert.DoesNotContain(battle.Log.Skip(from), entry => entry.Side == 0 && entry.Text.Contains("PP 已耗尽"));
        Assert.DoesNotContain(battle.Log.Skip(from), entry => entry.Kind == "act" && entry.Side == 0 && entry.Text.Contains(battle.Db.Struggle.Name));
    }
}
