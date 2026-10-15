using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using StreamDuel.Game;
using Xunit;

public class T70_ReplayNavigatorTests
{
    static Battle Completed(uint seed, bool opening = true)
    {
        var db = BattleTests.LoadPack("original_wave2");
        var ids = db.Streamers.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "回看甲", "回看乙" }, new BattleOptions
            {
                Seed = seed, BanOne = opening, LeadPick = opening,
                Modifiers = new List<string> { "stage_cap_3", "half_pp" }
            });
        for (int step = 0; step < Battle.TurnCap * 4 && battle.Phase != Phase.Over; step++)
        {
            if (battle.Phase == Phase.Ban)
            {
                for (int side = 0; side < 2 && battle.Phase == Phase.Ban; side++)
                    if (!battle.HasChosenBan(side)) battle.ChooseBan(side, Ai.ChooseBan(battle, side));
            }
            else if (battle.Phase == Phase.Lead)
            {
                for (int side = 0; side < 2 && battle.Phase == Phase.Lead; side++)
                    if (!battle.HasChosenLead(side)) battle.ChooseLead(side, Ai.ChooseLead(battle, side));
            }
            else if (battle.Phase == Phase.Replace)
            {
                for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, AiLevel.Normal));
            }
            else battle.ResolveTurn(Ai.Choose(battle, 0, AiLevel.Normal), Ai.Choose(battle, 1, AiLevel.Normal));
        }
        Assert.Equal(Phase.Over, battle.Phase);
        return battle;
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(42u)]
    public void EveryTurnAndReplacementCanBeTraversedInBothDirections(uint seed)
    {
        var battle = Completed(seed);
        ulong originalHash = battle.StateHash();
        string originalJson = battle.Record.ToJson();
        var navigation = new ReplayNavigator(battle.Db, battle.Record);
        Assert.Equal(battle.Record.Events.Count(e => e.Type == BattleRecordEventType.Turn) + 1, navigation.Count);
        Assert.Equal(0, navigation.Turns[0]);
        Assert.Equal(Phase.Choose, navigation.Current.Phase);
        var allLogs = new List<LogEntry>(navigation.FrameLog);
        var frameHashes = new List<ulong> { navigation.Current.StateHash() };
        for (int i = 1; i < navigation.Count; i++)
        {
            navigation.Next();
            Assert.Equal(i, navigation.Index);
            Assert.Equal(i, navigation.Turns[i]);
            Assert.Contains(navigation.FrameLog, entry => entry.Node == Node.TurnHeader && entry.Turn == i);
            allLogs.AddRange(navigation.FrameLog);
            frameHashes.Add(navigation.Current.StateHash());
        }
        Assert.Equal(Phase.Over, navigation.Current.Phase);
        Assert.Equal(originalHash, navigation.Current.StateHash());
        Assert.Equal(originalJson, navigation.Current.Record.ToJson());
        Assert.Equal(BattleSummary.From(battle).ToText(), BattleSummary.From(navigation.Current).ToText());
        Assert.Equal(battle.Log.Count, allLogs.Count);
        for (int i = 0; i < allLogs.Count; i++)
        {
            Assert.Equal(battle.Log[i].Text, allLogs[i].Text);
            Assert.Equal(battle.Log[i].Node, allLogs[i].Node);
            Assert.Equal(battle.Log[i].Turn, allLogs[i].Turn);
            Assert.Equal(battle.Log[i].SnapActive, allLogs[i].SnapActive);
            Assert.Equal(battle.Log[i].SnapHp[0], allLogs[i].SnapHp[0]);
            Assert.Equal(battle.Log[i].SnapHp[1], allLogs[i].SnapHp[1]);
        }
        Assert.Contains(allLogs, entry => entry.Node == Node.Replace);
        for (int i = navigation.Count - 1; i >= 0; i--)
        {
            navigation.Seek(i);
            Assert.Equal(frameHashes[i], navigation.Current.StateHash());
        }
        navigation.Previous();
        Assert.Equal(0, navigation.Index);
        navigation.Seek(navigation.Count - 1);
        navigation.Next();
        Assert.Equal(navigation.Count - 1, navigation.Index);
        Assert.Equal(originalHash, battle.StateHash());
        Assert.Equal(originalJson, battle.Record.ToJson());
    }

    [Fact]
    public void SourceAndReturnedRecordsCannotChangeTheLoadedTimeline()
    {
        var battle = Completed(17);
        var source = BattleRecord.FromJson(battle.Record.ToJson());
        var navigation = new ReplayNavigator(battle.Db, source);
        source.Events.Clear();
        source.Teams[0][0] = "not-a-streamer";
        var exposed = navigation.Record;
        exposed.Events.Clear();
        exposed.ContentHash = "modified";
        navigation.Seek(navigation.Count - 1);
        Assert.Equal(battle.StateHash(), navigation.Current.StateHash());
        navigation.Current.Active(0).Hp = 0;
        navigation.Seek(navigation.Count - 1);
        Assert.Equal(battle.StateHash(), navigation.Current.StateHash());
    }

    [Fact]
    public void InvalidSeeksPreserveTheCurrentFrame()
    {
        var battle = Completed(5);
        var navigation = new ReplayNavigator(battle.Db, battle.Record);
        navigation.Next();
        ulong hash = navigation.Current.StateHash();
        Assert.Throws<ArgumentOutOfRangeException>(() => navigation.Seek(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => navigation.Seek(navigation.Count));
        Assert.Equal(1, navigation.Index);
        Assert.Equal(hash, navigation.Current.StateHash());
    }

    [Fact]
    public void EmptyAndPartialRecordsRemainInspectable()
    {
        var battle = Completed(6);
        var record = BattleRecord.FromJson(battle.Record.ToJson());
        record.Events.Clear();
        var empty = new ReplayNavigator(battle.Db, record);
        Assert.Equal(1, empty.Count);
        Assert.Equal(Phase.Ban, empty.Current.Phase);
        empty.Next();
        empty.Previous();
        Assert.Equal(0, empty.Index);

        var partial = BattleRecord.FromJson(battle.Record.ToJson());
        int secondTurn = partial.Events.FindIndex(partial.Events.FindIndex(e => e.Type == BattleRecordEventType.Turn) + 1,
            e => e.Type == BattleRecordEventType.Turn);
        partial.Events.RemoveRange(secondTurn, partial.Events.Count - secondTurn);
        var navigation = new ReplayNavigator(battle.Db, partial);
        navigation.Next();
        Assert.Equal(2, navigation.Count);
        Assert.Equal(Replay.Run(battle.Db, partial).StateHash(), navigation.Current.StateHash());
        Assert.NotEqual(Phase.Over, navigation.Current.Phase);
    }

    [Fact]
    public void CoreRejectsMismatchedContentBeforeAnyFrameIsExposed()
    {
        var battle = Completed(8, false);
        var record = BattleRecord.FromJson(battle.Record.ToJson());
        record.ContentHash = "different-content";
        Assert.Throws<InvalidOperationException>(() => new ReplayNavigator(battle.Db, record));
        record.ContentHash = null;
        record.PackId = "missing-pack";
        Assert.Throws<InvalidOperationException>(() => new ReplayNavigator(battle.Db, record));
        Assert.Throws<ArgumentNullException>(() => new ReplayNavigator(null, battle.Record));
        Assert.Throws<ArgumentNullException>(() => new ReplayNavigator(battle.Db, null));
    }
}
