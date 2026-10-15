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

public class T102_ReplayStatsTests
{
    static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = "T102-replay-" + Guid.NewGuid().ToString("N");
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
                throw new InvalidOperationException("只能清理本次统计测试创建的目录。");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    static Battle Play(bool ban = false)
    {
        var skills = new Dictionary<string, object>();
        foreach (string id in new[] { "finish", "wait", "third", "fourth", "fifth" })
            skills[id] = new
            {
                name = "统计测试技能", type = "talk", cat = "status", pp = 20,
                effects = id == "finish"
                    ? new object[] { new { code = "FIXED_DAMAGE", at = "post", target = "foe", n = 99999 } }
                    : Array.Empty<object>()
            };
        var pack = new
        {
            id = "t102_snapshot", name = "统计快照测试包", notice = "测试数据。", skills,
            personas = new Dictionary<string, object>
            {
                ["neutral"] = new { name = "测试人设", code = "T_LOWHP_POWER", @params = new { below = 0, mul = 1 } }
            },
            streamers = Enumerable.Range(0, 12).Select(i => new
            {
                id = "p" + i, name = "测试主播" + i, type = "talk", persona = "neutral",
                stats = new { hp = 300, atk = 80, def = 80, spa = 80, sdef = 80, spd = 100 },
                skills = new[] { "finish", "wait", "third", "fourth" }, fifth = "fifth"
            }).ToArray()
        };
        string rules = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", "StreamDuelRules.json"));
        var db = ContentDb.FromJson(rules, JsonSerializer.Serialize(pack));
        var battle = new Battle(db, new IList<string>[]
        {
            Enumerable.Range(0, 6).Select(i => "p" + i).ToArray(),
            Enumerable.Range(6, 6).Select(i => "p" + i).ToArray()
        }, new[] { "房主", "客机" }, new BattleOptions { Seed = 102, BanOne = ban });
        if (ban)
        {
            battle.ChooseBan(0, 2);
            battle.ChooseBan(1, 4);
        }
        for (int guard = 0; battle.Phase != Phase.Over && guard < 30; guard++)
        {
            if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
            else
                for (int side = 0; side < 2; side++)
                    if (battle.Need[side]) battle.Replace(side, battle.Sides[side].Team.FindIndex(f => !f.Fainted));
        }
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(1, battle.Winner);
        return battle;
    }

    static void AssertStats(BattleSummary summary, int side, ReplayStats stats, AiLevel? level)
    {
        Assert.NotNull(stats);
        Assert.Equal(side, stats.PlayerSide);
        Assert.Equal(level, stats.OpponentAiLevel);
        Assert.Equal(summary.Sides[side].Select(f => f.StreamerId), stats.Fighters.Select(f => f.StreamerId));
        Assert.Equal(summary.Sides[side].Select(f => f.DamageDealt), stats.Fighters.Select(f => f.DamageDealt));
        Assert.Equal(summary.Sides[side].Select(f => f.Kos), stats.Fighters.Select(f => f.Kos));
    }

    [Fact]
    public void ExplicitGuestPerspectiveSavesActualDamageAndKosAfterBan()
    {
        using var directory = new TestDirectory();
        var battle = Play(ban: true);
        var summary = BattleSummary.From(battle);
        var library = new ReplayLibrary(directory.Root, clock: () => Epoch);
        library.Save(battle.Record, summary, playerSide: 1, aiLevel: AiLevel.Hard);

        var entry = Assert.Single(library.ReadStatsSnapshot());
        Assert.False(entry.IsCorrupt);
        Assert.Equal(6, entry.Teams[1].Count);
        Assert.Equal(5, entry.Stats.Fighters.Count);
        Assert.DoesNotContain(entry.Stats.Fighters, f => f.StreamerId == "p8");
        AssertStats(summary, 1, entry.Stats, AiLevel.Hard);
        Assert.True(entry.Stats.Fighters.Sum(f => f.DamageDealt) > 0);
        Assert.Equal(5, entry.Stats.Fighters.Sum(f => f.Kos));
        Assert.Equal(0, summary.Sides[0].Sum(f => f.DamageDealt));
    }

    [Fact]
    public void LegacySaveKeepsExactJsonShapeAndDoesNotInventPerspective()
    {
        using var directory = new TestDirectory();
        var battle = Play();
        var library = new ReplayLibrary(directory.Root, clock: () => Epoch);
        string id = library.Save(battle.Record, BattleSummary.From(battle));
        string original = battle.Record.ToJson();
        string expected = original.Substring(0, original.Length - 1)
            + ",\"library\":{\"version\":1,\"id\":\"" + id + "\",\"date\":\""
            + Epoch.ToString("O", CultureInfo.InvariantCulture) + "\",\"winner\":1,\"turns\":"
            + battle.Record.Events.Count(e => e.Type == BattleRecordEventType.Turn).ToString(CultureInfo.InvariantCulture)
            + ",\"note\":null}}";
        Assert.Equal(expected, File.ReadAllText(Path.Combine(directory.Root, id + ".json")));
        Assert.Null(Assert.Single(library.List()).Stats);
        Assert.Null(Assert.Single(library.ReadStatsSnapshot()).Stats);
        Assert.Equal(original, library.Load(id).ToJson());
    }

