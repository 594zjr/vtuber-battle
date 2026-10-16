using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T127_RatingIntegrationReviewTests
{
    static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 10, 16, 18, 0, 0, TimeSpan.Zero);
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly int[] Ratings = { 1500, 1800, 1800, 2200, 1800 };
    static readonly int[] RatingGames = { 1, 4, 4, 2, 9 };

    [Theory]
    [InlineData("single", 3, 5)]
    [InlineData("single", 5, 5)]
    [InlineData("double", 3, 2)]
    [InlineData("double", 5, 2)]
    [InlineData("roundrobin", 3, 3)]
    [InlineData("roundrobin", 5, 3)]
    public void RatedSeriesKeepFrozenSeedsAndResumeEveryGameThroughTheWholeTournament(string format, int bestOf, int count)
    {
        var db = BattleTests.LoadPack("original");
        var profiles = Enumerable.Range(0, count).Select(player => ProfileOf(Ratings[player], RatingGames[player])).ToArray();
        var tournament = Make(db, format, bestOf, profiles: profiles);
        int[] order = count == 5 ? new[] { 3, 4, 1, 2, 0 } : count == 3 ? new[] { 1, 2, 0 } : new[] { 1, 0 };
        Assert.Equal(order, tournament.EffectiveSeedOrder);
        Assert.Equal(Ratings.Take(count), tournament.RatingSeeds.Select(item => item.Rating));
        Assert.Equal(RatingGames.Take(count), tournament.RatingSeeds.Select(item => item.Games));
        Assert.All(tournament.RatingSeeds, item => Assert.Equal(1, item.Season));
        string[] snapshots = SnapshotRows(tournament);
        foreach (var profile in profiles)
        {
            RatingSystem.NewSeason(profile.Ratings[Profile.DefaultRatingFormat]);
            profile.Ratings.Clear();
        }
        Assert.Equal(order, tournament.EffectiveSeedOrder);
        Assert.Equal(snapshots, SnapshotRows(tournament));
        var opening = tournament.NextRound();
        Assert.Equal(format == "single" ? 3 : format == "roundrobin" ? 1 : 0, opening.Count(pair => pair.IsBye));
        int drawPairing = opening.First(pair => !pair.IsBye).Id;
        int drawnGameCount = format == "roundrobin" ? bestOf : bestOf + 3;
        int guard = 0;
        while (!tournament.IsFinished)
        {
            Assert.True(guard++ < 100, "逐局赛事必须在有限局数内结束。");
            var pairing = tournament.NextRound().First(pair => !pair.IsBye && !pair.WinnerSide.HasValue);
            int attempt = pairing.Games.Count;
            int winner = pairing.Id == drawPairing ? -1 : 0;
            var battle = Finish(tournament.CreateGame(pairing), winner);
            tournament.ReportGame(pairing, battle);
            Assert.Equal(attempt + 1, pairing.Games.Count);
            Assert.Equal(Hash(battle.Record.ToJson()), pairing.Games[attempt].RecordHash);
            Assert.Equal(battle.Seed, pairing.Games[attempt].Seed);
            if (pairing.Id == drawPairing)
            {
                Assert.Equal(attempt == drawnGameCount - 1, pairing.WinnerSide.HasValue);
                Assert.Equal(format != "roundrobin" && attempt == drawnGameCount - 1, pairing.AdvancedBySeed);
                if (pairing.WinnerSide.HasValue)
                {
                    int expected = format == "roundrobin" ? -1
                        : Array.IndexOf(order, pairing.Player0) < Array.IndexOf(order, pairing.Player1) ? 0 : 1;
                    Assert.Equal(expected, pairing.WinnerSide);
                }
            }
            tournament = Resume(db, tournament, bestOf, order, snapshots);
        }
        var drawn = tournament.Pairings.Single(pair => pair.Id == drawPairing);
        Assert.Equal(drawnGameCount, drawn.Games.Count);
        Assert.All(drawn.Games, game => Assert.Equal(-1, game.WinnerSide));
        Assert.Equal(format != "roundrobin", drawn.AdvancedBySeed);
        Assert.All(tournament.Pairings.Where(pair => pair.IsBye), pair => Assert.Empty(pair.Games));
        Assert.All(tournament.Pairings.Where(pair => !pair.IsBye && pair.Id != drawPairing), pair =>
        {
            Assert.Equal(bestOf / 2 + 1, pair.Games.Count);
            Assert.False(pair.AdvancedBySeed);
        });
        Assert.Empty(tournament.NextRound());
        if (format != "roundrobin") Assert.NotNull(tournament.Champion);
        else
        {
            Assert.Equal(3, tournament.Pairings.Count(pair => !pair.IsBye));
            Assert.All(tournament.Standings(), row => Assert.Equal(
                row.Player == drawn.Player0 || row.Player == drawn.Player1 ? 1 : 0, row.Draws));
            Assert.Equal(2, tournament.Standings().Sum(row => row.Draws));
        }
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(5, 0)]
    [InlineData(5, 1)]
    public void RatedRoundRobinAwardsTheLeadingSideOnlyAfterTheFullGameQuotaWithoutOvertime(int bestOf, int winner)
    {
        var db = BattleTests.LoadPack("original");
        var tournament = Make(db, "roundrobin", bestOf);
        var pairing = Assert.Single(tournament.NextRound());
        int[] order = tournament.EffectiveSeedOrder.ToArray();
        string[] snapshots = SnapshotRows(tournament);
        int winnerPlayer = winner == 0 ? pairing.Player0 : pairing.Player1;
        for (int attempt = 0; attempt < bestOf; attempt++)
        {
            tournament.ReportGame(pairing, Finish(tournament.CreateGame(pairing), attempt == 0 ? winner : -1));
            Assert.Equal(attempt == bestOf - 1, pairing.WinnerSide.HasValue);
            Assert.False(pairing.AdvancedBySeed);
            tournament = Resume(db, tournament, bestOf, order, snapshots);
            pairing = Assert.Single(tournament.Pairings);
        }
        Assert.True(tournament.IsFinished);
        Assert.Equal(winner, pairing.WinnerSide);
        Assert.Equal(bestOf, pairing.Games.Count);
        Assert.Equal(1, pairing.Games.Count(game => game.WinnerSide == winner));
        Assert.Equal(bestOf - 1, pairing.Games.Count(game => game.WinnerSide == -1));
        Assert.Throws<InvalidOperationException>(() => tournament.CreateGame(pairing));
        foreach (var row in tournament.Standings())
        {
            Assert.Equal(row.Player == winnerPlayer ? 3 : 0, row.Points);
            Assert.Equal(row.Player == winnerPlayer ? 1 : 0, row.Wins);
            Assert.Equal(row.Player == winnerPlayer ? 0 : 1, row.Losses);
            Assert.Equal(0, row.Draws);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void FinalFallbackUsesRatingEvenWhenTheHigherSeedIsSideOneAndHasTheLargerPlayerId(int bestOf)
    {
        var db = BattleTests.LoadPack("original");
        var profiles = new[] { 1500, 1900, 1700, 2100 }.Select(rating => ProfileOf(rating, 0)).ToArray();
        var tournament = Make(db, "single", bestOf, profiles: profiles);
        Assert.Equal(new[] { 3, 1, 2, 0 }, tournament.EffectiveSeedOrder);
        foreach (var pairing in tournament.NextRound())
        {
            int advancing = pairing.Player0 == 0 || pairing.Player1 == 0 ? 0 : 1;
            int side = pairing.Player0 == advancing ? 0 : 1;
            for (int win = 0; win < bestOf / 2 + 1; win++)
                tournament.ReportGame(pairing, Finish(tournament.CreateGame(pairing), side));
        }
        var final = Assert.Single(tournament.NextRound());
        Assert.Equal(0, final.Player0);
        Assert.Equal(1, final.Player1);
        int id = final.Id;
        for (int attempt = 0; attempt < bestOf + 3; attempt++)
        {
            tournament.ReportGame(final, Finish(tournament.CreateGame(final), -1));
            string saved = tournament.ToJson();
            tournament = Tournament.FromJson(db, saved);
            Assert.Equal(saved, tournament.ToJson());
            final = tournament.Pairings.Single(pair => pair.Id == id);
        }
        Assert.True(tournament.IsFinished);
        Assert.Equal(1, tournament.Champion);
        Assert.Equal(1, final.WinnerSide);
        Assert.True(final.AdvancedBySeed);
        Assert.All(final.Games, game => Assert.Equal(-1, game.WinnerSide));
    }

    [Theory]
    [InlineData("single")]
    [InlineData("double")]
    [InlineData("roundrobin")]
    public void RatedVersionFiveArchivesResumeWithoutRewritingAndDoNotBorrowOtherRatingOrVersionFourGames(string format)
    {
        using var files = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var tournament = Make(db, format, 5);
        var pairing = Assert.Single(tournament.NextRound());
        var library = new ReplayLibrary(files.Root, clock: () => Epoch);
        var ids = new List<string>();
        var records = new List<Battle>();
        string key = null;
        int gameCount = format == "roundrobin" ? 5 : 8;
        for (int attempt = 0; attempt < gameCount; attempt++)
        {
            var battle = Finish(tournament.CreateGame(pairing), -1);
            string pending = tournament.ToJson();
            string id = library.SaveTournamentGame(tournament, pairing, battle, attempt);
            string path = Path.Combine(files.Root, id + ".json");
            byte[] bytes = File.ReadAllBytes(path);
            ids.Add(id);
            records.Add(battle);
            Assert.Equal(pending, tournament.ToJson());
            string savedKey = ArchiveKey(files.Root, id, 3);
            if (key == null) key = savedKey;
            Assert.Equal(key, savedKey);
            Assert.Equal(ExpectedKey(tournament, db, version: 5, bestOf: 5, automatic: true), key);

            // 归档后尚未登记的重启，必须找到同一局而不是生成另一份录像。
            tournament = Tournament.FromJson(db, pending);
            pairing = Assert.Single(tournament.Pairings);
            library = new ReplayLibrary(files.Root, clock: () => Epoch.AddDays(1));
            Assert.Equal(id, library.SaveTournamentGame(tournament, pairing, battle, attempt));
            tournament.ReportGame(pairing, battle);
            string saved = tournament.ToJson();
            tournament = Tournament.FromJson(db, saved);
            pairing = Assert.Single(tournament.Pairings);
            Assert.Equal(id, library.SaveTournamentGame(tournament, pairing, battle, attempt));
            Assert.Equal(saved, tournament.ToJson());
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(battle.Record.ToJson(), library.Load(id).ToJson());
            using var report = JsonDocument.Parse(TournamentReport.Build(tournament, ReplayLibrary.OpenReadOnly(files.Root)).ToJson());
            AssertCoverage(report.RootElement, attempt + 1, attempt + 1, 0);
        }
        Assert.Equal(gameCount, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(format != "roundrobin", pairing.AdvancedBySeed);
        if (format == "roundrobin")
        {
            Assert.True(tournament.IsFinished);
            Assert.Equal(-1, pairing.WinnerSide);
            Assert.Throws<InvalidOperationException>(() => tournament.CreateGame(pairing));
            Assert.All(tournament.Standings(), row =>
            {
                Assert.Equal(1, row.Points);
                Assert.Equal(1, row.Draws);
                Assert.Equal(0, row.Wins);
                Assert.Equal(0, row.Losses);
            });
        }
        using (var report = JsonDocument.Parse(TournamentReport.Build(tournament, ReplayLibrary.OpenReadOnly(files.Root)).ToJson()))
        {
            Assert.Equal(3, report.RootElement.GetProperty("version").GetInt32());
            var match = Assert.Single(report.RootElement.GetProperty("rounds").EnumerateArray()
                .SelectMany(round => round.GetProperty("matches").EnumerateArray()));
            Assert.Equal(ids, match.GetProperty("games").EnumerateArray()
                .Select(game => game.GetProperty("replay").GetProperty("id").GetString()));
            Assert.Equal(gameCount, match.GetProperty("games").GetArrayLength());
            if (format == "roundrobin") Assert.Equal(-1, match.GetProperty("winnerSide").GetInt32());
            Assert.Equal("saved", match.GetProperty("replay").GetProperty("status").GetString());
        }

        // 顺序相同但报名时的分数不同，不能借用同一库里的旧赛事关联。
        var changed = Make(db, format, 5, profiles: new[] { ProfileOf(1501, 1), ProfileOf(1801, 4) });
        var changedPair = Assert.Single(changed.NextRound());
        var changedBattle = Finish(changed.CreateGame(changedPair), -1);
        Assert.Equal(records[0].Record.ToJson(), changedBattle.Record.ToJson());
        changed.ReportGame(changedPair, changedBattle);
        using (var report = JsonDocument.Parse(TournamentReport.Build(changed, ReplayLibrary.OpenReadOnly(files.Root)).ToJson()))
            AssertCoverage(report.RootElement, 1, 0, 1);
        string changedId = library.SaveTournamentGame(changed, changedPair, changedBattle, 0);
        Assert.DoesNotContain(changedId, ids);
        Assert.NotEqual(key, ArchiveKey(files.Root, changedId, 3));

        if (format != "roundrobin")
        {
            var legacy = Make(db, format, null, automatic: true);
            var legacyPair = Assert.Single(legacy.NextRound());
            var legacyBattle = Finish(legacy.CreateGame(legacyPair), -1);
            Assert.Equal(records[0].Record.ToJson(), legacyBattle.Record.ToJson());
            legacy.ReportGame(legacyPair, legacyBattle);
            using (var report = JsonDocument.Parse(TournamentReport.Build(legacy, ReplayLibrary.OpenReadOnly(files.Root)).ToJson()))
                AssertCoverage(report.RootElement, 1, 0, 1);
            string legacyId = library.SaveTournamentGame(legacy, legacyPair, legacyBattle, 0);
            Assert.DoesNotContain(legacyId, ids);
            string legacyKey = ArchiveKey(files.Root, legacyId, 2);
            Assert.NotEqual(key, legacyKey);
            Assert.Equal(ExpectedKey(legacy, db, 4, null, true), legacyKey);
        }
    }

    [Theory]
    [InlineData("single")]
    [InlineData("roundrobin")]
    public void VersionFiveAllowsBothRatingFieldsToBeAbsentButDoesNotInferRatingMode(string format)
    {
        var db = BattleTests.LoadPack("original");
        var rated = Make(db, format, 3);
        var root = JsonNode.Parse(rated.ToJson()).AsObject();
        root.Remove("seedBy");
        root.Remove("ratingSeeds");
        var restored = Tournament.FromJson(db, root.ToJsonString());
        Assert.True(restored.HasRuleset);
        Assert.True(restored.AutoTiebreak);
        Assert.Equal(TournamentSeedMode.Default, restored.SeedMode);
        Assert.Empty(restored.RatingSeeds);
        Assert.Equal(new[] { 0, 1 }, restored.EffectiveSeedOrder);
        using var canonical = JsonDocument.Parse(restored.ToJson());
        Assert.Equal(5, canonical.RootElement.GetProperty("version").GetInt32());
        Assert.False(canonical.RootElement.TryGetProperty("seedBy", out _));
        Assert.False(canonical.RootElement.TryGetProperty("ratingSeeds", out _));
        Assert.Equal(restored.ToJson(), Tournament.FromJson(db, restored.ToJson()).ToJson());
    }

    [Theory]
    [InlineData("missing-mode")]
    [InlineData("missing-snapshots")]
    [InlineData("unknown-mode")]
    [InlineData("null-mode")]
    [InlineData("null-snapshots")]
    [InlineData("missing-player")]
    [InlineData("duplicate-player")]
    [InlineData("negative-games")]
    [InlineData("zero-season")]
    [InlineData("unknown-level")]
    [InlineData("random-seed")]
    [InlineData("explicit-order")]
    [InlineData("changed-order")]
    [InlineData("downgrade-to-four")]
    public void RatedVersionFiveRejectsIncompleteSnapshotsConflictingModesAndReorderedHistory(string defect)
    {
        var db = BattleTests.LoadPack("original");
        var tournament = Make(db, "single", 3);
        var pairing = Assert.Single(tournament.NextRound());
        tournament.ReportGame(pairing, Finish(tournament.CreateGame(pairing), -1));
        string saved = tournament.ToJson();
        var root = JsonNode.Parse(saved).AsObject();
        switch (defect)
        {
            case "missing-mode": root.Remove("seedBy"); break;
            case "missing-snapshots": root.Remove("ratingSeeds"); break;
            case "unknown-mode": root["seedBy"] = "default"; break;
            case "null-mode": root["seedBy"] = null; break;
            case "null-snapshots": root["ratingSeeds"] = null; break;
            case "missing-player": root["ratingSeeds"].AsArray().RemoveAt(1); break;
            case "duplicate-player": root["ratingSeeds"][1]["player"] = 0; break;
            case "negative-games": root["ratingSeeds"][0]["games"] = -1; break;
            case "zero-season": root["ratingSeeds"][0]["season"] = 0; break;
            case "unknown-level": root["ratingSeeds"][0]["computerLevel"] = "easy"; break;
            case "random-seed": root["seed"] = 1; break;
            case "explicit-order": root["seedOrder"] = new JsonArray(1, 0); break;
            case "changed-order": root["ratingSeeds"][0]["rating"] = 2500; break;
            case "downgrade-to-four": root["version"] = 4; break;
        }
        var error = Assert.Throws<FormatException>(() => Tournament.FromJson(db, root.ToJsonString()));
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
        Assert.Equal(saved, tournament.ToJson());
    }

    [Theory]
    [InlineData("single", false)]
    [InlineData("double", false)]
    [InlineData("roundrobin", false)]
    [InlineData("single", true)]
    public void NoRulesetRatingKeepsItsExactVersionFourRegistrationBytesAndOriginalGameSemantics(string format, bool automatic)
    {
        var db = BattleTests.LoadPack("original");
        var tournament = Make(db, format, null, automatic: automatic);
        string expected = "{\"version\":4,\"packId\":" + Quote(db.PackId) + ",\"contentHash\":" + Quote(db.ContentHash())
            + ",\"format\":" + Quote(format) + Configuration(format, null, automatic)
            + ",\"rounds\":0,\"players\":" + Players(tournament) + ",\"roundsPlayed\":[]}";
        Assert.Equal(expected, tournament.ToJson());
        tournament = Tournament.FromJson(db, expected);
        Assert.Equal(expected, tournament.ToJson());
        Assert.False(tournament.HasRuleset);
        Assert.Equal(1, tournament.BestOf);
        Assert.Equal(automatic, tournament.AutoTiebreak);
        var pairing = Assert.Single(tournament.NextRound());
        if (automatic)
        {
            for (int attempt = 0; attempt < 4; attempt++)
                tournament.ReportGame(pairing, Finish(tournament.CreateGame(pairing), -1));
            Assert.True(pairing.AdvancedBySeed);
            Assert.Equal(4, pairing.Games.Count);
        }
        else
        {
            var first = tournament.Players[pairing.Player0];
            var second = tournament.Players[pairing.Player1];
            var battle = new Battle(db, new IList<string>[] { first.Team.ToArray(), second.Team.ToArray() },
                new[] { first.Name, second.Name }, 127);
            Finish(battle, format == "roundrobin" ? -1 : 0);
            tournament.ReportResult(pairing, battle.Winner);
            Assert.Empty(pairing.Games);
            if (format == "roundrobin") Assert.All(tournament.Standings(), row => Assert.Equal(1, row.Points));
        }
        string saved = tournament.ToJson();
        Assert.Equal(saved, Tournament.FromJson(db, saved).ToJson());
        using var json = JsonDocument.Parse(saved);
        Assert.Equal(4, json.RootElement.GetProperty("version").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("ruleset", out _));
    }

    static Tournament Make(ContentDb db, string format, int? bestOf, Profile[] profiles = null, bool automatic = false)
    {
        var tournament = new Tournament(db, format,
            options: new TournamentOptions { SeedMode = TournamentSeedMode.ByRating, AutoTiebreak = automatic },
            ruleset: bestOf.HasValue ? Rule(bestOf.Value) : null);
        profiles = profiles ?? new[] { ProfileOf(1500, 1), ProfileOf(1800, 4) };
        for (int player = 0; player < profiles.Length; player++)
            tournament.AddPlayer("评级选手" + player, TeamCode.Encode(new TeamPreset
            {
                Name = "评级组合阵容", PackId = db.PackId,
                Ids = db.Streamers.Skip(player % 2 * 6).Take(6).Select(unit => unit.Id).ToList()
            }), profiles[player]);
        return tournament;
    }

    static Ruleset Rule(int bestOf)
    {
        return new Ruleset { Id = "t127-rating-review", Name = "评级多局回归", BestOf = bestOf, Modifiers = new List<string>() };
    }

    static Profile ProfileOf(int rating, int games)
    {
        var profile = new Profile();
        profile.Ratings[Profile.DefaultRatingFormat] = new PlayerRating { Rating = rating, Games = games };
        return profile;
    }

    static Battle Finish(Battle battle, int winner)
    {
        if (winner < 0) battle.ResolveSurrenders(true, true);
        else battle.Surrender(1 - winner);
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(winner, battle.Winner);
        var replay = Replay.Run(battle.Db, BattleRecord.FromJson(battle.Record.ToJson()));
        Assert.Equal(battle.StateHash(), replay.StateHash());
        Assert.Equal(battle.Record.ToJson(), replay.Record.ToJson());
        return battle;
    }

    static Tournament Resume(ContentDb db, Tournament tournament, int bestOf, int[] order, string[] snapshots)
    {
        string saved = tournament.ToJson();
        using var root = JsonDocument.Parse(saved);
        Assert.Equal(5, root.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("rating", root.RootElement.GetProperty("seedBy").GetString());
        var restored = Tournament.FromJson(db, saved);
        Assert.Equal(saved, restored.ToJson());
        Assert.True(restored.HasRuleset);
        Assert.True(restored.AutoTiebreak);
        Assert.Equal(bestOf, restored.BestOf);
        Assert.Equal(TournamentSeedMode.ByRating, restored.SeedMode);
        Assert.Equal(order, restored.EffectiveSeedOrder);
        Assert.Equal(snapshots, SnapshotRows(restored));
        return restored;
    }

    static string[] SnapshotRows(Tournament tournament)
    {
        return tournament.RatingSeeds.Select(item => item.Player + ":" + item.Rating + ":" + item.Games
            + ":" + item.Season + ":" + item.ComputerLevel).ToArray();
    }

    static void AssertCoverage(JsonElement root, int completed, int archived, int missing)
    {
        var coverage = root.GetProperty("coverage");
        Assert.Equal(completed, coverage.GetProperty("completedGames").GetInt32());
        Assert.Equal(archived, coverage.GetProperty("archivedGames").GetInt32());
        Assert.Equal(missing, coverage.GetProperty("missingGames").GetInt32());
        Assert.Equal(0, coverage.GetProperty("invalidGames").GetInt32());
    }

    static string ArchiveKey(string directory, string id, int tagVersion)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, id + ".json")));
        var tag = json.RootElement.GetProperty("library").GetProperty("tournament");
        Assert.Equal(tagVersion, tag.GetProperty("version").GetInt32());
        return tag.GetProperty("key").GetString();
    }

    static string Configuration(string format, int? bestOf, bool automatic)
    {
        // 固定已发布 v4 与新 v5 的配置顺序，不能用待测 ToJson 生成期望关联键。
        string text = format == "single" || format == "double" || bestOf.HasValue
            ? ",\"seed\":null,\"seedOrder\":null,\"resetFinal\":true" : "";
        if (automatic) text += ",\"autoTiebreak\":true";
        if (bestOf.HasValue) text += ",\"ruleset\":{\"id\":\"t127-rating-review\",\"name\":\"评级多局回归\",\"season\":null,"
            + "\"banOne\":false,\"leadPick\":false,\"bestOf\":" + bestOf.Value.ToString(CultureInfo.InvariantCulture)
            + ",\"modifiers\":[],\"turnSeconds\":0,\"bankSeconds\":0}";
        return text + ",\"seedBy\":\"rating\",\"ratingSeeds\":["
            + "{\"player\":0,\"rating\":1500,\"games\":1,\"season\":1,\"computerLevel\":null},"
            + "{\"player\":1,\"rating\":1800,\"games\":4,\"season\":1,\"computerLevel\":null}]";
    }

    static string Players(Tournament tournament)
    {
        return "[" + string.Join(",", tournament.Players.Select(player => "{\"id\":" + player.Id.ToString(CultureInfo.InvariantCulture)
            + ",\"name\":" + Quote(player.Name) + ",\"teamCode\":" + Quote(player.TeamCode) + "}")) + "]";
    }

    static string ExpectedKey(Tournament tournament, ContentDb db, int version, int? bestOf, bool automatic)
    {
        string config = "{\"version\":" + version.ToString(CultureInfo.InvariantCulture) + ",\"format\":" + Quote(tournament.Format)
            + Configuration(tournament.Format, bestOf, automatic) + ",\"rounds\":0,\"packId\":" + Quote(db.PackId)
            + ",\"contentHash\":" + Quote(db.ContentHash()) + ",\"players\":" + Players(tournament) + "}";
        return Hash("StreamDuel.TournamentArchive.v" + version.ToString(CultureInfo.InvariantCulture) + "\n" + config);
    }

    static string Quote(string text) { return JsonSerializer.Serialize(text, JsonOptions); }
    static string Hash(string text) { return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(); }

    sealed class TestDirectory : IDisposable
    {
        readonly string workspace, parent, name = "t127-rating-" + Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string source = "")
        {
            string tests = Path.GetDirectoryName(Path.GetFullPath(source));
            if (Path.GetFileName(tests) != "Engine.Tests") throw new InvalidOperationException("临时目录只能位于当前测试工程。");
            workspace = Path.GetDirectoryName(tests);
            parent = Path.GetFullPath(Path.Combine(tests, "bin", "test-temp"));
            Root = Path.Combine(parent, name);
            ValidatePath(Root);
            Directory.CreateDirectory(Root);
        }

        void ValidatePath(string path)
        {
            string target = Path.GetFullPath(path);
            if (!target.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.GetDirectoryName(Root) != parent || Path.GetFileName(Root) != name
                || (target != Root && !target.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw new InvalidOperationException("只能清理当前测试创建的独立目录。");
            for (string item = target; item != workspace; item = Path.GetDirectoryName(item))
                if ((Directory.Exists(item) || File.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("测试目录不能经过链接。");
        }

        public void Dispose()
        {
            ValidatePath(Root);
            if (!Directory.Exists(Root)) return;
            var pending = new Stack<string>();
            pending.Push(Root);
            while (pending.Count != 0)
                foreach (string child in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    ValidatePath(child);
                    if (Directory.Exists(child)) pending.Push(child);
                }
            Directory.Delete(Root, true);
        }
    }
}
