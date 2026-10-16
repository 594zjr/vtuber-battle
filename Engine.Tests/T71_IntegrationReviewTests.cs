using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public sealed class T71_IntegrationReviewTests
{
    [Theory]
    [InlineData(0.5, 1, 640, 360)]
    [InlineData(2, 300, 7680, 4320)]
    public void NewSettingsAndColorblindModePersistTogetherAtSupportedBoundaries(double speed, int seconds, int width, int height)
    {
        string parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "T71-review"));
        string directory = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        try
        {
            var settings = new GameSettings
            {
                AnimationSpeed = speed, TurnSeconds = seconds, Width = width, Height = height,
                Fullscreen = true, TurnTimer = true, ColorblindMode = true, Animations = false,
                FastPlay = true, AiLevel = AiLevel.Expert, DefaultPack = "original_wave3", Language = "zh-CN"
            };
            var store = new FileSaveStore(directory);
            store.SaveSettings(settings);
            var restored = new FileSaveStore(directory).LoadSettings();

            Assert.True(restored.ColorblindMode);
            Assert.Equal(speed, restored.AnimationSpeed);
            Assert.Equal(seconds, restored.TurnSeconds);
            Assert.Equal(width, restored.Width);
            Assert.Equal(height, restored.Height);
            Assert.Equal(settings.ToJson(), restored.ToJson());
            var keys = Assert.IsType<Dictionary<string, object>>(MiniJson.Parse(restored.ToJson())).Keys;
            Assert.Equal(keys.OrderBy(key => key, StringComparer.Ordinal), keys);

            restored.ColorblindMode = false;
            store.SaveSettings(restored);
            var disabled = new FileSaveStore(directory).LoadSettings();
            Assert.False(disabled.ColorblindMode);
            Assert.DoesNotContain("colorblindMode", disabled.ToJson());
            Assert.Equal(restored.ToJson(), disabled.ToJson());
            Assert.True(settings.ColorblindMode);
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(directory)) != parent)
                throw new InvalidOperationException("只能清理当前测试的独立目录。");
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void PriorColorblindSettingsKeepTheirExactEncodingWithoutNewRequiredFields()
    {
        const string oldJson = "{\"aiLevel\":\"Hard\",\"animations\":false,\"colorblindMode\":true,"
            + "\"defaultPack\":\"original\",\"fastPlay\":true,\"language\":\"zh-CN\",\"turnTimer\":true,\"version\":1}";

        var restored = GameSettings.FromJson(oldJson);

        Assert.Equal(oldJson, restored.ToJson());
        Assert.True(restored.ColorblindMode);
        Assert.Equal(1, restored.AnimationSpeed);
        Assert.Equal(10, restored.TurnSeconds);
        Assert.Equal(1600, restored.Width);
        Assert.Equal(900, restored.Height);
        Assert.False(restored.Fullscreen);
    }

    [Fact]
    public void InvalidNewFieldsCannotDiscardValidMainSettingsOrBypassLegacyDifficultyValidation()
    {
        var settings = GameSettings.FromJson("{\"colorblindMode\":true,\"fastPlay\":true,\"turnTimer\":true,"
            + "\"animations\":false,\"defaultPack\":\"original\",\"language\":\"zh-CN\",\"aiLevel\":\"3\","
            + "\"animationSpeed\":{},\"turnSeconds\":-1,\"fullscreen\":\"true\",\"width\":7681,\"height\":360.5}");

        Assert.True(settings.ColorblindMode);
        Assert.True(settings.FastPlay);
        Assert.True(settings.TurnTimer);
        Assert.False(settings.Animations);
        Assert.Equal("original", settings.DefaultPack);
        Assert.Equal("zh-CN", settings.Language);
        Assert.Equal(AiLevel.Normal, settings.AiLevel);
        Assert.Equal(1, settings.AnimationSpeed);
        Assert.Equal(10, settings.TurnSeconds);
        Assert.Equal(1600, settings.Width);
        Assert.Equal(900, settings.Height);
        Assert.False(settings.Fullscreen);
        Assert.Equal(settings.ToJson(), GameSettings.FromJson(settings.ToJson()).ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoubleReplacementAfterBanAndLeadKeepsPaletteSourceNoticesAndIndependentViews(bool colorblind)
    {
        var original = BattleTests.LoadPack("original");
        var fan = BattleTests.LoadPack("fan_asoul_vr");
        var db = ContentDb.Merge(original, fan);
        var team = original.Streamers.Take(3).Concat(fan.Streamers.Take(3)).Select(streamer => streamer.Id).ToArray();
        var battle = new Battle(db, new IList<string>[] { team, team }, new[] { "甲\u0001方", "乙\n方" },
            new BattleOptions { Seed = 71, BanOne = true, LeadPick = true, Modifiers = new List<string> { "no_switch" } });
        battle.ChooseBan(0, 5);
        battle.ChooseBan(1, 5);
        battle.ChooseLead(0, 1);
        battle.ChooseLead(1, 1);
        foreach (var side in battle.Sides)
        foreach (int slot in new[] { 0, 2 })
        {
            side.Team[slot].Hp = 0;
            side.Team[slot].Fainted = true;
        }
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(Phase.Replace, battle.Phase);
        Assert.All(battle.Need, need => Assert.True(need));
        var settings = new GameSettings { ColorblindMode = colorblind, AnimationSpeed = 2, TurnTimer = true, TurnSeconds = 1 };
        string contentHash = db.ContentHash(), record = battle.Record.ToJson();
        string log = JsonSerializer.Serialize(battle.Log, new JsonSerializerOptions { IncludeFields = true });
        ulong hash = battle.StateHash();
        double next = battle.Clone().Random();

        for (int viewer = 0; viewer < 2; viewer++)
        {
            Assert.Empty(battle.LegalFor(viewer).Switches);
            var view = BattleViewModel.From(battle, viewer, settings);
            Assert.Equal(new[] { 1, 3, 4 }, view.SwitchTargets);
            Assert.Equal(db.PackNotice, view.Notice);
            for (int side = 0; side < 2; side++)
            {
                Assert.Equal(5, view.Sides[side].Team.Count);
                Assert.Equal(TextInput.Clean(battle.Sides[side].Name, TextInputKind.PlayerName).Text, view.Sides[side].Name);
                foreach (var fighter in view.Sides[side].Team)
                {
                    Assert.Equal(db.NoticeFor(fighter.Id), fighter.Notice);
                    foreach (var track in fighter.TrackViews)
                        Assert.Equal(colorblind ? db.Types[track.Id].ColorAlt : db.Types[track.Id].Color, track.Color);
                }
            }
            foreach (int target in view.SwitchTargets)
            {
                var branch = battle.Clone();
                branch.Replace(viewer, target);
                Assert.Equal(target, branch.Sides[viewer].Active);
                Assert.Empty(BattleViewModel.From(branch, viewer, settings).SwitchTargets);
                Assert.Equal(new[] { 1, 3, 4 }, BattleViewModel.From(branch, 1 - viewer, settings).SwitchTargets);
            }
            view.SwitchTargets.Clear();
            view.Sides[0].Team[1].TrackViews[0].Color = "#000000";
            Assert.Equal(new[] { 1, 3, 4 }, BattleViewModel.From(battle, viewer, settings).SwitchTargets);
        }
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(contentHash, db.ContentHash());
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(log, JsonSerializer.Serialize(battle.Log, new JsonSerializerOptions { IncludeFields = true }));
        Assert.Equal(next, battle.Clone().Random());

        battle.Replace(0, 1);
        battle.Replace(1, 1);
        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.Empty(BattleViewModel.From(battle, 0, settings).SwitchTargets);
        Assert.Empty(BattleViewModel.From(battle, 1, settings).SwitchTargets);
    }
}
