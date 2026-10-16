using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

[Collection("Localization current")]
public class T88_CommentaryTests
{
    static readonly string[] Categories =
    {
        "opening", "crit", "miss", "effective", "resisted", "knockout", "comeback", "streak",
        "control", "cleanse", "switch", "field", "last", "victory", "draw"
    };

    static string LocaleJson => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", "Locale", "zh-CN.json"));

    static Loc MarkerLocale()
    {
        var texts = new Dictionary<string, string>();
        foreach (string category in Categories)
            for (int variant = 0; variant < 3; variant++)
                texts.Add("comment." + category + "." + variant, category + ":" + variant + "|{0}|{1}|{2}|{3}");
        return Loc.FromJson(JsonSerializer.Serialize(new { language = "test", texts }), Loc.Current);
    }

    static void InLocale(Loc locale, Action action)
    {
        var previous = Loc.Current;
        try { Loc.Current = locale; action(); }
        finally { Loc.Current = previous; }
    }

    static string Category(CommentLine line) => line.Text.Split(':')[0];
    static (int, string, int) Key(CommentLine line) => (line.EventIndex, line.Text, line.Priority);

    static ContentDb FixtureDb()
    {
        var db = BattleTests.LoadPack("original");
        db.Skills["comment_finish"] = Skill("comment_finish", "决胜一招",
            new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 10000 });
        db.Skills["comment_hit"] = Skill("comment_hit", "日常互动");
        db.Skills["comment_hit"].Cat = "interact";
        db.Skills["comment_hit"].Power = 40;
        db.Skills["comment_setup"] = Skill("comment_setup", "准备开播");
        db.Skills["comment_other"] = Skill("comment_other", "节奏调整");
        db.Skills["comment_fifth"] = Skill("comment_fifth", "压轴时刻");
        foreach (var streamer in db.Streamers)
        {
            streamer.Persona = "pure";
            streamer.Stats["hp"] = 400;
            streamer.Skills = new List<string> { "comment_finish", "comment_hit", "comment_setup", "comment_other" };
            streamer.Fifth = "comment_fifth";
        }
        db.Validate();
        return db;
    }

    static SkillDef Skill(string id, string name, params EffectDef[] effects)
    {
        return new SkillDef
        {
            Id = id, Name = name, Type = "talk", Cat = "status", Pp = 200, Acc = 100,
            Effects = effects.ToList()
        };
    }

    static Battle Make(ContentDb db = null, uint seed = 88, bool leadPick = false)
    {
        db = db ?? FixtureDb();
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        return new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "晨光队", "星河队" }, seed, leadPick);
    }

    static void Replace(Battle battle)
    {
        for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
            if (battle.Need[side]) battle.Replace(side, battle.Sides[side].Team.FindIndex(f => !f.Fainted));
    }

    static Battle Completed(uint seed = 88)
    {
        var battle = Make(seed: seed);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        for (int i = 0; i < 6 && battle.Phase != Phase.Over; i++)
        {
            battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            Replace(battle);
        }
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(0, battle.Winner);
        return battle;
    }

    static string[] EventSnapshot(Battle battle) => battle.Events.Select(e =>
        $"{e.LogIndex}|{e.Turn}|{e.Node}|{e.Kind}|{e.Side}|{e.Slot}|{e.Id}|{e.Amount}").ToArray();

    static string[] LogSnapshot(Battle battle) => battle.Log.Select(e =>
        $"{e.Turn}|{e.Node}|{e.Kind}|{e.Side}|{e.Text}|" + string.Join(",", e.SnapActive) + "|" +
        string.Join("/", e.SnapHp.Select(hp => string.Join(",", hp)))).ToArray();

    [Fact]
    public void AllFifteenCategoriesHaveThreeUsableTemplatesInResourceAndBuiltin()
    {
        string json = LocaleJson;
        using var document = JsonDocument.Parse(json);
        var texts = document.RootElement.GetProperty("texts");
        Assert.True(texts.EnumerateObject().Count(p => p.Name.StartsWith("comment.", StringComparison.Ordinal)) >= 40);
        var resource = Loc.FromJson(json);
        InLocale(null, () =>
        {
            foreach (string category in Categories)
                for (int variant = 0; variant < 3; variant++)
                {
                    string key = "comment." + category + "." + variant;
                    Assert.True(texts.TryGetProperty(key, out var template), "缺少模板 " + key);
                    Assert.True(resource.Has(key));
                    Assert.True(Loc.Current.Has(key));
                    object[] args = { "晨光主播", "星光技能", 123, "对面主播" };
                    string expected = string.Format(CultureInfo.InvariantCulture, template.GetString(), args);
                    Assert.False(string.IsNullOrWhiteSpace(expected));
                    Assert.DoesNotContain("{", expected);
                    Assert.DoesNotContain("}", expected);
                    Assert.Equal(expected, resource.T(key, args));
                    Assert.Equal(expected, Loc.Current.T(key, args));
                }
        });
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(88u)]
    public void CompletedFixedSeedGameHasOpeningKnockoutAndVictoryAndAtMostTwoPerTurn(uint seed)
    {
        var battle = Completed(seed);
        InLocale(MarkerLocale(), () =>
        {
            var lines = Commentary.For(battle, 0, battle.Events.Count);
            Assert.Contains(lines, line => Category(line) == "opening");
            Assert.Contains(lines, line => Category(line) == "knockout");
            Assert.Contains(lines, line => Category(line) == "victory");
            Assert.All(lines, line => Assert.InRange(line.EventIndex, 0, battle.Events.Count - 1));
            Assert.All(lines.GroupBy(line => battle.Events[line.EventIndex].Turn), group => Assert.InRange(group.Count(), 1, 2));
            Assert.Equal(lines.Select(Key), Commentary.For(battle, 0, battle.Events.Count).Select(Key));
            Assert.Equal(lines.Select(Key), Commentary.For(battle.Clone(), 0, battle.Events.Count).Select(Key));
            var replay = Replay.Run(battle.Db, battle.Record);
            Assert.Equal(lines.Select(Key), Commentary.For(replay, 0, replay.Events.Count).Select(Key));
            Assert.Equal(lines.Select(Key), Commentary.For(Completed(seed), 0, battle.Events.Count).Select(Key));
        });
    }

    [Fact]
    public void ResourceAndDefaultLocalesProduceTheSameRealChineseCommentary()
    {
        var battle = Completed();
        List<CommentLine> builtin = null;
        InLocale(null, () => builtin = Commentary.For(battle, 0, battle.Events.Count));
        InLocale(Loc.FromJson(LocaleJson), () =>
        {
            var resource = Commentary.For(battle, 0, battle.Events.Count);
            Assert.NotEmpty(resource);
            Assert.Equal(builtin.Select(Key), resource.Select(Key));
            Assert.All(resource, line =>
            {
                Assert.False(string.IsNullOrWhiteSpace(line.Text));
                Assert.DoesNotContain("[comment.", line.Text);
                Assert.DoesNotContain("{", line.Text);
            });
        });
    }

    [Fact]
    public void EverySliceUsesAbsoluteIndicesAndTheSameGlobalPerTurnSelection()
    {
        var battle = Completed();
        var all = Commentary.For(battle, 0, battle.Events.Count).Select(Key).ToArray();
        var individual = new List<(int, string, int)>();
        for (int index = 0; index < battle.Events.Count; index++)
        {
            var slice = Commentary.For(battle, index, index + 1);
            Assert.All(slice, line => Assert.Equal(index, line.EventIndex));
            Assert.Equal(all.Where(line => line.Item1 == index), slice.Select(Key));
            individual.AddRange(slice.Select(Key));
            Assert.Empty(Commentary.For(battle, index, index));
        }
        Assert.Equal(all, individual);
        for (int from = 0; from < battle.Events.Count; from += 7)
        {
            int to = Math.Min(from + 11, battle.Events.Count);
            Assert.Equal(all.Where(line => line.Item1 >= from && line.Item1 < to),
                Commentary.For(battle, from, to).Select(Key));
        }
        Assert.Empty(Commentary.For(battle, battle.Events.Count, battle.Events.Count));
    }

    [Fact]
    public void HigherPriorityEventsSuppressEarlierLowPriorityLinesEvenWhenQueriedAlone()
    {
        var db = FixtureDb();
        db.Skills["comment_setup"].Effects.AddRange(new[]
        {
            new EffectDef { At = "post", Code = "FIELD", Target = "self", Field = "early", Turns = 3 },
            new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "stall" },
            new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 10000 }
        });
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        int from = battle.Events.Count;
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        InLocale(MarkerLocale(), () =>
        {
            var lines = Commentary.For(battle, from, battle.Events.Count);
            Assert.Equal(2, lines.Count);
            Assert.Contains(lines, line => Category(line) == "control");
            Assert.Contains(lines, line => Category(line) == "knockout");
            Assert.DoesNotContain(lines, line => Category(line) == "field");
            int field = Enumerable.Range(from, battle.Events.Count - from)
                .Single(i => battle.Events[i].Kind == BattleEventKind.FieldAdd);
            Assert.Empty(Commentary.For(battle, field, field + 1));
            Assert.True(lines.Single(line => Category(line) == "knockout").Priority
                > lines.Single(line => Category(line) == "control").Priority);
        });
    }

    [Fact]
    public void QueriesPreserveStateLogsRecordSummaryEventsAndTheRandomStream()
    {
        var battle = Make(BattleTests.LoadPack("original"));
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        Replace(battle);
        var untouched = battle.Clone();
        ulong hash = battle.StateHash();
        string record = battle.Record.ToJson();
        string summary = BattleSummary.From(battle).ToText();
        var logs = LogSnapshot(battle);
        var events = EventSnapshot(battle);
        for (int query = 0; query < 4; query++)
        {
            Commentary.For(battle, 0, battle.Events.Count);
            Commentary.For(battle, battle.Events.Count / 2, battle.Events.Count);
            Commentary.For(battle, 0, 0);
        }
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(summary, BattleSummary.From(battle).ToText());
        Assert.Equal(logs, LogSnapshot(battle));
        Assert.Equal(events, EventSnapshot(battle));
        for (int i = 0; i < 8; i++) Assert.Equal(untouched.Random(), battle.Random());
    }

    [Fact]
    public void ReturnedLinesAndListsAreIndependentOfLaterQueries()
    {
        var battle = Completed();
        var expected = Commentary.For(battle, 0, battle.Events.Count).Select(Key).ToArray();
        var changed = Commentary.For(battle, 0, battle.Events.Count);
        Assert.NotEmpty(changed);
        changed[0].EventIndex = -1;
        changed[0].Text = "已修改";
        changed[0].Priority = -1;
        changed.Clear();
        Assert.Equal(expected, Commentary.For(battle, 0, battle.Events.Count).Select(Key));
    }

    [Fact]
    public void EmptyPreStartHistoryAndInvalidRangesHaveExplicitBehavior()
    {
        var waiting = Make(leadPick: true);
        Assert.Empty(waiting.Events);
        Assert.Empty(Commentary.For(waiting, 0, 0));
        waiting.ChooseLead(0, 0);
        Assert.Empty(Commentary.For(waiting, 0, waiting.Events.Count));
        waiting.ChooseLead(1, 0);
        Assert.NotEmpty(Commentary.For(waiting, 0, waiting.Events.Count));
        int end = waiting.Events.Count;
        Assert.Throws<ArgumentNullException>(() => Commentary.For(null, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Commentary.For(waiting, -1, end));
        Assert.Throws<ArgumentOutOfRangeException>(() => Commentary.For(waiting, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Commentary.For(waiting, end, end - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Commentary.For(waiting, 0, end + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Commentary.For(waiting, end + 1, end + 1));

        var ban = new Battle(waiting.Db, waiting.Record.Teams, waiting.Record.Names,
            new BattleOptions { Seed = 88, BanOne = true, LeadPick = true });
        Assert.Empty(Commentary.For(ban, 0, ban.Events.Count));
        ban.ChooseBan(0, 0);
        Assert.Empty(Commentary.For(ban, 0, ban.Events.Count));
        ban.ChooseBan(1, 0);
        Assert.Equal(Phase.Lead, ban.Phase);
        Assert.Empty(Commentary.For(ban, 0, ban.Events.Count));
    }

    [Fact]
    public void ComebackUsesHpAtTheHistoricalKnockoutRatherThanHpAfterHealing()
    {
        var db = FixtureDb();
        db.Skills["comment_setup"].Effects.Add(new EffectDef
            { At = "post", Code = "FIXED_DAMAGE", Target = "self", N = 350 });
        db.Skills["comment_other"].Effects.Add(new EffectDef
            { At = "post", Code = "HEAL", Target = "self", Frac = 1 });
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        Assert.Equal(50, battle.Active(0).Hp);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        int knockout = Enumerable.Range(0, battle.Events.Count).Single(i => battle.Events[i].Kind == BattleEventKind.Faint);
        Replace(battle);
        battle.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());
        Assert.Equal(battle.Active(0).MaxHp, battle.Active(0).Hp);
        InLocale(MarkerLocale(), () =>
        {
            var line = Assert.Single(Commentary.For(battle, knockout, knockout + 1));
            Assert.Equal("comeback", Category(line));
            Assert.Contains(battle.Sides[0].Team[0].Name, line.Text);
        });
    }

    [Fact]
    public void StreakTracksTheHistoricalAttackerAndResetsWhenThatFighterSwitchesOut()
    {
        var battle = Make();
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        for (int kill = 0; kill < 4; kill++)
        {
            if (kill >= 2) battle.ResolveTurn(BattleAction.SwitchTo(kill == 2 ? 1 : 0), BattleAction.Pass());
            battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            Replace(battle);
        }
        var knockouts = Enumerable.Range(0, battle.Events.Count).Where(i => battle.Events[i].Kind == BattleEventKind.Faint).ToArray();
        Assert.Equal(4, knockouts.Length);
        InLocale(MarkerLocale(), () =>
        {
            string[] expected = { "knockout", "streak", "knockout", "knockout" };
            for (int i = 0; i < knockouts.Length; i++)
                Assert.Equal(expected[i], Category(Assert.Single(Commentary.For(battle, knockouts[i], knockouts[i] + 1))));
        });
    }

    [Theory]
    [InlineData("self")]
    [InlineData("dot")]
    [InlineData("reflection")]
    public void IndirectFaintsNeverInventACombatComebackOrStreak(string cause)
    {
        var db = FixtureDb();
        if (cause == "self") db.Skills["comment_setup"].Effects.Add(new EffectDef
            { At = "post", Code = "FIXED_DAMAGE", Target = "self", N = 10000 });
        else if (cause == "dot")
        {
            db.Statuses["flame"].Dot = 1;
            db.Skills["comment_setup"].Effects.Add(new EffectDef
                { At = "post", Code = "STATUS", Target = "foe", Status = "flame" });
        }
        else
        {
            db.Fields["mirror"].ReflectDamage = 10;
            db.Skills["comment_setup"].Effects.Add(new EffectDef
                { At = "post", Code = "FIELD", Target = "self", Field = "mirror", Turns = 3 });
            db.Skills["comment_other"].Effects.Add(new EffectDef
                { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 100 });
        }
        var battle = Make(db);
        if (cause == "reflection")
        {
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(2));
            battle.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());
        }
        else battle.ResolveTurn(cause == "self" ? BattleAction.Skill(2) : BattleAction.Pass(),
            cause == "dot" ? BattleAction.Skill(2) : BattleAction.Pass());
        int faint = Enumerable.Range(0, battle.Events.Count).Single(i => battle.Events[i].Kind == BattleEventKind.Faint);
        InLocale(MarkerLocale(), () =>
            Assert.Equal("knockout", Category(Assert.Single(Commentary.For(battle, faint, faint + 1)))));
    }

    [Fact]
    public void ARealTurnCapDrawHasDrawCommentaryInsteadOfVictory()
    {
        var battle = Make();
        for (int turn = 0; turn < Battle.TurnCap; turn++) battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(-1, battle.Winner);
        InLocale(MarkerLocale(), () =>
        {
            var lines = Commentary.For(battle, 0, battle.Events.Count);
            Assert.Contains(lines, line => Category(line) == "draw");
            Assert.DoesNotContain(lines, line => Category(line) == "victory");
        });
    }

    [Fact]
    public void ControlRecoveryAndItsProtectionFieldUseTheirOwnRealEvents()
    {
        var db = FixtureDb();
        db.Skills["comment_setup"].Effects.Add(new EffectDef
            { At = "post", Code = "STATUS", Target = "foe", Status = "stall" });
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        InLocale(MarkerLocale(), () =>
        {
            var lines = Commentary.For(battle, 0, battle.Events.Count);
            foreach (string category in new[] { "control", "cleanse", "field" })
            {
                var line = Assert.Single(lines, candidate => Category(candidate) == category);
                Assert.Contains(battle.Sides[1].Team[0].Name, line.Text);
                var entry = battle.Events[line.EventIndex];
                Assert.Equal(category == "control" ? BattleEventKind.StatusAdd
                    : category == "cleanse" ? BattleEventKind.StatusRemove : BattleEventKind.FieldAdd, entry.Kind);
                Assert.Equal(category == "field" ? "reconnect" : "stall", entry.Id);
                Assert.Contains(category == "field" ? db.Fields["reconnect"].Name : db.Statuses["stall"].Name, line.Text);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CriticalAndMissLinesReferToTheActualActingSkill(bool miss)
    {
        var db = FixtureDb();
        for (int i = 0; i < db.Streamers.Count; i++) db.Streamers[i].Stats["spd"] = i < 6 ? 400 : 100;
        db.Skills["comment_hit"].Acc = miss ? 0 : 100;
        var battle = Make(db, seed: 7);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        var expectedKind = miss ? BattleEventKind.Miss : BattleEventKind.Crit;
        int eventIndex = Enumerable.Range(0, battle.Events.Count).Single(i => battle.Events[i].Kind == expectedKind);
        InLocale(MarkerLocale(), () =>
        {
            var line = Assert.Single(Commentary.For(battle, eventIndex, eventIndex + 1));
            Assert.Equal(miss ? "miss" : "crit", Category(line));
            Assert.Contains(battle.Active(0).Name, line.Text);
            Assert.Contains(db.Skills["comment_hit"].Name, line.Text);
        });
    }

    [Theory]
    [InlineData("game", "effective")]
    [InlineData("song", "resisted")]
    public void MatchupCommentaryUsesTheOpponentAtTheHistoricalAction(string opponentType, string category)
    {
        var db = FixtureDb();
        db.Streamers[6].Type = opponentType;
        db.Streamers[6].Type2 = null;
        db.Streamers[7].Type = "art";
        db.Streamers[7].Type2 = null;
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        int action = Enumerable.Range(0, battle.Events.Count).Single(i => battle.Events[i].Kind == BattleEventKind.Act);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.SwitchTo(1));
        Assert.Equal("art", battle.Active(1).Type);
        InLocale(MarkerLocale(), () =>
        {
            var line = Assert.Single(Commentary.For(battle, action, action + 1));
            Assert.Equal(category, Category(line));
            Assert.Contains(db.Skills["comment_hit"].Name, line.Text);
            Assert.Contains(battle.Sides[1].Team[0].Name, line.Text);
        });
    }

    [Fact]
    public void SwitchNamesTheIncomingFighterAndLastEntryUsesHistoricalLivingCount()
    {
        var switched = Make();
        switched.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        switched.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
        var last = Make();
        last.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        for (int kill = 0; kill < 5; kill++)
        {
            last.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            Replace(last);
        }
        Assert.Single(last.Sides[1].Team, f => !f.Fainted);
        last.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        InLocale(MarkerLocale(), () =>
        {
            var switchLine = Assert.Single(Commentary.For(switched, 0, switched.Events.Count),
                line => Category(line) == "switch");
            Assert.Contains(switched.Sides[0].Team[1].Name, switchLine.Text);
            Assert.Equal(BattleEventKind.Enter, switched.Events[switchLine.EventIndex].Kind);
            var lastLine = Assert.Single(Commentary.For(last, 0, last.Events.Count),
                line => Category(line) == "last");
            Assert.Contains(last.Sides[1].Team[5].Name, lastLine.Text);
            Assert.Equal(Node.Replace, last.Events[lastLine.EventIndex].Node);
            Assert.Equal(5, last.Events[lastLine.EventIndex].Slot);
        });
    }
}
