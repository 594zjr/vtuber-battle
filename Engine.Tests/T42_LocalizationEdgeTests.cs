using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

[CollectionDefinition("Localization current", DisableParallelization = true)]
public sealed class T42_LocalizationCollection
{
}

// Current 是进程共享的注入点，此集合必须与其它中文说明断言隔离。
[Collection("Localization current")]
public class T42_LocalizationEdgeTests
{
    static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    static string Resources => Path.Combine(Root, "UnityProject", "Assets", "StreamDuel", "Resources");

    static Loc Table(string language, Dictionary<string, string> texts, Loc fallback = null)
    {
        return Loc.FromJson(JsonSerializer.Serialize(new { language, texts }), fallback);
    }

    [Fact]
    public void FallbackChainsFormatAtTheFoundEntryAndKeepLocalEmptyTexts()
    {
        var chinese = Table("zh-CN", new Dictionary<string, string>
        {
            { "root", "中文 {0} / {1:N1}" }, { "overridden", "原文" }, { "empty", "不能回落到这里" }
        });
        var middle = Table("en", new Dictionary<string, string> { { "middle", "middle {0}" } }, chinese);
        var local = Table("ja", new Dictionary<string, string> { { "overridden", "独立译文" }, { "empty", "" } }, middle);

        Assert.Equal("中文 参数 / 1,234.5", local.T("root", "参数", 1234.5));
        Assert.Equal("middle 7", local.T("middle", 7));
        Assert.Equal("独立译文", local.T("overridden"));
        Assert.Equal("", local.T("empty"));
        Assert.True(local.Has("root"));
        Assert.True(local.Has("middle"));
        Assert.True(local.Has("empty"));
        Assert.False(local.Has("missing"));
        Assert.Equal("[missing]", local.T("missing", 1));
        Assert.False(local.Has("ROOT"));
        Assert.Throws<ArgumentNullException>(() => local.T(null));
        Assert.Throws<ArgumentNullException>(() => local.Has(null));
    }

    [Fact]
    public void FormattingAlwaysUsesInvariantCultureAndSupportsEscapedBraces()
    {
        var loc = Table("zh-CN", new Dictionary<string, string>
        {
            { "number", "{0:N2}|{1:yyyy-MM-dd}|{{字面括号}}" }, { "braces", "{{0}}" }
        });
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("1,234.50|2026-10-15|{字面括号}", loc.T("number", 1234.5, new DateTime(2026, 10, 15)));
            Assert.Equal("{0}", loc.T("braces"));
            Assert.Equal("{0}", loc.T("braces", (object[])null));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void InvalidTemplatesAndMissingArgumentsReportFormatErrors()
    {
        var fallback = Table("zh-CN", new Dictionary<string, string> { { "bad", "未闭合 {" } });
        var loc = Table("en", new Dictionary<string, string> { { "args", "{0}/{1}" } }, fallback);

        Assert.Throws<FormatException>(() => loc.T("bad"));
        Assert.Throws<FormatException>(() => loc.T("args", "only one"));
        Assert.Throws<FormatException>(() => loc.T("args", (object[])null));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"language\":3,\"texts\":{}}")]
    [InlineData("{\"language\":\" \",\"texts\":{}}")]
    [InlineData("{\"language\":\"zh-CN\"}")]
    [InlineData("{\"language\":\"zh-CN\",\"texts\":[]}")]
    [InlineData("{\"language\":\"zh-CN\",\"texts\":{\"key\":null}}")]
    [InlineData("{\"language\":\"zh-CN\",\"texts\":{\"key\":5}}")]
    public void InvalidTextTableShapesAreRejected(string json)
    {
        Assert.Throws<FormatException>(() => Loc.FromJson(json));
    }

    [Fact]
    public void CompiledChineseTableEqualsTheResourceTextAndNullRestoresIt()
    {
        string source = File.ReadAllText(Path.Combine(Resources, "Locale", "zh-CN.json"));
        var field = typeof(Loc).GetField("BuiltinChineseJson", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.True(field.IsLiteral);
        string compiled = (string)field.GetRawConstantValue();
        // Git 可在 Windows 检出 CRLF；除换行编码外不放宽任何文本差异。
        Assert.Equal(source.Replace("\r\n", "\n"), compiled.Replace("\r\n", "\n"));
        var previous = Loc.Current;
        try
        {
            Loc.Current = Table("custom", new Dictionary<string, string> { { "team.err.size", "custom {0}" } });
            Assert.Equal("custom 3", Loc.Current.T("team.err.size", 3));
            Loc.Current = null;
            Assert.Equal("zh-CN", Loc.Current.Language);
            Assert.Equal(Loc.FromJson(source).T("team.err.size", 3), Loc.Current.T("team.err.size", 3));
            Assert.Same(Loc.Current, Loc.Current);
        }
        finally { Loc.Current = previous; }
    }

    [Fact]
    public void CurrentInjectionReachesTeamCheckDescribeAndPackLoader()
    {
        var previous = Loc.Current;
        string fixture = Path.Combine(Root, "Engine.Tests", "obj", "T42InjectedLocale-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(fixture, "broken.json");
        try
        {
            Loc.Current = Table("test", new Dictionary<string, string>
            {
                { "team.err.size", "TEAM[{0}]" },
                { "describe.at.pre", "PRE" }, { "describe.target.self", "ME" },
                { "describe.dispel.persona", "PERMANENT" }, { "describe.field.turn", "TURN" },
                { "pack.err.load", "LOAD[{0}]" }, { "pack.err.json.format", "FORMAT" }
            }, previous);
            var db = BattleTests.LoadPack("original");
            Assert.Equal("TEAM[0]", Assert.Single(TeamCheck.Validate(db, null, new string[0])));
            var effect = Describe.Effect(db, new EffectDef { At = "pre", Code = "STATUS", Target = "self", Status = "flame" });
            Assert.Equal("PRE", effect.At);
            Assert.Contains("ME", effect.Text);
            Assert.Equal("TURN", Describe.FieldKind(new FieldDef { Kind = "turn" }));
            Assert.Equal("PERMANENT", Describe.Persona(db, new PersonaDef { Code = "UNKNOWN" }).Dispel);
            Directory.CreateDirectory(fixture);
            File.WriteAllText(file, "{", new UTF8Encoding(false));
            Assert.Equal("LOAD[FORMAT]", Assert.Single(PackLoader.LoadDirectory(
                File.ReadAllText(Path.Combine(Resources, "StreamDuelRules.json")), fixture)).Error);
        }
        finally
        {
            Loc.Current = previous;
            if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(fixture)) Directory.Delete(fixture);
        }
    }

    [Fact]
    public void MigratedChineseOutputsMatchThePreLocalizationBaselineExactly()
    {
        var previous = Loc.Current;
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            Loc.Current = null;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var rows = T42_ChineseSnapshot.Collect(Resources,
                Path.Combine(Root, "Engine.Tests", "obj", "T42ChineseFixtures-" + Guid.NewGuid().ToString("N")));
            Assert.Equal(467, rows.Count);
            using var sha = SHA256.Create();
            string actual = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rows))));
            // 在 9fb5e12 的独立 Core 源码副本上采集；覆盖所有内置包的效果、人设、赛道及代表错误分支。
            // T-82 用 32f190e 资源复现旧摘要后逐项核对：467 项中仅 14 项含已批准的中性显示名替换。
            Assert.Equal("60A4A611E213D0878A9357738C4E381302A73FA3864B6EB8B2C0B24A8EF12B88", actual);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            Loc.Current = previous;
        }
    }
}
