using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T94_PickPhaseTests
{
    static IList<string>[] Teams()
    {
        return new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        };
    }

    static Battle Make(int count = 3, bool ban = false, bool lead = false, ContentDb db = null,
        IList<string>[] teams = null, uint seed = 194)
    {
        return new Battle(db ?? BattleTests.LoadPack("original"), teams ?? Teams(), new[] { "我方", "对方" },
            new BattleOptions { Seed = seed, PickCount = count, BanOne = ban, LeadPick = lead });
    }

    static void FinishBans(Battle battle)
    {
        if (battle.Phase != Phase.Ban) return;
        battle.ChooseBan(0, 2);
        battle.ChooseBan(1, 1);
        Assert.Equal(Phase.Pick, battle.Phase);
    }

    [Theory]
    [InlineData(3, false, false, 0)]
    [InlineData(4, false, true, 1)]
    [InlineData(3, true, true, 1)]
    [InlineData(4, true, false, 0)]
    public void CompleteGamesReplayWithBothPickCountsAndCursorPreservesEveryPrefix(int count, bool ban, bool lead, int first)
    {
        var battle = Make(count, ban, lead);
        var originals = Teams();
        FinishBans(battle);
        var picks = new[]
        {
            battle.AvailablePicks(0).Reverse().Take(count).ToList(),
            battle.AvailablePicks(1).Skip(1).Take(count).ToList()
        };
        battle.Random();
        battle.ChoosePick(first, picks[first]);
        battle.Random();
        battle.Random();
        battle.ChoosePick(1 - first, picks[1 - first]);
        var opening = battle.Clone();
        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.False(battle.HasChosenLead(0));
        Assert.False(battle.HasChosenLead(1));
        Assert.All(battle.Sides, side => Assert.Equal(count, side.Team.Count));
        var pickEvents = battle.Record.Events.Where(entry => entry.Type == BattleRecordEventType.Pick).ToArray();
        Assert.Equal(new[] { first, 1 - first }, pickEvents.Select(entry => entry.Side));
        Assert.All(pickEvents, entry => Assert.True(entry.RngState.HasValue));
        Assert.NotEqual(pickEvents[0].RngState, pickEvents[1].RngState);
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(originals[side], battle.Record.Teams[side]);
            Assert.Equal(picks[side].Select(index => originals[side][index]), Ids(battle, side));
        }

        PlayToEnd(battle);

        string json = battle.Record.ToJson();
        var record = BattleRecord.FromJson(json);
        Assert.Equal(count, record.PickCount);
        var replayed = Replay.Run(battle.Db, record);
        AssertSame(battle, replayed);
        Assert.Equal(json, record.ToJson());
        var cursor = new ReplayCursor(battle.Db, record, 2);
        AssertSame(opening, cursor.Current);
        Assert.Empty(cursor.EventsOfCurrentStep);
        while (cursor.Next())
            AssertSame(ReplayPrefix(battle.Db, record, cursor.Turn), cursor.Current);
        AssertSame(battle, cursor.Current);
        foreach (int turn in new[] { 0, cursor.MaxTurn / 2, cursor.MaxTurn, 1, 0 })
        {
            cursor.Seek(turn);
            AssertSame(ReplayPrefix(battle.Db, record, turn), cursor.Current);
        }
        cursor.ToEnd();
        AssertSame(battle, cursor.Current);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void SecretChoicesAreIndistinguishableThroughPublicStateAndRecordUntilBothConfirm(int side, bool ban)
    {
        var first = Make(ban: ban);
        var alternative = Make(ban: ban);
        FinishBans(first);
        FinishBans(alternative);
        var before = first.Clone();
        var available = first.AvailablePicks(side).ToArray();

        first.ChoosePick(side, available.Take(3).ToList());
        alternative.ChoosePick(side, available.Reverse().Take(3).ToList());

        Assert.Equal(Phase.Pick, first.Phase);
        Assert.True(first.HasChosenPick(side));
        Assert.False(first.HasChosenPick(1 - side));
        Assert.Equal(before.Record.ToJson(), first.Record.ToJson());
        Assert.Equal(LogDump(before), LogDump(first));
        Assert.Equal(EventDump(before), EventDump(first));
        Assert.NotEqual(before.StateHash(), first.StateHash());
        Assert.Equal(first.StateHash(), alternative.StateHash());
        Assert.Equal(first.Record.ToJson(), alternative.Record.ToJson());
        for (int s = 0; s < 2; s++) Assert.Equal(Ids(before, s), Ids(first, s));
        Assert.All(first.Sides.SelectMany(s => s.Team), fighter => Assert.Empty(fighter.Fields));
        Assert.DoesNotContain(first.Record.Events, entry => entry.Type == BattleRecordEventType.Pick);
        Assert.Equal(before.Clone().Random(), first.Clone().Random());

        // 公开录像只能恢复公开前缀，不能把对手尚未揭示的选择带入回放。
        var publicReplay = Replay.Run(first.Db, BattleRecord.FromJson(first.Record.ToJson()));
        Assert.Equal(Phase.Pick, publicReplay.Phase);
        Assert.False(publicReplay.HasChosenPick(0));
        Assert.False(publicReplay.HasChosenPick(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CloneOfPendingPickContinuesIndependentlyWithCapturedOrderAndRandomCheckpoints(int first)
    {
        var battle = Make(ban: true);
        FinishBans(battle);
        battle.Random();
        battle.ChoosePick(first, battle.AvailablePicks(first).Reverse().Take(3).ToList());
        var pending = battle.Clone();
        AssertSame(battle, pending);
        var secondPick = battle.AvailablePicks(1 - first).Take(3).ToList();
        string recordBefore = battle.Record.ToJson();
        var logBefore = LogDump(battle);
        pending.Random();
        pending.ChoosePick(1 - first, secondPick);
        Assert.Equal(Phase.Pick, battle.Phase);
        Assert.Equal(recordBefore, battle.Record.ToJson());
        Assert.Equal(logBefore, LogDump(battle));
        Assert.False(battle.HasChosenPick(1 - first));

        battle.Random();
        battle.ChoosePick(1 - first, secondPick);
        AssertSame(battle, pending);
        AssertSame(battle, Replay.Run(battle.Db, BattleRecord.FromJson(battle.Record.ToJson())));
        var cloneEvent = pending.Record.Events.First(entry => entry.Type == BattleRecordEventType.Pick);
        var liveEvent = battle.Record.Events.First(entry => entry.Type == BattleRecordEventType.Pick);
        int original = liveEvent.Indices[0];
        cloneEvent.Indices[0] = 99;
        Assert.Equal(original, liveEvent.Indices[0]);
    }

    [Fact]
    public void PickInputAndAvailableCandidatesDoNotExposeMutableInternalLists()
    {
        var battle = Make();
        var available = battle.AvailablePicks(0);
        if (available is IList<int> mutable)
        {
            Assert.True(mutable.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => mutable[0] = 5);
        }
        var chosen = new List<int> { 5, 2, 0 };
        battle.ChoosePick(0, chosen);
        chosen[0] = 1;
        chosen.Clear();
        Assert.Equal(Enumerable.Range(0, 6), battle.AvailablePicks(0));
        battle.ChoosePick(1, new[] { 3, 1, 5 });
        Assert.Equal(new[] { Teams()[0][5], Teams()[0][2], Teams()[0][0] }, Ids(battle, 0));
        Assert.Equal(new[] { 5, 2, 0 }, battle.Record.Events.First().Indices);
        Assert.Empty(battle.AvailablePicks(0));
        Assert.Equal(Enumerable.Range(0, 6), available);

        var record = BattleRecord.FromJson(battle.Record.ToJson());
        var cursor = new ReplayCursor(battle.Db, record);
        record.PickCount = 4;
        record.Events[0].Indices[0] = 99;
        record.Events[0].Indices.Clear();
        AssertSame(battle, cursor.Current);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void InvalidPickCountsAreRejectedInChinese(int count)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Make(count));
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
    }

    [Fact]
    public void PickRequiresTwoLegalSixPersonPublicRosters()
    {
        var db = BattleTests.LoadPack("original");
        var invalid = new List<IList<string>[]>
        {
            new IList<string>[] { null, Teams()[1] },
            new IList<string>[] { Teams()[0].Take(5).ToList(), Teams()[1] },
            new IList<string>[] { Teams()[0], Teams()[1].Concat(new[] { "bell" }).ToList() },
            new IList<string>[] { Enumerable.Repeat("bell", 6).ToList(), Teams()[1] },
            new IList<string>[] { new[] { "missing", "enka", "shiori", "lumi", "hane", "momo" }, Teams()[1] },
            new IList<string>[] { Teams()[0] }
        };
        foreach (var teams in invalid)
            Assert.ThrowsAny<ArgumentException>(() => Make(db: db, teams: teams));
        Assert.ThrowsAny<ArgumentException>(() => new Battle(db, null, new[] { "我方", "对方" }, new BattleOptions { PickCount = 3 }));
        var mirror = Make(db: db, teams: new[] { Teams()[0], Teams()[0] });
        Assert.Equal(Phase.Pick, mirror.Phase);
    }

    [Fact]
    public void InvalidChoicesAndWrongPhaseCallsAreAtomicIncludingRepeatedSubmission()
    {
        var battle = Make();
        foreach (var indices in new IList<int>[]
        {
            null, Array.Empty<int>(), new[] { 0, 1 }, new[] { 0, 1, 2, 3 },
            new[] { 0, 0, 1 }, new[] { -1, 1, 2 }, new[] { 0, 1, 6 }, new[] { 0, 1, int.MaxValue }
        })
            Rejected<ArgumentException>(battle, () => battle.ChoosePick(0, indices));
        foreach (int side in new[] { -1, 2 })
        {
            Rejected<ArgumentOutOfRangeException>(battle, () => battle.ChoosePick(side, new[] { 0, 1, 2 }));
            Rejected<ArgumentOutOfRangeException>(battle, () => battle.HasChosenPick(side));
            Rejected<ArgumentOutOfRangeException>(battle, () => battle.AvailablePicks(side));
        }
        Rejected<InvalidOperationException>(battle, () => battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass()));
        Rejected<InvalidOperationException>(battle, () => battle.Replace(0, 1));
        Rejected<InvalidOperationException>(battle, () => battle.ChooseLead(0, 1));
        Rejected<InvalidOperationException>(battle, () => battle.ChooseBan(0, 1));
        battle.ChoosePick(0, new[] { 2, 0, 5 });
        Rejected<InvalidOperationException>(battle, () => battle.ChoosePick(0, new[] { 1, 3, 4 }));
        Rejected<ArgumentException>(battle, () => battle.ChoosePick(1, new[] { 0, 0, 5 }));
        battle.ChoosePick(1, new[] { 1, 3, 4 });
        Rejected<InvalidOperationException>(battle, () => battle.ChoosePick(1, new[] { 0, 2, 5 }));
        Assert.Equal(new[] { 2, 0, 5 }, battle.Record.Events[0].Indices);
        Assert.Equal(2, battle.Record.Events.Count);
    }

    [Fact]
    public void BanThenPickUsesOriginalSlotsAndRemapsEveryOpeningSnapshotToSelectedFighters()
    {
        var battle = Make(ban: true, lead: true);
        Rejected<InvalidOperationException>(battle, () => battle.ChoosePick(0, new[] { 0, 1, 2 }));
        FinishBans(battle);
        Assert.Equal(new[] { 0, 2, 3, 4, 5 }, battle.AvailablePicks(0));
        Assert.Equal(new[] { 0, 1, 3, 4, 5 }, battle.AvailablePicks(1));
        Rejected<ArgumentException>(battle, () => battle.ChoosePick(0, new[] { 0, 1, 5 }));
        Rejected<ArgumentException>(battle, () => battle.ChoosePick(1, new[] { 2, 3, 4 }));
        Assert.DoesNotContain(battle.Log, entry => entry.Kind == "enter" || entry.Kind == "trait");
        var picks = new[] { new[] { 5, 0, 3 }, new[] { 4, 1, 5 } };
        battle.ChoosePick(0, picks[0]);
        Assert.All(battle.Sides, side => Assert.Equal(5, side.Team.Count));
        battle.ChoosePick(1, picks[1]);
        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.Equal(1, battle.Turn);
        Assert.DoesNotContain(battle.Record.Events, entry => entry.Type == BattleRecordEventType.Lead);
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(picks[side].Select(index => Teams()[side][index]), Ids(battle, side));
            Assert.Equal(Enumerable.Range(1, 3), battle.Sides[side].Team.Select(fighter => fighter.Slot));
            Assert.Equal(0, battle.Sides[side].Active);
            foreach (var entry in battle.Log)
            {
                Assert.Equal(3, entry.SnapHp[side].Length);
                Assert.InRange(entry.SnapActive[side], 0, 2);
            }
            foreach (var entry in battle.Log.Where(entry => entry.Kind == "ban"))
                Assert.Equal(battle.Sides[side].Team.Select(fighter => fighter.MaxHp), entry.SnapHp[side]);
        }
        Assert.Equal(2, battle.Log.Count(entry => entry.Kind == "enter"));
        AssertSame(battle, Replay.Run(battle.Db, BattleRecord.FromJson(battle.Record.ToJson())));
    }

    [Fact]
    public void PendingPickReportDoesNotPresentOriginalFirstMemberAsConfirmedLead()
    {
        var battle = Make(ban: true);
        FinishBans(battle);
        string before = BattleReport.Markdown(battle);
        battle.ChoosePick(0, new[] { 5, 3, 0 });
        string pending = BattleReport.Markdown(battle);
        Assert.Equal(before, pending);
        Assert.Equal(2, pending.Split(new[] { "首发：待双方确认" }, StringSplitOptions.None).Length - 1);
        Assert.Contains("当前候选阵容", pending);
        Assert.DoesNotContain("首发：" + battle.Active(0).Name, pending);
        Assert.DoesNotContain("首发：" + battle.Active(1).Name, pending);
        battle.ChoosePick(1, new[] { 5, 3, 0 });
        string confirmed = BattleReport.Markdown(battle);
        Assert.DoesNotContain("首发：待双方确认", confirmed);
        Assert.Contains("首发：" + battle.Active(0).Name, confirmed);
        Assert.Contains("首发：" + battle.Active(1).Name, confirmed);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void OnlySelectedMembersParticipateInReplacementAndElimination(int count)
    {
        var db = KnockoutContent();
        var battle = Make(count, db: db);
        var selected = Enumerable.Range(0, count).ToArray();
        var unselected = battle.Sides.SelectMany(side => side.Team.Skip(count)).ToArray();
        battle.ChoosePick(0, selected);
        battle.ChoosePick(1, selected);
        for (int defeated = 1; defeated <= count; defeated++)
        {
            battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            Assert.Equal(defeated, battle.Sides[1].Team.Count(fighter => fighter.Fainted));
            if (defeated < count)
            {
                Assert.Equal(Phase.Replace, battle.Phase);
                Assert.True(battle.Need[1]);
                Assert.False(battle.Need[0]);
                battle.Replace(1, defeated);
                Assert.Equal(Phase.Choose, battle.Phase);
                Assert.Equal(defeated, battle.Sides[1].Active);
            }
        }
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(0, battle.Winner);
        Assert.Equal(count, battle.Record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn));
        Assert.Equal(count - 1, battle.Record.Events.Count(entry => entry.Type == BattleRecordEventType.Replace));
        Assert.All(unselected, fighter => { Assert.False(fighter.Fainted); Assert.Equal(fighter.MaxHp, fighter.Hp); });
        AssertSame(battle, Replay.Run(db, BattleRecord.FromJson(battle.Record.ToJson())));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void KeepingOriginalSixthMemberDoesNotGrantSixthSlotFreePpAfterSelection(int count)
    {
        var db = BattleTests.LoadPack("original");
        var teams = Teams();
        teams[0][0] = "nyako";
        var full = Make(0, db: db, teams: teams);
        Assert.True(full.LegalFor(0).FreePp);
        var battle = Make(count, db: db, teams: teams);
        battle.ChoosePick(0, new[] { 0, 5, 1, 2 }.Take(count).ToList());
        battle.ChoosePick(1, Enumerable.Range(0, count).ToArray());
        Assert.Contains(battle.Sides[0].Team, fighter => fighter.Def.Id == "momo");
        Assert.DoesNotContain(battle.Sides[0].Team, fighter => fighter.Slot == 6);
        Assert.False(battle.LegalFor(0).FreePp);
        int skill = battle.Active(0).Skills.FindIndex(slot => slot.Id == "cat_walk");
        int pp = battle.Active(0).Skills[skill].Pp;
        battle.ResolveTurn(BattleAction.Skill(skill), BattleAction.Pass());
        Assert.Equal(pp - 1, battle.Active(0).Skills[skill].Pp);
        foreach (var slot in battle.Active(0).Skills) slot.Pp = 0;
        Assert.True(battle.LegalFor(0).Struggle);
    }

    public static IEnumerable<object[]> AiCases()
    {
        foreach (AiLevel level in Enum.GetValues(typeof(AiLevel)))
            foreach (int count in new[] { 3, 4 })
                foreach (bool ban in new[] { false, true }) yield return new object[] { level, count, ban };
    }

    [Theory]
    [MemberData(nameof(AiCases))]
    public void AiReturnsLegalDeterministicPicksWithoutSubmittingAndOnlyEasyConsumesBattleRandomness(AiLevel level, int count, bool ban)
    {
        var db = BattleTests.LoadPack("original");
        var battle = Make(count, ban, db: db);
        var same = Make(count, ban, db: db);
        FinishBans(battle);
        FinishBans(same);
        var before = battle.Clone();
        string content = db.ContentHash();
        var choice = Ai.ChoosePick(battle, 0, level);
        Assert.Equal(choice, Ai.ChoosePick(same, 0, level));
        Assert.Equal(count, choice.Count);
        Assert.Equal(count, choice.Distinct().Count());
        Assert.All(choice, index => Assert.Contains(index, battle.AvailablePicks(0)));
        Assert.False(battle.HasChosenPick(0));
        Assert.Equal(before.Record.ToJson(), battle.Record.ToJson());
        Assert.Equal(LogDump(before), LogDump(battle));
        Assert.Equal(EventDump(before), EventDump(battle));
        Assert.Equal(content, db.ContentHash());
        if (level == AiLevel.Easy) Assert.NotEqual(before.StateHash(), battle.StateHash());
        else AssertSame(before, battle);
        Assert.Equal(battle.Clone().Random(), same.Clone().Random());
    }

    [Theory]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void NonEasyAiDoesNotReadOpponentsPrivateSelection(AiLevel level)
    {
        var db = BattleTests.LoadPack("original");
        var noSelection = Make(db: db);
        var first = Make(db: db);
        var second = Make(db: db);
        first.ChoosePick(1, new[] { 0, 1, 2 });
        second.ChoosePick(1, new[] { 5, 4, 3 });
        var expected = Ai.ChoosePick(noSelection, 0, level);
        ulong firstHash = first.StateHash(), secondHash = second.StateHash();
        Assert.Equal(expected, Ai.ChoosePick(first, 0, level));
        Assert.Equal(expected, Ai.ChoosePick(second, 0, level));
        Assert.Equal(firstHash, first.StateHash());
        Assert.Equal(secondHash, second.StateHash());
    }

    [Fact]
    public void AiRejectsInvalidSideLevelAndAlreadySubmittedOrWrongPhase()
    {
        var battle = Make();
        Assert.Throws<ArgumentNullException>(() => Ai.ChoosePick(null, 0, AiLevel.Normal));
        Rejected<ArgumentOutOfRangeException>(battle, () => Ai.ChoosePick(battle, -1, AiLevel.Normal));
        Rejected<ArgumentOutOfRangeException>(battle, () => Ai.ChoosePick(battle, 2, AiLevel.Normal));
        Rejected<ArgumentOutOfRangeException>(battle, () => Ai.ChoosePick(battle, 0, (AiLevel)99));
        battle.ChoosePick(0, new[] { 0, 1, 2 });
        Rejected<InvalidOperationException>(battle, () => Ai.ChoosePick(battle, 0, AiLevel.Normal));
        battle.ChoosePick(1, new[] { 0, 1, 2 });
        Rejected<InvalidOperationException>(battle, () => Ai.ChoosePick(battle, 1, AiLevel.Normal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledPickingKeepsLegacyJsonAndHash(bool lead)
    {
        var db = BattleTests.LoadPack("original");
        var legacy = new Battle(db, Teams(), new[] { "我方", "对方" }, 194, lead);
        var explicitZero = Make(0, lead: lead, db: db);
        AssertSame(legacy, explicitZero);
        Assert.DoesNotContain("pickCount", explicitZero.Record.ToJson());
        Assert.Equal(0, BattleRecord.FromJson(legacy.Record.ToJson()).PickCount);
        AssertSame(legacy, Replay.Run(db, BattleRecord.FromJson(legacy.Record.ToJson())));
        Assert.Empty(explicitZero.AvailablePicks(0));
    }

    [Fact]
    public void ReplayJsonRejectsInvalidPickCountAndMalformedPickIndices()
    {
        var battle = Make();
        battle.ChoosePick(0, new[] { 0, 1, 2 });
        battle.ChoosePick(1, new[] { 3, 4, 5 });
        string valid = battle.Record.ToJson();
        foreach (string count in new[] { "-1", "1", "2", "5", "3.5", "null", "true", "\"3\"" })
            Assert.Throws<FormatException>(() => BattleRecord.FromJson(valid.Replace("\"pickCount\":3", "\"pickCount\":" + count)));
        foreach (string indices in new[] { "null", "[]", "[0,1]", "[0,0,2]", "[0,1,6]", "[0,1,-1]", "[0,1,2.5]", "[0,1,\"2\"]" })
            Assert.Throws<FormatException>(() => BattleRecord.FromJson(valid.Replace("\"indices\":[0,1,2]", "\"indices\":" + indices)));
        Assert.Throws<FormatException>(() => BattleRecord.FromJson(valid.Replace(",\"pickCount\":3", "")));
    }

    static ContentDb KnockoutContent()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["pick_none"] = new PersonaDef { Id = "pick_none", Name = "测试人设", Code = "TEST_NONE" };
        db.Skills["pick_knockout"] = new SkillDef
        {
            Id = "pick_knockout", Name = "测试下播", Type = "talk", Cat = "status", Pp = 20,
            Effects = { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 1000 } }
        };
        foreach (var streamer in db.Streamers)
        {
            streamer.Persona = "pick_none";
            streamer.Stats["hp"] = 100;
            streamer.Skills = Enumerable.Repeat("pick_knockout", 4).ToList();
            streamer.Fifth = "pick_knockout";
        }
        return db;
    }

    static void PlayToEnd(Battle battle)
    {
        int guard = 500;
        while (battle.Phase != Phase.Over && guard-- > 0)
        {
            if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
            else
            {
                Assert.Equal(Phase.Replace, battle.Phase);
                for (int side = 0; side < 2 && battle.Phase != Phase.Over; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
            }
        }
        Assert.Equal(Phase.Over, battle.Phase);
    }

    static Battle ReplayPrefix(ContentDb db, BattleRecord record, int turns)
    {
        var prefix = BattleRecord.FromJson(record.ToJson());
        var positions = prefix.Events.Select((entry, index) => new { entry.Type, Index = index })
            .Where(entry => entry.Type == BattleRecordEventType.Turn).Select(entry => entry.Index).ToArray();
        if (turns < positions.Length) prefix.Events.RemoveRange(positions[turns], prefix.Events.Count - positions[turns]);
        return Replay.Run(db, prefix);
    }

    static string[] Ids(Battle battle, int side) => battle.Sides[side].Team.Select(fighter => fighter.Def.Id).ToArray();

    static string[] LogDump(Battle battle) => battle.Log.Select(entry => string.Join("|", entry.Turn,
        entry.Node, entry.Kind, entry.Side, entry.Text, string.Join(",", entry.SnapActive),
        string.Join(",", entry.SnapHp[0]), string.Join(",", entry.SnapHp[1]))).ToArray();

    static string[] EventDump(Battle battle) => battle.Events.Select(entry => string.Join("|", entry.LogIndex,
        entry.Turn, entry.Node, entry.Kind, entry.Side, entry.Slot, entry.Id, entry.Amount)).ToArray();

    static void AssertSame(Battle expected, Battle actual)
    {
        Assert.Equal(expected.Phase, actual.Phase);
        Assert.Equal(expected.PickCount, actual.PickCount);
        Assert.Equal(expected.StateHash(), actual.StateHash());
        Assert.Equal(expected.Record.ToJson(), actual.Record.ToJson());
        Assert.Equal(LogDump(expected), LogDump(actual));
        Assert.Equal(EventDump(expected), EventDump(actual));
        Assert.Equal(expected.Clone().Random(), actual.Clone().Random());
    }

    static void Rejected<T>(Battle battle, Action action) where T : Exception
    {
        var before = battle.Clone();
        Assert.ThrowsAny<T>(action);
        AssertSame(before, battle);
    }
}
