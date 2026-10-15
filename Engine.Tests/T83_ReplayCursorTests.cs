using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T83_ReplayCursorTests
{
    [Theory]
    [InlineData(1u, 1)]
    [InlineData(2u, 5)]
    [InlineData(3u, 1000)]
    [InlineData(4u, 1)]
    [InlineData(5u, 5)]
    [InlineData(6u, 1000)]
    [InlineData(7u, 1)]
    [InlineData(8u, 5)]
    [InlineData(9u, 1000)]
    [InlineData(10u, 5)]
    public void FiftyRandomMovesInEachOfTenGamesMatchIndependentPrefixReplays(uint seed, int snapshotEvery)
    {
        var db = BattleTests.LoadPack(seed % 2 == 0 ? "original" : "fan_holo_niji");
        var played = Play(db, seed);
        var record = CopyRecord(played.Record);
        string original = record.ToJson();
        string contentHash = db.ContentHash();
        int maximum = record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn);
        var cursor = new ReplayCursor(db, record, snapshotEvery);
        Assert.Equal(maximum, cursor.MaxTurn);
        Assert.Equal(0, cursor.Turn);
        Assert.Empty(cursor.EventsOfCurrentStep);
        AssertSame(ReplayPrefix(db, record, 0), cursor.Current);
        AssertSame(played, Replay.Run(db, record));

        var choices = new Rng(seed ^ 0x83f01234u);
        int position = 0;
        for (int move = 0; move < 50; move++)
        {
            int operation = (int)(choices.Next() * 3);
            int previousEventCount = cursor.Current.Events.Count;
            bool advanced = false;
            if (operation == 0)
            {
                advanced = position < maximum;
                Assert.Equal(advanced, cursor.Next());
                if (advanced) position++;
            }
            else if (operation == 1)
            {
                Assert.Equal(position > 0, cursor.Prev());
                position = Math.Max(0, position - 1);
            }
            else
            {
                int requested = (int)(choices.Next() * (maximum + 9)) - 4;
                cursor.Seek(requested);
                position = Math.Max(0, Math.Min(maximum, requested));
            }

            // 对照只裁剪原录像并从头 Replay.Run，不使用游标保存的状态或快照。
            var expected = ReplayPrefix(db, record, position);
            Assert.Equal(position, cursor.Turn);
            Assert.Equal(maximum, cursor.MaxTurn);
            AssertSame(expected, cursor.Current);
            if (advanced)
                AssertEvents(expected.Events.Skip(previousEventCount), cursor.EventsOfCurrentStep);
            else Assert.Empty(cursor.EventsOfCurrentStep);
            Assert.Equal(original, record.ToJson());
        }

        cursor.ToEnd();
        Assert.Equal(maximum, cursor.Turn);
        Assert.Empty(cursor.EventsOfCurrentStep);
        AssertSame(played, cursor.Current);
        Assert.Equal(original, record.ToJson());
        Assert.Equal(contentHash, db.ContentHash());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(1000)]
    public void ForwardEventsMatchLiveTurnsIncludingTheirReplacementsButExcludeOpeningChoices(int snapshotEvery)
    {
        var fixture = ControlledGame();
        var cursor = new ReplayCursor(fixture.Battle.Db, fixture.Battle.Record, snapshotEvery);
        Assert.Equal(0, cursor.Turn);
        Assert.Equal(5, cursor.MaxTurn);
        Assert.Equal(Phase.Choose, cursor.Current.Phase);
        Assert.All(cursor.Current.Sides, side => Assert.Equal(5, side.Team.Count));
        Assert.Equal(4, cursor.Current.Record.Events.Count);
        Assert.Empty(cursor.EventsOfCurrentStep);

        for (int turn = 1; turn <= cursor.MaxTurn; turn++)
        {
            Assert.True(cursor.Next());
            Assert.Equal(turn, cursor.Turn);
            Assert.Equal(fixture.StepEvents[turn - 1], EventDump(cursor.EventsOfCurrentStep));
            AssertSame(ReplayPrefix(fixture.Battle.Db, fixture.Battle.Record, turn), cursor.Current);
            foreach (var entry in cursor.EventsOfCurrentStep)
            {
                Assert.InRange(entry.LogIndex, 0, cursor.Current.Log.Count - 1);
                Assert.Equal(entry.Node, cursor.Current.Log[entry.LogIndex].Node);
                Assert.Equal(entry.Turn, cursor.Current.Log[entry.LogIndex].Turn);
            }
            if (turn < cursor.MaxTurn)
            {
                Assert.Contains(cursor.EventsOfCurrentStep, entry => entry.Node == Node.Replace && entry.Kind == BattleEventKind.Enter);
                Assert.Equal(turn + 1, cursor.Current.Turn);
                Assert.Equal(Phase.Choose, cursor.Current.Phase);
            }
        }
        Assert.Equal(Phase.Over, cursor.Current.Phase);
        Assert.Contains(cursor.EventsOfCurrentStep, entry => entry.Kind == BattleEventKind.End);
        AssertSame(fixture.Battle, cursor.Current);
        Assert.False(cursor.Next());
        Assert.Empty(cursor.EventsOfCurrentStep);
    }

    [Fact]
    public void ClampedSeeksSamePositionSeeksAndBoundaryMovesClearAnimationEvents()
    {
        var fixture = ControlledGame();
        var cursor = new ReplayCursor(fixture.Battle.Db, fixture.Battle.Record, 2);
        Assert.False(cursor.Prev());
        Assert.Empty(cursor.EventsOfCurrentStep);
        Assert.True(cursor.Next());
        Assert.NotEmpty(cursor.EventsOfCurrentStep);
        cursor.Seek(cursor.Turn);
        Assert.Empty(cursor.EventsOfCurrentStep);
        Assert.True(cursor.Next());
        Assert.True(cursor.Prev());
        Assert.Equal(1, cursor.Turn);
        Assert.Empty(cursor.EventsOfCurrentStep);

        foreach (int requested in new[] { int.MaxValue, int.MinValue, 3, -1, 2, int.MaxValue, 0 })
        {
            cursor.Seek(requested);
            int clamped = Math.Max(0, Math.Min(cursor.MaxTurn, requested));
            Assert.Equal(clamped, cursor.Turn);
            Assert.Empty(cursor.EventsOfCurrentStep);
            AssertSame(ReplayPrefix(fixture.Battle.Db, fixture.Battle.Record, clamped), cursor.Current);
        }
        cursor.ToEnd();
        Assert.Empty(cursor.EventsOfCurrentStep);
        AssertSame(fixture.Battle, cursor.Current);
        cursor.ToEnd();
        Assert.Empty(cursor.EventsOfCurrentStep);
        Assert.False(cursor.Next());
        Assert.Empty(cursor.EventsOfCurrentStep);
        Assert.True(cursor.Prev());
        Assert.True(cursor.Next());
        Assert.Equal(fixture.StepEvents.Last(), EventDump(cursor.EventsOfCurrentStep));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EmptyRecordsKeepTheirActualInitialPhaseWithoutInventingATurn(bool ban, bool lead)
    {
        var db = BattleTests.LoadPack("original");
        var battle = Make(db, 83, ban, lead);
        var record = CopyRecord(battle.Record);
        string original = record.ToJson();
        var cursor = new ReplayCursor(db, record);
        Assert.Equal(0, cursor.Turn);
        Assert.Equal(0, cursor.MaxTurn);
        AssertSame(battle, cursor.Current);
        Assert.Empty(cursor.EventsOfCurrentStep);
        Assert.False(cursor.Next());
        Assert.False(cursor.Prev());
        cursor.Seek(int.MaxValue);
        cursor.Seek(int.MinValue);
        cursor.ToEnd();
        Assert.Equal(0, cursor.Turn);
        Assert.Empty(cursor.EventsOfCurrentStep);
        AssertSame(battle, cursor.Current);
        Assert.Equal(original, record.ToJson());
    }

    [Theory]
    [InlineData(1, Phase.Ban)]
    [InlineData(2, Phase.Lead)]
    [InlineData(3, Phase.Lead)]
    [InlineData(4, Phase.Choose)]
    public void PartialOpeningChoicesAreAlreadyAppliedAtCursorZero(int eventCount, Phase phase)
    {
        var fixture = ControlledGame();
        var record = FirstEvents(fixture.Battle.Record, eventCount);
        var cursor = new ReplayCursor(fixture.Battle.Db, record, 1);
        Assert.Equal(0, cursor.MaxTurn);
        Assert.Equal(0, cursor.Turn);
        Assert.Equal(phase, cursor.Current.Phase);
        AssertSame(Replay.Run(fixture.Battle.Db, record), cursor.Current);
        Assert.Empty(cursor.EventsOfCurrentStep);
        Assert.False(cursor.Next());
        Assert.Empty(cursor.EventsOfCurrentStep);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialRecordsMayEndBeforeOrAfterTheRecordedReplacement(bool includeReplacement)
    {
        var fixture = ControlledGame();
        int index = fixture.Battle.Record.Events.FindIndex(entry => entry.Type == BattleRecordEventType.Replace);
        Assert.True(index > 0);
        var record = FirstEvents(fixture.Battle.Record, index + (includeReplacement ? 1 : 0));
        string original = record.ToJson();
        var expected = Replay.Run(fixture.Battle.Db, record);
        var cursor = new ReplayCursor(fixture.Battle.Db, record, 1);
        int openingEvents = cursor.Current.Events.Count;
        Assert.Equal(1, cursor.MaxTurn);
        Assert.True(cursor.Next());
        Assert.Equal(includeReplacement ? Phase.Choose : Phase.Replace, cursor.Current.Phase);
        Assert.Equal(!includeReplacement, cursor.Current.Need[1]);
        AssertSame(expected, cursor.Current);
        AssertEvents(expected.Events.Skip(openingEvents), cursor.EventsOfCurrentStep);
        Assert.False(cursor.Next());
        Assert.Empty(cursor.EventsOfCurrentStep);
        Assert.True(cursor.Prev());
        Assert.True(cursor.Next());
        AssertSame(expected, cursor.Current);
        Assert.Equal(original, record.ToJson());
    }

    [Fact]
    public void BothViewerSnapshotsAreIndependentAndDoNotChangeStateHistoryOrRandomness()
    {
        var fixture = ControlledGame();
        var record = CopyRecord(fixture.Battle.Record);
        string original = record.ToJson();
        var cursor = new ReplayCursor(fixture.Battle.Db, record);
        Assert.True(cursor.Next());
        var expected = ReplayPrefix(fixture.Battle.Db, record, 1);
        string[] stepEvents = EventDump(cursor.EventsOfCurrentStep);
        for (int side = 0; side < 2; side++)
        {
            var view = cursor.View(side);
            var normal = BattleViewModel.From(expected, side);
            Assert.Equal(normal.Turn, view.Turn);
            Assert.Equal(normal.Phase, view.Phase);
            Assert.Equal(normal.Notice, view.Notice);
            Assert.Equal(normal.SwitchTargets, view.SwitchTargets);
            Assert.Equal(normal.Skills.Select(skill => (skill.Index, skill.Id, skill.Pp, skill.Usable, skill.DamageMin, skill.DamageMax)),
                view.Skills.Select(skill => (skill.Index, skill.Id, skill.Pp, skill.Usable, skill.DamageMin, skill.DamageMax)));
            Assert.Equal(expected.Sides[side].Team.Select(fighter => fighter.Hp), view.Sides[side].Team.Select(fighter => fighter.Hp));
            view.Sides[side].Team[0].Hp = -1;
            view.Sides[side].Team[0].Stages["atk"] = 99;
            view.Skills.Clear();
            view.SwitchTargets.Clear();
            AssertSame(expected, cursor.Current);
            Assert.NotEqual(-1, cursor.View(side).Sides[side].Team[0].Hp);
        }
        Assert.Equal(stepEvents, EventDump(cursor.EventsOfCurrentStep));
        Assert.Equal(original, record.ToJson());
        Assert.Throws<ArgumentOutOfRangeException>(() => cursor.View(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => cursor.View(2));
        AssertSame(expected, cursor.Current);
    }

    [Fact]
    public void EditingAnimationEventCopiesCannotCorruptCurrentOrStoredSnapshots()
    {
        var fixture = ControlledGame();
        var cursor = new ReplayCursor(fixture.Battle.Db, fixture.Battle.Record, 1);
        Assert.True(cursor.Next());
        var expected = ReplayPrefix(fixture.Battle.Db, fixture.Battle.Record, 1);
        var animationEvent = cursor.EventsOfCurrentStep[0];
        animationEvent.LogIndex = -99;
        animationEvent.Turn = -99;
        animationEvent.Amount = -99;
        animationEvent.Node = "画面副本";
        animationEvent.Id = "画面副本";
        animationEvent.Kind = BattleEventKind.End;
        AssertSame(expected, cursor.Current);
        cursor.ToEnd();
        cursor.Seek(0);
        Assert.True(cursor.Next());
        AssertSame(expected, cursor.Current);
        Assert.Equal(fixture.StepEvents[0], EventDump(cursor.EventsOfCurrentStep));
    }

    [Fact]
    public void CallerRecordEditsAfterConstructionDoNotChangeTheCapturedReplay()
    {
        var fixture = ControlledGame();
        var record = CopyRecord(fixture.Battle.Record);
        var cursor = new ReplayCursor(fixture.Battle.Db, record, 2);
        record.Names[0] = "后来改名";
        record.Teams[0].Clear();
        record.Events[0].To = 999;
        record.Events.Last().A0 = BattleAction.Pass();
        record.Events.Clear();
        record.Seed++;
        record.PackId = "后来改包";
        record.ContentHash = "后来改指纹";
        cursor.ToEnd();
        AssertSame(fixture.Battle, cursor.Current);
        cursor.Seek(1);
        AssertSame(ReplayPrefix(fixture.Battle.Db, fixture.Battle.Record, 1), cursor.Current);
        Assert.True(cursor.Next());
        Assert.Equal(fixture.StepEvents[1], EventDump(cursor.EventsOfCurrentStep));
    }

    [Fact]
    public void LegacyRecordsWithoutFingerprintNamesOrRngCheckpointsStillUseReplaySemantics()
    {
        var fixture = ControlledGame();
        var record = CopyRecord(fixture.Battle.Record);
        record.Version = 1;
        record.ContentHash = null;
        record.Names = null;
        foreach (var entry in record.Events) entry.RngState = null;
        string original = record.ToJson();
        var cursor = new ReplayCursor(fixture.Battle.Db, record, 2);
        for (int turn = 0; turn <= cursor.MaxTurn; turn++)
        {
            cursor.Seek(turn);
            AssertSame(ReplayPrefix(fixture.Battle.Db, record, turn), cursor.Current);
            Assert.Empty(cursor.EventsOfCurrentStep);
        }
        Assert.Equal(original, record.ToJson());
    }

    [Fact]
    public void ConstructorRejectsNullInputsInvalidIntervalsAndMismatchedContent()
    {
        var db = BattleTests.LoadPack("original");
        var record = Make(db, 83).Record;
        Assert.Throws<ArgumentNullException>(() => new ReplayCursor(null, record));
        Assert.Throws<ArgumentNullException>(() => new ReplayCursor(db, null));
        foreach (int interval in new[] { 0, -1, int.MinValue })
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayCursor(db, record, interval));
            Assert.Matches("[\u4e00-\u9fff]", error.Message);
        }
        var wrongHash = CopyRecord(record);
        wrongHash.ContentHash = new string('0', 64);
        Assert.Contains("内容不一致", Assert.Throws<InvalidOperationException>(() => new ReplayCursor(db, wrongHash)).Message);
        var wrongPack = CopyRecord(record);
        wrongPack.ContentHash = null;
        wrongPack.PackId = "missing_pack";
        Assert.Throws<InvalidOperationException>(() => new ReplayCursor(db, wrongPack));
    }

    static Battle Make(ContentDb db, uint seed, bool ban = false, bool lead = false, IList<string>[] teams = null,
        List<string> modifiers = null)
    {
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        return new Battle(db, teams ?? new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "甲方\"\n", "乙方\\" }, new BattleOptions { Seed = seed, BanOne = ban, LeadPick = lead, Modifiers = modifiers });
    }

    static Battle Play(ContentDb db, uint seed)
    {
        var setup = new Rng(seed ^ 0x83u);
        var ids = db.Streamers.Select(streamer => streamer.Id).OrderBy(_ => setup.Next()).ToArray();
        var battle = Make(db, seed, seed % 2 == 0, seed % 3 != 0,
            new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            seed % 3 == 0 ? new List<string> { "hype_start", "half_pp", "sudden_death" } : null);
        int first = (int)(seed % 2);
        if (battle.Phase == Phase.Ban)
        {
            battle.ChooseBan(first, (int)(battle.Random() * 6));
            battle.ChooseBan(1 - first, (int)(battle.Random() * 6));
        }
        if (battle.Phase == Phase.Lead)
        {
            battle.ChooseLead(first, (int)(battle.Random() * battle.Sides[first].Team.Count));
            battle.ChooseLead(1 - first, Ai.ChooseLead(battle, 1 - first));
        }
        int guard = 500;
        while (battle.Phase != Phase.Over && guard-- > 0)
        {
            if (battle.Phase == Phase.Choose)
            {
                for (int i = 0; i <= guard % 3; i++) battle.Random();
                battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
            }
            else
                for (int side = 1; side >= 0 && battle.Phase != Phase.Over; side--)
                    if (battle.Need[side])
                    {
                        battle.Random();
                        battle.Replace(side, Ai.ChooseReplacement(battle, side));
                    }
        }
        Assert.Equal(Phase.Over, battle.Phase);
        return battle;
    }

    sealed class Fixture
    {
        public Battle Battle;
        public readonly List<string[]> StepEvents = new List<string[]>();
    }

    static Fixture ControlledGame()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["cursor_none"] = new PersonaDef { Id = "cursor_none", Name = "测试人设", Code = "TEST_NONE" };
        db.Skills["cursor_hit"] = new SkillDef
        {
            Id = "cursor_hit", Name = "测试退场", Type = "talk", Cat = "status", Pp = 20,
            Effects = { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 1000 } }
        };
        foreach (var streamer in db.Streamers)
        {
            streamer.Persona = "cursor_none";
            streamer.Stats["hp"] = 100;
            streamer.Skills = new List<string> { "cursor_hit", "cursor_hit", "cursor_hit", "cursor_hit" };
            streamer.Fifth = "cursor_hit";
        }
        var battle = Make(db, 83, true, true);
        battle.ChooseBan(1, 2);
        battle.ChooseBan(0, 1);
        battle.ChooseLead(1, 3);
        battle.ChooseLead(0, 2);
        var result = new Fixture { Battle = battle };
        for (int turn = 0; battle.Phase != Phase.Over && turn < 6; turn++)
        {
            int before = battle.Events.Count;
            battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            if (battle.Phase == Phase.Replace)
                battle.Replace(1, battle.LegalFor(1).Switches.First());
            result.StepEvents.Add(EventDump(battle.Events.Skip(before)));
        }
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(5, result.StepEvents.Count);
        return result;
    }

    static BattleRecord CopyRecord(BattleRecord record) => BattleRecord.FromJson(record.ToJson());

    static BattleRecord FirstEvents(BattleRecord record, int count)
    {
        var prefix = CopyRecord(record);
        prefix.Events.RemoveRange(count, prefix.Events.Count - count);
        return prefix;
    }

    static Battle ReplayPrefix(ContentDb db, BattleRecord record, int turns)
    {
        int completed = 0;
        int count = 0;
        foreach (var entry in record.Events)
        {
            if (entry.Type == BattleRecordEventType.Turn)
            {
                if (completed == turns) break;
                completed++;
            }
            count++;
        }
        return Replay.Run(db, FirstEvents(record, count));
    }

    static string[] EventDump(IEnumerable<BattleEvent> events)
    {
        return events.Select(entry => string.Join("|", entry.LogIndex, entry.Turn, entry.Kind, entry.Node,
            entry.Side, entry.Slot, entry.Amount, entry.Id)).ToArray();
    }

    static void AssertEvents(IEnumerable<BattleEvent> expected, IEnumerable<BattleEvent> actual)
    {
        Assert.Equal(EventDump(expected), EventDump(actual));
    }

    static void AssertSame(Battle expected, Battle actual)
    {
        Assert.Equal(expected.StateHash(), actual.StateHash());
        Assert.Equal(expected.Turn, actual.Turn);
        Assert.Equal(expected.Phase, actual.Phase);
        Assert.Equal(expected.Winner, actual.Winner);
        Assert.Equal(expected.Record.ToJson(), actual.Record.ToJson());
        AssertEvents(expected.Events, actual.Events);
        Assert.Equal(expected.Log.Count, actual.Log.Count);
        for (int i = 0; i < expected.Log.Count; i++)
        {
            var left = expected.Log[i];
            var right = actual.Log[i];
            Assert.Equal((left.Turn, left.Node, left.Kind, left.Side, left.Text),
                (right.Turn, right.Node, right.Kind, right.Side, right.Text));
            Assert.Equal(left.SnapActive, right.SnapActive);
            Assert.Equal(left.SnapHp[0], right.SnapHp[0]);
            Assert.Equal(left.SnapHp[1], right.SnapHp[1]);
        }
        Assert.Equal(BattleSummary.From(expected).ToText(), BattleSummary.From(actual).ToText());
    }
}
