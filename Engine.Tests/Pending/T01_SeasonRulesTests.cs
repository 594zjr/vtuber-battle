// T-01 验收测试：赛季禁用池与限制池。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T01_SeasonRulesTests
{
    // 内容包里新增：
    // "seasons": { "s1": { "name": "第一赛季", "banned": ["kei"], "limited": [ { "max": 1, "ids": ["bell", "bee"] } ] } }
    // 测试会在原创包上使用 s1。

    [Fact]
    public void ValidTeamHasNoErrors()
    {
        var db = BattleTests.LoadPack("original");
        var errs = TeamCheck.Validate(db, "s1", new[] { "enka", "shiori", "lumi", "bell", "hane", "momo" });
        Assert.Empty(errs);
    }

    [Fact]
    public void BannedStreamerIsRejectedWithItsName()
    {
        var db = BattleTests.LoadPack("original");
        var errs = TeamCheck.Validate(db, "s1", new[] { "enka", "shiori", "lumi", "kei", "hane", "momo" });
        Assert.Single(errs);
        Assert.Contains(db.Streamer("kei").Name, errs[0]);
    }

    [Fact]
    public void LimitedPoolOverMaxIsRejected()
    {
        var db = BattleTests.LoadPack("original");
        var errs = TeamCheck.Validate(db, "s1", new[] { "enka", "shiori", "lumi", "bell", "bee", "momo" });
        Assert.Single(errs);
    }

    [Fact]
    public void WrongSizeOrDuplicatesAreRejected()
    {
        var db = BattleTests.LoadPack("original");
        Assert.NotEmpty(TeamCheck.Validate(db, null, new[] { "enka", "shiori" }));
        Assert.NotEmpty(TeamCheck.Validate(db, null, new[] { "enka", "enka", "lumi", "bell", "hane", "momo" }));
    }

    [Fact]
    public void NullSeasonMeansNoPools()
    {
        var db = BattleTests.LoadPack("original");
        Assert.Empty(TeamCheck.Validate(db, null, new[] { "enka", "shiori", "lumi", "kei", "bell", "bee" }));
    }
}
