using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using StreamDuel.Core;
using Xunit;

public class T37_BattleReportEdgeTests
{
    static Battle Make(ContentDb db = null, BattleOptions options = null)
    {
        db = db ?? BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        return new Battle(db, new IList<string>[]
        {
            ids.Take(6).ToArray(), ids.Skip(Math.Max(0, ids.Length - 6)).Take(6).ToArray()
        }, new[] { "甲方测试", "乙方测试" }, options ?? new BattleOptions { Seed = 31 });
    }

    static string Header(string markdown)
    {
        int section = markdown.IndexOf("\n## ", StringComparison.Ordinal);
        return section < 0 ? markdown : markdown.Substring(0, section);
    }

    static string Visible(string markdown)
    {
        return WebUtility.HtmlDecode(Regex.Replace(markdown, @"\\([\\`*_{}\[\]()#+.!|<>-])", "$1"));
    }

    static void AssertInOrder(string text, IEnumerable<string> values)
    {
        int cursor = 0;
        foreach (string value in values)
        {
            int index = text.IndexOf(value, cursor, StringComparison.Ordinal);
            Assert.True(index >= 0, "缺少原文或顺序发生变化：" + value);
            cursor = index + value.Length;
        }
    }

    static string[] FullLog(Battle b)
    {
        return b.Log.Select(l => string.Join("|", l.Turn, l.Node, l.Kind, l.Side, l.Text,
            string.Join(",", l.SnapActive), string.Join(",", l.SnapHp[0]), string.Join(",", l.SnapHp[1]))).ToArray();
    }

    [Fact]
    public void NonContiguousRepeatedNodesAndDuplicateMessagesKeepTheirExactSequence()
    {
        var b = Make();
        b.Log.Clear();
        var entries = new[]
        {
            new LogEntry { Turn = 1, Node = Node.First, Text = "顺序-A：先手开始" },
            new LogEntry { Turn = 1, Node = Node.Second, Text = "顺序-B：后手响应" },
            new LogEntry { Turn = 1, Node = Node.First, Text = "顺序-C：先手补记" },
            new LogEntry { Turn = 1, Node = Node.First, Text = "重复原文必须逐条保留" },
            new LogEntry { Turn = 1, Node = Node.Second, Text = "重复原文必须逐条保留" },
            new LogEntry { Turn = 2, Node = Node.Replace, Text = "顺序-D：补位" },
            new LogEntry { Turn = 2, Node = Node.First, Text = "顺序-E：行动" },
            new LogEntry { Turn = 2, Node = Node.Replace, Text = "顺序-F：再次补位" }
        };
        b.Log.AddRange(entries);

        string report = BattleReport.Markdown(b);

        AssertInOrder(report, entries.Select(e => e.Text));
        Assert.Equal(2, Regex.Matches(report, Regex.Escape("重复原文必须逐条保留")).Count);
        AssertInOrder(report, new[] { "## 第 1 回合", entries[0].Text, "## 第 2 回合", entries[5].Text });
        Assert.Contains(Node.First, report);
        Assert.Contains(Node.Second, report);
        Assert.Contains(Node.Replace, report);
    }

    [Fact]
    public void DrawIsExplicitInTheHeader()
    {
        var b = Make();
        b.Phase = Phase.Over;
        b.Winner = -1;

        string header = Header(BattleReport.Markdown(b));

        Assert.Contains("平局", header);
        Assert.Contains(b.Db.PackName, header);
        Assert.All(b.Sides, side => Assert.Contains(side.Name, header));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void UnfinishedBattlePhasesHaveAnExplicitUnfinishedResult(bool ban, bool lead)
    {
        var b = Make(options: new BattleOptions { Seed = 31, BanOne = ban, LeadPick = lead });

        string header = Header(BattleReport.Markdown(b));

        Assert.Contains("未结束", header);
        Assert.DoesNotContain("平局", header);
        Assert.DoesNotContain("获胜", header);
    }

    [Fact]
    public void FanNoticeIsPreservedVerbatimIncludingMarkdownAndLineBreaks()
    {
        var db = BattleTests.LoadPack("fan_asoul_vr");
        string notice = db.PackNotice + "\n附注：*原样* | [说明] <文字> `引用`\n第二行保留。";
        db.PackNotice = notice;
        var b = Make(db);

        string report = BattleReport.Markdown(b);

        Assert.Contains(notice, report);
        Assert.Contains("非官方同人作品", report);
        Assert.True(report.IndexOf(notice, StringComparison.Ordinal) < report.IndexOf("## 第 1 回合", StringComparison.Ordinal));
    }

    [Fact]
    public void HeaderShowsThePostBanOpeningRosterEvenAfterBothSidesSwitch()
    {
        var b = Make(options: new BattleOptions { Seed = 31, BanOne = true, LeadPick = true });
        string banned0 = b.Sides[0].Team[0].Name;
        string banned1 = b.Sides[1].Team[2].Name;
        b.ChooseBan(0, 2);
        b.ChooseBan(1, 0);
        b.ChooseLead(0, 4);
        b.ChooseLead(1, 2);
        var rosters = b.Sides.Select(s => s.Team.Select(f => f.Name).ToArray()).ToArray();
        b.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.SwitchTo(1));

        string header = Visible(Header(BattleReport.Markdown(b)));

        Assert.DoesNotContain(banned0, header);
        Assert.DoesNotContain(banned1, header);
        for (int side = 0; side < 2; side++)
        {
            Assert.NotEqual(rosters[side][0], b.Active(side).Name);
            AssertInOrder(header, rosters[side]);
            Assert.Contains(header.Split('\n'), line => line.Contains("首发") && line.Contains(rosters[side][0]));
        }
    }

