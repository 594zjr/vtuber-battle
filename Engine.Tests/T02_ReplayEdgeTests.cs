using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T02_ReplayEdgeTests
{
    static IList<string>[] Teams(ContentDb db)
    {
        var ids = db.Streamers.Select(s => s.Id).ToList();
        return new IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() };
    }

    static Battle Make(ContentDb db, uint seed = 99)
    {
        return new Battle(db, Teams(db), new[] { "你", "对手" }, seed);
    }

    static void AssertLogsEqual(Battle expected, Battle actual)
    {
        Assert.Equal(expected.Log.Count, actual.Log.Count);
        for (int i = 0; i < expected.Log.Count; i++)
        {
            var left = expected.Log[i];
            var right = actual.Log[i];
            Assert.Equal(left.Turn, right.Turn);
            Assert.Equal(left.Side, right.Side);
            Assert.Equal(left.Node, right.Node);
            Assert.Equal(left.Text, right.Text);
            Assert.Equal(left.Kind, right.Kind);
            Assert.Equal(left.SnapActive, right.SnapActive);
            Assert.Equal(left.SnapHp[0], right.SnapHp[0]);
            Assert.Equal(left.SnapHp[1], right.SnapHp[1]);
        }
        Assert.Equal(expected.Winner, actual.Winner);
        Assert.Equal(expected.Phase, actual.Phase);
        Assert.Equal(expected.Turn, actual.Turn);
    }

    [Fact]
    public void RecordPreservesActionsBeforeNormalization()
    {
        var db = BattleTests.LoadPack("original");
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Skill(-1), BattleAction.SwitchTo(99));

        var record = BattleRecord.FromJson(battle.Record.ToJson());
        var turn = Assert.Single(record.Events);
        Assert.Equal(BattleRecordEventType.Turn, turn.Type);
        Assert.Equal(ActionType.Skill, turn.A0.Type);
        Assert.Equal(-1, turn.A0.Index);
        Assert.Equal(ActionType.Switch, turn.A1.Type);
        Assert.Equal(99, turn.A1.Index);
        AssertLogsEqual(battle, Replay.Run(db, record));
    }

    [Theory]
    [InlineData(99u)]
    [InlineData(uint.MaxValue)]
    public void ReplayPreservesSnapshotsAndRandomSequenceAfterAiAndExternalDraws(uint seed)
    {
        var db = BattleTests.LoadPack("original");
        var battle = Make(db, seed);
        int guard = 0;
        while (battle.Phase != Phase.Over && guard++ < 500)
        {
            for (int i = 0; i <= guard % 3; i++) battle.Random();
            if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
            else
            {
                for (int side = 1; side >= 0; side--)
                {
                    if (!battle.Need[side]) continue;
                    battle.Random();
                    battle.Replace(side, Ai.ChooseReplacement(battle, side));
                }
            }
        }
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Contains(battle.Record.Events, e => e.Type == BattleRecordEventType.Replace);

        string json = battle.Record.ToJson();
        var replayed = Replay.Run(db, BattleRecord.FromJson(json));
        AssertLogsEqual(battle, replayed);
        Assert.Equal(json, replayed.Record.ToJson());
        for (int i = 0; i < 8; i++) Assert.Equal(battle.Random(), replayed.Random());
    }

    [Fact]
    public void JsonRoundTripEscapesControlCharactersAndPreservesUnsignedValues()
    {
        string text = "中文\"\\/\n\r\t\b\f\0\u0001";
        var record = new BattleRecord
        {
            Seed = uint.MaxValue,
            PackId = text,
            Teams = new IList<string>[] { new List<string> { text }, new List<string> { "另一队" } },
        };
        record.Events.Add(new BattleRecordEvent
        {
            Type = BattleRecordEventType.Turn,
            A0 = BattleAction.Skill(int.MinValue),
            A1 = BattleAction.SwitchTo(int.MaxValue),
            RngState = uint.MaxValue,
        });

        string json = record.ToJson();
        Assert.DoesNotContain(json.ToCharArray(), c => c < ' ');
        var back = BattleRecord.FromJson(json);
        Assert.Equal(uint.MaxValue, back.Seed);
        Assert.Equal(text, back.PackId);
        Assert.Equal(text, back.Teams[0][0]);
        Assert.Equal("另一队", back.Teams[1][0]);
        var turn = Assert.Single(back.Events);
        Assert.Equal(uint.MaxValue, turn.RngState);
        Assert.Equal(int.MinValue, turn.A0.Index);
        Assert.Equal(int.MaxValue, turn.A1.Index);
    }

    [Fact]
    public void BattleCopiesCallerTeamListsIntoRecord()
    {
        var db = BattleTests.LoadPack("original");
        var teams = Teams(db);
        var expected = teams.Select(t => t.ToArray()).ToArray();
        var battle = new Battle(db, teams, new[] { "你", "对手" }, 7);

        teams[0].Clear();
        teams[1][0] = "changed";
        teams[1] = new List<string>();

        Assert.Equal(expected[0], battle.Record.Teams[0]);
        Assert.Equal(expected[1], battle.Record.Teams[1]);
    }

    [Fact]
    public void FailedReplacementDoesNotAppendEvent()
    {
        var db = BattleTests.LoadPack("original");
        var battle = Make(db);
        Assert.Throws<InvalidOperationException>(() => battle.Replace(0, 1));
        Assert.Empty(battle.Record.Events);

        int guard = 0;
        while (battle.Phase == Phase.Choose && guard++ < Battle.TurnCap)
            battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        Assert.Equal(Phase.Replace, battle.Phase);
        int side = Array.FindIndex(battle.Need, needed => needed);
        int count = battle.Record.Events.Count;
        Assert.Throws<InvalidOperationException>(() => battle.Replace(side, battle.Sides[side].Active));
        Assert.Throws<InvalidOperationException>(() => battle.Replace(side, -1));
        Assert.Throws<InvalidOperationException>(() => battle.Replace(side, battle.Sides[side].Team.Count));
        Assert.Equal(count, battle.Record.Events.Count);

        int to = battle.LegalFor(side).Switches.First();
        battle.Replace(side, to);
        Assert.Equal(count + 1, battle.Record.Events.Count);
        var replacement = battle.Record.Events.Last();
        Assert.Equal(BattleRecordEventType.Replace, replacement.Type);
        Assert.Equal(side, replacement.Side);
        Assert.Equal(to, replacement.To);
    }

    [Fact]
    public void ReplayAcceptsActionOnlyEventsWithoutRandomState()
    {
        var db = BattleTests.LoadPack("original");
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        var record = BattleRecord.FromJson(battle.Record.ToJson());
        foreach (var entry in record.Events) entry.RngState = null;

        var back = BattleRecord.FromJson(record.ToJson());
        Assert.All(back.Events, entry => Assert.Null(entry.RngState));
        AssertLogsEqual(battle, Replay.Run(db, back));
    }
}
