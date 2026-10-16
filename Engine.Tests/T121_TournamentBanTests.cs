using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T121_TournamentBanTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    [Theory]
    [InlineData("roundrobin")]
    [InlineData("swiss")]
    public void ZeroBansPreservesEveryLegacyGameRandomStreamArchiveKeyAndSaveByteForByte(string format)
    {
        using var files = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var defaultTournament = Make(db, format, 4);
        var explicitZero = Make(db, format, 4, 0);
        var firstBattles = new Dictionary<int, Battle>();
        while (!defaultTournament.IsFinished)
        {
            var oldRound = defaultTournament.NextRound();
            var zeroRound = explicitZero.NextRound();
            Assert.Equal(defaultTournament.ToJson(), explicitZero.ToJson());
            for (int index = 0; index < oldRound.Count; index++)
            {
                var pairing = oldRound[index];
                Assert.Equal(-1, defaultTournament.NextBanSide(pairing));
                Assert.Empty(pairing.Bans);
                var legacy = LegacyBattle(db, defaultTournament, pairing);
                var actual = defaultTournament.CreateBattle(pairing);
                var zero = explicitZero.CreateBattle(zeroRound[index]);
                for (int guard = 0; legacy.Phase != Phase.Over && guard < 500; guard++)
                {
                    Advance(legacy);
                    Advance(actual);
                    Advance(zero);
                    Assert.Equal(legacy.StateHash(), actual.StateHash());
                    Assert.Equal(legacy.StateHash(), zero.StateHash());
                    Assert.Equal(legacy.Record.ToJson(), actual.Record.ToJson());
                    Assert.Equal(legacy.Record.ToJson(), zero.Record.ToJson());
                }
                Assert.Equal(Phase.Over, legacy.Phase);
                Assert.Equal(Json(legacy.Log), Json(actual.Log));
                Assert.Equal(Json(legacy.Log), Json(zero.Log));
                double next = legacy.Clone().Random();
                Assert.Equal(next, actual.Clone().Random());
                Assert.Equal(next, zero.Clone().Random());
                Assert.Equal(legacy.Record.ToJson(), zero.Record.ToJson());
                defaultTournament.ReportResult(pairing, legacy.Winner);
                explicitZero.ReportResult(zeroRound[index], legacy.Winner);
                firstBattles[pairing.Id] = zero;
            }
            Assert.Equal(defaultTournament.ToJson(), explicitZero.ToJson());
        }
        string saved = defaultTournament.ToJson();
        Assert.DoesNotContain("banCount", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("\"bans\"", saved, StringComparison.Ordinal);
        Assert.Equal(saved, Tournament.FromJson(db, saved).ToJson());
        Assert.Equal(1, Convert.ToInt32(Object(saved)["version"]));
        var library = files.Library();
        var originalPair = defaultTournament.Pairings[0];
        string id = library.SaveTournament(defaultTournament, originalPair, firstBattles[0]);
        Assert.Equal(id, library.SaveTournament(explicitZero, explicitZero.Pairings[0], firstBattles[0]));
        var metadata = (Dictionary<string, object>)Object(File.ReadAllText(Path.Combine(files.Root, id + ".json")))["library"];
        var association = (Dictionary<string, object>)metadata["tournament"];
        Assert.Equal(LegacyArchiveKey(saved), association["key"]);
    }

    [Theory]
    [InlineData("roundrobin", 1)]
    [InlineData("roundrobin", 2)]
    [InlineData("swiss", 1)]
    [InlineData("swiss", 2)]
    public void EveryBannedIdentityStaysOutOfCompleteGamesAndReplays(string format, int bans)
    {
        var db = BattleTests.LoadPack("original");
        string content = db.ContentHash();
        var tournament = Make(db, format, 4, bans);
        int games = 0;
        while (!tournament.IsFinished)
            foreach (var pairing in tournament.NextRound())
            {
                AutoCompleteBans(tournament, pairing);
                var battle = tournament.CreateBattle(pairing);
                Assert.Equal(Phase.Choose, battle.Phase);
                Assert.Equal(2 * bans, pairing.Bans.Count);
                AssertBanPrefix(tournament, pairing, battle);
                Assert.Equal(new[] { 6 - bans, 6 - bans }, battle.Sides.Select(side => side.Team.Count));
                Finish(battle);
                foreach (var banned in pairing.Bans)
                {
                    int targetSide = 1 - banned.Side;
                    Assert.DoesNotContain(battle.Sides[targetSide].Team, fighter => fighter.Def.Id == banned.StreamerId);
                    Assert.DoesNotContain(battle.Events, entry => entry.Kind == BattleEventKind.Enter
                        && entry.Side == targetSide && entry.Id == banned.StreamerId);
                    Assert.All(battle.Events.Where(entry => entry.Kind == BattleEventKind.Act && entry.Side == targetSide),
                        entry => Assert.NotEqual(banned.StreamerId, battle.Sides[targetSide].Team[entry.Slot].Def.Id));
                }
                var replay = Replay.Run(db, BattleRecord.FromJson(battle.Record.ToJson()));
                Assert.Equal(battle.StateHash(), replay.StateHash());
                Assert.Equal(Json(battle.Log), Json(replay.Log));
                Assert.Equal(battle.Random(), replay.Random());
                tournament.ReportResult(pairing, battle.Winner);
                games++;
            }
        Assert.Equal(6, games);
        Assert.Equal(content, db.ContentHash());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SmallerRegistrationSeedStartsAndSidesAlternateEvenWhenPairingSidesAreReversed(int bans)
    {
        var tournament = Make(BattleTests.LoadPack("original"), "roundrobin", 4, bans);
        bool sawSideOneFirst = false;
        while (!tournament.IsFinished)
            foreach (var pairing in tournament.NextRound())
            {
                int first = pairing.Player0 < pairing.Player1 ? 0 : 1;
                sawSideOneFirst |= first == 1;
                for (int choice = 0; choice < 2 * bans; choice++)
                {
                    int side = choice % 2 == 0 ? first : 1 - first;
                    Assert.Equal(side, tournament.NextBanSide(pairing));
                    var before = tournament.CreateBattle(pairing);
                    int target = before.LegalBanTargets(side).Last();
                    tournament.ChooseBan(pairing, side, target);
                    Assert.Equal(side, pairing.Bans[choice].Side);
                    Assert.Equal(target, pairing.Bans[choice].TargetIndex);
                }
                Assert.Equal(-1, tournament.NextBanSide(pairing));
                tournament.ReportResult(pairing, 1);
            }
        Assert.True(sawSideOneFirst);
    }

    [Fact]
    public void TwoBansReferToOriginalSixSlotsAndRemoveBothOnlyAfterAllChoices()
    {
        var tournament = Make(BattleTests.LoadPack("original"), "roundrobin", 2, 2);
        var pairing = Assert.Single(tournament.NextRound());
        var original = tournament.CreateBattle(pairing);
        Assert.Equal(Phase.Ban, original.Phase);
        Assert.Empty(original.Log);
        int[] sides = { 0, 1, 0, 1 }, targets = { 0, 5, 4, 1 };
        for (int i = 0; i < sides.Length; i++)
        {
            tournament.ChooseBan(pairing, sides[i], targets[i]);
            var current = tournament.CreateBattle(pairing);
            if (i < sides.Length - 1)
            {
                Assert.Equal(Phase.Ban, current.Phase);
                Assert.Equal(new[] { 6, 6 }, current.Sides.Select(side => side.Team.Count));
                Assert.Empty(current.Log);
                Assert.DoesNotContain(targets[i], current.LegalBanTargets(sides[i]));
            }
        }
        var battle = tournament.CreateBattle(pairing);
        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.Equal(original.Record.Teams[0].Where((id, index) => index != 5 && index != 1), battle.Sides[0].Team.Select(f => f.Def.Id));
        Assert.Equal(original.Record.Teams[1].Where((id, index) => index != 0 && index != 4), battle.Sides[1].Team.Select(f => f.Def.Id));
        Assert.All(battle.Sides, side => Assert.Equal(new[] { 1, 2, 3, 4 }, side.Team.Select(f => f.Slot)));
        Assert.Equal(new[] { 6, 6 }, battle.Record.Teams.Select(team => team.Count));
        AssertBanPrefix(tournament, pairing, battle);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 3)]
    [InlineData(2, 4)]
    public void SaveAndResumeAtAnyBanBoundaryPreservesTheAiStreamAndRemainingTournament(int bans, int prefix)
    {
        var db = BattleTests.LoadPack("original");
        var uninterrupted = Make(db, "swiss", 4, bans);
        var pairing = uninterrupted.NextRound()[0];
        for (int i = 0; i < prefix; i++) uninterrupted.AutoChooseBan(pairing);
        string saved = uninterrupted.ToJson();
        var restored = Tournament.FromJson(db, saved);
        Assert.Equal(saved, restored.ToJson());
        Assert.Equal(bans, restored.BanCount);
        Assert.Equal(3, Convert.ToInt32(Object(saved)["version"]));
        Assert.Equal(uninterrupted.NextBanSide(pairing), restored.NextBanSide(restored.Pairings[pairing.Id]));
        Assert.Equal(uninterrupted.CreateBattle(pairing).StateHash(), restored.CreateBattle(restored.Pairings[pairing.Id]).StateHash());
        while (!uninterrupted.IsFinished)
        {
            var left = uninterrupted.NextRound();
            var right = restored.NextRound();
            for (int i = 0; i < left.Count; i++)
            {
                AutoCompleteBans(uninterrupted, left[i]);
                AutoCompleteBans(restored, right[i]);
                var a = uninterrupted.CreateBattle(left[i]);
                var b = restored.CreateBattle(right[i]);
                Assert.Equal(a.StateHash(), b.StateHash());
                Assert.Equal(a.Record.ToJson(), b.Record.ToJson());
                a.Surrender(1);
                b.Surrender(1);
                uninterrupted.ReportResult(left[i], a.Winner);
                restored.ReportResult(right[i], b.Winner);
                Assert.Equal(uninterrupted.ToJson(), restored.ToJson());
                Assert.Equal(a.Random(), b.Random());
            }
            restored = Tournament.FromJson(db, restored.ToJson());
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AutomaticBansUseExistingAiAndPreserveItsRandomDraws(int bans)
    {
        var tournament = Make(BattleTests.LoadPack("original"), "roundrobin", 2, bans);
        var pairing = Assert.Single(tournament.NextRound());
        var independent = tournament.CreateBattle(pairing);
        while (tournament.NextBanSide(pairing) >= 0)
        {
            int side = tournament.NextBanSide(pairing);
            int target = Ai.ChooseBan(independent, side);
            independent.ChooseBan(side, target);
            tournament.AutoChooseBan(pairing);
            Assert.Equal(target, pairing.Bans.Last().TargetIndex);
            var restored = tournament.CreateBattle(pairing);
            Assert.Equal(independent.Record.ToJson(), restored.Record.ToJson());
            Assert.Equal(independent.StateHash(), restored.StateHash());
            Assert.Equal(independent.Clone().Random(), restored.Random());
        }
    }

    [Fact]
    public void InvalidOrRepeatedChoicesAreAtomicAndResultsRequireTheCompleteBanSequence()
    {
        var tournament = Make(BattleTests.LoadPack("original"), "roundrobin", 2, 2);
        var pairing = Assert.Single(tournament.NextRound());
        string before = tournament.ToJson();
        Assert.Throws<ArgumentOutOfRangeException>(() => tournament.ChooseBan(pairing, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tournament.ChooseBan(pairing, 2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tournament.ChooseBan(pairing, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => tournament.ChooseBan(pairing, 0, 6));
        Assert.Throws<InvalidOperationException>(() => tournament.ChooseBan(pairing, 1, 0));
        Assert.Throws<InvalidOperationException>(() => tournament.ReportResult(pairing, 0));
        Assert.Equal(before, tournament.ToJson());
        tournament.ChooseBan(pairing, 0, 0);
        tournament.ChooseBan(pairing, 1, 0);
        before = tournament.ToJson();
        Assert.Throws<InvalidOperationException>(() => tournament.ChooseBan(pairing, 0, 0));
        Assert.Throws<InvalidOperationException>(() => tournament.ReportResult(pairing, -1));
        Assert.Equal(before, tournament.ToJson());
        tournament.ChooseBan(pairing, 0, 1);
        tournament.ChooseBan(pairing, 1, 1);
        before = tournament.ToJson();
        Assert.Throws<InvalidOperationException>(() => tournament.AutoChooseBan(pairing));
        Assert.Throws<InvalidOperationException>(() => tournament.ChooseBan(pairing, 0, 2));
        Assert.Equal(before, tournament.ToJson());
        tournament.ReportResult(pairing, 0);
        Assert.Equal(-1, tournament.NextBanSide(pairing));
        Assert.Equal(Phase.Choose, tournament.CreateBattle(pairing).Phase);
        Assert.Throws<InvalidOperationException>(() => tournament.ChooseBan(pairing, 0, 2));
    }

    [Fact]
    public void ForeignAndByePairingsCannotAcquireChoicesOrBattles()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = Make(db, "roundrobin", 3, 2);
        var round = tournament.NextRound();
        var bye = Assert.Single(round, pairing => pairing.IsBye);
        var foreign = Make(db, "roundrobin", 3, 2).NextRound().First(pairing => !pairing.IsBye);
        string before = tournament.ToJson();
        Assert.Equal(-1, tournament.NextBanSide(bye));
        Assert.Empty(bye.Bans);
        Assert.Throws<InvalidOperationException>(() => tournament.CreateBattle(bye));
        Assert.Throws<InvalidOperationException>(() => tournament.AutoChooseBan(bye));
        Assert.Throws<InvalidOperationException>(() => tournament.ChooseBan(bye, 0, 0));
        Assert.Throws<ArgumentNullException>(() => tournament.CreateBattle(null));
        Assert.Throws<ArgumentException>(() => tournament.CreateBattle(foreign));
        Assert.Throws<ArgumentException>(() => tournament.ChooseBan(foreign, 0, 0));
        Assert.Throws<ArgumentException>(() => tournament.AutoChooseBan(foreign));
        Assert.Throws<ArgumentException>(() => tournament.NextBanSide(foreign));
        Assert.Equal(before, tournament.ToJson());
        Assert.Equal(before, Tournament.FromJson(db, before).ToJson());
    }

    [Fact]
    public void ReturnedBanCollectionsAndCreatedBattleEditsCannotChangeTheTournament()
    {
        var tournament = Make(BattleTests.LoadPack("original"), "roundrobin", 2, 2);
        var pairing = Assert.Single(tournament.NextRound());
        tournament.ChooseBan(pairing, 0, 0);
        string before = tournament.ToJson();
        var bans = Assert.IsAssignableFrom<IList<TournamentBan>>(pairing.Bans);
        Assert.Throws<NotSupportedException>(() => bans.Clear());
        var battle = tournament.CreateBattle(pairing);
        battle.ChooseBan(1, 1);
        battle.Record.Events.Clear();
        battle.Sides[0].Team.Clear();
        Assert.Equal(before, tournament.ToJson());
        var restored = tournament.CreateBattle(pairing);
        Assert.Equal(6, restored.Sides[0].Team.Count);
        Assert.Single(restored.Record.Events);
        Assert.Equal(1, tournament.NextBanSide(pairing));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void InvalidBanCountsAreRejected(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Tournament(BattleTests.LoadPack("original"), "roundrobin", 0, count));
    }

    [Fact]
    public void MalformedSavedBanConfigurationAndSequenceAreRejectedWithoutChangingTheSource()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = Make(db, "roundrobin", 2, 2);
        var pairing = Assert.Single(tournament.NextRound());
        foreach (var choice in new[] { new[] { 0, 0 }, new[] { 1, 1 }, new[] { 0, 2 } })
            tournament.ChooseBan(pairing, choice[0], choice[1]);
        string valid = tournament.ToJson();
        var changes = new Action<Dictionary<string, object>>[]
        {
            root => root["banCount"] = -1,
            root => root["banCount"] = 3,
            root => root["banCount"] = 1.5,
            root => root["banCount"] = "2",
            root => root["banCount"] = null,
            root => root.Remove("banCount"),
            root => root["version"] = 1,
            root => root["version"] = 2,
            root => SavedPair(root)["bans"] = null,
            root => SavedPair(root)["bans"] = new Dictionary<string, object>(),
            root => SavedBans(root)[0]["side"] = 1,
            root => SavedBans(root)[1]["side"] = 0,
            root => SavedBans(root)[0]["targetIndex"] = 6,
            root => SavedBans(root)[0]["targetIndex"] = 0.5,
            root => SavedBans(root)[0]["streamerId"] = "not_in_the_team",
            root =>
            {
                SavedBans(root)[2]["targetIndex"] = 0;
                SavedBans(root)[2]["streamerId"] = SavedBans(root)[0]["streamerId"];
            },
            root => SavedBans(root)[0]["rng"] = -1,
            root => SavedBans(root)[0]["rng"] = 4294967296L,
            root => SavedBans(root)[0].Remove("rng"),
            root => SavedPair(root)["winnerSide"] = 0
        };
        foreach (var change in changes)
        {
            var source = Object(valid);
            change(source);
            var error = Assert.Throws<FormatException>(() => Tournament.FromJson(db, Json(source)));
            Assert.Matches("[\u3400-\u9fff]", error.Message);
        }
        Assert.Equal(valid, tournament.ToJson());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ReportsUseSavedBanIdentitiesWithOrWithoutAReplayAndRemainReadOnly(int bans)
    {
        using var files = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var tournament = Make(db, "roundrobin", 2, bans);
        var pairing = Assert.Single(tournament.NextRound());
        AutoCompleteBans(tournament, pairing);
        var battle = tournament.CreateBattle(pairing);
        battle.Surrender(1);
        tournament.ReportResult(pairing, battle.Winner);
        var missing = TournamentReport.Build(tournament, null);
        AssertReportBans(tournament, pairing, missing);
        Assert.Contains("未保存", missing.ToText());
        var library = files.Library();
        string replayId = library.SaveTournament(tournament, pairing, battle);
        string saved = tournament.ToJson();
        var before = Files(files.Root);

        var report = TournamentReport.Build(tournament, ReplayLibrary.OpenReadOnly(files.Root, PackPolicy.Empty));

        AssertReportBans(tournament, pairing, report);
        using var document = JsonDocument.Parse(report.ToJson());
        var row = MatchRow(document.RootElement, pairing.Id);
        Assert.Equal("saved", row.GetProperty("replay").GetProperty("status").GetString());
        Assert.Equal(replayId, row.GetProperty("replay").GetProperty("id").GetString());
        Assert.Equal(report.ToJson(), TournamentReport.Build(Tournament.FromJson(db, saved), library).ToJson());
        Assert.Equal(saved, tournament.ToJson());
        AssertFilesEqual(before, files.Root);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("order")]
    [InlineData("rng")]
    [InlineData("count")]
    public void AValidSameWinnerReplayWithDifferentBansCannotImpersonateTheSavedMatch(string change)
    {
        using var files = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var tournament = Make(db, "roundrobin", 2, 2);
        var pairing = Assert.Single(tournament.NextRound());
        foreach (var choice in new[] { new[] { 0, 0 }, new[] { 1, 0 }, new[] { 0, 1 }, new[] { 1, 1 } })
            tournament.ChooseBan(pairing, choice[0], choice[1]);
        var genuine = tournament.CreateBattle(pairing);
        genuine.Surrender(1);
        tournament.ReportResult(pairing, genuine.Winner);
        var library = files.Library();
        string id = library.SaveTournament(tournament, pairing, genuine);
        Battle forged;
        if (change == "count")
        {
            var wrongCount = Make(db, "roundrobin", 2, 1);
            var wrongPair = Assert.Single(wrongCount.NextRound());
            wrongCount.ChooseBan(wrongPair, 0, 0);
            wrongCount.ChooseBan(wrongPair, 1, 0);
            forged = wrongCount.CreateBattle(wrongPair);
            forged.Surrender(1);
        }
        else
        {
            var record = BattleRecord.FromJson(genuine.Record.ToJson());
            if (change == "target") record.Events[0].To = 2;
            else if (change == "rng") record.Events[0].RngState = unchecked(record.Events[0].RngState.Value + 1);
            else
            {
                var first = record.Events[0];
                record.Events[0] = record.Events[1];
                record.Events[1] = first;
            }
            forged = Replay.Run(db, record);
        }
        Assert.Equal(Phase.Over, forged.Phase);
        Assert.Equal(genuine.Winner, forged.Winner);
        using var emptyFiles = new TestDirectory();
        var emptyLibrary = emptyFiles.Library();
        var emptyBefore = Files(emptyFiles.Root);
        Assert.Throws<InvalidOperationException>(() => emptyLibrary.SaveTournament(tournament, pairing, forged));
        AssertFilesEqual(emptyBefore, emptyFiles.Root);
        Assert.Throws<InvalidOperationException>(() => library.SaveTournament(tournament, pairing, forged));
        string path = Path.Combine(files.Root, id + ".json");
        var archived = Object(File.ReadAllText(path));
        var substituted = Object(forged.Record.ToJson());
        substituted["library"] = archived["library"];
        File.WriteAllText(path, Json(substituted), new UTF8Encoding(false));
        var before = Files(files.Root);

        var report = TournamentReport.Build(tournament, ReplayLibrary.OpenReadOnly(files.Root, PackPolicy.Empty));

        AssertReportBans(tournament, pairing, report);
        using var document = JsonDocument.Parse(report.ToJson());
        var root = document.RootElement;
        Assert.Equal("mismatch", MatchRow(root, pairing.Id).GetProperty("replay").GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("coverage").GetProperty("archivedGames").GetInt32());
        Assert.Equal(1, root.GetProperty("coverage").GetProperty("invalidGames").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("maxDamage").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("mostAppearances").ValueKind);
        AssertFilesEqual(before, files.Root);
    }

    [Fact]
    public void BanConfigurationSeparatesArchiveAssociationsEvenWhenRegistrationIsIdentical()
    {
        using var files = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var one = Make(db, "roundrobin", 2, 1);
        var two = Make(db, "roundrobin", 2, 2);
        var first = Assert.Single(one.NextRound());
        var second = Assert.Single(two.NextRound());
        AutoCompleteBans(one, first);
        AutoCompleteBans(two, second);
        var battle = one.CreateBattle(first);
        battle.Surrender(1);
        one.ReportResult(first, battle.Winner);
        two.ReportResult(second, battle.Winner);
        var library = files.Library();
        library.SaveTournament(one, first, battle);
        using var report = JsonDocument.Parse(TournamentReport.Build(two, library).ToJson());
        Assert.Equal("not_saved", MatchRow(report.RootElement, second.Id).GetProperty("replay").GetProperty("status").GetString());
    }

    [Fact]
    public void ACompletedBattleWithANullRecordEventIsRejectedInChineseBeforeWritingAnArchive()
    {
        using var files = new TestDirectory();
        var tournament = Make(BattleTests.LoadPack("original"), "roundrobin", 2, 1);
        var pairing = Assert.Single(tournament.NextRound());
        AutoCompleteBans(tournament, pairing);
        var battle = tournament.CreateBattle(pairing);
        battle.Surrender(1);
        Assert.Equal(Phase.Over, battle.Phase);
        tournament.ReportResult(pairing, battle.Winner);
        var library = files.Library();
        var before = Files(files.Root);
        string saved = tournament.ToJson();
        battle.Record.Events.Add(null);

        var error = Assert.Throws<InvalidOperationException>(() => library.SaveTournament(tournament, pairing, battle));

        Assert.Matches("[\u3400-\u9fff]", error.Message);
        Assert.Equal(saved, tournament.ToJson());
        Assert.Null(battle.Record.Events.Last());
        AssertFilesEqual(before, files.Root);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void DoubleBanCloneKeepsPartialChoicesAndCanFinishWithoutChangingItsSource(int prefix)
    {
        var tournament = Make(BattleTests.LoadPack("original"), "roundrobin", 2, 2);
        var pairing = Assert.Single(tournament.NextRound());
        int[][] choices = { new[] { 0, 0 }, new[] { 1, 0 }, new[] { 0, 1 }, new[] { 1, 1 } };
        for (int i = 0; i < prefix; i++) tournament.ChooseBan(pairing, choices[i][0], choices[i][1]);
        var source = tournament.CreateBattle(pairing);
        string record = source.Record.ToJson();
        ulong hash = source.StateHash();
        var clone = source.Clone();
        Assert.Equal(2, clone.BanCount);
        Assert.Equal(hash, clone.StateHash());
        Assert.Equal(record, clone.Record.ToJson());
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(source.HasChosenBan(side), clone.HasChosenBan(side));
            Assert.Equal(source.LegalBanTargets(side), clone.LegalBanTargets(side));
        }
        for (int i = prefix; i < choices.Length; i++) clone.ChooseBan(choices[i][0], choices[i][1]);
        Assert.Equal(Phase.Choose, clone.Phase);
        Assert.Equal(Phase.Ban, source.Phase);
        Assert.Equal(record, source.Record.ToJson());
        Assert.Equal(hash, source.StateHash());
        Assert.All(source.Sides, side => Assert.Equal(6, side.Team.Count));
        for (int i = prefix; i < choices.Length; i++) source.ChooseBan(choices[i][0], choices[i][1]);
        Assert.Equal(source.StateHash(), clone.StateHash());
        Assert.Equal(source.Record.ToJson(), clone.Record.ToJson());
        Assert.Equal(Json(source.Log), Json(clone.Log));
        Assert.Equal(source.Random(), clone.Random());
    }

    [Fact]
    public void DoubleBanReplayCursorIncludesBansAtPositionZeroAndRestoresThemAfterSeeking()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = Make(db, "roundrobin", 2, 2);
        var pairing = Assert.Single(tournament.NextRound());
        AutoCompleteBans(tournament, pairing);
        var battle = tournament.CreateBattle(pairing);
        var snapshots = new List<Battle> { battle.Clone() };
        for (int turn = 0; turn < 3; turn++)
        {
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            snapshots.Add(battle.Clone());
        }
        battle.Surrender(1);
        snapshots[3] = battle.Clone();
        string record = battle.Record.ToJson();
        var cursor = new ReplayCursor(db, BattleRecord.FromJson(record), 2);
        Assert.Equal(3, cursor.MaxTurn);
        foreach (int position in new[] { 0, 3, 1, 2, 0, 3 })
        {
            cursor.Seek(position);
            var expected = snapshots[position];
            Assert.Equal(2, cursor.Current.BanCount);
            Assert.Equal(expected.StateHash(), cursor.Current.StateHash());
            Assert.Equal(Json(expected.Log), Json(cursor.Current.Log));
            Assert.Equal(expected.Record.ToJson(), cursor.Current.Record.ToJson());
            Assert.Equal(expected.Clone().Random(), cursor.Current.Clone().Random());
            Assert.All(cursor.Current.Sides, side => Assert.Equal(4, side.Team.Count));
        }
        cursor.Seek(0);
        Assert.True(cursor.Next());
        Assert.Equal(snapshots[1].StateHash(), cursor.Current.StateHash());
        Assert.Equal(record, battle.Record.ToJson());
    }

    [Fact]
    public void UnsupportedHotseatAndSpectatorDoubleBansAreRejectedWithoutChangingTheBattle()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = Make(db, "roundrobin", 2, 2);
        var pairing = Assert.Single(tournament.NextRound());
        var battle = tournament.CreateBattle(pairing);
        string record = battle.Record.ToJson(), content = db.ContentHash();
        ulong hash = battle.StateHash();
        double next = battle.Clone().Random();
        var spectatorError = Assert.Throws<InvalidOperationException>(() => new SpectatorFeed(battle, 0));
        Assert.Contains("禁用两人", spectatorError.Message);
        var hotseatError = Assert.Throws<ArgumentException>(() => new HotseatSession(db,
            tournament.Players[0].Team.ToArray(), tournament.Players[1].Team.ToArray(), "甲", "乙",
            new BattleOptions { Seed = 121, BanCount = 2 }));
        Assert.Contains("禁用两人", hotseatError.Message);
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(content, db.ContentHash());
        Assert.Equal(next, battle.Random());
    }

    [Theory]
    [InlineData("single", 1, false)]
    [InlineData("single", 1, true)]
    [InlineData("single", 2, false)]
    [InlineData("single", 2, true)]
    [InlineData("double", 1, false)]
    [InlineData("double", 1, true)]
    [InlineData("double", 2, false)]
    [InlineData("double", 2, true)]
    public void EliminationBansFollowEffectiveSeedsAcrossResumeByesFinalsAndArchivedReports(string format, int bans, bool shuffle)
    {
        using var files = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var options = shuffle ? new TournamentOptions { Seed = 115 }
            : new TournamentOptions { SeedOrder = new[] { 4, 3, 2, 1, 0 } };
        var continuous = Make(db, format, 5, bans, options);
        int[] seeds = ExpectedSeedOrder(continuous);
        var resumed = Tournament.FromJson(db, continuous.ToJson());
        var library = files.Library();
        var archives = new Dictionary<int, string>();
        bool differsFromRegistration = false;
        while (!continuous.IsFinished)
        {
            Assert.True(continuous.Round < 16, "有限淘汰赛必须结束。");
            var leftRound = continuous.NextRound();
            resumed.NextRound();
            Assert.Equal(continuous.ToJson(), resumed.ToJson());
            foreach (var pairing in leftRound)
            {
                if (pairing.IsBye)
                {
                    Assert.Empty(pairing.Bans);
                    Assert.Equal(-1, continuous.NextBanSide(pairing));
                    Assert.Empty(resumed.Pairings[pairing.Id].Bans);
                    continue;
                }
                int first = Array.IndexOf(seeds, pairing.Player0) < Array.IndexOf(seeds, pairing.Player1) ? 0 : 1;
                differsFromRegistration |= first != (pairing.Player0 < pairing.Player1 ? 0 : 1);
                for (int choice = 0; choice < 2 * bans; choice++)
                {
                    var restoredPair = resumed.Pairings[pairing.Id];
                    int side = first ^ (choice % 2);
                    Assert.Equal(side, continuous.NextBanSide(pairing));
                    Assert.Equal(side, resumed.NextBanSide(restoredPair));
                    if (choice % 2 == 0)
                    {
                        continuous.AutoChooseBan(pairing);
                        resumed.AutoChooseBan(restoredPair);
                    }
                    else
                    {
                        int target = continuous.CreateBattle(pairing).LegalBanTargets(side).Last();
                        continuous.ChooseBan(pairing, side, target);
                        resumed.ChooseBan(restoredPair, side, target);
                    }
                    Assert.Equal(side, pairing.Bans[choice].Side);
                    string checkpoint = resumed.ToJson();
                    Assert.Equal(3, Convert.ToInt32(Object(checkpoint)["version"]));
                    Assert.Equal(continuous.ToJson(), checkpoint);
                    resumed = Tournament.FromJson(db, checkpoint);
                    Assert.Equal(checkpoint, resumed.ToJson());
                    var a = continuous.CreateBattle(pairing);
                    var b = resumed.CreateBattle(resumed.Pairings[pairing.Id]);
                    Assert.Equal(a.StateHash(), b.StateHash());
                    Assert.Equal(a.Record.ToJson(), b.Record.ToJson());
                    Assert.Equal(a.Clone().Random(), b.Clone().Random());
                }
                var originalBattle = continuous.CreateBattle(pairing);
                var resumedPair = resumed.Pairings[pairing.Id];
                var resumedBattle = resumed.CreateBattle(resumedPair);
                AssertBanPrefix(continuous, pairing, originalBattle);
                Assert.All(originalBattle.Sides, side => Assert.Equal(6 - bans, side.Team.Count));
                foreach (var ban in pairing.Bans)
                    Assert.DoesNotContain(originalBattle.Sides[1 - ban.Side].Team, fighter => fighter.Def.Id == ban.StreamerId);
                int winner = EliminationWinner(pairing);
                originalBattle.Surrender(1 - winner);
                resumedBattle.Surrender(1 - winner);
                Assert.Equal(originalBattle.StateHash(), resumedBattle.StateHash());
                Assert.Equal(originalBattle.Record.ToJson(), resumedBattle.Record.ToJson());
                archives[pairing.Id] = library.SaveTournament(resumed, resumedPair, resumedBattle);
                continuous.ReportResult(pairing, winner);
                resumed.ReportResult(resumedPair, winner);
                resumed = Tournament.FromJson(db, resumed.ToJson());
                Assert.Equal(continuous.ToJson(), resumed.ToJson());
            }
        }
        Assert.True(differsFromRegistration, "夹具须能区分有效种子位置与报名编号。");
        Assert.Equal(format == "single" ? 4 : 9, archives.Count);
        if (format == "double") Assert.Single(resumed.Pairings, pairing => pairing.Bracket == "reset");
        string saved = resumed.ToJson();
        var before = Files(files.Root);
        var report = TournamentReport.Build(resumed, ReplayLibrary.OpenReadOnly(files.Root, PackPolicy.Empty));
        using var parsed = JsonDocument.Parse(report.ToJson());
        var root = parsed.RootElement;
        Assert.Equal(resumed.Champion.Value, root.GetProperty("champion").GetInt32());
        Assert.Equal(archives.Count, root.GetProperty("coverage").GetProperty("archivedGames").GetInt32());
        Assert.Equal(0, root.GetProperty("coverage").GetProperty("invalidGames").GetInt32());
        foreach (var pairing in resumed.Pairings)
        {
            var row = MatchRow(root, pairing.Id);
            Assert.Equal(pairing.Bracket, row.GetProperty("bracket").GetString());
            Assert.Equal(pairing.BracketRound, row.GetProperty("bracketRound").GetInt32());
            if (pairing.IsBye)
            {
                Assert.Empty(row.GetProperty("bans").EnumerateArray());
                Assert.Equal("not_applicable", row.GetProperty("replay").GetProperty("status").GetString());
            }
            else
            {
                AssertReportBans(resumed, pairing, report);
                Assert.Equal("saved", row.GetProperty("replay").GetProperty("status").GetString());
                Assert.Equal(archives[pairing.Id], row.GetProperty("replay").GetProperty("id").GetString());
            }
        }
        Assert.Equal(report.ToJson(), TournamentReport.Build(continuous, library).ToJson());
        Assert.Equal(saved, resumed.ToJson());
        AssertFilesEqual(before, files.Root);
    }

    [Theory]
    [InlineData("single")]
    [InlineData("double")]
    public void EliminationWithZeroBansKeepsVersionTwoAndTheExistingArchiveIdentity(string format)
    {
        using var files = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var options = new TournamentOptions { Seed = 115 };
        var tournament = Make(db, format, 5, options: options);
        var explicitZero = Make(db, format, 5, 0, options);
        var library = files.Library();
        var archives = new Dictionary<int, string>();
        while (!tournament.IsFinished)
        {
            Assert.True(tournament.Round < 16);
            var round = tournament.NextRound();
            explicitZero.NextRound();
            foreach (var pairing in round.Where(item => !item.IsBye))
            {
                var legacy = LegacyBattle(db, tournament, pairing);
                var actual = tournament.CreateBattle(pairing);
                Assert.Equal(legacy.StateHash(), actual.StateHash());
                Assert.Equal(legacy.Record.ToJson(), actual.Record.ToJson());
                legacy.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
                actual.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
                int winner = EliminationWinner(pairing);
                legacy.Surrender(1 - winner);
                actual.Surrender(1 - winner);
                Assert.Equal(legacy.StateHash(), actual.StateHash());
                Assert.Equal(legacy.Record.ToJson(), actual.Record.ToJson());
                Assert.Equal(Json(legacy.Log), Json(actual.Log));
                Assert.Equal(legacy.Clone().Random(), actual.Clone().Random());
                Assert.Empty(pairing.Bans);
                Assert.Equal(-1, tournament.NextBanSide(pairing));
                tournament.ReportResult(pairing, winner);
                explicitZero.ReportResult(explicitZero.Pairings[pairing.Id], winner);
                string id = library.SaveTournament(tournament, pairing, actual);
                archives[pairing.Id] = id;
                Assert.Equal(id, library.SaveTournament(explicitZero, explicitZero.Pairings[pairing.Id], actual));
            }
            Assert.Equal(tournament.ToJson(), explicitZero.ToJson());
        }
        string saved = tournament.ToJson();
        Assert.Equal(2, Convert.ToInt32(Object(saved)["version"]));
        Assert.DoesNotContain("banCount", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("\"bans\"", saved, StringComparison.Ordinal);
        Assert.Equal(saved, Tournament.FromJson(db, saved).ToJson());
        foreach (string id in archives.Values)
        {
            var metadata = (Dictionary<string, object>)Object(File.ReadAllText(Path.Combine(files.Root, id + ".json")))["library"];
            Assert.Equal(LegacyArchiveKey(saved), ((Dictionary<string, object>)metadata["tournament"])["key"]);
        }
        string report = TournamentReport.Build(tournament, library).ToJson();
        Assert.DoesNotContain("banCount", report, StringComparison.Ordinal);
        Assert.DoesNotContain("\"bans\"", report, StringComparison.Ordinal);
        Assert.Equal(report, TournamentReport.Build(Tournament.FromJson(db, saved), library).ToJson());
    }

    static Tournament Make(ContentDb db, string format, int players, int? bans = null, TournamentOptions options = null)
    {
        int rounds = format == "swiss" ? 3 : 0;
        Tournament tournament;
        if (options != null)
            tournament = bans.HasValue ? new Tournament(db, format, rounds, options, bans.Value)
                : new Tournament(db, format, rounds, options);
        else
            tournament = bans.HasValue ? new Tournament(db, format, rounds, bans.Value)
                : new Tournament(db, format, rounds);
        for (int player = 0; player < players; player++)
        {
            var team = Enumerable.Range(0, 6).Select(slot => db.Streamers[(player * 2 + slot) % db.Streamers.Count].Id).ToList();
            tournament.AddPlayer("选手" + player, TeamCode.Encode(new TeamPreset { Name = "赛事队伍", PackId = db.PackId, Ids = team }));
        }
        return tournament;
    }

    static Battle LegacyBattle(ContentDb db, Tournament tournament, Pairing pairing)
    {
        var a = tournament.Players[pairing.Player0];
        var b = tournament.Players[pairing.Player1];
        return new Battle(db, new IList<string>[] { a.Team.ToArray(), b.Team.ToArray() }, new[] { a.Name, b.Name },
            unchecked(0x9E3779B9u + (uint)pairing.Id));
    }

    static void AutoCompleteBans(Tournament tournament, Pairing pairing)
    {
        for (int guard = 0; tournament.NextBanSide(pairing) >= 0 && guard < 4; guard++) tournament.AutoChooseBan(pairing);
        Assert.Equal(-1, tournament.NextBanSide(pairing));
    }

    static void Advance(Battle battle)
    {
        if (battle.Phase == Phase.Choose)
        {
            battle.ResolveTurn(Ai.Choose(battle, 0, AiLevel.Normal), Ai.Choose(battle, 1, AiLevel.Normal));
        }
        else if (battle.Phase == Phase.Replace)
        {
            for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, AiLevel.Normal));
        }
        else throw new InvalidOperationException("驱动只处理已结束禁用的普通对战。");
    }

    static void Finish(Battle battle)
    {
        for (int guard = 0; battle.Phase != Phase.Over && guard < 500; guard++) Advance(battle);
        Assert.Equal(Phase.Over, battle.Phase);
    }

    static void AssertBanPrefix(Tournament tournament, Pairing pairing, Battle battle)
    {
        var prefix = battle.Record.Events.Where(entry => entry.Type == BattleRecordEventType.Ban).ToArray();
        Assert.Equal(pairing.Bans.Count, prefix.Length);
        Assert.Equal(pairing.Bans.Count, battle.Record.Events.TakeWhile(entry => entry.Type == BattleRecordEventType.Ban).Count());
        for (int i = 0; i < prefix.Length; i++)
        {
            var choice = pairing.Bans[i];
            Assert.Equal(choice.Side, prefix[i].Side);
            Assert.Equal(choice.TargetIndex, prefix[i].To);
            Assert.Equal(choice.RngState, prefix[i].RngState);
            Assert.Equal(battle.Record.Teams[1 - choice.Side][choice.TargetIndex], choice.StreamerId);
            int[] seeds = ExpectedSeedOrder(tournament);
            int first = Array.IndexOf(seeds, pairing.Player0) < Array.IndexOf(seeds, pairing.Player1) ? 0 : 1;
            Assert.Equal(first ^ (i % 2), choice.Side);
        }
        Assert.Equal(unchecked(0x9E3779B9u + (uint)pairing.Id), battle.Seed);
        Assert.Equal(tournament.BanCount * 2, prefix.Length);
    }

    static void AssertReportBans(Tournament tournament, Pairing pairing, TournamentReport report)
    {
        using var document = JsonDocument.Parse(report.ToJson());
        Assert.Equal(tournament.BanCount, document.RootElement.GetProperty("banCount").GetInt32());
        var bans = MatchRow(document.RootElement, pairing.Id).GetProperty("bans").EnumerateArray().ToArray();
        Assert.Equal(pairing.Bans.Count, bans.Length);
        Assert.Contains("本场禁用：", report.ToText());
        var battle = tournament.CreateBattle(pairing);
        for (int i = 0; i < bans.Length; i++)
        {
            var expected = pairing.Bans[i];
            Assert.Equal(expected.Side, bans[i].GetProperty("side").GetInt32());
            Assert.Equal(expected.TargetIndex, bans[i].GetProperty("targetIndex").GetInt32());
            Assert.Equal(expected.StreamerId, bans[i].GetProperty("streamerId").GetString());
            Assert.Equal(expected.RngState, bans[i].GetProperty("rng").GetUInt32());
            string name = battle.Db.Streamer(expected.StreamerId).Name;
            Assert.Equal(name, bans[i].GetProperty("streamerName").GetString());
            Assert.Contains(name, report.ToText());
        }
    }

    static JsonElement MatchRow(JsonElement root, int id) => root.GetProperty("rounds").EnumerateArray()
        .SelectMany(round => round.GetProperty("matches").EnumerateArray()).Single(row => row.GetProperty("pairingId").GetInt32() == id);
    static Dictionary<string, object> Object(string json) => (Dictionary<string, object>)MiniJson.Parse(json);
    static Dictionary<string, object> SavedPair(Dictionary<string, object> root) =>
        (Dictionary<string, object>)((List<object>)((List<object>)root["roundsPlayed"])[0])[0];
    static Dictionary<string, object>[] SavedBans(Dictionary<string, object> root) =>
        ((List<object>)SavedPair(root)["bans"]).Cast<Dictionary<string, object>>().ToArray();
    static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    static string LegacyArchiveKey(string json)
    {
        var source = Object(json);
        bool elimination = (string)source["format"] == "single" || (string)source["format"] == "double";
        var config = new Dictionary<string, object>
        {
            ["version"] = elimination ? 2 : 1, ["format"] = source["format"]
        };
        if (elimination)
        {
            config["seed"] = source["seed"];
            config["seedOrder"] = source["seedOrder"];
            config["resetFinal"] = source["resetFinal"];
        }
        config["rounds"] = source["rounds"];
        config["packId"] = source["packId"];
        config["contentHash"] = source["contentHash"];
        config["players"] = source["players"];
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        string domain = elimination ? "StreamDuel.TournamentArchive.v2\n" : "StreamDuel.TournamentArchive.v1\n";
        byte[] bytes = Encoding.UTF8.GetBytes(domain + JsonSerializer.Serialize(config, options));
        using var hash = SHA256.Create();
        return string.Concat(hash.ComputeHash(bytes).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    }

    static int[] ExpectedSeedOrder(Tournament tournament)
    {
        if (tournament.IsElimination && tournament.SeedOrder != null) return tournament.SeedOrder.ToArray();
        var order = Enumerable.Range(0, tournament.Players.Count).ToArray();
        if (tournament.IsElimination && tournament.Seed.HasValue)
        {
            var random = new Rng(tournament.Seed.Value);
            for (int index = order.Length - 1; index > 0; index--)
            {
                int other = (int)(random.Next() * (index + 1));
                int old = order[index];
                order[index] = order[other];
                order[other] = old;
            }
        }
        return order;
    }

    static int EliminationWinner(Pairing pairing) => pairing.Bracket == "final" ? 1
        : pairing.Bracket == "reset" ? 0 : (pairing.Id + pairing.Player0) % 2;

    static Dictionary<string, byte[]> Files(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes, StringComparer.Ordinal);
    static void AssertFilesEqual(Dictionary<string, byte[]> expected, string root)
    {
        var actual = Files(root);
        Assert.Equal(expected.Keys.OrderBy(key => key, StringComparer.Ordinal), actual.Keys.OrderBy(key => key, StringComparer.Ordinal));
        foreach (var pair in expected) Assert.Equal(pair.Value, actual[pair.Key]);
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = "T121-" + Guid.NewGuid().ToString("N");
        public string Root { get; }
        public TestDirectory([CallerFilePath] string source = "")
        {
            parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "obj", "test-temp"));
            Root = Path.Combine(parent, name);
            Directory.CreateDirectory(Root);
        }
        public ReplayLibrary Library() => new ReplayLibrary(Root, clock: () => new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero), policy: PackPolicy.Empty);
        public void Dispose()
        {
            string target = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(target) != parent || Path.GetFileName(target) != name)
                throw new InvalidOperationException("只能清理当前测试创建的目录。");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }
}
