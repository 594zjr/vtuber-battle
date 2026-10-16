// T-56 验收测试：队伍保存与导入导出码。实现后把本文件移到 Engine.Tests/ 下。
using System;
using StreamDuel.Core;
using Xunit;

public class T56_TeamCodeTests
{
    [Fact]
    public void CodeRoundTripsAndValidates()
    {
        var db = BattleTests.LoadPack("fan_asoul_vr");
        var t = new TeamPreset { Name = "我的队伍", PackId = db.PackId, Ids = { "bella", "diana", "eileen", "nana7mi", "koichi", "aza" } };
        var code = TeamCode.Encode(t);
        Assert.StartsWith("SDT1-", code);
        Assert.True(code.Length < 200);
        var back = TeamCode.Decode(" " + code + "\n");
        Assert.Equal(t.Name, back.Name); Assert.Equal(t.PackId, back.PackId); Assert.Equal(t.Ids, back.Ids);
        Assert.Empty(back.Check(db, null));
        Assert.Throws<FormatException>(() => TeamCode.Decode(code.Substring(0, code.Length - 2)));
    }

    [Fact]
    public void WrongPackOrUnknownIdIsReported()
    {
        var t = new TeamPreset { Name = "x", PackId = "original", Ids = { "bell", "enka", "shiori", "lumi", "hane", "nobody" } };
        var errs = t.Check(BattleTests.LoadPack("original"), null);
        Assert.Contains(errs, e => e.Contains("nobody"));
        Assert.NotEmpty(t.Check(BattleTests.LoadPack("fan_asoul_vr"), null));
    }

    [Fact]
    public void PresetListRoundTrips()
    {
        var list = new TeamPresetList();
        list.Items.Add(new TeamPreset { Name = "一", PackId = "original", Ids = { "bell", "enka", "shiori", "lumi", "hane", "momo" } });
        list.Items.Add(new TeamPreset { Name = "二", PackId = "original", Ids = { "kei", "yuan", "nyako", "buck", "kasumi", "bee" } });
        Assert.Equal(list.ToJson(), TeamPresetList.FromJson(list.ToJson()).ToJson());
        Assert.Empty(TeamPresetList.FromJson("坏").Items);
    }
}
