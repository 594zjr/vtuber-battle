using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T124_RatingSeedTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Theory]
    [InlineData("single")]
    [InlineData("double")]
    [InlineData("roundrobin")]
    [InlineData("swiss")]
    public void SeedsUseRatingThenCurrentSeasonGamesThenRegistrationOrder(string format)
    {
        var db = BattleTests.LoadPack("original");
        var tournament = RatingTournament(db, format);
        int[] ratings = { 1500, 1600, 1800, 1500, 1900, 1800, 1600, 1200 };
        int[] games = { 10, 20, 8, 2, 0, 8, 3, 200 };
        for (int i = 0; i < ratings.Length; i++) Add(tournament, db, i, ProfileOf(ratings[i], games[i]));

        Assert.Equal(TournamentSeedMode.ByRating, tournament.SeedMode);
        Assert.Equal(new[] { 4, 2, 5, 1, 6, 0, 3, 7 }, tournament.EffectiveSeedOrder);
        Assert.Equal(Enumerable.Range(0, 8), tournament.RatingSeeds.Select(item => item.Player));
        Assert.Equal(ratings, tournament.RatingSeeds.Select(item => item.Rating));
        Assert.Equal(games, tournament.RatingSeeds.Select(item => item.Games));
        Assert.All(tournament.RatingSeeds, item => { Assert.Equal(1, item.Season); Assert.Null(item.ComputerLevel); });
    }

    [Fact]
    public void SignedRatingExtremesAndLargeGameCountsSortWithoutOverflow()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = RatingTournament(db);
        Add(tournament, db, 0, ProfileOf(int.MinValue, int.MaxValue));
        Add(tournament, db, 1, ProfileOf(int.MaxValue, 0));
        Add(tournament, db, 2, ProfileOf(-1, 0));
        Add(tournament, db, 3, ProfileOf(-1, int.MaxValue));
        Assert.Equal(new[] { 1, 3, 2, 0 }, tournament.EffectiveSeedOrder);
        Assert.Equal(tournament.ToJson(), Tournament.FromJson(db, tournament.ToJson()).ToJson());
    }

    [Theory]
    [InlineData(AiLevel.Normal, 1400)]
    [InlineData(AiLevel.Hard, 1600)]
    [InlineData(AiLevel.Expert, 1800)]
    public void ComputersWithoutProfilesUseTheDocumentedReferenceRating(AiLevel level, int expected)
    {
        var db = BattleTests.LoadPack("original");
        var tournament = RatingTournament(db);
        tournament.AddPlayer("电脑", Code(db, 0), null, level);
        var snapshot = Assert.Single(tournament.RatingSeeds);
        Assert.Equal(expected, snapshot.Rating);
        Assert.Equal(0, snapshot.Games);
        Assert.Equal(1, snapshot.Season);
        Assert.Equal(level, snapshot.ComputerLevel);
    }

    [Theory]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void AComputerProfileTakesPriorityOverTheReferenceRating(AiLevel level)
    {
        var db = BattleTests.LoadPack("original");
        var tournament = RatingTournament(db);
        tournament.AddPlayer("电脑", Code(db, 0), ProfileOf(1321, 17), level);
        var snapshot = Assert.Single(tournament.RatingSeeds);
        Assert.Equal(1321, snapshot.Rating);
        Assert.Equal(17, snapshot.Games);
        Assert.Equal(level, snapshot.ComputerLevel);
    }

    [Fact]
    public void MissingHumanRatingAndAnExistingUnratedComputerProfileUseInitialValues()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = RatingTournament(db);
        tournament.AddPlayer("旧报名入口", Code(db, 0));
        tournament.AddPlayer("无档案真人", Code(db, 1), null);
        var unrelated = new Profile();
        unrelated.Ratings["其它赛制"] = new PlayerRating { Rating = 9999, Games = 99 };
        tournament.AddPlayer("无默认记录", Code(db, 2), unrelated, AiLevel.Expert);
        Assert.Equal(new[] { 0, 1, 2 }, tournament.EffectiveSeedOrder);
        Assert.All(tournament.RatingSeeds, item =>
        {
            Assert.Equal(1500, item.Rating);
            Assert.Equal(0, item.Games);
            Assert.Equal(1, item.Season);
        });
        Assert.Single(unrelated.Ratings);
        Assert.False(unrelated.Ratings.ContainsKey(Profile.DefaultRatingFormat));
    }

    [Fact]
    public void OnlyTheCurrentDefaultSeasonIsSnapshottedAtEachSuccessfulRegistration()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = RatingTournament(db);
        var profile = ProfileOf(2500, 80);
        var rating = profile.Ratings[Profile.DefaultRatingFormat];
        RatingSystem.NewSeason(rating);
        rating.Rating = 1450;
        rating.Games = 2;
        profile.Ratings["其它赛制"] = new PlayerRating { Rating = 3000, Games = 300 };
        Add(tournament, db, 0, profile);
        rating.Rating = 1460;
        rating.Games = 3;
        Add(tournament, db, 1, profile);
        string before = tournament.ToJson();
        RatingSystem.NewSeason(rating);
        profile.Ratings.Clear();
        Assert.Equal(before, tournament.ToJson());
        Assert.Equal(new[] { 1450, 1460 }, tournament.RatingSeeds.Select(item => item.Rating));
        Assert.Equal(new[] { 2, 3 }, tournament.RatingSeeds.Select(item => item.Games));
        Assert.All(tournament.RatingSeeds, item => Assert.Equal(2, item.Season));
        Assert.Equal(new[] { 1, 0 }, tournament.EffectiveSeedOrder);
        var opening = Assert.Single(tournament.NextRound());
        Assert.Equal(1, opening.Player0);
        Assert.Equal(0, opening.Player1);
    }

    [Fact]
    public void SnapshotQueriesAreReadOnlyAndDoNotChangeConfigurationOrAdvanceTheSchedule()
    {
        var db = BattleTests.LoadPack("original");
        var options = new TournamentOptions { SeedMode = TournamentSeedMode.ByRating };
        var tournament = new Tournament(db, "single", options: options);
        Add(tournament, db, 0, ProfileOf(1500, 0));
        Add(tournament, db, 1, ProfileOf(1900, 1));
        options.SeedMode = TournamentSeedMode.Default;
        options.Seed = 124;
        options.SeedOrder = new[] { 0, 1 };
        string saved = tournament.ToJson(), hash = db.ContentHash();
        var snapshots = Assert.IsAssignableFrom<IList<TournamentRatingSnapshot>>(tournament.RatingSeeds);
        Assert.Throws<NotSupportedException>(() => snapshots.Clear());
        Assert.Throws<NotSupportedException>(() => snapshots[0] = snapshots[1]);
        var order = Assert.IsAssignableFrom<IList<int>>(tournament.EffectiveSeedOrder);
        Assert.Throws<NotSupportedException>(() => order[0] = 0);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(new[] { 1, 0 }, tournament.EffectiveSeedOrder);
            Assert.Equal(2, tournament.RatingSeeds.Count);
            _ = tournament.Standings();
        }
        Assert.Equal(0, tournament.Round);
        Assert.Empty(tournament.Pairings);
        Assert.Null(tournament.Seed);
        Assert.Null(tournament.SeedOrder);
        Assert.Equal(saved, tournament.ToJson());
        Assert.Equal(hash, db.ContentHash());
    }

    [Fact]
    public void TheTwoHighestRatedPlayersInEightPlayerSingleEliminationCannotMeetBeforeTheFinal()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = RatingTournament(db);
        int[] ratings = { 1000, 1400, 1800, 1300, 1900, 1500, 1200, 1100 };
        for (int i = 0; i < ratings.Length; i++) Add(tournament, db, i, ProfileOf(ratings[i], 0));
        int[] seeds = { 4, 2, 5, 1, 3, 6, 7, 0 };
        var first = tournament.NextRound();
        Assert.Equal(new[] { 4, 0, 1, 3, 2, 7, 5, 6 }, first.SelectMany(pairing => new[] { pairing.Player0, pairing.Player1 }));
        Assert.Contains(first.Take(2), pairing => pairing.Player0 == 4 || pairing.Player1 == 4);
        Assert.Contains(first.Skip(2), pairing => pairing.Player0 == 2 || pairing.Player1 == 2);
        int guard = 0;
        while (!tournament.IsFinished)
        {
            Assert.True(guard++ < 3);
            foreach (var pairing in tournament.NextRound())
            {
                if (tournament.Round < 3) Assert.False(new[] { pairing.Player0, pairing.Player1 }.Contains(4) && new[] { pairing.Player0, pairing.Player1 }.Contains(2));
                tournament.ReportResult(pairing, Array.IndexOf(seeds, pairing.Player0) < Array.IndexOf(seeds, pairing.Player1) ? 0 : 1);
            }
        }
        var final = Assert.Single(tournament.Pairings, pairing => pairing.Round == 3);
        Assert.Equal(new[] { 4, 2 }, new[] { final.Player0, final.Player1 });
        Assert.Equal(4, tournament.Champion);
    }

    [Fact]
    public void RoundRobinUsesTheRatingOrderForItsRotationAndStillPlaysEveryPairOnce()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = MakeRated(db, "roundrobin", new[] { 1000, 1900, 1200, 1800 });
        Assert.Equal(new[] { 1, 0, 3, 2 }, tournament.NextRound().SelectMany(pairing => new[] { pairing.Player0, pairing.Player1 }));
        Complete(tournament);
        var pairs = tournament.Pairings.Select(pairing => Math.Min(pairing.Player0, pairing.Player1) + ":" + Math.Max(pairing.Player0, pairing.Player1)).ToArray();
        Assert.Equal(6, pairs.Length);
        Assert.Equal(6, pairs.Distinct().Count());
    }

    [Fact]
    public void SwissUsesRatingOnlyAfterPointsOpponentPointsAndHeadToHeadTies()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = MakeRated(db, "swiss", new[] { 1000, 1900, 1200, 1800 });
        Assert.Equal(new[] { 1, 3, 2, 0 }, tournament.Standings().Select(row => row.Player));
        var first = tournament.NextRound();
        Assert.Equal(new[] { 1, 3, 2, 0 }, first.SelectMany(pairing => new[] { pairing.Player0, pairing.Player1 }));
        foreach (var pairing in first) tournament.ReportResult(pairing, 1);
        Assert.Equal(new[] { 3, 0, 1, 2 }, tournament.Standings().Select(row => row.Player));
        var second = tournament.NextRound();
        Assert.Equal(new[] { 3, 0, 1, 2 }, second.SelectMany(pairing => new[] { pairing.Player0, pairing.Player1 }));
        foreach (var pairing in second) tournament.ReportResult(pairing, -1);
        Assert.Equal(new[] { 3, 0, 1, 2 }, tournament.Standings().Select(row => row.Player));
    }

    [Theory]
    [InlineData("single")]
    [InlineData("double")]
    [InlineData("roundrobin")]
    [InlineData("swiss")]
    public void SavingAndResumingEveryStageKeepsSnapshotsPairingsAndResultsExactly(string format)
    {
        var db = BattleTests.LoadPack("original");
        var continuous = MakeRated(db, format, new[] { 1300, 1800, 1500, 1700, 1400 });
        var resumed = Tournament.FromJson(db, continuous.ToJson());
        string[] snapshots = SnapshotRows(continuous);
        using (var document = JsonDocument.Parse(continuous.ToJson()))
        {
            Assert.Equal(4, document.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("rating", document.RootElement.GetProperty("seedBy").GetString());
            Assert.Equal(5, document.RootElement.GetProperty("ratingSeeds").GetArrayLength());
        }
        int guard = 0;
        while (!continuous.IsFinished)
        {
            Assert.True(guard++ < 20);
            var round = continuous.NextRound();
            resumed.NextRound();
            Assert.Equal(continuous.ToJson(), resumed.ToJson());
            resumed = Tournament.FromJson(db, resumed.ToJson());
            foreach (var pairing in round.Where(pairing => !pairing.IsBye))
            {
                int winner = Result(pairing);
                continuous.ReportResult(pairing, winner);
                resumed.ReportResult(resumed.Pairings.Single(item => item.Id == pairing.Id), winner);
                Assert.Equal(continuous.ToJson(), resumed.ToJson());
                resumed = Tournament.FromJson(db, resumed.ToJson());
                Assert.Equal(snapshots, SnapshotRows(resumed));
            }
        }
        Assert.True(resumed.IsFinished);
        Assert.Equal(continuous.EffectiveSeedOrder, resumed.EffectiveSeedOrder);
        Assert.Equal(continuous.Standings().Select(row => row.Player), resumed.Standings().Select(row => row.Player));
        Assert.Equal(TournamentReport.Build(continuous, null).ToJson(), TournamentReport.Build(resumed, null).ToJson());
    }

    [Fact]
    public void RegistrationCanContinueAfterRestoringAnUnstartedRatingTournament()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = RatingTournament(db);
        Assert.Empty(tournament.EffectiveSeedOrder);
        tournament = Tournament.FromJson(db, tournament.ToJson());
        Add(tournament, db, 0, ProfileOf(1700, 3));
        tournament = Tournament.FromJson(db, tournament.ToJson());
        Add(tournament, db, 1, ProfileOf(1800, 4));
        Assert.Equal(new[] { 1, 0 }, tournament.EffectiveSeedOrder);
        Assert.Equal(new[] { 0, 1 }, tournament.RatingSeeds.Select(item => item.Player));
    }

    [Theory]
    [InlineData("single", "none")]
    [InlineData("double", "none")]
    [InlineData("single", "random")]
    [InlineData("double", "explicit")]
    [InlineData("roundrobin", "none")]
    [InlineData("swiss", "none")]
    public void DefaultModePreservesTheEntireLegacyV1OrV2JsonLayout(string format, string seeding)
    {
        var db = BattleTests.LoadPack("original");
        TournamentOptions options = format == "single" || format == "double" ? new TournamentOptions { SeedMode = TournamentSeedMode.Default } : null;
        if (seeding == "random") options.Seed = 124;
        if (seeding == "explicit") options.SeedOrder = new[] { 3, 1, 0, 2 };
        var tournament = new Tournament(db, format, options: options);
        for (int i = 0; i < 4; i++) tournament.AddPlayer("选手 " + i, Code(db, i));
        Assert.Equal(TournamentSeedMode.Default, tournament.SeedMode);
        Assert.Empty(tournament.RatingSeeds);
        Assert.Equal(LegacyJson(tournament, db), tournament.ToJson());
        int guard = 0;
        while (!tournament.IsFinished)
        {
            Assert.True(guard++ < 20);
            foreach (var pairing in tournament.NextRound().Where(pairing => !pairing.IsBye)) tournament.ReportResult(pairing, Result(pairing));
            Assert.Equal(LegacyJson(tournament, db), tournament.ToJson());
            var restored = Tournament.FromJson(db, tournament.ToJson());
            Assert.Equal(tournament.ToJson(), restored.ToJson());
            Assert.Equal(TournamentSeedMode.Default, restored.SeedMode);
            Assert.Empty(restored.RatingSeeds);
        }
    }

    [Theory]
    [InlineData("random")]
    [InlineData("explicit")]
    public void RatingModeRejectsOtherSeedSources(string source)
    {
        var db = BattleTests.LoadPack("original");
        var options = new TournamentOptions { SeedMode = TournamentSeedMode.ByRating };
        if (source == "random") options.Seed = 0;
        else options.SeedOrder = new[] { 0, 1 };
        Assert.ThrowsAny<ArgumentException>(() => new Tournament(db, "single", options: options));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void UnknownSeedModesAreRejected(int mode)
    {
        var db = BattleTests.LoadPack("original");
        Assert.ThrowsAny<ArgumentException>(() => new Tournament(db, "single", options: new TournamentOptions { SeedMode = (TournamentSeedMode)mode }));
    }

    [Theory]
    [InlineData("roundrobin")]
    [InlineData("swiss")]
    public void RatingModeDoesNotEnableUnrelatedEliminationOptionsForLeagueFormats(string format)
    {
        var db = BattleTests.LoadPack("original");
        Assert.ThrowsAny<ArgumentException>(() => new Tournament(db, format, options: new TournamentOptions()));
        Assert.ThrowsAny<ArgumentException>(() => new Tournament(db, format,
            options: new TournamentOptions { SeedMode = TournamentSeedMode.ByRating, ResetFinal = false }));
    }

    [Fact]
    public void DefaultModeNewRegistrationOverloadDoesNotReadOrPersistProfileScores()
    {
        var db = BattleTests.LoadPack("original");
        var old = new Tournament(db, "single");
        var modern = new Tournament(db, "single");
        for (int i = 0; i < 2; i++)
        {
            old.AddPlayer("选手 " + i, Code(db, i));
            modern.AddPlayer("选手 " + i, Code(db, i), new Profile { Ratings = null }, AiLevel.Expert);
        }
        Assert.Empty(modern.RatingSeeds);
        Assert.Equal(old.ToJson(), modern.ToJson());
        old.NextRound();
        modern.NextRound();
        Assert.Equal(old.ToJson(), modern.ToJson());
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData((AiLevel)(-1))]
    [InlineData((AiLevel)99)]
    public void UnsupportedComputerLevelsCannotLeaveAPartiallyRegisteredPlayer(AiLevel level)
    {
        var db = BattleTests.LoadPack("original");
        var tournament = RatingTournament(db);
        string before = tournament.ToJson();
        Assert.ThrowsAny<ArgumentException>(() => tournament.AddPlayer("电脑", Code(db, 0), null, level));
        Assert.Equal(before, tournament.ToJson());
        Assert.Empty(tournament.Players);
        Assert.Empty(tournament.RatingSeeds);
        Add(tournament, db, 0, ProfileOf(1500, 0));
        Assert.Equal(0, Assert.Single(tournament.RatingSeeds).Player);
    }

    [Theory]
    [InlineData("null_ratings")]
    [InlineData("null_default")]
    [InlineData("negative_games")]
    [InlineData("zero_season")]
    [InlineData("missing_history")]
    public void InvalidDefaultRatingCannotSilentlyFallBackOrPartiallyRegister(string problem)
    {
        var db = BattleTests.LoadPack("original");
        var tournament = RatingTournament(db);
        var profile = ProfileOf(1600, 3);
        if (problem == "null_ratings") profile.Ratings = null;
        else if (problem == "null_default") profile.Ratings[Profile.DefaultRatingFormat] = null;
        else if (problem == "negative_games") profile.Ratings[Profile.DefaultRatingFormat].Games = -1;
        else if (problem == "zero_season") profile.Ratings[Profile.DefaultRatingFormat].Season = 0;
        else profile.Ratings[Profile.DefaultRatingFormat].Season = 2;
        string before = tournament.ToJson();
        Assert.Throws<FormatException>(() => tournament.AddPlayer("坏档案", Code(db, 0), profile));
        Assert.Equal(before, tournament.ToJson());
        Assert.Empty(tournament.Players);
        Assert.Empty(tournament.RatingSeeds);
    }

    [Fact]
    public void InvalidTeamAndLateRegistrationDoNotAppendRatingSnapshots()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = RatingTournament(db);
        string before = tournament.ToJson();
        Assert.Throws<FormatException>(() => tournament.AddPlayer("坏队伍", "不是队伍码", ProfileOf(2000, 100)));
        Assert.Equal(before, tournament.ToJson());
        Add(tournament, db, 0, ProfileOf(1500, 0));
        Add(tournament, db, 1, ProfileOf(1600, 1));
        tournament.NextRound();
        before = tournament.ToJson();
        Assert.Throws<InvalidOperationException>(() => Add(tournament, db, 2, ProfileOf(1800, 20)));
        Assert.Equal(before, tournament.ToJson());
        Assert.Equal(2, tournament.RatingSeeds.Count);
    }

    [Theory]
    [InlineData("missing_snapshot")]
    [InlineData("null_snapshots")]
    [InlineData("extra_snapshot")]
    [InlineData("duplicate_player")]
    [InlineData("reordered_players")]
    [InlineData("missing_rating")]
    [InlineData("fractional_rating")]
    [InlineData("text_rating")]
    [InlineData("overflow_rating")]
    [InlineData("negative_games")]
    [InlineData("fractional_games")]
    [InlineData("zero_season")]
    [InlineData("easy")]
    [InlineData("unknown_level")]
    [InlineData("unknown_field")]
    [InlineData("wrong_mode")]
    [InlineData("missing_mode")]
    [InlineData("seed_conflict")]
    [InlineData("order_conflict")]
    [InlineData("old_version")]
    [InlineData("future_version")]
    public void MalformedRatingSnapshotsAndConflictingSaveConfigurationAreRejected(string problem)
    {
        var db = BattleTests.LoadPack("original");
        var tournament = MakeRated(db, "single", new[] { 1800, 1500 });
        var root = JsonNode.Parse(tournament.ToJson()).AsObject();
        var rows = root["ratingSeeds"].AsArray();
        var first = rows[0].AsObject();
        switch (problem)
        {
            case "missing_snapshot": rows.RemoveAt(1); break;
            case "null_snapshots": root["ratingSeeds"] = null; break;
            case "extra_snapshot": rows.Add(first.DeepClone()); break;
            case "duplicate_player": rows[1]["player"] = 0; break;
            case "reordered_players": first["player"] = 1; rows[1]["player"] = 0; break;
            case "missing_rating": first.Remove("rating"); break;
            case "fractional_rating": first["rating"] = 1800.5; break;
            case "text_rating": first["rating"] = "1800"; break;
            case "overflow_rating": first["rating"] = (long)int.MaxValue + 1; break;
            case "negative_games": first["games"] = -1; break;
            case "fractional_games": first["games"] = 0.5; break;
            case "zero_season": first["season"] = 0; break;
            case "easy": first["computerLevel"] = "easy"; break;
            case "unknown_level": first["computerLevel"] = "future"; break;
            case "unknown_field": first["path"] = "forbidden-profile-source"; break;
            case "wrong_mode": root["seedBy"] = "random"; break;
            case "missing_mode": root.Remove("seedBy"); break;
            case "seed_conflict": root["seed"] = 0; break;
            case "order_conflict": root["seedOrder"] = new JsonArray(0, 1); break;
            case "old_version": root["version"] = 2; break;
            case "future_version": root["version"] = 999; break;
        }
        var error = Assert.Throws<FormatException>(() => Tournament.FromJson(db, root.ToJsonString()));
        Assert.Contains(error.Message, character => character >= '\u4e00' && character <= '\u9fff');
    }

    [Fact]
    public void AChangedRatingSnapshotCannotMakeAnAlreadySavedBracketSilentlyChange()
    {
        var db = BattleTests.LoadPack("original");
        var tournament = MakeRated(db, "single", new[] { 1800, 1500, 1600, 1700 });
        tournament.NextRound();
        var root = JsonNode.Parse(tournament.ToJson());
        root["ratingSeeds"][1]["rating"] = 2000;
        Assert.Throws<FormatException>(() => Tournament.FromJson(db, root.ToJsonString()));
    }

    [Theory]
    [InlineData("rating")]
    [InlineData("games")]
    [InlineData("season")]
    [InlineData("computer")]
    [InlineData("mode")]
    public void DifferentSnapshotsNeverShareArchiveAssociationsEvenWhenPairingsAreIdentical(string difference)
    {
        using var directory = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var original = RatingTournament(db);
        var changed = difference == "mode" ? new Tournament(db, "single") : RatingTournament(db);
        var first = ProfileOf(1800, 20);
        var changedFirst = ProfileOf(difference == "rating" ? 1801 : 1800, difference == "games" ? 21 : 20);
        if (difference == "season")
        {
            changedFirst = ProfileOf(2100, 20);
            RatingSystem.NewSeason(changedFirst.Ratings[Profile.DefaultRatingFormat]);
            changedFirst.Ratings[Profile.DefaultRatingFormat].Games = 20;
        }
        Add(original, db, 0, first);
        if (difference == "mode") changed.AddPlayer("选手 0", Code(db, 0));
        else changed.AddPlayer("选手 0", Code(db, 0), changedFirst, difference == "computer" ? AiLevel.Hard : (AiLevel?)null);
        Add(original, db, 1, ProfileOf(1500, 0));
        if (difference == "mode") changed.AddPlayer("选手 1", Code(db, 1));
        else Add(changed, db, 1, ProfileOf(1500, 0));
        var pairing = Assert.Single(original.NextRound());
        var changedPairing = Assert.Single(changed.NextRound());
        Assert.Equal(new[] { pairing.Player0, pairing.Player1 }, new[] { changedPairing.Player0, changedPairing.Player1 });
        var battle = CompletedBattle(db, original, pairing);
        original.ReportResult(pairing, 0);
        changed.ReportResult(changedPairing, 0);
        var library = new ReplayLibrary(directory.Root);
        string originalId = library.SaveTournament(original, pairing, battle);
        var restored = Tournament.FromJson(db, original.ToJson());
        Assert.Equal(originalId, library.SaveTournament(restored, restored.Pairings[0], battle));
        using (var report = JsonDocument.Parse(TournamentReport.Build(changed, library).ToJson()))
            Assert.Equal(0, report.RootElement.GetProperty("coverage").GetProperty("archivedGames").GetInt32());
        string changedId = library.SaveTournament(changed, changedPairing, battle);
        Assert.NotEqual(originalId, changedId);
    }

    static Tournament RatingTournament(ContentDb db, string format = "single")
    {
        return new Tournament(db, format, options: new TournamentOptions { SeedMode = TournamentSeedMode.ByRating });
    }

    static Profile ProfileOf(int rating, int games)
    {
        var profile = new Profile();
        profile.Ratings[Profile.DefaultRatingFormat] = new PlayerRating { Rating = rating, Games = games };
        return profile;
    }

    static void Add(Tournament tournament, ContentDb db, int player, Profile profile)
    {
        tournament.AddPlayer("选手 " + player, Code(db, player), profile);
    }

    static Tournament MakeRated(ContentDb db, string format, int[] ratings)
    {
        var tournament = RatingTournament(db, format);
        for (int i = 0; i < ratings.Length; i++) Add(tournament, db, i, ProfileOf(ratings[i], i));
        return tournament;
    }

    static string Code(ContentDb db, int offset)
    {
        return TeamCode.Encode(new TeamPreset
        {
            Name = "排位赛事阵容", PackId = db.PackId,
            Ids = Enumerable.Range(0, 6).Select(slot => db.Streamers[(slot + offset) % db.Streamers.Count].Id).ToList()
        });
    }

    static string[] SnapshotRows(Tournament tournament)
    {
        return tournament.RatingSeeds.Select(item => item.Player + ":" + item.Rating + ":" + item.Games + ":" + item.Season + ":" + item.ComputerLevel).ToArray();
    }

    static int Result(Pairing pairing)
    {
        if (pairing.Bracket == "final") return 1;
        if (pairing.Bracket == "reset") return 0;
        return (pairing.Player0 + pairing.Id) % 2;
    }

    static void Complete(Tournament tournament)
    {
        int guard = 0;
        while (!tournament.IsFinished)
        {
            Assert.True(guard++ < 20);
            foreach (var pairing in tournament.NextRound().Where(pairing => !pairing.IsBye)) tournament.ReportResult(pairing, Result(pairing));
        }
    }

    static Battle CompletedBattle(ContentDb db, Tournament tournament, Pairing pairing)
    {
        var first = tournament.Players[pairing.Player0];
        var second = tournament.Players[pairing.Player1];
        var battle = new Battle(db, new IList<string>[] { first.Team.ToArray(), second.Team.ToArray() },
            new[] { first.Name, second.Name }, unchecked(0x9E3779B9u + (uint)pairing.Id));
        battle.Surrender(1);
        return battle;
    }

    static string LegacyJson(Tournament tournament, ContentDb db)
    {
        // 独立保留旧存档的完整字段顺序，避免仅用新写入器的往返测试证明自己。
        var text = new StringBuilder(tournament.IsElimination ? "{\"version\":2,\"packId\":" : "{\"version\":1,\"packId\":");
        text.Append(Quote(db.PackId)).Append(",\"contentHash\":").Append(Quote(db.ContentHash()))
            .Append(",\"format\":").Append(Quote(tournament.Format));
        if (tournament.IsElimination)
            text.Append(",\"seed\":").Append(tournament.Seed.HasValue ? tournament.Seed.Value.ToString() : "null")
                .Append(",\"seedOrder\":").Append(tournament.SeedOrder == null ? "null" : "[" + string.Join(",", tournament.SeedOrder) + "]")
                .Append(",\"resetFinal\":").Append(tournament.ResetFinal ? "true" : "false");
        text.Append(",\"rounds\":0,\"players\":[");
        text.Append(string.Join(",", tournament.Players.Select(player => "{\"id\":" + player.Id + ",\"name\":" + Quote(player.Name) + ",\"teamCode\":" + Quote(player.TeamCode) + "}")));
        text.Append("],\"roundsPlayed\":[");
        for (int round = 1; round <= tournament.Round; round++)
        {
            if (round > 1) text.Append(',');
            text.Append('[');
            bool first = true;
            foreach (var pairing in tournament.Pairings.Where(pairing => pairing.Round == round))
            {
                if (!first) text.Append(',');
                first = false;
                text.Append("{\"id\":").Append(pairing.Id).Append(",\"round\":").Append(pairing.Round)
                    .Append(",\"player0\":").Append(pairing.Player0).Append(",\"player1\":").Append(pairing.Player1);
                if (tournament.IsElimination) text.Append(",\"bracket\":").Append(Quote(pairing.Bracket)).Append(",\"bracketRound\":").Append(pairing.BracketRound);
                text.Append(",\"winnerSide\":").Append(pairing.WinnerSide.HasValue ? pairing.WinnerSide.Value.ToString() : "null").Append('}');
            }
            text.Append(']');
        }
        return text.Append("]}").ToString();
    }

    static string Quote(string text) { return JsonSerializer.Serialize(text, JsonOptions); }

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = "T124-" + Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string source = "")
        {
            parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "obj", "test-temp"));
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
