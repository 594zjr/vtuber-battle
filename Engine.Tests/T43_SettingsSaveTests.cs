// T-43 验收测试：设置与本地存档。实现后把本文件移到 Engine.Tests/ 下。
using System;
using System.IO;
using StreamDuel.Core;
using Xunit;

public class T43_SettingsSaveTests
{
    [Fact]
    public void SettingsRoundTripAndMigrate()
    {
        var s = new GameSettings { FastPlay = true, TurnTimer = true, Animations = false, AiLevel = AiLevel.Hard, DefaultPack = "fan_asoul_vr", Language = "zh-CN" };
        var back = GameSettings.FromJson(s.ToJson());
        Assert.Equal(s.ToJson(), back.ToJson());
        Assert.Equal(GameSettings.CurrentVersion, back.Version);
        // 旧版本（没有 version、没有 aiLevel）读进来用默认值补齐
        var old = GameSettings.FromJson("{\"fastPlay\":true}");
        Assert.True(old.FastPlay);
        Assert.Equal(AiLevel.Normal, old.AiLevel);
        Assert.Equal(GameSettings.CurrentVersion, old.Version);
        Assert.Equal(new GameSettings().ToJson(), GameSettings.FromJson("坏掉").ToJson());
    }

    [Fact]
    public void FileSaveStoreKeepsEverythingUnderItsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sd_save_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSaveStore(root);
            store.SaveSettings(new GameSettings { FastPlay = true });
            Assert.True(store.LoadSettings().FastPlay);
            var p = new Profile();
            store.SaveProfile(p);
            Assert.Equal(p.ToJson(), store.LoadProfile().ToJson());
            for (int i = 0; i < 55; i++) store.SaveReplay("r" + i.ToString("D2"), "{\"seed\":" + i + "}");
            Assert.Equal(50, store.ListReplays().Count);
            Assert.Equal("{\"seed\":54}", store.LoadReplay(store.ListReplays()[0]));
            Assert.Throws<ArgumentException>(() => store.SaveReplay("../evil", "{}"));
            Assert.All(Directory.GetFiles(root, "*", SearchOption.AllDirectories), f => Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(f)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void MissingFilesGiveDefaults()
    {
        var store = new FileSaveStore(Path.Combine(Path.GetTempPath(), "sd_none_" + Guid.NewGuid().ToString("N")));
        Assert.Equal(new GameSettings().ToJson(), store.LoadSettings().ToJson());
        Assert.Empty(store.LoadProfile().Packs);
        Assert.Empty(store.ListReplays());
    }
}
