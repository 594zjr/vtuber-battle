using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T43_ReplayOwnershipTests
{
    const string OwnershipFile = ".replay-ownership.json";
    static readonly DateTime OldTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    sealed class SaveDirectory : IDisposable
    {
        readonly string parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, nameof(T43_ReplayOwnershipTests)));
        readonly string id = Guid.NewGuid().ToString("N");
        public string Root { get; }
        public string Replays { get; }

        public SaveDirectory()
        {
            Root = Path.Combine(parent, id);
            Replays = Path.Combine(Root, "replays");
            Directory.CreateDirectory(Replays);
        }

        public string Put(string name, string payload, DateTime timestamp)
        {
            string path = Path.Combine(Replays, name + ".json");
            File.WriteAllText(path, payload);
            File.SetLastWriteTimeUtc(path, timestamp);
            return path;
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

    static string RecordJson()
    {
        var db = BattleTests.LoadPack("original");
        var team = db.Streamers.Take(6).Select(streamer => streamer.Id).ToArray();
        var battle = new Battle(db, new IList<string>[] { team, team }, new[] { "甲", "乙" }, 1);
        return battle.Record.ToJson();
    }

    static string AutomaticName(int index)
    {
        return "20000101_000000_" + index.ToString("D7") + "_original";
    }

    static void SeedAutomatic(SaveDirectory directory, int count, string payload)
    {
        for (int index = 0; index < count; index++)
            directory.Put(AutomaticName(index), payload, OldTime.AddSeconds(index));
    }

    [Fact]
    public void ForeignJsonSurvivesAutomaticRetentionAndIsNotListed()
    {
        using var directory = new SaveDirectory();
        string record = RecordJson();
        SeedAutomatic(directory, 51, record);
        string foreign = directory.Put("foreign", "{\"keep\":true}", OldTime.AddYears(-1));
        var store = new FileSaveStore(directory.Root);

        store.SaveReplay("new_match", record);

        Assert.Equal("{\"keep\":true}", File.ReadAllText(foreign));
        Assert.DoesNotContain("foreign", store.ListReplays());
        Assert.Equal(50, store.ListReplays().Count);
        Assert.Equal("new_match", store.ListReplays()[0]);
        Assert.False(File.Exists(Path.Combine(directory.Replays, AutomaticName(0) + ".json")));
        Assert.False(File.Exists(Path.Combine(directory.Replays, AutomaticName(1) + ".json")));
        Assert.False(File.Exists(Path.Combine(directory.Replays, OwnershipFile)));
    }

    [Fact]
    public void HandCopiedNamedRecordSharesRetentionWithAutomaticAndOpaqueReplays()
    {
        using var directory = new SaveDirectory();
        string record = RecordJson();
        SeedAutomatic(directory, 49, record);
        string named = directory.Put("shared_replay", record, OldTime.AddYears(-1));
        var store = new FileSaveStore(directory.Root);
        Assert.Contains("shared_replay", store.ListReplays());
        Assert.Equal(record, store.LoadReplay("shared_replay"));

        store.SaveReplay("opaque", "任意文本，保留原样");

        Assert.False(File.Exists(named));
        Assert.Equal(50, store.ListReplays().Count);
        Assert.Contains(AutomaticName(0), store.ListReplays());
        Assert.Equal("opaque", store.ListReplays()[0]);
        Assert.Equal("任意文本，保留原样", new FileSaveStore(directory.Root).LoadReplay("opaque"));
    }

    [Fact]
    public void OpaquePayloadsSurviveReopeningAndOwnershipConvergesWithRetention()
    {
        using var directory = new SaveDirectory();
        for (int index = 0; index < 55; index++)
            new FileSaveStore(directory.Root).SaveReplay("r" + index.ToString("D2"),
                index % 2 == 0 ? "纯文本\n" + index : "{\"seed\":" + index + "}");

        var reopened = new FileSaveStore(directory.Root);
        var names = reopened.ListReplays();
        Assert.Equal(50, names.Count);
        Assert.Equal("r54", names[0]);
        Assert.Equal("纯文本\n54", reopened.LoadReplay("r54"));
        Assert.Equal("{\"seed\":53}", reopened.LoadReplay("r53"));
        Assert.DoesNotContain("r00", names);
        var ownership = Assert.IsType<Dictionary<string, object>>(
            MiniJson.Parse(File.ReadAllText(Path.Combine(directory.Replays, OwnershipFile))));
        Assert.Equal(50, ownership.Count);
        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal),
            ownership.Keys.OrderBy(name => name, StringComparer.Ordinal));
        Assert.All(ownership.Values, value => Assert.Matches("^[0-9a-f]{64}$", Assert.IsType<string>(value)));
    }

    [Fact]
    public void StaleOwnershipCannotDeleteAReplacedForeignPayload()
    {
        using var directory = new SaveDirectory();
        var store = new FileSaveStore(directory.Root);
        store.SaveReplay("replaced", "原来的自有内容");
        string replacement = directory.Put("replaced", "{\"keep\":true}", OldTime.AddYears(-1));
        SeedAutomatic(directory, 50, RecordJson());

        new FileSaveStore(directory.Root).SaveReplay("new_match", RecordJson());

        Assert.Equal("{\"keep\":true}", File.ReadAllText(replacement));
        Assert.DoesNotContain("replaced", store.ListReplays());
        Assert.Equal(50, store.ListReplays().Count);
        Assert.False(File.Exists(Path.Combine(directory.Replays, OwnershipFile)));
    }

    [Theory]
    [InlineData("{坏掉")]
    [InlineData("{\"foreign\":\"不是摘要\"}")]
    [InlineData("{\"foreign\":\"0000000000000000000000000000000000000000000000000000000000000000\"}")]
    public void CorruptOrMismatchingOwnershipNeverAuthorizesForeignDeletion(string ownership)
    {
        using var directory = new SaveDirectory();
        string foreign = directory.Put("foreign", "{\"keep\":true}", OldTime.AddYears(-1));
        File.WriteAllText(Path.Combine(directory.Replays, OwnershipFile), ownership);
        SeedAutomatic(directory, 50, RecordJson());
        var store = new FileSaveStore(directory.Root);

        store.SaveReplay("new_match", RecordJson());

        Assert.Equal("{\"keep\":true}", File.ReadAllText(foreign));
        Assert.DoesNotContain("foreign", store.ListReplays());
        Assert.Equal(50, store.ListReplays().Count);
    }

    [Fact]
    public void ExclusivelyLockedForeignJsonDoesNotBlockListingOrAutomaticRetention()
    {
        using var directory = new SaveDirectory();
        string record = RecordJson();
        SeedAutomatic(directory, 50, record);
        string foreign = directory.Put("foreign", "{\"keep\":true}", OldTime.AddYears(-1));
        var store = new FileSaveStore(directory.Root);

        using (File.Open(foreign, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(50, store.ListReplays().Count);
            Assert.DoesNotContain("foreign", store.ListReplays());

            store.SaveReplay(AutomaticName(99), record);

            Assert.Equal(50, store.ListReplays().Count);
            Assert.Contains(AutomaticName(99), store.ListReplays());
            Assert.DoesNotContain(AutomaticName(0), store.ListReplays());
            Assert.True(File.Exists(foreign));
        }

        Assert.Equal("{\"keep\":true}", File.ReadAllText(foreign));
    }

    [Fact]
    public void LockedOpaqueReplayKeepsOwnershipAndReturnsAfterUnlocking()
    {
        using var directory = new SaveDirectory();
        var store = new FileSaveStore(directory.Root);
        const string payload = "解锁后仍然属于本机回放";
        store.SaveReplay("opaque", payload);
        string opaque = Path.Combine(directory.Replays, "opaque.json");
        File.SetLastWriteTimeUtc(opaque, OldTime.AddYears(-1));
        string record = RecordJson();
        SeedAutomatic(directory, 49, record);

        using (File.Open(opaque, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(49, store.ListReplays().Count);
            Assert.DoesNotContain("opaque", store.ListReplays());

            store.SaveReplay(AutomaticName(99), record);

            Assert.Equal(50, store.ListReplays().Count);
            Assert.True(File.Exists(opaque));
            var ownership = Assert.IsType<Dictionary<string, object>>(
                MiniJson.Parse(File.ReadAllText(Path.Combine(directory.Replays, OwnershipFile))));
            Assert.True(ownership.ContainsKey("opaque"));
        }

        var reopened = new FileSaveStore(directory.Root);
        Assert.Contains("opaque", reopened.ListReplays());
        Assert.Equal(payload, reopened.LoadReplay("opaque"));
        reopened.SaveReplay(AutomaticName(100), record);
        Assert.Equal(50, reopened.ListReplays().Count);
        Assert.False(File.Exists(opaque));
        Assert.False(File.Exists(Path.Combine(directory.Replays, OwnershipFile)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnershipReadOrWriteFailurePreservesTheSuccessfullySavedReplay(bool blockRead)
    {
        using var directory = new SaveDirectory();
        string blocked = Path.Combine(directory.Replays, OwnershipFile + (blockRead ? "" : ".tmp"));
        Directory.CreateDirectory(blocked);
        var store = new FileSaveStore(directory.Root);

        var error = Assert.Throws<ReplayRetentionException>(() => store.SaveReplay("saved", "不能丢失的原文"));

        Assert.Equal("saved", error.ReplayName);
        Assert.Contains("回放已保存", error.Message);
        Assert.Equal("不能丢失的原文", File.ReadAllText(Path.Combine(directory.Replays, "saved.json")));
        Assert.Equal("不能丢失的原文", new FileSaveStore(directory.Root).LoadReplay("saved"));
        Assert.False(File.Exists(Path.Combine(directory.Replays, ".replay.json.tmp")));
    }
}
