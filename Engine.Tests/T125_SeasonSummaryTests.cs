using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T125_SeasonSummaryTests
{
    static readonly string[] OwnTeam = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] OpponentTeam = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" };
    static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = "T125-" + Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string sourceFile = "")
        {
            parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile), "..", "Build", "T125", "temp"));
            Root = Path.Combine(parent, name);
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(resolved), name, StringComparison.Ordinal))
                throw new InvalidOperationException("只能清理当前测试创建的目录。");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    sealed class SavedGame
    {
        public Battle Battle;
        public RankedReplayTag Tag;
        public string Id;
    }

    sealed class Scenario : IDisposable
    {
        readonly TestDirectory directory = new TestDirectory();
        uint seed = 125;
        public readonly ContentDb Db = BattleTests.LoadPack("original");
        public readonly FileSaveStore Store;
        public readonly ReplayLibrary Library;
        public DateTimeOffset Now = Epoch;
        public string Root { get { return directory.Root; } }
        public string ProfileRoot { get { return Path.Combine(Root, "profile"); } }
        public string LibraryRoot { get { return Path.Combine(Root, "library"); } }
        public Profile Profile { get { return FileSaveStore.LoadProfileSnapshot(ProfileRoot, true); } }

        public Scenario()
        {
            Store = new FileSaveStore(ProfileRoot);
            Library = new ReplayLibrary(LibraryRoot, clock: () => Now);
        }

        public Battle Play(int outcome, int playerSide = 0, string[] ownTeam = null, string[] opponentTeam = null)
        {
            var teams = new IList<string>[2];
            teams[playerSide] = ownTeam ?? OwnTeam;
            teams[1 - playerSide] = opponentTeam ?? OpponentTeam;
            var battle = new Battle(Db, teams, new[] { "主机", "客机" }, seed++);
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            if (outcome == 0) battle.ResolveSurrenders(true, true);
            else battle.Surrender(outcome > 0 ? 1 - playerSide : playerSide);
            Assert.Equal(Phase.Over, battle.Phase);
            return battle;
        }

        public SavedGame Add(int outcome, int playerSide = 0, bool save = true,
            string[] ownTeam = null, string[] opponentTeam = null)
        {
            var battle = Play(outcome, playerSide, ownTeam, opponentTeam);
            Store.RecordProfile(battle, playerSide, new PlayerRating { Rating = 1500, Games = 20 }, out var tag);
            Assert.NotNull(tag);
            return new SavedGame { Battle = battle, Tag = tag, Id = save ? Library.SaveRanked(battle, tag) : null };
        }

        public string PathOf(SavedGame game) { return Path.Combine(LibraryRoot, game.Id + ".json"); }
        public SeasonSummary Summary(int? season = null)
        {
            return SeasonSummary.Create(Profile, ReplayLibrary.OpenReadOnly(LibraryRoot), season);
        }
        public void Dispose() { directory.Dispose(); }
    }

    static string[] Snapshot(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        var directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(path => "D|" + Path.GetRelativePath(root, path));
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(path =>
            "F|" + Path.GetRelativePath(root, path) + "|" + File.GetLastWriteTimeUtc(path).Ticks
            + "|" + Convert.ToBase64String(File.ReadAllBytes(path)));
        return directories.Concat(files).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    static void Edit(string path, Action<JsonObject> edit)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)).AsObject();
        edit(root);
        File.WriteAllText(path, root.ToJsonString());
    }

    [Fact]
    public void OnlyTaggedRankedGamesCountAndBothPlayerSidesUseTheirOwnRosterAndOutcome()
    {
        using var scenario = new Scenario();
        scenario.Add(1, 1);
        scenario.Add(-1, 0);
        scenario.Add(0, 1);
        scenario.Add(1, 0);
        string ratingBeforeUnranked = scenario.Profile.Ratings[Profile.DefaultRatingFormat].ToJson();
        foreach (string note in new[] { "对电脑练习", "同屏对战；备注写排位也不应算排位" })
        {
            var battle = scenario.Play(1);
            scenario.Store.RecordProfile(battle, 0, null, out var tag);
            Assert.Null(tag);
            scenario.Library.Save(battle.Record, BattleSummary.From(battle), note);
        }
        var original = scenario.Play(1);
        var branch = ReplayBranch.Start(scenario.Db, original.Record, 1, 0, AiLevel.Normal);
        branch.Surrender(1);
        scenario.Store.RecordProfile(branch, 0, new PlayerRating(), out var branchTag);
        Assert.Null(branchTag);
        scenario.Library.Save(branch.Record, BattleSummary.From(branch), "接手获胜");

        var summary = scenario.Summary();

        Assert.Equal(ratingBeforeUnranked, scenario.Profile.Ratings[Profile.DefaultRatingFormat].ToJson());
        Assert.Equal((4, 4, 2, 1, 1, 0),
            (summary.Games, summary.RecordedGames, summary.Wins, summary.Losses, summary.Draws, summary.MissingGames));
        Assert.Equal(0.5, summary.WinRate, 10);
        Assert.Equal(1, summary.LongestWinStreak);
        Assert.Equal(new[] { "bell", "enka", "hane" }, summary.TopStreamers.Select(streamer => streamer.Id));
        Assert.All(summary.TopStreamers, streamer =>
        {
            Assert.Equal(4, streamer.Games);
            Assert.Equal(2, streamer.Wins);
            Assert.Equal(0.5, streamer.WinRate, 10);
        });
        Assert.Equal(OpponentTeam.OrderBy(id => id, StringComparer.Ordinal), summary.OpponentStreamers.Select(streamer => streamer.Id));
        Assert.All(summary.OpponentStreamers, streamer => Assert.Equal(4, streamer.Games));
        Assert.Equal(1500, summary.StartRating);
        Assert.Equal(RatingSystem.TierOf(1500).Name, summary.StartTier);
        var rating = scenario.Profile.Ratings[Profile.DefaultRatingFormat];
        Assert.Equal(rating.Rating, summary.FinalRating);
        Assert.Equal(rating.PeakRating, summary.PeakRating);
        Assert.Equal(RatingSystem.TierOf(rating.Rating).Name, summary.FinalTier);
        Assert.Contains("赛季", summary.ToText());
    }

    [Fact]
    public void WinStreakFollowsSeasonGameNumbersRatherThanSavedDates()
    {
        using var scenario = new Scenario();
        int[] outcomes = { 1, 1, -1, 1, 1, 0, 1 };
        int[] days = { 1, 2, 6, 3, 4, 7, 5 };
        for (int i = 0; i < outcomes.Length; i++)
        {
            scenario.Now = Epoch.AddDays(days[i]);
            var game = scenario.Add(outcomes[i]);
            Assert.Equal(i + 1, game.Tag.GameNumber);
        }

        var summary = scenario.Summary();

        Assert.Equal(7, summary.RecordedGames);
        Assert.Equal((5, 1, 1), (summary.Wins, summary.Losses, summary.Draws));
        Assert.Equal(5.0 / 7, summary.WinRate, 10);
        Assert.Equal(2, summary.LongestWinStreak);
    }

    [Fact]
    public void MissingReplayBreaksTheStreakWithoutBecomingALossOrDilutingRecordedWinRate()
    {
        using var scenario = new Scenario();
        var games = Enumerable.Range(0, 5).Select(_ => scenario.Add(1)).ToArray();
        File.Delete(scenario.PathOf(games[2]));

        var summary = scenario.Summary();

        Assert.Equal((5, 4, 4, 0, 0, 1),
            (summary.Games, summary.RecordedGames, summary.Wins, summary.Losses, summary.Draws, summary.MissingGames));
        Assert.Equal(1, summary.WinRate);
        Assert.Equal(2, summary.LongestWinStreak);
        Assert.Contains("有 1 局录像缺失", summary.ToText());
        Assert.NotEmpty(summary.Warnings);
    }

    [Fact]
    public void EveryCopyOfADuplicateSeasonGameNumberIsExcluded()
    {
        using var scenario = new Scenario();
        var games = Enumerable.Range(0, 4).Select(_ => scenario.Add(1)).ToArray();
        File.Copy(scenario.PathOf(games[1]), Path.Combine(scenario.LibraryRoot, "duplicate_game_two.json"));

        var summary = scenario.Summary();

        Assert.Equal((4, 3, 1), (summary.Games, summary.RecordedGames, summary.MissingGames));
        Assert.Equal(3, summary.Wins);
        Assert.Equal(2, summary.LongestWinStreak);
        Assert.Contains("重复", string.Join("\n", summary.Warnings));
    }

    [Fact]
    public void UntaggedOldReplaysNeverBecomeRankedByMatchingNamesDatesOrNotes()
    {
        using var scenario = new Scenario();
        var game = scenario.Add(1, save: false);
        scenario.Library.Save(game.Battle.Record, BattleSummary.From(game.Battle), "第1赛季 排位 第1局");

        var summary = scenario.Summary();

        Assert.Equal((1, 0, 1), (summary.Games, summary.RecordedGames, summary.MissingGames));
        Assert.Equal((0, 0, 0), (summary.Wins, summary.Losses, summary.Draws));
        Assert.Equal(0, summary.WinRate);
        Assert.Equal(0, summary.LongestWinStreak);
        Assert.Empty(summary.TopStreamers);
        Assert.Empty(summary.OpponentStreamers);
    }

    [Fact]
    public void HistoricalSeasonIsQueriedIndependentlyAndUsesItsArchivedRatings()
    {
        using var scenario = new Scenario();
        scenario.Add(1);
        scenario.Add(1, 1);
        scenario.Add(-1);
        var previous = scenario.Profile.Ratings[Profile.DefaultRatingFormat].Clone();
        scenario.Store.NewRatingSeason();
        int start = scenario.Profile.Ratings[Profile.DefaultRatingFormat].Rating;
        var firstCurrent = scenario.Add(-1, 1);
        scenario.Add(0);

        var history = scenario.Summary(1);
        var current = scenario.Summary();

        Assert.Equal((1, 3, 2, 1, 0, 2),
            (history.Season, history.Games, history.Wins, history.Losses, history.Draws, history.LongestWinStreak));
        Assert.Equal(previous.Rating, history.FinalRating);
        Assert.Equal(previous.PeakRating, history.PeakRating);
        Assert.Equal(1500, history.StartRating);
        Assert.Equal((2, 2, 0, 1, 1), (current.Season, current.Games, current.Wins, current.Losses, current.Draws));
        Assert.Equal(start, current.StartRating);
        Assert.Equal(start, firstCurrent.Tag.RatingBefore);
        Assert.Equal(1, firstCurrent.Tag.GameNumber);
        Assert.Equal(2, firstCurrent.Tag.Season);
        Assert.Equal(RatingSystem.TierOf(start).Name, current.StartTier);
        Assert.Equal(scenario.Profile.Ratings[Profile.DefaultRatingFormat].Rating, current.FinalRating);
    }

    [Fact]
    public void FrequencyRanksUseThePlayersWholeRosterAndOrdinalTieBreaking()
    {
        using var scenario = new Scenario();
        scenario.Add(1);
        scenario.Add(-1);
        scenario.Add(0);
        scenario.Add(1, 1, ownTeam: OpponentTeam, opponentTeam: OwnTeam);
        scenario.Add(1, 1, ownTeam: OpponentTeam, opponentTeam: OwnTeam);

        var summary = scenario.Summary();

        Assert.Equal(5, summary.RecordedGames);
        Assert.Equal(3.0 / 5, summary.WinRate, 10);
        Assert.Equal(new[] { "bell", "enka", "hane" }, summary.TopStreamers.Select(streamer => streamer.Id));
        Assert.All(summary.TopStreamers, streamer =>
        {
            Assert.Equal(3, streamer.Games);
            Assert.Equal(1, streamer.Wins);
            Assert.Equal(1.0 / 3, streamer.WinRate, 10);
        });
        Assert.Equal(12, summary.OpponentStreamers.Count);
        Assert.Equal(OpponentTeam.OrderBy(id => id, StringComparer.Ordinal)
            .Concat(OwnTeam.OrderBy(id => id, StringComparer.Ordinal)), summary.OpponentStreamers.Select(streamer => streamer.Id));
        Assert.All(summary.OpponentStreamers.Take(6), streamer => Assert.Equal(3, streamer.Games));
        Assert.All(summary.OpponentStreamers.Skip(6), streamer => Assert.Equal(2, streamer.Games));
    }

    [Fact]
    public void SummaryDoesNotRewriteProfileReplaysLocksOrAMissingIndex()
    {
        using var scenario = new Scenario();
        scenario.Add(1);
        scenario.Add(0);
        string index = Path.Combine(scenario.LibraryRoot, "index.json");
        File.Delete(index);
        var profile = scenario.Profile;
        string profileBefore = profile.ToJson();
        string[] before = Snapshot(scenario.Root);

        var readOnly = SeasonSummary.Create(profile, ReplayLibrary.OpenReadOnly(scenario.LibraryRoot));
        var writableHandle = SeasonSummary.Create(profile, scenario.Library);
        readOnly.ToText();
        writableHandle.ToText();

        Assert.Equal(2, readOnly.RecordedGames);
        Assert.Equal(readOnly.ToText(), writableHandle.ToText());
        Assert.Equal(profileBefore, profile.ToJson());
        Assert.Equal(before, Snapshot(scenario.Root));
        Assert.False(File.Exists(index));
    }

    [Fact]
    public void MissingLibraryAndEmptyProfileProduceAReadOnlyZeroSummary()
    {
        using var directory = new TestDirectory();
        string missing = Path.Combine(directory.Root, "absent", "library");
        var profile = new Profile();
        string before = profile.ToJson();

        var summary = SeasonSummary.Create(profile, ReplayLibrary.OpenReadOnly(missing));

        Assert.Equal((0, 0, 0), (summary.Games, summary.RecordedGames, summary.MissingGames));
        Assert.Equal((0, 0, 0), (summary.Wins, summary.Losses, summary.Draws));
        Assert.Equal(0, summary.WinRate);
        Assert.Equal(0, summary.LongestWinStreak);
        Assert.Equal((1500, 1500, 1500), (summary.StartRating, summary.FinalRating, summary.PeakRating));
        Assert.Equal(before, profile.ToJson());
        Assert.False(Directory.Exists(Path.Combine(directory.Root, "absent")));
        Assert.Empty(summary.TopStreamers);
        Assert.Empty(summary.OpponentStreamers);
    }

    [Fact]
    public void ChangingANotePreservesRankedTagAndSummaryIncludingAfterIndexRemoval()
    {
        using var scenario = new Scenario();
        var game = scenario.Add(1, 1);
        string before = scenario.Summary().ToText();
        var tagBefore = JsonNode.Parse(File.ReadAllText(scenario.PathOf(game)))["library"]["ranked"].ToJsonString();

        scenario.Library.SetNote(game.Id, "收藏这局\n保留排位关联");
        File.Delete(Path.Combine(scenario.LibraryRoot, "index.json"));

        var entry = Assert.Single(scenario.Library.List());
        Assert.Equal("收藏这局\n保留排位关联", entry.Note);
        Assert.NotNull(entry.RankedReplay);
        Assert.Equal(1, entry.RankedReplay.PlayerSide);
        Assert.Equal(game.Tag.GameNumber, entry.RankedReplay.GameNumber);
        Assert.Equal(tagBefore, JsonNode.Parse(File.ReadAllText(scenario.PathOf(game)))["library"]["ranked"].ToJsonString());
        Assert.Equal(before, scenario.Summary().ToText());
    }

    [Fact]
    public void RankedNamesAndNotesPreserveEscapesEmojiAndUnpairedUtf16Surrogates()
    {
        using var scenario = new Scenario();
        const string special = "引号\"、反斜杠\\、换行\r\n、卫星🛰、孤立高代理\uD800、孤立低代理\uDC00";
        var battle = new Battle(scenario.Db, new IList<string>[] { OwnTeam, OpponentTeam },
            new[] { special, "另一方：" + special }, 125);
        battle.Surrender(1);
        string recordBefore = battle.Record.ToJson();
        scenario.Store.RecordProfile(battle, 0, new PlayerRating(), out var tag);
        string id = scenario.Library.SaveRanked(battle, tag, special);

        var first = Assert.Single(scenario.Library.List());
        Assert.False(first.IsCorrupt);
        Assert.Equal(special, first.Note);
        Assert.Equal(battle.Record.Names, first.Names);
        Assert.Equal(recordBefore, scenario.Library.Load(id).ToJson());
        string edited = special + "\n再编辑\uD800";
        scenario.Library.SetNote(id, edited);

        var entry = Assert.Single(scenario.Library.List());
        Assert.False(entry.IsCorrupt);
        Assert.Equal(edited, entry.Note);
        Assert.Equal(battle.Record.Names, entry.Names);
        Assert.NotNull(entry.RankedReplay);
        Assert.Equal(recordBefore, scenario.Library.Load(id).ToJson());
        Assert.Equal(recordBefore, battle.Record.ToJson());
        var summary = scenario.Summary();
        Assert.Equal((1, 1, 0), (summary.RecordedGames, summary.Wins, summary.MissingGames));
    }

    [Fact]
    public void ARankedSettlementTagCannotBeAppliedToAnotherBattle()
    {
        using var scenario = new Scenario();
        var game = scenario.Add(1, save: false);
        var other = scenario.Play(1);
        string[] before = Snapshot(scenario.Root);

        var error = Record.Exception(() => scenario.Library.SaveRanked(other, game.Tag));

        Assert.NotNull(error);
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
        Assert.Equal(before, Snapshot(scenario.Root));
        Assert.Equal(0, scenario.Summary().RecordedGames);
        scenario.Library.SaveRanked(game.Battle, game.Tag);
        Assert.Equal(1, scenario.Summary().RecordedGames);
    }

    public static IEnumerable<object[]> InvalidRankedMetadata()
    {
        yield return new object[] { "version", "2" };
        yield return new object[] { "season", "0" };
        yield return new object[] { "gameNumber", "0" };
        yield return new object[] { "gameNumber", "1.5" };
        yield return new object[] { "playerSide", "2" };
        yield return new object[] { "playerSide", "\"0\"" };
        yield return new object[] { "ratingBefore", "null" };
        yield return new object[] { "ratingAfter", "2147483648" };
        yield return new object[] { "format", "\"\"" };
        yield return new object[] { "recordHash", "\"not-a-hash\"" };
    }

    [Theory]
    [MemberData(nameof(InvalidRankedMetadata))]
    public void InvalidOrFutureRankedMetadataIsExcludedWithoutMutatingTheSource(string field, string value)
    {
        using var scenario = new Scenario();
        var game = scenario.Add(1);
        Edit(scenario.PathOf(game), root => root["library"]["ranked"][field] = JsonNode.Parse(value));
        string[] before = Snapshot(scenario.Root);

        var summary = scenario.Summary();

        Assert.Equal((1, 0, 1), (summary.Games, summary.RecordedGames, summary.MissingGames));
        Assert.Equal(0, summary.Wins);
        Assert.Empty(summary.TopStreamers);
        Assert.NotEmpty(summary.Warnings);
        Assert.Equal(before, Snapshot(scenario.Root));
    }

    [Theory]
    [InlineData("record")]
    [InlineData("winner")]
    [InlineData("future_record")]
    [InlineData("offline_continuation")]
    public void ChangedOrUnsupportedReplayCannotReuseItsRankedAssociation(string change)
    {
        using var scenario = new Scenario();
        var game = scenario.Add(1);
        Edit(scenario.PathOf(game), root =>
        {
            if (change == "record") root["seed"] = 999;
            else if (change == "winner") root["library"]["winner"] = 1;
            else if (change == "future_record") root["version"] = BattleRecord.CurrentVersion + 1;
            else root["offlineContinuation"] = true;
        });
        string[] before = Snapshot(scenario.Root);

        var summary = scenario.Summary();

        Assert.Equal((1, 0, 1), (summary.Games, summary.RecordedGames, summary.MissingGames));
        Assert.Equal(0, summary.Wins);
        Assert.Equal(before, Snapshot(scenario.Root));
    }

    [Fact]
    public void OtherRatingFormatsDoNotBorrowTheDefaultFormatsReplays()
    {
        using var scenario = new Scenario();
        scenario.Add(1);
        var profile = scenario.Profile;
        profile.Ratings["自定义赛制"] = new PlayerRating { Rating = 1800, Games = 2 };
        string before = profile.ToJson();

        var summary = SeasonSummary.Create(profile, ReplayLibrary.OpenReadOnly(scenario.LibraryRoot), format: "自定义赛制");

        Assert.Equal("自定义赛制", summary.Format);
        Assert.Equal((2, 0, 2), (summary.Games, summary.RecordedGames, summary.MissingGames));
        Assert.Equal(1800, summary.FinalRating);
        Assert.Equal(before, profile.ToJson());
    }
}
