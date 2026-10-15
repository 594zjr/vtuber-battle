using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T93_ReplayCompatTests
{
    const string TeamJson = "[[\"bell\",\"enka\",\"shiori\",\"lumi\",\"hane\",\"momo\"],[\"kei\",\"yuan\",\"nyako\",\"buck\",\"kasumi\",\"bee\"]]";

    static IList<string>[] Teams()
    {
        return new IList<string>[]
        {
            new List<string> { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new List<string> { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        };
    }

    static Battle Make(ContentDb db, bool lead = false, bool ban = false)
    {
        return new Battle(db, Teams(), new[] { "你", "对手" },
            new BattleOptions { Seed = 93, LeadPick = lead, BanOne = ban });
    }

    static string LegacyJson(int? version = null, string events = "[]")
    {
        string versionField = version.HasValue
            ? "\"version\":" + version.Value.ToString(CultureInfo.InvariantCulture) + "," : "";
        return "{" + versionField + "\"seed\":93,\"packId\":\"original\",\"teams\":" + TeamJson
            + ",\"events\":" + events + "}";
    }

    static void AssertChineseReason(ReplayCompat result)
    {
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Contains(result.Reason, c => c >= '\u4e00' && c <= '\u9fff');
    }

    static void AssertSameBattle(Battle expected, Battle actual)
    {
        Assert.Equal(expected.StateHash(), actual.StateHash());
        Assert.Equal(expected.Phase, actual.Phase);
        Assert.Equal(expected.Winner, actual.Winner);
        Assert.Equal(expected.Log.Select(e => e.Node + "|" + e.Text), actual.Log.Select(e => e.Node + "|" + e.Text));
        for (int i = 0; i < 8; i++) Assert.Equal(expected.Random(), actual.Random());
    }

    [Fact]
    public void CurrentVersionRemainsTwoAndNewBattlesUseIt()
    {
        var db = BattleTests.LoadPack("original");
        Assert.Equal(2, BattleRecord.CurrentVersion);
        Assert.Equal(BattleRecord.CurrentVersion, new BattleRecord().Version);
        var battle = Make(db);
        Assert.Equal(BattleRecord.CurrentVersion, battle.Record.Version);
        Assert.Equal(db.ContentHash(), battle.Record.ContentHash);
        Assert.Equal(BattleRecord.CurrentVersion, BattleRecord.FromJson(battle.Record.ToJson()).Version);
        var compatibility = Replay.Check(db, battle.Record);
        Assert.True(compatibility.CanReplay);
        Assert.Equal(string.Empty, compatibility.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public void MissingHistoricalOptionalFieldsUseTheirOriginalDefaults(int? version)
    {
        var db = BattleTests.LoadPack("original");
        var record = BattleRecord.FromJson(LegacyJson(version));

        Assert.Equal(version ?? 1, record.Version);
        Assert.Equal(93u, record.Seed);
        Assert.Null(record.ContentHash);
        Assert.Null(record.Names);
        Assert.Null(record.Modifiers);
        Assert.False(record.LeadPick);
        Assert.False(record.BanOne);
        Assert.Empty(record.Events);
        var compatibility = Replay.Check(db, record);
        Assert.True(compatibility.CanReplay);
        Assert.Equal(string.Empty, compatibility.Reason);
        var replayed = Replay.Run(db, record);
        Assert.Equal(new[] { "你", "对手" }, replayed.Sides.Select(side => side.Name));
        AssertSameBattle(Make(db), replayed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public void LegacyActionsWithoutRandomCheckpointsFollowTheSeededStream(int? version)
    {
        const string events = "[{\"type\":\"turn\",\"a0\":{\"t\":\"Skill\",\"i\":0},\"a1\":{\"t\":\"Skill\",\"i\":0}}]";
        var db = BattleTests.LoadPack("original");
        var record = BattleRecord.FromJson(LegacyJson(version, events));
        Assert.Null(Assert.Single(record.Events).RngState);
        var expected = Make(db);
        expected.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));

        Assert.True(Replay.Check(db, record).CanReplay);
        AssertSameBattle(expected, Replay.Run(db, record));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PresentOptionalFieldsAndRandomCheckpointsSurviveHistoricalParsing(int version)
    {
        var db = BattleTests.LoadPack("original");
        var battle = new Battle(db, Teams(), new[] { "主场\"甲", "客场\n乙" }, new BattleOptions
        {
            Seed = 93, LeadPick = true, BanOne = true,
            Modifiers = new List<string> { "half_pp", "hype_start" }
        });
        battle.ChooseBan(1, 5);
        battle.ChooseBan(0, 4);
        battle.ChooseLead(1, 2);
        battle.ChooseLead(0, 1);
        battle.Random();
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        var source = battle.Record;
        source.Version = version;
        var record = BattleRecord.FromJson(source.ToJson());

        Assert.Equal(version, record.Version);
        Assert.Equal(source.ContentHash, record.ContentHash);
        Assert.Equal(source.Names, record.Names);
        Assert.Equal(source.Modifiers, record.Modifiers);
        Assert.True(record.LeadPick);
        Assert.True(record.BanOne);
        Assert.Equal(source.Events.Select(e => e.RngState), record.Events.Select(e => e.RngState));
        Assert.All(record.Events, e => Assert.True(e.RngState.HasValue));
        Assert.True(Replay.Check(db, record).CanReplay);
        AssertSameBattle(battle, Replay.Run(db, record));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void FutureVersionParsingPreservesTheVersionAndStillValidatesPayloadStructure(int version)
    {
        var record = BattleRecord.FromJson(LegacyJson(version));
        Assert.Equal(version, record.Version);
        Assert.Equal(93u, record.Seed);
        Assert.Equal("original", record.PackId);
        Assert.Equal(Teams()[0], record.Teams[0]);
        Assert.Equal(Teams()[1], record.Teams[1]);
        Assert.Empty(record.Events);
        Assert.Equal(version, BattleRecord.FromJson(record.ToJson()).Version);

        string marker = "\"version\":" + version.ToString(CultureInfo.InvariantCulture);
        foreach (string json in new[]
        {
            "{" + marker + "}",
            "{\"teams\":false,\"seed\":\"new-format\",\"events\":null," + marker + "}",
            LegacyJson(version, "null"),
            LegacyJson(version, "[{\"type\":\"future-action\"}]")
        })
        {
            var error = Assert.Throws<FormatException>(() => BattleRecord.FromJson(json));
            Assert.DoesNotContain("录像来自更新的版本", error.Message);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void FutureRecordsRequireCompatibilityRejectionWhileStrictRunKeepsLegacyBehavior(int version)
    {
        var db = BattleTests.LoadPack("original");
        var record = Make(db).Record;
        record.Version = version;
        var result = Replay.Check(db, record);

        Assert.False(result.CanReplay);
        Assert.Contains("录像来自更新的版本", result.Reason);
        AssertSameBattle(Make(db), Replay.Run(db, record));
        Assert.Contains("录像来自更新的版本", Assert.ThrowsAny<Exception>(() => Replay.RunBestEffort(db, record)).Message);
    }

    [Fact]
    public void MissingContentLibraryAndRecordGiveChineseReasonsWithoutThrowing()
    {
        var db = BattleTests.LoadPack("original");
        var record = Make(db).Record;
        var noLibrary = Replay.Check(null, record);
        Assert.False(noLibrary.CanReplay);
        AssertChineseReason(noLibrary);
        Assert.Contains("内容包", noLibrary.Reason);

        var noRecord = Replay.Check(db, null);
        Assert.False(noRecord.CanReplay);
        AssertChineseReason(noRecord);
        Assert.ThrowsAny<Exception>(() => Replay.RunBestEffort(null, record));
        Assert.ThrowsAny<Exception>(() => Replay.RunBestEffort(db, null));
    }

    [Theory]
    [InlineData("not_installed")]
    [InlineData("")]
    [InlineData(null)]
    public void MissingOrDifferentPackCannotBeMadeReplayableByBestEffort(string packId)
    {
        var db = BattleTests.LoadPack("original");
        var record = Make(db).Record;
        record.PackId = packId;
        record.ContentHash = "different-content";

        var result = Replay.Check(db, record);
        Assert.False(result.CanReplay);
        AssertChineseReason(result);
        Assert.Contains("内容包", result.Reason);
        Assert.ThrowsAny<Exception>(() => Replay.RunBestEffort(db, record));
    }

    [Theory]
    [InlineData("unknown_streamer")]
    [InlineData("")]
    [InlineData(null)]
    public void MissingStreamerIsACompatibilityFailureEvenWhenFingerprintAlsoDiffers(string id)
    {
        var db = BattleTests.LoadPack("original");
        var record = Make(db).Record;
        record.Teams[0][0] = id;
        record.ContentHash = "different-content";

        var result = Replay.Check(db, record);
        Assert.False(result.CanReplay);
        AssertChineseReason(result);
        Assert.ThrowsAny<Exception>(() => Replay.RunBestEffort(db, record));
    }

    [Theory]
    [InlineData("unknown_modifier")]
    [InlineData("HALF_PP")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownModifiersAreNotSilentlyDroppedByCompatibilityOrBestEffort(string id)
    {
        var db = BattleTests.LoadPack("original");
        var record = Make(db).Record;
        record.Modifiers = new List<string> { "half_pp", id };
        record.ContentHash = "different-content";

        var result = Replay.Check(db, record);
        Assert.False(result.CanReplay);
        AssertChineseReason(result);
        Assert.ThrowsAny<Exception>(() => Replay.RunBestEffort(db, record));
        Assert.Equal(new[] { "half_pp", id }, record.Modifiers);
    }

    [Theory]
    [InlineData("missing-stat")]
    [InlineData("missing-persona")]
    [InlineData("missing-skill")]
    [InlineData("modifier-overflow")]
    public void UnconstructableContentCannotPassAsOnlyAFingerprintWarning(string invalid)
    {
        var db = BattleTests.LoadPack("original");
        var record = Make(db).Record;
        var streamer = db.Streamer("bell");
        switch (invalid)
        {
            case "missing-stat": streamer.Stats.Remove("hp"); break;
            case "missing-persona": streamer.Persona = "missing_persona"; break;
            case "missing-skill": streamer.Skills[0] = "missing_skill"; break;
            case "modifier-overflow":
                streamer.Stats["hp"] = int.MaxValue;
                record.Modifiers = new List<string> { "hp_x2" };
                break;
            default: throw new ArgumentOutOfRangeException(nameof(invalid));
        }

        var result = Replay.Check(db, record);
        Assert.False(result.CanReplay);
        AssertChineseReason(result);
        Assert.ThrowsAny<Exception>(() => Replay.RunBestEffort(db, record));
    }

    [Theory]
    [InlineData("zero-version")]
    [InlineData("negative-version")]
    [InlineData("null-teams")]
    [InlineData("one-team")]
    [InlineData("null-team")]
    [InlineData("empty-team")]
    [InlineData("one-name")]
    [InlineData("null-event")]
    [InlineData("unknown-event")]
    [InlineData("bad-side")]
    [InlineData("negative-choice")]
    public void MalformedInMemoryRecordsReturnReasonsInsteadOfEscapingCheck(string invalid)
    {
        var db = BattleTests.LoadPack("original");
        var record = Make(db).Record;
        switch (invalid)
        {
            case "zero-version": record.Version = 0; break;
            case "negative-version": record.Version = -1; break;
            case "null-teams": record.Teams = null; break;
            case "one-team": record.Teams = new[] { record.Teams[0] }; break;
            case "null-team": record.Teams[1] = null; break;
            case "empty-team": record.Teams[0].Clear(); break;
            case "one-name": record.Names = new[] { "甲" }; break;
            case "null-event": record.Events.Add(null); break;
            case "unknown-event": record.Events.Add(new BattleRecordEvent { Type = (BattleRecordEventType)99 }); break;
            case "bad-side": record.Events.Add(new BattleRecordEvent { Type = BattleRecordEventType.Replace, Side = 2, To = 1 }); break;
            case "negative-choice": record.Events.Add(new BattleRecordEvent { Type = BattleRecordEventType.Lead, Side = 0, To = -1 }); break;
            default: throw new ArgumentOutOfRangeException(nameof(invalid));
        }

        var result = Replay.Check(db, record);
        Assert.False(result.CanReplay);
        AssertChineseReason(result);
        Assert.ThrowsAny<Exception>(() => Replay.RunBestEffort(db, record));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckAndReplayKeepHistoricalActionsForEngineNormalization(bool unknownAction)
    {
        var db = BattleTests.LoadPack("original");
        var battle = Make(db);
        var action = unknownAction ? new BattleAction { Type = (ActionType)99, Index = int.MinValue } : BattleAction.Skill(-1);
        battle.ResolveTurn(action, BattleAction.SwitchTo(int.MaxValue));
        var record = BattleRecord.FromJson(battle.Record.ToJson());

        Assert.Equal(action.Type, Assert.Single(record.Events).A0.Type);
        Assert.Equal(action.Index, record.Events[0].A0.Index);
        Assert.True(Replay.Check(db, record).CanReplay);
        AssertSameBattle(battle, Replay.RunBestEffort(db, record));
    }

    [Fact]
    public void ChangedContentWarnsAndRequiresExplicitBestEffortWhilePreservingTheOriginalRecord()
    {
        var original = BattleTests.LoadPack("original");
        var record = Make(original).Record;
        var changed = BattleTests.LoadPack("original");
        changed.Streamer("bell").Stats["hp"] += 19;
        string json = record.ToJson();

        var result = Replay.Check(changed, record);
        Assert.True(result.CanReplay);
        AssertChineseReason(result);
        Assert.Contains("结果可能不同", result.Reason);
        Assert.Contains("内容不一致", Assert.Throws<InvalidOperationException>(() => Replay.Run(changed, record)).Message);
        var replayed = Replay.RunBestEffort(changed, record);
        Assert.Equal(json, record.ToJson());
        Assert.Equal(original.ContentHash(), record.ContentHash);
        Assert.Equal(changed.ContentHash(), replayed.Record.ContentHash);
        Assert.NotEqual(Make(original).StateHash(), replayed.StateHash());
        AssertSameBattle(Make(changed), replayed);
    }

    [Fact]
    public void LegacyMissingFingerprintRemainsPlayableWithChangedContent()
    {
        var db = BattleTests.LoadPack("original");
        db.Skills["warm_up"].Power++;
        var record = BattleRecord.FromJson(LegacyJson());

        Assert.True(Replay.Check(db, record).CanReplay);
        AssertSameBattle(Replay.Run(db, record), Replay.RunBestEffort(db, record));
    }

    [Fact]
    public void RepeatedChecksDoNotNormalizeCallerListsChangeRecordsOrConsumeTheLiveRandomStream()
    {
        var db = BattleTests.LoadPack("original");
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        var control = battle.Clone();
        battle.Record.Modifiers = new List<string> { "stage_cap_3", "half_pp", "half_pp" };
        string json = battle.Record.ToJson();
        string contentHash = db.ContentHash();
        ulong state = battle.StateHash();
        var eventReferences = battle.Record.Events.ToArray();
        var namesReference = battle.Record.Names;
        var teamsReference = battle.Record.Teams;
        var modifiersReference = battle.Record.Modifiers;
        var log = battle.Log.Select(e => e.Node + "|" + e.Text).ToArray();

        for (int i = 0; i < 3; i++) Assert.True(Replay.Check(db, battle.Record).CanReplay);

        Assert.Equal(json, battle.Record.ToJson());
        Assert.Equal(contentHash, db.ContentHash());
        Assert.Equal(state, battle.StateHash());
        Assert.Equal(log, battle.Log.Select(e => e.Node + "|" + e.Text));
        Assert.Same(namesReference, battle.Record.Names);
        Assert.Same(teamsReference, battle.Record.Teams);
        Assert.Same(modifiersReference, battle.Record.Modifiers);
        for (int i = 0; i < eventReferences.Length; i++) Assert.Same(eventReferences[i], battle.Record.Events[i]);
        for (int i = 0; i < 8; i++) Assert.Equal(control.Random(), battle.Random());
    }

    [Fact]
    public void CheckCanInspectARecordWithoutExecutingItsEvents()
    {
        var db = BattleTests.LoadPack("original");
        var record = Make(db).Record;
        record.Events.Add(new BattleRecordEvent { Type = BattleRecordEventType.Replace, Side = 0, To = 1 });
        string json = record.ToJson();

        Assert.True(Replay.Check(db, record).CanReplay);
        Assert.ThrowsAny<Exception>(() => Replay.Run(db, record));
        record.ContentHash = "different-content";
        Assert.True(Replay.Check(db, record).CanReplay);
        Assert.ThrowsAny<Exception>(() => Replay.RunBestEffort(db, record));
        record.ContentHash = db.ContentHash();
        Assert.Equal(json, record.ToJson());
    }

    [Theory]
    [InlineData("opening")]
    [InlineData("one-turn")]
    [InlineData("one-ban")]
    [InlineData("one-lead")]
    [InlineData("finished")]
    public void LegalPartialAndCompletedRecordsRemainReplayable(string ending)
    {
        var db = BattleTests.LoadPack("original");
        var battle = Make(db, lead: ending == "one-lead", ban: ending == "one-ban");
        if (ending == "one-turn") battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        else if (ending == "one-ban") battle.ChooseBan(1, 3);
        else if (ending == "one-lead") battle.ChooseLead(0, 2);
        else if (ending == "finished")
        {
            int guard = 0;
            while (battle.Phase != Phase.Over && guard++ < 500)
            {
                if (battle.Phase == Phase.Choose) battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
                else for (int side = 0; side < 2; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
            }
            Assert.Equal(Phase.Over, battle.Phase);
        }
        var record = BattleRecord.FromJson(battle.Record.ToJson());

        Assert.True(Replay.Check(db, record).CanReplay);
        var strict = Replay.Run(db, record);
        var bestEffort = Replay.RunBestEffort(db, record);
        AssertSameBattle(battle.Clone(), strict);
        AssertSameBattle(battle, bestEffort);
    }
}
