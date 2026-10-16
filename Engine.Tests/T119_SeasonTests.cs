using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T119_SeasonTests
{
    const string ValidSeason = "{\"games\":1,\"rating\":1510,\"season\":2,\"peakRating\":1520,\"isPlaced\":true,\"history\":[{\"season\":1,\"finalRating\":1520,\"peakRating\":1530,\"tier\":\"人气聚光\",\"games\":12}]}";

    [Theory]
    [InlineData(2100, 1800)]
    [InlineData(900, 1200)]
    [InlineData(1500, 1500)]
    [InlineData(1499, 1500)]
    [InlineData(1501, 1501)]
    [InlineData(-1501, -1)]
    [InlineData(-1500, 0)]
    [InlineData(-1499, 1)]
    [InlineData(-1, 750)]
    [InlineData(1, 751)]
    [InlineData(int.MinValue, -1073741074)]
    [InlineData(int.MinValue + 1, -1073741074)]
    [InlineData(int.MaxValue, 1073742574)]
    public void SoftResetRoundsTheCompleteExpressionAwayFromZeroWithoutIntegerOverflow(int oldRating, int expected)
    {
        var rating = new PlayerRating { Rating = oldRating, Games = 12 };
        RatingSystem.NewSeason(rating);
        Assert.Equal(expected, rating.Rating);
        Assert.Equal(expected, rating.PeakRating);
        Assert.Equal(2, rating.Season);
        Assert.Equal(0, rating.Games);
        Assert.True(rating.IsPlaced);
        var history = Assert.Single(rating.History);
        Assert.Equal(1, history.Season);
        Assert.Equal(oldRating, history.FinalRating);
        Assert.Equal(oldRating, history.PeakRating);
        Assert.Equal(12, history.Games);
        Assert.Equal(RatingSystem.TierOf(oldRating).Name, history.Tier);
    }

    [Fact]
    public void HistoryKeepsFinalPeakFinalTierAndGamesAcrossLaterSeasonsAndResults()
    {
        var rating = new PlayerRating { Rating = 1490, Games = 10 };
        RatingSystem.Apply(rating, new PlayerRating { Rating = 1490, Games = 10 }, 1);
        Assert.Equal(1502, rating.Rating);
        Assert.Equal(1502, rating.PeakRating);
        RatingSystem.Apply(rating, new PlayerRating { Rating = 1502, Games = 10 }, 0);
        Assert.Equal(1490, rating.Rating);
        Assert.Equal(1502, rating.PeakRating);
        RatingSystem.NewSeason(rating);
        var first = Assert.Single(rating.History);
        AssertHistory(first, 1, 1490, 1502, "暖场新星", 12);
        Assert.Equal(1495, rating.Rating);
        Assert.Equal(1495, rating.PeakRating);
        RatingSystem.Apply(rating, new PlayerRating { Rating = 1495, Games = 10 }, 1);
        Assert.Equal(1507, rating.Rating);
        RatingSystem.NewSeason(rating);
        Assert.Equal(3, rating.Season);
        Assert.Equal(1504, rating.Rating);
        Assert.Equal(1504, rating.PeakRating);
        Assert.Equal(0, rating.Games);
        AssertHistory(rating.History[0], 1, 1490, 1502, "暖场新星", 12);
        AssertHistory(rating.History[1], 2, 1507, 1507, "人气聚光", 1);
        AssertHistory(first, 1, 1490, 1502, "暖场新星", 12);
        RatingSystem.Apply(rating, new PlayerRating { Rating = 1504, Games = 10 }, 0);
        Assert.Equal(1504, rating.PeakRating);
        AssertHistory(rating.History[1], 2, 1507, 1507, "人气聚光", 1);
    }

    [Theory]
    [InlineData(0, false, 20)]
    [InlineData(9, false, 20)]
    [InlineData(10, true, 12)]
    [InlineData(100, true, 12)]
    public void NewSeasonPreservesOnlyCompletedPlacementAndUsesTheCorrectFirstGameK(int previousGames, bool placed, int gain)
    {
        var rating = new PlayerRating { Games = previousGames };
        RatingSystem.NewSeason(rating);
        rating = PlayerRating.FromJson(rating.ToJson());
        Assert.Equal(0, rating.Games);
        Assert.Equal(placed, rating.IsPlaced);
        var opponent = new PlayerRating { Rating = 1500, Games = 40 };
        RatingSystem.Apply(rating, opponent, 1);
        Assert.Equal(1500 + gain, rating.Rating);
        Assert.Equal(1488, opponent.Rating);
        Assert.Equal(1, rating.Games);
        Assert.Equal(placed, rating.IsPlaced);
        Assert.Equal(previousGames, rating.History[0].Games);
    }

    [Fact]
    public void TheTenthGameQualifiesThePlayerBeforeSwitchingAndEmptySeasonsStillKeepQualification()
    {
        var rating = new PlayerRating { Games = 9 };
        Assert.False(rating.IsPlaced);
        RatingSystem.Apply(rating, new PlayerRating { Games = 50 }, 0.5);
        Assert.Equal(10, rating.Games);
        Assert.True(rating.IsPlaced);
        RatingSystem.NewSeason(rating);
        RatingSystem.NewSeason(rating);
        Assert.Equal(3, rating.Season);
        Assert.Equal(0, rating.History[1].Games);
        Assert.True(rating.IsPlaced);
        RatingSystem.Apply(rating, new PlayerRating { Games = 50 }, 1);
        Assert.Equal(1512, rating.Rating);
        Assert.Equal(1, rating.Games);
    }

    [Theory]
    [InlineData(9, 20)]
    [InlineData(10, 12)]
    public void LegacyRatingAndProfileKeepSeasonOneDefaultsAndOriginalPlacementBehavior(int games, int gain)
    {
        string json = "{\"games\":" + games + ",\"rating\":1400}";
        var rating = PlayerRating.FromJson(json);
        Assert.Equal(1, rating.Season);
        Assert.Equal(1400, rating.PeakRating);
        Assert.Equal(games >= 10, rating.IsPlaced);
        Assert.Empty(rating.History);
        Assert.Equal(json, rating.ToJson());
        var profile = Profile.FromJson("{\"packs\":{},\"ratings\":{\"默认\":" + json + "}}");
        Assert.Equal(json, profile.Ratings[Profile.DefaultRatingFormat].ToJson());
        RatingSystem.Apply(rating, new PlayerRating { Rating = 1400, Games = 30 }, 1);
        Assert.Equal(1400 + gain, rating.Rating);
        Assert.Equal(games + 1, rating.Games);
        Assert.Empty(rating.History);
        Assert.Equal("{\"games\":0,\"rating\":1500}", new PlayerRating().ToJson());
        Assert.Equal("{\"packs\":{}}", Profile.FromJson("{\"packs\":{}}").ToJson());
    }

    [Fact]
    public void CloneAndJsonRoundTripsAreIndependentAndPreserveEverySeasonField()
    {
        var rating = PlayerRating.FromJson(ValidSeason);
        var clone = rating.Clone();
        var back = PlayerRating.FromJson(rating.ToJson());
        Assert.NotSame(rating, clone);
        Assert.NotSame(rating.History, clone.History);
        Assert.Equal(rating.ToJson(), clone.ToJson());
        Assert.Equal(rating.ToJson(), back.ToJson());
        Assert.Equal(2, back.Season);
        Assert.Equal(1510, back.Rating);
        Assert.Equal(1520, back.PeakRating);
        Assert.True(back.IsPlaced);
        AssertHistory(back.History[0], 1, 1520, 1530, "人气聚光", 12);
        var history = Assert.IsAssignableFrom<IList<RatingSeasonHistory>>(clone.History);
        Assert.True(history.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => history.Clear());
        string original = rating.ToJson();
        RatingSystem.NewSeason(clone);
        RatingSystem.Apply(back, new PlayerRating { Rating = 1510, Games = 10 }, 1);
        Assert.Equal(original, rating.ToJson());
        Assert.Equal(3, clone.Season);
        Assert.Equal(2, back.Season);
        var profile = new Profile();
        profile.Ratings[Profile.DefaultRatingFormat] = rating;
        profile.Ratings["另一个赛制"] = clone;
        string profileJson = profile.ToJson();
        var profileCopy = Profile.FromJson(profileJson);
        Assert.Equal(profileJson, profileCopy.ToJson());
        RatingSystem.NewSeason(profileCopy.Ratings[Profile.DefaultRatingFormat]);
        Assert.Equal(profileJson, profile.ToJson());
        Assert.Equal(clone.ToJson(), profileCopy.Ratings["另一个赛制"].ToJson());
    }

    [Theory]
    [MemberData(nameof(InvalidSeasonJson))]
    public void InvalidSeasonFieldsRejectTheWholeRatingAndProfile(string json)
    {
        var error = Assert.Throws<FormatException>(() => PlayerRating.FromJson(json));
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
        var profile = Profile.FromJson("{\"packs\":{\"original\":{\"wins\":7,\"losses\":1,\"draws\":0,\"picks\":{},\"pickWins\":{}}},\"ratings\":{\"默认\":" + json + "}}");
        Assert.Empty(profile.Packs);
        Assert.Empty(profile.Ratings);
    }

    public static IEnumerable<object[]> InvalidSeasonJson()
    {
        foreach (string value in new[] { "0", "-1", "2.5", "2147483648", "null", "\"2\"" })
            yield return new object[] { Change(ValidSeason, "season", value) };
        foreach (string value in new[] { "1509", "1520.5", "2147483648", "null", "\"1520\"" })
            yield return new object[] { Change(ValidSeason, "peakRating", value) };
        foreach (string value in new[] { "0", "null", "\"true\"" })
            yield return new object[] { Change(ValidSeason, "isPlaced", value) };
        yield return new object[] { Change(ValidSeason, "isPlaced", "false") };
        foreach (string value in new[] { "null", "{}", "[]", "[null]" })
            yield return new object[] { Change(ValidSeason, "history", value) };
        yield return new object[] { Change(ValidSeason, "season", "3") };
        yield return new object[] { ChangeHistory("season", "2") };
        yield return new object[] { ChangeHistory("finalRating", "1520.5") };
        yield return new object[] { ChangeHistory("peakRating", "1519") };
        yield return new object[] { ChangeHistory("games", "-1") };
        yield return new object[] { ChangeHistory("games", "2147483648") };
        yield return new object[] { ChangeHistory("tier", "\"巅峰主场\"") };
        yield return new object[] { ChangeHistory("tier", "null") };
        var missing = JsonNode.Parse(ValidSeason).AsObject();
        missing.Remove("history");
        yield return new object[] { missing.ToJsonString() };
        var incompleteEntry = JsonNode.Parse(ValidSeason).AsObject();
        incompleteEntry["history"][0].AsObject().Remove("tier");
        yield return new object[] { incompleteEntry.ToJsonString() };
        var duplicate = JsonNode.Parse(ValidSeason).AsObject();
        duplicate["season"] = 3;
        duplicate["history"].AsArray().Add(JsonNode.Parse(duplicate["history"][0].ToJsonString()));
        yield return new object[] { duplicate.ToJsonString() };
        yield return new object[] { "{\"games\":0,\"rating\":1500,\"isPlaced\":true}" };
    }

    [Fact]
    public void FailedApplyDoesNotMutateCurrentPeakPlacementOrEitherHistory()
    {
        var first = PlayerRating.FromJson(ValidSeason);
        var second = first.Clone();
        second.Games = int.MaxValue;
        string firstBefore = first.ToJson(), secondBefore = second.ToJson();
        Assert.Throws<OverflowException>(() => RatingSystem.Apply(first, second, 1));
        Assert.Equal(firstBefore, first.ToJson());
        Assert.Equal(secondBefore, second.ToJson());
        foreach (double score in new[] { -1.0, 0.25, double.NaN, double.PositiveInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => RatingSystem.Apply(first, second, score));
            Assert.Equal(firstBefore, first.ToJson());
            Assert.Equal(secondBefore, second.ToJson());
        }
    }

    [Fact]
    public void FailedNewSeasonLeavesAllCurrentFieldsAndArchivedSeasonsUntouched()
    {
        Assert.Throws<ArgumentNullException>(() => RatingSystem.NewSeason(null));
        var rating = PlayerRating.FromJson(ValidSeason);
        rating.Season = int.MaxValue;
        string before = RawSnapshot(rating);
        Assert.Throws<OverflowException>(() => RatingSystem.NewSeason(rating));
        Assert.Equal(before, RawSnapshot(rating));
        rating.Season = 2;
        rating.Games = -1;
        before = RawSnapshot(rating);
        var error = Record.Exception(() => RatingSystem.NewSeason(rating));
        Assert.NotNull(error);
        Assert.True(error is ArgumentException || error is FormatException || error is InvalidOperationException);
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
        Assert.Equal(before, RawSnapshot(rating));
    }

    [Fact]
    public void StoreSwitchThenRatedResultPreservesHistoryQualificationOtherFormatsAndLatestSeason()
    {
        using var files = new TestDirectory();
        var store = new FileSaveStore(files.Root);
        var initial = new Profile();
        initial.Ratings[Profile.DefaultRatingFormat] = new PlayerRating { Games = 10 };
        initial.Ratings["保留赛制"] = PlayerRating.FromJson(ValidSeason);
        initial.Packs["旧包"] = new PackStats { Wins = 3, Losses = 2, Draws = 1 };
        store.SaveProfile(initial);
        string other = initial.Ratings["保留赛制"].ToJson();
        var switched = store.NewRatingSeason();
        Assert.Equal(2, switched.Ratings[Profile.DefaultRatingFormat].Season);
        Assert.True(switched.Ratings[Profile.DefaultRatingFormat].IsPlaced);
        Assert.Equal(0, switched.Ratings[Profile.DefaultRatingFormat].Games);
        Assert.Equal(other, switched.Ratings["保留赛制"].ToJson());
        Assert.Equal(3, switched.Packs["旧包"].Wins);
        switched.Ratings[Profile.DefaultRatingFormat].Rating = 999;
        var opponent = new PlayerRating { Games = 15 };
        RatingSystem.NewSeason(opponent);
        string opponentBefore = opponent.ToJson();
        var result = new FileSaveStore(files.Root).RecordProfile(CompletedWin(), 0, opponent);
        var own = result.Ratings[Profile.DefaultRatingFormat];
        Assert.Equal(2, own.Season);
        Assert.Equal(1512, own.Rating);
        Assert.Equal(1512, own.PeakRating);
        Assert.Equal(1, own.Games);
        Assert.True(own.IsPlaced);
        AssertHistory(Assert.Single(own.History), 1, 1500, 1500, "人气聚光", 10);
        Assert.Equal(other, result.Ratings["保留赛制"].ToJson());
        Assert.Equal(opponentBefore, opponent.ToJson());
        Assert.Equal(1, result.Packs["original"].Wins);
        string beforeUnrated = own.ToJson();
        var unrated = store.RecordProfile(CompletedWin(), 0);
        Assert.Equal(beforeUnrated, unrated.Ratings[Profile.DefaultRatingFormat].ToJson());
        Assert.Equal(2, unrated.Packs["original"].Wins);
        var next = store.NewRatingSeason();
        Assert.Equal(3, next.Ratings[Profile.DefaultRatingFormat].Season);
        Assert.Equal(1506, next.Ratings[Profile.DefaultRatingFormat].Rating);
        AssertHistory(next.Ratings[Profile.DefaultRatingFormat].History[1], 2, 1512, 1512, "人气聚光", 1);
        Assert.Equal(other, next.Ratings["保留赛制"].ToJson());
        Assert.Equal(next.ToJson(), FileSaveStore.LoadProfileSnapshot(files.Root).ToJson());
    }

    [Fact]
    public void StoreUsesTheProfileLockAndFailedRatedSettlementCannotPartiallyPersistSeasonData()
    {
        using var files = new TestDirectory();
        var store = new FileSaveStore(files.Root);
        var profile = new Profile();
        profile.Ratings[Profile.DefaultRatingFormat] = PlayerRating.FromJson(ValidSeason);
        store.SaveProfile(profile);
        string path = Path.Combine(files.Root, "profile.json");
        byte[] before = File.ReadAllBytes(path);
        using (var held = new FileStream(Path.Combine(files.Root, ".profile.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => store.NewRatingSeason());
        Assert.Equal(before, File.ReadAllBytes(path));
        var opponent = new PlayerRating { Games = int.MaxValue };
        Assert.Throws<OverflowException>(() => store.RecordProfile(CompletedWin(), 0, opponent));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(store.LoadProfile().Packs);
        Assert.False(File.Exists(Path.Combine(files.Root, ".profile.json.tmp")));
        using var released = new FileStream(Path.Combine(files.Root, ".profile.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{broken")]
    [InlineData("{\"packs\":{},\"ratings\":{\"默认\":{\"season\":2,\"history\":[]}}}")]
    public void ExistingEmptyOrDamagedProfileCannotBeOverwrittenByNewSeason(string json)
    {
        using var files = new TestDirectory();
        string path = Path.Combine(files.Root, "profile.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(path);
        Assert.Throws<FormatException>(() => new FileSaveStore(files.Root).NewRatingSeason());
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Throws<FormatException>(() => FileSaveStore.LoadProfileSnapshot(files.Root, true));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(FileSaveStore.LoadProfileSnapshot(files.Root).Ratings);
        Assert.False(File.Exists(Path.Combine(files.Root, ".profile.json.tmp")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrictSeasonCommandsRejectInvalidUtf8WithoutRewritingProfile(bool newSeason)
    {
        using var files = new TestDirectory();
        string path = Path.Combine(files.Root, "profile.json");
        byte[] bytes = Encoding.UTF8.GetBytes("{\"packs\":{},\"ratings\":{\"X\":{\"games\":12,\"rating\":1510}}}");
        bytes[Array.IndexOf(bytes, (byte)'X')] = 0xff;
        File.WriteAllBytes(path, bytes);
        File.WriteAllText(Path.Combine(files.Root, ".profile.lock"), "");
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        string[] before = FileSnapshot(files.Root);
        var store = new FileSaveStore(files.Root);
        // 普通入口继续兼容旧的替换式读取，只有新严格入口拒绝损坏编码。
        Assert.Equal(1510, store.LoadProfile().Ratings["\ufffd"].Rating);

        var error = Assert.Throws<FormatException>(() =>
        {
            if (newSeason) store.NewRatingSeason();
            else FileSaveStore.LoadProfileSnapshot(files.Root, true);
        });

        Assert.Contains("UTF-8", error.Message);
        Assert.Equal(before, FileSnapshot(files.Root));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(Path.Combine(files.Root, ".profile.json.tmp")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrictSeasonCommandsAcceptUtf8WithOrWithoutBom(bool withBom)
    {
        using var files = new TestDirectory();
        string path = Path.Combine(files.Root, "profile.json");
        var encoding = new UTF8Encoding(withBom, true);
        string json = "{\"packs\":{},\"ratings\":{\"默认\":" + ValidSeason + "}}";
        File.WriteAllBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes(json)).ToArray());
        byte[] before = File.ReadAllBytes(path);

        var profile = FileSaveStore.LoadProfileSnapshot(files.Root, true);

        Assert.Equal(ValidSeason, profile.Ratings[Profile.DefaultRatingFormat].ToJson());
        Assert.Equal(before, File.ReadAllBytes(path));
        var next = new FileSaveStore(files.Root).NewRatingSeason();
        Assert.Equal(3, next.Ratings[Profile.DefaultRatingFormat].Season);
        Assert.Equal(1505, next.Ratings[Profile.DefaultRatingFormat].Rating);
        Assert.Equal(2, next.Ratings[Profile.DefaultRatingFormat].History.Count);
        Assert.Equal(next.ToJson(), FileSaveStore.LoadProfileSnapshot(files.Root, true).ToJson());
    }

    [Fact]
    public void MissingProfileCanStartANewSeasonAndReadOnlyQueriesNeverCreateOrRewriteFiles()
    {
        using var files = new TestDirectory();
        string absent = Path.Combine(files.Root, "missing", "nested");
        Assert.Empty(FileSaveStore.LoadProfileSnapshot(absent, true).Ratings);
        Assert.False(Directory.Exists(Path.Combine(files.Root, "missing")));
        var store = new FileSaveStore(files.Root);
        var result = store.NewRatingSeason();
        var rating = result.Ratings[Profile.DefaultRatingFormat];
        Assert.Equal(2, rating.Season);
        Assert.Equal(1500, rating.Rating);
        Assert.Equal(0, rating.Games);
        Assert.False(rating.IsPlaced);
        AssertHistory(Assert.Single(rating.History), 1, 1500, 1500, "人气聚光", 0);
        string path = Path.Combine(files.Root, "profile.json");
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        string[] before = FileSnapshot(files.Root);
        var snapshot = FileSaveStore.LoadProfileSnapshot(files.Root, true);
        _ = snapshot.Ratings[Profile.DefaultRatingFormat].History.ToArray();
        _ = snapshot.Ratings[Profile.DefaultRatingFormat].Clone().ToJson();
        _ = snapshot.ToJson();
        Assert.Equal(before, FileSnapshot(files.Root));
        RatingSystem.NewSeason(snapshot.Ratings[Profile.DefaultRatingFormat]);
        Assert.Equal(before, FileSnapshot(files.Root));
        Assert.Equal(2, FileSaveStore.LoadProfileSnapshot(files.Root, true).Ratings[Profile.DefaultRatingFormat].Season);
    }

    [Fact]
    public void NetworkingSendsOnlyLegacyCurrentRatingFieldsAndDoesNotShareSeasonHistory()
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        var local = PlayerRating.FromJson(ValidSeason);
        string before = local.ToJson();
        var first = new Transport();
        var second = new Transport();
        first.Peer = second;
        second.Peer = first;
        var host = new NetSession(db, first, true, ids.Take(6).ToArray(), "主机", 119, rating: local);
        var guest = new NetSession(db, second, false, ids.Skip(6).Take(6).ToArray(), "客机", null);
        RatingSystem.NewSeason(local);
        for (int i = 0; i < 8; i++) { host.Poll(); guest.Poll(); }
        Assert.Equal(NetState.Choose, host.State);
        Assert.Equal(NetState.Choose, guest.State);
        Assert.Equal(3, host.SessionProtocolVersion);
        using var hello = JsonDocument.Parse(first.Sent[0]);
        var wire = hello.RootElement.GetProperty("rating");
        Assert.Equal(new[] { "games", "rating" }, wire.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(1510, wire.GetProperty("rating").GetInt32());
        Assert.Equal(1, wire.GetProperty("games").GetInt32());
        var remote = guest.OpponentRating;
        Assert.Equal(1510, remote.Rating);
        Assert.Equal(1, remote.Games);
        Assert.Equal(1, remote.Season);
        Assert.Empty(remote.History);
        RatingSystem.NewSeason(remote);
        Assert.Equal(1, guest.OpponentRating.Season);
        Assert.Empty(guest.OpponentRating.History);
        Assert.Equal(host.Battle.StateHash(), guest.Battle.StateHash());
        Assert.Equal(host.Battle.Record.ToJson(), guest.Battle.Record.ToJson());
        Assert.Equal(2, PlayerRating.FromJson(before).Season);
        Assert.Equal(3, local.Season);
    }

    static void AssertHistory(RatingSeasonHistory history, int season, int final, int peak, string tier, int games)
    {
        Assert.Equal(season, history.Season);
        Assert.Equal(final, history.FinalRating);
        Assert.Equal(peak, history.PeakRating);
        Assert.Equal(tier, history.Tier);
        Assert.Equal(games, history.Games);
    }

    static string Change(string json, string field, string value)
    {
        var root = JsonNode.Parse(json).AsObject();
        root[field] = JsonNode.Parse(value);
        return root.ToJsonString();
    }

    static string ChangeHistory(string field, string value)
    {
        var root = JsonNode.Parse(ValidSeason).AsObject();
        root["history"][0][field] = JsonNode.Parse(value);
        return root.ToJsonString();
    }

    static string RawSnapshot(PlayerRating rating)
    {
        return string.Join("|", rating.Season, rating.Rating, rating.Games, rating.PeakRating, rating.IsPlaced)
            + ";" + string.Join(";", rating.History.Select(history => string.Join("|", history.Season,
                history.FinalRating, history.PeakRating, history.Tier, history.Games)));
    }

    static Battle CompletedWin()
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "甲", "乙" }, 119);
        battle.Surrender(1);
        return battle;
    }

    static string[] FileSnapshot(string root)
    {
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path) + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":"
                + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
    }

    sealed class Transport : INetTransport
    {
        readonly Queue<string> incoming = new Queue<string>();
        public readonly List<string> Sent = new List<string>();
        public Transport Peer;
        public void Send(string message) { Sent.Add(message); Peer.incoming.Enqueue(message); }
        public bool TryReceive(out string message)
        {
            if (incoming.Count == 0) { message = null; return false; }
            message = incoming.Dequeue();
            return true;
        }
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = "T119-" + Guid.NewGuid().ToString("N");
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
