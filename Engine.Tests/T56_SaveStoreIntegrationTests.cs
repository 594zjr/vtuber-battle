using System;
using System.Collections.Generic;
using System.IO;
using StreamDuel.Core;
using Xunit;

public class T56_SaveStoreIntegrationTests
{
    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string id = Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory()
        {
            string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
            if (!File.Exists(Path.Combine(project, "StreamDuel.Tests.csproj")))
                throw new InvalidOperationException("测试夹具必须位于测试工程内");
            parent = Path.Combine(project, "obj", "reviewer-temp", nameof(T56_SaveStoreIntegrationTests));
            Root = Path.Combine(parent, id);
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            string path = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(path), parent, StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(path), id, StringComparison.Ordinal))
                throw new InvalidOperationException("只能清理当前测试夹具目录");
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    static TeamPresetList Teams(string name = "常用队伍\n\"甲\"")
    {
        return new TeamPresetList
        {
            Items = new List<TeamPreset>
            {
                new TeamPreset
                {
                    Name = name,
                    PackId = "original",
                    Ids = new List<string> { "enka", "shiori", "lumi", "bell", "hane", "momo" }
                }
            }
        };
    }

    [Fact]
    public void MissingOrDamagedTeamsReturnIndependentEmptyListsWithoutRewriting()
    {
        using var directory = new TestDirectory();
        var store = new FileSaveStore(directory.Root);
        string path = Path.Combine(directory.Root, "teams.json");

        var missing = store.LoadTeams();
        Assert.Empty(missing.Items);
        missing.Items.Add(Teams().Items[0]);
        Assert.Empty(store.LoadTeams().Items);
        Assert.False(File.Exists(path));

        const string damaged = "{\"items\":[{\"name\":\"未完成\"}";
        File.WriteAllText(path, damaged);
        Assert.Empty(store.LoadTeams().Items);
        Assert.Equal(damaged, File.ReadAllText(path));
        Assert.False(File.Exists(Path.Combine(directory.Root, ".teams.json.tmp")));
    }

    [Fact]
    public void SavedTeamsRoundTripAcrossInstancesAndDoNotAliasCallerLists()
    {
        using var directory = new TestDirectory();
        var source = Teams();
        string expected = source.ToJson();
        var store = new FileSaveStore(directory.Root);
        store.SaveTeams(source);
        source.Items[0].Ids[0] = "changed";
        source.Items.Clear();

        var second = new FileSaveStore(directory.Root);
        var loaded = second.LoadTeams();
        Assert.Equal(expected, loaded.ToJson());
        Assert.Equal(expected, File.ReadAllText(Path.Combine(directory.Root, "teams.json")));
        loaded.Items[0].Name = "修改读取结果";
        loaded.Items[0].Ids.Reverse();
        Assert.Equal(expected, store.LoadTeams().ToJson());

        second.SaveTeams(new TeamPresetList());
        Assert.Empty(store.LoadTeams().Items);
        Assert.False(File.Exists(Path.Combine(directory.Root, ".teams.json.tmp")));
    }

    [Fact]
    public void ExclusiveTeamsLockRejectsConcurrentWriterWithoutChangingSavedBytes()
    {
        using var directory = new TestDirectory();
        var first = new FileSaveStore(directory.Root);
        var second = new FileSaveStore(directory.Root);
        first.SaveTeams(Teams("保留"));
        string path = Path.Combine(directory.Root, "teams.json");
        byte[] before = File.ReadAllBytes(path);

        using (new FileStream(Path.Combine(directory.Root, ".teams.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => second.SaveTeams(Teams("替换")));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal("保留", Assert.Single(second.LoadTeams().Items).Name);
            Assert.False(File.Exists(Path.Combine(directory.Root, ".teams.json.tmp")));
        }

        second.SaveTeams(Teams("解锁后保存"));
        Assert.Equal("解锁后保存", Assert.Single(first.LoadTeams().Items).Name);
    }

    [Fact]
    public void TemporaryPathFailurePreservesSavedTeamsAndReleasesTheLock()
    {
        using var directory = new TestDirectory();
        var store = new FileSaveStore(directory.Root);
        store.SaveTeams(Teams("保留"));
        string path = Path.Combine(directory.Root, "teams.json");
        byte[] before = File.ReadAllBytes(path);
        string temporary = Path.Combine(directory.Root, ".teams.json.tmp");
        Directory.CreateDirectory(temporary);

        Assert.ThrowsAny<IOException>(() => store.SaveTeams(Teams("替换")));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.True(Directory.Exists(temporary));

        Directory.Delete(temporary);
        new FileSaveStore(directory.Root).SaveTeams(Teams("重试成功"));
        Assert.Equal("重试成功", Assert.Single(store.LoadTeams().Items).Name);
    }

    [Fact]
    public void ReadFailureIsNotTreatedAsMissingAndInvalidSavesPreserveOldData()
    {
        using var directory = new TestDirectory();
        var store = new FileSaveStore(directory.Root);
        store.SaveTeams(Teams("保留"));
        string path = Path.Combine(directory.Root, "teams.json");
        byte[] before = File.ReadAllBytes(path);

        Assert.Throws<ArgumentNullException>(() => store.SaveTeams(null));
        Assert.Throws<FormatException>(() => store.SaveTeams(new TeamPresetList { Items = null }));
        Assert.Equal(before, File.ReadAllBytes(path));
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => store.LoadTeams());
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal("保留", Assert.Single(store.LoadTeams().Items).Name);
    }
}
