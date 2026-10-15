using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T98_TextDisplayReviewTests
{
    [Theory]
    [InlineData("different", false)]
    [InlineData("different", true)]
    [InlineData("same", false)]
    [InlineData("same", true)]
    [InlineData("empty", false)]
    [InlineData("empty", true)]
    [InlineData("prefix", false)]
    [InlineData("prefix", true)]
    public void TerminalTemplatesSanitizeOnlyDisplayAndPreserveRawReplayAndState(string namesCase, bool surrender)
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        string[] rawNames = Names(namesCase);
        var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            rawNames, new BattleOptions { Seed = 98, BanOne = true, LeadPick = true });
        int firstTerminalLog = battle.Log.Count;
        string[] safe = rawNames.Select(name => TextInput.Clean(name, TextInputKind.PlayerName).Text).ToArray();
        string[] expected;
        if (surrender)
        {
            battle.Surrender(0);
            expected = new[] { safe[0] + " 投降，" + safe[1] + " 获胜" };
        }
        else
        {
            battle.OfferDraw(0);
            battle.RespondDraw(1, false);
            battle.OfferDraw(1);
            battle.RespondDraw(0, true);
            expected = new[]
            {
                safe[0] + " 提议和局", safe[1] + " 拒绝和局，继续对战",
                safe[1] + " 提议和局", safe[0] + " 同意和局，双方平局"
            };
        }
        Assert.Equal(3, battle.Record.Version);
        Assert.Equal(Phase.Over, battle.Phase);
        var before = battle.Clone();
        string rawRecord = battle.Record.ToJson();
        string[] rawLog = Logs(battle);
        ulong rawHash = battle.StateHash();
        string contentHash = db.ContentHash();
        var display = TextInput.ForDisplay(battle);

        Assert.True(expected.SequenceEqual(display.Log.Skip(firstTerminalLog).Select(entry => entry.Text)),
            "新终局模板必须使用清理后的昵称，且只改模板中的昵称位置。");
        Assert.Equal(safe, display.Sides.Select(side => side.Name));
        Assert.Equal(safe, display.Record.Names);
        Assert.Equal(battle.Log.Count, display.Log.Count);
        for (int i = 0; i < battle.Log.Count; i++)
        {
            var source = battle.Log[i];
            var shown = display.Log[i];
            Assert.Equal(source.Turn, shown.Turn);
            Assert.Equal(source.Node, shown.Node);
            Assert.Equal(source.Kind, shown.Kind);
            Assert.Equal(source.Side, shown.Side);
            Assert.Equal(source.SnapActive, shown.SnapActive);
            Assert.Equal(source.SnapHp[0], shown.SnapHp[0]);
            Assert.Equal(source.SnapHp[1], shown.SnapHp[1]);
        }
        AssertSafe(string.Join("\n", display.Log.Select(entry => entry.Text)));
        AssertSafe(BattleReport.Markdown(display, 0));
        Assert.True(rawRecord == battle.Record.ToJson(), "显示净化不得改写原始录像。");
        Assert.True(rawNames.SequenceEqual(battle.Sides.Select(side => side.Name)), "原对战名称不得改变。");
        Assert.True(rawLog.SequenceEqual(Logs(battle)), "原日志必须保持逐项不变。");
        Assert.Equal(rawHash, battle.StateHash());
        Assert.Equal(contentHash, db.ContentHash());
        SameRandom(before, battle);

        var decoded = ShareCode.Decode(ShareCode.Encode(battle.Record));
        Assert.True(rawRecord == decoded.ToJson(), "分享码必须保存完整原始记录。");
        var replay = Replay.Run(db, decoded);
        Assert.Equal(rawHash, replay.StateHash());
        Assert.True(rawLog.SequenceEqual(Logs(replay)), "重放原日志不得提前净化。");
        Assert.True(rawRecord == replay.Record.ToJson(), "重放必须保留终止响应和原始名称。");
        AssertSafe(string.Join("\n", TextInput.ForDisplay(replay).Log.Select(entry => entry.Text)));
        var cursor = new ReplayCursor(db, decoded, 1);
        cursor.ToEnd();
        Assert.Equal(rawHash, cursor.Current.StateHash());
        Assert.True(rawRecord == cursor.Current.Record.ToJson(), "回看游标必须保留原终止记录。");
        SameRandom(battle, replay);
        SameRandom(battle, cursor.Current);
    }

    [Fact]
    public void MatchingWordsInOtherLogKindsRemainUntouchedAndDoubleSurrenderHasNoNameReplacement()
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "和局\u200b", "\u202e投降" }, 98);
        // 独立验证显示器按日志类型和节点匹配，不能全局替换同名片段或规则文字。
        battle.Log[0].Text = battle.Sides[0].Name + " 提议和局";
        battle.Log[0].Kind = "skill";
        battle.Log[0].Side = 0;
        string ruleText = battle.Log[0].Text;
        battle.ResolveSurrenders(true, true);
        var display = TextInput.ForDisplay(battle);
        Assert.True(ruleText == display.Log[0].Text, "非终止模板的规则文字不应按昵称替换。");
        Assert.Equal("双方同时投降，平局", display.Log.Last().Text);
        Assert.Equal(-1, display.Winner);
    }

    static string[] Names(string scenario)
    {
        string word = TextLint.DefaultBannedWords[0];
        if (scenario == "different") return new[] { "\u001b甲" + word + "\u200b\n", "乙\u202e\0名" };
        if (scenario == "same") return new[] { "\u200b" + word + "\u001b", "\u200b" + word + "\u001b" };
        if (scenario == "empty") return new[] { "", " \u200b\n" };
        return new[] { "\u200b同意和局", "\u202e同意和局的对手" };
    }

    static void AssertSafe(string text)
    {
        foreach (char hidden in new[] { '\0', '\u001b', '\u200b', '\u202e' })
            Assert.False(text.IndexOf(hidden) >= 0, "显示结果残留控制或不可见字符。");
        Assert.False(text.Contains(TextLint.DefaultBannedWords[0]), "显示结果残留词表原词。");
    }

    static string[] Logs(Battle battle)
    {
        return battle.Log.Select(entry => JsonSerializer.Serialize(new
        {
            entry.Turn, entry.Node, entry.Kind, entry.Side, entry.Text, entry.SnapActive, entry.SnapHp
        })).ToArray();
    }

    static void SameRandom(Battle left, Battle right)
    {
        var first = left.Clone();
        var second = right.Clone();
        for (int i = 0; i < 8; i++) Assert.Equal(first.Random(), second.Random());
    }
}
