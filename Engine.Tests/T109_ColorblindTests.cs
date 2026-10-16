using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T109_ColorblindTests
{
    static string Resource(string path)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", path));
    }

    static Battle Make()
    {
        var db = BattleTests.LoadPack("original");
        db.Streamer("bell").Type2 = "tech";
        db.Streamer("kei").Type2 = "song";
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        return new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "甲方", "乙方" }, 109);
    }

    static string Snapshot(object value)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true });
    }

    static void AssertTracks(Battle battle, BattleViewModel view, bool enabled)
    {
        for (int side = 0; side < battle.Sides.Length; side++)
            for (int slot = 0; slot < battle.Sides[side].Team.Count; slot++)
            {
                var definition = battle.Sides[side].Team[slot].Def;
                var fighter = view.Sides[side].Team[slot];
                var expected = new[] { definition.Type, definition.Type2 }.Where(id => id != null).ToArray();
                Assert.Equal(expected, fighter.TrackViews.Select(track => track.Id));
                Assert.Equal(Describe.Tracks(battle.Db, definition), fighter.Tracks);
                foreach (var track in fighter.TrackViews)
                {
                    var type = battle.Db.Types[track.Id];
                    Assert.Equal(type.Name, track.Name);
                    Assert.Equal(type.Symbol, track.Symbol);
                    Assert.Equal(enabled ? type.ColorAlt : type.Color, track.Color);
                }
            }
        foreach (var skill in view.Skills)
        {
            var definition = battle.Db.Skills[skill.Id];
            var type = battle.Db.Types[definition.Type];
            Assert.Equal(definition.Type, skill.TrackId);
            Assert.Equal(type.Name, skill.Track);
            Assert.Equal(type.Symbol, skill.TrackSymbol);
            Assert.Equal(enabled ? type.ColorAlt : type.Color, skill.TrackColor);
        }
    }

    [Fact]
    public void EveryTrackHasItsOwnSymbolAndBothDisplayColors()
    {
        var db = BattleTests.LoadPack("original");
        Assert.Equal(8, db.Types.Count);
        Assert.All(db.Types.Values, type =>
        {
            Assert.False(string.IsNullOrWhiteSpace(type.Symbol));
            Assert.False(string.IsNullOrWhiteSpace(type.Color));
            Assert.False(string.IsNullOrWhiteSpace(type.ColorAlt));
        });
        Assert.Equal(db.Types.Count, db.Types.Values.Select(type => type.Symbol).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(db.Types.Values, type => type.Color != type.ColorAlt);
    }

    [Fact]
    public void OlderRulesAndManuallyCreatedTypesHaveUsableDisplayDefaults()
    {
        var rules = (Dictionary<string, object>)MiniJson.Parse(Resource("StreamDuelRules.json"));
        foreach (var value in ((Dictionary<string, object>)rules["types"]).Values)
        {
            var type = (Dictionary<string, object>)value;
            type.Remove("symbol");
            type.Remove("colorAlt");
        }
        var db = ContentDb.FromJson(JsonSerializer.Serialize(rules), Resource(Path.Combine("Packs", "original.json")));
        Assert.All(db.Types.Values, type =>
        {
            Assert.Equal("●", type.Symbol);
            Assert.Equal(type.Color, type.ColorAlt);
        });
        var manual = new TypeDef { Id = "custom", Name = "自定义赛道", Color = "#123456" };
        Assert.Equal("●", manual.Symbol);
        Assert.Equal(manual.Color, manual.ColorAlt);
        manual.Color = "#abcdef";
        Assert.Equal(manual.Color, manual.ColorAlt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void MissingOrBlankDisplayMetadataStillProducesUsableTrackAndSkillViews(string missing)
    {
        var battle = Make();
        foreach (var type in battle.Db.Types.Values)
        {
            type.Symbol = missing;
            type.ColorAlt = missing;
        }
        var normal = BattleViewModel.From(battle, 0);
        var accessible = BattleViewModel.From(battle, 0, new GameSettings { ColorblindMode = true });
        Assert.Equal(Snapshot(normal), Snapshot(accessible));
        foreach (var track in accessible.Sides.SelectMany(side => side.Team).SelectMany(fighter => fighter.TrackViews))
        {
            Assert.Equal("●", track.Symbol);
            Assert.Equal(battle.Db.Types[track.Id].Color, track.Color);
        }
        foreach (var skill in accessible.Skills)
        {
            Assert.Equal("●", skill.TrackSymbol);
            Assert.Equal(battle.Db.Types[skill.TrackId].Color, skill.TrackColor);
        }
    }

    [Fact]
    public void DefaultNullAndDisabledSettingsKeepTheOriginalColorsAndViewContent()
    {
        var battle = Make();
        for (int side = 0; side < 2; side++)
        {
            var original = BattleViewModel.From(battle, side);
            var absent = BattleViewModel.From(battle, side, null);
            var disabled = BattleViewModel.From(battle, side, new GameSettings());
            Assert.Equal(Snapshot(original), Snapshot(absent));
            Assert.Equal(Snapshot(original), Snapshot(disabled));
            AssertTracks(battle, original, false);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void BothModesCoverPrimarySecondaryBenchAndSkillTracks(int side, bool enabled)
    {
        var battle = Make();
        var view = BattleViewModel.From(battle, side, new GameSettings { ColorblindMode = enabled });
        AssertTracks(battle, view, enabled);
        Assert.Equal(8, view.Sides.SelectMany(team => team.Team).SelectMany(fighter => fighter.TrackViews)
            .Select(track => track.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(view.Sides[0].Team, fighter => fighter.Id == "bell" && fighter.TrackViews.Count == 2);
        Assert.Contains(view.Sides[1].Team, fighter => fighter.Id == "kei" && fighter.TrackViews.Count == 2);
        Assert.Equal(5, view.Skills.Count);
    }

    [Fact]
    public void ChangingModeOnlyChangesViewColorsWithoutTouchingCombatOrRandomness()
    {
        var battle = Make();
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        var untouched = battle.Clone();
        ulong hash = battle.StateHash();
        string contentHash = battle.Db.ContentHash();
        string record = battle.Record.ToJson();
        string log = Snapshot(battle.Log);
        string events = Snapshot(battle.Events);

        for (int side = 0; side < 2; side++)
        {
            var normal = BattleViewModel.From(battle, side, new GameSettings());
            var accessible = BattleViewModel.From(battle, side, new GameSettings { ColorblindMode = true });
            AssertTracks(battle, accessible, true);
            AssertTracks(battle, normal, false);
            // 排除唯一预期变化后比较整个快照，覆盖合法行动、伤害、时机、状态和说明。
            foreach (var fighter in accessible.Sides.SelectMany(team => team.Team))
                foreach (var track in fighter.TrackViews) track.Color = battle.Db.Types[track.Id].Color;
            foreach (var skill in accessible.Skills) skill.TrackColor = battle.Db.Types[skill.TrackId].Color;
            Assert.Equal(Snapshot(normal), Snapshot(accessible));
        }
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(contentHash, battle.Db.ContentHash());
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(log, Snapshot(battle.Log));
        Assert.Equal(events, Snapshot(battle.Events));
        for (int draw = 0; draw < 12; draw++) Assert.Equal(untouched.Random(), battle.Random());
    }

    [Fact]
    public void TrackSnapshotsDoNotAliasSettingsDefinitionsOtherFightersOrOtherSnapshots()
    {
        var battle = Make();
        var settings = new GameSettings { ColorblindMode = true };
        var first = BattleViewModel.From(battle, 0, settings);
        var second = BattleViewModel.From(battle, 0, settings);
        string firstBefore = Snapshot(first), secondBefore = Snapshot(second);
        settings.ColorblindMode = false;
        Assert.Equal(firstBefore, Snapshot(first));
        Assert.Equal(secondBefore, Snapshot(second));

        var track = first.Sides[0].Team[0].TrackViews[0];
        string id = track.Id, symbol = track.Symbol;
        string ordinaryColor = battle.Db.Types[id].Color;
        string alternateColor = battle.Db.Types[id].ColorAlt;
        track.Symbol = "只改快照";
        track.Color = "#000000";
        first.Skills[0].TrackSymbol = "技能快照";
        first.Skills[0].TrackColor = "#ffffff";
        Assert.Equal(symbol, battle.Db.Types[id].Symbol);
        Assert.Equal(ordinaryColor, battle.Db.Types[id].Color);
        Assert.Equal(alternateColor, battle.Db.Types[id].ColorAlt);
        Assert.Equal(secondBefore, Snapshot(second));
        var anotherFighter = first.Sides[1].Team.Single(fighter => fighter.Id == "buck");
        Assert.Equal(id, anotherFighter.TrackViews[0].Id);
        Assert.Equal(symbol, anotherFighter.TrackViews[0].Symbol);
        Assert.Equal(alternateColor, anotherFighter.TrackViews[0].Color);

        battle.Db.Types[id].Symbol = "新定义";
        battle.Db.Types[id].ColorAlt = "#654321";
        Assert.Equal(secondBefore, Snapshot(second));
        var newest = BattleViewModel.From(battle, 0, new GameSettings { ColorblindMode = true });
        Assert.Equal("新定义", newest.Sides[0].Team[0].TrackViews[0].Symbol);
        Assert.Equal("#654321", newest.Sides[0].Team[0].TrackViews[0].Color);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":0,\"fastPlay\":true}")]
    [InlineData("{\"animations\":false,\"language\":\"zh-CN\"}")]
    public void OlderSettingsDefaultToOriginalPalette(string json)
    {
        Assert.False(new GameSettings().ColorblindMode);
        var settings = GameSettings.FromJson(json);
        Assert.False(settings.ColorblindMode);
        Assert.Equal(GameSettings.CurrentVersion, settings.Version);
        using var serialized = JsonDocument.Parse(settings.ToJson());
        Assert.False(serialized.RootElement.TryGetProperty("colorblindMode", out _));
        Assert.False(GameSettings.FromJson(settings.ToJson()).ColorblindMode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BooleanSettingRoundTripsWithoutChangingOtherOptions(bool enabled)
    {
        var settings = GameSettings.FromJson("{\"colorblindMode\":" + (enabled ? "true" : "false")
            + ",\"fastPlay\":true,\"turnTimer\":true,\"animations\":false,\"aiLevel\":\"Hard\"}");
        Assert.Equal(enabled, settings.ColorblindMode);
        Assert.True(settings.FastPlay);
        Assert.True(settings.TurnTimer);
        Assert.False(settings.Animations);
        Assert.Equal(AiLevel.Hard, settings.AiLevel);
        using var json = JsonDocument.Parse(settings.ToJson());
        if (enabled) Assert.True(json.RootElement.GetProperty("colorblindMode").GetBoolean());
        var keys = json.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(keys.OrderBy(key => key, StringComparer.Ordinal), keys);
        var restored = GameSettings.FromJson(settings.ToJson());
        Assert.Equal(enabled, restored.ColorblindMode);
        Assert.Equal(settings.ToJson(), restored.ToJson());
        restored.ColorblindMode = !enabled;
        Assert.Equal(enabled, settings.ColorblindMode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("\"true\"")]
    [InlineData("\"false\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void WronglyTypedModeDefaultsWithoutDiscardingOtherValidSettings(string value)
    {
        var settings = GameSettings.FromJson("{\"fastPlay\":true,\"colorblindMode\":" + value + "}");
        Assert.True(settings.FastPlay);
        Assert.False(settings.ColorblindMode);
    }

    [Theory]
    [InlineData("{\"colorblindMode\":true")]
    [InlineData("{\"colorblindMode\":true} trailing")]
    [InlineData("{\"colorblindMode\":true,\"language\":\"\\q\"}")]
    public void DamagedJsonDoesNotPartiallyEnableTheMode(string json)
    {
        Assert.Equal(new GameSettings().ToJson(), GameSettings.FromJson(json).ToJson());
        Assert.False(GameSettings.FromJson(json).ColorblindMode);
    }

    [Fact]
    public void FileSettingsPersistEnablingAndDisablingAndMigrateExistingFiles()
    {
        using var files = new TestDirectory();
        var store = new FileSaveStore(files.Root);
        Assert.False(store.LoadSettings().ColorblindMode);
        string path = Path.Combine(files.Root, "settings.json");
        File.WriteAllText(path, "{\"fastPlay\":true,\"animations\":false}");
        var old = store.LoadSettings();
        Assert.False(old.ColorblindMode);
        Assert.True(old.FastPlay);
        old.ColorblindMode = true;
        store.SaveSettings(old);
        var reopened = new FileSaveStore(files.Root);
        var loaded = reopened.LoadSettings();
        Assert.True(loaded.ColorblindMode);
        Assert.True(loaded.FastPlay);
        Assert.False(loaded.Animations);
        loaded.ColorblindMode = false;
        reopened.SaveSettings(loaded);
        Assert.False(new FileSaveStore(files.Root).LoadSettings().ColorblindMode);
        Assert.True(new FileSaveStore(files.Root).LoadSettings().FastPlay);
        File.WriteAllText(path, "{\"colorblindMode\":true");
        Assert.False(reopened.LoadSettings().ColorblindMode);
        Assert.Equal("{\"colorblindMode\":true", File.ReadAllText(path));
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string source = "")
        {
            parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "obj", nameof(T109_ColorblindTests)));
            Root = Path.GetFullPath(Path.Combine(parent, name));
            VerifyPath();
            Directory.CreateDirectory(Root);
        }

        void VerifyPath()
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(Root)), parent, StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(Root), name, StringComparison.Ordinal))
                throw new InvalidOperationException("只能操作当前测试的 obj 目录");
        }

        public void Dispose()
        {
            VerifyPath();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
