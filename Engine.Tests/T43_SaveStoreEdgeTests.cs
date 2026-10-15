using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T43_SaveStoreEdgeTests
{
    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string id = Guid.NewGuid().ToString("N");
        public string PathName { get; }

        public TestDirectory()
        {
            // 夹具留在测试输出目录中，不接触系统临时目录或用户存档。
            parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, nameof(T43_SaveStoreEdgeTests)));
            PathName = Path.Combine(parent, id);
            Directory.CreateDirectory(PathName);
        }

        public string Child(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(PathName, relative));
            if (!path.StartsWith(PathName + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("测试文件必须位于当前夹具目录内");
            return path;
        }

        public void Dispose()
        {
            string path = Path.GetFullPath(PathName);
            if (!string.Equals(Path.GetDirectoryName(path), parent, StringComparison.Ordinal) ||
                !string.Equals(Path.GetFileName(path), id, StringComparison.Ordinal))
                throw new InvalidOperationException("只能清理当前测试夹具目录");
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    static Profile ExampleProfile(int wins = 3)
    {
        var profile = new Profile();
        profile.Packs.Add("original", new PackStats
        {
            Wins = wins,
            Losses = 2,
            Draws = 1,
            Picks = new Dictionary<string, int> { { "bell", wins + 3 }, { "enka", 2 } },
            PickWins = new Dictionary<string, int> { { "bell", wins }, { "enka", 1 } }
        });
        return profile;
    }

    [Fact]
    public void ConstructorCreatesNestedRootAndMissingFilesLoadIndependentDefaults()
    {
        using var dir = new TestDirectory();
        string root = dir.Child(Path.Combine("nested", "store"));
        Assert.False(Directory.Exists(root));

        ISaveStore store = new FileSaveStore(root);

        Assert.True(Directory.Exists(root));
        var settings = store.LoadSettings();
        Assert.Equal(new GameSettings().ToJson(), settings.ToJson());
        settings.FastPlay = true;
        Assert.False(store.LoadSettings().FastPlay);
        var profile = store.LoadProfile();
        Assert.Empty(profile.Packs);
        profile.Packs.Add("local", new PackStats());
        Assert.Empty(store.LoadProfile().Packs);
        Assert.Empty(store.ListReplays());
        Assert.Throws<FileNotFoundException>(() => store.LoadReplay("missing"));
    }

    [Fact]
    public void SettingsAndProfilePersistAcrossStoreInstancesWithoutOverwritingEachOther()
    {
        using var dir = new TestDirectory();
        string root = dir.Child("store");
        var store = new FileSaveStore(root + Path.DirectorySeparatorChar);
        var settings = new GameSettings
        {
            FastPlay = true,
            TurnTimer = true,
            Animations = false,
            AiLevel = AiLevel.Hard,
            DefaultPack = "original",
            Language = "zh-CN"
        };
        var profile = ExampleProfile();

        store.SaveSettings(settings);
        store.SaveProfile(profile);
        store.SaveReplay("match_1", "{\"seed\":1}");
        var reopened = new FileSaveStore(root);

        Assert.Equal(settings.ToJson(), reopened.LoadSettings().ToJson());
        Assert.Equal(profile.ToJson(), reopened.LoadProfile().ToJson());
        Assert.Equal("{\"seed\":1}", reopened.LoadReplay("match_1"));
        profile.Packs["original"].Wins = 99;
        settings.FastPlay = false;
        Assert.Equal(3, reopened.LoadProfile().Packs["original"].Wins);
        Assert.True(reopened.LoadSettings().FastPlay);
    }

    [Fact]
    public void CorruptSettingsAndProfileFilesUseTheirModelDefaults()
    {
        using var dir = new TestDirectory();
        string root = dir.Child("store");
        var store = new FileSaveStore(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), "{坏掉");
        File.WriteAllText(Path.Combine(root, "profile.json"), "{坏掉");

        Assert.Equal(new GameSettings().ToJson(), store.LoadSettings().ToJson());
        Assert.Empty(store.LoadProfile().Packs);
    }

    [Theory]
    [InlineData("Match_09-aZ")]
    [InlineData("0")]
    [InlineData("_")]
    [InlineData("-")]
    public void AllowedReplayNamesRoundTripTheirExactPayload(string name)
    {
        using var dir = new TestDirectory();
        string root = dir.Child("store");
        var store = new FileSaveStore(root);
        const string json = "{\"说明\":\"记录\\n包含转义与中文\",\"seed\":7}\n";

        store.SaveReplay(name, json);

        Assert.Equal(json, store.LoadReplay(name));
        Assert.Equal(new[] { name }, store.ListReplays());
        Assert.Equal(json, File.ReadAllText(Path.Combine(root, "replays", name + ".json")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("nested/file")]
    [InlineData("nested\\file")]
    [InlineData("/absolute")]
    [InlineData("\\absolute")]
    [InlineData("C:\\outside")]
    [InlineData("name.json")]
    [InlineData("name.")]
    [InlineData("name:stream")]
    [InlineData("name\n")]
    [InlineData("name\u0000")]
    [InlineData("主播")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("AUX")]
    [InlineData("PRN")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void UnsafeReplayNamesAreRejectedByBothReadAndWrite(string name)
    {
        using var dir = new TestDirectory();
        string root = dir.Child("store");
        var store = new FileSaveStore(root);
        store.SaveReplay("safe", "保留");

        Assert.ThrowsAny<ArgumentException>(() => store.SaveReplay(name, "替换"));
        Assert.ThrowsAny<ArgumentException>(() => store.LoadReplay(name));

        Assert.Equal(new[] { "safe" }, store.ListReplays());
        Assert.Equal("保留", store.LoadReplay("safe"));
    }

    [Fact]
    public void ReplaysAreOrderedByLastWriteTimeRatherThanNameOrCreationOrder()
    {
        using var dir = new TestDirectory();
        string root = dir.Child("store");
        var store = new FileSaveStore(root);
        foreach (string name in new[] { "alpha", "zulu", "middle" }) store.SaveReplay(name, name);
        var time = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(root, "replays", "zulu.json"), time);
        File.SetLastWriteTimeUtc(Path.Combine(root, "replays", "middle.json"), time.AddMinutes(1));
        File.SetLastWriteTimeUtc(Path.Combine(root, "replays", "alpha.json"), time.AddMinutes(2));

        Assert.Equal(new[] { "alpha", "middle", "zulu" }, store.ListReplays());
        Assert.Equal(store.ListReplays(), new FileSaveStore(root).ListReplays());
    }

    [Fact]
    public void EqualTimestampsHaveStableOrderingAcrossDifferentCreationOrders()
    {
        using var dir = new TestDirectory();
        string firstRoot = dir.Child("first"), secondRoot = dir.Child("second");
        var first = new FileSaveStore(firstRoot);
        var second = new FileSaveStore(secondRoot);
        var names = new[] { "Zeta", "alpha", "Beta" };
        foreach (string name in names) first.SaveReplay(name, name);
        foreach (string name in names.Reverse()) second.SaveReplay(name, name);
        var time = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string name in names)
        {
            File.SetLastWriteTimeUtc(Path.Combine(firstRoot, "replays", name + ".json"), time);
            File.SetLastWriteTimeUtc(Path.Combine(secondRoot, "replays", name + ".json"), time);
        }

        Assert.Equal(first.ListReplays(), second.ListReplays());
        Assert.Equal(first.ListReplays(), new FileSaveStore(firstRoot).ListReplays());
        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal), first.ListReplays().OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void FiftyFirstReplayRemovesOnlyTheOldestManagedReplay()
    {
        using var dir = new TestDirectory();
        string root = dir.Child("store");
        var store = new FileSaveStore(root);
        var time = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 50; i++)
        {
            string name = "r" + i.ToString("D2");
            store.SaveReplay(name, i.ToString());
            File.SetLastWriteTimeUtc(Path.Combine(root, "replays", name + ".json"), time.AddMinutes(i));
        }
        string other = Path.Combine(root, "replays", "other.file.json");
        string notes = Path.Combine(root, "replays", "notes.txt");
        File.WriteAllText(other, "无关文件");
        File.WriteAllText(notes, "无关说明");
        File.SetLastWriteTimeUtc(other, time.AddDays(-1));
        File.SetLastWriteTimeUtc(notes, time.AddDays(-1));

        store.SaveReplay("r50", "50");

        var names = store.ListReplays();
        Assert.Equal(50, names.Count);
        Assert.Equal("r50", names[0]);
        Assert.DoesNotContain("r00", names);
        Assert.False(File.Exists(Path.Combine(root, "replays", "r00.json")));
        Assert.Equal(Enumerable.Range(1, 50).Reverse().Select(i => "r" + i.ToString("D2")), names);
        Assert.Equal("无关文件", File.ReadAllText(other));
        Assert.Equal("无关说明", File.ReadAllText(notes));
    }

    [Fact]
    public void OverwritingReplayMakesItNewestWithoutEvictingAnotherReplay()
    {
        using var dir = new TestDirectory();
        string root = dir.Child("store");
        var store = new FileSaveStore(root);
        var time = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 50; i++)
        {
            string name = "r" + i.ToString("D2");
            store.SaveReplay(name, i.ToString());
            File.SetLastWriteTimeUtc(Path.Combine(root, "replays", name + ".json"), time.AddMinutes(i));
        }

        store.SaveReplay("r00", "已更新");

        Assert.Equal(50, store.ListReplays().Count);
        Assert.Equal("r00", store.ListReplays()[0]);
        Assert.Equal("已更新", new FileSaveStore(root).LoadReplay("r00"));
        Assert.Equal(Enumerable.Range(0, 50).Select(i => "r" + i.ToString("D2")),
            store.ListReplays().OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void AdjacentRootsWithSharedPrefixesRemainIndependent()
    {
        using var dir = new TestDirectory();
        string firstRoot = dir.Child("store"), secondRoot = dir.Child("store-other");
        var first = new FileSaveStore(firstRoot);
        var second = new FileSaveStore(secondRoot);
        first.SaveSettings(new GameSettings { FastPlay = true });
        second.SaveSettings(new GameSettings { FastPlay = false });
        first.SaveProfile(ExampleProfile(3));
        second.SaveProfile(ExampleProfile(8));
        first.SaveReplay("same", "甲");
        second.SaveReplay("same", "乙");

        Assert.ThrowsAny<ArgumentException>(() => first.SaveReplay("../../store-other/replays/same", "覆盖"));
        Assert.ThrowsAny<ArgumentException>(() => first.LoadReplay("../../store-other/replays/same"));

        Assert.True(first.LoadSettings().FastPlay);
        Assert.False(second.LoadSettings().FastPlay);
        Assert.Equal(3, first.LoadProfile().Packs["original"].Wins);
        Assert.Equal(8, second.LoadProfile().Packs["original"].Wins);
        Assert.Equal("甲", first.LoadReplay("same"));
        Assert.Equal("乙", second.LoadReplay("same"));
        foreach (string root in new[] { firstRoot, secondRoot })
            Assert.All(Directory.GetFiles(root, "*", SearchOption.AllDirectories),
                path => Assert.StartsWith(root + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.Ordinal));
    }
}
