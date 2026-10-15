// T-42 验收测试：文本表（为以后多语言做准备）。实现后把本文件移到 Engine.Tests/ 下。
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using StreamDuel.Core;
using Xunit;

public class T42_LocalizationTests
{
    static string Res(string rel) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", rel));

    [Fact]
    public void LookupFormatsAndFallsBack()
    {
        var loc = Loc.FromJson(Res(Path.Combine("Locale", "zh-CN.json")));
        Assert.Equal("zh-CN", loc.Language);
        Assert.False(string.IsNullOrEmpty(loc.T("team.err.size", 5)));
        Assert.Contains("5", loc.T("team.err.size", 5));
        Assert.Equal("[no.such.key]", loc.T("no.such.key"));
        var en = Loc.FromJson("{\"language\":\"en\",\"texts\":{\"team.err.size\":\"Need 6, got {0}\"}}", loc);
        Assert.Equal("Need 6, got 5", en.T("team.err.size", 5));
        Assert.Equal(loc.T("describe.at.pre"), en.T("describe.at.pre")); // 缺的键回落到中文
    }

    [Fact]
    public void EveryKeyUsedInCoreExistsInChinese()
    {
        var loc = Loc.FromJson(Res(Path.Combine("Locale", "zh-CN.json")));
        var core = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "UnityProject", "Assets", "StreamDuel", "Core"));
        var used = Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "Loc\\.T\\(\"([a-z0-9_.]+)\"").Cast<Match>().Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        Assert.True(used.Count >= 30, "至少迁移 30 条文字，现在 " + used.Count);
        foreach (var k in used) Assert.True(loc.Has(k), "zh-CN.json 缺少键 " + k);
    }

    [Fact]
    public void TeamCheckMessagesStillChinese()
    {
        var db = BattleTests.LoadPack("original");
        var errs = TeamCheck.Validate(db, "s1", new[] { "kei", "enka" });
        Assert.Contains(errs, e => e.Contains("6"));
        Assert.Contains(errs, e => e.Contains("铁壁老K") && e.Contains("禁用"));
    }
}
