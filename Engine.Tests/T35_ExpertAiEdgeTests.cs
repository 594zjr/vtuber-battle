using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T35_ExpertAiEdgeTests
{
    static Battle Make(uint seed, string lead = "bell")
    {
        var db = BattleTests.LoadPack("original");
        var others = db.Streamers.Select(s => s.Id).Where(id => id != lead).ToArray();
        return new Battle(db, new IList<string>[]
        {
            new[] { lead }.Concat(others.Take(5)).ToArray(),
            others.Skip(5).Take(6).ToArray()
        }, new[] { "甲方", "乙方" }, seed);
    }

    static string Snapshot(Battle battle)
    {
        // 搜索可推进平局随机数，但不能改动公开状态、定义、历史记录或累计统计。
        return JsonSerializer.Serialize(battle, new JsonSerializerOptions { IncludeFields = true })
            + "\n" + BattleSummary.From(battle).ToText();
    }

    static void AssertLegal(Battle battle, int side, BattleAction action)
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
        Assert.Contains(legal.Skills, skill => skill.Index == action.Index && skill.Usable);
    }

    [Theory]
    [InlineData(1u, 0)]
    [InlineData(9u, 1)]
    [InlineData(2026u, 0)]
    [InlineData(4294967295u, 1)]
    public void SearchPreservesRealStateAndIsDeterministicFromTheSamePosition(uint seed, int side)
    {
        var battle = Make(seed);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        battle.Active(0).Fields.Add(new FieldInst { Id = "hype", Turns = -1, Stacks = 2 });
        battle.Active(1).Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        battle.Active(0).Statuses.Add(new StatusInst { Id = "hater", Left = 3 });
        battle.Sides[1].Team[2].Stages["spd"] = -2;
        var samePosition = battle.Clone();
        string before = Snapshot(battle);

        var first = Ai.Choose(battle, side, AiLevel.Expert);
        var second = Ai.Choose(samePosition, side, AiLevel.Expert);

        AssertLegal(battle, side, first);
        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(before, Snapshot(samePosition));
        Assert.Equal(first.ToString(), second.ToString());
        Assert.Equal(battle.StateHash(), samePosition.StateHash());
    }

    [Fact]
    public void SingleLegalSkillDoesNotConsumeRealRandomness()
    {
        var battle = Make(35);
        battle.Active(0).Fields.Add(new FieldInst { Id = "lockin", Turns = 3 });
        foreach (var skill in battle.Active(0).Skills) skill.Pp = 0;
        battle.Active(0).Skills[0].Pp = 1;
        Assert.Single(battle.LegalFor(0).Skills, skill => skill.Usable);
        Assert.Empty(battle.LegalFor(0).Switches);
        ulong beforeHash = battle.StateHash();
        string before = Snapshot(battle);

        var action = Ai.Choose(battle, 0, AiLevel.Expert);

        Assert.Equal(BattleAction.Skill(0).ToString(), action.ToString());
        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(beforeHash, battle.StateHash());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void ActionsRespectControlSwitchLocksAndExhaustedPp(bool controlled, bool locked, bool exhausted)
    {
        var battle = Make(82);
        if (controlled) battle.Active(0).Statuses.Add(new StatusInst { Id = "disconnect", Left = 2 });
        if (locked) battle.Active(0).Fields.Add(new FieldInst { Id = "lockin", Turns = 3 });
        if (exhausted) foreach (var skill in battle.Active(0).Skills) skill.Pp = 0;
        string before = Snapshot(battle);
        ulong beforeHash = battle.StateHash();

        var action = Ai.Choose(battle, 0, AiLevel.Expert);

        AssertLegal(battle, 0, action);
        Assert.Equal(before, Snapshot(battle));
        if (locked && (controlled || exhausted)) Assert.Equal(beforeHash, battle.StateHash());
    }

    [Fact]
    public void FreePpPersonaCanChooseAnExhaustedSkillInsteadOfStruggle()
    {
        var battle = Make(18, "nyako");
        foreach (var skill in battle.Active(0).Skills) skill.Pp = 0;
        battle.Active(0).Fields.Add(new FieldInst { Id = "lockin", Turns = 3 });
        var legal = battle.LegalFor(0);
        Assert.True(legal.FreePp);
        Assert.False(legal.Struggle);
        string before = Snapshot(battle);

        var action = Ai.Choose(battle, 0, AiLevel.Expert);

        Assert.Equal(ActionType.Skill, action.Type);
        AssertLegal(battle, 0, action);
        Assert.Equal(before, Snapshot(battle));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ReplacementSearchDoesNotApplyEitherSidesPendingReplacement(int side)
    {
        var battle = Make(44);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        for (int s = 0; s < 2; s++)
        {
            battle.Active(s).Hp = 0;
            battle.Active(s).Fainted = true;
            battle.Need[s] = true;
            battle.Sides[s].Team[2].Hp = 0;
            battle.Sides[s].Team[2].Fainted = true;
        }
        battle.Phase = Phase.Replace;
        var samePosition = battle.Clone();
        string before = Snapshot(battle);
        var candidates = Enumerable.Range(0, battle.Sides[side].Team.Count)
            .Where(index => index != battle.Sides[side].Active && !battle.Sides[side].Team[index].Fainted).ToArray();

        int first = Ai.ChooseReplacement(battle, side, AiLevel.Expert);
        int second = Ai.ChooseReplacement(samePosition, side, AiLevel.Expert);

        Assert.Contains(first, candidates);
        Assert.Equal(first, second);
        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(before, Snapshot(samePosition));
        Assert.Equal(battle.StateHash(), samePosition.StateHash());
    }

    [Fact]
    public void SoleReplacementDoesNotConsumeRealRandomness()
    {
        var battle = Make(93);
        for (int i = 0; i < battle.Sides[0].Team.Count; i++)
        {
            if (i == 3) continue;
            battle.Sides[0].Team[i].Hp = 0;
            battle.Sides[0].Team[i].Fainted = true;
        }
        battle.Need[0] = true;
        battle.Phase = Phase.Replace;
        string before = Snapshot(battle);
        ulong beforeHash = battle.StateHash();

        int replacement = Ai.ChooseReplacement(battle, 0, AiLevel.Expert);

        Assert.Equal(3, replacement);
        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(beforeHash, battle.StateHash());
    }

    [Fact]
    public void EquivalentSkillsBreakTiesReproduciblyAcrossSeeds()
    {
        var selected = new HashSet<int>();
        for (uint seed = 0; seed < 12; seed++)
        {
            var battle = Make(seed);
            battle.Active(0).Fields.Add(new FieldInst { Id = "lockin", Turns = 3 });
            for (int i = 0; i < battle.Active(0).Skills.Count; i++)
            {
                var slot = battle.Active(0).Skills[i];
                slot.Id = "warm_up";
                slot.Max = 1;
                slot.Pp = i < 2 ? 1 : 0;
                slot.Fifth = false;
            }
            var samePosition = battle.Clone();
            string before = Snapshot(battle);

            var first = Ai.Choose(battle, 0, AiLevel.Expert);
            var second = Ai.Choose(samePosition, 0, AiLevel.Expert);

            Assert.Equal(ActionType.Skill, first.Type);
            Assert.Contains(first.Index, new[] { 0, 1 });
            Assert.Equal(first.ToString(), second.ToString());
            Assert.Equal(before, Snapshot(battle));
            Assert.Equal(battle.StateHash(), samePosition.StateHash());
            selected.Add(first.Index);
        }
        Assert.Equal(2, selected.Count);
    }
}
