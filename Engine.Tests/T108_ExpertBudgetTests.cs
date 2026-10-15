using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T108_ExpertBudgetTests
{
    static readonly IList<string>[] Teams =
    {
        new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
        new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
    };

    static ContentDb QuietContent()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["t108_neutral"] = new PersonaDef
        {
            Id = "t108_neutral", Name = "稳定发挥", Code = "T_INTERACT_REDUCE", Node = Node.First,
            Desc = "互动伤害保持原值", Params = { { "mul", 1 } }
        };
        for (int index = 0; index < 5; index++)
        {
            string id = "t108_wait" + index;
            db.Skills[id] = new SkillDef { Id = id, Name = "等待片刻", Type = "song", Cat = "status", Pp = 1000 };
        }
        db.Skills["t108_hit"] = new SkillDef
        {
            Id = "t108_hit", Name = "固定输出", Type = "song", Cat = "status", Pp = 1000,
            Effects = new List<EffectDef> { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 100 } }
        };
        foreach (var streamer in db.Streamers)
        {
            streamer.Persona = "t108_neutral";
            streamer.AiStyle = null;
            streamer.Stats["hp"] = 1000;
            streamer.Stats["spd"] = 100;
            streamer.Skills = Enumerable.Range(0, 4).Select(index => "t108_wait" + index).ToList();
            streamer.Fifth = "t108_wait4";
        }
        db.Validate();
        return db;
    }

    static Battle QuietBattle(int usable = 1, bool locked = true, uint seed = 108)
    {
        var battle = new Battle(QuietContent(), Teams, new[] { "甲方", "乙方" }, seed);
        foreach (var fighter in battle.Sides.SelectMany(side => side.Team))
        {
            if (locked) fighter.Fields.Add(new FieldInst { Id = "lockin", Turns = -1 });
            for (int index = 0; index < fighter.Skills.Count; index++)
            {
                fighter.Skills[index].Id = "t108_wait0";
                fighter.Skills[index].Pp = index < usable ? 1000 : 0;
                fighter.Skills[index].Fifth = false;
            }
        }
        return battle;
    }

    static Battle ReplacementBattle(int side, bool both, bool sole = true)
    {
        var battle = QuietBattle();
        for (int current = 0; current < 2; current++)
        {
            if (current != side && !both) continue;
            for (int index = 0; index < battle.Sides[current].Team.Count; index++)
            {
                if (index != 0 && (!sole || index == 1)) continue;
                var fighter = battle.Sides[current].Team[index];
                fighter.Hp = 0;
                fighter.Fainted = true;
            }
            battle.Need[current] = true;
        }
        battle.Phase = Phase.Replace;
        return battle;
    }

    static string Snapshot(Battle battle)
    {
        // 私有随机流允许为最终同分选择前进一步；所有公开状态、共享定义和历史都必须保持原样。
        return JsonSerializer.Serialize(battle, new JsonSerializerOptions { IncludeFields = true })
            + "\n" + BattleSummary.From(battle).ToText();
    }

    static void SameDecision(ExpertDecision first, ExpertDecision second)
    {
        Assert.Equal(first.Action.Type, second.Action.Type);
        Assert.Equal(first.Action.Index, second.Action.Index);
        Assert.Equal(first.SimulatedNodes, second.SimulatedNodes);
        Assert.Equal(first.BudgetExhausted, second.BudgetExhausted);
    }

    static void LegalAction(Battle battle, int side, BattleAction action)
    {
        var legal = battle.LegalFor(side);
        if (action.Type == ActionType.Switch)
        {
            Assert.Contains(action.Index, legal.Switches);
            return;
        }
        if (legal.Controlled != null)
        {
            Assert.Equal(ActionType.Pass, action.Type);
            return;
        }
        if (legal.Struggle)
        {
            Assert.Equal(ActionType.Struggle, action.Type);
            return;
        }
        Assert.Equal(ActionType.Skill, action.Type);
        Assert.Contains(legal.Skills, skill => skill.Usable && skill.Index == action.Index);
    }

    static void LegalReplacement(Battle battle, int side, BattleAction action)
    {
        Assert.Equal(ActionType.Switch, action.Type);
        Assert.InRange(action.Index, 0, battle.Sides[side].Team.Count - 1);
        Assert.NotEqual(battle.Sides[side].Active, action.Index);
        Assert.False(battle.Sides[side].Team[action.Index].Fainted);
    }

    [Fact]
    public void BudgetAndDecisionAreReadOnlyAndPresetAccessReturnsIndependentBudgets()
    {
        Assert.True(typeof(ExpertBudget).IsSealed);
        Assert.True(typeof(ExpertDecision).IsSealed);
        Assert.Equal(128, ExpertBudget.DefaultMaxNodes);
        Assert.Equal(128, new ExpertBudget().MaxNodes);
        Assert.Equal(128, ExpertBudget.Default.MaxNodes);
        Assert.Equal(int.MaxValue, ExpertBudget.Unlimited.MaxNodes);
        Assert.Equal(0, new ExpertBudget(0).MaxNodes);
        Assert.NotSame(ExpertBudget.Default, ExpertBudget.Default);
        Assert.NotSame(ExpertBudget.Unlimited, ExpertBudget.Unlimited);
        Assert.Null(typeof(ExpertBudget).GetProperty(nameof(ExpertBudget.MaxNodes)).GetSetMethod());
        foreach (string name in new[] { "Action", "SimulatedNodes", "BudgetExhausted" })
        {
            var property = typeof(ExpertDecision).GetProperty(name);
            Assert.NotNull(property);
            Assert.Null(property.GetSetMethod());
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NegativeBudgetsAreRejected(int nodes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExpertBudget(nodes));
    }

    [Theory]
    [InlineData(0, false, false, false)]
    [InlineData(1, false, false, false)]
    [InlineData(0, true, false, false)]
    [InlineData(1, true, false, false)]
    [InlineData(0, true, true, false)]
    [InlineData(1, true, true, false)]
    [InlineData(0, false, true, true)]
    [InlineData(1, false, true, true)]
    [InlineData(0, false, false, true)]
    [InlineData(1, false, false, true)]
    public void TinyBudgetsAlwaysReturnALegalActionWithoutChangingRealState(int nodes, bool controlled, bool locked, bool exhausted)
    {
        var battle = QuietBattle(usable: exhausted ? 0 : 2, locked: locked);
        if (controlled) battle.Active(0).Statuses.Add(new StatusInst { Id = "disconnect", Left = 2 });
        string before = Snapshot(battle);
        ulong beforeHash = battle.StateHash();
        var decision = Ai.ChooseExpert(battle, 0, new ExpertBudget(nodes));
        LegalAction(battle, 0, decision.Action);
        Assert.Equal(nodes, decision.SimulatedNodes);
        Assert.True(decision.BudgetExhausted);
        Assert.Equal(before, Snapshot(battle));
        // 零节点直接回首选；一个节点至多完成首选的一个样本，两者都没有真实同分选择。
        Assert.Equal(beforeHash, battle.StateHash());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DefaultAndExplicitBudgetsAreDeterministicFromTheSameComplexPosition(int side)
    {
        var battle = new Battle(BattleTests.LoadPack("original"), Teams, new[] { "甲方", "乙方" }, 109u);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        battle.Active(0).Fields.Add(new FieldInst { Id = "hype", Turns = -1, Stacks = 2 });
        battle.Active(1).Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        battle.Active(0).Statuses.Add(new StatusInst { Id = "hater", Left = 3 });
        battle.Sides[1].Team[2].Stages["spd"] = -2;
        string before = Snapshot(battle);
        int previous = 0;
        foreach (int nodes in new[] { 0, 1, 7, 32, 128, 512, int.MaxValue })
        {
            var first = battle.Clone();
            var second = battle.Clone();
            var one = Ai.ChooseExpert(first, side, new ExpertBudget(nodes));
            var two = Ai.ChooseExpert(second, side, new ExpertBudget(nodes));
            SameDecision(one, two);
            LegalAction(battle, side, one.Action);
            Assert.InRange(one.SimulatedNodes, previous, nodes);
            Assert.Equal(before, Snapshot(first));
            Assert.Equal(before, Snapshot(second));
            Assert.Equal(first.StateHash(), second.StateHash());
            Assert.Equal(first.Random(), second.Random());
            previous = one.SimulatedNodes;
        }
        Assert.Equal(before, Snapshot(battle));
    }

    [Fact]
    public void IncreasingBudgetsConsumeMonotonicallyMoreNodesAndStopAtTheCompletedSearch()
    {
        var position = QuietBattle(usable: 2);
        int previous = -1;
        foreach (int nodes in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 128, int.MaxValue })
        {
            var battle = position.Clone();
            var decision = Ai.ChooseExpert(battle, 0, new ExpertBudget(nodes));
            Assert.InRange(decision.SimulatedNodes, previous, nodes);
            Assert.Equal(Math.Min(nodes, 12), decision.SimulatedNodes);
            Assert.Equal(nodes < 12, decision.BudgetExhausted);
            LegalAction(position, 0, decision.Action);
            previous = decision.SimulatedNodes;
        }
    }

    [Fact]
    public void DefaultReallyStopsAt128WhenTiedActionsCauseReplacementsInBothSearchLayers()
    {
        var position = QuietBattle(usable: 5, locked: false);
        foreach (var fighter in position.Sides.SelectMany(side => side.Team))
        {
            fighter.Hp = 1;
            fighter.Statuses.Add(new StatusInst { Id = "flame", Left = 3 });
        }
        Assert.Equal(5, position.LegalFor(0).Skills.Count(option => option.Usable));
        Assert.Equal(5, position.LegalFor(0).Switches.Count);
        string before = Snapshot(position);
        ulong originalHash = position.StateHash();

        // 实际结算确认两层都需要双方补位，使十个并列候选超出默认预算，而非只检查一个宽松上界。
        var layers = position.Clone();
        for (int layer = 0; layer < 2; layer++)
        {
            layers.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
            Assert.Equal(Phase.Replace, layers.Phase);
            Assert.True(layers.Need[0]);
            Assert.True(layers.Need[1]);
            for (int side = 0; side < 2; side++)
                layers.Replace(side, layers.Sides[side].Team.FindIndex(fighter => !fighter.Fainted));
            Assert.Equal(Phase.Choose, layers.Phase);
        }

        var unlimitedInput = position.Clone();
        var limitedInput = position.Clone();
        var standardInput = position.Clone();
        var unlimited = Ai.ChooseExpert(unlimitedInput, 0, ExpertBudget.Unlimited);
        var limited = Ai.ChooseExpert(limitedInput, 0, ExpertBudget.Default);
        var standard = Ai.Choose(standardInput, 0, AiLevel.Expert);
        Assert.True(unlimited.SimulatedNodes > ExpertBudget.DefaultMaxNodes);
        Assert.False(unlimited.BudgetExhausted);
        Assert.Equal(128, limited.SimulatedNodes);
        Assert.True(limited.BudgetExhausted);
        LegalAction(position, 0, unlimited.Action);
        LegalAction(position, 0, limited.Action);
        Assert.Equal(limited.Action.ToString(), standard.ToString());
        foreach (var battle in new[] { position, unlimitedInput, limitedInput, standardInput })
            Assert.Equal(before, Snapshot(battle));
        Assert.Equal(originalHash, position.StateHash());
        Assert.Equal(limitedInput.StateHash(), standardInput.StateHash());
        Assert.Equal(limitedInput.Random(), standardInput.Random());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 0)]
    [InlineData(6, 1)]
    public void CompleteCandidatesTakePriorityOverHigherPartialScores(int limit, int expectedIndex)
    {
        var battle = QuietBattle(usable: 2);
        battle.Active(0).Skills[1].Id = "t108_hit";
        string before = Snapshot(battle);
        var decision = Ai.ChooseExpert(battle, 0, new ExpertBudget(limit));
        // 预算 5 时仅等待候选完成三个样本，不能用另一个候选尚未完成的高分取代它；
        // 尚无完整候选时按已完成样本平均分回退，预算 6 则两者首层都已完整。
        Assert.Equal(ActionType.Skill, decision.Action.Type);
        Assert.Equal(expectedIndex, decision.Action.Index);
        Assert.Equal(limit, decision.SimulatedNodes);
        Assert.True(decision.BudgetExhausted);
        Assert.Equal(before, Snapshot(battle));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void IncompleteSecondLayerCannotCompeteWithTheOnlyCompletedExpansion(int limit)
    {
        var battle = QuietBattle(usable: 2);
        string before = Snapshot(battle);
        ulong hash = battle.StateHash();
        var expectedRng = battle.Clone();
        var decision = Ai.ChooseExpert(battle, 0, new ExpertBudget(limit));
        // 首层并列时，只有第一个候选已完成全部扩展，不能拿第二个的浅层分数参与抽签。
        Assert.Equal(ActionType.Skill, decision.Action.Type);
        Assert.Equal(0, decision.Action.Index);
        Assert.Equal(limit, decision.SimulatedNodes);
        Assert.True(decision.BudgetExhausted);
        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(expectedRng.Random(), battle.Random());
    }

    [Fact]
    public void UnlimitedEquivalentChoicesKeepOriginalCandidateOrderAndOneTieBreakingRandomDraw()
    {
        var selected = new HashSet<int>();
        for (uint seed = 0; seed < 12; seed++)
        {
            var battle = QuietBattle(usable: 2, seed: seed);
            var expectedRng = battle.Clone();
            int expectedIndex = (int)(expectedRng.Random() * 2);
            string before = Snapshot(battle);
            var decision = Ai.ChooseExpert(battle, 0, ExpertBudget.Unlimited);
            Assert.Equal(ActionType.Skill, decision.Action.Type);
            Assert.Equal(expectedIndex, decision.Action.Index);
            Assert.Equal(12, decision.SimulatedNodes);
            Assert.False(decision.BudgetExhausted);
            Assert.Equal(before, Snapshot(battle));
            Assert.Equal(expectedRng.StateHash(), battle.StateHash());
            Assert.Equal(expectedRng.Random(), battle.Random());
            selected.Add(decision.Action.Index);
        }
        Assert.Equal(2, selected.Count);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 1, true)]
    [InlineData(5, 5, true)]
    [InlineData(6, 6, false)]
    [InlineData(128, 6, false)]
    [InlineData(int.MaxValue, 6, false)]
    public void SingleLegalActionChargesEveryResolveTurnAndNeverConsumesLiveRandomness(int limit, int expected, bool exhausted)
    {
        var battle = QuietBattle();
        Assert.Single(battle.LegalFor(0).Skills, option => option.Usable);
        Assert.Empty(battle.LegalFor(0).Switches);
        string before = Snapshot(battle);
        ulong hash = battle.StateHash();
        var decision = Ai.ChooseExpert(battle, 0, new ExpertBudget(limit));
        Assert.Equal(BattleAction.Skill(0).ToString(), decision.Action.ToString());
        Assert.Equal(expected, decision.SimulatedNodes);
        Assert.Equal(exhausted, decision.BudgetExhausted);
        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(hash, battle.StateHash());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void ReplacementBudgetCountsOwnAndOpponentsActualReplacements(int side, bool both)
    {
        var position = ReplacementBattle(side, both);
        int complete = both ? 9 : 6;
        string before = Snapshot(position);
        ulong hash = position.StateHash();
        int previous = -1;
        foreach (int limit in new[] { 0, 1, 2, 3, 5, 6, 8, 9, 128, int.MaxValue })
        {
            var battle = position.Clone();
            var decision = Ai.ChooseExpertReplacement(battle, side, new ExpertBudget(limit));
            LegalReplacement(position, side, decision.Action);
            Assert.Equal(1, decision.Action.Index);
            Assert.Equal(Math.Min(limit, complete), decision.SimulatedNodes);
            Assert.Equal(limit < complete, decision.BudgetExhausted);
            Assert.InRange(decision.SimulatedNodes, previous, limit);
            Assert.Equal(before, Snapshot(battle));
            Assert.Equal(hash, battle.StateHash());
            previous = decision.SimulatedNodes;
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void MultipleReplacementCandidatesRemainLegalAndDeterministicWithTinyOrDefaultBudgets(int side, bool both)
    {
        var position = ReplacementBattle(side, both, sole: false);
        string before = Snapshot(position);
        foreach (int limit in new[] { 0, 1, 17, ExpertBudget.DefaultMaxNodes })
        {
            var first = position.Clone();
            var second = position.Clone();
            var one = Ai.ChooseExpertReplacement(first, side, new ExpertBudget(limit));
            var two = Ai.ChooseExpertReplacement(second, side, new ExpertBudget(limit));
            SameDecision(one, two);
            LegalReplacement(position, side, one.Action);
            Assert.InRange(one.SimulatedNodes, 0, limit);
            Assert.Equal(before, Snapshot(first));
            Assert.Equal(before, Snapshot(second));
            Assert.Equal(first.StateHash(), second.StateHash());
        }
        var direct = position.Clone();
        var legacyEntry = position.Clone();
        var decision = Ai.ChooseExpertReplacement(direct, side, ExpertBudget.Default);
        int index = Ai.ChooseReplacement(legacyEntry, side, AiLevel.Expert);
        Assert.Equal(decision.Action.Index, index);
        Assert.Equal(direct.StateHash(), legacyEntry.StateHash());
        Assert.Equal(before, Snapshot(legacyEntry));
    }

    [Fact]
    public void ATerminalFirstLayerDoesNotChargeAnyRolloutNodes()
    {
        var battle = QuietBattle();
        battle.Turn = Battle.TurnCap;
        string before = Snapshot(battle);
        var result = Ai.ChooseExpert(battle, 0, new ExpertBudget(3));
        Assert.Equal(3, result.SimulatedNodes);
        Assert.False(result.BudgetExhausted);
        Assert.Equal(BattleAction.Skill(0).ToString(), result.Action.ToString());
        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(Phase.Choose, battle.Phase);
        var unlimited = Ai.ChooseExpert(battle.Clone(), 0, ExpertBudget.Unlimited);
        SameDecision(result, unlimited);
    }

    [Fact]
    public void AReusedBudgetDoesNotShareSpentNodesBetweenCallsOrPhases()
    {
        var budget = new ExpertBudget(5);
        var original = QuietBattle(usable: 2);
        var replacement = ReplacementBattle(0, both: true);
        var first = Ai.ChooseExpert(original.Clone(), 0, budget);
        var otherPhase = Ai.ChooseExpertReplacement(replacement.Clone(), 0, budget);
        var second = Ai.ChooseExpert(original.Clone(), 0, budget);
        var third = Ai.ChooseExpertReplacement(replacement.Clone(), 0, budget);
        SameDecision(first, second);
        SameDecision(otherPhase, third);
        Assert.Equal(5, first.SimulatedNodes);
        Assert.Equal(5, otherPhase.SimulatedNodes);
        Assert.True(first.BudgetExhausted);
        Assert.True(otherPhase.BudgetExhausted);
        Assert.Equal(5, budget.MaxNodes);
    }

    [Fact]
    public void DefaultBudgetBoundsTwentyRandomPositionsAndMatchesTheExistingExpertEntryPoint()
    {
        var db = BattleTests.LoadPack("original");
        var random = new Rng(1082026);
        var seen = new HashSet<string>();
        for (int sample = 0; sample < 20; sample++)
        {
            var ids = db.Streamers.Select(streamer => streamer.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            for (int index = ids.Length - 1; index > 0; index--)
            {
                int other = (int)(random.Next() * (index + 1));
                string swap = ids[index]; ids[index] = ids[other]; ids[other] = swap;
            }
            uint seed = (uint)(random.Next() * uint.MaxValue);
            var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
                new[] { "甲方", "乙方" }, seed);
            var opening = battle.Clone();
            int turns = (int)(random.Next() * 5);
            for (int turn = 0; turn < turns && battle.Phase != Phase.Over; turn++)
            {
                battle.ResolveTurn(Ai.Choose(battle, 0, AiLevel.Normal), Ai.Choose(battle, 1, AiLevel.Normal));
                int replacements = 0;
                while (battle.Phase == Phase.Replace && replacements++ < 12)
                    for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                        if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, AiLevel.Normal));
                Assert.NotEqual(Phase.Replace, battle.Phase);
            }
            if (battle.Phase == Phase.Over) battle = opening;
            Assert.Equal(Phase.Choose, battle.Phase);
            seen.Add(battle.Record.ToJson());
            int choosing = sample % 2;
            var samePosition = battle.Clone();
            string before = Snapshot(battle);
            var decision = Ai.ChooseExpert(battle, choosing, ExpertBudget.Default);
            var standard = Ai.Choose(samePosition, choosing, AiLevel.Expert);
            Assert.InRange(decision.SimulatedNodes, 0, ExpertBudget.DefaultMaxNodes);
            LegalAction(battle, choosing, decision.Action);
            Assert.Equal(decision.Action.ToString(), standard.ToString());
            Assert.Equal(before, Snapshot(battle));
            Assert.Equal(before, Snapshot(samePosition));
            Assert.Equal(battle.StateHash(), samePosition.StateHash());
            Assert.Equal(battle.Random(), samePosition.Random());
        }
        Assert.Equal(20, seen.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void InvalidSidesAreRejectedWithoutChangingTheBattle(int side)
    {
        var battle = QuietBattle();
        string before = Snapshot(battle);
        ulong hash = battle.StateHash();
        Assert.Throws<ArgumentOutOfRangeException>(() => Ai.ChooseExpert(battle, side, ExpertBudget.Default));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ai.ChooseExpertReplacement(battle, side, ExpertBudget.Default));
        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(hash, battle.StateHash());
    }

    [Fact]
    public void NullBattleAndWrongPhaseAreRejectedWithoutSimulating()
    {
        Assert.Throws<ArgumentNullException>(() => Ai.ChooseExpert(null, 0, ExpertBudget.Default));
        Assert.Throws<ArgumentNullException>(() => Ai.ChooseExpertReplacement(null, 0, ExpertBudget.Default));
        var choose = QuietBattle();
        var replacement = ReplacementBattle(0, both: false);
        Assert.Throws<ArgumentNullException>(() => Ai.ChooseExpert(choose, 0, null));
        Assert.Throws<ArgumentNullException>(() => Ai.ChooseExpertReplacement(replacement, 0, null));
        var over = QuietBattle();
        over.Surrender(0);
        var lead = new Battle(QuietContent(), Teams, new[] { "甲方", "乙方" }, new BattleOptions { Seed = 108, LeadPick = true });
        var ban = new Battle(QuietContent(), Teams, new[] { "甲方", "乙方" }, new BattleOptions { Seed = 108, BanOne = true });
        foreach (var battle in new[] { replacement, over, lead, ban })
        {
            string before = Snapshot(battle);
            ulong hash = battle.StateHash();
            Assert.Throws<InvalidOperationException>(() => Ai.ChooseExpert(battle, 0, ExpertBudget.Default));
            Assert.Equal(before, Snapshot(battle));
            Assert.Equal(hash, battle.StateHash());
        }
        foreach (var battle in new[] { choose, over, lead, ban })
        {
            string before = Snapshot(battle);
            ulong hash = battle.StateHash();
            Assert.Throws<InvalidOperationException>(() => Ai.ChooseExpertReplacement(battle, 0, ExpertBudget.Default));
            Assert.Equal(before, Snapshot(battle));
            Assert.Equal(hash, battle.StateHash());
        }
        Assert.Throws<InvalidOperationException>(() => Ai.ChooseExpertReplacement(replacement, 1, ExpertBudget.Default));
    }
}
