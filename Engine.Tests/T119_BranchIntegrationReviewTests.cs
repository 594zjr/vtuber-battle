using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using StreamDuel.Core;
using Xunit;

public class T119_BranchIntegrationReviewTests
{
    [Theory]
    [InlineData(2, -1)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(3, -1)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    public void BranchSettlementPreservesCompleteSeasonProfilesForBothSidesAndEveryCopy(int season, int winner)
    {
        using var files = new TestDirectory();
        var source = SourceBattle();
        string sourceBefore = source.Record.ToJson();
        ulong sourceHash = source.StateHash();
        var branch = ReplayBranch.Start(source.Db, source.Record, 1, 0, AiLevel.Normal);
        if (winner < 0) branch.ResolveSurrenders(true, true);
        else branch.Surrender(1 - winner);
        Assert.True(branch.IsBranch);
        Assert.Equal(Phase.Over, branch.Phase);
        Assert.Equal(winner, branch.Winner);

        var store = new FileSaveStore(files.Root);
        var originalProfile = SeedProfile(store, files.Root, source, season);
        string profileBefore = originalProfile.ToJson();
        string[] filesBefore = FileSnapshot(files.Root);
        var opponent = SeasonalRating(season == 2 ? 3 : 2, 1920);
        string opponentBefore = opponent.ToJson();
        string branchBefore = branch.Record.ToJson();

        var candidates = new[]
        {
            branch,
            branch.Clone(),
            Replay.Run(source.Db, BattleRecord.FromJson(branchBefore))
        };
        foreach (var candidate in candidates)
        {
            Assert.True(candidate.IsBranch);
            Assert.Equal(Phase.Over, candidate.Phase);
            Assert.Equal(winner, candidate.Winner);
            string recordBefore = candidate.Record.ToJson();
            ulong stateBefore = candidate.StateHash();
            for (int side = 0; side < 2; side++)
            {
                // 两个入口都要覆盖；只保留其中一个提前返回仍应被测试发现。
                originalProfile.Record(candidate, side);
                Assert.Equal(profileBefore, originalProfile.ToJson());

                var returned = store.RecordProfile(candidate, side, opponent);

                Assert.Equal(profileBefore, returned.ToJson());
                Assert.Equal(profileBefore, FileSaveStore.LoadProfileSnapshot(files.Root, true).ToJson());
                Assert.Equal(filesBefore, FileSnapshot(files.Root));
                Assert.False(File.Exists(Path.Combine(files.Root, ".profile.json.tmp")));
                Assert.Equal(opponentBefore, opponent.ToJson());
                Assert.Equal(sourceBefore, source.Record.ToJson());
                Assert.Equal(sourceHash, source.StateHash());
                Assert.Equal(recordBefore, candidate.Record.ToJson());
                Assert.Equal(stateBefore, candidate.StateHash());
                Assert.Equal(branchBefore, branch.Record.ToJson());
            }
        }
    }

    [Fact]
    public void UnfinishedBranchStillRejectsSettlementWithoutChangingSeasonHistoryOrFiles()
    {
        using var files = new TestDirectory();
        var source = SourceBattle();
        string sourceBefore = source.Record.ToJson();
        var branch = ReplayBranch.Start(source.Db, source.Record, 1, 1, AiLevel.Hard);
        Assert.True(branch.IsBranch);
        Assert.Equal(Phase.Choose, branch.Phase);
        string branchBefore = branch.Record.ToJson();
        ulong branchHash = branch.StateHash();
        var store = new FileSaveStore(files.Root);
        var originalProfile = SeedProfile(store, files.Root, source, 3);
        string profileBefore = originalProfile.ToJson();
        string[] filesBefore = FileSnapshot(files.Root);
        var opponent = SeasonalRating(2, 1920);
        string opponentBefore = opponent.ToJson();

        for (int side = 0; side < 2; side++)
        {
            var memoryError = Assert.Throws<InvalidOperationException>(() => originalProfile.Record(branch, side));
            Assert.Contains("对战尚未结束", memoryError.Message);
            var storeError = Assert.Throws<InvalidOperationException>(() => store.RecordProfile(branch, side, opponent));
            Assert.Contains("对战尚未结束", storeError.Message);

            Assert.Equal(profileBefore, originalProfile.ToJson());
            Assert.Equal(profileBefore, FileSaveStore.LoadProfileSnapshot(files.Root, true).ToJson());
            Assert.Equal(filesBefore, FileSnapshot(files.Root));
            Assert.False(File.Exists(Path.Combine(files.Root, ".profile.json.tmp")));
            Assert.Equal(opponentBefore, opponent.ToJson());
            Assert.Equal(sourceBefore, source.Record.ToJson());
            Assert.Equal(branchBefore, branch.Record.ToJson());
            Assert.Equal(branchHash, branch.StateHash());
        }
    }

    static PlayerRating SeasonalRating(int season, int initialRating)
    {
        var rating = new PlayerRating { Rating = initialRating, Games = 12 };
        for (int current = 1; current <= season; current++)
        {
            // 同分已定级对手各赢、输一局，真实产生高于最终分数的已知峰值。
            RatingSystem.Apply(rating, new PlayerRating { Rating = rating.Rating, Games = 20 }, 1);
            RatingSystem.Apply(rating, new PlayerRating { Rating = rating.Rating, Games = 20 }, 0);
            if (current < season) RatingSystem.NewSeason(rating);
        }
        Assert.Equal(season, rating.Season);
        Assert.True(rating.IsPlaced);
        Assert.True(rating.PeakRating > rating.Rating);
        Assert.Equal(2, rating.Games);
        Assert.Equal(season - 1, rating.History.Count);
        Assert.Equal(Enumerable.Range(1, season - 1), rating.History.Select(item => item.Season));
        Assert.All(rating.History, item =>
        {
            Assert.True(item.PeakRating > item.FinalRating);
            Assert.True(item.Games > 0);
            Assert.Equal(RatingSystem.TierOf(item.FinalRating).Name, item.Tier);
        });
        Assert.Equal(14, rating.History[0].Games);
        Assert.Equal(rating.ToJson(), PlayerRating.FromJson(rating.ToJson()).ToJson());
        return rating;
    }

    static Profile SeedProfile(FileSaveStore store, string root, Battle source, int season)
    {
        var profile = new Profile();
        profile.Record(source, 0);
        Assert.Equal(1, profile.Packs[source.Db.PackId].Wins);
        Assert.NotEmpty(profile.Packs[source.Db.PackId].Picks);
        profile.Ratings[Profile.DefaultRatingFormat] = SeasonalRating(season, 1800);
        profile.Ratings["保留赛制"] = SeasonalRating(season == 2 ? 3 : 2, 1650);
        store.SaveProfile(profile);
        string profilePath = Path.Combine(root, "profile.json");
        // 合法空白与固定旧 mtime 能发现即使语义相同、仍不应发生的档案重写。
        File.WriteAllText(profilePath, "\r\n  " + profile.ToJson() + "\r\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(profilePath, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        Assert.True(File.Exists(Path.Combine(root, ".profile.lock")));
        Assert.Equal(profile.ToJson(), FileSaveStore.LoadProfileSnapshot(root, true).ToJson());
        return profile;
    }

    static Battle SourceBattle()
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        Assert.True(ids.Length >= 12);
        var battle = new Battle(db, new IList<string>[]
        {
            ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray()
        }, new[] { "来源甲", "来源乙" }, 119);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        battle.Surrender(1);
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(0, battle.Winner);
        Assert.Single(battle.Record.Events, entry => entry.Type == BattleRecordEventType.Turn);
        return battle;
    }

    static string[] FileSnapshot(string root)
    {
        return Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Directory.Exists(path)
                ? "directory:" + Path.GetRelativePath(root, path)
                : "file:" + Path.GetRelativePath(root, path) + ":" + File.GetLastWriteTimeUtc(path).Ticks
                    + ":" + Convert.ToBase64String(File.ReadAllBytes(path)))
            .ToArray();
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string id = Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string source = "")
        {
            parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "obj", "t119-branch-review"));
            Root = Path.Combine(parent, id);
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            string target = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(target) != parent || Path.GetFileName(target) != id
                || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("只能清理本测试创建的普通目录。");
            Directory.Delete(target, true);
        }
    }
}
