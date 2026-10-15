using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T05_PreviewEdgeTests
{
    static Battle Make(string lead0, string lead1, uint seed = 42)
    {
        var db = BattleTests.LoadPack("original");
        var team0 = new[] { lead0 }.Concat(db.Streamers.Select(x => x.Id).Where(x => x != lead0).Take(5)).ToArray();
        var team1 = new[] { lead1 }.Concat(db.Streamers.Select(x => x.Id).Where(x => x != lead1).Take(5)).ToArray();
        return new Battle(db, new IList<string>[] { team0, team1 }, new[] { "你", "对手" }, seed);
    }

    static int SkillIndex(Battle b, int side, string id)
    {
        return b.Active(side).Skills.FindIndex(x => x.Id == id);
    }

    [Fact]
    public void InteractEndpointsIncludeStagesStatusFieldsAndBothPersonas()
    {
        var b = Make("enka", "kei");
        var attacker = b.Active(0);
        var defender = b.Active(1);
        attacker.Hp = 100;
        attacker.Stages["atk"] = 1;
        defender.Stages["def"] = -1;
        attacker.Statuses.Add(new StatusInst { Id = "flame", Left = 3 });
        defender.Fields.Add(new FieldInst { Id = "shield", Turns = 2 });

        var preview = b.PreviewDamage(0, SkillIndex(b, 0, "aim_burst"));

        // 固定算例独立锁定端点：基础 167，叠加倍率 1.512。
        Assert.Equal(214, preview.Min);
        Assert.Equal(252, preview.Max);
        Assert.Equal(2.0, preview.Eff);
        Assert.Contains(preview.Notes, x => x.Contains("炎上"));
        Assert.Contains(preview.Notes, x => x.Contains("残血翻盘"));
        Assert.Contains(preview.Notes, x => x.Contains("房管护盘"));
        Assert.Contains(preview.Notes, x => x.Contains("老司机"));
    }

    [Fact]
    public void TalentEndpointsIncludeChargeAndBenchGuardWithoutConsumingFields()
    {
        var b = Make("bell", "lumi");
        var attacker = b.Active(0);
        var defender = b.Active(1);
        attacker.Stages["spa"] = 1;
        defender.Stages["sdef"] = -1;
        var charge = new FieldInst { Id = "charge", Turns = 5 };
        attacker.Fields.Add(charge);
        defender.Fields.Add(new FieldInst { Id = "shield", Turns = 2 });
        int logCount = b.Log.Count;
        int index = SkillIndex(b, 0, "full_volume");
        int pp = attacker.Skills[index].Pp;

        var preview = b.PreviewDamage(0, index);

        // 定力先向下取整为 193；基础为 130 × 510 / 193 + 2，总倍率 2.7。
        Assert.Equal(792, preview.Min);
        Assert.Equal(932, preview.Max);
        Assert.Equal(2.0, preview.Eff);
        Assert.Contains(preview.Notes, x => x.Contains("憋大招"));
        Assert.Contains(preview.Notes, x => x.Contains("家族群"));
        Assert.Same(charge, attacker.FieldOf("charge"));
        Assert.Equal(5, charge.Turns);
        Assert.Equal(pp, attacker.Skills[index].Pp);
        Assert.Equal(1, attacker.Stages["spa"]);
        Assert.Equal(defender.MaxHp, defender.Hp);
        Assert.Equal(logCount, b.Log.Count);
    }

    [Theory]
    [InlineData("momo", "scare_combo", 103, 121)]
    [InlineData("shiori", "late_reply", 88, 103)]
    public void PowerConditionsAlwaysAssumeFirstAction(string lead, string skill, int min, int max)
    {
        var b = Make(lead, "kei");
        b.ActedFirst[0] = false;
        var preview = b.PreviewDamage(0, SkillIndex(b, 0, skill));
        Assert.Equal(min, preview.Min);
        Assert.Equal(max, preview.Max);
        Assert.False(b.ActedFirst[0]);
    }

    [Fact]
    public void CurrentMarkStacksIncreasePowerButPreviewDoesNotConsumeThem()
    {
        var b = Make("nyako", "yuan");
        var mark = new FieldInst { Id = "hype", Stacks = 3, Turns = -1 };
        b.Active(0).Fields.Add(mark);
        int logCount = b.Log.Count;

        var preview = b.PreviewDamage(0, SkillIndex(b, 0, "iconic_scene"));
        var again = b.PreviewDamage(0, SkillIndex(b, 0, "iconic_scene"));

        Assert.Equal(178, preview.Min);
        Assert.Equal(209, preview.Max);
        Assert.Equal(preview.Min, again.Min);
        Assert.Equal(preview.Max, again.Max);
        Assert.Same(mark, b.Active(0).FieldOf("hype"));
        Assert.Equal(3, mark.Stacks);
        Assert.Equal(-1, mark.Turns);
        Assert.Equal(logCount, b.Log.Count);
    }

    [Fact]
    public void PriorityIncludesFieldsAndFirstTurnPersonaBeforeComparingSpeed()
    {
        var b = Make("enka", "bell");
        b.Active(0).Stages["spd"] = 6;
        b.Active(0).Fields.Add(new FieldInst { Id = "early", Turns = 3 });
        var a0 = BattleAction.Skill(SkillIndex(b, 0, "quick_mic"));
        var a1 = BattleAction.Skill(SkillIndex(b, 1, "warm_up"));

        var firstTurn = b.PreviewOrder(a0, a1);
        Assert.Equal(1, firstTurn.First);
        Assert.Equal("先制 3 对 2", firstTurn.Reason);

        b.Turn = 2;
        var later = b.PreviewOrder(a0, a1);
        Assert.Equal(0, later.First);
        Assert.Equal("先制都是 2，反应 1280 对 360", later.Reason);

        b.Active(0).Fields.Add(new FieldInst { Id = "lag", Turns = 3 });
        var delayed = b.PreviewOrder(a0, a1);
        Assert.Equal(1, delayed.First);
        Assert.Equal("先制 2 对 1", delayed.Reason);
    }

    [Theory]
    [InlineData(ActionType.Skill, -1)]
    [InlineData(ActionType.Skill, 99)]
    [InlineData(ActionType.Switch, 0)]
    [InlineData(ActionType.Switch, 99)]
    public void InvalidActionsAreNormalizedBeforeOrderPrediction(ActionType type, int index)
    {
        var b = Make("enka", "bell");
        b.Turn = 2;
        var preview = b.PreviewOrder(new BattleAction { Type = type, Index = index },
            BattleAction.Skill(SkillIndex(b, 1, "full_volume")));

        Assert.Equal(1, preview.First);
        Assert.Equal("先制都是 0，反应 360 对 320", preview.Reason);
        Assert.Equal(0, b.Sides[0].Active);
    }

    [Fact]
    public void EmptyPpAndControlRemoveChosenSkillPriority()
    {
        var b = Make("enka", "bell");
        b.Turn = 2;
        int quick = SkillIndex(b, 0, "quick_mic");
        b.Active(0).Skills[quick].Pp = 0;
        var a0 = BattleAction.Skill(quick);
        var a1 = BattleAction.Skill(SkillIndex(b, 1, "full_volume"));
        Assert.Equal(1, b.PreviewOrder(a0, a1).First);

        b.Active(0).Skills[quick].Pp = 5;
        var control = new StatusInst { Id = "stall", Left = 1 };
        b.Active(0).Statuses.Add(control);
        var preview = b.PreviewOrder(a0, a1);
        Assert.Equal(1, preview.First);
        Assert.Equal("先制都是 0，反应 360 对 320", preview.Reason);
        Assert.Equal(1, control.Left);
        Assert.Equal(5, b.Active(0).Skills[quick].Pp);
    }

    [Fact]
    public void ExhaustedSkillsNormalizeToStruggleAndKeepFieldPriority()
    {
        var b = Make("enka", "bell");
        b.Turn = 2;
        foreach (var slot in b.Active(0).Skills) slot.Pp = 0;
        b.Active(0).Fields.Add(new FieldInst { Id = "early", Turns = 2 });

        var preview = b.PreviewOrder(BattleAction.Skill(0),
            BattleAction.Skill(SkillIndex(b, 1, "full_volume")));

        Assert.Equal(0, preview.First);
        Assert.Equal("先制 1 对 0", preview.Reason);
        Assert.All(b.Active(0).Skills, slot => Assert.Equal(0, slot.Pp));
    }

    [Fact]
    public void BothSwitchesHaveNoActionAndDoNotChangeActiveFighters()
    {
        var b = Make("enka", "bell");
        int logCount = b.Log.Count;
        var preview = b.PreviewOrder(BattleAction.SwitchTo(1), BattleAction.SwitchTo(1));
        Assert.Equal(-1, preview.First);
        Assert.Contains("双方换人", preview.Reason);
        Assert.Equal(0, b.Sides[0].Active);
        Assert.Equal(0, b.Sides[1].Active);
        Assert.Equal(logCount, b.Log.Count);
    }

    [Fact]
    public void TiedOrderAndProbabilisticSkillPreviewsLeaveRandomSequenceUntouched()
    {
        var b = Make("bell", "bell");
        var baseline = Make("bell", "bell");
        int logCount = b.Log.Count;

        for (int i = 0; i < 10; i++)
        {
            var preview = b.PreviewOrder(BattleAction.Skill(0), BattleAction.Skill(0));
            Assert.Equal(-1, preview.First);
            Assert.Equal("随机", preview.Reason);
            for (int slot = 0; slot < 5; slot++) b.PreviewDamage(0, slot);
        }

        Assert.Equal(logCount, b.Log.Count);
        Assert.Empty(b.Active(0).Statuses);
        Assert.Empty(b.Active(1).Statuses);
        for (int i = 0; i < 20; i++) Assert.Equal(baseline.Random(), b.Random());
    }

    [Fact]
    public void DefaultDamageEstimateKeepsExistingMidpointAndDoesNotRoll()
    {
        var b = Make("bell", "kei");
        var baseline = Make("bell", "kei");
        var estimate = b.CalcDamage(0, b.Db.Skills["full_volume"], 1, true);
        Assert.Equal(238, estimate.Dmg);
        Assert.False(estimate.Crit);
        Assert.Equal(baseline.Random(), b.Random());
    }

    [Fact]
    public void OptionalEstimateFactorDoesNotAlterRealDamageOrRandomSequence()
    {
        var b = Make("bell", "kei");
        var baseline = Make("bell", "kei");
        var actual = b.CalcDamage(0, b.Db.Skills["full_volume"], 1, false, 0.01);
        var expected = baseline.CalcDamage(0, baseline.Db.Skills["full_volume"], 1, false);
        Assert.Equal(expected.Dmg, actual.Dmg);
        Assert.Equal(expected.Crit, actual.Crit);
        Assert.Equal(baseline.Random(), b.Random());
    }
}
