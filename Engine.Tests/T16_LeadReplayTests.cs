using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T16_LeadReplayTests
{
    static Battle Make(ContentDb db, bool leadPick = true)
    {
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        return new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "你", "对手" }, 73, leadPick);
    }

    static void AssertSame(Battle expected, Battle actual)
    {
        Assert.Equal(expected.Phase, actual.Phase);
        Assert.Equal(expected.Turn, actual.Turn);
        Assert.Equal(expected.Winner, actual.Winner);
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(expected.HasChosenLead(side), actual.HasChosenLead(side));
            Assert.Equal(expected.Sides[side].Team.Select(f => f.Def.Id), actual.Sides[side].Team.Select(f => f.Def.Id));
            Assert.Equal(expected.Sides[side].Team.Select(f => f.Slot), actual.Sides[side].Team.Select(f => f.Slot));
        }
        Assert.Equal(expected.Log.Count, actual.Log.Count);
        for (int i = 0; i < expected.Log.Count; i++)
        {
            var left = expected.Log[i];
            var right = actual.Log[i];
            Assert.Equal(left.Turn, right.Turn);
            Assert.Equal(left.Node, right.Node);
            Assert.Equal(left.Kind, right.Kind);
            Assert.Equal(left.Side, right.Side);
            Assert.Equal(left.Text, right.Text);
            Assert.Equal(left.SnapActive, right.SnapActive);
            Assert.Equal(left.SnapHp[0], right.SnapHp[0]);
            Assert.Equal(left.SnapHp[1], right.SnapHp[1]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void LeadSelectionsAndCompleteBattleReplayWithOriginalRosterAndRandomStream(int firstSide)
    {
        var db = BattleTests.LoadPack("original");
        var battle = Make(db);
        var originalTeams = battle.Record.Teams.Select(team => team.ToArray()).ToArray();
        battle.Random();
        battle.ChooseLead(firstSide, 3);
        battle.Random();
        battle.ChooseLead(1 - firstSide, Ai.ChooseLead(battle, 1 - firstSide));
        int guard = 500;
        while (battle.Phase != Phase.Over && guard-- > 0)
        {
            if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
            else
                for (int side = 0; side < 2; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
        }
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(originalTeams[0], battle.Record.Teams[0]);
        Assert.Equal(originalTeams[1], battle.Record.Teams[1]);
        string json = battle.Record.ToJson();
        var replayed = Replay.Run(db, BattleRecord.FromJson(json));
        AssertSame(battle, replayed);
        Assert.Equal(json, replayed.Record.ToJson());
        for (int i = 0; i < 8; i++) Assert.Equal(battle.Random(), replayed.Random());
    }

    [Fact]
    public void EmptyAndPartiallySelectedLeadRecordsRemainInLeadPhase()
    {
        var db = BattleTests.LoadPack("original");
        var battle = Make(db);
        AssertSame(battle, Replay.Run(db, BattleRecord.FromJson(battle.Record.ToJson())));
        battle.Random();
        battle.ChooseLead(1, 4);
        var replayed = Replay.Run(db, BattleRecord.FromJson(battle.Record.ToJson()));
        AssertSame(battle, replayed);
        Assert.Empty(replayed.Log);
        Assert.Equal(battle.Random(), replayed.Random());
        battle.ChooseLead(0, 3);
        replayed.ChooseLead(0, 3);
        AssertSame(battle, replayed);
    }

    [Fact]
    public void InvalidOrRepeatedLeadSelectionDoesNotAppendEvents()
    {
        var battle = Make(BattleTests.LoadPack("original"));
        Assert.Throws<ArgumentOutOfRangeException>(() => battle.ChooseLead(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => battle.ChooseLead(0, 6));
        Assert.Empty(battle.Record.Events);
        battle.ChooseLead(0, 3);
        Assert.Single(battle.Record.Events);
        Assert.Throws<InvalidOperationException>(() => battle.ChooseLead(0, 4));
        Assert.Single(battle.Record.Events);
        battle.ChooseLead(1, 2);
        Assert.Equal(2, battle.Record.Events.Count);
        Assert.Throws<InvalidOperationException>(() => battle.ChooseLead(1, 0));
        Assert.Equal(2, battle.Record.Events.Count);
    }

    [Fact]
    public void LegacyRecordWithoutLeadPickStillStartsImmediately()
    {
        var db = BattleTests.LoadPack("original");
        var battle = Make(db, false);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        string json = battle.Record.ToJson();
        Assert.DoesNotContain("leadPick", json);
        AssertSame(battle, Replay.Run(db, BattleRecord.FromJson(json)));
    }
}
