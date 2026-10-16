using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T97_TextInputTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    static void Unchanged(string expected, TextInputResult result)
    {
        Assert.Equal(expected, result.Text);
        Assert.False(result.Changed);
        Assert.False(result.UsedDefault);
        Assert.True(string.IsNullOrEmpty(result.Message));
    }

    static void Changed(string expected, TextInputResult result, TextInputKind kind, bool usedDefault = false)
    {
        Assert.Equal(expected, result.Text);
        Assert.True(result.Changed);
        Assert.Equal(usedDefault, result.UsedDefault);
        Assert.Matches("[\u3400-\u9fff]", result.Message ?? "");
        Assert.All(TextLint.DefaultBannedWords, word => Assert.DoesNotContain(word, result.Message));
        Unchanged(result.Text, TextInput.Clean(result.Text, kind));
    }

    [Theory]
    [InlineData(TextInputKind.PlayerName, "玩家")]
    [InlineData(TextInputKind.TeamName, "我的队伍")]
    [InlineData(TextInputKind.RoomName, "对战房间")]
    [InlineData(TextInputKind.Note, "无备注")]
    public void EmptyOrRemovedInputUsesTheKindDefaultAndBecomesStable(TextInputKind kind, string expected)
    {
        foreach (string raw in new[] { null, "", " \u3000\u00a0 ", "\r\n\t\0\u200b\u2066\ufeff", "\ud800" })
            Changed(expected, TextInput.Clean(raw, kind), kind, usedDefault: true);
        Unchanged(expected, TextInput.Clean(expected, kind));
    }

    [Theory]
    [InlineData(TextInputKind.PlayerName, 12)]
    [InlineData(TextInputKind.TeamName, 16)]
    [InlineData(TextInputKind.RoomName, 24)]
    [InlineData(TextInputKind.Note, 60)]
    public void LimitsCountUnicodeScalarsAndNeverLeaveHalfOfAnEmoji(TextInputKind kind, int limit)
    {
        string exact = string.Concat(Enumerable.Repeat("🦊", limit));
        Unchanged(exact, TextInput.Clean(exact, kind));
        Changed(exact, TextInput.Clean(exact + "尾", kind), kind);
        string boundary = new string('中', limit - 1) + "🦊";
        Changed(boundary, TextInput.Clean(boundary + "尾", kind), kind);
        Changed(new string('中', limit), TextInput.Clean(new string('中', limit) + "🦊", kind), kind);
        Changed(new string('中', limit - 1), TextInput.Clean(new string('中', limit - 1) + " 尾", kind), kind);
    }

    [Theory]
    [InlineData("小主播_01")]
    [InlineData("Alice-42")]
    [InlineData("123456789012")]
    [InlineData("A+B/@.#!?")]
    [InlineData("[队]<名>＆（）")]
    [InlineData("星⭐🦊🛰️")]
    [InlineData("é e\u0301 中")]
    [InlineData("ＡＢＣ１２３")]
    public void OrdinaryNamesAndSymbolsKeepTheirOriginalSpelling(string raw)
    {
        Unchanged(raw, TextInput.Clean(raw, TextInputKind.PlayerName));
    }

    [Fact]
    public void ControlsAndAllLineSeparatorsAreDeletedInsteadOfAddingSpaces()
    {
        string controls = new string(Enumerable.Range(0, 32).Concat(Enumerable.Range(127, 33)).Select(n => (char)n).ToArray());
        Changed("甲乙", TextInput.Clean("甲" + controls + "\u2028\u2029乙", TextInputKind.PlayerName), TextInputKind.PlayerName);
        Changed("ABCD", TextInput.Clean("A\tB\r\nC\u0085D", TextInputKind.PlayerName), TextInputKind.PlayerName);
    }

    [Fact]
    public void FormatCharactersIncludingSupplementaryTagsCannotHideText()
    {
        string formats = "\u00ad\u061c\u180e\u200b\u200c\u200d\u200e\u200f"
            + "\u202a\u202b\u202c\u202d\u202e\u2060\u2061\u2062\u2063\u2064\u2066\u2067\u2068\u2069\ufeff"
            + "\U000e0001\U000e0020\U000e007f";
        Changed("甲乙", TextInput.Clean("甲" + formats + "乙", TextInputKind.PlayerName), TextInputKind.PlayerName);
    }

    [Fact]
    public void SpaceSeparatorsBecomeOneAsciiSpaceAndAreTrimmed()
    {
        string spaces = " \u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u202f\u205f\u3000";
        Changed("甲 乙", TextInput.Clean(spaces + "甲" + spaces + "乙" + spaces, TextInputKind.PlayerName), TextInputKind.PlayerName);
        Unchanged("甲 乙", TextInput.Clean("甲 乙", TextInputKind.PlayerName));
    }

    [Fact]
    public void UnpairedSurrogatesAreDroppedWhileAdjacentValidPairsSurvive()
    {
        string raw = "\ud800甲\udc00🦊乙\ud800\ud800\udc00";
        Changed("甲🦊乙\U00010000", TextInput.Clean(raw, TextInputKind.PlayerName), TextInputKind.PlayerName);
        Changed("甲乙", TextInput.Clean("甲\udfff\ud800乙", TextInputKind.PlayerName), TextInputKind.PlayerName);
    }

    [Fact]
    public void JoinersAreRemovedButVariationSelectorsAndCombiningMarksArePreserved()
    {
        Changed("👩💻❤️ e\u0301", TextInput.Clean("👩\u200d💻❤️ e\u0301", TextInputKind.PlayerName), TextInputKind.PlayerName);
        string boundary = new string('甲', 10) + "e\u0301";
        Unchanged(boundary, TextInput.Clean(boundary, TextInputKind.PlayerName));
        Changed(boundary, TextInput.Clean(boundary + "尾", TextInputKind.PlayerName), TextInputKind.PlayerName);
        Unchanged("木\U000e0100", TextInput.Clean("木\U000e0100", TextInputKind.PlayerName));
    }

    [Fact]
    public void EverySharedBannedWordIsMaskedAndMatchesTheResourceList()
    {
        string path = BannedWordsPath();
        string[] fromFile = File.ReadAllLines(path).Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).OrderBy(word => word, StringComparer.Ordinal).ToArray();
        Assert.Equal(fromFile, TextLint.DefaultBannedWords.OrderBy(word => word, StringComparer.Ordinal).ToArray());
        foreach (string word in fromFile)
        {
            string stars = new string('*', word.EnumerateRunes().Count());
            Changed("前" + stars + "后" + stars,
                TextInput.Clean("前" + word + "后" + word, TextInputKind.Note), TextInputKind.Note);
        }
    }

    static string BannedWordsPath([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile), "..", "UnityProject", "Assets",
            "StreamDuel", "Resources", "Lint", "banned.txt"));
    }

    [Fact]
    public void WordsAreMatchedAfterRemovingHiddenCharactersAndBeforeTruncation()
    {
        Changed("***", TextInput.Clean("中\u200b之\n人", TextInputKind.PlayerName), TextInputKind.PlayerName);
        string prefix = new string('甲', 11);
        Changed(prefix + "*", TextInput.Clean(prefix + "中之人", TextInputKind.PlayerName), TextInputKind.PlayerName);
        Changed("****", TextInput.Clean("毕业毕业", TextInputKind.PlayerName), TextInputKind.PlayerName);
    }

    [Fact]
    public void CleaningIsIndependentOfCurrentCultureAndNeverEchoesRemovedInput()
    {
        var oldCulture = CultureInfo.CurrentCulture;
        var oldUiCulture = CultureInfo.CurrentUICulture;
        const string raw = " \u200f中之人\0 İ😀\r\n \u3000";
        try
        {
            string baseline = null;
            foreach (string culture in new[] { "zh-CN", "en-US", "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                var result = TextInput.Clean(raw, TextInputKind.Note);
                Changed("*** İ😀", result, TextInputKind.Note);
                Assert.DoesNotContain(raw, result.Message);
                string actual = Json(new { result.Text, result.Message, result.Changed, result.UsedDefault });
                if (baseline == null) baseline = actual;
                else Assert.Equal(baseline, actual);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
            CultureInfo.CurrentUICulture = oldUiCulture;
        }
    }

    static Battle BattleWithNames(string first, string second)
    {
        var db = BattleTests.LoadPack("original");
        var battle = new Battle(db, new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { first, second }, 97);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        return battle;
    }

    static string SourceSnapshot(Battle battle)
    {
        return Json(new
        {
            battle.Phase, battle.Winner, battle.Turn, battle.Sides, battle.Need,
            Hash = battle.StateHash(), Record = battle.Record.ToJson(), battle.Log, battle.Events,
            NextRandom = battle.Clone().Random()
        });
    }

    [Fact]
    public void DisplayBattleCleansNamesAndLogReferencesWithoutChangingTheSourceOrRandomStream()
    {
        const string first = "甲\n";
        const string second = "甲\n中之人";
        var battle = BattleWithNames(first, second);
        string before = SourceSnapshot(battle);
        var display = TextInput.ForDisplay(battle);
        Assert.NotSame(battle, display);
        Assert.Equal(new[] { "甲", "甲***" }, display.Sides.Select(side => side.Name));
        Assert.Equal(new[] { "甲", "甲***" }, display.Record.Names);
        Assert.Equal(battle.StateHash(), display.StateHash());
        Assert.Equal(battle.Clone().Random(), display.Clone().Random());
        Assert.Equal(Json(battle.Events), Json(display.Events));
        Assert.Equal(Json(battle.Record.Events), Json(display.Record.Events));
        Assert.Equal(battle.Record.Seed, display.Record.Seed);
        Assert.Equal(battle.Record.ContentHash, display.Record.ContentHash);
        Assert.Equal(battle.Log.Count, display.Log.Count);
        for (int index = 0; index < battle.Log.Count; index++)
        {
            var original = battle.Log[index];
            var clean = display.Log[index];
            Assert.Equal(original.Text.Replace(second, "甲***").Replace(first, "甲"), clean.Text);
            Assert.Equal(Json(new { original.Turn, original.Node, original.Side, original.Kind, original.SnapActive, original.SnapHp }),
                Json(new { clean.Turn, clean.Node, clean.Side, clean.Kind, clean.SnapActive, clean.SnapHp }));
            Assert.DoesNotContain(first, clean.Text);
            Assert.DoesNotContain(second, clean.Text);
        }
        Assert.Equal(before, SourceSnapshot(battle));
        display.Sides[0].Name = "改副本";
        display.Active(0).Hp = 1;
        display.Active(0).Stages["atk"] = 6;
        display.Log[0].Text = "改副本日志";
        display.Log[0].SnapHp[0][0] = 1;
        display.Record.Names[0] = "改副本录像";
        display.Record.Events.Clear();
        display.Events[0].Amount = 99;
        display.Random();
        Assert.Equal(before, SourceSnapshot(battle));
    }

    [Fact]
    public void ViewModelsFilterIncomingPlayerNamesWithoutEditingTheBattle()
    {
        string first = new string('甲', 11) + "🦊\u200b尾";
        var battle = BattleWithNames(first, " \u3000\r\n\u200b ");
        string before = SourceSnapshot(battle);
        foreach (int side in new[] { 0, 1 })
        {
            var view = BattleViewModel.From(battle, side);
            Assert.Equal(new string('甲', 11) + "🦊", view.Sides[0].Name);
            Assert.Equal("玩家", view.Sides[1].Name);
            Assert.Equal(battle.Sides[side].Team.Select(f => f.Name), view.Sides[side].Team.Select(f => f.Name));
            view.Sides[0].Name = "改显示副本";
        }
        Assert.Equal(before, SourceSnapshot(battle));
    }

    [Fact]
    public void DisplayLogKeepsOrdinarySpacesAndFighterNamesWhenTheyMatchDirtyPlayerNames()
    {
        string fighterName = BattleTests.LoadPack("original").Streamer("bell").Name;
        var battle = BattleWithNames(fighterName + " ", " ");
        string before = SourceSnapshot(battle);
        var display = TextInput.ForDisplay(battle);
        Assert.Equal(new[] { fighterName, "玩家" }, display.Sides.Select(side => side.Name));
        for (int index = 0; index < battle.Log.Count; index++)
        {
            var original = battle.Log[index];
            string expected = original.Text;
            if (original.Kind == "enter")
            {
                string prefix = battle.Sides[original.Side].Name + "：";
                Assert.StartsWith(prefix, expected);
                expected = display.Sides[original.Side].Name + "：" + expected.Substring(prefix.Length);
            }
            Assert.Equal(expected, display.Log[index].Text);
        }
        Assert.Contains(display.Log, entry => entry.Text == "第 1 回合");
        Assert.Contains(display.Log, entry => entry.Text == fighterName + " 放弃了行动");
        Assert.Equal(before, SourceSnapshot(battle));
    }

    [Fact]
    public void BanLeadAndVictoryLinesCleanBothPlayersWithoutChangingBattleHistory()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["t97_neutral"] = new PersonaDef { Id = "t97_neutral", Name = "测试人设", Code = "T_LOWHP_POWER" };
        db.Skills["t97_finish"] = new SkillDef
        {
            Id = "t97_finish", Name = "测试下播", Type = "talk", Cat = "status", Pp = 20, Prio = 1,
            Effects = new List<EffectDef> { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 99999 } }
        };
        foreach (var fighter in db.Streamers)
        {
            fighter.Persona = "t97_neutral";
            fighter.Skills = Enumerable.Repeat("t97_finish", 4).ToList();
            fighter.Fifth = "t97_finish";
        }
        var battle = new Battle(db, new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "甲\n中之人", " " }, new BattleOptions { Seed = 97, BanOne = true, LeadPick = true });
        battle.ChooseBan(0, 5);
        battle.ChooseBan(1, 5);
        battle.ChooseLead(0, 1);
        battle.ChooseLead(1, 1);
        int guard = 30;
        while (battle.Phase != Phase.Over && guard-- > 0)
        {
            if (battle.Phase == Phase.Choose) battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            else for (int side = 0; side < 2; side++)
                if (battle.Need[side]) battle.Replace(side, battle.Sides[side].Team.FindIndex(fighter => !fighter.Fainted));
        }
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(0, battle.Winner);
        string before = SourceSnapshot(battle);
        var display = TextInput.ForDisplay(battle);
        Assert.Equal(new[]
        {
            "甲*** 禁用了 玩家 的「" + db.Streamer("bee").Name + "」",
            "玩家 禁用了 甲*** 的「" + db.Streamer("momo").Name + "」"
        }, display.Log.Where(entry => entry.Kind == "ban").Select(entry => entry.Text));
        Assert.Equal(new[]
        {
            "甲*** 选择「" + db.Streamer("enka").Name + "」首发，移至 1 号位",
            "玩家 选择「" + db.Streamer("yuan").Name + "」首发，移至 1 号位"
        }, display.Log.Where(entry => entry.Kind == "lead").Select(entry => entry.Text));
        Assert.Equal("甲*** 获胜", display.Log.Last().Text);
        Assert.Equal(Node.Over, display.Log.Last().Node);
        for (int index = 0; index < battle.Log.Count; index++)
        {
            var original = battle.Log[index];
            if (original.Kind != "enter" && original.Kind != "ban" && original.Kind != "lead" && original.Node != Node.Over)
                Assert.Equal(original.Text, display.Log[index].Text);
        }
        Assert.Equal(battle.StateHash(), display.StateHash());
        Assert.Equal(Json(battle.Events), Json(display.Events));
        Assert.Equal(before, SourceSnapshot(battle));
    }
}
