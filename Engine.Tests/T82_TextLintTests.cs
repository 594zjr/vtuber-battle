using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T82_TextLintTests
{
    static string ResourceRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile), "..", "UnityProject", "Assets", "StreamDuel", "Resources"));
    }

    static ContentDb Clean()
    {
        var skills = new Dictionary<string, object>();
        foreach (string id in new[] { "pk_a", "pk_b", "pk_c", "pk_d", "pk_fifth" })
            skills[id] = new { name = id == "pk_a" ? "拍点" : "节奏", type = "talk", cat = "status", pp = 10 };
        var rules = new
        {
            stats = new Dictionary<string, string> { ["hp"] = "人气", ["atk"] = "互动", ["def"] = "抗压", ["spa"] = "才艺", ["sdef"] = "定力", ["spd"] = "反应" },
            categories = new { status = "状态" },
            types = new { talk = new { name = "杂谈", color = "#ffffff" } },
            chart = new { talk = new { talk = 1 } },
            statuses = new Dictionary<string, object>
            {
                ["pk_status"] = new { name = "迟缓", cat = "weak", turns = 2, cleanse = true, desc = "保持稳定节奏。" }
            },
            fields = new Dictionary<string, object>
            {
                ["pk_field"] = new { name = "护幕", kind = "turn", when = "受到伤害时", dispel = true, desc = "维持防护。" }
            },
            personas = new Dictionary<string, object>
            {
                ["pk_persona"] = new { name = "平稳发挥", code = "T_LOWHP_POWER", node = "造成伤害时", desc = "保持节奏。", @params = new { below = 0, mul = 1 } }
            },
            skills,
            struggle = new { name = "硬撑", type = "talk", cat = "status", pp = 0 }
        };
        var pack = new
        {
            id = "fan_text_test", name = "文本测试包", notice = "虚构角色的测试内容。",
            streamers = Enumerable.Range(0, 6).Select(index => new
            {
                id = "p" + index, name = "主播" + index, type = "talk", persona = "pk_persona", blurb = "以节奏传递欢乐。",
                stats = new { hp = 300, atk = 80, def = 80, spa = 80, sdef = 80, spd = 100 },
                skills = new[] { "pk_a", "pk_b", "pk_c", "pk_d" }, fifth = "pk_fifth"
            }).ToArray()
        };
        return ContentDb.FromJson(JsonSerializer.Serialize(rules), JsonSerializer.Serialize(pack));
    }

    static (string id, string member) SetText(ContentDb db, string field, string text)
    {
        switch (field)
        {
            case "streamer": db.Streamers[0].Name = text; return ("p0", "name");
            case "skill": db.Skills["pk_a"].Name = text; return ("pk_a", "name");
            case "persona": db.Personas["pk_persona"].Name = text; return ("pk_persona", "name");
            case "status": db.Statuses["pk_status"].Name = text; return ("pk_status", "name");
            case "field": db.Fields["pk_field"].Name = text; return ("pk_field", "name");
            case "blurb": db.Streamers[0].Blurb = text; return ("p0", "blurb");
            default: throw new ArgumentException("未知测试字段", nameof(field));
        }
    }

    static LintIssue[] At(ContentDb db, string id, string member)
    {
        var issues = TextLint.Check(db);
        Assert.All(issues, issue =>
        {
            Assert.Contains(issue.Level, new[] { "error", "warn" });
            Assert.False(string.IsNullOrWhiteSpace(issue.Where));
            Assert.False(string.IsNullOrWhiteSpace(issue.Id));
            Assert.False(string.IsNullOrWhiteSpace(issue.Message));
        });
        return issues.Where(issue => issue.Id == id && issue.Where.IndexOf(member, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
    }

    static string Scalars(int count)
    {
        return string.Concat(Enumerable.Range(0, count).Select(index => index % 2 == 0 ? "字" : "🛰"));
    }

    static string Snapshot(IEnumerable<LintIssue> issues)
    {
        return JsonSerializer.Serialize(issues, new JsonSerializerOptions { IncludeFields = true });
    }

    [Fact]
    public void CleanLoadedPackHasNoIssuesAndIndependentProblemsAreReportedAtTheirFields()
    {
        var db = Clean();
        Assert.Empty(TextLint.Check(db));
        db.Streamers[0].Name = "中之人";
        db.Streamers[1].Name = new string('长', 16);
        db.Streamers[2].Blurb = " \t\r\n";

        Assert.Equal("error", Assert.Single(At(db, "p0", "name")).Level);
        Assert.Equal("error", Assert.Single(At(db, "p1", "name")).Level);
        Assert.Equal("error", Assert.Single(At(db, "p2", "blurb")).Level);
    }

    [Fact]
    public void AllBuiltInPacksHaveNoTextErrors()
    {
        string root = ResourceRoot();
        string rules = File.ReadAllText(Path.Combine(root, "StreamDuelRules.json"));
        var packs = Directory.GetFiles(Path.Combine(root, "Packs"), "*.json").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(packs);
        foreach (string path in packs)
        {
            var db = ContentDb.FromJson(rules, File.ReadAllText(path));
            var errors = TextLint.Check(db).Where(issue => issue.Level == "error").ToArray();
            Assert.True(errors.Length == 0, Path.GetFileName(path) + ":\n" +
                string.Join("\n", errors.Select(issue => issue.Where + " [" + issue.Id + "] " + issue.Message)));
        }
    }

    [Fact]
    public void DefaultBannedWordsMatchTheMaintainedResource()
    {
        var words = File.ReadAllLines(Path.Combine(ResourceRoot(), "Lint", "banned.txt"))
            .Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(words);
        Assert.Equal(words, TextLint.DefaultBannedWords);
    }

    [Theory]
    [InlineData("streamer", 10)]
    [InlineData("skill", 6)]
    [InlineData("persona", 8)]
    [InlineData("status", 6)]
    [InlineData("field", 6)]
    [InlineData("blurb", 40)]
    public void LengthUsesUnicodeScalarsAndOnlyExceedingOneAndAHalfTimesIsAnError(string field, int limit)
    {
        var db = Clean();
        var location = SetText(db, field, Scalars(limit));
        Assert.Empty(At(db, location.id, location.member));

        SetText(db, field, Scalars(limit + 1));
        Assert.Equal("warn", Assert.Single(At(db, location.id, location.member)).Level);
        SetText(db, field, Scalars(limit * 3 / 2));
        Assert.Equal("warn", Assert.Single(At(db, location.id, location.member)).Level);
        SetText(db, field, Scalars(limit * 3 / 2 + 1));
        Assert.Equal("error", Assert.Single(At(db, location.id, location.member)).Level);
    }

    [Fact]
    public void CombiningMarksCountAsSeparateScalarsRatherThanGraphemeClusters()
    {
        var db = Clean();
        db.Skills["pk_a"].Name = string.Concat(Enumerable.Repeat("e\u0301", 3));
        Assert.Empty(At(db, "pk_a", "name"));
        db.Skills["pk_a"].Name = string.Concat(Enumerable.Repeat("e\u0301", 4));
        Assert.Equal("warn", Assert.Single(At(db, "pk_a", "name")).Level);
        db.Skills["pk_a"].Name = string.Concat(Enumerable.Repeat("e\u0301", 5));
        Assert.Equal("error", Assert.Single(At(db, "pk_a", "name")).Level);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n\u3000")]
    public void RequiredNamesAndBlurbsRejectNullEmptyOrWhitespace(string text)
    {
        foreach (string field in new[] { "streamer", "skill", "persona", "status", "field", "blurb" })
        {
            var db = Clean();
            var location = SetText(db, field, text);
            Assert.Equal("error", Assert.Single(At(db, location.id, location.member)).Level);
        }
    }

    [Fact]
    public void AbsentOptionalTextIsAllowedButProvidedWhitespaceIsAnError()
    {
        var db = Clean();
        db.Streamers[0].Org = null;
        db.Personas["pk_persona"].Node = null;
        db.Personas["pk_persona"].Desc = null;
        db.Fields["pk_field"].When = null;
        db.Fields["pk_field"].Desc = null;
        db.Statuses["pk_status"].Desc = null;
        Assert.Empty(TextLint.Check(db));

        db.Streamers[0].Org = " \t";
        db.Personas["pk_persona"].Desc = "\n";
        db.Fields["pk_field"].When = " ";
        Assert.Equal("error", Assert.Single(At(db, "p0", "org")).Level);
        Assert.Equal("error", Assert.Single(At(db, "pk_persona", "desc")).Level);
        Assert.Equal("error", Assert.Single(At(db, "pk_field", "when")).Level);
    }

    [Fact]
    public void OrdinaryBannedWordsAreErrorsAndNoticeDoesNotExemptThem()
    {
        foreach (string word in new[] { "中之人", "前世", "转生", "本人", "真名", "黑历史", "炎上", "毕业" })
        {
            var db = Clean();
            db.Streamers[0].Blurb = "提及" + word + "。";
            var issue = Assert.Single(At(db, "p0", "blurb"));
            Assert.Equal("error", issue.Level);
            Assert.Contains(word, issue.Message);
        }
        var noticeDb = Clean();
        noticeDb.PackNotice = "本人说明。";
        Assert.Contains(TextLint.Check(noticeDb), issue => issue.Level == "error" && issue.Where.Contains("notice") && issue.Message.Contains("本人"));
    }

    [Theory]
    [InlineData("官方")]
    [InlineData("授权")]
    public void FanPackOfficialClaimsAreErrorsOutsideNoticeOnly(string word)
    {
        var db = Clean();
        db.PackNotice = "本声明提及" + word + "。";
        Assert.Empty(TextLint.Check(db));
        db.Streamers[0].Blurb = word + "作品。";
        db.PackName = word + "作品";

        Assert.Equal("error", Assert.Single(At(db, "p0", "blurb")).Level);
        Assert.Contains(TextLint.Check(db), issue => issue.Level == "error" && issue.Id == db.PackId && issue.Message.Contains(word));
        db.PackId = "original_text_test";
        Assert.Empty(TextLint.Check(db));
    }

    [Fact]
    public void HalfWidthChinesePunctuationWarnsWithoutFlaggingEnglishOrDecimalPoints()
    {
        foreach (char punctuation in new[] { ',', '.', ':', ';', '!', '?' })
        {
            var db = Clean();
            db.Streamers[0].Blurb = "你好" + punctuation + "继续开播";
            Assert.Equal("warn", Assert.Single(At(db, "p0", "blurb")).Level);
        }
        foreach (string text in new[] { "Hello, world!", "伤害乘以1.5。", "你好，继续开播；保持节奏！", "中文https://example.test。" })
        {
            var db = Clean();
            db.Streamers[0].Blurb = text;
            Assert.Empty(At(db, "p0", "blurb"));
        }
        var mixed = Clean();
        mixed.Streamers[0].Blurb = "中文https://example.test。混用!";
        Assert.Equal("warn", Assert.Single(At(mixed, "p0", "blurb")).Level);
    }

    [Theory]
    [InlineData("持续3 回合。")]
    [InlineData("持续3  回合。")]
    [InlineData("恢复2\t点人气。")]
    public void ExtraWhitespaceBetweenNumbersAndUnitsIsAWarning(string text)
    {
        var db = Clean();
        db.Streamers[0].Blurb = text;
        Assert.Equal("warn", Assert.Single(At(db, "p0", "blurb")).Level);
        db.Streamers[0].Blurb = "持续3回合，恢复2点人气。";
        Assert.Empty(At(db, "p0", "blurb"));
    }

    [Theory]
    [InlineData("使用技能「拍点」维持节奏。", false)]
    [InlineData("使用技能[pk_a]「拍点」维持节奏。", false)]
    [InlineData("使用技能[pk_a]「错名」维持节奏。", true)]
    [InlineData("使用技能[missing]「拍点」维持节奏。", true)]
    [InlineData("使用技能「不存在招式」维持节奏。", true)]
    [InlineData("使用技能「硬撑」维持节奏。", false)]
    [InlineData("获得「护幕」并保持节奏。", false)]
    public void ExplicitSkillReferencesUseCurrentNamesWhileOtherQuotedTextIsNotASkill(string text, bool mismatch)
    {
        var db = Clean();
        db.Streamers[0].Blurb = text;
        var issues = At(db, "p0", "blurb");
        if (mismatch) Assert.Equal("warn", Assert.Single(issues).Level);
        else Assert.Empty(issues);
    }

    [Fact]
    public void CheckIsDeterministicAcrossCulturesAndDoesNotMutateContentOrReuseTheResultList()
    {
        var db = Clean();
        db.Streamers[0].Name = Scalars(11);
        db.Streamers[1].Blurb = "本人,持续3 回合。";
        string contentHash = db.ContentHash();
        var oldCulture = CultureInfo.CurrentCulture;
        var oldUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            var first = TextLint.Check(db);
            Assert.NotEmpty(first);
            string expected = Snapshot(first);
            first.Clear();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");

            Assert.Equal(expected, Snapshot(TextLint.Check(db)));
            Assert.Equal(contentHash, db.ContentHash());
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
            CultureInfo.CurrentUICulture = oldUiCulture;
        }
    }
}
