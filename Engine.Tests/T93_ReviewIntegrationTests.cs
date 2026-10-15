using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T93_ReviewIntegrationTests
{
    static ContentDb Load()
    {
        string content = Path.Combine(AppContext.BaseDirectory, "Content");
        return ContentDb.FromJson(File.ReadAllText(Path.Combine(content, "StreamDuelRules.json")),
            File.ReadAllText(Path.Combine(content, "Packs", "original.json")));
    }

    static Battle Make()
    {
        var db = Load();
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        Assert.True(ids.Length >= 12);
        return new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "审查甲方", "审查乙方" }, 93);
    }

    static Battle Play()
    {
        var battle = Make();
        for (int guard = 0; guard < 500 && battle.Phase != Phase.Over; guard++)
        {
            if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
            else
            {
                Assert.Equal(Phase.Replace, battle.Phase);
                for (int side = 0; side < 2 && battle.Phase != Phase.Over; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
            }
        }
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.NotEmpty(battle.Record.Events);
        return battle;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void CursorRejectsFutureInMemoryRecordWithTheUpgradeReason(int futureVersion)
    {
        var battle = Make();
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        var record = BattleRecord.FromJson(battle.Record.ToJson());
        record.Version = futureVersion;
        Assert.True(record.Version > BattleRecord.CurrentVersion);
        string original = record.ToJson();
        record = BattleRecord.FromJson(original);
        Assert.Equal(futureVersion, record.Version);
        Assert.Equal(original, record.ToJson());
        Assert.Equal(battle.StateHash(), Replay.Run(battle.Db, record).StateHash());
        var compatibility = Replay.Check(battle.Db, record);
        Assert.False(compatibility.CanReplay);
        Assert.Contains("录像来自更新的版本", compatibility.Reason);
        string contentHash = battle.Db.ContentHash();
        ulong battleHash = battle.StateHash();
        double nextRandom = battle.Clone().Random();

        // 解析与旧重放入口已经成功，游标须显式采用 Check 的不兼容结果。
        var error = Assert.Throws<InvalidOperationException>(() => new ReplayCursor(battle.Db, record, 2));

        Assert.Equal(compatibility.Reason, error.Message);
        Assert.Equal(original, record.ToJson());
        Assert.Equal(contentHash, battle.Db.ContentHash());
        Assert.Equal(battleHash, battle.StateHash());
        Assert.Equal(nextRandom, battle.Clone().Random());
    }

    [Fact]
    public void ActualRetentionPrunesHealthyOldReplayButPreservesFutureReplayBytesAndReason()
    {
        using var directory = new TestDirectory();
        var now = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        var library = new ReplayLibrary(directory.Root, 1, () => now);
        var battle = Play();
        var summary = BattleSummary.From(battle);
        string originalRecord = battle.Record.ToJson();
        string futureId = library.Save(battle.Record, summary);
        string futurePath = Path.Combine(directory.Root, futureId + ".json");
        string savedJson = File.ReadAllText(futurePath);
        string currentMarker = "\"version\":" + BattleRecord.CurrentVersion.ToString(CultureInfo.InvariantCulture);
        string futureMarker = "\"version\":" + (BattleRecord.CurrentVersion + 1).ToString(CultureInfo.InvariantCulture);
        Assert.Contains(currentMarker, savedJson);
        string futureJson = savedJson.Replace(currentMarker, futureMarker);
        futureJson = futureJson.Substring(0, futureJson.Length - 1)
            + ",\"futureReviewPayload\":{\"opaque\":\"保留未知字段\"}}";
        Assert.NotEqual(savedJson, futureJson);
        using (var futureDocument = JsonDocument.Parse(futureJson))
        {
            Assert.Equal(BattleRecord.CurrentVersion + 1, futureDocument.RootElement.GetProperty("version").GetInt32());
            // 只升录像版本，库元数据仍合法，不能借损坏元数据碰巧落入另一条保护路径。
            var metadata = futureDocument.RootElement.GetProperty("library");
            Assert.Equal(1, metadata.GetProperty("version").GetInt32());
            Assert.Equal(futureId, metadata.GetProperty("id").GetString());
            Assert.Equal(JsonValueKind.Null, metadata.GetProperty("note").ValueKind);
        }
        var futureRecord = BattleRecord.FromJson(futureJson);
        Assert.Equal(BattleRecord.CurrentVersion + 1, futureRecord.Version);
        var compatibility = Replay.Check(battle.Db, futureRecord);
        Assert.False(compatibility.CanReplay);
        Assert.Contains("录像来自更新的版本", compatibility.Reason);
        File.WriteAllText(futurePath, futureJson, new UTF8Encoding(false, true));
        byte[] futureBytes = File.ReadAllBytes(futurePath);

        now = now.AddMinutes(1);
        string oldId = library.Save(battle.Record, summary);
        string oldPath = Path.Combine(directory.Root, oldId + ".json");
        Assert.True(File.Exists(oldPath));
        Assert.True(File.Exists(futurePath));
        Assert.Equal(futureBytes, File.ReadAllBytes(futurePath));

        now = now.AddMinutes(1);
        string newestId = library.Save(battle.Record, summary);
        string newestPath = Path.Combine(directory.Root, newestId + ".json");

        Assert.NotEqual(oldId, newestId);
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(newestPath));
        Assert.True(File.Exists(futurePath));
        Assert.Equal(futureBytes, File.ReadAllBytes(futurePath));
        Assert.Equal(new[] { futureId, newestId }.OrderBy(id => id, StringComparer.Ordinal),
            Directory.GetFiles(directory.Root, "*.json").Where(path => Path.GetFileName(path) != "index.json")
                .Select(Path.GetFileNameWithoutExtension).OrderBy(id => id, StringComparer.Ordinal));

        // 直接检查 Save 清理后写下的索引，不能先调用 List 自动修复后再断言成功。
        using (var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Root, "index.json"))))
        {
            Assert.Equal(1, index.RootElement.GetProperty("version").GetInt32());
            var entries = index.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            Assert.Equal(2, entries.Length);
            var future = Assert.Single(entries, item => item.GetProperty("id").GetString() == futureId);
            Assert.True(future.GetProperty("isCorrupt").GetBoolean());
            Assert.Contains("录像来自更新的版本", future.GetProperty("error").GetString());
            var newest = Assert.Single(entries, item => item.GetProperty("id").GetString() == newestId);
            Assert.False(newest.GetProperty("isCorrupt").GetBoolean());
            Assert.Equal(JsonValueKind.Null, newest.GetProperty("error").ValueKind);
            Assert.Equal(battle.Record.PackId, newest.GetProperty("packId").GetString());
            Assert.Equal(summary.Winner, newest.GetProperty("winner").GetInt32());
            Assert.Equal(summary.Turns, newest.GetProperty("turns").GetInt32());
        }

        byte[] indexBytes = File.ReadAllBytes(Path.Combine(directory.Root, "index.json"));
        var loadError = Assert.Throws<FormatException>(() => library.Load(futureId));
        Assert.Equal(compatibility.Reason, loadError.Message);
        var noteError = Assert.Throws<FormatException>(() => library.SetNote(futureId, "不能通过改备注丢失未知字段"));
        Assert.Equal(compatibility.Reason, noteError.Message);
        var saveError = Assert.Throws<FormatException>(() => library.Save(futureRecord, summary));
        Assert.Equal(compatibility.Reason, saveError.Message);
        Assert.Equal(indexBytes, File.ReadAllBytes(Path.Combine(directory.Root, "index.json")));
        Assert.Equal(new[] { futureId, newestId }.OrderBy(id => id, StringComparer.Ordinal),
            Directory.GetFiles(directory.Root, "*.json").Where(path => Path.GetFileName(path) != "index.json")
                .Select(Path.GetFileNameWithoutExtension).OrderBy(id => id, StringComparer.Ordinal));
        var loaded = library.Load(newestId);
        Assert.Equal(originalRecord, loaded.ToJson());
        Assert.Equal(battle.StateHash(), Replay.Run(battle.Db, loaded).StateHash());
        Assert.Equal(futureBytes, File.ReadAllBytes(futurePath));
        Assert.Equal(originalRecord, battle.Record.ToJson());
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string sourceFile = "")
        {
            string tests = Path.GetDirectoryName(Path.GetFullPath(sourceFile));
            if (string.Equals(Path.GetFileName(tests), "obj", StringComparison.OrdinalIgnoreCase))
                tests = Path.GetDirectoryName(tests);
            if (!string.Equals(Path.GetFileName(tests), "Engine.Tests", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("测试源码必须位于本仓库 Engine.Tests 或其 obj 目录。");
            parent = Path.GetFullPath(Path.Combine(tests, "obj", "t93-review"));
            Root = Path.Combine(parent, name);
            if (Directory.Exists(Root) || File.Exists(Root))
                throw new IOException("本轮测试目录必须不存在。");
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(resolved), name, StringComparison.Ordinal)
                || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("只能清理本轮创建的测试目录。");
            // 库只生成顶层文件；出现意外子目录时让非递归删除失败，避免扩大清理范围。
            foreach (string file in Directory.GetFiles(resolved, "*", SearchOption.TopDirectoryOnly)) File.Delete(file);
            Directory.Delete(resolved);
        }
    }
}
