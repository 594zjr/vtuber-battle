using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T101_ReplayPolicyTests
{
    static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    static ContentDb Load(string pack = "original", PackPolicy policy = null)
    {
        string content = Path.Combine(AppContext.BaseDirectory, "Content");
        return ContentDb.FromJson(File.ReadAllText(Path.Combine(content, "StreamDuelRules.json")),
            File.ReadAllText(Path.Combine(content, "Packs", pack + ".json")), policy ?? PackPolicy.Empty);
    }

    static BattleRecord Record(string pack = "original")
    {
        var db = Load(pack);
        var ids = db.Streamers.Select(streamer => streamer.Id).Take(12).ToArray();
        return new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "原始甲方", "原始乙方" }, 101).Record;
    }

    static PackPolicy Policy(string pack = null, string streamer = null)
    {
        var reasons = new Dictionary<string, string>();
        if (pack != null) reasons[pack] = "内容包申请撤下";
        if (streamer != null) reasons[streamer] = "主播申请撤下";
        return PackPolicy.FromJson(JsonSerializer.Serialize(new
        {
            disabledPacks = pack == null ? Array.Empty<string>() : new[] { pack },
            disabledStreamers = streamer == null ? Array.Empty<string>() : new[] { streamer },
            reason = reasons
        }));
    }

    static string Save(ReplayLibrary library, BattleRecord record, string note = null)
    {
        // 开场记录用于隔离存储策略；库元数据只要求合法胜方和实际 Turn 事件数量。
        return library.Save(record, new BattleSummary { Winner = -1, Turns = 0 }, note);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    public void DisabledPackReasonPrecedesMissingContentHashAndVersion(bool missingContent, int version)
    {
        var record = Record();
        record.Version = version;
        record.ContentHash = "historical-hash";
        string original = record.ToJson();
        var policy = Policy(pack: record.PackId);

        var result = Replay.Check(missingContent ? null : Load(), record, policy);

        Assert.False(result.CanReplay);
        Assert.Equal(policy.PackReason(record.PackId), result.Reason);
        Assert.Contains("该内容包已下架", result.Reason);
        Assert.Equal(original, record.ToJson());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 5)]
    [InlineData(1, 0)]
    [InlineData(1, 5)]
    public void OriginalTeamSlotsAreCheckedEvenWhenTheStreamerWasRemovedFromContent(int side, int slot)
    {
        var record = Record();
        string id = record.Teams[side][slot];
        var policy = Policy(streamer: id);
        var filtered = Load(policy: policy);
        string original = record.ToJson();
        string contentHash = filtered.ContentHash();
        Assert.DoesNotContain(filtered.Streamers, streamer => streamer.Id == id);
        Assert.NotEqual(record.ContentHash, contentHash);

        foreach (var explicitPolicy in new[] { (PackPolicy)null, PackPolicy.Empty, Policy(streamer: "unrelated-id") })
        {
            var result = Replay.Check(filtered, record, explicitPolicy);
            Assert.False(result.CanReplay);
            Assert.Equal(policy.StreamerReason(id), result.Reason);
            Assert.Contains("该主播已下架", result.Reason);
        }
        var replayError = Assert.Throws<InvalidOperationException>(() => Replay.RunBestEffort(filtered, record));
        Assert.Equal(policy.StreamerReason(id), replayError.Message);
        Assert.Equal(original, record.ToJson());
        Assert.Equal(contentHash, filtered.ContentHash());
    }

    [Fact]
    public void ExplicitPolicyAddsRestrictionsToAnUnrelatedContentPolicy()
    {
        var record = Record();
        string target = record.Teams[1][5];
        var db = Load(policy: Policy(streamer: "unrelated-id"));
        var explicitPolicy = Policy(streamer: target);

        var result = Replay.Check(db, record, explicitPolicy);

        Assert.False(result.CanReplay);
        Assert.Equal(explicitPolicy.StreamerReason(target), result.Reason);
    }

    [Fact]
    public void UnrelatedAndEmptyPoliciesPreserveCompatibilityWarningsAndTheRecord()
    {
        var record = Record();
        var db = Load();
        var policy = Policy(pack: "unrelated-pack", streamer: "unrelated-id");
        Assert.True(Replay.Check(db, record, policy).CanReplay);
        Assert.Equal(string.Empty, Replay.Check(db, record, PackPolicy.Empty).Reason);
        record.ContentHash = "historical-hash";
        string original = record.ToJson();
        var expected = Replay.Check(db, record);

        var result = Replay.Check(db, record, policy);

        Assert.True(result.CanReplay);
        Assert.Equal(expected.Reason, result.Reason);
        Assert.Contains("内容指纹不同", result.Reason);
        Assert.Equal(original, record.ToJson());
        Assert.Contains("缺少录像所需的内容包", Replay.Check(null, record, PackPolicy.Empty).Reason);
        Assert.Contains("录像记录缺失", Replay.Check(db, null, policy).Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListPreservesUnavailableMetadataFiltersAndOriginalBytes(bool disablePack)
    {
        using var directory = new TestDirectory();
        var record = Record();
        string target = record.Teams[1][5];
        const string note = "原始备注：不重写录像";
        var initial = new ReplayLibrary(directory.Root, clock: () => Epoch, policy: PackPolicy.Empty);
        string id = Save(initial, record, note);
        string path = Path.Combine(directory.Root, id + ".json");
        string json = File.ReadAllText(path);
        // 未知字段必须原样保留，不能通过重新序列化录像来附加政策状态。
        File.WriteAllText(path, json.Substring(0, json.Length - 1) + ",\"opaqueOriginal\":{\"keep\":true}}", new UTF8Encoding(false));
        byte[] original = File.ReadAllBytes(path);
        var policy = disablePack ? Policy(pack: record.PackId) : Policy(streamer: target);
        var restricted = new ReplayLibrary(directory.Root, policy: policy);

        var entry = Assert.Single(restricted.List(new ReplayFilter { PackId = record.PackId, StreamerId = target }));

        Assert.Equal(id, entry.Id);
        Assert.Equal(Epoch, entry.Date);
        Assert.Equal(record.PackId, entry.PackId);
        Assert.Equal(record.Names, entry.Names);
        Assert.Equal(record.Teams[0], entry.Teams[0]);
        Assert.Equal(record.Teams[1], entry.Teams[1]);
        Assert.Equal(-1, entry.Winner);
        Assert.Equal(0, entry.Turns);
        Assert.Equal(note, entry.Note);
        Assert.True(entry.IsUnavailable);
        Assert.False(entry.IsCorrupt);
        Assert.Null(entry.Error);
        Assert.Equal(policy.CheckTeam(record.PackId, record.Teams.SelectMany(team => team)), entry.UnavailableReason);
        Assert.Empty(restricted.List(new ReplayFilter { PackId = "different-pack" }));
        using (var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Root, "index.json"))))
        {
            var saved = Assert.Single(index.RootElement.GetProperty("entries").EnumerateArray());
            Assert.True(saved.GetProperty("isUnavailable").GetBoolean());
            Assert.Equal(entry.UnavailableReason, saved.GetProperty("unavailableReason").GetString());
            Assert.False(saved.GetProperty("isCorrupt").GetBoolean());
        }
        Assert.Equal(original, File.ReadAllBytes(path));

        var restored = new ReplayLibrary(directory.Root, policy: PackPolicy.Empty);
        var restoredEntry = Assert.Single(restored.List());
        Assert.False(restoredEntry.IsUnavailable);
        Assert.Null(restoredEntry.UnavailableReason);
        Assert.Equal(record.ToJson(), restored.Load(id).ToJson());
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void LoadSaveAndNoteChangesRejectUnavailableRecordsWithoutWritingFiles()
    {
        using var directory = new TestDirectory();
        var record = Record();
        string originalRecord = record.ToJson();
        var initial = new ReplayLibrary(directory.Root, clock: () => Epoch, policy: PackPolicy.Empty);
        string id = Save(initial, record);
        var policy = Policy(streamer: record.Teams[0][5]);
        var restricted = new ReplayLibrary(directory.Root, policy: policy);
        string reason = Assert.Single(restricted.List()).UnavailableReason;
        var before = Directory.GetFiles(directory.Root).ToDictionary(Path.GetFileName, File.ReadAllBytes);

        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() => restricted.Load(id)).Message);
        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() => Save(restricted, record)).Message);
        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() => restricted.SetNote(id, "不能重写")).Message);

        Assert.Equal(before.Keys.OrderBy(name => name), Directory.GetFiles(directory.Root).Select(Path.GetFileName).OrderBy(name => name));
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(directory.Root, pair.Key)));
        Assert.Equal(originalRecord, record.ToJson());
        restricted.Delete(id);
        Assert.False(File.Exists(Path.Combine(directory.Root, id + ".json")));
        Assert.Empty(restricted.List());
    }

    [Fact]
    public void RetentionExcludesUnavailableRecordsAndOnlyPrunesHealthyOldFiles()
    {
        using var directory = new TestDirectory();
        var unavailable = Record();
        var available = Record("original_wave2");
        var now = Epoch;
        var initial = new ReplayLibrary(directory.Root, clock: () => now, policy: PackPolicy.Empty);
        string protectedId = Save(initial, unavailable);
        string protectedPath = Path.Combine(directory.Root, protectedId + ".json");
        byte[] original = File.ReadAllBytes(protectedPath);
        var restricted = new ReplayLibrary(directory.Root, 1, () => now, Policy(pack: unavailable.PackId));

        now = now.AddMinutes(1);
        string first = Save(restricted, available);
        Assert.True(File.Exists(Path.Combine(directory.Root, first + ".json")));
        now = now.AddMinutes(1);
        string newest = Save(restricted, available);

        Assert.False(File.Exists(Path.Combine(directory.Root, first + ".json")));
        Assert.True(File.Exists(Path.Combine(directory.Root, newest + ".json")));
        Assert.Equal(original, File.ReadAllBytes(protectedPath));
        var entries = restricted.List();
        Assert.Equal(2, entries.Count);
        Assert.True(Assert.Single(entries, entry => entry.Id == protectedId).IsUnavailable);
        Assert.False(Assert.Single(entries, entry => entry.Id == newest).IsUnavailable);
        Assert.Equal(available.ToJson(), restricted.Load(newest).ToJson());
    }

    [Fact]
    public void FutureVersionRetainsItsCorruptProtectionEvenWhenItsPackIsDisabled()
    {
        using var directory = new TestDirectory();
        var record = Record();
        var initial = new ReplayLibrary(directory.Root, clock: () => Epoch, policy: PackPolicy.Empty);
        string id = Save(initial, record);
        string path = Path.Combine(directory.Root, id + ".json");
        string source = File.ReadAllText(path);
        string marker = "\"version\":" + record.Version;
        Assert.Contains(marker, source);
        string json = source.Replace(marker,
            "\"version\":" + (BattleRecord.CurrentVersion + 1));
        using (var parsed = JsonDocument.Parse(json))
        {
            Assert.Equal(BattleRecord.CurrentVersion + 1, parsed.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(1, parsed.RootElement.GetProperty("library").GetProperty("version").GetInt32());
        }
        File.WriteAllText(path, json, new UTF8Encoding(false));
        byte[] original = File.ReadAllBytes(path);
        var restricted = new ReplayLibrary(directory.Root, policy: Policy(pack: record.PackId));

        var entry = Assert.Single(restricted.List());

        Assert.True(entry.IsCorrupt);
        Assert.Contains("录像来自更新的版本", entry.Error);
        Assert.Contains("录像来自更新的版本", Assert.Throws<FormatException>(() => restricted.Load(id)).Message);
        Assert.Contains("录像来自更新的版本", Assert.Throws<FormatException>(() => restricted.SetNote(id, "不重写")).Message);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string sourceFile = "")
        {
            string tests = Path.GetDirectoryName(Path.GetFullPath(sourceFile));
            if (!string.Equals(Path.GetFileName(tests), "Engine.Tests", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("测试源码必须位于本仓库 Engine.Tests。");
            parent = Path.Combine(tests, "obj", "T101-replay-policy");
            Root = Path.Combine(parent, name);
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(resolved), name, StringComparison.Ordinal)
                || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("只能清理本轮创建的测试目录。");
            foreach (string file in Directory.GetFiles(resolved, "*", SearchOption.TopDirectoryOnly)) File.Delete(file);
            Directory.Delete(resolved);
        }
    }
}
