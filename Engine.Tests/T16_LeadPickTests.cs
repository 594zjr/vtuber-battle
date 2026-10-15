using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T16_LeadPickTests
{
    static IList<string>[] Teams()
    {
        return new IList<string>[]
        {
            new List<string> { "enka", "shiori", "lumi", "bell", "hane", "momo" },
            new List<string> { "yuan", "nyako", "buck", "kasumi", "kei", "bee" }
        };
    }

    static Battle Make(ContentDb db = null, IList<string>[] teams = null, uint seed = 42)
    {
        return new Battle(db ?? BattleTests.LoadPack("original"), teams ?? Teams(), new[] { "甲方", "乙方" }, seed, leadPick: true);
    }

    static string[] Ids(Battle battle, int side)
    {
        return battle.Sides[side].Team.Select(f => f.Def.Id).ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BothSelectionsMoveLeadsToFrontWithoutChangingOtherOrderOrInput(int firstSide)
    {
        var teams = Teams();
        var inputs = teams.Select(t => t.ToArray()).ToArray();
        var battle = Make(teams: teams);
        var fighters = battle.Sides.Select(s => s.Team.ToArray()).ToArray();
        var choices = new[] { 3, 5 };

        Assert.Equal(Phase.Lead, battle.Phase);
        Assert.False(battle.HasChosenLead(0));
        Assert.False(battle.HasChosenLead(1));
        battle.ChooseLead(firstSide, choices[firstSide]);

        Assert.Equal(Phase.Lead, battle.Phase);
        Assert.True(battle.HasChosenLead(firstSide));
        Assert.False(battle.HasChosenLead(1 - firstSide));
        Assert.Equal(inputs[0], Ids(battle, 0));
        Assert.Equal(inputs[1], Ids(battle, 1));
        Assert.DoesNotContain(battle.Log, entry => entry.Kind == "enter");

        battle.ChooseLead(1 - firstSide, choices[1 - firstSide]);

        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.Equal(1, battle.Turn);
        Assert.True(battle.HasChosenLead(0));
        Assert.True(battle.HasChosenLead(1));
        Assert.Equal(new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" }, Ids(battle, 0));
        Assert.Equal(new[] { "bee", "yuan", "nyako", "buck", "kasumi", "kei" }, Ids(battle, 1));
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(inputs[side], teams[side]);
            Assert.Equal(0, battle.Sides[side].Active);
            Assert.Same(fighters[side][choices[side]], battle.Active(side));
            for (int index = 0; index < battle.Sides[side].Team.Count; index++)
            {
                var fighter = battle.Sides[side].Team[index];
                Assert.Contains(fighter, fighters[side]);
                Assert.Equal(index + 1, fighter.Slot);
                Assert.Equal(side, fighter.Side);
            }
            Assert.Contains(battle.Log, entry => entry.Node == Node.Start && entry.Side == side &&
                entry.Text.Contains("首发") && entry.Text.Contains(battle.Active(side).Name));
        }
        Assert.Equal(2, battle.Log.Count(entry => entry.Kind == "enter"));
        Assert.All(battle.Log, entry => Assert.Equal(Node.Start, entry.Node));
    }

    [Fact]
    public void OpeningPersonaAndRandomnessWaitUntilBothSidesConfirm()
    {
        var teams = Teams();
        teams[0][0] = "kasumi";
        var battle = Make(teams: teams, seed: 73);
        var random = new Rng(73);
        var unselectedOpeningFighter = battle.Sides[0].Team[0];
        var selectedOpeningFighter = battle.Sides[1].Team[3];

        Assert.Empty(battle.Log);
        Assert.All(battle.Sides.SelectMany(s => s.Team), fighter => Assert.Empty(fighter.Fields));
        Assert.Equal(random.Next(), battle.Random());
        battle.ChooseLead(1, 3);
        Assert.DoesNotContain(battle.Log, entry => entry.Kind == "enter" || entry.Kind == "trait");
        Assert.Empty(selectedOpeningFighter.Fields);
        Assert.Equal(random.Next(), battle.Random());

        battle.ChooseLead(0, 1);

        Assert.Empty(unselectedOpeningFighter.Fields);
        Assert.Same(selectedOpeningFighter, battle.Active(1));
        Assert.Equal(2, selectedOpeningFighter.FieldOf("filter").Turns);
        Assert.Single(battle.Log, entry => entry.Kind == "trait" && entry.Side == 1);
        Assert.DoesNotContain(battle.Log, entry => entry.Kind == "enter" && entry.Side == 0 &&
            entry.Text.Contains(unselectedOpeningFighter.Name));
    }

    [Fact]
    public void TeamSpiritUsesTheNewSixthSlotAfterItsOriginalSixthSlotBecomesLead()
    {
        var teams = Teams();
        teams[0][5] = "nyako";
        var battle = Make(teams: teams);
        var selected = battle.Sides[0].Team[5];
        var newSixth = battle.Sides[0].Team[4];

        battle.ChooseLead(0, 5);
        battle.ChooseLead(1, 4);

        Assert.Same(selected, battle.Active(0));
        Assert.Equal(1, selected.Slot);
        Assert.Same(newSixth, battle.Sides[0].Team[5]);
        Assert.Equal(6, newSixth.Slot);
        Assert.True(battle.LegalFor(0).FreePp);
        int skillIndex = selected.Skills.FindIndex(skill => skill.Id == "cat_walk");
        int originalPp = selected.Skills[skillIndex].Pp;
        battle.ResolveTurn(BattleAction.Skill(skillIndex), BattleAction.Pass());
        Assert.Equal(originalPp, selected.Skills[skillIndex].Pp);

        newSixth.Fainted = true;
        newSixth.Hp = 0;
        Assert.False(battle.LegalFor(0).FreePp);
        battle.ResolveTurn(BattleAction.Skill(skillIndex), BattleAction.Pass());
        Assert.Equal(originalPp - 1, selected.Skills[skillIndex].Pp);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 6)]
    [InlineData(1, 6)]
    public void InvalidLeadSelectionDoesNotChangePendingSelections(int side, int index)
    {
        var battle = Make();

        Assert.Throws<ArgumentOutOfRangeException>(() => battle.ChooseLead(side, index));

        Assert.Equal(Phase.Lead, battle.Phase);
        Assert.False(battle.HasChosenLead(0));
        Assert.False(battle.HasChosenLead(1));
        Assert.Empty(battle.Log);
        if (side < 0 || side > 1)
            Assert.Throws<ArgumentOutOfRangeException>(() => battle.HasChosenLead(side));
    }

    [Fact]
    public void LeadPhaseRejectsTurnsReplacementAndRepeatedOrLateSelections()
    {
        var battle = Make();
        Assert.Throws<InvalidOperationException>(() => battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass()));
        Assert.Throws<InvalidOperationException>(() => battle.Replace(0, 1));
        battle.ChooseLead(0, 2);

        Assert.Throws<InvalidOperationException>(() => battle.ChooseLead(0, 3));
        Assert.Throws<InvalidOperationException>(() => battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass()));
        Assert.Throws<InvalidOperationException>(() => battle.Replace(1, 1));
        Assert.Equal(1, battle.Turn);
        Assert.Equal(Phase.Lead, battle.Phase);
        Assert.False(battle.HasChosenLead(1));
        Assert.Empty(battle.Log);

        battle.ChooseLead(1, 5);
        Assert.Equal("lumi", battle.Active(0).Def.Id);
        Assert.Throws<InvalidOperationException>(() => battle.ChooseLead(1, 0));
        Assert.Throws<InvalidOperationException>(() => Ai.ChooseLead(battle, 0));
    }

    [Theory]
    [InlineData(7u)]
    [InlineData(42u)]
    public void ExplicitFalseAndLegacyConstructorProduceIdenticalCompleteBattles(uint seed)
    {
        var db = BattleTests.LoadPack("original");
        var teams = Teams();
        teams[0][0] = "kasumi";
        var legacy = new Battle(db, teams, new[] { "甲方", "乙方" }, seed);
        var explicitFalse = new Battle(db, teams, new[] { "甲方", "乙方" }, seed, leadPick: false);

        Assert.Equal(Phase.Choose, legacy.Phase);
        Assert.Equal(Phase.Choose, explicitFalse.Phase);
        Assert.True(legacy.Active(0).HasField("filter"));
        Finish(legacy);
        Finish(explicitFalse);

        Assert.Equal(legacy.Winner, explicitFalse.Winner);
        Assert.Equal(legacy.Turn, explicitFalse.Turn);
        Assert.Equal(FullLog(legacy), FullLog(explicitFalse));
        Assert.Equal(legacy.Random(), explicitFalse.Random());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AiUsesTheWholeEnemyRosterAndDoesNotSpendRandomnessOnAUniqueBest(int side)
    {
        var teams = Teams();
        if (side == 1) teams = new[] { teams[1], teams[0] };
        var battle = Make(teams: teams, seed: 19);
        var control = Make(teams: teams, seed: 19);
        var original = battle.Sides.Select((s, index) => Ids(battle, index)).ToArray();

        // 阿渊单独对位偏向铃铛，但全队平均对位以炎华唯一最优，避免只看首位也通过。
        Assert.Equal(0, Ai.ChooseLead(battle, side));

        Assert.Equal(control.Random(), battle.Random());
        Assert.Equal(Phase.Lead, battle.Phase);
        Assert.False(battle.HasChosenLead(side));
        Assert.Empty(battle.Log);
        for (int s = 0; s < 2; s++)
        {
            Assert.Equal(original[s], Ids(battle, s));
            Assert.Equal(0, battle.Sides[s].Active);
        }
    }

    [Fact]
    public void AiTieBreaksAreSeededAndRepeatable()
    {
        var db = BattleTests.LoadPack("original");
        foreach (var streamer in db.Streamers)
        {
            streamer.Type = "game";
            streamer.Stats["spd"] = 300;
            streamer.Skills = new List<string> { "aim_burst", "aim_burst", "aim_burst", "aim_burst" };
            streamer.Fifth = "aim_burst";
        }
        var choices = new HashSet<int>();
        for (uint seed = 1; seed <= 16; seed++)
        {
            var first = Make(db, seed: seed);
            var second = Make(db, seed: seed);

            int selected = Ai.ChooseLead(first, 0);

            Assert.InRange(selected, 0, 5);
            Assert.Equal(selected, Ai.ChooseLead(second, 0));
            Assert.Equal(first.Random(), second.Random());
            Assert.Equal(Phase.Lead, first.Phase);
            Assert.False(first.HasChosenLead(0));
            choices.Add(selected);
        }
        Assert.True(choices.Count > 1, "完全同分的首发应由对战种子打破平局");
    }

    static void Finish(Battle battle)
    {
        int remaining = 500;
        while (battle.Phase != Phase.Over && remaining-- > 0)
        {
            if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
            else
                for (int side = 0; side < 2; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
        }
        Assert.Equal(Phase.Over, battle.Phase);
    }

    static string[] FullLog(Battle battle)
    {
        return battle.Log.Select(entry => string.Join("|", entry.Turn, entry.Node, entry.Kind, entry.Side, entry.Text,
            string.Join(",", entry.SnapActive), string.Join(",", entry.SnapHp[0]), string.Join(",", entry.SnapHp[1]))).ToArray();
    }
}
