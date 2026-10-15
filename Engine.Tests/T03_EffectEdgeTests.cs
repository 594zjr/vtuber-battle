using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T03_EffectEdgeTests
{
    static Battle Make(ContentDb db, string lead0 = "kei", string lead1 = "bell")
    {
        IList<string> Team(string lead) => new[] { lead }.Concat(db.Streamers.Select(s => s.Id).Where(id => id != lead)).Take(6).ToArray();
        return new Battle(db, new[] { Team(lead0), Team(lead1) }, new[] { "你", "对手" }, 31);
    }

    static SkillDef Equip(Battle b, int side, int index, params EffectDef[] effects)
    {
        string id = "test_edge_" + side + "_" + index;
        var skill = new SkillDef { Id = id, Name = "边界测试技能" + index, Type = "game", Cat = "status", Pp = 10, Prio = 10 };
        skill.Effects.AddRange(effects);
        b.Db.Skills[id] = skill;
        b.Active(side).Skills[index] = new SkillSlot { Id = id, Pp = 10, Max = 10, Fifth = index == 4 };
        return skill;
    }

    static EffectDef Fixed(string at, string target, int n = 77)
    {
        return new EffectDef { At = at, Code = "FIXED_DAMAGE", Target = target, N = n };
    }

    static EffectDef Reduce(string at, string target, int n = 3)
    {
        return new EffectDef { At = at, Code = "PP_REDUCE", Target = target, N = n };
    }

    [Fact]
    public void MissedFifthSkillStillRecordsItsIndexAndConsumesPp()
    {
        var b = Make(BattleTests.LoadPack("original"));
        var skill = Equip(b, 0, 4, Fixed("post", "foe"));
        skill.Acc = 0;
        var p = b.Active(0);
        int foeHp = b.Active(1).Hp;
        Assert.Equal(-1, p.LastSkillIndex);

        b.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());

        Assert.Equal(4, p.LastSkillIndex);
        Assert.Equal(9, p.Skills[4].Pp);
        Assert.Equal(foeHp, b.Active(1).Hp);
        Assert.Contains(b.Log, l => l.Kind == "miss");
    }

    [Fact]
    public void FreePpSkillRecordsItsIndexEvenAtZeroPp()
    {
        var b = Make(BattleTests.LoadPack("original"), "nyako");
        Equip(b, 0, 3);
        var p = b.Active(0);
        p.Skills[3].Pp = 0;

        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());

        Assert.Equal(3, p.LastSkillIndex);
        Assert.Equal(0, p.Skills[3].Pp);
        Assert.Contains(b.Log, l => l.Kind == "act" && l.Side == 0);
    }

    [Theory]
    [InlineData("control")]
    [InlineData("struggle")]
    [InlineData("invalid")]
    [InlineData("pass")]
    public void SkippedOrFallbackActionsDoNotReplaceTheLastSkill(string mode)
    {
        var b = Make(BattleTests.LoadPack("original"));
        Equip(b, 0, 2);
        b.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        var p = b.Active(0);
        var action = BattleAction.Skill(4);
        if (mode == "control") p.Statuses.Add(new StatusInst { Id = "stall", Left = 1 });
        if (mode == "struggle") foreach (var slot in p.Skills) slot.Pp = 0;
        if (mode == "invalid") action = BattleAction.Skill(99);
        if (mode == "pass") action = BattleAction.Pass();

        b.ResolveTurn(action, BattleAction.Pass());

        Assert.Equal(2, p.LastSkillIndex);
        if (mode == "struggle") Assert.Contains(b.Log, l => l.Text.Contains("使用「硬撑」"));
    }

    [Fact]
    public void SwitchingOutAndBackClearsTheLastSkillButPreservesPp()
    {
        var b = Make(BattleTests.LoadPack("original"));
        Equip(b, 0, 2);
        var p = b.Active(0);
        b.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());

        b.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
        Assert.Equal(-1, p.LastSkillIndex);
        Assert.Equal(-1, b.Active(0).LastSkillIndex);
        b.ResolveTurn(BattleAction.SwitchTo(0), BattleAction.Pass());

        Assert.Same(p, b.Active(0));
        Assert.Equal(-1, p.LastSkillIndex);
        Assert.Equal(9, p.Skills[2].Pp);
    }

    [Fact]
    public void ReplacingAFaintedFighterAlsoClearsItsLastSkill()
    {
        var b = Make(BattleTests.LoadPack("original"));
        Equip(b, 0, 2);
        var p = b.Active(0);
        b.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        Equip(b, 1, 0, Fixed("post", "foe", p.Hp));
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        Assert.True(p.Fainted);
        Assert.Equal(Phase.Replace, b.Phase);

        b.Replace(0, 1);

        Assert.Equal(-1, p.LastSkillIndex);
        Assert.Equal(-1, b.Active(0).LastSkillIndex);
    }

    [Fact]
    public void PpReductionClampsToZeroAndLogsTheActualChangeInTheActionNode()
    {
        var b = Make(BattleTests.LoadPack("original"));
        Equip(b, 0, 0, Reduce("post", "foe")).Prio = 0;
        Equip(b, 1, 3);
        var foe = b.Active(1);
        foe.Skills[3].Pp = 2;

        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(3));

        Assert.Equal(3, foe.LastSkillIndex);
        Assert.Equal(0, foe.Skills[3].Pp);
        Assert.True(foe.Skills.Where((slot, index) => index != 3).All(slot => slot.Pp == slot.Max));
        Assert.Contains(b.Log, l => l.Side == 1 && l.Node == Node.Second && l.Text.Contains("PP -1"));
    }

    [Theory]
    [InlineData("pre")]
    [InlineData("post")]
    public void SelfPpReductionTargetsTheSkillBeingUsedAfterItsOwnPpCost(string at)
    {
        var b = Make(BattleTests.LoadPack("original"));
        Equip(b, 0, 4, Reduce(at, "self"));

        b.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());

        Assert.Equal(4, b.Active(0).LastSkillIndex);
        Assert.Equal(6, b.Active(0).Skills[4].Pp);
        Assert.True(b.Active(1).Skills.All(slot => slot.Pp == slot.Max));
    }

    [Theory]
    [InlineData("pre", "self", "interact", "kei")]
    [InlineData("post", "self", "talent", "kei")]
    [InlineData("pre", "foe", "interact", "kei")]
    [InlineData("post", "foe", "interact", "kei")]
    [InlineData("pre", "foe", "talent", "lumi")]
    [InlineData("post", "foe", "talent", "lumi")]
    public void FixedDamageIgnoresStagesTypesFieldsAndDamagePersonas(string at, string target, string category, string foeId)
    {
        var b = Make(BattleTests.LoadPack("original"), "enka", foeId);
        var skill = Equip(b, 0, 0, Fixed(at, target));
        skill.Cat = category;
        var p = b.Active(0);
        p.Hp = 100;
        p.Stages["atk"] = 6;
        p.Stages["spa"] = 6;
        p.Fields.Add(new FieldInst { Id = "charge", Turns = 3 });
        var tgt = b.Active(target == "self" ? 0 : 1);
        tgt.Stages["def"] = 6;
        tgt.Stages["sdef"] = 6;
        tgt.Fields.Add(new FieldInst { Id = "shield", Turns = 3 });
        tgt.Fields.Add(new FieldInst { Id = "filter", Turns = 3 });
        int hp = tgt.Hp;

        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(hp - 77, tgt.Hp);
        Assert.True(p.HasField("charge"));
        Assert.Contains(b.Log, l => l.Kind == "dmg" && l.Side == tgt.Side && l.Node == Node.First && l.Text.Contains("77"));
    }

    [Theory]
    [InlineData("pre")]
    [InlineData("post")]
    public void FixedDamageWakesAfkWithoutTriggeringAfterHitPersona(string at)
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["trollback"].Params["chance"] = 100;
        var b = Make(db, "kei", "buck");
        Equip(b, 0, 0, Fixed(at, "foe")).Cat = "interact";
        var foe = b.Active(1);
        foe.Statuses.Add(new StatusInst { Id = "afk", Left = 3 });
        int hp = foe.Hp;

        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));

        Assert.Equal(hp - 77, foe.Hp);
        Assert.False(foe.HasStatus("afk"));
        Assert.False(b.Active(0).HasStatus("flame"));
        Assert.Equal(-1, foe.LastSkillIndex);
        Assert.Contains(b.Log, l => l.Side == 1 && l.Text.Contains("回过神"));
        Assert.DoesNotContain(b.Log, l => l.Kind == "trait" && l.Side == 1);
    }

    [Fact]
    public void LethalFixedDamageClampsToRemainingHpAndStillTriggersFaintPersona()
    {
        var b = Make(BattleTests.LoadPack("original"), "kei", "yuan");
        Equip(b, 0, 0, Fixed("post", "foe"));
        var foe = b.Active(1);
        foe.Hp = 50;

        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(0, foe.Hp);
        Assert.True(foe.Fainted);
        Assert.Equal(Phase.Replace, b.Phase);
        Assert.True(b.Need[1]);
        Assert.True(b.Active(0).Stages.Values.All(n => n == -1));
        Assert.Contains(b.Log, l => l.Kind == "dmg" && l.Side == 1 && l.Text.Contains("50"));
        Assert.Contains(b.Log, l => l.Kind == "faint" && l.Side == 1);
    }

    [Theory]
    [InlineData("foe", "pre")]
    [InlineData("foe", "post")]
    [InlineData("self", "post")]
    public void FixedDamageEndingTheBattleStopsLaterEffectsAndNodes(string target, string at)
    {
        var b = Make(BattleTests.LoadPack("original"));
        int loser = target == "self" ? 0 : 1;
        var tgt = b.Active(loser);
        tgt.Hp = 20;
        foreach (var bench in b.Sides[loser].Team.Skip(1))
        {
            bench.Hp = 0;
            bench.Fainted = true;
        }
        Equip(b, 0, 0, Fixed(at, target), new EffectDef
        {
            At = "post", Code = "STAGE", Target = target == "self" ? "foe" : "self",
            Changes = new Dictionary<string, int> { { "atk", 1 } }
        });

        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(Phase.Over, b.Phase);
        Assert.Equal(1 - loser, b.Winner);
        Assert.True(tgt.Fainted);
        Assert.Equal(0, b.Active(1 - loser).Stages["atk"]);
        Assert.Equal(Node.Over, b.Log.Last().Node);
        Assert.DoesNotContain(b.Log, l => l.Node == Node.TurnEnd || l.Node == Node.Second);
    }

    [Fact]
    public void FixedDamagePreservesEffectTimingAndArrayOrder()
    {
        var b = Make(BattleTests.LoadPack("original"));
        Equip(b, 0, 0,
            Fixed("post", "foe", 29),
            Fixed("pre", "foe", 13),
            Fixed("post", "foe", 31),
            Fixed("pre", "foe", 17));

        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        var damage = b.Log.Where(l => l.Kind == "dmg" && l.Side == 1).ToArray();
        Assert.Equal(4, damage.Length);
        int[] expected = { 13, 17, 29, 31 };
        int before = b.Active(1).MaxHp;
        for (int i = 0; i < damage.Length; i++)
        {
            int after = damage[i].SnapHp[1][0];
            Assert.Equal(expected[i], before - after);
            before = after;
        }
    }

    [Fact]
    public void DeterministicEffectsDoNotConsumeExtraRandomness()
    {
        var changed = Make(BattleTests.LoadPack("original"));
        var repeated = Make(BattleTests.LoadPack("original"));
        var baseline = Make(BattleTests.LoadPack("original"));
        foreach (var b in new[] { changed, repeated, baseline })
        {
            Equip(b, 0, 0);
            Equip(b, 1, 2).Prio = 20;
        }
        foreach (var b in new[] { changed, repeated })
        {
            b.Db.Skills[b.Active(0).Skills[0].Id].Effects.Add(Reduce("pre", "foe"));
            b.Db.Skills[b.Active(0).Skills[0].Id].Effects.Add(Fixed("post", "foe"));
        }
        foreach (var b in new[] { changed, repeated, baseline }) b.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(2));

        Assert.Equal(changed.Log.Select(l => l.Node + ":" + l.Text), repeated.Log.Select(l => l.Node + ":" + l.Text));
        Assert.Equal(changed.Active(1).Hp, repeated.Active(1).Hp);
        Assert.Equal(changed.Active(1).Skills[2].Pp, repeated.Active(1).Skills[2].Pp);
        Assert.Equal(baseline.Random(), changed.Random());
    }

    [Theory]
    [InlineData("PP_REDUCE", "pre", "self")]
    [InlineData("PP_REDUCE", "post", "foe")]
    [InlineData("FIXED_DAMAGE", "pre", "foe")]
    [InlineData("FIXED_DAMAGE", "post", "self")]
    public void DescriptionsShowTargetChanceTimingAndDispelRule(string code, string at, string target)
    {
        var effect = new EffectDef { Code = code, At = at, Target = target, N = 3, Chance = 40 };

        var description = Describe.Effect(BattleTests.LoadPack("original"), effect);

        Assert.Equal(at == "pre" ? "命中前判定" : "命中后判定", description.At);
        Assert.Contains(target == "self" ? "自身" : "对手", description.Text);
        Assert.Contains("40%", description.Text);
        Assert.Contains("3", description.Text);
        Assert.Contains("不可驱散", description.Dispel);
    }
}
