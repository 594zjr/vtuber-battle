using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamDuel.Core;
using Xunit;

[Collection("Localization current")]
public class T74_ReviewIntegrationTests
{
    static Battle Make(bool markup = false)
    {
        var db = BattleTests.LoadPack("original");
        if (markup)
        {
            for (int index = 0; index < db.Streamers.Count; index++)
                db.Streamers[index].Name = "主播|" + index + " *星* [角色](url) <形象>\n## 假标题";
        }
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        return new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            markup ? new[] { "甲*方*\n## 假视角", "乙|方 <名字>" } : new[] { "甲方玩家", "乙方玩家" }, 74);
    }

    static Battle Finish(Battle battle)
    {
        int guard = 0;
        while (battle.Phase != Phase.Over && guard++ < 500)
        {
            if (battle.Phase == Phase.Choose) battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
            else for (int side = 0; side < 2; side++)
                if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
        }
        Assert.Equal(Phase.Over, battle.Phase);
        return battle;
    }

    static string ReviewSection(string markdown, string heading = "关键回合")
    {
        string marker = "## " + heading + "\n\n";
        int index = markdown.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "战报缺少末尾关键回合节");
        return markdown.Substring(index);
    }

    static string Visible(string markdown)
    {
        return Regex.Replace(markdown, @"\\([\\`*_{}\[\]()#+.!|<>&-])", "$1");
    }

    static string OneLine(string text)
    {
        return text.Replace('\r', ' ').Replace('\n', ' ');
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ReportAppendsTheSelectedViewersMomentsAfterStatistics(int viewerSide)
    {
        var battle = Finish(Make());
        var moments = BattleReview.KeyMoments(battle, viewerSide);
        Assert.NotEmpty(moments);
        string report = BattleReport.Markdown(battle, viewerSide);
        string section = ReviewSection(report);
        var rows = section.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal)).ToArray();

        Assert.Contains(Loc.Current.T("review.perspective", battle.Sides[viewerSide].Name), section);
        Assert.Equal(moments.Select(moment => OneLine(moment.Text)), rows.Select(line => Visible(line.Substring(2))));
        Assert.InRange(rows.Length, 1, 5);
        Assert.True(report.IndexOf("## 对战统计", StringComparison.Ordinal) < report.LastIndexOf("## 关键回合", StringComparison.Ordinal));
        Assert.DoesNotContain("\n## ", section.Substring(3));
        Assert.Equal(BattleReport.Markdown(battle, 0), BattleReport.Markdown(battle));
    }

    [Fact]
    public void ReplayProducesTheSameReportReviewWithoutChangingTheBattle()
    {
        var battle = Finish(Make());
        ulong hash = battle.StateHash();
        string record = battle.Record.ToJson();
        var replayed = Replay.Run(battle.Db, BattleRecord.FromJson(record));
        string original = BattleReport.Markdown(battle);

        Assert.Equal(original, BattleReport.Markdown(replayed));
        Assert.Equal(original, BattleReport.Markdown(battle));
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(record, battle.Record.ToJson());
    }

    [Fact]
    public void ReviewNamesAndLinesCannotInjectMarkdownSectionsOrHtml()
    {
        var battle = Finish(Make(markup: true));
        string section = ReviewSection(BattleReport.Markdown(battle));
        var moments = BattleReview.KeyMoments(battle, 0);
        Assert.NotEmpty(moments);

        Assert.Contains("甲\\*方\\* \\#\\# 假视角", section);
        Assert.DoesNotContain("\n## 假标题", section);
        Assert.DoesNotContain("\n## 假视角", section);
        Assert.DoesNotContain("<形象>", section);
        Assert.DoesNotContain("[角色](url)", section);
        Assert.Equal(moments.Select(moment => OneLine(moment.Text)), section.Split('\n')
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal)).Select(line => Visible(line.Substring(2))));
    }

    [Fact]
    public void UnfinishedReportsExplainWhyPostBattleReviewIsUnavailable()
    {
        var battle = Make();
        battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
        Assert.NotEqual(Phase.Over, battle.Phase);
        string section = ReviewSection(BattleReport.Markdown(battle));

        Assert.Contains(Loc.Current.T("review.unfinished"), section);
        Assert.DoesNotContain("- ", section);
        Assert.DoesNotContain(Loc.Current.T("review.empty"), section);
    }

    [Fact]
    public void EmptyCompletedHistoryHasAnExplicitEmptyReview()
    {
        var battle = Make();
        battle.Phase = Phase.Over;
        battle.Winner = -1;
        battle.Log.Clear();
        string section = ReviewSection(BattleReport.Markdown(battle));

        Assert.Contains(Loc.Current.T("review.empty"), section);
        Assert.DoesNotContain(Loc.Current.T("review.unfinished"), section);
    }

    [Fact]
    public void ReviewHeadingPerspectiveAndPlaceholdersUseCurrentLocale()
    {
        var previous = Loc.Current;
        try
        {
            var texts = new Dictionary<string, string>
            {
                { "review.heading", "Review" }, { "review.perspective", "Viewer: {0}" },
                { "review.empty", "No moments." }, { "review.unfinished", "Not finished." }
            };
            Loc.Current = Loc.FromJson(JsonSerializer.Serialize(new { language = "test", texts }), previous);
            var battle = Make();
            Assert.Contains("Not finished.", Visible(ReviewSection(BattleReport.Markdown(battle), "Review")));
            battle.Phase = Phase.Over;
            battle.Winner = -1;
            battle.Log.Clear();
            string section = ReviewSection(BattleReport.Markdown(battle, 1), "Review");

            Assert.Contains("Viewer: 乙方玩家", section);
            Assert.Contains("No moments.", Visible(section));
            Assert.DoesNotContain("关键回合", section);
        }
        finally { Loc.Current = previous; }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void ExplicitReportPerspectiveRejectsInvalidSides(int viewerSide)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BattleReport.Markdown(Make(), viewerSide));
    }
}
