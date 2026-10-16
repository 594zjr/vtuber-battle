using System;
using System.Collections.Generic;
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

public class T124_TiebreakIntegrationReviewTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("single", 5)]
    [InlineData("double", 2)]
    public void RatedAutomaticGamesResumeEveryAttemptAndUseTheFrozenRatingOrderForFourDraws(string format, int count)
    {
        var db = BattleTests.LoadPack("original");
        var tournament = Make(db, format, rated: true, automatic: true, count: count);
        string[] snapshots = SnapshotRows(tournament);
        int[] seedOrder = Enumerable.Range(0, count).Reverse().ToArray();
        Assert.Equal(seedOrder, tournament.EffectiveSeedOrder);
        var opening = tournament.NextRound();
        Assert.Equal(count == 5 ? 3 : 0, opening.Count(item => item.IsBye));
        var pairing = Assert.Single(opening, item => !item.IsBye);
        Assert.True(pairing.Player0 > pairing.Player1);
        int pairingId = pairing.Id, higherRatedPlayer = pairing.Player0;
        uint[] seeds = Enumerable.Range(0, 4).Select(attempt => tournament.GameSeed(pairing, attempt)).ToArray();
        Assert.Equal(4, seeds.Distinct().Count());

        for (int attempt = 0; attempt < 4; attempt++)
        {
            var battle = Finish(tournament.CreateGame(pairing), -1);
            Assert.Equal(seeds[attempt], battle.Seed);
            tournament.ReportGame(pairing, battle);
            Assert.Equal(attempt + 1, pairing.Games.Count);
            Assert.Equal(-1, pairing.Games[attempt].WinnerSide);
            Assert.Equal(RecordHash(battle.Record), pairing.Games[attempt].RecordHash);
            if (attempt < 3)
            {
                Assert.Null(pairing.WinnerSide);
                Assert.False(pairing.AdvancedBySeed);
            }
            string saved = tournament.ToJson();
            using (var json = JsonDocument.Parse(saved))
            {
                Assert.Equal(4, json.RootElement.GetProperty("version").GetInt32());
                Assert.True(json.RootElement.GetProperty("autoTiebreak").GetBoolean());
                Assert.Equal("rating", json.RootElement.GetProperty("seedBy").GetString());
            }
            tournament = Tournament.FromJson(db, saved);
            Assert.Equal(saved, tournament.ToJson());
            Assert.Equal(snapshots, SnapshotRows(tournament));
            Assert.Equal(seedOrder, tournament.EffectiveSeedOrder);
            pairing = tournament.Pairings[pairingId];
            Assert.Equal(seeds, Enumerable.Range(0, 4).Select(index => tournament.GameSeed(pairing, index)));
        }
        Assert.True(pairing.AdvancedBySeed);
        Assert.Equal(0, pairing.WinnerSide);
        Assert.Equal(higherRatedPlayer, pairing.Player0);
        Assert.True(higherRatedPlayer > pairing.Player1);

        // 后续轮次仍由真实单局报分推进，覆盖轮空和双败后续赛程的恢复。
        int guard = 0;
        while (!tournament.IsFinished)
        {
            Assert.True(guard++ < 20);
            foreach (var item in tournament.NextRound().Where(item => !item.IsBye && !item.WinnerSide.HasValue))
                tournament.ReportGame(item, Finish(tournament.CreateGame(item), 0));
            string saved = tournament.ToJson();
            tournament = Tournament.FromJson(db, saved);
            Assert.Equal(saved, tournament.ToJson());
            Assert.Equal(snapshots, SnapshotRows(tournament));
        }
        Assert.Empty(tournament.NextRound());
        Assert.NotNull(tournament.Champion);
    }

    [Fact]
    public void RatedAutomaticArchivesSurviveRestartAndStaySeparateFromManualRatingArchives()
    {
        using var directory = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var tournament = Make(db, "single", rated: true, automatic: true);
        var manual = Make(db, "single", rated: true, automatic: false);
        var pairing = Assert.Single(tournament.NextRound());
        var manualPairing = Assert.Single(manual.NextRound());
        Assert.Equal(new[] { pairing.Player0, pairing.Player1 }, new[] { manualPairing.Player0, manualPairing.Player1 });
        var library = new ReplayLibrary(directory.Root, clock: () => Epoch);
        var ids = new List<string>();
        string automaticKey = null;
        Battle winningBattle = null;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var battle = Finish(tournament.CreateGame(pairing), attempt == 0 ? -1 : 0);
            string record = battle.Record.ToJson(), beforeSave = tournament.ToJson();
            ulong state = battle.StateHash();
            string id = library.SaveTournamentGame(tournament, pairing, battle, attempt);
            ids.Add(id);
            Assert.Equal(beforeSave, tournament.ToJson());
            Assert.Equal(attempt, pairing.Games.Count);
            string key = ArchiveKey(directory.Root, id, expectedTagVersion: 2);
            if (automaticKey == null) automaticKey = key;
            Assert.Equal(automaticKey, key);
            tournament.ReportGame(pairing, battle);
            string saved = tournament.ToJson();
            tournament = Tournament.FromJson(db, saved);
            pairing = tournament.Pairings[pairing.Id];
            library = new ReplayLibrary(directory.Root, clock: () => Epoch.AddDays(1));
            Assert.Equal(id, library.SaveTournamentGame(tournament, pairing, battle, attempt));
            Assert.Equal(saved, tournament.ToJson());
            Assert.Equal(record, battle.Record.ToJson());
            Assert.Equal(state, battle.StateHash());
            Assert.Equal(record, library.Load(id).ToJson());
            winningBattle = battle;
        }
        Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ExpectedArchiveKey(tournament, db, 4, rated: true, automatic: true), automaticKey);
        manual.ReportResult(manualPairing, 0);
        using (var missing = JsonDocument.Parse(TournamentReport.Build(manual, library).ToJson()))
            Assert.Equal(0, missing.RootElement.GetProperty("coverage").GetProperty("archivedGames").GetInt32());
        string manualId = library.SaveTournament(manual, manualPairing, winningBattle);
        Assert.DoesNotContain(manualId, ids);
        Assert.NotEqual(automaticKey, ArchiveKey(directory.Root, manualId, expectedTagVersion: 1));

        var reader = ReplayLibrary.OpenReadOnly(directory.Root);
        string beforeReport = tournament.ToJson();
        using var report = JsonDocument.Parse(TournamentReport.Build(tournament, reader).ToJson());
        Assert.Equal(2, report.RootElement.GetProperty("version").GetInt32());
        var coverage = report.RootElement.GetProperty("coverage");
        Assert.Equal(2, coverage.GetProperty("completedGames").GetInt32());
        Assert.Equal(2, coverage.GetProperty("archivedGames").GetInt32());
        Assert.Equal(0, coverage.GetProperty("missingGames").GetInt32());
        Assert.Equal(0, coverage.GetProperty("invalidGames").GetInt32());
        var match = Assert.Single(report.RootElement.GetProperty("rounds").EnumerateArray()
            .SelectMany(round => round.GetProperty("matches").EnumerateArray()));
        Assert.Equal("saved", match.GetProperty("replay").GetProperty("status").GetString());
        var games = match.GetProperty("games");
        Assert.Equal(2, games.GetArrayLength());
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(attempt, games[attempt].GetProperty("attempt").GetInt32());
            Assert.Equal("saved", games[attempt].GetProperty("replay").GetProperty("status").GetString());
            Assert.Equal(ids[attempt], games[attempt].GetProperty("replay").GetProperty("id").GetString());
        }
        Assert.Equal(beforeReport, tournament.ToJson());
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("attempt")]
    [InlineData("record-hash")]
    [InlineData("winner")]
    [InlineData("fallback")]
    [InlineData("earlier-winner")]
    [InlineData("fifth-game")]
    [InlineData("missing-games")]
    [InlineData("missing-auto")]
    [InlineData("version-three")]
    public void VersionFourRejectsTamperedAutomaticGameHistoryAndDerivedResults(string defect)
    {
        var db = BattleTests.LoadPack("original");
        var tournament = Make(db, "single", rated: true, automatic: true);
        var pairing = Assert.Single(tournament.NextRound());
        for (int attempt = 0; attempt < 4; attempt++)
            tournament.ReportGame(pairing, Finish(tournament.CreateGame(pairing), -1));
        string saved = tournament.ToJson();
        var root = JsonNode.Parse(saved).AsObject();
        Assert.Equal(4, root["version"].GetValue<int>());
        Assert.True(root["autoTiebreak"].GetValue<bool>());
        var match = root["roundsPlayed"][0][0].AsObject();
        var games = match["games"].AsArray();
        switch (defect)
        {
            case "seed": games[1]["seed"] = games[1]["seed"].GetValue<uint>() ^ 1u; break;
            case "attempt": games[1]["attempt"] = 2; break;
            case "record-hash": games[0]["recordHash"] = "不是摘要"; break;
            case "winner": match["winnerSide"] = 1; break;
            case "fallback": match["advancedBySeed"] = false; break;
            case "earlier-winner": games[0]["winnerSide"] = 0; break;
            case "fifth-game": games.Add(games[3].DeepClone()); break;
            case "missing-games": match.Remove("games"); break;
            case "missing-auto": root.Remove("autoTiebreak"); break;
            case "version-three": root["version"] = 3; break;
        }
        ChineseFormatFailure(() => Tournament.FromJson(db, root.ToJsonString()));
        Assert.Equal(saved, tournament.ToJson());
    }

    [Theory]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("\"true\"")]
    public void ManualVersionFourRejectsAnExplicitNonTrueAutomaticField(string value)
    {
        var db = BattleTests.LoadPack("original");
        var tournament = Make(db, "single", rated: true, automatic: false);
        string saved = tournament.ToJson();
        var root = JsonNode.Parse(saved).AsObject();
        Assert.False(root.ContainsKey("autoTiebreak"));
        root["autoTiebreak"] = JsonNode.Parse(value);
        ChineseFormatFailure(() => Tournament.FromJson(db, root.ToJsonString()));
        Assert.Equal(saved, tournament.ToJson());
    }

    [Theory]
    [InlineData("roundrobin")]
    [InlineData("swiss")]
    public void RatingDoesNotSilentlyEnableOrDiscardAutomaticTiebreakForLeagueFormats(string format)
    {
        var db = BattleTests.LoadPack("original");
        Assert.Throws<ArgumentException>(() => Make(db, format, rated: true, automatic: true));
        var tournament = Make(db, format, rated: true, automatic: false);
        string saved = tournament.ToJson();
        var root = JsonNode.Parse(saved).AsObject();
        root["autoTiebreak"] = true;
        ChineseFormatFailure(() => Tournament.FromJson(db, root.ToJsonString()));
        Assert.Equal(saved, tournament.ToJson());
    }

    [Theory]
    [InlineData("roundrobin", false, false, 1)]
    [InlineData("single", false, false, 2)]
    [InlineData("single", false, true, 3)]
    [InlineData("single", true, false, 4)]
    public void UncombinedModesKeepTheirExactRegistrationBytesAndArchiveDomains(string format, bool rated, bool automatic, int version)
    {
        using var directory = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var tournament = Make(db, format, rated, automatic);
        string expected = "{\"version\":" + version + ",\"packId\":" + Quote(db.PackId)
            + ",\"contentHash\":" + Quote(db.ContentHash()) + ",\"format\":" + Quote(format)
            + ExpectedConfiguration(format, rated, automatic) + ",\"rounds\":0,\"players\":"
            + ExpectedPlayers(tournament) + ",\"roundsPlayed\":[]}";
        Assert.Equal(expected, tournament.ToJson());
        Assert.Equal(expected, Tournament.FromJson(db, expected).ToJson());
        var pairing = Assert.Single(tournament.NextRound());
        Battle battle;
        if (automatic) battle = tournament.CreateGame(pairing);
        else
        {
            var first = tournament.Players[pairing.Player0];
            var second = tournament.Players[pairing.Player1];
            battle = new Battle(db, new IList<string>[] { first.Team.ToArray(), second.Team.ToArray() },
                new[] { first.Name, second.Name }, 124);
        }
        Finish(battle, 0);
        var library = new ReplayLibrary(directory.Root, clock: () => Epoch);
        string id = automatic ? library.SaveTournamentGame(tournament, pairing, battle, 0)
            : library.SaveTournament(tournament, pairing, battle);
        Assert.Equal(ExpectedArchiveKey(tournament, db, version, rated, automatic),
            ArchiveKey(directory.Root, id, automatic ? 2 : 1));
        if (automatic) tournament.ReportGame(pairing, battle);
        else tournament.ReportResult(pairing, 0);
        string completed = tournament.ToJson();
        var restored = Tournament.FromJson(db, completed);
        Assert.Equal(completed, restored.ToJson());
        Assert.Equal(id, automatic ? library.SaveTournamentGame(restored, restored.Pairings[0], battle, 0)
            : library.SaveTournament(restored, restored.Pairings[0], battle));
    }

    [Fact]
    public void RatedSwissHeadToHeadPrecedesRatingWhenPointsAndOpponentPointsTie()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = new Tournament(db, "swiss", rounds: 3,
            options: new TournamentOptions { SeedMode = TournamentSeedMode.ByRating });
        for (int player = 0; player < 4; player++)
            tournament.AddPlayer("选手 " + player, Code(db, player), ProfileOf(2000 - player * 100, 0));
        Assert.Equal(new[] { 0, 1, 2, 3 }, tournament.EffectiveSeedOrder);
        var winners = new Dictionary<(int, int), int>
        {
            [(0, 1)] = 1, [(0, 2)] = 0, [(0, 3)] = 0,
            [(1, 2)] = 2, [(1, 3)] = 1, [(2, 3)] = -1
        };
        var seen = new HashSet<(int, int)>();
        for (int round = 1; round <= 3; round++)
        {
            var pairings = tournament.NextRound();
            Assert.Equal(round, tournament.Round);
            Assert.Equal(2, pairings.Count);
            foreach (var pairing in pairings)
            {
                Assert.False(pairing.IsBye);
                var key = (Math.Min(pairing.Player0, pairing.Player1), Math.Max(pairing.Player0, pairing.Player1));
                Assert.True(seen.Add(key), "同一选手对重复交手。");
                Assert.True(winners.TryGetValue(key, out int winner));
                tournament.ReportResult(pairing, winner < 0 ? -1 : pairing.Player0 == winner ? 0 : 1);
            }
            Assert.Equal(round == 3, tournament.IsFinished);
        }
        Assert.Equal(6, seen.Count);
        var rows = tournament.Standings();
        Assert.Equal(new[] { 1, 0, 2, 3 }, rows.Select(row => row.Player));
        Assert.Equal(new[] { 6, 6, 4, 1 }, rows.Select(row => row.Points));
        Assert.Equal(new[] { 11, 11, 13, 16 }, rows.Select(row => row.OpponentPoints));
        Assert.Equal(new[] { 3, 0, 0, 0 }, rows.Select(row => row.HeadToHeadPoints));
    }

    static Tournament Make(ContentDb db, string format, bool rated, bool automatic, int count = 2)
    {
        var options = rated || format == "single" || format == "double" ? new TournamentOptions
        {
            SeedMode = rated ? TournamentSeedMode.ByRating : TournamentSeedMode.Default,
            AutoTiebreak = automatic
        } : null;
        var tournament = new Tournament(db, format, options: options);
        for (int player = 0; player < count; player++)
            tournament.AddPlayer("选手 " + player, Code(db, player), ProfileOf(1400 + player * 400, 5 + player));
        return tournament;
    }

    static Profile ProfileOf(int rating, int games)
    {
        var profile = new Profile();
        profile.Ratings[Profile.DefaultRatingFormat] = new PlayerRating { Rating = rating, Games = games };
        return profile;
    }

    static string Code(ContentDb db, int player)
    {
        return TeamCode.Encode(new TeamPreset
        {
            Name = "组合回归阵容", PackId = db.PackId,
            Ids = db.Streamers.Skip(player % 2 * 6).Take(6).Select(item => item.Id).ToList()
        });
    }

    static Battle Finish(Battle battle, int winner)
    {
        if (winner == -1) battle.ResolveSurrenders(true, true);
        else battle.Surrender(1 - winner);
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(winner, battle.Winner);
        var replay = Replay.Run(battle.Db, BattleRecord.FromJson(battle.Record.ToJson()));
        Assert.Equal(battle.StateHash(), replay.StateHash());
        Assert.Equal(battle.Clone().Random(), replay.Random());
        return battle;
    }

    static string[] SnapshotRows(Tournament tournament)
    {
        return tournament.RatingSeeds.Select(item => item.Player + ":" + item.Rating + ":" + item.Games
            + ":" + item.Season + ":" + item.ComputerLevel).ToArray();
    }

    static void ChineseFormatFailure(Action action)
    {
        var error = Assert.Throws<FormatException>(action);
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
    }

    static string ArchiveKey(string directory, string id, int expectedTagVersion)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, id + ".json")));
        var tag = json.RootElement.GetProperty("library").GetProperty("tournament");
        Assert.Equal(expectedTagVersion, tag.GetProperty("version").GetInt32());
        return tag.GetProperty("key").GetString();
    }

    static string ExpectedConfiguration(string format, bool rated, bool automatic)
    {
        // 固定旧协议字段及顺序，不调用待验证的生产序列化方法生成期望。
        string text = format == "single" || format == "double"
            ? ",\"seed\":null,\"seedOrder\":null,\"resetFinal\":true" : "";
        if (automatic) text += ",\"autoTiebreak\":true";
        if (rated) text += ",\"seedBy\":\"rating\",\"ratingSeeds\":["
            + "{\"player\":0,\"rating\":1400,\"games\":5,\"season\":1,\"computerLevel\":null},"
            + "{\"player\":1,\"rating\":1800,\"games\":6,\"season\":1,\"computerLevel\":null}]";
        return text;
    }

    static string ExpectedPlayers(Tournament tournament)
    {
        return "[" + string.Join(",", tournament.Players.Select(player => "{\"id\":" + player.Id
            + ",\"name\":" + Quote(player.Name) + ",\"teamCode\":" + Quote(player.TeamCode) + "}")) + "]";
    }

    static string ExpectedArchiveKey(Tournament tournament, ContentDb db, int version, bool rated, bool automatic)
    {
        string canonical = "{\"version\":" + version + ",\"format\":" + Quote(tournament.Format)
            + ExpectedConfiguration(tournament.Format, rated, automatic) + ",\"rounds\":0,\"packId\":"
            + Quote(db.PackId) + ",\"contentHash\":" + Quote(db.ContentHash()) + ",\"players\":"
            + ExpectedPlayers(tournament) + "}";
        return Hash("StreamDuel.TournamentArchive.v" + version + "\n" + canonical);
    }

    static string Quote(string text) { return JsonSerializer.Serialize(text, JsonOptions); }
    static string RecordHash(BattleRecord record) { return Hash(record.ToJson()); }
    static string Hash(string text)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string workspace, parent, name = Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string source = "")
        {
            string tests = Path.GetDirectoryName(Path.GetFullPath(source));
            while (tests != null && Path.GetFileName(tests) != "Engine.Tests") tests = Path.GetDirectoryName(tests);
            if (tests == null) throw new InvalidOperationException("临时目录必须位于当前工程 Engine.Tests/obj。");
            workspace = Path.GetDirectoryName(tests);
            parent = Path.GetFullPath(Path.Combine(tests, "obj", "t124-review", "temp"));
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
                throw new InvalidOperationException("只能清理当前工程内本测试创建的 GUID 目录。");
            for (string item = target; item != workspace; item = Path.GetDirectoryName(item))
                if (Directory.Exists(item) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("测试目录不能经过链接。");
        }

        public void Dispose()
        {
            ValidatePath(Root);
            if (!Directory.Exists(Root)) return;
            var pending = new Stack<string>();
            pending.Push(Root);
            while (pending.Count != 0)
                foreach (string child in Directory.EnumerateDirectories(pending.Pop()))
                {
                    ValidatePath(child);
                    pending.Push(child);
                }
            Directory.Delete(Root, true);
        }
    }
}
