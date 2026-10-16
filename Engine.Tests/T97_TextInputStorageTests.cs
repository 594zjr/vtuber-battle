using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using StreamDuel.Core;
using Xunit;

public class T97_TextInputStorageTests
{
    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = "T97-storage-" + Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string source = "")
        {
            parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "bin", "test-temp"));
            Root = Path.Combine(parent, name);
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(resolved) != parent || Path.GetFileName(resolved) != name)
                throw new InvalidOperationException("只能清理当前测试创建的目录");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    static TeamPreset Preset(string name)
    {
        return new TeamPreset
        {
            Name = name, PackId = "original",
            Ids = new List<string> { "bell", "enka", "shiori", "lumi", "hane", "momo" }
        };
    }

    static BattleRecord Record(string first = "甲方", string second = "乙方")
    {
        return new BattleRecord
        {
            Seed = 97, PackId = "original", Names = new[] { first, second },
            Teams = new IList<string>[] { Preset("").Ids, Preset("").Ids }
        };
    }

    [Fact]
    public void PresetCodeJsonAndFileStorePreserveRawNameWhileDisplayRemovesUnsafeCharacters()
    {
        using var directory = new TestDirectory();
        const string raw = "\u3000甲\r\n\u200B乙\0\uD800 🦊\u3000";
        var preset = Preset(raw);
        var list = new TeamPresetList { Items = new List<TeamPreset> { preset } };
        string json = list.ToJson();
        string code = TeamCode.Encode(preset);
        var store = new FileSaveStore(directory.Root);
        store.SaveTeams(list);

        foreach (var restored in new[]
        {
            preset, TeamCode.Decode(code), TeamPresetList.FromJson(json).Items[0], store.LoadTeams().Items[0]
        })
        {
            Assert.Equal(raw, restored.Name);
            Assert.Equal("甲乙 🦊", restored.DisplayName);
            Assert.Equal(restored.DisplayName, restored.NameInput.Text);
            Assert.True(restored.NameInput.Changed);
            Assert.False(restored.NameInput.UsedDefault);
            Assert.False(string.IsNullOrWhiteSpace(restored.NameInput.Message));
            Assert.Equal(code, TeamCode.Encode(restored));
        }
        Assert.Equal(json, File.ReadAllText(Path.Combine(directory.Root, "teams.json")));
        Assert.Equal(json, list.ToJson());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n\u3000")]
    [InlineData("\u200B\u0000")]
    public void EmptyPresetDisplayUsesDefaultWithoutChangingStoredName(string raw)
    {
        var preset = TeamCode.Decode(TeamCode.Encode(Preset(raw)));
        Assert.Equal(raw, preset.Name);
        Assert.Equal("我的队伍", preset.DisplayName);
        Assert.True(preset.NameInput.UsedDefault);
        Assert.True(preset.NameInput.Changed);
    }

    [Fact]
    public void PresetDisplayAppliesTeamLimitAndBannedWordsWithoutChangingTheShareCode()
    {
        string word = TextLint.DefaultBannedWords[0];
        var preset = Preset(word + string.Concat(Enumerable.Repeat("🦊", 20)));
        string code = TeamCode.Encode(preset);
        var decoded = TeamCode.Decode(code);
        Assert.DoesNotContain(word, decoded.DisplayName);
        Assert.Contains("*", decoded.DisplayName);
        Assert.True(decoded.NameInput.Changed);
        Assert.True(decoded.DisplayName.EnumerateRunes().Count() <= 16);
        Assert.Equal(preset.Name, decoded.Name);
        Assert.Equal(code, TeamCode.Encode(decoded));

        decoded.Name = string.Concat(Enumerable.Repeat("🦊", 20));
        Assert.Equal(string.Concat(Enumerable.Repeat("🦊", 16)), decoded.DisplayName);
        decoded.Name = "普通队伍 123";
        Assert.Equal("普通队伍 123", decoded.DisplayName);
        Assert.False(decoded.NameInput.Changed);
    }

    [Fact]
    public void ReplayMetadataKeepsRawNamesAndNotesWhileEveryReadUsesFilteredDisplayProperties()
    {
        using var directory = new TestDirectory();
        const string first = " 甲\r\n\u200B方\uD800 ";
        const string second = "乙方🛰";
        const string note = "\u3000前\0\u200B后\r\n\uD800 🛰 ";
        var record = Record(first, second);
        string recordJson = record.ToJson();
        var library = new ReplayLibrary(directory.Root);
        string id = library.Save(record, new BattleSummary(), note);
        var entry = Assert.Single(library.List());

        Assert.Equal(record.Names, entry.Names);
        Assert.Equal(new[] { "甲方", "乙方🛰" }, entry.DisplayNames);
        Assert.True(entry.NameInputs[0].Changed);
        Assert.False(entry.NameInputs[1].Changed);
        Assert.Equal(note, entry.Note);
        Assert.Equal("前后 🛰", entry.DisplayNote);
        Assert.True(entry.NoteInput.Changed);
        Assert.Equal(recordJson, library.Load(id).ToJson());

        var names = entry.DisplayNames;
        names[0] = "外部改动";
        Assert.Equal("甲方", entry.DisplayNames[0]);
        entry.NameInputs[0] = null;
        Assert.NotNull(entry.NameInputs[0]);
        Assert.Equal(first, entry.Names[0]);

        string edited = "\u200B" + string.Concat(Enumerable.Repeat("🦊", 65)) + "\n\uDC00";
        library.SetNote(id, edited);
        File.Delete(Path.Combine(directory.Root, "index.json"));
        var reopened = new ReplayLibrary(directory.Root);
        entry = Assert.Single(reopened.List());
        Assert.Equal(edited, entry.Note);
        Assert.Equal(string.Concat(Enumerable.Repeat("🦊", 60)), entry.DisplayNote);
        Assert.True(entry.NoteInput.Changed);
        Assert.Equal(record.Names, entry.Names);
        Assert.Equal(recordJson, reopened.Load(id).ToJson());
    }

    [Fact]
    public void ReplayNamesUseThePlayerLimitAndNotesMaskBannedWordsOnDisplayOnly()
    {
        string word = TextLint.DefaultBannedWords[0];
        string raw = word + string.Concat(Enumerable.Repeat("🛰", 20));
        var entry = new ReplayEntry { Names = new[] { raw, "\u200B\u0000" }, Note = raw };
        Assert.Equal(raw, entry.Names[0]);
        Assert.Equal(raw, entry.Note);
        Assert.DoesNotContain(word, entry.DisplayNames[0]);
        Assert.Contains("*", entry.DisplayNames[0]);
        Assert.True(entry.DisplayNames[0].EnumerateRunes().Count() <= 12);
        Assert.Equal("玩家", entry.DisplayNames[1]);
        Assert.True(entry.NameInputs[1].UsedDefault);
        Assert.DoesNotContain(word, entry.DisplayNote);
        Assert.True(entry.NoteInput.Changed);

        entry.Names[0] = string.Concat(Enumerable.Repeat("🛰", 20));
        Assert.Equal(string.Concat(Enumerable.Repeat("🛰", 12)), entry.DisplayNames[0]);
    }

    [Fact]
    public void DefaultNoteDisplayNeverCreatesAFavoriteAndClearingStillTriggersRetention()
    {
        using var directory = new TestDirectory();
        var now = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        var library = new ReplayLibrary(directory.Root, 1, () => now);
        var record = Record();
        string first = library.Save(record, new BattleSummary());
        var entry = Assert.Single(library.List());
        Assert.Null(entry.Note);
        Assert.Equal("无备注", entry.DisplayNote);
        Assert.True(entry.NoteInput.UsedDefault);
        now = now.AddSeconds(1);
        string favorite = library.Save(record, new BattleSummary(), "收藏");
        Assert.Equal(favorite, Assert.Single(library.List()).Id);
        Assert.False(File.Exists(Path.Combine(directory.Root, first + ".json")));

        library.SetNote(favorite, " \t\r\n\u3000");
        entry = Assert.Single(library.List());
        Assert.Null(entry.Note);
        Assert.Equal("无备注", entry.DisplayNote);
        now = now.AddSeconds(1);
        string newest = library.Save(record, new BattleSummary());
        Assert.Equal(newest, Assert.Single(library.List()).Id);
        Assert.False(File.Exists(Path.Combine(directory.Root, favorite + ".json")));
    }
}
