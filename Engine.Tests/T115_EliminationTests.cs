using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T115_EliminationTests
{
    [Theory]
    [InlineData(4, 2, 0)]
    [InlineData(5, 3, 3)]
    [InlineData(8, 3, 0)]
    public void SingleEliminationUsesSeededByesAndEliminatesEachLoserExactlyOnce(int count, int rounds, int byes)
    {
        var tournament = Make(Tournament.SingleElimination, count);
        Assert.True(tournament.IsElimination);
        Assert.Equal("单败淘汰赛", tournament.FormatName);
        Assert.Equal(1, tournament.BestOf);
        Assert.Equal(rounds, tournament.Rounds);
        Assert.Null(tournament.Champion);
        var opening = tournament.NextRound();
        Assert.Equal(byes, opening.Count(pairing => pairing.IsBye));
        if (count == 5)
        {
            Assert.Equal(new[] { 0, 1, 2 }, opening.Where(pairing => pairing.IsBye).Select(pairing => pairing.Player0).OrderBy(id => id));
            var real = Assert.Single(opening, pairing => !pairing.IsBye);
            Assert.Equal(new[] { 3, 4 }, new[] { real.Player0, real.Player1 });
        }
        else
        {
            int[] slots = count == 4 ? new[] { 0, 3, 1, 2 } : new[] { 0, 7, 3, 4, 1, 6, 2, 5 };
            Assert.Equal(slots, opening.SelectMany(pairing => new[] { pairing.Player0, pairing.Player1 }));
        }
        var losses = PlayAndCheck(tournament, pairing => pairing.Player0 < pairing.Player1 ? 0 : 1);
        Assert.Equal(rounds, tournament.Round);
        Assert.Equal(rounds, tournament.Rounds);
        Assert.Equal(count - 1, tournament.Pairings.Count(pairing => !pairing.IsBye));
        Assert.Equal(byes, tournament.Pairings.Count(pairing => pairing.IsBye));
        Assert.Equal(0, tournament.Champion);
        Assert.Equal(0, losses[0]);
        Assert.All(losses.Skip(1), lost => Assert.Equal(1, lost));
        Assert.All(tournament.Pairings, pairing =>
        {
            Assert.Equal("single", pairing.Bracket);
            Assert.Equal(pairing.Round, pairing.BracketRound);
            if (pairing.IsBye) Assert.Equal(1, pairing.Round);
        });
        AssertRanking(tournament, Enumerable.Range(0, count).ToArray());
    }

    [Theory]
    [InlineData(true, 0, 0, 14)]
    [InlineData(true, 1, 0, 15)]
    [InlineData(true, 1, 1, 15)]
    [InlineData(false, 0, 0, 14)]
    [InlineData(false, 1, 0, 14)]
    public void DoubleEliminationRequiresTwoLossesAndHonorsTheFinalReset(bool reset, int finalWinner, int resetWinner, int matches)
    {
        var tournament = Make(Tournament.DoubleElimination, 8, new TournamentOptions { ResetFinal = reset });
        Assert.Equal("双败淘汰赛", tournament.FormatName);
        Assert.Equal(reset ? 7 : 6, tournament.Rounds);
        var losses = PlayAndCheck(tournament, pairing => Result(pairing, finalWinner, resetWinner));
        var final = Assert.Single(tournament.Pairings, pairing => pairing.Bracket == "final");
        var resets = tournament.Pairings.Where(pairing => pairing.Bracket == "reset").ToArray();
        Assert.Equal(reset && finalWinner == 1 ? 1 : 0, resets.Length);
        if (resets.Length > 0)
        {
            Assert.Equal(final.Player0, resets[0].Player0);
            Assert.Equal(final.Player1, resets[0].Player1);
            Assert.True(resets[0].Round > final.Round);
        }
        var decisive = resets.Length > 0 ? resets[0] : final;
        Assert.Equal(Winner(decisive), tournament.Champion);
        Assert.Equal(matches, tournament.Pairings.Count);
        Assert.DoesNotContain(tournament.Pairings, pairing => pairing.IsBye);
        for (int player = 0; player < losses.Length; player++)
        {
            if (player == tournament.Champion) Assert.InRange(losses[player], 0, 1);
            else if (!reset && finalWinner == 1 && player == final.Player0) Assert.Equal(1, losses[player]);
            else Assert.Equal(2, losses[player]);
        }
        Assert.Equal(tournament.Round, tournament.Rounds);
        AssertRanking(tournament, Enumerable.Range(0, 8).ToArray());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(16)]
    public void DoubleEliminationSmallAndPaddedBracketsNeverCreatePhantomLosers(int count)
    {
        var tournament = Make(Tournament.DoubleElimination, count);
        var losses = PlayAndCheck(tournament, pairing => Result(pairing, 1, 1));
        Assert.Equal(2 * count - 1, tournament.Pairings.Count(pairing => !pairing.IsBye));
        Assert.All(tournament.Pairings, pairing =>
        {
            Assert.InRange(pairing.Player0, 0, count - 1);
            Assert.InRange(pairing.Player1, -1, count - 1);
        });
        for (int player = 0; player < count; player++) Assert.Equal(player == tournament.Champion ? 1 : 2, losses[player]);
    }

    [Fact]
    public void ExplicitSeedOrderIsCopiedReadOnlyAndGivesTheFirstSeedsByes()
    {
        var order = new List<int> { 4, 1, 3, 0, 2 };
        var options = new TournamentOptions { SeedOrder = order, ResetFinal = false };
        var tournament = Make(Tournament.SingleElimination, 5, options);
        order.Reverse();
        options.SeedOrder = new[] { 0, 1, 2, 3, 4 };
        options.Seed = 115;
        options.ResetFinal = true;
        Assert.Null(tournament.Seed);
        Assert.False(tournament.ResetFinal);
        Assert.Equal(new[] { 4, 1, 3, 0, 2 }, tournament.SeedOrder);
        var exposed = Assert.IsAssignableFrom<IList<int>>(tournament.SeedOrder);
        Assert.Throws<NotSupportedException>(() => exposed[0] = 0);
        var opening = tournament.NextRound();
        Assert.Equal(new[] { 1, 3, 4 }, opening.Where(pairing => pairing.IsBye).Select(pairing => pairing.Player0).OrderBy(id => id));
        var real = Assert.Single(opening, pairing => !pairing.IsBye);
        Assert.Equal(new[] { 0, 2 }, new[] { real.Player0, real.Player1 });
        PlayAndCheck(tournament, pairing => pairing.Player0 > pairing.Player1 ? 0 : 1);
        AssertRanking(tournament, new[] { 4, 1, 3, 0, 2 });
    }

    [Theory]
    [InlineData("single", 0u)]
    [InlineData("single", 115u)]
    [InlineData("double", 115u)]
    [InlineData("double", uint.MaxValue)]
    public void SeededPairingsAndCompleteBytesAreDeterministic(string format, uint seed)
    {
        var db = BattleTests.LoadPack("original");
        string hash = db.ContentHash();
        var first = Make(format, 8, new TournamentOptions { Seed = seed }, db);
        var second = Make(format, 8, new TournamentOptions { Seed = seed }, db);
        Assert.Equal(seed, first.Seed);
        Assert.Null(first.SeedOrder);
        while (!first.IsFinished)
        {
            Assert.True(first.Round < 12);
            var left = first.NextRound();
            var right = second.NextRound();
            Assert.Equal(first.ToJson(), second.ToJson());
            foreach (var pairing in left.Where(pairing => !pairing.IsBye)) first.ReportResult(pairing, Result(pairing, 1, 0));
            foreach (var pairing in right.Where(pairing => !pairing.IsBye).Reverse()) second.ReportResult(pairing, Result(pairing, 1, 0));
            Assert.Equal(first.ToJson(), second.ToJson());
        }
        Assert.True(second.IsFinished);
        Assert.Equal(hash, db.ContentHash());
    }

    [Theory]
    [InlineData("single", 5)]
    [InlineData("double", 8)]
    public void ResumeEveryPendingAndCompletedStageProducesTheUninterruptedBytes(string format, int count)
    {
        var db = BattleTests.LoadPack("original");
        var continuous = Make(format, count, new TournamentOptions { Seed = 115 }, db);
        var resumed = Tournament.FromJson(db, continuous.ToJson());
        while (!continuous.IsFinished)
        {
            Assert.True(continuous.Round < 12);
            var current = continuous.NextRound();
            resumed.NextRound();
            Assert.Equal(continuous.ToJson(), resumed.ToJson());
            resumed = RestoreAndCheckQueries(db, resumed);
            foreach (var pairing in current.Where(pairing => !pairing.IsBye))
            {
                int result = Result(pairing, 1, 1);
                continuous.ReportResult(pairing, result);
                resumed.ReportResult(resumed.Pairings.Single(item => item.Id == pairing.Id), result);
                Assert.Equal(continuous.ToJson(), resumed.ToJson());
                resumed = RestoreAndCheckQueries(db, resumed);
            }
        }
        Assert.Equal(continuous.Champion, resumed.Champion);
        Assert.Equal(continuous.ToJson(), resumed.ToJson());
        if (format == "double") Assert.Contains(resumed.Pairings, pairing => pairing.Bracket == "reset");
    }

    [Fact]
    public void IncompleteRoundReturnsTheSameObjectsAndReturnedCollectionsCannotChangeHistory()
    {
        var tournament = Make("double", 8);
        var first = tournament.NextRound();
        tournament.ReportResult(first[0], 1);
        string before = tournament.ToJson();
        var again = tournament.NextRound();
        Assert.Equal(1, tournament.Round);
        for (int i = 0; i < first.Count; i++) Assert.Same(first[i], again[i]);
        again.Clear();
        tournament.Standings().Clear();
        var history = Assert.IsAssignableFrom<IList<Pairing>>(tournament.Pairings);
        Assert.Throws<NotSupportedException>(() => history.Clear());
        Assert.Equal(before, tournament.ToJson());
        Assert.Throws<InvalidOperationException>(() => tournament.AddPlayer("追加", Code(BattleTests.LoadPack("original"), 0)));
        Assert.Equal(before, tournament.ToJson());
    }

    [Fact]
    public void DoubleEliminationCollectsIndependentReadyGroupsAndRanksSurvivorsBeforeEliminatedPlayers()
    {
        var tournament = Make("double", 8);
        var first = tournament.NextRound();
        Assert.Equal(4, first.Count);
        foreach (var pairing in first) tournament.ReportResult(pairing, 1);
        var second = tournament.NextRound();
        Assert.Equal(4, second.Count);
        Assert.Equal(2, second.Count(pairing => pairing.Bracket == "winners" && pairing.BracketRound == 2));
        Assert.Equal(2, second.Count(pairing => pairing.Bracket == "losers" && pairing.BracketRound == 1));
        var eliminated = new HashSet<int>();
        foreach (var pairing in second)
        {
            tournament.ReportResult(pairing, 1);
            if (pairing.Bracket == "losers") eliminated.Add(pairing.Player0);
        }
        var standings = tournament.Standings();
        Assert.DoesNotContain(standings.Take(6), row => eliminated.Contains(row.Player));
        Assert.Equal(eliminated.OrderBy(id => id), standings.Skip(6).Select(row => row.Player));
        Assert.Null(tournament.Champion);
        Assert.False(tournament.IsFinished);
    }

    [Fact]
    public void InvalidConfigurationAndSeedPermutationAreRejectedWithoutStarting()
    {
        var db = BattleTests.LoadPack("original");
        Assert.Throws<ArgumentException>(() => new Tournament(db, "single", 2));
        Assert.Throws<ArgumentException>(() => new Tournament(db, "double", 2));
        Assert.Throws<ArgumentException>(() => new Tournament(db, "single", options: new TournamentOptions { Seed = 1, SeedOrder = new[] { 0, 1 } }));
        foreach (var order in new[] { new int[0], new[] { 0 }, new[] { 0, 0 }, new[] { -1, 0 }, new[] { 0, 2 } })
            Assert.Throws<ArgumentException>(() => new Tournament(db, "single", options: new TournamentOptions { SeedOrder = order }));
        var shortRegistration = Make("single", 3, new TournamentOptions { SeedOrder = new[] { 3, 2, 1, 0 } }, db);
        string before = shortRegistration.ToJson();
        Assert.Throws<InvalidOperationException>(() => shortRegistration.NextRound());
        Assert.Equal(before, shortRegistration.ToJson());
        Assert.Equal(0, shortRegistration.Round);
        Assert.Empty(shortRegistration.Pairings);
        shortRegistration.AddPlayer("选手 3", Code(db, 3));
        Assert.NotEmpty(shortRegistration.NextRound());
        var extraRegistration = Make("double", 5, new TournamentOptions { SeedOrder = new[] { 0, 1, 2, 3 } }, db);
        before = extraRegistration.ToJson();
        Assert.Throws<InvalidOperationException>(() => extraRegistration.NextRound());
        Assert.Equal(before, extraRegistration.ToJson());
        var singleton = Make("single", 1, db: db);
        Assert.Throws<InvalidOperationException>(() => singleton.NextRound());
        Assert.Empty(singleton.Pairings);
    }

    [Theory]
    [InlineData("single")]
    [InlineData("double")]
    public void IllegalResultForeignPairingAndByeOverwriteAreAtomic(string format)
    {
        var tournament = Make(format, 5);
        var current = tournament.NextRound();
        var real = current.First(pairing => !pairing.IsBye);
        var bye = current.First(pairing => pairing.IsBye);
        var foreign = Make(format, 5).NextRound().First(pairing => !pairing.IsBye);
        string before = tournament.ToJson();
        var drawError = Assert.Throws<ArgumentException>(() => tournament.ReportResult(real, -1));
        Assert.Contains("平局", drawError.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => tournament.ReportResult(real, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tournament.ReportResult(real, -2));
        Assert.Throws<ArgumentNullException>(() => tournament.ReportResult(null, 0));
        Assert.Throws<ArgumentException>(() => tournament.ReportResult(foreign, 0));
        Assert.Throws<InvalidOperationException>(() => tournament.ReportResult(bye, 0));
        Assert.Equal(before, tournament.ToJson());
        Assert.Null(real.WinnerSide);
        tournament.ReportResult(real, 1);
        before = tournament.ToJson();
        Assert.Throws<InvalidOperationException>(() => tournament.ReportResult(real, 0));
        Assert.Equal(before, tournament.ToJson());
    }

    [Fact]
    public void VersionTwoRejectsCorruptedBracketConfigurationAndIllegalAdvancement()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = Make("single", 4, new TournamentOptions { Seed = uint.MaxValue }, db);
        var current = tournament.NextRound();
        tournament.ReportResult(current[0], 0);
        string json = tournament.ToJson();
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(2, parsed.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(uint.MaxValue, parsed.RootElement.GetProperty("seed").GetUInt32());
        foreach (var bad in new[]
        {
            json.Replace("\"version\":2", "\"version\":1"),
            json.Replace("\"format\":\"single\"", "\"format\":\"double\""),
            json.Replace("\"bracket\":\"single\"", "\"bracket\":\"losers\""),
            json.Replace("\"bracketRound\":1", "\"bracketRound\":2"),
            json.Replace("\"winnerSide\":0", "\"winnerSide\":-1"),
            json.Replace("\"seed\":4294967295", "\"seed\":4294967296"),
            json.Replace("\"seed\":4294967295", "\"seed\":0.5"),
            json.Replace("\"seedOrder\":null", "\"seedOrder\":[0,1,2,3]"),
            json.Replace("\"resetFinal\":true", "\"resetFinal\":1")
        })
        {
            Assert.NotEqual(json, bad);
            Assert.Throws<FormatException>(() => Tournament.FromJson(db, bad));
        }
        Assert.Equal(json, tournament.ToJson());
    }

    [Theory]
    [InlineData("roundrobin")]
    [InlineData("swiss")]
    public void ExistingFormatsKeepVersionOneAndTheirDrawSemantics(string format)
    {
        var db = BattleTests.LoadPack("original");
        var tournament = Make(format, 4, db: db);
        var first = tournament.NextRound();
        tournament.ReportResult(first[0], -1);
        Assert.False(tournament.IsElimination);
        Assert.Null(tournament.Champion);
        string json = tournament.ToJson();
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(1, parsed.RootElement.GetProperty("version").GetInt32());
        foreach (string field in new[] { "seed", "seedOrder", "resetFinal" }) Assert.False(parsed.RootElement.TryGetProperty(field, out _));
        foreach (var pairing in parsed.RootElement.GetProperty("roundsPlayed")[0].EnumerateArray())
            Assert.False(pairing.TryGetProperty("bracket", out _));
        Assert.Equal(json, Tournament.FromJson(db, json).ToJson());
    }

    [Theory]
    [InlineData("single", 5, 4)]
    [InlineData("double", 8, 15)]
    public void ReportAndArchiveSupportEliminationWithoutAdvancingOrWriting(string format, int count, int matches)
    {
        using var files = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var tournament = Make(format, count, db: db);
        var library = new ReplayLibrary(files.Root, policy: PackPolicy.Empty);
        var archiveIds = new Dictionary<int, string>();
        while (!tournament.IsFinished)
        {
            Assert.True(tournament.Round < 12);
            foreach (var pairing in tournament.NextRound().Where(pairing => !pairing.IsBye))
            {
                int result = Result(pairing, 1, 1);
                var battle = CompletedBattle(db, tournament, pairing, result);
                if (pairing.Id % 2 == 0) tournament.ReportResult(pairing, result);
                archiveIds[pairing.Id] = library.SaveTournament(tournament, pairing, battle);
                if (!pairing.WinnerSide.HasValue) tournament.ReportResult(pairing, result);
            }
        }
        string before = tournament.ToJson();
        var diskBefore = FileSnapshot(files.Root);
        var readOnly = ReplayLibrary.OpenReadOnly(files.Root, PackPolicy.Empty);
        var report = TournamentReport.Build(tournament, readOnly);
        string json = report.ToJson();
        Assert.Equal(json, TournamentReport.Build(Tournament.FromJson(db, before), readOnly).ToJson());
        Assert.Equal(before, tournament.ToJson());
        Assert.Equal(diskBefore, FileSnapshot(files.Root));
        Assert.Contains(tournament.FormatName, report.ToText(), StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Assert.Equal(format, root.GetProperty("format").GetString());
        Assert.Equal(tournament.Champion.Value, root.GetProperty("champion").GetInt32());
        Assert.Equal(tournament.Champion.Value, root.GetProperty("standings")[0].GetProperty("player").GetInt32());
        Assert.Equal(1, root.GetProperty("bestOf").GetInt32());
        Assert.True(root.GetProperty("resetFinal").GetBoolean());
        Assert.Equal(matches, root.GetProperty("coverage").GetProperty("completedGames").GetInt32());
        Assert.Equal(matches, root.GetProperty("coverage").GetProperty("archivedGames").GetInt32());
        foreach (var round in root.GetProperty("rounds").EnumerateArray())
            foreach (var match in round.GetProperty("matches").EnumerateArray())
            {
                int id = match.GetProperty("pairingId").GetInt32();
                var pairing = tournament.Pairings.Single(item => item.Id == id);
                Assert.Equal(pairing.Bracket, match.GetProperty("bracket").GetString());
                Assert.Equal(pairing.BracketRound, match.GetProperty("bracketRound").GetInt32());
                Assert.Equal(pairing.IsBye ? "not_applicable" : "saved", match.GetProperty("replay").GetProperty("status").GetString());
                if (!pairing.IsBye) Assert.Equal(archiveIds[id], match.GetProperty("replay").GetProperty("id").GetString());
            }
    }

    [Fact]
    public void ArchiveKeysIsolateSeedOrderSeedAndResetConfiguration()
    {
        using var files = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var library = new ReplayLibrary(files.Root, policy: PackPolicy.Empty);
        var original = Make("double", 4, db: db);
        foreach (var pairing in original.NextRound())
        {
            library.SaveTournament(original, pairing, CompletedBattle(db, original, pairing, 0));
            original.ReportResult(pairing, 0);
        }
        foreach (var options in new[]
        {
            new TournamentOptions { Seed = 115 },
            new TournamentOptions { SeedOrder = new[] { 0, 1, 2, 3 } },
            new TournamentOptions { ResetFinal = false }
        })
        {
            var different = Make("double", 4, options, db);
            foreach (var pairing in different.NextRound()) different.ReportResult(pairing, 0);
            using var parsed = JsonDocument.Parse(TournamentReport.Build(different, library).ToJson());
            var coverage = parsed.RootElement.GetProperty("coverage");
            Assert.Equal(2, coverage.GetProperty("completedGames").GetInt32());
            Assert.Equal(0, coverage.GetProperty("archivedGames").GetInt32());
            Assert.Equal(2, coverage.GetProperty("missingGames").GetInt32());
            Assert.Equal(0, coverage.GetProperty("invalidGames").GetInt32());
        }
    }

    [Theory]
    [InlineData("single", true)]
    [InlineData("double", true)]
    [InlineData("roundrobin", false)]
    [InlineData("swiss", false)]
    public void DrawArchiveCannotBlockEliminationRematchAndExistingFormatsStillAcceptDraws(string format, bool reject)
    {
        using var files = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var tournament = Make(format, 2, db: db);
        var pairing = Assert.Single(tournament.NextRound());
        var first = tournament.Players[pairing.Player0];
        var second = tournament.Players[pairing.Player1];
        var draw = new Battle(db, new IList<string>[] { first.Team.ToArray(), second.Team.ToArray() },
            new[] { first.Name, second.Name }, 115u);
        draw.ResolveSurrenders(true, true);
        Assert.Equal(-1, draw.Winner);
        var library = new ReplayLibrary(files.Root, policy: PackPolicy.Empty);
        string before = tournament.ToJson();
        var filesBefore = FileSnapshot(files.Root);
        if (reject)
        {
            var error = Assert.Throws<InvalidOperationException>(() => library.SaveTournament(tournament, pairing, draw));
            Assert.Contains("平局", error.Message, StringComparison.Ordinal);
            Assert.Equal(before, tournament.ToJson());
            Assert.Equal(filesBefore, FileSnapshot(files.Root));
            string id = library.SaveTournament(tournament, pairing, CompletedBattle(db, tournament, pairing, 0));
            Assert.False(string.IsNullOrEmpty(id));
            tournament.ReportResult(pairing, 0);
        }
        else
        {
            string id = library.SaveTournament(tournament, pairing, draw);
            Assert.False(string.IsNullOrEmpty(id));
            tournament.ReportResult(pairing, -1);
        }
        using var report = JsonDocument.Parse(TournamentReport.Build(tournament, library).ToJson());
        Assert.Equal(1, report.RootElement.GetProperty("coverage").GetProperty("archivedGames").GetInt32());
    }

    static int[] PlayAndCheck(Tournament tournament, Func<Pairing, int> result)
    {
        var losses = new int[tournament.Players.Count];
        var ids = new HashSet<int>();
        int upperRounds = tournament.Rounds;
        int guard = 0;
        while (!tournament.IsFinished)
        {
            Assert.True(guard++ < 16, "有限淘汰赛必须终止。");
            var current = tournament.NextRound();
            Assert.NotEmpty(current);
            Assert.InRange(tournament.Round, 1, upperRounds);
            var participants = new HashSet<int>();
            foreach (var pairing in current)
            {
                Assert.True(ids.Add(pairing.Id));
                Assert.True(participants.Add(pairing.Player0));
                Assert.Equal(tournament.Round, pairing.Round);
                Assert.True(pairing.BracketRound > 0);
                if (pairing.IsBye)
                {
                    Assert.Equal(0, pairing.WinnerSide);
                    Assert.Equal(pairing.Bracket == "losers" ? 1 : 0, losses[pairing.Player0]);
                    continue;
                }
                Assert.True(participants.Add(pairing.Player1));
                Assert.NotEqual(pairing.Player0, pairing.Player1);
                int firstLosses = pairing.Bracket == "losers" || pairing.Bracket == "reset" ? 1 : 0;
                int secondLosses = pairing.Bracket == "losers" || pairing.Bracket == "final" || pairing.Bracket == "reset" ? 1 : 0;
                Assert.Equal(firstLosses, losses[pairing.Player0]);
                Assert.Equal(secondLosses, losses[pairing.Player1]);
                int winner = result(pairing);
                tournament.ReportResult(pairing, winner);
                losses[winner == 0 ? pairing.Player1 : pairing.Player0]++;
            }
        }
        Assert.NotNull(tournament.Champion);
        Assert.Empty(tournament.NextRound());
        Assert.Equal(Enumerable.Range(0, ids.Count), ids.OrderBy(id => id));
        foreach (var standing in tournament.Standings()) Assert.Equal(losses[standing.Player], standing.Losses);
        return losses;
    }

    static void AssertRanking(Tournament tournament, int[] seeds)
    {
        var elimination = new Dictionary<int, int>();
        foreach (var player in tournament.Players)
            if (player.Id != tournament.Champion)
                elimination[player.Id] = tournament.Pairings.Where(pairing => !pairing.IsBye && Winner(pairing) != player.Id
                    && (pairing.Player0 == player.Id || pairing.Player1 == player.Id)).Max(pairing => pairing.Round);
        var expected = new[] { tournament.Champion.Value }.Concat(elimination.Keys.OrderByDescending(id => elimination[id]).ThenBy(id => Array.IndexOf(seeds, id)));
        Assert.Equal(expected, tournament.Standings().Select(row => row.Player));
        Assert.Equal(Enumerable.Range(1, seeds.Length), tournament.Standings().Select(row => row.Rank));
    }

    static Tournament RestoreAndCheckQueries(ContentDb db, Tournament tournament)
    {
        string json = tournament.ToJson();
        var restored = Tournament.FromJson(db, json);
        Assert.Equal(json, restored.ToJson());
        var originalReport = TournamentReport.Build(tournament, null);
        Assert.Equal(originalReport.ToJson(), TournamentReport.Build(restored, null).ToJson());
        _ = restored.Standings();
        _ = restored.Champion;
        _ = restored.IsFinished;
        _ = restored.Rounds;
        Assert.Equal(json, restored.ToJson());
        Assert.Equal(json, tournament.ToJson());
        return restored;
    }

    static int Result(Pairing pairing, int finalWinner, int resetWinner)
    {
        if (pairing.Bracket == "final") return finalWinner;
        if (pairing.Bracket == "reset") return resetWinner;
        return (pairing.Id + pairing.Player0) % 2;
    }

    static int Winner(Pairing pairing) { return pairing.WinnerSide == 0 ? pairing.Player0 : pairing.Player1; }

    static Tournament Make(string format, int count, TournamentOptions options = null, ContentDb db = null)
    {
        db = db ?? BattleTests.LoadPack("original");
        var tournament = new Tournament(db, format, options: options);
        for (int player = 0; player < count; player++) tournament.AddPlayer("选手 " + player, Code(db, player));
        return tournament;
    }

    static string Code(ContentDb db, int offset)
    {
        return TeamCode.Encode(new TeamPreset
        {
            Name = "淘汰赛阵容", PackId = db.PackId,
            Ids = Enumerable.Range(0, 6).Select(slot => db.Streamers[(slot + offset) % db.Streamers.Count].Id).ToList()
        });
    }

    static Battle CompletedBattle(ContentDb db, Tournament tournament, Pairing pairing, int result)
    {
        var first = tournament.Players[pairing.Player0];
        var second = tournament.Players[pairing.Player1];
        var battle = new Battle(db, new IList<string>[] { first.Team.ToArray(), second.Team.ToArray() },
            new[] { first.Name, second.Name }, unchecked(0x9E3779B9u + (uint)pairing.Id));
        battle.Surrender(1 - result);
        Assert.Equal(result, battle.Winner);
        return battle;
    }

    static string[] FileSnapshot(string root)
    {
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = "T115-" + Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string source = "")
        {
            parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "bin", "test-temp"));
            Root = Path.Combine(parent, name);
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            string target = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(target) != parent || Path.GetFileName(target) != name)
                throw new InvalidOperationException("只能清理当前测试创建的目录。");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }
}
