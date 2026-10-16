using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using StreamDuel.Game;
using Xunit;

public class T70_ReplayIntegrationReviewTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    static double[] NextRandoms(Battle battle)
    {
        var copy = battle.Clone();
        return Enumerable.Range(0, 4).Select(_ => copy.Random()).ToArray();
    }

    static void Terminate(Battle battle, int kind)
    {
        if (kind == 0) battle.Surrender(1);
        else if (kind == 1) { battle.OfferDraw(0); battle.RespondDraw(1, true); }
        else battle.ResolveSurrenders(true, true);
    }

    static void SameFinal(Battle expected, ReplayNavigator navigation)
    {
        Assert.Equal(expected.StateHash(), navigation.Current.StateHash());
        Assert.Equal(expected.Phase, navigation.Current.Phase);
        Assert.Equal(expected.Winner, navigation.Current.Winner);
        Assert.Equal(expected.Record.ToJson(), navigation.Current.Record.ToJson());
        Assert.Equal(Json(expected.Log), Json(navigation.Current.Log));
        Assert.Equal(Json(expected.Events), Json(navigation.Current.Events));
        Assert.Equal(BattleSummary.From(expected).ToText(), BattleSummary.From(navigation.Current).ToText());
        Assert.Equal(BattleSummary.From(expected).Mvp(), BattleSummary.From(navigation.Current).Mvp());
        Assert.Equal(NextRandoms(expected), NextRandoms(navigation.Current));
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 2)]
    public void VersionThreeOpeningTerminationIsOneCompleteInspectableFrame(bool ban, bool lead, int kind)
    {
        var battle = T70_MvpReviewTests.MakeBattle(ban, lead);
        Terminate(battle, kind);
        Assert.Equal(BattleRecord.TerminationVersion, battle.Record.Version);
        Assert.DoesNotContain(battle.Record.Events, entry => entry.Type == BattleRecordEventType.Turn);
        var navigation = new ReplayNavigator(battle.Db, battle.Record);

        Assert.Equal(1, navigation.Count);
        Assert.Equal(new[] { 0 }, navigation.Turns);
        Assert.Equal(0, navigation.FrameEventStart);
        SameFinal(battle, navigation);
        Assert.Equal(Json(battle.Log), Json(navigation.FrameLog));
        var cues = BattleTimeline.Build(navigation.Current.Events, navigation.FrameEventStart, navigation.Current.Events.Count, 1);
        Assert.Contains(cues, cue => cue.Kind == CueKind.End);
        navigation.Next();
        navigation.Previous();
        navigation.Seek(0);
        SameFinal(battle, navigation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void VersionThreeTerminationAfterARealTurnBelongsToItsFinalFrame(int kind)
    {
        var battle = T70_MvpReviewTests.MakeBattle();
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(1));
        battle.OfferDraw(1);
        battle.RespondDraw(0, false);
        Terminate(battle, kind);
        string source = battle.Record.ToJson();
        var navigation = new ReplayNavigator(battle.Db, battle.Record);
        Assert.Equal(2, navigation.Count);
        Assert.Equal(Phase.Choose, navigation.Current.Phase);
        int openingEvents = navigation.Current.Events.Count;
        var logs = navigation.FrameLog.ToList();
        navigation.Next();

        Assert.Equal(openingEvents, navigation.FrameEventStart);
        Assert.Contains(navigation.FrameLog, entry => entry.Node == Node.Over);
        logs.AddRange(navigation.FrameLog);
        Assert.Equal(Json(battle.Log), Json(logs));
        SameFinal(battle, navigation);
        navigation.Previous();
        Assert.Equal(0, navigation.Index);
        Assert.Equal(Phase.Choose, navigation.Current.Phase);
        navigation.Next();
        SameFinal(battle, navigation);
        Assert.Equal(source, battle.Record.ToJson());
        Assert.Equal(source, navigation.Record.ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VersionFourBranchBoundaryAndContinuedActionsRetainMetadataAndRandomCheckpoints(bool continueBranch)
    {
        var source = T70_MvpReviewTests.MakeBattle(true, true);
        source.ChooseBan(0, 5);
        source.ChooseBan(1, 5);
        source.ChooseLead(0, 2);
        source.ChooseLead(1, 3);
        source.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(1));
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        source.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        source.Surrender(1);
        string originalSource = source.Record.ToJson();
        var branch = ReplayBranch.Start(source.Db, source.Record, 2, 0, AiLevel.Normal);
        if (continueBranch)
        {
            var computer = Ai.Choose(branch, 1, AiLevel.Normal);
            branch.ResolveTurn(BattleAction.Skill(1), computer);
            branch.Surrender(1);
        }
        string originalBranch = branch.Record.ToJson();
        var navigation = new ReplayNavigator(source.Db, branch.Record);
        Assert.Equal(BattleRecord.BranchVersion, navigation.Record.Version);
        Assert.Equal(continueBranch ? 3 : 2, navigation.Count);
        navigation.Seek(1);
        Assert.True(navigation.Current.IsBranch);
        Assert.Equal(2, navigation.Current.Record.Branch.Turn);
        Assert.Equal(0, navigation.Current.Record.Branch.HumanSide);
        Assert.Equal(branch.Record.Branch.SourceHash, navigation.Current.Record.Branch.SourceHash);
        Assert.Contains(navigation.FrameLog, entry => entry.Kind == "branch");
        navigation.Seek(navigation.Count - 1);
        SameFinal(branch, navigation);
        navigation.Seek(0);
        Assert.False(navigation.Current.IsBranch);
        navigation.Seek(navigation.Count - 1);
        SameFinal(branch, navigation);
        Assert.Equal(originalSource, source.Record.ToJson());
        Assert.Equal(originalBranch, branch.Record.ToJson());
        Assert.Equal(originalBranch, navigation.Record.ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadedRecordAndSameIndexSeekRemainIndependentEvenForASingleTerminalFrame(bool hasTurn)
    {
        var battle = T70_MvpReviewTests.MakeBattle();
        if (hasTurn) battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        battle.Surrender(1);
        var record = BattleRecord.FromJson(battle.Record.ToJson());
        string original = record.ToJson();
        var navigation = new ReplayNavigator(battle.Db, record);
        record.Events.Clear();
        record.Names[0] = "外部改名";
        record.Teams[0][0] = "missing";
        var exposed = navigation.Record;
        exposed.Events.Clear();
        exposed.Names[1] = "另一外部改名";
        navigation.Seek(navigation.Count - 1);
        SameFinal(battle, navigation);
        navigation.Current.Active(0).Hp = 0;
        navigation.Seek(navigation.Index);
        SameFinal(battle, navigation);
        Assert.Equal(original, navigation.Record.ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedStreamerPolicyPrecedesHashMismatchEvenIfTheOriginalSlotWasBanned(bool bannedSlot)
    {
        string content = Path.Combine(AppContext.BaseDirectory, "Content");
        string rules = File.ReadAllText(Path.Combine(content, "StreamDuelRules.json"));
        string pack = File.ReadAllText(Path.Combine(content, "Packs", "original.json"));
        var db = ContentDb.FromJson(rules, pack, PackPolicy.Empty);
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "政策甲", "政策乙" }, new BattleOptions { Seed = 70, BanOne = bannedSlot });
        string removed = battle.Record.Teams[0][bannedSlot ? 5 : 0];
        if (bannedSlot) { battle.ChooseBan(0, 5); battle.ChooseBan(1, 5); }
        battle.Surrender(1);
        var policy = PackPolicy.FromJson(JsonSerializer.Serialize(new
        {
            disabledPacks = Array.Empty<string>(), disabledStreamers = new[] { removed },
            reason = new Dictionary<string, string> { { removed, "验收下架原因" } }
        }));
        var filtered = ContentDb.FromJson(rules, pack, policy);
        string original = battle.Record.ToJson(), filteredHash = filtered.ContentHash();
        Assert.NotEqual(battle.Record.ContentHash, filteredHash);

        var error = Assert.Throws<InvalidOperationException>(() => new ReplayNavigator(filtered, battle.Record));

        Assert.Equal(policy.StreamerReason(removed), error.Message);
        Assert.Contains("该主播已下架", error.Message);
        Assert.Equal(original, battle.Record.ToJson());
        Assert.Equal(filteredHash, filtered.ContentHash());
    }

    [Fact]
    public void FutureRecordVersionIsRejectedBeforeExposingAnOtherwiseValidTimeline()
    {
        var battle = T70_MvpReviewTests.MakeBattle();
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        battle.Surrender(1);
        var record = BattleRecord.FromJson(battle.Record.ToJson());
        record.Version = BattleRecord.CurrentVersion + 1;
        string original = record.ToJson();

        var error = Assert.Throws<InvalidOperationException>(() => new ReplayNavigator(battle.Db, record));

        Assert.Contains("录像来自更新的版本", error.Message);
        Assert.Equal(original, record.ToJson());
    }

    [Fact]
    public void BestEffortUsesCurrentContentButKeepsTheOriginalWarningRecordAndShareExport()
    {
        var battle = T70_MvpReviewTests.MakeBattle();
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(1));
        battle.Surrender(1);
        var record = BattleRecord.FromJson(battle.Record.ToJson());
        string original = record.ToJson(), originalHash = record.ContentHash;
        Assert.Equal(31, BattleSummary.From(battle).Sides[0][0].DamageDealt);
        battle.Db.Skills["full_volume"].Effects[0].N = 47;
        var compatibility = Replay.Check(battle.Db, record);
        Assert.True(compatibility.CanReplay);
        Assert.Contains("内容指纹不同", compatibility.Reason);
        Assert.NotEqual(originalHash, battle.Db.ContentHash());
        Assert.Throws<InvalidOperationException>(() => new ReplayNavigator(battle.Db, record));
        var expected = Replay.RunBestEffort(battle.Db, record);

        var navigation = new ReplayNavigator(battle.Db, record, bestEffort: true);

        Assert.Equal(compatibility.Reason, navigation.CompatibilityWarning);
        Assert.Equal(original, record.ToJson());
        Assert.Equal(original, navigation.Record.ToJson());
        navigation.Seek(navigation.Count - 1);
        SameFinal(expected, navigation);
        Assert.Equal(47, BattleSummary.From(navigation.Current).Sides[0][0].DamageDealt);
        var exported = ShareCode.Decode(ShareCode.Encode(navigation.Record));
        Assert.Equal(originalHash, exported.ContentHash);
        Assert.Equal(original, exported.ToJson());
        var exposed = navigation.Record;
        exposed.ContentHash = null;
        exposed.Events.Clear();
        navigation.Seek(0);
        navigation.Seek(navigation.Count - 1);
        SameFinal(expected, navigation);
        Assert.Equal(original, record.ToJson());
        Assert.Equal(original, navigation.Record.ToJson());
        Assert.Equal(compatibility.Reason, navigation.CompatibilityWarning);
    }

    [Fact]
    public void BestEffortStillRejectsFutureVersionsBeforeIgnoringAChangedFingerprint()
    {
        var battle = T70_MvpReviewTests.MakeBattle();
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        battle.Surrender(1);
        var record = BattleRecord.FromJson(battle.Record.ToJson());
        record.Version = BattleRecord.CurrentVersion + 1;
        record.ContentHash = "historical-content";
        string original = record.ToJson();
        var compatibility = Replay.Check(battle.Db, record);
        Assert.False(compatibility.CanReplay);

        var error = Assert.Throws<InvalidOperationException>(() => new ReplayNavigator(battle.Db, record, bestEffort: true));

        Assert.Equal(compatibility.Reason, error.Message);
        Assert.Contains("录像来自更新的版本", error.Message);
        Assert.Equal(original, record.ToJson());
    }

    [Fact]
    public void BestEffortStillRejectsThePolicyForAnOriginalSlotRemovedByBan()
    {
        var battle = T70_MvpReviewTests.MakeBattle(true, true);
        string removed = battle.Record.Teams[0][5];
        battle.ChooseBan(0, 5);
        battle.ChooseBan(1, 5);
        battle.Surrender(1);
        var policy = PackPolicy.FromJson(JsonSerializer.Serialize(new
        {
            disabledPacks = Array.Empty<string>(), disabledStreamers = new[] { removed },
            reason = new Dictionary<string, string> { { removed, "尽力回放下架验收" } }
        }));
        string content = Path.Combine(AppContext.BaseDirectory, "Content");
        var filtered = ContentDb.FromJson(File.ReadAllText(Path.Combine(content, "StreamDuelRules.json")),
            File.ReadAllText(Path.Combine(content, "Packs", "original.json")), policy);
        string original = battle.Record.ToJson(), contentHash = filtered.ContentHash();
        Assert.DoesNotContain(filtered.Streamers, streamer => streamer.Id == removed);
        Assert.DoesNotContain(battle.Sides[0].Team, fighter => fighter.Def.Id == removed);
        Assert.NotEqual(battle.Record.ContentHash, contentHash);
        var compatibility = Replay.Check(filtered, battle.Record);
        Assert.False(compatibility.CanReplay);

        var error = Assert.Throws<InvalidOperationException>(() => new ReplayNavigator(filtered, battle.Record, bestEffort: true));

        Assert.Equal(compatibility.Reason, error.Message);
        Assert.Equal(policy.StreamerReason(removed), error.Message);
        Assert.Equal(original, battle.Record.ToJson());
        Assert.Equal(contentHash, filtered.ContentHash());
    }
}