    static Battle BattleWithStats()
    {
        var db = BattleTests.LoadPack("original");
        db.Skills["report_fixed"] = new SkillDef
        {
            Id = "report_fixed", Name = "战报固定伤害", Type = "game", Cat = "status", Pp = 10, Prio = 5,
            Effects = { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 77 } }
        };
        db.Streamers[0].Skills[0] = "report_fixed";
        var b = Make(db);
        b.Active(1).Hp = 100;
        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.Equal(Phase.Replace, b.Phase);
        b.Replace(1, 1);
        return b;
    }

    [Fact]
    public void ReportIsDeterministicCultureIndependentAndDoesNotChangeAnyBattleState()
    {
        var b = BattleWithStats();
        var control = BattleWithStats();
        ulong state = b.StateHash();
        string record = b.Record.ToJson();
        string summary = BattleSummary.From(b).ToText();
        var logs = FullLog(b);
        string expected = BattleReport.Markdown(b);
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            foreach (string name in new[] { "tr-TR", "de-DE", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                Assert.Equal(expected, BattleReport.Markdown(b));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }

        Assert.Equal(expected, BattleReport.Markdown(control));
        Assert.Equal(state, b.StateHash());
        Assert.Equal(record, b.Record.ToJson());
        Assert.Equal(summary, BattleSummary.From(b).ToText());
        Assert.Equal(logs, FullLog(b));
        Assert.Equal(control.Random(), b.Random());
    }

    static string[] Cells(string row)
    {
        return Regex.Split(row.Trim(), @"(?<!\\)\|").Skip(1).Reverse().Skip(1).Reverse().Select(c => c.Trim()).ToArray();
    }

    [Fact]
    public void TrailingTableContainsEachFightersActualSummaryCounters()
    {
        var b = BattleWithStats();
        var summary = BattleSummary.From(b);
        string report = BattleReport.Markdown(b);
        int lastLog = report.LastIndexOf(b.Log.Last().Text, StringComparison.Ordinal);
        string tail = report.Substring(lastLog + b.Log.Last().Text.Length);
        var rows = tail.Split('\n').Where(l => l.TrimStart().StartsWith("|", StringComparison.Ordinal)).Select(Cells).ToArray();
        var headers = rows.Where(row => row.Contains("造成伤害") && row.Contains("承受伤害")).ToArray();
        Assert.Equal(2, headers.Length);
        Assert.Equal(headers[0], headers[1]);
        var columns = headers[0];
        int dealt = Array.IndexOf(columns, "造成伤害");
        int taken = Array.IndexOf(columns, "承受伤害");
        int kos = Array.IndexOf(columns, "击倒");
        int turns = Array.IndexOf(columns, "在场回合");
        Assert.True(kos >= 0 && turns >= 0);
        for (int side = 0; side < 2; side++)
        {
            for (int index = 0; index < summary.Sides[side].Count; index++)
            {
                var expected = summary.Sides[side][index];
                string fighter = b.Sides[side].Team[index].Name;
                var row = Assert.Single(rows, cells => cells.Contains(fighter));
                Assert.Equal(columns.Length, row.Length);
                Assert.Equal(expected.DamageDealt.ToString(CultureInfo.InvariantCulture), row[dealt]);
                Assert.Equal(expected.DamageTaken.ToString(CultureInfo.InvariantCulture), row[taken]);
                Assert.Equal(expected.Kos.ToString(CultureInfo.InvariantCulture), row[kos]);
                Assert.Equal(expected.TurnsActive.ToString(CultureInfo.InvariantCulture), row[turns]);
                Assert.Contains(expected.Fainted ? "已下播" : "在播", row);
            }
        }
        Assert.Contains("战报固定伤害", tail);
    }

    [Fact]
    public void MarkdownSymbolsStayReadableAndLogFencesCannotBeClosedByTheLogText()
    {
        var b = BattleWithStats();
        b.Db.PackName = "测试|包 *星* [题] <名>";
        b.Sides[0].Name = "甲|队 [标签] <名字>";
        b.Sides[0].Team[0].Def.Name = "主播|名 *星* [角色] <形象>";
        string raw = "原文开始 | *强调* [链接] <内容> `引用`\n``````\n## 原文中的标题\n原文结束";
        b.Log.Add(new LogEntry { Turn = b.Turn, Node = Node.Second, Text = raw });

        string report = BattleReport.Markdown(b).Replace("\r\n", "\n");

        Assert.Contains(raw, report);
        string visibleHeader = Visible(Header(report));
        Assert.Contains(b.Db.PackName, visibleHeader);
        Assert.Contains(b.Sides[0].Name, visibleHeader);
        Assert.Contains(b.Sides[0].Team[0].Name, visibleHeader);
        var fence = Regex.Match(report, @"(?m)^(`{7,})[^`\n]*$");
        Assert.True(fence.Success, "日志中的六个反引号不能关闭承载日志原文的围栏");
        int rawAt = report.IndexOf(raw, StringComparison.Ordinal);
        int close = report.IndexOf("\n" + fence.Groups[1].Value + "\n", fence.Index + fence.Length, StringComparison.Ordinal);
        Assert.True(rawAt > fence.Index && close >= rawAt + raw.Length);
        var tableRows = report.Substring(close).Split('\n').Where(l => l.TrimStart().StartsWith("|", StringComparison.Ordinal)).Select(Cells).ToArray();
        var nameRow = Assert.Single(tableRows, row => row.Any(cell => Visible(cell) == b.Sides[0].Team[0].Name));
        Assert.Equal(tableRows[0].Length, nameRow.Length);
    }
}
