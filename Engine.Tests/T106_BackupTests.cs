using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T106_BackupTests
{
    static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 10, 16, 8, 30, 0, TimeSpan.FromHours(8));

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string sourceFile = "")
        {
            parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile), "obj", "T106", "temp"));
            Root = Path.Combine(parent, name);
            Directory.CreateDirectory(Root);
        }

        public string Child(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(Root, relative));
            if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("测试路径必须位于当前夹具目录内。");
            return path;
        }

        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(resolved), name, StringComparison.Ordinal)
                || (Directory.Exists(resolved) && (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidOperationException("只能清理当前测试创建的普通目录。");
            if (Directory.Exists(resolved)) DeleteTree(resolved);
        }

        static void DeleteTree(string directory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                bool childDirectory = (attributes & FileAttributes.Directory) != 0;
                // 联接只删除目录项，不递归进入其目标；目标也只会位于当前夹具内。
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (childDirectory) Directory.Delete(path); else File.Delete(path);
                }
                else if (childDirectory) DeleteTree(path);
                else File.Delete(path);
            }
            Directory.Delete(directory);
        }
    }

    sealed class SavedData
    {
        public string Root, LibraryDirectory;
        public FileSaveStore Store;
        public Battle FirstBattle, SecondBattle;
        public string FirstId, SecondId;
    }

    static Battle Play(uint seed = 106, int winner = 0)
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(s => s.Id).Take(12).ToArray();
        var b = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "甲方\n\"名字\"", "乙方🛰" }, seed);
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        if (winner == -1) b.ResolveTurn(BattleAction.Surrender(), BattleAction.Surrender());
        else b.Surrender(1 - winner);
        Assert.Equal(Phase.Over, b.Phase);
        return b;
    }

    static Profile Stats(int wins)
    {
        var profile = new Profile();
        profile.Packs["original"] = new PackStats
        {
            Wins = wins, Losses = 2, Draws = 1,
            Picks = new Dictionary<string, int> { { "kei", wins + 3 }, { "enka", 2 } },
            PickWins = new Dictionary<string, int> { { "kei", wins }, { "enka", 1 } }
        };
        return profile;
    }

    static TeamPresetList Teams(params string[] names)
    {
        return new TeamPresetList
        {
            Items = names.Select(name => new TeamPreset
            {
                Name = name, PackId = "original",
                Ids = new List<string> { "kei", "enka", "shiori", "lumi", "hane", "momo" }
            }).ToList()
        };
    }

    static SavedData Seed(string root, int wins = 3, bool second = true)
    {
        var data = new SavedData { Root = root, LibraryDirectory = Path.Combine(root, "replay-library"), Store = new FileSaveStore(root) };
        data.Store.SaveSettings(new GameSettings
        {
            FastPlay = true, TurnTimer = true, Animations = false, AiLevel = AiLevel.Hard,
            DefaultPack = "original", Language = "zh-CN"
        });
        data.Store.SaveProfile(Stats(wins));
        data.Store.SaveTeams(Teams("常用\n\"甲\"🛰", "另一队"));
        data.FirstBattle = Play(winner: 0);
        data.Store.SaveReplay("opaque_shared", "旧版任意载荷，不是 JSON\n\"保真\"🛰");
        var library = new ReplayLibrary(data.LibraryDirectory, clock: () => Epoch);
        data.FirstId = library.Save(data.FirstBattle.Record, BattleSummary.From(data.FirstBattle), "收藏\n备注：\\与\"引号\"🛰");
        if (second)
        {
            data.SecondBattle = Play(107, -1);
            data.Store.SaveReplay("record_unique", data.SecondBattle.Record.ToJson());
            data.SecondId = new ReplayLibrary(data.LibraryDirectory, clock: () => Epoch.AddDays(1))
                .Save(data.SecondBattle.Record, BattleSummary.From(data.SecondBattle));
        }
        return data;
    }

    static string Export(SavedData data, bool replays = true)
    {
        return Backup.Export(data.Store, data.LibraryDirectory, Epoch.AddDays(2), replays);
    }

    static string Change(string json, Action<JsonObject> change)
    {
        var root = JsonNode.Parse(json).AsObject();
        change(root);
        return root.ToJsonString();
    }

    static void ChangeLibrary(JsonObject root, int index, Action<JsonObject> change)
    {
        var entry = root["library"].AsArray()[index].AsObject();
        entry["json"] = Change(entry["json"].GetValue<string>(), change);
    }

    static SortedDictionary<string, string> Tree(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(root)) ReadTree(root, root, result);
        return result;
    }

    static void ReadTree(string root, string directory, SortedDictionary<string, string> result)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            string relative = Path.GetRelativePath(root, path);
            var attributes = File.GetAttributes(path);
            bool childDirectory = (attributes & FileAttributes.Directory) != 0;
            if ((attributes & FileAttributes.ReparsePoint) != 0) result[relative] = "链接";
            else if (childDirectory) { result[relative + "/"] = "目录"; ReadTree(root, path, result); }
            else result[relative] = Convert.ToBase64String(File.ReadAllBytes(path));
        }
    }

    static void ChineseFailure(Action action)
    {
        var error = Record.Exception(action);
        Assert.NotNull(error);
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
    }

    static void AssertSavedEqual(SavedData expected, FileSaveStore actual, string actualLibrary)
    {
        Assert.Equal(expected.Store.LoadSettings().ToJson(), actual.LoadSettings().ToJson());
        Assert.Equal(expected.Store.LoadProfile().ToJson(), actual.LoadProfile().ToJson());
        Assert.Equal(expected.Store.LoadTeams().ToJson(), actual.LoadTeams().ToJson());
        Assert.Equal(expected.Store.ListReplays().OrderBy(id => id, StringComparer.Ordinal),
            actual.ListReplays().OrderBy(id => id, StringComparer.Ordinal));
        foreach (string id in expected.Store.ListReplays()) Assert.Equal(expected.Store.LoadReplay(id), actual.LoadReplay(id));
        var source = new ReplayLibrary(expected.LibraryDirectory).List();
        var target = new ReplayLibrary(actualLibrary).List();
        Assert.Equal(source.Select(e => e.Id), target.Select(e => e.Id));
        foreach (var entry in source)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(expected.LibraryDirectory, entry.Id + ".json")),
                File.ReadAllBytes(Path.Combine(actualLibrary, entry.Id + ".json")));
            var original = entry.Id == expected.FirstId ? expected.FirstBattle : expected.SecondBattle;
            var replay = Replay.Run(original.Db, new ReplayLibrary(actualLibrary).Load(entry.Id));
            Assert.Equal(original.StateHash(), replay.StateHash());
            Assert.Equal(BattleSummary.From(original).ToText(), BattleSummary.From(replay).ToText());
        }
    }

    [Fact]
    public void ExportAndImportRoundTripSettingsTeamsProfileOpaqueReplaysAndRealLibraryRecords()
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"));
        string json = Export(source);
        Backup.Validate(json);
        var destination = new FileSaveStore(dir.Child("target"));
        string library = dir.Child("target/replay-library");

        var result = Backup.Import(destination, library, json, Epoch.AddDays(3));

        Assert.Equal(2, result.ImportedTeams);
        Assert.Equal(2, result.ImportedReplays);
        Assert.Equal(2, result.ImportedLibraryReplays);
        Assert.Null(result.AutomaticBackupPath);
        AssertSavedEqual(source, new FileSaveStore(dir.Child("target")), library);
    }

    [Fact]
    public void ExportIsDeterministicAtAnExplicitTimeAndOnlyIncludesWhitelistedSaveData()
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"));
        const string privateMarker = "THIS_IS_NOT_A_SAVE_ITEM";
        File.WriteAllText(Path.Combine(source.Root, "unrelated.json"), privateMarker);
        File.WriteAllText(Path.Combine(source.LibraryDirectory, "notes.txt"), privateMarker);
        var before = Tree(source.Root);

        string first = Export(source), second = Export(source);

        Assert.Equal(first, second);
        Assert.Equal(before, Tree(source.Root));
        Assert.DoesNotContain(privateMarker, first);
        Assert.DoesNotContain(source.Root, first);
        using var parsed = JsonDocument.Parse(first);
        Assert.Equal("StreamDuelBackup", parsed.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, parsed.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(Epoch.AddDays(2), DateTimeOffset.ParseExact(parsed.RootElement.GetProperty("exportedAt").GetString(), "O", CultureInfo.InvariantCulture));
        Assert.True(parsed.RootElement.GetProperty("includeReplays").GetBoolean());
        Assert.Equal(new[] { "exportedAt", "format", "includeReplays", "library", "libraryIndex", "profile", "replays", "settings", "teams", "version" },
            parsed.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void MergeRetainsLocalSettingsAndWholeExistingPackStatsInsteadOfAddingCounts()
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"), second: false);
        var incoming = source.Store.LoadProfile();
        incoming.Packs["imported_pack"] = new PackStats { Wins = 8, Picks = new Dictionary<string, int> { { "x", 8 } } };
        source.Store.SaveProfile(incoming);
        var local = Seed(dir.Child("target"), wins: 91, second: false);
        var settings = new GameSettings { FastPlay = false, TurnTimer = false, DefaultPack = "local_pack", Language = "en-US" };
        local.Store.SaveSettings(settings);
        var profile = local.Store.LoadProfile();
        profile.Packs["local_pack"] = new PackStats { Draws = 7 };
        local.Store.SaveProfile(profile);

        Backup.Import(local.Store, local.LibraryDirectory, Export(source), Epoch.AddDays(3));

        Assert.Equal(settings.ToJson(), local.Store.LoadSettings().ToJson());
        var merged = local.Store.LoadProfile();
        Assert.Equal(91, merged.Packs["original"].Wins);
        Assert.Equal(profile.Packs["original"].Picks, merged.Packs["original"].Picks);
        Assert.Equal(7, merged.Packs["local_pack"].Draws);
        Assert.Equal(8, merged.Packs["imported_pack"].Wins);
    }

    [Fact]
    public void MergeNamesUseOrdinalComparisonAndFindUnusedSuffixesAcrossTheWholeBatch()
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"), second: false);
        source.Store.SaveTeams(Teams("常用", "常用 (2)", "常用", "Alpha"));
        var destination = new FileSaveStore(dir.Child("target"));
        var original = Teams("常用", "常用 (2)", "alpha");
        destination.SaveTeams(original);
        string sourceBefore = source.Store.LoadTeams().ToJson();

        var result = Backup.Import(destination, dir.Child("target/replay-library"), Export(source), Epoch.AddDays(3));

        Assert.Equal(4, result.ImportedTeams);
        var actual = destination.LoadTeams();
        Assert.Equal(new[] { "常用", "常用 (2)", "alpha", "常用 (3)", "常用 (2) (2)", "常用 (4)", "Alpha" }, actual.Items.Select(t => t.Name));
        Assert.Equal(original.ToJson(), new TeamPresetList { Items = actual.Items.Take(3).ToList() }.ToJson());
        Assert.All(actual.Items, team => Assert.Equal(original.Items[0].Ids, team.Ids));
        Assert.Equal(sourceBefore, source.Store.LoadTeams().ToJson());
    }

    [Fact]
    public void MergeDeduplicatesBothReplayStoresByIdAndKeepsDifferentLocalContents()
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"));
        var local = Seed(dir.Child("target"), second: false);
        local.Store.SaveReplay("opaque_shared", "本机同 id 的内容优先");
        new ReplayLibrary(local.LibraryDirectory).SetNote(local.FirstId, "本机备注优先");
        byte[] oldRecord = File.ReadAllBytes(Path.Combine(local.LibraryDirectory, local.FirstId + ".json"));

        var result = Backup.Import(local.Store, local.LibraryDirectory, Export(source), Epoch.AddDays(3));

        Assert.Equal(1, result.ImportedReplays);
        Assert.Equal(1, result.ImportedLibraryReplays);
        Assert.Equal("本机同 id 的内容优先", local.Store.LoadReplay("opaque_shared"));
        Assert.Equal(oldRecord, File.ReadAllBytes(Path.Combine(local.LibraryDirectory, local.FirstId + ".json")));
        Assert.Equal(source.Store.LoadReplay("record_unique"), local.Store.LoadReplay("record_unique"));
        Assert.Equal(new[] { source.FirstId, source.SecondId }.OrderBy(x => x, StringComparer.Ordinal),
            new ReplayLibrary(local.LibraryDirectory).List().Select(e => e.Id).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void ReplayIdCaseCollisionsFollowTheDestinationFileSystem()
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"), second: false);
        source.Store.SaveReplay("CaseReplay", "导入内容");
        var target = new FileSaveStore(dir.Child("target"));
        target.SaveReplay("casereplay", "已有内容");
        bool sameFile = File.Exists(dir.Child("target/replays/CaseReplay.json"));

        var result = Backup.Import(target, dir.Child("target/replay-library"), Export(source), Epoch.AddDays(3));

        Assert.Equal("已有内容", target.LoadReplay("casereplay"));
        Assert.Equal(sameFile ? 1 : 2, result.ImportedReplays);
        if (!sameFile) Assert.Equal("导入内容", target.LoadReplay("CaseReplay"));
    }

    [Fact]
    public void OverwriteFirstCreatesAnIndependentRestorableBackupOfThePreviousData()
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"));
        var target = Seed(dir.Child("target"), wins: 91, second: false);
        target.Store.SaveSettings(new GameSettings { DefaultPack = "old_pack", FastPlay = false });
        target.Store.SaveTeams(Teams("旧队伍"));
        target.Store.SaveReplay("old_only", "只能在自动备份中找回");
        new ReplayLibrary(target.LibraryDirectory).SetNote(target.FirstId, "旧备注");
        string oldSnapshot = Export(target);

        var result = Backup.Import(target.Store, target.LibraryDirectory, Export(source), Epoch.AddDays(3), BackupImportMode.Overwrite);

        Assert.False(string.IsNullOrWhiteSpace(result.AutomaticBackupPath));
        string backupPath = Path.GetFullPath(result.AutomaticBackupPath);
        Assert.StartsWith(Path.Combine(target.Root, "backups") + Path.DirectorySeparatorChar, backupPath, StringComparison.Ordinal);
        Assert.StartsWith("before-restore-", Path.GetFileName(backupPath));
        Assert.True(File.Exists(backupPath));
        string automatic = File.ReadAllText(backupPath);
        Backup.Validate(automatic);
        foreach (string key in new[] { "settings", "profile", "teams", "replays", "library", "libraryIndex" })
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(oldSnapshot)[key], JsonNode.Parse(automatic)[key]), key);
        AssertSavedEqual(source, target.Store, target.LibraryDirectory);
        Assert.DoesNotContain("old_only", target.Store.ListReplays());

        var recovered = new FileSaveStore(dir.Child("recovered"));
        Backup.Import(recovered, dir.Child("recovered/replay-library"), automatic, Epoch.AddDays(4));
        Assert.Equal(91, recovered.LoadProfile().Packs["original"].Wins);
        Assert.Equal("旧队伍", Assert.Single(recovered.LoadTeams().Items).Name);
        Assert.Equal("只能在自动备份中找回", recovered.LoadReplay("old_only"));
        Assert.Equal("旧备注", Assert.Single(new ReplayLibrary(dir.Child("recovered/replay-library")).List()).Note);
    }

    [Fact]
    public void FailureToCreateTheAutomaticBackupDoesNotOverwriteAnyExistingData()
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"));
        var target = Seed(dir.Child("target"), wins: 91);
        File.WriteAllText(Path.Combine(target.Root, "backups"), "普通文件不能当成备份目录");
        var before = Tree(target.Root);

        ChineseFailure(() => Backup.Import(target.Store, target.LibraryDirectory, Export(source), Epoch.AddDays(3), BackupImportMode.Overwrite));

        Assert.Equal(before, Tree(target.Root));
    }

    [Fact]
    public void ExcludingReplaysPreservesBothReplayDirectoriesAndIndexEvenInOverwriteMode()
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"));
        var target = Seed(dir.Child("target"), wins: 91);
        target.Store.SaveReplay("local_only", "本机保留");
        new ReplayLibrary(target.LibraryDirectory).SetNote(target.FirstId, "本地备注保留");
        var oldReplays = Tree(Path.Combine(target.Root, "replays"));
        var oldLibrary = Tree(target.LibraryDirectory);
        string payload = Export(source, replays: false);
        var root = JsonNode.Parse(payload);
        Assert.False(root["includeReplays"].GetValue<bool>());
        Assert.Empty(root["replays"].AsArray());
        Assert.Empty(root["library"].AsArray());

        var result = Backup.Import(target.Store, target.LibraryDirectory, payload, Epoch.AddDays(3), BackupImportMode.Overwrite);

        Assert.Equal(0, result.ImportedReplays);
        Assert.Equal(0, result.ImportedLibraryReplays);
        Assert.Equal(oldReplays, Tree(Path.Combine(target.Root, "replays")));
        Assert.Equal(oldLibrary, Tree(target.LibraryDirectory));
        Assert.Equal(source.Store.LoadProfile().ToJson(), target.Store.LoadProfile().ToJson());
        Assert.True(JsonNode.Parse(File.ReadAllText(result.AutomaticBackupPath))["includeReplays"].GetValue<bool>());
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("future")]
    [InlineData("wrong-format")]
    [InlineData("missing-settings")]
    [InlineData("bad-settings-bool")]
    [InlineData("negative-profile")]
    [InlineData("fractional-profile")]
    [InlineData("bad-last-team")]
    [InlineData("bad-last-library-json")]
    [InlineData("future-library-record")]
    [InlineData("library-id-mismatch")]
    [InlineData("library-turn-mismatch")]
    public void InvalidOrFutureContentIsRejectedBeforeAnyDestinationChanges(string defect)
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"));
        var target = Seed(dir.Child("target"), wins: 91);
        string payload = Export(source);
        if (defect == "truncated") payload = payload.Substring(0, payload.Length / 2);
        else payload = Change(payload, root =>
        {
            switch (defect)
            {
                case "future": root["version"] = 2; break;
                case "wrong-format": root["format"] = "OtherBackup"; break;
                case "missing-settings": root.Remove("settings"); break;
                case "bad-settings-bool": root["settings"]["fastPlay"] = "true"; break;
                case "negative-profile": root["profile"]["packs"]["original"]["wins"] = -1; break;
                case "fractional-profile": root["profile"]["packs"]["original"]["wins"] = 1.5; break;
                case "bad-last-team": root["teams"]["items"].AsArray().Add(new JsonObject { ["name"] = "前半份有效，最后一项损坏" }); break;
                case "bad-last-library-json": root["library"].AsArray()[1]["json"] = "{坏文件"; break;
                case "future-library-record": ChangeLibrary(root, 1, r => r["version"] = BattleRecord.CurrentVersion + 1); break;
                case "library-id-mismatch": ChangeLibrary(root, 1, r => r["library"]["id"] = "another_id"); break;
                case "library-turn-mismatch": ChangeLibrary(root, 1, r => r["library"]["turns"] = 900); break;
                default: throw new InvalidOperationException("未定义的损坏测试。");
            }
        });
        var before = Tree(target.Root);

        ChineseFailure(() => Backup.Validate(payload));
        ChineseFailure(() => Backup.Import(target.Store, target.LibraryDirectory, payload, Epoch.AddDays(3)));
        Assert.Equal(before, Tree(target.Root));
        ChineseFailure(() => Backup.Import(target.Store, target.LibraryDirectory, payload, Epoch.AddDays(3), BackupImportMode.Overwrite));
        Assert.Equal(before, Tree(target.Root));
    }

    [Fact]
    public void EscapedIsolatedSurrogateInLegacyPayloadIsRejectedBeforeAnyFileWrite()
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"), second: false);
        var target = Seed(dir.Child("target"), wins: 91, second: false);
        const string marker = "T106_ISOLATED_SURROGATE";
        string payload = Change(Export(source), root => root["replays"].AsArray()[0]["json"] = marker);
        // 外层 JSON 保持合法 ASCII 转义；不能让测试序列化器先把代理项替换掉。
        payload = payload.Replace("\"" + marker + "\"", "\"\\ud800\"");
        Assert.Contains("\"json\":\"\\ud800\"", payload);
        Assert.DoesNotContain(marker, payload);
        var before = Tree(dir.Root);

        ChineseFailure(() => Backup.Validate(payload));
        ChineseFailure(() => Backup.Import(target.Store, target.LibraryDirectory, payload, Epoch));
        ChineseFailure(() => Backup.Import(target.Store, target.LibraryDirectory, payload, Epoch, BackupImportMode.Overwrite));

        Assert.Equal(before, Tree(dir.Root));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("escaped-root")]
    [InlineData("replays")]
    [InlineData("library")]
    public void DuplicateFieldsAndCaseOnlyReplayIdsAreRejectedBeforeAnyDestinationChanges(string duplicate)
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"));
        var target = Seed(dir.Child("target"), wins: 91, second: false);
        string payload = Export(source);
        if (duplicate == "root") payload = payload.Insert(1, "\"version\":1,");
        else if (duplicate == "escaped-root") payload = payload.Insert(1, "\"ver\\u0073ion\":1,");
        else payload = Change(payload, root =>
        {
            root[duplicate].AsArray()[0]["id"] = "CaseReplay";
            root[duplicate].AsArray()[1]["id"] = "casereplay";
            if (duplicate == "library")
            {
                ChangeLibrary(root, 0, record => record["library"]["id"] = "CaseReplay");
                ChangeLibrary(root, 1, record => record["library"]["id"] = "casereplay");
            }
        });
        var before = Tree(dir.Root);

        ChineseFailure(() => Backup.Validate(payload));
        ChineseFailure(() => Backup.Import(target.Store, target.LibraryDirectory, payload, Epoch));
        ChineseFailure(() => Backup.Import(target.Store, target.LibraryDirectory, payload, Epoch, BackupImportMode.Overwrite));

        Assert.Equal(before, Tree(dir.Root));
    }

    [Fact]
    public void LockedManagedOpaqueReplayCannotBeSilentlyOmittedFromExportOrAutomaticBackup()
    {
        using var dir = new TestDirectory();
        var incoming = Seed(dir.Child("incoming"));
        var target = Seed(dir.Child("target"), wins: 91, second: false);
        string payload = Export(incoming);
        string locked = Path.Combine(target.Root, "replays", "opaque_shared.json");
        Assert.Contains("opaque_shared", target.Store.ListReplays());
        var before = Tree(dir.Root);

        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => Export(target));
            Assert.ThrowsAny<IOException>(() => Backup.Import(target.Store, target.LibraryDirectory, payload, Epoch, BackupImportMode.Overwrite));
        }

        Assert.Equal(before, Tree(dir.Root));
        Assert.Contains("opaque_shared", target.Store.ListReplays());
        Backup.Validate(Export(target));
    }

    [Theory]
    [InlineData(".settings.lock")]
    [InlineData(".teams.lock")]
    [InlineData("replay-library/.library.lock")]
    public void LockedSourceComponentPreventsExportWithoutCreatingOrChangingSourceFiles(string relativeLock)
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"), second: false);
        var before = Tree(source.Root);

        using (new FileStream(Path.Combine(source.Root, relativeLock), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => Export(source));

        Assert.Equal(before, Tree(source.Root));
        Backup.Validate(Export(source));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("nested/file")]
    [InlineData("C:\\outside")]
    [InlineData("name:stream")]
    [InlineData("CON")]
    [InlineData("index")]
    public void UnsafeLibraryIdsNeverBecomePathsOrModifySiblingFiles(string id)
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"), second: false);
        var target = Seed(dir.Child("target"), wins: 91, second: false);
        File.WriteAllText(dir.Child("escape.json"), "夹具内的兄弟哨兵");
        string payload = Change(Export(source), root =>
        {
            root["library"].AsArray()[0]["id"] = id;
            ChangeLibrary(root, 0, r => r["library"]["id"] = id);
        });
        var before = Tree(dir.Root);

        ChineseFailure(() => Backup.Validate(payload));
        ChineseFailure(() => Backup.Import(target.Store, target.LibraryDirectory, payload, Epoch.AddDays(3), BackupImportMode.Overwrite));

        Assert.Equal(before, Tree(dir.Root));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("legacy")]
    [InlineData("sibling")]
    public void ReplayDirectoryMustBeAnIndependentChildOfTheSaveRoot(string location)
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"));
        var target = Seed(dir.Child("target"), wins: 91);
        string other = dir.Child("target-other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "sentinel.txt"), "保留");
        string library = location == "root" ? target.Root : location == "legacy" ? Path.Combine(target.Root, "replays") : other;
        string payload = Export(source);
        var before = Tree(dir.Root);

        ChineseFailure(() => Backup.Export(target.Store, library, Epoch));
        ChineseFailure(() => Backup.Import(target.Store, library, payload, Epoch, BackupImportMode.Overwrite));

        Assert.Equal(before, Tree(dir.Root));
    }

    [Theory]
    [InlineData("settings.json/child")]
    [InlineData("profile.json/child")]
    [InlineData("teams.json/child")]
    [InlineData("library.")]
    public void ReservedSaveFileAncestorsAndTrailingDotAliasesAreRejectedBeforeCreatingAnEmptyTargetsFiles(string relative)
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"), second: false);
        string payload = Export(source);
        var target = new FileSaveStore(dir.Child("target"));
        string library = Path.Combine(dir.Child("target"), relative);
        var before = Tree(dir.Child("target"));
        Assert.Empty(before);

        ChineseFailure(() => Backup.Export(target, library, Epoch));
        Assert.Equal(before, Tree(dir.Child("target")));
        ChineseFailure(() => Backup.Import(target, library, payload, Epoch));
        Assert.Equal(before, Tree(dir.Child("target")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExportDerivesTheIndexFromRecordsWithoutRewritingAMissingOrCorruptSourceIndex(bool missing)
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"));
        string index = Path.Combine(source.LibraryDirectory, "index.json");
        if (missing) File.Delete(index); else File.WriteAllText(index, "{坏索引");
        var before = Tree(source.Root);

        string payload = Export(source);

        Assert.Equal(before, Tree(source.Root));
        Backup.Validate(payload);
        var destination = new FileSaveStore(dir.Child("target"));
        Backup.Import(destination, dir.Child("target/replay-library"), payload, Epoch);
        Assert.Equal(new[] { source.FirstId, source.SecondId }.OrderBy(id => id, StringComparer.Ordinal),
            new ReplayLibrary(dir.Child("target/replay-library")).List().Select(e => e.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void OverwritePreservesUnmanagedFilesAndDoesNotTreatAnIndexAsDeletionAuthority()
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"), second: false);
        var target = Seed(dir.Child("target"), wins: 91);
        string foreign = Path.Combine(target.Root, "replays", "foreign.json");
        File.WriteAllText(foreign, "{\"external\":true}");
        File.WriteAllText(Path.Combine(target.Root, "notes.txt"), "本机说明");
        File.WriteAllText(Path.Combine(target.LibraryDirectory, "notes.txt"), "录像库说明");
        string sentinel = dir.Child("outside.json");
        File.WriteAllText(sentinel, "兄弟目录数据");
        File.WriteAllText(Path.Combine(target.LibraryDirectory, "index.json"),
            "{\"version\":1,\"entries\":[{\"id\":\"../../outside\"}]}");

        Backup.Import(target.Store, target.LibraryDirectory, Export(source), Epoch, BackupImportMode.Overwrite);

        Assert.Equal("{\"external\":true}", File.ReadAllText(foreign));
        Assert.Equal("本机说明", File.ReadAllText(Path.Combine(target.Root, "notes.txt")));
        Assert.Equal("录像库说明", File.ReadAllText(Path.Combine(target.LibraryDirectory, "notes.txt")));
        Assert.Equal("兄弟目录数据", File.ReadAllText(sentinel));
    }

    [Fact]
    public void ImportDoesNotInvokeEitherReplayRetentionLimitOrGenerateNewIds()
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"), second: false);
        string record = source.FirstBattle.Record.ToJson();
        for (int i = 0; i < 51; i++) File.WriteAllText(Path.Combine(source.Root, "replays", "old_" + i.ToString("D3") + ".json"), record);
        string libraryRecord = File.ReadAllText(Path.Combine(source.LibraryDirectory, source.FirstId + ".json"));
        for (int i = 0; i < 201; i++)
        {
            string id = "clip_" + i.ToString("D3");
            File.WriteAllText(Path.Combine(source.LibraryDirectory, id + ".json"),
                Change(libraryRecord, root => { root["library"]["id"] = id; root["library"]["note"] = null; }));
        }
        var destination = new FileSaveStore(dir.Child("target"));

        var result = Backup.Import(destination, dir.Child("target/replay-library"), Export(source), Epoch);

        Assert.Equal(52, result.ImportedReplays);
        Assert.Equal(202, result.ImportedLibraryReplays);
        Assert.Equal(source.Store.ListReplays().OrderBy(id => id, StringComparer.Ordinal),
            destination.ListReplays().OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(Directory.GetFiles(source.LibraryDirectory, "*.json").Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal),
            Directory.GetFiles(dir.Child("target/replay-library"), "*.json").Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void LinkedReplayDirectoryCannotRedirectExportOrImportToTheSiblingSentinel()
    {
        using var dir = new TestDirectory();
        var source = Seed(dir.Child("source"));
        var target = new FileSaveStore(dir.Child("target"));
        target.SaveProfile(Stats(91));
        string outside = dir.Child("sibling");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel.json"), "不可读写的兄弟哨兵");
        string link = dir.Child("target/replay-library");
        CreateDirectoryLink(link, outside);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        var before = Tree(dir.Root);
        string payload = Export(source);
        try
        {
            ChineseFailure(() => Backup.Export(target, link, Epoch));
            ChineseFailure(() => Backup.Import(target, link, payload, Epoch, BackupImportMode.Overwrite));
            Assert.Equal(before, Tree(dir.Root));
        }
        finally { Directory.Delete(link); }
    }

    static void CreateDirectoryLink(string link, string target)
    {
        if (Path.DirectorySeparatorChar != '\\') { Directory.CreateSymbolicLink(link, target); return; }
        // Windows 目录联接不要求开启开发者模式；参数只指向当前测试夹具。
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" +
            link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null");
        using var process = Process.Start(start);
        string error = process.StandardError.ReadToEnd();
        string standard = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(10000), "创建测试联接超时。");
        Assert.True(process.ExitCode == 0, "创建测试联接失败：" + error + standard);
    }
}
