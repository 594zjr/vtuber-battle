using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T80_ReplayLibraryTests
{
    static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 10, 15, 12, 30, 0, TimeSpan.FromHours(8));

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = "T80-" + Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string sourceFile = "")
        {
            parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile), "bin", "test-temp"));
            Root = Path.Combine(parent, name);
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(resolved), name, StringComparison.Ordinal))
                throw new InvalidOperationException("只能清理当前测试创建的目录");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    static Battle Play(int winner = 0, string packId = "t80_demo", int rosterOffset = 0)
    {
        var skills = new Dictionary<string, object>();
        foreach (string id in new[] { "pk_finish", "pk_wait", "pk_other", "pk_fourth", "pk_fifth" })
            skills[id] = new
            {
                name = id == "pk_finish" ? "录像测试终结技" : "录像测试待机", type = "talk", cat = "status", pp = 10,
                effects = id == "pk_finish"
                    ? new object[] { new { code = "FIXED_DAMAGE", at = "post", target = "foe", n = 99999 } }
                    : Array.Empty<object>()
            };
        var pack = new
        {
            id = packId, name = "录像测试包", notice = "测试数据。", skills,
            personas = new Dictionary<string, object>
            {
                ["pk_neutral"] = new { name = "测试人设", code = "T_LOWHP_POWER", @params = new { below = 0, mul = 1 } }
            },
            streamers = Enumerable.Range(0, 18).Select(index => new
            {
                id = "p" + index, name = "测试主播" + index, type = "talk", persona = "pk_neutral",
                stats = new { hp = 300, atk = 80, def = 80, spa = 80, sdef = 80, spd = 120 - index },
                skills = new[] { "pk_finish", "pk_wait", "pk_other", "pk_fourth" }, fifth = "pk_fifth"
            }).ToArray()
        };
        string rules = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", "StreamDuelRules.json"));
        var db = ContentDb.FromJson(rules, JsonSerializer.Serialize(pack));
        var ids = db.Streamers.Select(s => s.Id).Skip(rosterOffset).ToArray();
        var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "甲方「猫」\\直播", "乙方\n卫星🛰" }, 80);
        for (int guard = 0; battle.Phase != Phase.Over && guard < 300; guard++)
        {
            if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(winner == 0 ? BattleAction.Skill(0) : BattleAction.Pass(),
                    winner == 1 ? BattleAction.Skill(0) : BattleAction.Pass());
            else
                for (int side = 0; side < 2; side++)
                    if (battle.Need[side]) battle.Replace(side, battle.Sides[side].Team.FindIndex(f => !f.Fainted));
        }
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(winner, battle.Winner);
        return battle;
    }

    static string Save(ReplayLibrary library, Battle battle, string note = null)
    {
        return library.Save(battle.Record, BattleSummary.From(battle), note);
    }

    static void AssertIds(IEnumerable<ReplayEntry> entries, params string[] ids)
    {
        Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal), entries.Select(e => e.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    static string Snapshot(object value)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true });
    }

    [Fact]
    public void SavedRecordListsCompleteMetadataAndReplaysTheSameBattle()
    {
        using var directory = new TestDirectory();
        string root = Path.Combine(directory.Root, "new-library");
        var library = new ReplayLibrary(root, clock: () => Epoch);
        var battle = Play();
        var summary = BattleSummary.From(battle);
        const string note = "收藏：首发「猫」\n反斜杠\\与引号\"、卫星🛰";

        string id = library.Save(battle.Record, summary, note);

        Assert.True(File.Exists(Path.Combine(root, "index.json")));
        Assert.True(File.Exists(Path.Combine(root, id + ".json")));
        Assert.Equal(Path.GetFileName(id), id);
        var entry = Assert.Single(library.List());
        Assert.Equal(id, entry.Id);
        Assert.False(entry.IsCorrupt);
        Assert.True(string.IsNullOrEmpty(entry.Error));
        Assert.Equal(Epoch.ToUniversalTime(), entry.Date);
        Assert.Equal(TimeSpan.Zero, entry.Date.Offset);
        Assert.Equal(battle.Db.PackId, entry.PackId);
        Assert.Equal(battle.Record.Names, entry.Names);
        for (int side = 0; side < 2; side++) Assert.Equal(battle.Record.Teams[side], entry.Teams[side]);
        Assert.Equal(battle.Winner, entry.Winner);
        Assert.Equal(battle.Winner, summary.Winner);
        int turns = battle.Record.Events.Count(e => e.Type == BattleRecordEventType.Turn);
        Assert.Equal(turns, entry.Turns);
        Assert.Equal(turns, summary.Turns);
        Assert.Equal(note, entry.Note);

        var loaded = library.Load(id);
        Assert.Equal(battle.Record.ToJson(), loaded.ToJson());
        Assert.Equal(battle.Record.ToJson(), BattleRecord.FromJson(File.ReadAllText(Path.Combine(root, id + ".json"))).ToJson());
        var replay = Replay.Run(battle.Db, loaded);
        Assert.Equal(battle.StateHash(), replay.StateHash());
        Assert.Equal(Snapshot(battle.Log), Snapshot(replay.Log));
        Assert.Equal(Snapshot(battle.Events), Snapshot(replay.Events));
        Assert.Equal(BattleSummary.From(battle).ToText(), BattleSummary.From(replay).ToText());
        Assert.Equal(battle.Clone().Random(), replay.Clone().Random());
    }

    [Fact]
    public void FiltersCombineAndIncludeBothDateEndpointsAndBothTeams()
    {
        using var directory = new TestDirectory();
        var now = Epoch;
        var library = new ReplayLibrary(directory.Root, clock: () => now);
        string first = Save(library, Play(0, "pack_a"));
        now = Epoch.AddDays(1);
        string second = Save(library, Play(1, "pack_a", 6));
        now = Epoch.AddDays(2);
        string draw = Save(library, Play(-1, "pack_b"));

        AssertIds(library.List(new ReplayFilter { StreamerId = "p0" }), first, draw);
        AssertIds(library.List(new ReplayFilter { StreamerId = "p17" }), second);
        AssertIds(library.List(new ReplayFilter { PackId = "pack_a" }), first, second);
        AssertIds(library.List(new ReplayFilter { Winner = 0 }), first);
        AssertIds(library.List(new ReplayFilter { Winner = 1 }), second);
        AssertIds(library.List(new ReplayFilter { Winner = -1 }), draw);
        AssertIds(library.List(new ReplayFilter { From = Epoch, To = Epoch.AddDays(1) }), first, second);
        AssertIds(library.List(new ReplayFilter
        {
            From = Epoch.AddDays(1).ToOffset(TimeSpan.FromHours(-4)), To = Epoch.AddDays(1),
            PackId = "pack_a", StreamerId = "p17", Winner = 1
        }), second);
        Assert.Empty(library.List(new ReplayFilter { PackId = "pack_b", Winner = 0 }));
        Assert.Empty(library.List(new ReplayFilter { StreamerId = "missing" }));
    }

    [Fact]
    public void RetentionRemovesTheOldestUnnotedRecordBySavedDate()
    {
        using var directory = new TestDirectory();
        var now = Epoch;
        var library = new ReplayLibrary(directory.Root, 2, () => now);
        var battle = Play();
        string favorite = Save(library, battle, "收藏");
        now = Epoch.AddMinutes(1);
        string old = Save(library, battle);
        File.SetLastWriteTimeUtc(Path.Combine(directory.Root, old + ".json"), Epoch.AddYears(1).UtcDateTime);
        now = Epoch.AddMinutes(2);

        string newest = Save(library, battle);

        AssertIds(library.List(), favorite, newest);
        Assert.False(File.Exists(Path.Combine(directory.Root, old + ".json")));
        Assert.True(File.Exists(Path.Combine(directory.Root, favorite + ".json")));
        Assert.Equal("收藏", library.List().Single(e => e.Id == favorite).Note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" \t\r\n")]
    public void FavoritesMayExceedTheLimitAndClearingANoteTriggersRetention(string clearedNote)
    {
        using var directory = new TestDirectory();
        var now = Epoch;
        var library = new ReplayLibrary(directory.Root, 1, () => now);
        var battle = Play();
        string first = Save(library, battle, "第一份收藏");
        now = Epoch.AddMinutes(1);
        string second = Save(library, battle, "第二份收藏");
        AssertIds(library.List(), first, second);
        now = Epoch.AddMinutes(2);
        string unprotected = Save(library, battle);
        AssertIds(library.List(), first, second);
        Assert.False(File.Exists(Path.Combine(directory.Root, unprotected + ".json")));

        library.SetNote(first, clearedNote);

        AssertIds(library.List(), second);
        Assert.False(File.Exists(Path.Combine(directory.Root, first + ".json")));
        library.SetNote(second, clearedNote);
        now = Epoch.AddMinutes(3);
        string newest = Save(library, battle);
        AssertIds(library.List(), newest);
        Assert.False(File.Exists(Path.Combine(directory.Root, second + ".json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrCorruptIndexRebuildsMetadataAndEditedNotes(bool removeIndex)
    {
        using var directory = new TestDirectory();
        var library = new ReplayLibrary(directory.Root, clock: () => Epoch);
        var battle = Play();
        string id = Save(library, battle, "旧备注");
        const string note = "重建后也保留的收藏\n\"完整备注\"🛰";
        library.SetNote(id, note);
        string index = Path.Combine(directory.Root, "index.json");
        if (removeIndex) File.Delete(index); else File.WriteAllText(index, "{ damaged index");

        var rebuilt = new ReplayLibrary(directory.Root, clock: () => Epoch.AddYears(1));
        var entry = Assert.Single(rebuilt.List());

        Assert.Equal(id, entry.Id);
        Assert.Equal(note, entry.Note);
        Assert.Equal(Epoch.ToUniversalTime(), entry.Date);
        Assert.Equal(battle.Winner, entry.Winner);
        Assert.Equal(BattleSummary.From(battle).Turns, entry.Turns);
        Assert.Equal(battle.Record.Names, entry.Names);
        for (int side = 0; side < 2; side++) Assert.Equal(battle.Record.Teams[side], entry.Teams[side]);
        Assert.False(entry.IsCorrupt);
        Assert.Equal(battle.Record.ToJson(), rebuilt.Load(id).ToJson());
    }

    [Fact]
    public void CorruptReplayIsVisibleButDoesNotBlockFilteringLoadingOrRetentionOfHealthyRecords()
    {
        using var directory = new TestDirectory();
        var now = Epoch;
        var library = new ReplayLibrary(directory.Root, 10, () => now);
        var battle = Play();
        string healthy = Save(library, battle);
        now = Epoch.AddMinutes(1);
        string broken = Save(library, battle);
        string brokenFile = Path.Combine(directory.Root, broken + ".json");
        const string invalidJson = "{\"seed\":";
        File.WriteAllText(brokenFile, invalidJson);

        var listed = library.List();
        var error = Assert.Single(listed, e => e.Id == broken);
        Assert.True(error.IsCorrupt);
        Assert.False(string.IsNullOrWhiteSpace(error.Error));
        Assert.False(Assert.Single(listed, e => e.Id == healthy).IsCorrupt);
        Assert.Equal(battle.Record.ToJson(), library.Load(healthy).ToJson());
        Assert.Throws<FormatException>(() => library.Load(broken));
        AssertIds(library.List(new ReplayFilter { PackId = "no-match", Winner = -1 }), broken);

        now = Epoch.AddMinutes(2);
        var restricted = new ReplayLibrary(directory.Root, 1, () => now);
        string newest = Save(restricted, battle);
        AssertIds(restricted.List(), broken, newest);
        Assert.False(File.Exists(Path.Combine(directory.Root, healthy + ".json")));
        Assert.Equal(invalidJson, File.ReadAllText(brokenFile));
        Assert.Equal(battle.Record.ToJson(), restricted.Load(newest).ToJson());
    }

    [Fact]
    public void EqualTimestampsAndSeedsReceiveDistinctIdsWithoutOverwriting()
    {
        using var directory = new TestDirectory();
        var battle = Play();
        var library = new ReplayLibrary(directory.Root, clock: () => Epoch);
        string first = Save(library, battle, "一");
        string second = Save(library, battle, "二");
        string third = Save(new ReplayLibrary(directory.Root, clock: () => Epoch), battle, "三");

        Assert.Equal(3, new[] { first, second, third }.Distinct(StringComparer.Ordinal).Count());
        AssertIds(library.List(), first, second, third);
        Assert.Equal(new[] { "一", "二", "三" }, new[] { first, second, third }.Select(id => library.List().Single(e => e.Id == id).Note));
        foreach (string id in new[] { first, second, third })
        {
            Assert.True(File.Exists(Path.Combine(directory.Root, id + ".json")));
            Assert.Equal(battle.Record.ToJson(), library.Load(id).ToJson());
        }
    }

    [Fact]
    public void EqualTimestampRetentionKeepsTheLastTwoSavesWithoutReusingDeletedIds()
    {
        using var directory = new TestDirectory();
        var library = new ReplayLibrary(directory.Root, 2, () => Epoch);
        var battle = Play();

        var ids = Enumerable.Range(0, 4).Select(_ => Save(library, battle)).ToArray();

        Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());
        AssertIds(library.List(), ids[2], ids[3]);
        foreach (string id in ids.Take(2)) Assert.False(File.Exists(Path.Combine(directory.Root, id + ".json")));
        foreach (string id in ids.Skip(2))
        {
            Assert.True(File.Exists(Path.Combine(directory.Root, id + ".json")));
            Assert.Equal(battle.Record.ToJson(), library.Load(id).ToJson());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData("../guard")]
    [InlineData("folder/name")]
    [InlineData("folder\\name")]
    [InlineData("/absolute")]
    [InlineData("C:\\record")]
    [InlineData("index")]
    [InlineData("INDEX")]
    [InlineData("CON")]
    public void InvalidIdsAreRejectedByEveryOperationWithoutTouchingOtherFiles(string id)
    {
        using var directory = new TestDirectory();
        string guard = Path.Combine(directory.Root, "guard.json");
        File.WriteAllText(guard, "外侧文件保持不变");
        string root = Path.Combine(directory.Root, "library");
        var library = new ReplayLibrary(root, clock: () => Epoch);
        var battle = Play();
        string valid = Save(library, battle, "保留");
        string indexBefore = File.ReadAllText(Path.Combine(root, "index.json"));
        string replayBefore = File.ReadAllText(Path.Combine(root, valid + ".json"));

        Assert.ThrowsAny<ArgumentException>(() => library.Load(id));
        Assert.ThrowsAny<ArgumentException>(() => library.Delete(id));
        Assert.ThrowsAny<ArgumentException>(() => library.SetNote(id, "不能写入"));

        Assert.Equal("外侧文件保持不变", File.ReadAllText(guard));
        Assert.Equal(indexBefore, File.ReadAllText(Path.Combine(root, "index.json")));
        Assert.Equal(replayBefore, File.ReadAllText(Path.Combine(root, valid + ".json")));
    }

    [Fact]
    public void DirectoriesAndReturnedObjectsRemainIndependentAndDeleteRemovesOnlyItsRecord()
    {
        using var directory = new TestDirectory();
        var first = new ReplayLibrary(Path.Combine(directory.Root, "a"), clock: () => Epoch);
        var second = new ReplayLibrary(Path.Combine(directory.Root, "b"), clock: () => Epoch);
        var battle = Play();
        string firstId = Save(first, battle, "甲库");
        string secondId = Save(second, battle, "乙库");
        var loaded = first.Load(firstId);
        loaded.Names[0] = "改动返回对象";
        loaded.Teams[0][0] = "missing";
        loaded.Events.Clear();
        var entries = first.List();
        entries[0].Names[1] = "改动返回名字";
        entries[0].Teams[1][0] = "missing";
        entries.Clear();

        Assert.Equal(battle.Record.ToJson(), first.Load(firstId).ToJson());
        Assert.Equal(battle.Record.Names, Assert.Single(first.List()).Names);
        first.Delete(firstId);

        Assert.Empty(first.List());
        Assert.False(File.Exists(Path.Combine(directory.Root, "a", firstId + ".json")));
        var remaining = Assert.Single(second.List());
        Assert.Equal(secondId, remaining.Id);
        Assert.Equal("乙库", remaining.Note);
        Assert.Equal(battle.Record.ToJson(), second.Load(secondId).ToJson());
        Assert.Empty(new ReplayLibrary(Path.Combine(directory.Root, "a")).List());
    }

    [Fact]
    public void LibraryOperationsDoNotMutateTheInputsBattleOrRandomStream()
    {
        using var directory = new TestDirectory();
        var battle = Play();
        var summary = BattleSummary.From(battle);
        string recordBefore = battle.Record.ToJson(), summaryBefore = Snapshot(summary);
        string logsBefore = Snapshot(battle.Log), eventsBefore = Snapshot(battle.Events);
        ulong hashBefore = battle.StateHash();
        double nextRandom = battle.Clone().Random();
        var library = new ReplayLibrary(directory.Root, clock: () => Epoch);

        string id = library.Save(battle.Record, summary);
        library.SetNote(id, "备注");
        library.List(new ReplayFilter { Winner = battle.Winner });
        library.Load(id);
        library.Delete(id);

        Assert.Equal(recordBefore, battle.Record.ToJson());
        Assert.Equal(summaryBefore, Snapshot(summary));
        Assert.Equal(logsBefore, Snapshot(battle.Log));
        Assert.Equal(eventsBefore, Snapshot(battle.Events));
        Assert.Equal(hashBefore, battle.StateHash());
        Assert.Equal(nextRandom, battle.Clone().Random());
    }

    [Fact]
    public void NotesAndNamesRoundTripQuotesNewlinesEmojiAndUnpairedUtf16Surrogates()
    {
        using var directory = new TestDirectory();
        var battle = Play();
        var record = BattleRecord.FromJson(battle.Record.ToJson());
        const string special = "引号\"、反斜杠\\、换行\r\n、卫星🛰、孤立高代理\uD800、孤立低代理\uDC00";
        record.Names = new[] { special, "另一方：" + special };
        var library = new ReplayLibrary(directory.Root, clock: () => Epoch);

        string id = library.Save(record, BattleSummary.From(battle), special);

        var entry = Assert.Single(library.List());
        Assert.Equal(special, entry.Note);
        Assert.Equal(record.Names, entry.Names);
        Assert.Equal(record.ToJson(), library.Load(id).ToJson());
        string edited = special + "\n再次编辑\uD800";
        library.SetNote(id, edited);
        var utf8 = new System.Text.UTF8Encoding(false, true);
        Assert.NotEmpty(File.ReadAllText(Path.Combine(directory.Root, "index.json"), utf8));
        Assert.NotEmpty(File.ReadAllText(Path.Combine(directory.Root, id + ".json"), utf8));
        File.Delete(Path.Combine(directory.Root, "index.json"));

        var reopened = new ReplayLibrary(directory.Root, clock: () => Epoch.AddDays(1));
        var rebuilt = Assert.Single(reopened.List());
        Assert.False(rebuilt.IsCorrupt);
        Assert.Equal(edited, rebuilt.Note);
        Assert.Equal(record.Names, rebuilt.Names);
        Assert.Equal(record.ToJson(), reopened.Load(id).ToJson());
    }

    [Fact]
    public void InvalidUtf8BytesAreMarkedCorruptAndLoadThrowsFormatException()
    {
        using var directory = new TestDirectory();
        var library = new ReplayLibrary(directory.Root, clock: () => Epoch);
        var battle = Play();
        string broken = Save(library, battle), healthy = Save(library, battle);
        string path = Path.Combine(directory.Root, broken + ".json");
        byte[] invalidUtf8 = { 0x7b, 0x22, 0x78, 0x22, 0x3a, 0x22, 0xc3, 0x28, 0x22, 0x7d };
        File.WriteAllBytes(path, invalidUtf8);

        var entries = library.List();

        AssertIds(entries, broken, healthy);
        var corrupt = Assert.Single(entries, e => e.Id == broken);
        Assert.True(corrupt.IsCorrupt);
        Assert.False(string.IsNullOrWhiteSpace(corrupt.Error));
        Assert.False(Assert.Single(entries, e => e.Id == healthy).IsCorrupt);
        Assert.Throws<FormatException>(() => library.Load(broken));
        Assert.Equal(battle.Record.ToJson(), library.Load(healthy).ToJson());
        Assert.Equal(invalidUtf8, File.ReadAllBytes(path));
    }
}
