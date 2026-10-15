using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T53_AiStyleEdgeTests
{
    static readonly string[] Styles = { "aggressive", "defensive", "tricky" };

    static Battle Make(string style, uint seed = 53)
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["style_neutral"] = new PersonaDef { Id = "style_neutral", Name = "测试人设", Code = "T_LOWHP_POWER" };
        AddSkill(db, "style_attack", 500);
        AddSkill(db, "style_heal", 0, new EffectDef { At = "post", Code = "HEAL", Target = "self", Frac = 0.5 });
        AddSkill(db, "style_control", 0, new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "disconnect" });
        AddSkill(db, "style_idle", 0);
        foreach (var streamer in db.Streamers)
        {
            streamer.Type = "talk";
            streamer.Type2 = null;
            streamer.Persona = "style_neutral";
            streamer.Stats["hp"] = 1000;
            foreach (string stat in ContentDb.StatKeys) streamer.Stats[stat] = 100;
            streamer.Skills = new List<string> { "style_attack", "style_heal", "style_control", "style_idle" };
            streamer.Fifth = "style_idle";
        }
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        db.Streamer(ids[0]).AiStyle = style;
        db.Streamer(ids[0]).Stats["spd"] = 120;
        var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "甲方", "乙方" }, seed);
        foreach (var fighter in battle.Sides[0].Team)
        {
            fighter.Hp = 400;
            fighter.Skills[3].Pp = fighter.Skills[4].Pp = 0;
        }
        foreach (var fighter in battle.Sides[1].Team)
            foreach (var slot in fighter.Skills) slot.Id = "style_idle";
        foreach (var side in battle.Sides) side.Team[side.Active].Fields.Add(new FieldInst { Id = "lockin", Turns = -1 });
        return battle;
    }

    static void AddSkill(ContentDb db, string id, int power, params EffectDef[] effects)
    {
        db.Skills[id] = new SkillDef
        {
            Id = id, Name = "风格测试技能", Type = "talk", Cat = power > 0 ? "interact" : "status",
            Power = power, Pp = 10, Effects = effects.ToList()
        };
    }

    static string Snapshot(Battle battle)
    {
        return JsonSerializer.Serialize(battle, new JsonSerializerOptions { IncludeFields = true })
            + "\n" + BattleSummary.From(battle).ToText();
    }

    static void AssertLegal(Battle battle, BattleAction action)
    {
        var legal = battle.LegalFor(0);
        if (action.Type == ActionType.Switch) Assert.Contains(action.Index, legal.Switches);
        else if (legal.Controlled != null) Assert.Equal(ActionType.Pass, action.Type);
        else if (legal.Struggle) Assert.Equal(ActionType.Struggle, action.Type);
        else
        {
            Assert.Equal(ActionType.Skill, action.Type);
            Assert.Contains(legal.Skills, option => option.Index == action.Index && option.Usable);
        }
    }

    [Theory]
    [InlineData("aggressive")]
    [InlineData("defensive")]
    [InlineData("tricky")]
    public void PresetsAreIndependentMutableSnapshots(string style)
    {
        var original = Styles.ToDictionary(name => name, name => AiStyles.Get(name).ToJson());
        string defaults = AiWeights.Default.ToJson();
        var changed = AiStyles.Get(style);
        foreach (string name in changed.Names()) changed.Set(name, changed.Get(name) + 10);

        Assert.NotEqual(original[style], changed.ToJson());
        foreach (string name in Styles) Assert.Equal(original[name], AiStyles.Get(name).ToJson());
        Assert.Equal(defaults, AiWeights.Default.ToJson());
    }

    [Fact]
    public void NewWeightsKeepLegacyDefaultsAndSurviveSerialization()
    {
        var weights = AiWeights.Default;
        Assert.Equal(1, weights.DamageScoreMultiplier);
        Assert.Equal(0, weights.PpReduceScore);
        weights.DamageScoreMultiplier = 1.75;
        weights.PpReduceScore = 0.625;

        var restored = AiWeights.FromJson(weights.ToJson());

        Assert.Equal(1.75, restored.DamageScoreMultiplier);
        Assert.Equal(0.625, restored.PpReduceScore);
        Assert.Equal(1, AiWeights.FromJson("{}").DamageScoreMultiplier);
        Assert.Equal(0, AiWeights.FromJson("{}").PpReduceScore);
    }

    [Theory]
    [InlineData("aggressive", 0)]
    [InlineData("defensive", 1)]
    [InlineData("tricky", 2)]
    public void NormalStylesPreferAttackRecoveryAndDisruptionInTheSamePosition(string style, int expected)
    {
        foreach (uint seed in new uint[] { 1, 53, 2026 })
        {
            var battle = Make(style, seed);
            var action = Ai.Choose(battle, 0, AiLevel.Normal);

            Assert.Equal(BattleAction.Skill(expected).ToString(), action.ToString());
        }
    }

    [Theory]
    [InlineData("FIELD", "defensive")]
    [InlineData("DISPEL_TURNS", "tricky")]
    [InlineData("PP_REDUCE", "tricky")]
    public void UtilityStylesCanPreferShieldDispelAndPpReductionOverDamage(string code, string style)
    {
        Battle Position(string selectedStyle)
        {
            var battle = Make(selectedStyle);
            battle.Active(0).Hp = battle.Active(0).MaxHp;
            battle.Active(0).Skills[1].Pp = 0;
            battle.Db.Skills["style_attack"].Power = 400;
            var effect = new EffectDef { At = "post", Code = code, Target = code == "FIELD" ? "self" : "foe", Field = "shield", Turns = 3, N = 5 };
            battle.Db.Skills["style_control"].Effects = new List<EffectDef> { effect };
            if (code == "DISPEL_TURNS")
            {
                battle.Active(1).Fields.Clear();
                battle.Active(1).Fields.Add(new FieldInst { Id = "sub", Turns = 4 });
            }
            if (code == "PP_REDUCE") battle.Active(1).LastSkillIndex = 0;
            return battle;
        }
        var aggressive = Position("aggressive");
        var utility = Position(style);

        Assert.Equal(BattleAction.Skill(0).ToString(), Ai.Choose(aggressive, 0).ToString());
        Assert.Equal(BattleAction.Skill(2).ToString(), Ai.Choose(utility, 0).ToString());
    }

    [Theory]
    [InlineData("unused")]
    [InlineData("empty")]
    [InlineData("zero_chance")]
    [InlineData("condition")]
    public void TrickyDoesNotValuePpReductionThatCannotTakeAnyPp(string reason)
    {
        var battle = Make("tricky");
        battle.Active(0).Hp = battle.Active(0).MaxHp;
        battle.Active(0).Skills[1].Pp = 0;
        var effect = new EffectDef { At = "post", Code = "PP_REDUCE", Target = "foe", N = 10 };
        battle.Db.Skills["style_control"].Effects = new List<EffectDef> { effect };
        battle.Active(1).LastSkillIndex = reason == "unused" ? -1 : 0;
        if (reason == "empty") battle.Active(1).Skills[0].Pp = 0;
        if (reason == "zero_chance") effect.Chance = 0;
        if (reason == "condition") effect.Cond = new CondDef { Kind = "foeHpAbove", Value = 1 };

        Assert.Equal(BattleAction.Skill(0).ToString(), Ai.Choose(battle, 0).ToString());
    }

    [Fact]
    public void CurrentStyleOverridesExplicitNormalWeightsWithoutChangingTheCallerWeights()
    {
        var weights = AiWeights.Default;
        weights.HealMissingHpScore = 100;
        weights.DamageScoreMultiplier = 0;
        weights.ControlStatusScore = 0;
        string before = weights.ToJson();
        var styled = Make("aggressive");
        var plain = Make(null);

        var styledAction = Ai.Choose(styled, 0, AiLevel.Normal, weights);
        var plainAction = Ai.Choose(plain, 0, AiLevel.Normal, weights);

        Assert.Equal(BattleAction.Skill(0).ToString(), styledAction.ToString());
        Assert.Equal(BattleAction.Skill(1).ToString(), plainAction.ToString());
        Assert.Equal(before, weights.ToJson());
    }

    [Fact]
    public void SwitchingReadsTheNewActiveStreamersStyle()
    {
        var battle = Make("aggressive");
        battle.Sides[0].Team[1].Def.AiStyle = "defensive";
        Assert.Equal(BattleAction.Skill(0).ToString(), Ai.Choose(battle, 0).ToString());
        battle.Active(0).Fields.Clear();

        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
        battle.Active(0).Fields.Add(new FieldInst { Id = "lockin", Turns = -1 });

        Assert.Equal(1, battle.Sides[0].Active);
        Assert.Equal(BattleAction.Skill(1).ToString(), Ai.Choose(battle, 0).ToString());
    }

    [Theory]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void AttackAndDefenseStylesAffectEachSupportedDifficulty(AiLevel level)
    {
        var aggressive = Make("aggressive");
        var defensive = Make("defensive");
        if (level == AiLevel.Expert)
        {
            // 限定一发攻击，让先攻后补与先补后攻的两回合终态接近，检验首步风格而非后续技能储备。
            foreach (var battle in new[] { aggressive, defensive })
            {
                battle.Active(0).Hp = 600;
                battle.Active(0).Skills[0].Pp = 1;
                battle.Active(0).Skills[2].Pp = 0;
            }
        }

        var attack = Ai.Choose(aggressive, 0, level);
        var defend = Ai.Choose(defensive, 0, level);

        AssertLegal(aggressive, attack);
        AssertLegal(defensive, defend);
        Assert.Equal(BattleAction.Skill(0).ToString(), attack.ToString());
        Assert.Equal(BattleAction.Skill(1).ToString(), defend.ToString());
    }

    [Theory]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void StyledChoicesAreLegalDeterministicAndLeaveTheRealPositionIntact(AiLevel level)
    {
        foreach (string style in Styles)
        for (int state = 0; state < 3; state++)
        {
            var battle = Make(style, (uint)(53 + state));
            if (state == 1) battle.Active(0).Statuses.Add(new StatusInst { Id = "disconnect", Left = 2 });
            if (state == 2) foreach (var slot in battle.Active(0).Skills) slot.Pp = 0;
            var same = battle.Clone();
            string before = Snapshot(battle);

            var action = Ai.Choose(battle, 0, level);
            var repeated = Ai.Choose(same, 0, level);

            AssertLegal(battle, action);
            Assert.Equal(action.ToString(), repeated.ToString());
            Assert.Equal(before, Snapshot(battle));
            Assert.Equal(before, Snapshot(same));
            Assert.Equal(battle.StateHash(), same.StateHash());
        }
    }

    [Theory]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void StyleEvaluationDoesNotConsumeRealRandomnessForASingleLegalAction(AiLevel level)
    {
        foreach (string style in Styles)
        {
            var battle = Make(style);
            foreach (var slot in battle.Active(0).Skills) slot.Pp = 0;
            battle.Active(0).Skills[0].Pp = 1;
            ulong before = battle.StateHash();

            var action = Ai.Choose(battle, 0, level);

            Assert.Equal(BattleAction.Skill(0).ToString(), action.ToString());
            Assert.Equal(before, battle.StateHash());
        }
    }

    [Fact]
    public void EasyIgnoresStylesIncludingItsNormalStrategyBranch()
    {
        foreach (string style in Styles)
        for (uint seed = 0; seed < 12; seed++)
        {
            var plain = Make(null, seed);
            var styled = Make(style, seed);

            var expected = Ai.Choose(plain, 0, AiLevel.Easy);
            var actual = Ai.Choose(styled, 0, AiLevel.Easy);

            Assert.Equal(expected.ToString(), actual.ToString());
            Assert.Equal(plain.Random(), styled.Random());
        }
    }

    [Fact]
    public void UnstyledOpponentDoesNotInheritTheOtherSidesPreset()
    {
        var styled = Make("tricky");
        var plain = Make(null);
        foreach (var battle in new[] { styled, plain })
        {
            battle.Active(1).Hp = 400;
            for (int i = 0; i < 5; i++)
            {
                battle.Active(1).Skills[i].Id = battle.Active(0).Skills[i].Id;
                battle.Active(1).Skills[i].Pp = battle.Active(0).Skills[i].Pp;
            }
        }
        Assert.Null(styled.Active(1).Def.AiStyle);

        Ai.Choose(styled, 0);
        Ai.Choose(plain, 0);
        var expected = Ai.Choose(plain, 1);
        var actual = Ai.Choose(styled, 1);

        Assert.Equal(expected.ToString(), actual.ToString());
        Assert.Equal(plain.Random(), styled.Random());
    }
}