    [Fact]
    public void NotesAndIndexRebuildPreserveStatsWithoutKeepingSummaryOrSnapshotReferences()
    {
        using var directory = new TestDirectory();
        var battle = Play();
        var summary = BattleSummary.From(battle);
        var expected = BattleSummary.From(battle);
        var library = new ReplayLibrary(directory.Root, clock: () => Epoch);
        string id = library.Save(battle.Record, summary, playerSide: 1);
        string path = Path.Combine(directory.Root, id + ".json");
        using var beforeJson = JsonDocument.Parse(File.ReadAllText(path));
        string statsJson = beforeJson.RootElement.GetProperty("library").GetProperty("stats").GetRawText();

        summary.Sides[1][0].DamageDealt = 0;
        summary.Sides[1].Clear();
        var first = Assert.Single(library.ReadStatsSnapshot());
        first.Stats.Fighters[0].DamageDealt = -1;
        first.Stats.Fighters.Clear();
        first.Stats.PlayerSide = 0;
        first.Teams[1].Clear();
        first.Names[1] = "改写快照";
        library.SetNote(id, "保留统计的备注");
        File.Delete(Path.Combine(directory.Root, "index.json"));

        var loaded = Assert.Single(new ReplayLibrary(directory.Root).List());
        Assert.Equal("保留统计的备注", loaded.Note);
        Assert.Equal("客机", loaded.Names[1]);
        Assert.Equal(6, loaded.Teams[1].Count);
        AssertStats(expected, 1, loaded.Stats, null);
        using var afterJson = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(statsJson, afterJson.RootElement.GetProperty("library").GetProperty("stats").GetRawText());
        using var indexJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Root, "index.json")));
        Assert.Equal(statsJson, indexJson.RootElement.GetProperty("entries")[0].GetProperty("stats").GetRawText());
    }

    [Fact]
    public void InvalidStatsAreRejectedBeforeAnyFileChanges()
    {
        using var directory = new TestDirectory();
        var battle = Play();
        var library = new ReplayLibrary(directory.Root, clock: () => Epoch);
        library.Save(battle.Record, BattleSummary.From(battle), playerSide: 1);
        var before = Directory.GetFiles(directory.Root).ToDictionary(Path.GetFileName, File.ReadAllBytes);
        Assert.ThrowsAny<ArgumentException>(() => library.Save(battle.Record, BattleSummary.From(battle), aiLevel: AiLevel.Normal));
        Assert.ThrowsAny<ArgumentException>(() => library.Save(battle.Record, BattleSummary.From(battle), playerSide: -1));
        Assert.ThrowsAny<ArgumentException>(() => library.Save(battle.Record, BattleSummary.From(battle), playerSide: 2));
        Assert.ThrowsAny<ArgumentException>(() => library.Save(battle.Record, BattleSummary.From(battle), playerSide: 1, aiLevel: (AiLevel)99));
        foreach (Action<BattleSummary> change in new Action<BattleSummary>[]
        {
            s => s.Sides = null,
            s => s.Sides[1] = null,
            s => s.Sides[1].Clear(),
            s => s.Sides[1].Add(s.Sides[1][0]),
            s => s.Sides[1][0] = null,
            s => s.Sides[1][0].StreamerId = "p0",
            s => s.Sides[1][0].StreamerId = s.Sides[1][1].StreamerId,
            s => s.Sides[1][0].DamageDealt = -1,
            s => s.Sides[1][0].Kos = -1
        })
        {
            var summary = BattleSummary.From(battle);
            change(summary);
            Assert.ThrowsAny<ArgumentException>(() => library.Save(battle.Record, summary, playerSide: 1));
        }
        Assert.Equal(before.Keys.OrderBy(n => n, StringComparer.Ordinal),
            Directory.GetFiles(directory.Root).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(directory.Root, pair.Key)));
    }

    [Theory]
    [InlineData("\"damageDealt\":-1,\"kos\":0")]
    [InlineData("\"damageDealt\":1.5,\"kos\":0")]
    [InlineData("\"damageDealt\":0,\"kos\":-1")]
    public void CorruptStatsRemainVisibleAndAreNotPrunedOrRewritten(string counts)
    {
        using var directory = new TestDirectory();
        var battle = Play();
        var library = new ReplayLibrary(directory.Root, maxCount: 1, clock: () => Epoch);
        string badId = library.Save(battle.Record, BattleSummary.From(battle), playerSide: 1);
        string badPath = Path.Combine(directory.Root, badId + ".json");
        string original = File.ReadAllText(badPath);
        int statsStart = original.LastIndexOf(",\"stats\":", StringComparison.Ordinal);
        Assert.True(statsStart > 0);
        string corrupt = original.Substring(0, statsStart)
            + ",\"stats\":{\"version\":1,\"playerSide\":1,\"aiLevel\":null,\"fighters\":[{\"streamerId\":\"p6\","
            + counts + "}]}}}";
        File.WriteAllText(badPath, corrupt, new UTF8Encoding(false));
        string goodId = library.Save(battle.Record, BattleSummary.From(battle), playerSide: 1);

        var entries = library.List();
        Assert.True(Assert.Single(entries, e => e.Id == badId).IsCorrupt);
        Assert.False(Assert.Single(entries, e => e.Id == goodId).IsCorrupt);
        Assert.Throws<FormatException>(() => library.Load(badId));
        Assert.Throws<FormatException>(() => library.SetNote(badId, "不能重写坏统计"));
        Assert.Equal(corrupt, File.ReadAllText(badPath));
    }

    [Fact]
    public void StatsReadOfExistingEmptyLibraryCreatesNoFiles()
    {
        using var directory = new TestDirectory();
        var library = new ReplayLibrary(directory.Root, clock: () => Epoch);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Root));

        Assert.Empty(library.ReadStatsSnapshot());

        Assert.Empty(Directory.GetFileSystemEntries(directory.Root));
    }

    [Theory]
    [InlineData("pack")]
    [InlineData("mine")]
    [InlineData("foe")]
    public void DisabledReplayKeepsValidatedStatsWithoutWeakeningReplayPolicy(string target)
    {
        using var directory = new TestDirectory();
        var battle = Play();
        var summary = BattleSummary.From(battle);
        var initial = new ReplayLibrary(directory.Root, clock: () => Epoch, policy: PackPolicy.Empty);
        string id = initial.Save(battle.Record, summary, playerSide: 1, aiLevel: AiLevel.Hard);
        var policy = PackPolicy.FromJson(JsonSerializer.Serialize(new
        {
            disabledPacks = target == "pack" ? new[] { battle.Db.PackId } : Array.Empty<string>(),
            disabledStreamers = target == "pack" ? Array.Empty<string>() : new[] { target == "mine" ? "p6" : "p0" }
        }));
        var restricted = new ReplayLibrary(directory.Root, policy: policy);
        var before = Directory.GetFiles(directory.Root).ToDictionary(Path.GetFileName, File.ReadAllBytes);

        var entries = restricted.ReadStatsSnapshot();
        var entry = Assert.Single(entries);
        Assert.True(entry.IsUnavailable);
        Assert.False(entry.IsCorrupt);
        Assert.Null(entry.Error);
        Assert.Equal(policy.CheckTeam(battle.Db.PackId, battle.Record.Teams.SelectMany(team => team)), entry.UnavailableReason);
        AssertStats(summary, 1, entry.Stats, AiLevel.Hard);

        // 下架限制播放和修改，不能把已保存的历史数值摘要当成坏录像丢弃。
        var stats = new ProfileStats(null, entries);
        Assert.Equal(6, stats.ByStreamer().Count);
        Assert.All(stats.ByStreamer(), row => Assert.Equal(1L, row.SampleGames));
        Assert.Equal(1L, Assert.Single(stats.TopTeams(1)).Wins);
        Assert.Equal(1L, Assert.Single(stats.VsAi()).Wins);
        Assert.Equal(ProfileBattleOutcome.Win, Assert.Single(stats.Recent(1)).Outcome);
        Assert.Throws<InvalidOperationException>(() => restricted.Load(id));
        Assert.Throws<InvalidOperationException>(() => restricted.SetNote(id, "不绕过下架限制"));
        Assert.Throws<InvalidOperationException>(() => restricted.Save(battle.Record, summary, playerSide: 1));
        Assert.Equal(before.Keys.OrderBy(name => name, StringComparer.Ordinal),
            Directory.GetFiles(directory.Root).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(directory.Root, pair.Key)));

        var restored = Assert.Single(new ReplayLibrary(directory.Root, policy: PackPolicy.Empty).ReadStatsSnapshot());
        Assert.False(restored.IsUnavailable);
        Assert.Null(restored.UnavailableReason);
        AssertStats(summary, 1, restored.Stats, AiLevel.Hard);
    }

    [Fact]
    public void StatsReadUsesReplayFilesAndDoesNotRewriteOrRecreateIndex()
    {
        using var directory = new TestDirectory();
        var battle = Play();
        var library = new ReplayLibrary(directory.Root, clock: () => Epoch);
        string id = library.Save(battle.Record, BattleSummary.From(battle), playerSide: 1, aiLevel: AiLevel.Expert);
        string index = Path.Combine(directory.Root, "index.json");
        byte[] forgedIndex = Encoding.UTF8.GetBytes("这不是可信索引，也不能用它推测视角。");
        File.WriteAllBytes(index, forgedIndex);

        var entries = library.ReadStatsSnapshot();
        Assert.Equal(id, Assert.Single(entries).Id);
        AssertStats(BattleSummary.From(battle), 1, entries[0].Stats, AiLevel.Expert);
        Assert.Equal(forgedIndex, File.ReadAllBytes(index));
        File.Delete(index);
        Assert.Equal(id, Assert.Single(library.ReadStatsSnapshot()).Id);
        Assert.False(File.Exists(index));
    }
}
