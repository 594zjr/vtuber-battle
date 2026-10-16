using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using StreamDuel.Core;
using Xunit;

public class T57_RatingStoreReviewTests
{
    [Fact]
    public void MissingProfileSnapshotDoesNotCreateAnyDirectory()
    {
        using var dir = new TestDirectory();
        string missing = Path.Combine(dir.PathName, "missing", "nested");

        var profile = FileSaveStore.LoadProfileSnapshot(missing);

        Assert.Empty(profile.Packs);
        Assert.Empty(profile.Ratings);
        Assert.Empty(Directory.GetFileSystemEntries(dir.PathName));
    }

    [Fact]
    public void SnapshotReadsLeaveFilesUntouchedAndOldReaderSurvivesAtomicReplace()
    {
        using var dir = new TestDirectory();
        string path = Path.Combine(dir.PathName, "profile.json");
        var original = RatedProfile(1320, 4);
        string originalJson = original.ToJson();
        File.WriteAllText(path, originalJson, new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal(originalJson, FileSaveStore.LoadProfileSnapshot(dir.PathName).ToJson());
        Assert.Equal(new[] { path }, Directory.GetFileSystemEntries(dir.PathName));
        Assert.Equal(before, File.ReadAllBytes(path));

        var replacement = RatedProfile(1340, 5);
        new FileSaveStore(dir.PathName).SaveProfile(replacement);

        Assert.Equal(originalJson, reader.ReadToEnd());
        Assert.Equal(replacement.ToJson(), FileSaveStore.LoadProfileSnapshot(dir.PathName).ToJson());
        Assert.False(File.Exists(Path.Combine(dir.PathName, ".profile.json.tmp")));
    }

    [Fact]
    public void RepeatedResultsUseLatestStoredRatingAndPreserveOtherFormatsAndOpponent()
    {
        using var dir = new TestDirectory();
        var store = new FileSaveStore(dir.PathName);
        store.SaveProfile(RatedProfile(1500, 0));
        var opponent = new PlayerRating { Rating = 1500, Games = 24 };
        string opponentBefore = opponent.ToJson();
        var battle = CompletedWin();

        var first = store.RecordProfile(battle, 0, opponent);
        var second = new FileSaveStore(dir.PathName).RecordProfile(battle, 0, opponent);

        Assert.Equal(1520, first.Ratings[Profile.DefaultRatingFormat].Rating);
        Assert.Equal(1, first.Ratings[Profile.DefaultRatingFormat].Games);
        Assert.Equal(1539, second.Ratings[Profile.DefaultRatingFormat].Rating);
        Assert.Equal(2, second.Ratings[Profile.DefaultRatingFormat].Games);
        Assert.Equal(new[] { "保留赛制", Profile.DefaultRatingFormat }, second.Ratings.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(1899, second.Ratings["保留赛制"].Rating);
        Assert.Equal(33, second.Ratings["保留赛制"].Games);
        Assert.Equal(2, second.Packs["original"].Wins);
        Assert.Equal(0, second.Packs["original"].Losses);
        Assert.Equal(0, second.Packs["original"].Draws);
        Assert.All(second.Packs["original"].Picks.Values, count => Assert.Equal(2, count));
        Assert.Equal(opponentBefore, opponent.ToJson());
        Assert.Equal(second.ToJson(), store.LoadProfile().ToJson());
    }

    [Theory]
    [InlineData(1500, 0, 1500, -1)]
    [InlineData(1500, 0, 1500, int.MaxValue)]
    [InlineData(1500, int.MaxValue, 1500, 0)]
    [InlineData(int.MaxValue, 0, int.MaxValue, 0)]
    public void InvalidOrOverflowingRatingCannotPartiallyPersistBattleStats(int ownRating, int ownGames,
        int opponentRating, int opponentGames)
    {
        using var dir = new TestDirectory();
        var store = new FileSaveStore(dir.PathName);
        store.SaveProfile(RatedProfile(ownRating, ownGames));
        string path = Path.Combine(dir.PathName, "profile.json");
        byte[] before = File.ReadAllBytes(path);
        var opponent = new PlayerRating { Rating = opponentRating, Games = opponentGames };
        var battle = CompletedWin();

        if (opponentGames < 0)
            Assert.Throws<ArgumentOutOfRangeException>(() => store.RecordProfile(battle, 0, opponent));
        else
            Assert.Throws<OverflowException>(() => store.RecordProfile(battle, 0, opponent));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(store.LoadProfile().Packs);
        Assert.Equal(opponentRating, opponent.Rating);
        Assert.Equal(opponentGames, opponent.Games);
        Assert.False(File.Exists(Path.Combine(dir.PathName, ".profile.json.tmp")));
        using var releasedLock = new FileStream(Path.Combine(dir.PathName, ".profile.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void ProfileLockPreventsConcurrentReadModifyWrite()
    {
        using var dir = new TestDirectory();
        var store = new FileSaveStore(dir.PathName);
        store.SaveProfile(RatedProfile(1500, 0));
        string path = Path.Combine(dir.PathName, "profile.json");
        byte[] before = File.ReadAllBytes(path);
        var battle = CompletedWin();
        using var held = new FileStream(Path.Combine(dir.PathName, ".profile.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.ThrowsAny<IOException>(() => store.RecordProfile(battle, 0, new PlayerRating()));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void SnapshotAndRatedSaveRejectDirectoryLinksBeforeAccessingTarget()
    {
        using var dir = new TestDirectory();
        string target = Path.Combine(dir.PathName, "target");
        string link = Path.Combine(dir.PathName, "linked");
        var store = new FileSaveStore(target);
        store.SaveProfile(RatedProfile(1500, 0));
        byte[] before = File.ReadAllBytes(Path.Combine(target, "profile.json"));
        CreateDirectoryLink(link, target);
        try
        {
            Assert.Throws<IOException>(() => FileSaveStore.LoadProfileSnapshot(link));
            Assert.Throws<IOException>(() => FileSaveStore.LoadProfileSnapshot(Path.Combine(link, "missing")));
            Assert.Throws<IOException>(() => new FileSaveStore(link).RecordProfile(CompletedWin(), 0, new PlayerRating()));
            Assert.False(Directory.Exists(Path.Combine(target, "missing")));
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(target, "profile.json")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    static Profile RatedProfile(int rating, int games)
    {
        var profile = new Profile();
        profile.Ratings.Add(Profile.DefaultRatingFormat, new PlayerRating { Rating = rating, Games = games });
        profile.Ratings.Add("保留赛制", new PlayerRating { Rating = 1899, Games = 33 });
        return profile;
    }

    static Battle CompletedWin()
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() },
            new[] { "甲", "乙" }, 57);
        battle.Surrender(1);
        return battle;
    }

    static void CreateDirectoryLink(string link, string target)
    {
        if (Path.DirectorySeparatorChar != '\\')
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        // Windows 联接不依赖管理员或开发者模式；两个路径都在本测试夹具中。
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"
            + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null");
        using var process = Process.Start(start);
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, nameof(T57_RatingStoreReviewTests)));
        readonly string id = Guid.NewGuid().ToString("N");
        public string PathName { get; }

        public TestDirectory()
        {
            PathName = Path.Combine(parent, id);
            Directory.CreateDirectory(PathName);
        }

        public void Dispose()
        {
            string resolved = Path.GetFullPath(PathName);
            if (Path.GetDirectoryName(resolved) != parent || Path.GetFileName(resolved) != id
                || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("只能清理当前测试夹具目录。");
            Directory.Delete(resolved, true);
        }
    }
}
