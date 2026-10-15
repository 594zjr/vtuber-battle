using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T91_AiExplainTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    static Battle Make(uint seed, int rotation = 0, ContentDb db = null, string modifier = null)
    {
        db = db ?? BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        int offset = rotation % ids.Length;
        var order = ids.Skip(offset).Concat(ids.Take(offset)).ToArray();
        return new Battle(db, new IList<string>[] { order.Take(6).ToArray(), order.Skip(6).Take(6).ToArray() },
            new[] { "说明甲", "说明乙" }, new BattleOptions
            {
                Seed = seed,
                Modifiers = modifier == null ? new List<string>() : new List<string> { modifier }
            });
    }

    static ContentDb NeutralDb()
    {
        var db = BattleTests.LoadPack("original");
        foreach (var streamer in db.Streamers) streamer.Persona = "pure";
        return db;
    }

    static (ActionType, int) ActionKey(BattleAction action) => (action.Type, action.Index);
    static string PublicSnapshot(Battle battle) => JsonSerializer.Serialize(battle, JsonOptions);

    static void AssertLegal(Battle battle, int side, BattleAction action)
    {
        var legal = battle.LegalFor(side);
        if (action.Type == ActionType.Switch)
        {
            Assert.Contains(action.Index, legal.Switches);
            Assert.False(battle.Sides[side].Team[action.Index].Fainted);
            Assert.NotEqual(battle.Sides[side].Active, action.Index);
        }
        else if (legal.Controlled != null) Assert.Equal(ActionType.Pass, action.Type);
        else if (legal.Struggle) Assert.Equal(ActionType.Struggle, action.Type);
        else
        {
            Assert.Equal(ActionType.Skill, action.Type);
            Assert.Contains(legal.Skills, skill => skill.Index == action.Index && skill.Usable);
        }
    }

    static AiExplanation AssertExplanationMatchesChoose(Battle battle, int side, AiLevel level)
    {
        ulong hash = battle.StateHash();
        string before = PublicSnapshot(battle);
        string record = battle.Record.ToJson();
        string summary = BattleSummary.From(battle).ToText();
        string contentHash = battle.Db.ContentHash();
        var untouchedRandom = battle.Clone();
        var chooseInput = battle.Clone();

        var explanation = Ai.Explain(battle, side, level);

        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(before, PublicSnapshot(battle));
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(summary, BattleSummary.From(battle).ToText());
        Assert.Equal(contentHash, battle.Db.ContentHash());
        var afterRandom = battle.Clone();
        for (int i = 0; i < 8; i++) Assert.Equal(untouchedRandom.Random(), afterRandom.Random());

        var expected = Ai.Choose(chooseInput, side, level);
        Assert.NotNull(explanation);
        Assert.Equal(ActionKey(expected), ActionKey(explanation.Chosen));
        AssertLegal(battle, side, explanation.Chosen);
        Assert.NotNull(explanation.Options);
        Assert.NotEmpty(explanation.Options);
        Assert.Contains(explanation.Options, option => ActionKey(option.Action) == ActionKey(explanation.Chosen));
        Assert.Equal(explanation.Options.Count, explanation.Options.Select(option => ActionKey(option.Action)).Distinct().Count());
        double previousScore = double.PositiveInfinity;
        foreach (var option in explanation.Options)
        {
            Assert.NotNull(option);
            Assert.False(double.IsNaN(option.Score));
            Assert.False(double.IsInfinity(option.Score));
            Assert.True(option.Score <= previousScore, "选项必须按分数降序排列。");
            previousScore = option.Score;
            AssertLegal(battle, side, option.Action);
            Assert.NotNull(option.Reasons);
            Assert.NotEmpty(option.Reasons);
            Assert.All(option.Reasons, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
            Assert.Contains(option.Reasons, reason => reason.Any(c => c >= '\u4e00' && c <= '\u9fff'));
        }
        return explanation;
    }

    static Battle RandomPosition(int index)
    {
        var battle = Make((uint)(9100 + index * 37), index);
        for (int turn = 0; turn < 1 + index % 5; turn++)
        {
            Assert.Equal(Phase.Choose, battle.Phase);
            battle.ResolveTurn(Ai.Choose(battle, 0, index % 2 == 0 ? AiLevel.Easy : AiLevel.Normal),
                Ai.Choose(battle, 1, index % 2 == 0 ? AiLevel.Normal : AiLevel.Easy));
            for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, AiLevel.Normal));
        }
        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.NotEmpty(battle.Record.Events);
        Assert.Contains(battle.Log, entry => entry.Node == Node.TurnHeader);
        return battle;
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void TwentySeededPlayedPositionsMatchChooseForBothSidesWithoutChangingAnything(AiLevel level)
    {
        var states = new HashSet<ulong>();
        for (int position = 0; position < 20; position++)
        {
            var battle = RandomPosition(position);
            Assert.True(states.Add(battle.StateHash()));
            for (int side = 0; side < 2; side++) AssertExplanationMatchesChoose(battle, side, level);
        }
        Assert.Equal(20, states.Count);
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void ControlledExhaustedLockedAndBenchBoundaryOptionsRemainLegal(AiLevel level)
    {
        string[] scenarios = { "controlled", "controlled_locked", "empty_pp", "empty_pp_locked", "partial_pp", "no_switch", "one_reserve", "no_reserve" };
        for (int scenarioIndex = 0; scenarioIndex < scenarios.Length; scenarioIndex++)
        {
            string scenario = scenarios[scenarioIndex];
            for (int side = 0; side < 2; side++)
            {
                var battle = Make((uint)(910 + scenarioIndex * 2 + side), db: NeutralDb(),
                    modifier: scenario == "no_switch" ? "no_switch" : null);
                var actor = battle.Active(side);
                if (scenario.StartsWith("controlled", StringComparison.Ordinal))
                    actor.Statuses.Add(new StatusInst { Id = "disconnect", Left = 2 });
                if (scenario.EndsWith("locked", StringComparison.Ordinal))
                    actor.Fields.Add(new FieldInst { Id = "lockin", Turns = 3 });
                if (scenario.StartsWith("empty_pp", StringComparison.Ordinal))
                    foreach (var skill in actor.Skills) skill.Pp = 0;
                if (scenario == "partial_pp")
                    foreach (int index in new[] { 0, 1, 4 }) actor.Skills[index].Pp = 0;
                if (scenario == "one_reserve" || scenario == "no_reserve")
                    for (int index = 1; index < battle.Sides[side].Team.Count; index++)
                    {
                        if (scenario == "one_reserve" && index == 4) continue;
                        battle.Sides[side].Team[index].Hp = 0;
                        battle.Sides[side].Team[index].Fainted = true;
                    }

                var explanation = AssertExplanationMatchesChoose(battle, side, level);
                if (scenario == "controlled_locked") Assert.Equal(ActionType.Pass, explanation.Chosen.Type);
                if (scenario == "empty_pp_locked") Assert.Equal(ActionType.Struggle, explanation.Chosen.Type);
                if (scenario == "no_switch" || scenario.EndsWith("locked", StringComparison.Ordinal) || scenario == "no_reserve")
                    Assert.DoesNotContain(explanation.Options, option => option.Action.Type == ActionType.Switch);
                if (scenario == "partial_pp")
                    Assert.DoesNotContain(explanation.Options, option => option.Action.Type == ActionType.Skill
                        && new[] { 0, 1, 4 }.Contains(option.Action.Index));
                if (scenario == "one_reserve")
                    Assert.All(explanation.Options.Where(option => option.Action.Type == ActionType.Switch),
                        option => Assert.Equal(4, option.Action.Index));
            }
        }
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void FreePpDependsOnTheLivingSixthTeammateAndDoesNotApplyToItself(AiLevel level)
    {
        foreach (string scenario in new[] { "living_teammate", "fainted_teammate", "actor_is_sixth" })
        {
            var db = NeutralDb();
            var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
            db.Streamer(ids[0]).Persona = "teamspirit";
            db.Streamer(ids[5]).Persona = "teamspirit";
            var battle = Make(917, db: db);
            if (scenario == "actor_is_sixth") battle.ResolveTurn(BattleAction.SwitchTo(5), BattleAction.Pass());
            if (scenario == "fainted_teammate")
            {
                battle.Sides[0].Team[5].Hp = 0;
                battle.Sides[0].Team[5].Fainted = true;
            }
            foreach (var skill in battle.Active(0).Skills) skill.Pp = 0;
            battle.Active(0).Fields.Add(new FieldInst { Id = "lockin", Turns = 3 });
            Assert.Equal(scenario == "living_teammate", battle.LegalFor(0).FreePp);

            var explanation = AssertExplanationMatchesChoose(battle, 0, level);
            Assert.Equal(scenario == "living_teammate" ? ActionType.Skill : ActionType.Struggle, explanation.Chosen.Type);
            if (scenario == "living_teammate") Assert.DoesNotContain(explanation.Options, option => option.Action.Type == ActionType.Struggle);
        }
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void BothPendingReplacementsAreRejectedUntilTheWholeReplacementPhaseFinishes(AiLevel level)
    {
        var db = NeutralDb();
        db.Skills["explain_double_faint"] = new SkillDef
        {
            Id = "explain_double_faint", Name = "双向下播测试", Type = "talk", Cat = "status", Pp = 10,
            Effects = new List<EffectDef>
            {
                new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 10000 },
                new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "self", N = 10000 }
            }
        };
        db.Streamers[0].Skills[0] = "explain_double_faint";
        var battle = Make(914, db: db);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.Equal(Phase.Replace, battle.Phase);
        Assert.All(battle.Need, needed => Assert.True(needed));
        var before = PublicSnapshot(battle);
        var hash = battle.StateHash();
        for (int side = 0; side < 2; side++) Assert.Throws<InvalidOperationException>(() => Ai.Explain(battle, side, level));
        Assert.Equal(before, PublicSnapshot(battle));
        Assert.Equal(hash, battle.StateHash());

        battle.Replace(0, 2);
        Assert.Equal(Phase.Replace, battle.Phase);
        Assert.Throws<InvalidOperationException>(() => Ai.Explain(battle, 0, level));
        battle.Replace(1, 3);
        Assert.Equal(Phase.Choose, battle.Phase);
        for (int side = 0; side < 2; side++)
        {
            var explanation = AssertExplanationMatchesChoose(battle, side, level);
            Assert.DoesNotContain(explanation.Options, option => option.Action.Type == ActionType.Switch && option.Action.Index == 0);
        }
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void StyledFightersKeepTheirActualStrategyInTheExplanation(AiLevel level)
    {
        foreach (string style in new[] { "aggressive", "defensive", "tricky" })
        {
            var db = NeutralDb();
            foreach (var streamer in db.Streamers) streamer.AiStyle = style;
            var battle = Make(915, db: db);
            battle.Active(1).Hp = Math.Max(1, battle.Active(1).MaxHp / 3);
            battle.Active(0).Stages["atk"] = 2;
            battle.Active(0).LastSkillIndex = 0;
            AssertExplanationMatchesChoose(battle, 1, level);
        }
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void RepeatedCallsAreDeterministicAndReturnedReasonsAreIndependent(AiLevel level)
    {
        var battle = RandomPosition(4);
        var explanation = AssertExplanationMatchesChoose(battle, 0, level);
        string expected = JsonSerializer.Serialize(explanation, JsonOptions);
        string source = PublicSnapshot(battle);
        ulong hash = battle.StateHash();
        explanation.Chosen = BattleAction.SwitchTo(-1);
        explanation.Options[0].Action = BattleAction.SwitchTo(-1);
        explanation.Options[0].Score = -999;
        explanation.Options[0].Reasons.Clear();
        explanation.Options[0].Reasons.Add("外部修改");
        explanation.Options.Clear();

        Assert.Equal(expected, JsonSerializer.Serialize(Ai.Explain(battle, 0, level), JsonOptions));
        Assert.Equal(expected, JsonSerializer.Serialize(Ai.Explain(battle, 0, level), JsonOptions));
        Assert.Equal(source, PublicSnapshot(battle));
        Assert.Equal(hash, battle.StateHash());
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void EquivalentSkillsExplainTheSameSeededTieOrRandomChoiceWithoutUsingLiveRandomness(AiLevel level)
    {
        var selected = new HashSet<int>();
        foreach (uint seed in new uint[] { 0, 1, 7, 42 })
        {
            var battle = Make(seed, db: NeutralDb());
            battle.Active(0).Fields.Add(new FieldInst { Id = "lockin", Turns = 3 });
            for (int i = 0; i < battle.Active(0).Skills.Count; i++)
            {
                var skill = battle.Active(0).Skills[i];
                skill.Id = "warm_up";
                skill.Max = 10;
                skill.Pp = i < 2 ? 10 : 0;
            }
            var explanation = AssertExplanationMatchesChoose(battle, 0, level);
            Assert.Equal(2, explanation.Options.Count);
            Assert.All(explanation.Options, option => Assert.Equal(ActionType.Skill, option.Action.Type));
            Assert.InRange(explanation.Chosen.Index, 0, 1);
            selected.Add(explanation.Chosen.Index);
        }
        Assert.Equal(2, selected.Count);
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void InvalidArgumentsAndNonActionPhasesAreRejectedWithoutMutation(AiLevel level)
    {
        var battle = Make(916);
        string before = PublicSnapshot(battle);
        ulong hash = battle.StateHash();
        Assert.Throws<ArgumentNullException>(() => Ai.Explain(null, 0, level));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ai.Explain(battle, -1, level));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ai.Explain(battle, 2, level));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ai.Explain(battle, 0, (AiLevel)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ai.Explain(battle, 0, (AiLevel)4));
        Assert.Equal(before, PublicSnapshot(battle));
        Assert.Equal(hash, battle.StateHash());

        foreach (Phase phase in new[] { Phase.Ban, Phase.Lead, Phase.Over })
        {
            // 这里只验证入口阶段约束；真实双侧补位流程由独立用例覆盖。
            battle.Phase = phase;
            before = PublicSnapshot(battle);
            hash = battle.StateHash();
            for (int side = 0; side < 2; side++) Assert.Throws<InvalidOperationException>(() => Ai.Explain(battle, side, level));
            Assert.Equal(before, PublicSnapshot(battle));
            Assert.Equal(hash, battle.StateHash());
        }
    }
}
