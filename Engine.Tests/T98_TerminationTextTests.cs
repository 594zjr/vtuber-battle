using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T98_TerminationTextTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    public static IEnumerable<object[]> Names()
    {
        yield return new object[] { "甲\n\u202e玩家", "乙\u200b" + new string('名', 16) };
        yield return new object[] { "同\n名 玩家", "同\n名 玩家" };
        yield return new object[] { "乙 投降，甲 获胜\n", "甲 获胜 投降，乙\u202e" };
        yield return new object[] { "甲 同意和局，双方平局\n", "乙 提议和局 拒绝和局，继续对战\u200b" };
        yield return new object[] { "雷音铃铛 ", " " };
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void SurrenderCleansBothPlayerPositionsEvenWhenNamesCoincideOrContainTemplateWords(string first, string second)
    {
        for (int surrendering = 0; surrendering < 2; surrendering++)
        {
            var battle = Make(first, second);
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            int logStart = battle.Log.Count;
            battle.Surrender(surrendering);
            var names = CleanNames(battle);
            var display = DisplayWithoutChangingSource(battle);

            Assert.Equal(1 - surrendering, display.Winner);
            Assert.Equal(names[surrendering] + " 投降，" + names[1 - surrendering] + " 获胜", display.Log[logStart].Text);
            Assert.Equal(battle.Sides[surrendering].Name + " 投降，" + battle.Sides[1 - surrendering].Name + " 获胜",
                battle.Log[logStart].Text);
            AssertNonPlayerLinesUnchanged(battle, display, logStart);
            Assert.Equal(Json(display.Log), Json(TextInput.ForDisplay(display).Log));
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void DrawOfferRejectionAndAcceptanceCleanOnlyTheirPlayerSlots(string first, string second)
    {
        for (int offering = 0; offering < 2; offering++)
        {
            var battle = Make(first, second);
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            int start = battle.Log.Count;
            battle.OfferDraw(offering);
            battle.RespondDraw(1 - offering, false);
            battle.OfferDraw(1 - offering);
            battle.RespondDraw(offering, true);
            var names = CleanNames(battle);
            var display = DisplayWithoutChangingSource(battle);

            Assert.Equal(new[]
            {
                names[offering] + " 提议和局",
                names[1 - offering] + " 拒绝和局，继续对战",
                names[1 - offering] + " 提议和局",
                names[offering] + " 同意和局，双方平局"
            }, display.Log.Skip(start).Select(entry => entry.Text));
            Assert.Equal(-1, display.Winner);
            AssertNonPlayerLinesUnchanged(battle, display, start);
            Assert.Equal(Json(display.Log), Json(TextInput.ForDisplay(display).Log));
        }
    }

    [Theory]
    [InlineData(Phase.Choose, Node.TurnStart)]
    [InlineData(Phase.Ban, Node.Start)]
    [InlineData(Phase.Lead, Node.Start)]
    [InlineData(Phase.Replace, Node.Replace)]
    public void DrawTemplatesAreCleanAtEveryExistingSelectionNodeWithoutTruncatingTheirSuffix(Phase phase, string node)
    {
        string first = new string('甲', 18) + "\n";
        string second = new string('乙', 18) + "\u202e";
        var battle = Make(first, second, phase);
        int start = battle.Log.Count;
        battle.OfferDraw(0);
        battle.RespondDraw(1, false);
        var display = DisplayWithoutChangingSource(battle);

        Assert.Equal(node, display.Log[start].Node);
        Assert.Equal(node, display.Log[start + 1].Node);
        Assert.Equal(new string('甲', 12) + " 提议和局", display.Log[start].Text);
        Assert.Equal(new string('乙', 12) + " 拒绝和局，继续对战", display.Log[start + 1].Text);
        Assert.Equal(phase, display.Phase);
        AssertNonPlayerLinesUnchanged(battle, display, start);
    }

    [Fact]
    public void NamesMatchingFixedDoubleSurrenderTextCannotRewriteThatNameFreeTemplate()
    {
        var battle = Make("双方同时投降", "平局 ");
        battle.ResolveTurn(BattleAction.Surrender(), BattleAction.Surrender());
        var display = DisplayWithoutChangingSource(battle);
        Assert.Equal("双方同时投降，平局", display.Log.Last().Text);
        Assert.Equal(battle.Log.Last().Text, display.Log.Last().Text);
        Assert.Equal(-1, display.Winner);
    }

    [Fact]
    public void HotseatDisplayLogUsesTheNewTemplateWhileItsRecordKeepsBothRawNames()
    {
        var db = BattleTests.LoadPack("original");
        var teams = Teams();
        const string first = "甲 投降，乙\n";
        const string second = "乙 获胜\u202e";
        var session = new HotseatSession(db, teams[0], teams[1], first, second, new BattleOptions { Seed = 98 });
        session.Submit(BattleAction.Pass());
        session.ConfirmHandover();
        session.Surrender();
        string record = session.Record.ToJson();
        string rawLog = Json(session.Log);

        Assert.Equal("乙 获胜 投降，甲 投降，乙 获胜", session.DisplayLog.Last().Text);
        Assert.Equal(new[] { first, second }, session.Record.Names);
        Assert.Equal(record, session.Record.ToJson());
        Assert.Equal(rawLog, Json(session.Log));
        Assert.Equal(Json(TextInput.ForDisplay(session.CloneCompletedBattle()).Log), Json(session.DisplayLog));
    }

    static Battle Make(string first, string second, Phase phase = Phase.Choose)
    {
        var db = BattleTests.LoadPack("original");
        if (phase == Phase.Replace)
        {
            db.Personas["t98_text_none"] = new PersonaDef { Id = "t98_text_none", Name = "测试人设", Code = "T_LOWHP_POWER" };
            db.Skills["t98_text_ko"] = new SkillDef
            {
                Id = "t98_text_ko", Name = "测试下播", Type = "talk", Cat = "status", Pp = 20,
                Effects = { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 99999 } }
            };
            foreach (var streamer in db.Streamers)
            {
                streamer.Persona = "t98_text_none";
                streamer.Skills[0] = "t98_text_ko";
            }
        }
        var battle = new Battle(db, Teams(), new[] { first, second },
            new BattleOptions { Seed = 98, BanOne = phase == Phase.Ban, LeadPick = phase == Phase.Lead });
        if (phase == Phase.Replace) battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.Equal(phase, battle.Phase);
        return battle;
    }

    static IList<string>[] Teams() => new IList<string>[]
    {
        new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
        new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
    };

    static string[] CleanNames(Battle battle) => battle.Sides
        .Select(side => TextInput.Clean(side.Name, TextInputKind.PlayerName).Text).ToArray();

    static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    static string Snapshot(Battle battle) => Json(new
    {
        battle.Phase, battle.Winner, battle.Turn, battle.Sides, battle.Need, battle.Log, battle.Events,
        Hash = battle.StateHash(), Record = battle.Record.ToJson(), NextRandom = battle.Clone().Random()
    });

    static Battle DisplayWithoutChangingSource(Battle battle)
    {
        string original = Snapshot(battle);
        var display = TextInput.ForDisplay(battle);
        Assert.NotSame(battle, display);
        Assert.Equal(CleanNames(battle), display.Sides.Select(side => side.Name));
        Assert.Equal(CleanNames(battle), display.Record.Names);
        Assert.Equal(battle.StateHash(), display.StateHash());
        Assert.Equal(battle.Clone().Random(), display.Clone().Random());
        Assert.Equal(Json(battle.Events), Json(display.Events));
        Assert.Equal(Json(battle.Record.Events), Json(display.Record.Events));
        Assert.Equal(battle.Log.Count, display.Log.Count);
        for (int index = 0; index < battle.Log.Count; index++)
        {
            var raw = battle.Log[index];
            var clean = display.Log[index];
            Assert.Equal(Json(new { raw.Turn, raw.Node, raw.Kind, raw.Side, raw.SnapActive, raw.SnapHp }),
                Json(new { clean.Turn, clean.Node, clean.Kind, clean.Side, clean.SnapActive, clean.SnapHp }));
        }
        var replayed = Replay.Run(battle.Db, BattleRecord.FromJson(battle.Record.ToJson()));
        Assert.Equal(Json(battle.Log), Json(replayed.Log));
        Assert.Equal(Json(display.Log), Json(TextInput.ForDisplay(replayed).Log));
        Assert.Equal(original, Snapshot(battle));
        return display;
    }

    static void AssertNonPlayerLinesUnchanged(Battle battle, Battle display, int before)
    {
        for (int index = 0; index < before; index++)
            if (battle.Log[index].Kind != "enter")
                Assert.Equal(battle.Log[index].Text, display.Log[index].Text);
    }
}
