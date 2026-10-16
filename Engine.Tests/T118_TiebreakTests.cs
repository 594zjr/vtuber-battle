using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T118_TiebreakTests
{
    static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    sealed class TestDirectory : IDisposable
    {
        readonly string parent, name = Guid.NewGuid().ToString("N");
        public string Root { get; }
        public TestDirectory([CallerFilePath] string file = "")
        {
            parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file), "obj", "T118", "temp"));
            Root = Path.Combine(parent, name);
            Directory.CreateDirectory(Root);
        }
        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(resolved) != parent || Path.GetFileName(resolved) != name
                || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("只能清理本测试创建的普通目录。");
            Directory.Delete(resolved, true);
        }
    }

    static ContentDb Content()
    {
        // 固定真实效果使早期平局的数据之最可精确断言，不随正式包平衡调整漂移。
        var db = new ContentDb { PackId = "t118_fixture", PackName = "加赛验收", PackNotice = "独立测试数据" };
        db.Types["talk"] = new TypeDef { Id = "talk", Name = "聊天", Color = "#808080" };
        db.Chart["talk"] = new Dictionary<string, double> { { "talk", 1 } };
        db.Personas["neutral"] = new PersonaDef { Id = "neutral", Name = "测试人设", Code = "T_LOWHP_POWER" };
        foreach (var item in new[] { (Id: "chip", N: 10), (Id: "chunk", N: 35), (Id: "finish", N: 99999) })
            db.Skills[item.Id] = new SkillDef
            {
                Id = item.Id, Name = "测试技能" + item.Id, Type = "talk", Cat = "status", Pp = 20,
                Effects = new List<EffectDef> { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = item.N } }
            };
        db.Skills["wait"] = new SkillDef { Id = "wait", Name = "测试等待", Type = "talk", Cat = "status", Pp = 20 };
        db.Struggle = new SkillDef { Id = "struggle", Name = "测试硬撑", Type = "talk", Cat = "interact", Power = 20, Pp = 1 };
        for (int index = 0; index < 12; index++)
            db.Streamers.Add(new StreamerDef
            {
                Id = "u" + index.ToString("D2"), Name = "测试主播" + index, Type = "talk", Persona = "neutral",
                Stats = new Dictionary<string, int>
                {
                    { "hp", 100 }, { "atk", 100 }, { "def", 100 }, { "spa", 100 }, { "sdef", 100 }, { "spd", 200 - index }
                },
                Skills = new List<string> { "chip", "chunk", "finish", "wait" }, Fifth = "finish"
            });
        db.Validate();
        return db;
    }

    static Tournament Make(ContentDb db, string format = "single", int count = 2, bool automatic = true,
        uint? seed = 118, IList<int> order = null, bool reset = true)
    {
        var tournament = new Tournament(db, format, format == "swiss" ? 2 : 0,
            format == "single" || format == "double" ? new TournamentOptions
            {
                AutoTiebreak = automatic, Seed = order == null ? seed : null, SeedOrder = order, ResetFinal = reset
            } : null);
        AddPlayers(tournament, db, count);
        return tournament;
    }

    static void AddPlayers(Tournament tournament, ContentDb db, int count)
    {
        for (int player = 0; player < count; player++)
            tournament.AddPlayer("选手" + player, TeamCode.Encode(new TeamPreset
            {
                Name = "验收阵容", PackId = db.PackId,
                Ids = db.Streamers.Skip(player % 2 * 6).Take(6).Select(unit => unit.Id).ToList()
            }));
    }

    static Battle Finish(Battle battle, int winner)
    {
        if (winner == -1) battle.ResolveSurrenders(true, true);
        else battle.Surrender(1 - winner);
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(winner, battle.Winner);
        var replay = Replay.Run(battle.Db, BattleRecord.FromJson(battle.Record.ToJson()));
        Assert.Equal(battle.StateHash(), replay.StateHash());
        return battle;
    }

    static Battle Report(Tournament tournament, Pairing pairing, int winner)
    {
        var battle = Finish(tournament.CreateGame(pairing), winner);
        tournament.ReportGame(pairing, battle);
        return battle;
    }

    static void FourDraws(Tournament tournament, Pairing pairing)
    {
        for (int attempt = 0; attempt <= 3; attempt++) Report(tournament, pairing, -1);
    }

    static string Hash(BattleRecord record)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(record.ToJson()))).Replace("-", "").ToLowerInvariant();
    }

    static Exception ChineseFailure(Action action)
    {
        var error = Record.Exception(action);
        Assert.NotNull(error);
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
        return error;
    }

    static SortedDictionary<string, string> Files(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            result[Path.GetRelativePath(root, path)] = Convert.ToBase64String(File.ReadAllBytes(path));
        return result;
    }

    static JsonElement Match(JsonElement report, int pairingId)
    {
        return Assert.Single(report.GetProperty("rounds").EnumerateArray()
            .SelectMany(round => round.GetProperty("matches").EnumerateArray()),
            match => match.GetProperty("pairingId").GetInt32() == pairingId);
    }

    static void Coverage(JsonElement report, int completed, int archived, int missing, int invalid = 0)
    {
        var coverage = report.GetProperty("coverage");
        Assert.Equal(completed, coverage.GetProperty("completedGames").GetInt32());
        Assert.Equal(archived, coverage.GetProperty("archivedGames").GetInt32());
        Assert.Equal(missing, coverage.GetProperty("missingGames").GetInt32());
        Assert.Equal(invalid, coverage.GetProperty("invalidGames").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FourRealDrawsAdvanceTheHigherSeedRatherThanTheLowerRegistrationId(bool shuffled)
    {
        var db = Content();
        var tournament = Make(db, seed: 0, order: shuffled ? null : new[] { 1, 0 });
        var pairing = Assert.Single(tournament.NextRound());
        Assert.Equal(1, pairing.Player0);
        Assert.Equal(0, pairing.Player1);
        for (int attempt = 0; attempt <= 3; attempt++)
        {
            var game = Report(tournament, pairing, -1);
            Assert.Equal(attempt + 1, pairing.Games.Count);
            var saved = pairing.Games[attempt];
            Assert.Equal(attempt, saved.Attempt);
            Assert.Equal(tournament.GameSeed(pairing, attempt), saved.Seed);
            Assert.Equal(-1, saved.WinnerSide);
            Assert.Equal(Hash(game.Record), saved.RecordHash);
            if (attempt < 3)
            {
                Assert.Null(pairing.WinnerSide);
                Assert.False(pairing.AdvancedBySeed);
                Assert.False(tournament.IsFinished);
                Assert.Same(pairing, Assert.Single(tournament.NextRound()));
            }
        }
        Assert.Equal(4, pairing.Games.Select(game => game.Seed).Distinct().Count());
        Assert.Equal(0, pairing.WinnerSide);
        Assert.True(pairing.AdvancedBySeed);
        Assert.True(tournament.IsFinished);
        Assert.Equal(1, tournament.Champion);
        Assert.Empty(tournament.NextRound());
        var exposed = Assert.IsAssignableFrom<IList<TournamentGame>>(pairing.Games);
        Assert.Throws<NotSupportedException>(() => exposed.Clear());
        var report = TournamentReport.Build(tournament, null);
        Assert.Contains("加赛 3 次仍平，按种子顺位晋级", report.ToText());
        using var json = JsonDocument.Parse(report.ToJson());
        Assert.True(Match(json.RootElement, pairing.Id).GetProperty("advancedBySeed").GetBoolean());
        Coverage(json.RootElement, 4, 0, 4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ARealWinnerStopsAtAnyAttemptWithoutUsingTheSeedFallback(int draws)
    {
        var tournament = Make(Content(), order: new[] { 1, 0 });
        var pairing = Assert.Single(tournament.NextRound());
        for (int i = 0; i < draws; i++) Report(tournament, pairing, -1);
        Report(tournament, pairing, 1);

        Assert.Equal(draws + 1, pairing.Games.Count);
        Assert.Equal(1, pairing.WinnerSide);
        Assert.False(pairing.AdvancedBySeed);
        Assert.Equal(0, tournament.Champion);
        string before = tournament.ToJson();
        ChineseFailure(() => tournament.CreateGame(pairing));
        Assert.Equal(before, tournament.ToJson());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SeedFallbackInTheGrandFinalObeysDoubleEliminationResetRules(bool reset)
    {
        var tournament = Make(Content(), "double", order: new[] { 1, 0 }, reset: reset);
        var opening = Assert.Single(tournament.NextRound());
        Report(tournament, opening, 1);
        var final = Assert.Single(tournament.NextRound());
        Assert.Equal("final", final.Bracket);
        Assert.Equal(0, final.Player0);
        Assert.Equal(1, final.Player1);
        FourDraws(tournament, final);
        Assert.Equal(1, final.WinnerSide);
        Assert.True(final.AdvancedBySeed);
        if (reset)
        {
            Assert.False(tournament.IsFinished);
            var rematch = Assert.Single(tournament.NextRound());
            Assert.Equal("reset", rematch.Bracket);
            Assert.Equal(final.Player0, rematch.Player0);
            Assert.Equal(final.Player1, rematch.Player1);
            FourDraws(tournament, rematch);
            Assert.True(rematch.AdvancedBySeed);
        }
        Assert.True(tournament.IsFinished);
        Assert.Equal(1, tournament.Champion);
        Assert.Equal(reset ? 3 : 2, tournament.Pairings.Count);
    }

    [Theory]
    [InlineData("single", 5)]
    [InlineData("double", 4)]
    public void EveryIntermediateDrawAndPendingRoundCanBeRestoredWithoutChangingSeedsOrFinalBytes(string format, int count)
    {
        var db = Content();
        var continuous = Make(db, format, count);
        var resumed = Tournament.FromJson(db, continuous.ToJson());
        string content = db.ContentHash();
        int guard = 0;
        while (!continuous.IsFinished)
        {
            Assert.True(guard++ < 30);
            var pairings = continuous.NextRound();
            resumed.NextRound();
            Assert.Equal(continuous.ToJson(), resumed.ToJson());
            resumed = Tournament.FromJson(db, resumed.ToJson());
            foreach (var pairing in pairings.Where(pair => !pair.IsBye))
                for (int attempt = 0; attempt <= 3; attempt++)
                {
                    var other = resumed.Pairings.Single(pair => pair.Id == pairing.Id);
                    var firstGame = Finish(continuous.CreateGame(pairing), attempt == 3 ? 1 : -1);
                    var otherGame = Finish(resumed.CreateGame(other), attempt == 3 ? 1 : -1);
                    Assert.Equal(firstGame.Record.ToJson(), otherGame.Record.ToJson());
                    continuous.ReportGame(pairing, firstGame);
                    resumed.ReportGame(other, otherGame);
                    Assert.Equal(continuous.ToJson(), resumed.ToJson());
                    string saved = resumed.ToJson();
                    resumed = Tournament.FromJson(db, saved);
                    Assert.Equal(saved, resumed.ToJson());
                }
        }
        Assert.True(resumed.IsFinished);
        Assert.Equal(continuous.Champion, resumed.Champion);
        Assert.Equal(continuous.ToJson(), resumed.ToJson());
        Assert.Equal(content, db.ContentHash());
    }

    [Theory]
    [InlineData(0u, 0, 0, 3295499171u)]
    [InlineData(118u, 1, 3, 1923371710u)]
    [InlineData(uint.MaxValue, 1, 2, 1260561282u)]
    public void PerGameSeedsMatchIndependentDocumentedUInt32Vectors(uint seed, int pairingId, int attempt, uint expected)
    {
        var tournament = Make(Content(), count: 4, seed: seed);
        tournament.NextRound();
        string before = tournament.ToJson();
        Assert.Equal(expected, tournament.GameSeed(tournament.Pairings[pairingId], attempt));
        Assert.Equal(before, tournament.ToJson());
    }

    [Fact]
    public void MissingTournamentSeedUsesZeroAndExplicitOrderDoesNotBecomeAHiddenRandomSource()
    {
        var db = Content();
        var natural = Make(db, seed: null);
        var ordered = Make(db, order: new[] { 1, 0 });
        var a = Assert.Single(natural.NextRound());
        var b = Assert.Single(ordered.NextRound());
        Assert.Null(natural.Seed);
        Assert.Null(ordered.Seed);
        Assert.Equal(1245077392u, natural.GameSeed(a, 1));
        Assert.Equal(1245077392u, ordered.GameSeed(b, 1));
    }

    [Theory]
    [InlineData("single", 2)]
    [InlineData("double", 2)]
    [InlineData("roundrobin", 1)]
    [InlineData("swiss", 1)]
    public void OldConstructorsAndDisabledTiebreakKeepTheirOriginalVersionAndManualResultSemantics(string format, int version)
    {
        var db = Content();
        var old = new Tournament(db, format);
        AddPlayers(old, db, 2);
        var disabled = new Tournament(db, format, options: old.IsElimination ? new TournamentOptions { AutoTiebreak = false } : null);
        AddPlayers(disabled, db, 2);
        Assert.False(old.AutoTiebreak);
        Assert.False(disabled.AutoTiebreak);
        Assert.Equal(old.ToJson(), disabled.ToJson());
        var pairing = Assert.Single(old.NextRound());
        if (old.IsElimination)
        {
            string before = old.ToJson();
            ChineseFailure(() => old.ReportResult(pairing, -1));
            Assert.Equal(before, old.ToJson());
        }
        old.ReportResult(pairing, old.IsElimination ? 0 : -1);
        string saved = old.ToJson();
        Assert.Equal(saved, Tournament.FromJson(db, saved).ToJson());
        using var json = JsonDocument.Parse(saved);
        Assert.Equal(version, json.RootElement.GetProperty("version").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("autoTiebreak", out _));
        Assert.False(json.RootElement.GetProperty("roundsPlayed")[0][0].TryGetProperty("games", out _));
        Assert.Empty(pairing.Games);
    }

    [Fact]
    public void AutomaticOptionsAreCopiedAndCannotBeBypassedByReportingAResultOrReusingAReportedGame()
    {
        var db = Content();
        var options = new TournamentOptions { AutoTiebreak = true };
        var tournament = new Tournament(db, "single", options: options);
        AddPlayers(tournament, db, 2);
        options.AutoTiebreak = false;
        Assert.True(tournament.AutoTiebreak);
        var pairing = Assert.Single(tournament.NextRound());
        string before = tournament.ToJson();
        ChineseFailure(() => tournament.ReportResult(pairing, 0));
        Assert.Equal(before, tournament.ToJson());
        var game = Report(tournament, pairing, -1);
        before = tournament.ToJson();
        ChineseFailure(() => tournament.ReportGame(pairing, game));
        Assert.Equal(before, tournament.ToJson());
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("team-order")]
    [InlineData("name")]
    [InlineData("unfinished")]
    [InlineData("forged-winner")]
    [InlineData("forged-hp")]
    [InlineData("lead")]
    [InlineData("ban")]
    [InlineData("modifier")]
    public void InvalidGamesCannotChangeTournamentHistory(string defect)
    {
        var db = Content();
        var tournament = Make(db);
        var pairing = Assert.Single(tournament.NextRound());
        var original = tournament.CreateGame(pairing).Record;
        var teams = original.Teams.Select(team => (IList<string>)team.ToList()).ToArray();
        var names = original.Names.ToArray();
        var options = new BattleOptions
        {
            Seed = defect == "seed" ? original.Seed ^ 1u : original.Seed,
            LeadPick = defect == "lead", BanOne = defect == "ban",
            Modifiers = defect == "modifier" ? new List<string> { "hp_x2" } : null
        };
        if (defect == "team-order") teams[0] = teams[0].Reverse().ToList();
        if (defect == "name") names[0] += "其他选手";
        var game = new Battle(db, teams, names, options);
        if (defect != "unfinished") Finish(game, -1);
        if (defect == "forged-winner") game.Winner = 0;
        if (defect == "forged-hp") game.Active(0).Hp--;
        string before = tournament.ToJson(), record = game.Record.ToJson(), content = db.ContentHash();
        ulong state = game.StateHash();

        ChineseFailure(() => tournament.ReportGame(pairing, game));

        Assert.Equal(before, tournament.ToJson());
        Assert.Equal(record, game.Record.ToJson());
        Assert.Equal(state, game.StateHash());
        Assert.Equal(content, db.ContentHash());
        Assert.Empty(pairing.Games);
    }

    [Fact]
    public void ByesForeignPairingsAndOutOfRangeAttemptsNeverCreatePhantomGames()
    {
        var db = Content();
        var tournament = Make(db, count: 5);
        var opening = tournament.NextRound();
        Assert.Equal(3, opening.Count(pair => pair.IsBye));
        var real = Assert.Single(opening, pair => !pair.IsBye);
        var game = Finish(tournament.CreateGame(real), 0);
        var foreign = Assert.Single(Make(db).NextRound());
        string before = tournament.ToJson();
        foreach (var bye in opening.Where(pair => pair.IsBye))
        {
            ChineseFailure(() => tournament.CreateGame(bye));
            ChineseFailure(() => tournament.ReportGame(bye, game));
            Assert.Empty(bye.Games);
            Assert.False(bye.AdvancedBySeed);
        }
        ChineseFailure(() => tournament.CreateGame(foreign));
        ChineseFailure(() => tournament.ReportGame(foreign, game));
        ChineseFailure(() => tournament.GameSeed(real, -1));
        ChineseFailure(() => tournament.GameSeed(real, 4));
        Assert.Equal(before, tournament.ToJson());
        tournament.ReportGame(real, game);
        while (!tournament.IsFinished)
            foreach (var pairing in tournament.NextRound().Where(pair => !pair.IsBye)) Report(tournament, pairing, 0);
        Assert.All(tournament.Pairings.Where(pair => pair.IsBye), pair => Assert.Empty(pair.Games));
    }

    [Theory]
    [InlineData("attempt-gap")]
    [InlineData("seed")]
    [InlineData("record-hash")]
    [InlineData("winner-cache")]
    [InlineData("fallback-cache")]
    [InlineData("earlier-winner")]
    [InlineData("fifth-game")]
    [InlineData("version-downgrade")]
    public void VersionThreeRestorationRejectsInconsistentHistoryAndDerivedCaches(string defect)
    {
        var db = Content();
        var tournament = Make(db, order: new[] { 1, 0 });
        var pairing = Assert.Single(tournament.NextRound());
        FourDraws(tournament, pairing);
        string saved = tournament.ToJson();
        var root = JsonNode.Parse(saved).AsObject();
        Assert.Equal(3, root["version"].GetValue<int>());
        Assert.True(root["autoTiebreak"].GetValue<bool>());
        var match = root["roundsPlayed"][0][0].AsObject();
        var games = match["games"].AsArray();
        switch (defect)
        {
            case "attempt-gap": games[1]["attempt"] = 2; break;
            case "seed": games[1]["seed"] = games[1]["seed"].GetValue<uint>() ^ 1u; break;
            case "record-hash": games[0]["recordHash"] = "不是摘要"; break;
            case "winner-cache": match["winnerSide"] = 1; break;
            case "fallback-cache": match["advancedBySeed"] = false; break;
            case "earlier-winner": games[0]["winnerSide"] = 0; break;
            case "fifth-game": games.Add(games[3].DeepClone()); break;
            case "version-downgrade": root["version"] = 2; break;
        }
        ChineseFailure(() => Tournament.FromJson(db, root.ToJsonString()));
        Assert.Equal(saved, tournament.ToJson());
    }

    static Battle StatisticalGame(Tournament tournament, Pairing pairing, int attempt)
    {
        var battle = tournament.CreateGame(pairing);
        if (attempt == 0)
        {
            battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
            Assert.Equal(Phase.Replace, battle.Phase);
            Assert.True(battle.Need[1]);
            battle.Replace(1, 1);
        }
        else if (attempt == 1) battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        return Finish(battle, attempt == 3 ? 1 : -1);
    }

    [Fact]
    public void EveryGameIsArchivedIdempotentlyAcrossRestartAndAllGamesContributeToTheReport()
    {
        using var files = new TestDirectory();
        var db = Content();
        var tournament = Make(db, order: new[] { 1, 0 });
        var pairing = Assert.Single(tournament.NextRound());
        string directory = Path.Combine(files.Root, "library");
        var library = new ReplayLibrary(directory, maxCount: 1, clock: () => Epoch);
        var oldBattle = Finish(new Battle(db,
            new IList<string>[] { db.Streamers.Take(6).Select(unit => unit.Id).ToList(), db.Streamers.Skip(6).Select(unit => unit.Id).ToList() },
            new[] { "旧甲", "旧乙" }, 17), -1);
        string oldId = library.Save(oldBattle.Record, BattleSummary.From(oldBattle));
        byte[] oldBytes = File.ReadAllBytes(Path.Combine(directory, oldId + ".json"));
        var ids = new List<string>();
        var records = new List<string>();
        for (int attempt = 0; attempt <= 3; attempt++)
        {
            var battle = StatisticalGame(tournament, pairing, attempt);
            string tournamentBeforeSave = tournament.ToJson();
            string id = library.SaveTournamentGame(tournament, pairing, battle, attempt);
            Assert.Equal(tournamentBeforeSave, tournament.ToJson());
            Assert.Equal(attempt, pairing.Games.Count);
            Assert.Equal(id, library.SaveTournamentGame(tournament, pairing, battle, attempt));
            if (attempt == 1)
            {
                // 录像已落盘、赛事未报分时中断；恢复后重打一局只能复用相同关联。
                tournament = Tournament.FromJson(db, tournamentBeforeSave);
                pairing = Assert.Single(tournament.Pairings);
                var retry = StatisticalGame(tournament, pairing, attempt);
                Assert.Equal(battle.Record.ToJson(), retry.Record.ToJson());
                Assert.Equal(id, library.SaveTournamentGame(tournament, pairing, retry, attempt));
                battle = retry;
            }
            tournament.ReportGame(pairing, battle);
            Assert.Equal(id, library.SaveTournamentGame(tournament, pairing, battle, attempt));
            ids.Add(id);
            records.Add(battle.Record.ToJson());
        }
        Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(oldBytes, File.ReadAllBytes(Path.Combine(directory, oldId + ".json")));
        var reader = ReplayLibrary.OpenReadOnly(directory);
        for (int attempt = 0; attempt <= 3; attempt++)
        {
            Assert.Equal(records[attempt], reader.Load(ids[attempt]).ToJson());
            using var archive = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, ids[attempt] + ".json")));
            var tag = archive.RootElement.GetProperty("library").GetProperty("tournament");
            Assert.Equal(2, tag.GetProperty("version").GetInt32());
            Assert.Equal(pairing.Id, tag.GetProperty("pairingId").GetInt32());
            Assert.Equal(attempt, tag.GetProperty("attempt").GetInt32());
        }
        var before = Files(files.Root);
        string tournamentJson = tournament.ToJson();
        var report = TournamentReport.Build(tournament, reader);
        using var json = JsonDocument.Parse(report.ToJson());
        var root = json.RootElement;
        Coverage(root, 4, 4, 0);
        var match = Match(root, pairing.Id);
        Assert.False(match.GetProperty("advancedBySeed").GetBoolean());
        Assert.Equal("saved", match.GetProperty("replay").GetProperty("status").GetString());
        Assert.Equal(4, match.GetProperty("games").GetArrayLength());
        for (int attempt = 0; attempt <= 3; attempt++)
        {
            var row = match.GetProperty("games")[attempt];
            Assert.Equal(attempt, row.GetProperty("attempt").GetInt32());
            Assert.Equal(pairing.Games[attempt].Seed, row.GetProperty("seed").GetUInt32());
            Assert.Equal(attempt == 3 ? 1 : -1, row.GetProperty("winnerSide").GetInt32());
            Assert.Equal(pairing.Games[attempt].RecordHash, row.GetProperty("recordHash").GetString());
            Assert.Equal("saved", row.GetProperty("replay").GetProperty("status").GetString());
            Assert.Equal(ids[attempt], row.GetProperty("replay").GetProperty("id").GetString());
        }
        Assert.Equal(100, root.GetProperty("maxDamage").GetProperty("amount").GetInt32());
        Assert.Equal(ids[0], root.GetProperty("maxDamage").GetProperty("replayId").GetString());
        Assert.Equal("u06", root.GetProperty("mostKos").GetProperty("streamerId").GetString());
        Assert.Equal(1, root.GetProperty("mostKos").GetProperty("count").GetInt32());
        Assert.Equal("u00", root.GetProperty("mostAppearances").GetProperty("streamerId").GetString());
        Assert.Equal(4, root.GetProperty("mostAppearances").GetProperty("count").GetInt32());
        Assert.Equal(report.ToJson(), TournamentReport.Build(Tournament.FromJson(db, tournamentJson), reader).ToJson());
        Assert.Equal(tournamentJson, tournament.ToJson());
        Assert.Equal(before, Files(files.Root));
    }

    [Fact]
    public void SameSeedAndWinnerWithDifferentActionsCannotReplaceAnAlreadyRegisteredGame()
    {
        using var files = new TestDirectory();
        var db = Content();
        var tournament = Make(db);
        var pairing = Assert.Single(tournament.NextRound());
        var library = new ReplayLibrary(Path.Combine(files.Root, "library"), clock: () => Epoch);
        var first = Finish(tournament.CreateGame(pairing), -1);
        string id = library.SaveTournamentGame(tournament, pairing, first, 0);
        tournament.ReportGame(pairing, first);
        var different = new Battle(db, first.Record.Teams, first.Record.Names, first.Seed);
        different.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Finish(different, -1);
        Assert.NotEqual(Hash(first.Record), Hash(different.Record));
        var before = Files(files.Root);
        string tournamentJson = tournament.ToJson();

        ChineseFailure(() => library.SaveTournamentGame(tournament, pairing, different, 0));
        ChineseFailure(() => library.SaveTournamentGame(tournament, pairing, first, 1));

        Assert.Equal(before, Files(files.Root));
        Assert.Equal(tournamentJson, tournament.ToJson());
        Assert.Equal(id, library.SaveTournamentGame(tournament, pairing, first, 0));
    }

    [Fact]
    public void PendingPairingsStillReportEveryConfirmedDrawAndItsMissingArchive()
    {
        using var files = new TestDirectory();
        var tournament = Make(Content());
        var pairing = Assert.Single(tournament.NextRound());
        string directory = Path.Combine(files.Root, "library");
        var library = new ReplayLibrary(directory, clock: () => Epoch);
        var first = Finish(tournament.CreateGame(pairing), -1);
        string id = library.SaveTournamentGame(tournament, pairing, first, 0);
        tournament.ReportGame(pairing, first);
        Report(tournament, pairing, -1);
        Assert.Null(pairing.WinnerSide);
        var before = Files(files.Root);
        using var json = JsonDocument.Parse(TournamentReport.Build(tournament, ReplayLibrary.OpenReadOnly(directory)).ToJson());
        Coverage(json.RootElement, 2, 1, 1);
        var match = Match(json.RootElement, pairing.Id);
        Assert.Equal("partial", match.GetProperty("replay").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, match.GetProperty("winnerSide").ValueKind);
        var games = match.GetProperty("games");
        Assert.Equal(2, games.GetArrayLength());
        Assert.Equal(id, games[0].GetProperty("replay").GetProperty("id").GetString());
        Assert.Equal("saved", games[0].GetProperty("replay").GetProperty("status").GetString());
        Assert.Equal("not_saved", games[1].GetProperty("replay").GetProperty("status").GetString());
        Assert.Equal(before, Files(files.Root));
    }

    [Fact]
    public void RemovingARecordedRngCheckpointCannotBeHiddenByReplayRegeneratingIt()
    {
        using var files = new TestDirectory();
        var db = Content();
        var tournament = Make(db);
        var pairing = Assert.Single(tournament.NextRound());
        string directory = Path.Combine(files.Root, "library");
        var library = new ReplayLibrary(directory, clock: () => Epoch);
        var battle = Finish(tournament.CreateGame(pairing), -1);
        string id = library.SaveTournamentGame(tournament, pairing, battle, 0);
        tournament.ReportGame(pairing, battle);
        string path = Path.Combine(directory, id + ".json");
        var archive = JsonNode.Parse(File.ReadAllText(path)).AsObject();
        var terminal = Assert.Single(archive["events"].AsArray(), item => item["type"].GetValue<string>() == "surrender").AsObject();
        Assert.True(terminal.Remove("rng"));
        File.WriteAllText(path, archive.ToJsonString(), new UTF8Encoding(false));
        var altered = BattleRecord.FromJson(File.ReadAllText(path));
        Assert.NotEqual(pairing.Games[0].RecordHash, Hash(altered));
        Assert.Equal(battle.StateHash(), Replay.Run(db, altered).StateHash());
        var before = Files(files.Root);

        using var report = JsonDocument.Parse(TournamentReport.Build(tournament, ReplayLibrary.OpenReadOnly(directory)).ToJson());

        Coverage(report.RootElement, 1, 0, 0, 1);
        Assert.Equal("mismatch", Match(report.RootElement, pairing.Id).GetProperty("games")[0].GetProperty("replay").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("maxDamage").ValueKind);
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("mostKos").ValueKind);
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("mostAppearances").ValueKind);
        Assert.Equal(before, Files(files.Root));
    }

    [Fact]
    public void AValidOptionalRngOmissionBeforeRegistrationKeepsItsOriginalRecordHashAndCanBeReported()
    {
        using var files = new TestDirectory();
        var db = Content();
        var tournament = Make(db);
        var pairing = Assert.Single(tournament.NextRound());
        var battle = Finish(tournament.CreateGame(pairing), -1);
        var terminal = Assert.Single(battle.Record.Events);
        Assert.Equal(BattleRecordEventType.Surrender, terminal.Type);
        Assert.NotNull(terminal.RngState);
        terminal.RngState = null;
        string source = battle.Record.ToJson();
        string expectedHash = Hash(battle.Record);
        Assert.Equal(battle.StateHash(), Replay.Run(db, battle.Record).StateHash());
        string directory = Path.Combine(files.Root, "library");
        var library = new ReplayLibrary(directory, clock: () => Epoch);
        tournament.ReportGame(pairing, battle);
        string id = library.SaveTournamentGame(tournament, pairing, battle, 0);

        Assert.Equal(expectedHash, Assert.Single(pairing.Games).RecordHash);
        Assert.Equal(source, battle.Record.ToJson());
        Assert.Equal(source, ReplayLibrary.OpenReadOnly(directory).Load(id).ToJson());
        using var report = JsonDocument.Parse(TournamentReport.Build(tournament, ReplayLibrary.OpenReadOnly(directory)).ToJson());
        Coverage(report.RootElement, 1, 1, 0);
        Assert.Equal("saved", Match(report.RootElement, pairing.Id).GetProperty("games")[0].GetProperty("replay").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("clone")]
    [InlineData("replay")]
    public void BranchGamesWithMatchingTournamentHeadersCannotBeReportedOrArchived(string representation)
    {
        using var files = new TestDirectory();
        var db = Content();
        var tournament = Make(db);
        var pairing = Assert.Single(tournament.NextRound());
        var source = tournament.CreateGame(pairing);
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        string sourceJson = source.Record.ToJson();
        ulong sourceState = source.StateHash();
        var branch = Finish(ReplayBranch.Start(db, source.Record, 1, 0, AiLevel.Normal), 0);
        var candidate = representation == "clone" ? branch.Clone()
            : representation == "replay" ? Replay.Run(db, BattleRecord.FromJson(branch.Record.ToJson())) : branch;
        Assert.True(candidate.IsBranch);
        Assert.Equal(Phase.Over, candidate.Phase);
        Assert.Equal(0, candidate.Winner);
        Assert.Equal(tournament.GameSeed(pairing, 0), candidate.Seed);
        Assert.Equal(candidate.Seed, candidate.Record.Seed);
        Assert.Equal(db.PackId, candidate.Record.PackId);
        Assert.Equal(db.ContentHash(), candidate.Record.ContentHash);
        Assert.False(candidate.Record.LeadPick);
        Assert.False(candidate.Record.BanOne);
        Assert.True(candidate.Record.Modifiers == null || candidate.Record.Modifiers.Count == 0);
        Assert.True(Replay.Check(db, candidate.Record).CanReplay);
        for (int side = 0; side < 2; side++)
        {
            var player = tournament.Players[side == 0 ? pairing.Player0 : pairing.Player1];
            Assert.Equal(player.Name, candidate.Record.Names[side]);
            Assert.Equal(player.Team, candidate.Record.Teams[side]);
        }
        var library = new ReplayLibrary(Path.Combine(files.Root, "library"), clock: () => Epoch);
        string tournamentJson = tournament.ToJson(), record = candidate.Record.ToJson();
        ulong state = candidate.StateHash();
        var before = Files(files.Root);

        var reportError = ChineseFailure(() => tournament.ReportGame(pairing, candidate));
        var archiveError = ChineseFailure(() => library.SaveTournamentGame(tournament, pairing, candidate, 0));

        Assert.Contains("接手", reportError.Message);
        Assert.Contains("接手", archiveError.Message);
        Assert.Equal(tournamentJson, tournament.ToJson());
        Assert.Empty(pairing.Games);
        Assert.Equal(before, Files(files.Root));
        Assert.Equal(record, candidate.Record.ToJson());
        Assert.Equal(state, candidate.StateHash());
        Assert.Equal(sourceJson, source.Record.ToJson());
        Assert.Equal(sourceState, source.StateHash());
    }
}
