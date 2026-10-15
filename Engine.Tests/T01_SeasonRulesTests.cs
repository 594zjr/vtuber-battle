// T-01 验收测试：赛季禁用池与限制池。实现后把本文件移到 Engine.Tests/ 下。
using System;
using System.IO;
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

    [Fact]
    public void SeasonDefinitionsAreLoadedAndOlderPacksRemainCompatible()
    {
        var db = BattleTests.LoadPack("original");
        var season = Assert.Single(db.Seasons).Value;
        Assert.Equal("第一赛季", season.Name);
        Assert.Equal(new[] { "kei" }, season.Banned);
        var pool = Assert.Single(season.Limited);
        Assert.Equal(1, pool.Max);
        Assert.Equal(new[] { "bell", "bee" }, pool.Ids);
        Assert.Empty(BattleTests.LoadPack("fan_holo_niji").Seasons);
    }

    [Fact]
    public void UnknownSeasonReturnsOneErrorEvenForInvalidTeam()
    {
        var errors = TeamCheck.Validate(BattleTests.LoadPack("original"), "missing-season", new[] { "unknown" });
        Assert.Contains("missing-season", Assert.Single(errors));
    }

    [Fact]
    public void UnknownStreamerReturnsAnErrorInsteadOfThrowing()
    {
        var errors = TeamCheck.Validate(BattleTests.LoadPack("original"), "s1", new[] { "enka", "shiori", "lumi", "bell", "hane", "unknown" });
        Assert.Contains("unknown", Assert.Single(errors));
    }

    [Theory]
    [InlineData("\"banned\": [\"kei\"]", "\"banned\": [\"missing-banned\"]", "missing-banned", "禁用池")]
    [InlineData("\"ids\": [\"bell\", \"bee\"]", "\"ids\": [\"bell\", \"missing-limited\"]", "missing-limited", "限制池 1")]
    public void InvalidSeasonReferencesIdentifySeasonAndId(string original, string replacement, string missingId, string poolName)
    {
        var content = Path.Combine(AppContext.BaseDirectory, "Content");
        var rules = File.ReadAllText(Path.Combine(content, "StreamDuelRules.json"));
        var pack = File.ReadAllText(Path.Combine(content, "Packs", "original.json"));
        Assert.Contains(original, pack);
        var error = Assert.Throws<KeyError>(() => ContentDb.FromJson(rules, pack.Replace(original, replacement)));
        Assert.Contains("s1", error.Message);
        Assert.Contains(missingId, error.Message);
        Assert.Contains(poolName, error.Message);
    }

    [Fact]
    public void EachLimitedPoolUsesItsOwnMaximumAndReportsDisplayNames()
    {
        var db = BattleTests.LoadPack("original");
        var season = db.Seasons["s1"];
        season.Limited.Add(new LimitedPoolDef { Max = 1, Ids = { "enka", "shiori" } });
        var team = new[] { "enka", "shiori", "lumi", "bell", "bee", "momo" };
        var errors = TeamCheck.Validate(db, "s1", team);
        Assert.Equal(2, errors.Count);
        Assert.Contains("第一赛季限制池 1", errors[0]);
        Assert.Contains("最多允许 1 位", errors[0]);
        Assert.Contains(db.Streamer("bell").Name, errors[0]);
        Assert.Contains(db.Streamer("bee").Name, errors[0]);
        Assert.Contains("第一赛季限制池 2", errors[1]);
        Assert.Contains(db.Streamer("enka").Name, errors[1]);
        Assert.Contains(db.Streamer("shiori").Name, errors[1]);
        season.Limited[0].Max = 2;
        season.Limited[1].Max = 2;
        Assert.Empty(TeamCheck.Validate(db, "s1", team));
        season.Limited[1].Max = 0;
        Assert.Contains("最多允许 0 位", Assert.Single(TeamCheck.Validate(db, "s1", team)));
    }

    [Fact]
    public void ErrorsFollowValidationOrderAndKnownStreamersUseDisplayNames()
    {
        var db = BattleTests.LoadPack("original");
        var errors = TeamCheck.Validate(db, "s1", new[] { "enka", "enka", "unknown", "kei", "bell", "bee", "lumi" });
        Assert.Equal(5, errors.Count);
        Assert.Contains("正好 6 位", errors[0]);
        Assert.Contains(db.Streamer("enka").Name, errors[1]);
        Assert.Contains("重复", errors[1]);
        Assert.Contains("unknown", errors[2]);
        Assert.Contains(db.Streamer("kei").Name, errors[3]);
        Assert.Contains("第一赛季禁用池", errors[3]);
        Assert.Contains("限制池", errors[4]);
    }
}
