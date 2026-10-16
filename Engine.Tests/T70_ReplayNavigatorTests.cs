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

    [Theory]
    [InlineData(0u, true)]
    [InlineData(42u, false)]
    public void FrameEventRangesPartitionTheActualBattleAcrossNavigation(uint seed, bool opening)
    {
        AssertFrameEventRanges(Completed(seed, opening));
    }

    [Fact]
    public void BothReplacementEntriesBelongToTheTurnThatCausedThem()
    {
        var battle = CompletedWithBothSidesReplacing();
        Assert.Equal(10, battle.Record.Events.Count(e => e.Type == BattleRecordEventType.Replace));
        AssertFrameEventRanges(battle);
        var navigation = new ReplayNavigator(battle.Db, battle.Record);
        for (int frame = 1; frame < navigation.Count - 1; frame++)
        {
            navigation.Seek(frame);
            var replacementIndices = Enumerable.Range(navigation.FrameEventStart,
                    navigation.Current.Events.Count - navigation.FrameEventStart)
                .Where(i => navigation.Current.Events[i].Kind == BattleEventKind.Enter
                    && navigation.Current.Events[i].Node == Node.Replace).ToArray();
            Assert.Equal(2, replacementIndices.Length);
            Assert.Equal(new[] { 0, 1 }, replacementIndices.Select(i => navigation.Current.Events[i].Side));
            Assert.All(replacementIndices, i =>
                Assert.Equal(navigation.Turns[frame] + 1, navigation.Current.Events[i].Turn));
            var cues = BattleTimeline.Build(navigation.Current.Events, navigation.FrameEventStart,
                navigation.Current.Events.Count, 1);
            Assert.Equal(replacementIndices, cues.Where(c => c.Kind == CueKind.Enter).Select(c => c.EventIndex));
        }
    }

    [Fact]
    public void EmptyOpeningAndUnfinishedReplacementHaveExactEventRanges()
    {
        var battle = CompletedWithBothSidesReplacing();
        int firstTurn = battle.Record.Events.FindIndex(e => e.Type == BattleRecordEventType.Turn);
        foreach (int count in new[] { 0, firstTurn, firstTurn + 1 })
        {
            var record = BattleRecord.FromJson(battle.Record.ToJson());
            record.Events.RemoveRange(count, record.Events.Count - count);
            var partial = Replay.Run(battle.Db, record);
            AssertFrameEventRanges(partial);
            var navigation = new ReplayNavigator(battle.Db, record);
            navigation.Seek(navigation.Count - 1);
            var cues = BattleTimeline.Build(navigation.Current.Events, navigation.FrameEventStart,
                navigation.Current.Events.Count, 0);
            if (count == 0)
            {
                Assert.Equal(1, navigation.Count);
                Assert.Equal(Phase.Lead, navigation.Current.Phase);
                Assert.Equal(0, navigation.FrameEventStart);
                Assert.Empty(navigation.Current.Events);
                Assert.Empty(cues);
            }
            else if (count == firstTurn)
            {
                Assert.Equal(1, navigation.Count);
                Assert.Equal(Phase.Choose, navigation.Current.Phase);
                Assert.Equal(0, navigation.FrameEventStart);
                Assert.Equal(2, cues.Count(c => c.Kind == CueKind.Enter));
            }
            else
            {
                Assert.Equal(2, navigation.Count);
                Assert.Equal(Phase.Replace, navigation.Current.Phase);
                Assert.Equal(2, cues.Count(c => c.Kind == CueKind.Faint));
                Assert.DoesNotContain(cues, c => c.Kind == CueKind.Enter);
                Assert.DoesNotContain(navigation.Current.Events, e => e.Node == Node.Replace);
            }
        }
    }

    static Battle CompletedWithBothSidesReplacing()
    {
        var db = BattleTests.LoadPack("original");
        foreach (var streamer in db.Streamers)
        {
            streamer.Persona = "pure";
            streamer.Skills = new List<string> { "full_volume", "pregame", "deep_breath", "quick_mic" };
            streamer.Fifth = "high_note";
        }
        var skill = db.Skills["full_volume"];
        skill.Cat = "status";
        skill.Power = 0;
        skill.Pp = 40;
        skill.Acc = 100;
        skill.Effects = new List<EffectDef>
        {
            new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 10000 },
            new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "self", N = 10000 }
        };
        db.Validate();
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "补位甲", "补位乙" }, 7u, leadPick: true);
        battle.ChooseLead(0, 0);
        battle.ChooseLead(1, 0);
        for (int turn = 0; turn < 6 && battle.Phase != Phase.Over; turn++)
        {
            battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            if (battle.Phase == Phase.Over) break;
            Assert.Equal(Phase.Replace, battle.Phase);
            Assert.All(battle.Need, needed => Assert.True(needed));
            for (int side = 0; side < 2; side++)
                battle.Replace(side, battle.Sides[side].Team.FindIndex(f => !f.Fainted));
        }
        Assert.Equal(Phase.Over, battle.Phase);
        return battle;
    }

    static void AssertFrameEventRanges(Battle battle)
    {
        string originalJson = battle.Record.ToJson();
        ulong originalHash = battle.StateHash();
        var originalEvents = battle.Events.Select(EventKey).ToArray();
        // 补位的 Turn 已递增，必须按真实回合标题划界，不能按事件的 Turn 分组。
        var boundaries = new List<int> { 0 };
        boundaries.AddRange(Enumerable.Range(0, battle.Events.Count).Where(i =>
            battle.Events[i].Kind == BattleEventKind.Info && battle.Events[i].Node == Node.TurnHeader));
        boundaries.Add(battle.Events.Count);
        var navigation = new ReplayNavigator(battle.Db, battle.Record);
        Assert.Equal(boundaries.Count - 1, navigation.Count);
        var hashes = new List<ulong>();
        var visited = new List<int>();
        for (int frame = 0; frame < navigation.Count; frame++)
        {
            if (frame > 0) navigation.Next();
            AssertFrameEventRange(navigation, battle.Events, boundaries, frame);
            visited.AddRange(Enumerable.Range(navigation.FrameEventStart,
                navigation.Current.Events.Count - navigation.FrameEventStart));
            hashes.Add(navigation.Current.StateHash());
        }
        Assert.Equal(Enumerable.Range(0, battle.Events.Count), visited);
        Assert.Equal(originalHash, navigation.Current.StateHash());
        Assert.Equal(originalJson, navigation.Current.Record.ToJson());
        navigation.Next();
        AssertFrameEventRange(navigation, battle.Events, boundaries, navigation.Count - 1);
        for (int frame = navigation.Count - 1; frame >= 0; frame--)
        {
            AssertFrameEventRange(navigation, battle.Events, boundaries, frame);
            Assert.Equal(hashes[frame], navigation.Current.StateHash());
            navigation.Previous();
        }
        Assert.Equal(0, navigation.Index);
        foreach (int frame in new[] { navigation.Count - 1, 0, navigation.Count / 2, navigation.Count / 2, 0 })
        {
            navigation.Seek(frame);
            AssertFrameEventRange(navigation, battle.Events, boundaries, frame);
            Assert.Equal(hashes[frame], navigation.Current.StateHash());
        }
        Assert.Equal(originalJson, battle.Record.ToJson());
        Assert.Equal(originalJson, navigation.Record.ToJson());
        Assert.Equal(originalHash, battle.StateHash());
        Assert.Equal(originalEvents, battle.Events.Select(EventKey));
    }

    static void AssertFrameEventRange(ReplayNavigator navigation, IReadOnlyList<BattleEvent> source,
        IList<int> boundaries, int frame)
    {
        int from = boundaries[frame], to = boundaries[frame + 1];
        Assert.Equal(frame, navigation.Index);
        Assert.Equal(from, navigation.FrameEventStart);
        Assert.Equal(to, navigation.Current.Events.Count);
        var expected = source.Skip(from).Take(to - from).Select(EventKey).ToArray();
        Assert.Equal(expected, navigation.Current.Events.Skip(from).Select(EventKey));
        var animated = BattleTimeline.Build(navigation.Current.Events, from, to, 2);
        var immediate = BattleTimeline.Build(navigation.Current.Events, from, to, 0);
        var visibleIndices = Enumerable.Range(from, to - from).Where(i =>
            source[i].Kind != BattleEventKind.Info && source[i].Kind != BattleEventKind.PpChange).ToArray();
        Assert.Equal(visibleIndices, animated.Select(c => c.EventIndex).Distinct());
        Assert.Equal(animated.Select(CueKey), immediate.Select(CueKey));
        Assert.All(immediate, cue => { Assert.Equal(0d, cue.Start); Assert.Equal(0d, cue.Duration); });
        Assert.All(animated, cue =>
        {
            Assert.InRange(cue.EventIndex, from, to - 1);
            Assert.Equal(source[cue.EventIndex].LogIndex, cue.LogIndex);
        });
        Assert.Equal(expected, navigation.Current.Events.Skip(from).Select(EventKey));
    }

    static (BattleEventKind, int, int, string, int, int, string, int) EventKey(BattleEvent entry) =>
        (entry.Kind, entry.LogIndex, entry.Turn, entry.Node, entry.Side, entry.Slot, entry.Id, entry.Amount);

    static (CueKind, int, int, int, int, int, string, string, string) CueKey(AnimCue cue) =>
        (cue.Kind, cue.EventIndex, cue.LogIndex, cue.Side, cue.Slot, cue.Amount, cue.Id, cue.Text, cue.Style);
}
