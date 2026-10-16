using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;
using Xunit.Abstractions;

public class T105_LogFilterTests
{
    static readonly string[][] Teams =
    {
        new[] { "kei", "enka", "shiori", "lumi", "hane", "momo" },
        new[] { "bell", "yuan", "nyako", "buck", "kasumi", "bee" }
    };
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };
    readonly ITestOutputHelper output;

    public T105_LogFilterTests(ITestOutputHelper output) { this.output = output; }

    static ContentDb Content()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["log_neutral"] = new PersonaDef { Id = "log_neutral", Name = "未触发的人设", Code = "T_LOWHP_POWER" };
        db.Statuses["log_control"] = new StatusDef { Id = "log_control", Name = "测试控制", Cat = "control", Skip = 1 };
        db.Statuses["log_weak"] = new StatusDef { Id = "log_weak", Name = "测试弱化", Cat = "weak", Turns = 1 };
        db.Fields["log_regen"] = new FieldDef { Id = "log_regen", Name = "测试回复场", Kind = "turn", Heal = 0.1, Dispel = true };
        db.Fields["log_mark"] = new FieldDef { Id = "log_mark", Name = "测试印记", Kind = "mark", MaxStacks = 3 };
        db.Chart["talk"]["talk"] = 1;
        Skill(db, "log_hit").Power = 40;
        db.Skills["log_hit"].Cat = "interact";
        Skill(db, "log_fixed", Effect("FIXED_DAMAGE", n: 10));
        Skill(db, "log_control_skill", new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "log_control" });
        Skill(db, "log_setup", new EffectDef { At = "post", Code = "FIELD", Target = "self", Field = "log_regen", Turns = 2 });
        Skill(db, "log_fifth", Effect("FIXED_DAMAGE", n: 99999));
        foreach (var definition in db.Streamers)
        {
            definition.Persona = "log_neutral";
            definition.Type = "talk";
            definition.Type2 = null;
            definition.Stats["hp"] = 1000;
            definition.Skills = new List<string> { "log_hit", "log_fixed", "log_control_skill", "log_setup" };
            definition.Fifth = "log_fifth";
        }
        for (int side = 0; side < 2; side++)
            for (int slot = 0; slot < 6; slot++)
                db.Streamer(Teams[side][slot]).Stats["spd"] = 200 - side * 100 - slot;
        return db;
    }

    static SkillDef Skill(ContentDb db, string id, params EffectDef[] effects)
    {
        var skill = new SkillDef { Id = id, Name = "测试技能「" + id + "」", Type = "talk", Cat = "status", Pp = 50, Prio = 5 };
        skill.Effects.AddRange(effects);
        db.Skills[id] = skill;
        return skill;
    }

    static EffectDef Effect(string code, string target = "foe", int n = 0)
    {
        return new EffectDef { At = "post", Code = code, Target = target, N = n };
    }

    static Battle Make(ContentDb db = null, uint seed = 105, bool blank = false)
    {
        return new Battle(db ?? Content(), Teams.Select(team => (IList<string>)team.ToArray()).ToArray(),
            new[] { "甲方", "乙方" }, new BattleOptions { Seed = seed, BanOne = blank });
    }

    static LogEntry Row(int turn, int side, string kind, string text, string node = Node.First)
    {
        return new LogEntry { Turn = turn, Side = side, Kind = kind, Text = text, Node = node };
    }

    static Battle Rows()
    {
        // 禁用阶段尚未产生事件，手工日志不会与遗留开场事件错误关联。
        var b = Make(blank: true);
        Assert.Empty(b.Events);
        b.Log.AddRange(new[]
        {
            Row(3, 0, "dmg", "Ａlpha 暴击提示"),
            Row(1, 1, "status", "alpha 控制"),
            null,
            Row(3, 1, "dmg", "beta"),
            Row(3, 0, "status", "aLpHa"),
            Row(2, -1, "turn", "第 2 回合", Node.TurnHeader),
            Row(1, 0, null, null),
            Row(1, -1, "info", "Résumé"),
            Row(1, -1, "info", "resume"),
            Row(4, 0, "info", "Cafe\u0301"),
            Row(4, 0, "info", "Cafe"),
            Row(4, 1, "info", "ｶﾀｶﾅ")
        });
        return b;
    }

    static List<int> Important(Battle b) { return LogFilter.Apply(b, new LogQuery { OnlyImportant = true }); }
    static string Json<T>(T value) { return JsonSerializer.Serialize(value, JsonOptions); }
    static string Snapshot(Battle b)
    {
        return b.StateHash().ToString("x16") + "\n" + b.Record.ToJson() + "\n" + Json(b.Log) + "\n" +
            Json(b.Events) + "\n" + Json(b.Sides) + "\n" + b.Db.ContentHash();
    }

    [Fact]
    public void EmptyQueriesReturnOriginalIndicesAndSkipNullEntries()
    {
        var b = Rows();
        int[] expected = { 0, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        Assert.Equal(expected, LogFilter.Apply(b, null));
        Assert.Equal(expected, LogFilter.Apply(b, new LogQuery()));
        Assert.Equal(expected, LogFilter.Apply(b, new LogQuery { Kinds = new HashSet<string>(), Text = "" }));
        var empty = Make(blank: true);
        empty.Log.Add(null);
        Assert.Empty(LogFilter.Apply(empty, null));
        Assert.Empty(LogFilter.GroupByTurn(empty));
        Assert.Throws<ArgumentNullException>(() => LogFilter.Apply(null, new LogQuery()));
        Assert.Throws<ArgumentNullException>(() => LogFilter.GroupByTurn(null));
    }

    [Fact]
    public void TurnSideAndKindsUseAndAcrossConditionsAndOrWithinKinds()
    {
        var b = Rows();
        Assert.Equal(new[] { 0, 3, 4 }, LogFilter.Apply(b, new LogQuery { Turn = 3 }));
        Assert.Equal(new[] { 0, 4, 6, 9, 10 }, LogFilter.Apply(b, new LogQuery { Side = 0 }));
        Assert.Equal(new[] { 5, 7, 8 }, LogFilter.Apply(b, new LogQuery { Side = -1 }));
        Assert.Equal(new[] { 0, 1, 3, 4 }, LogFilter.Apply(b, new LogQuery { Kinds = new HashSet<string> { "dmg", "status" } }));
        var combined = new LogQuery { Turn = 3, Side = 0, Kinds = new HashSet<string> { "dmg", "status" }, Text = "ALPHA" };
        Assert.Equal(new[] { 0, 4 }, LogFilter.Apply(b, combined));
        combined.Kinds.Remove("dmg");
        Assert.Equal(new[] { 4 }, LogFilter.Apply(b, combined));
        Assert.Empty(LogFilter.Apply(b, new LogQuery { Side = 2 }));
        Assert.Empty(LogFilter.Apply(b, new LogQuery { Turn = -1 }));
        Assert.Empty(LogFilter.Apply(b, new LogQuery { Kinds = new HashSet<string> { "DMG", "未知类型" } }));
        Assert.Empty(LogFilter.Apply(b, new LogQuery { Kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DMG" } }));
    }

    [Fact]
    public void TextSearchNormalizesWidthCaseAndCanonicalCompositionButKeepsAccents()
    {
        var b = Rows();
        Assert.Equal(new[] { 0, 1, 4 }, LogFilter.Apply(b, new LogQuery { Text = "ＡＬＰＨＡ" }));
        Assert.Equal(new[] { 7 }, LogFilter.Apply(b, new LogQuery { Text = "résumé" }));
        Assert.Equal(new[] { 8 }, LogFilter.Apply(b, new LogQuery { Text = "RESUME" }));
        Assert.Equal(new[] { 9 }, LogFilter.Apply(b, new LogQuery { Text = "CAFÉ" }));
        Assert.Equal(new[] { 10 }, LogFilter.Apply(b, new LogQuery { Text = "cafe" }));
        Assert.Equal(new[] { 11 }, LogFilter.Apply(b, new LogQuery { Text = "カタカナ" }));
        Assert.Equal(new[] { 0, 1, 5 }, LogFilter.Apply(b, new LogQuery { Text = " " }));
        Assert.Empty(LogFilter.Apply(b, new LogQuery { Text = "未出现的文字" }));
    }

    [Fact]
    public void SearchIsOrdinalRatherThanDependentOnTheCurrentCulture()
    {
        var b = Make(blank: true);
        b.Log.AddRange(new[] { Row(1, 0, "info", "FILE"), Row(1, 0, "info", "fıle"), Row(1, 0, "info", "fİle") });
        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        try
        {
            foreach (string culture in new[] { "en-US", "tr-TR", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                Assert.Equal(new[] { 0 }, LogFilter.Apply(b, new LogQuery { Text = "file" }));
            }
        }
        finally { CultureInfo.CurrentCulture = original; CultureInfo.CurrentUICulture = originalUi; }
    }

    [Fact]
    public void IsolatedSurrogatesAreSearchableWithoutRewritingOriginalStrings()
    {
        var b = Make(blank: true);
        string raw = "前\uD800后", query = "前\uDC00后";
        b.Log.Add(Row(1, 0, "info", raw));
        var q = new LogQuery { Text = query };
        Assert.Equal(new[] { 0 }, LogFilter.Apply(b, q));
        Assert.Equal(new[] { 0 }, LogFilter.Apply(b, new LogQuery { Text = "前\uFFFD后" }));
        Assert.Equal(raw, b.Log[0].Text);
        Assert.Equal(query, q.Text);
    }

    [Fact]
    public void FilteringAndGroupingDoNotMutateInputsAndReturnIndependentLists()
    {
        var b = Rows();
        var q = new LogQuery { Turn = 3, Kinds = new HashSet<string> { "status", "dmg" }, Text = "ａｌｐｈａ" };
        var kinds = q.Kinds;
        string before = Snapshot(b), queryBefore = Json(q);
        var filtered = LogFilter.Apply(b, q);
        var grouped = LogFilter.GroupByTurn(b);
        filtered.Clear();
        grouped[0].Indices.Clear();
        grouped[0].Summary = "外部修改";
        grouped.Clear();
        Assert.Equal(new[] { 0, 4 }, LogFilter.Apply(b, q));
        Assert.Equal(new[] { 1, 6, 7, 8 }, LogFilter.GroupByTurn(b)[0].Indices);
        Assert.Equal(before, Snapshot(b));
        Assert.Equal(queryBefore, Json(q));
        Assert.Same(kinds, q.Kinds);
    }

    [Fact]
    public void GroupsAreTurnSortedAndKeepAllOriginalIndicesWithMeaningfulFallbacks()
    {
        var b = Make(blank: true);
        b.Log.AddRange(new[]
        {
            Row(3, -1, "turn", "第 3 回合", Node.TurnHeader),
            Row(1, -1, "turn", "第 1 回合", Node.TurnHeader),
            Row(2, -1, "turn", "第 2 回合", Node.TurnHeader),
            Row(3, 0, "info", "第一条普通记录"),
            Row(2, 0, "info", " \t"),
            null,
            Row(1, 1, "info", "较早回合的记录"),
            Row(3, 1, "info", "后续普通记录"),
            Row(2, 1, "info", null)
        });
        var groups = LogFilter.GroupByTurn(b);
        Assert.Equal(new[] { 1, 2, 3 }, groups.Select(g => g.Turn));
        Assert.Equal(new[] { 1, 6 }, groups[0].Indices);
        Assert.Equal(new[] { 2, 4, 8 }, groups[1].Indices);
        Assert.Equal(new[] { 0, 3, 7 }, groups[2].Indices);
        Assert.Equal("第1回合：较早回合的记录", groups[0].Summary);
        Assert.Equal("第2回合：共3条记录", groups[1].Summary);
        Assert.Equal("第3回合：第一条普通记录", groups[2].Summary);
    }

    [Fact]
    public void SummaryPrefersAtMostThreeActualImportantEventsInOriginalOrder()
    {
        var b = Make();
        b.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.SwitchTo(1));
        var meaningful = b.Events.Where(e => e.Kind == BattleEventKind.Switch ||
            e.Kind == BattleEventKind.Enter && e.Node == Node.Switch).Select(e => e.LogIndex).Distinct().ToArray();
        Assert.Equal(4, meaningful.Length);
        var group = Assert.Single(LogFilter.GroupByTurn(b));
        Assert.Equal(Enumerable.Range(0, b.Log.Count), group.Indices);
        Assert.Equal("第1回合：" + string.Join("；", meaningful.Take(3).Select(i => b.Log[i].Text)), group.Summary);
        Assert.DoesNotContain(b.Log[meaningful[3]].Text, group.Summary);
    }

    [Fact]
    public void RealControlApplicationSkippingAndRemovalAreImportantButWeakStatusIsNot()
    {
        var b = Make();
        b.ResolveTurn(BattleAction.Skill(2), BattleAction.Skill(1));
        var added = Assert.Single(b.Events, e => e.Kind == BattleEventKind.StatusAdd && e.Id == "log_control");
        var removed = Assert.Single(b.Events, e => e.Kind == BattleEventKind.StatusRemove && e.Id == "log_control");
        int skipped = b.Log.FindIndex(e => e.Side == 1 && e.Kind == "status" && e.Text.Contains("无法行动"));
        Assert.True(skipped >= 0);
        var important = Important(b);
        Assert.Contains(added.LogIndex, important);
        Assert.Contains(removed.LogIndex, important);
        Assert.Contains(skipped, important);
        Assert.Equal(new[] { added.LogIndex, skipped, removed.LogIndex },
            LogFilter.Apply(b, new LogQuery { Turn = 1, Side = 1, Kinds = new HashSet<string> { "status" }, OnlyImportant = true }));

        var db = Content();
        Skill(db, "log_setup", new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "log_weak" });
        var weak = Make(db);
        weak.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());
        var weakEvents = weak.Events.Where(e => e.Id == "log_weak").ToArray();
        Assert.Equal(new[] { BattleEventKind.StatusAdd, BattleEventKind.StatusRemove }, weakEvents.Select(e => e.Kind));
        Assert.All(weakEvents, e => Assert.DoesNotContain(e.LogIndex, Important(weak)));
    }

    [Fact]
    public void OrdinaryDamageIsNotImportantEvenWhenANameContainsImportantKeywords()
    {
        var db = Content();
        db.Streamer(Teams[1][0]).Name = "暴击，赛道克制【人设·名字】";
        var b = Make(db);
        b.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        var damage = Assert.Single(b.Events, e => e.Kind == BattleEventKind.Damage);
        Assert.Contains("赛道克制", b.Log[damage.LogIndex].Text);
        Assert.DoesNotContain(b.Events, e => e.Kind == BattleEventKind.Crit);
        Assert.DoesNotContain(damage.LogIndex, Important(b));
        Assert.Empty(LogFilter.Apply(b, new LogQuery { OnlyImportant = true, Kinds = new HashSet<string> { "dmg" } }));
        Assert.Contains(damage.LogIndex, LogFilter.Apply(b, new LogQuery { Text = "暴击" }));
    }

    [Fact]
    public void RealCriticalDamageUsesTheLogSideAndDoesNotDuplicateItsIndex()
    {
        var db = Content();
        Battle critical = null;
        BattleEvent crit = null;
        for (uint seed = 1; seed <= 64 && crit == null; seed++)
        {
            var candidate = Make(db, seed);
            candidate.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            crit = candidate.Events.SingleOrDefault(e => e.Kind == BattleEventKind.Crit);
            if (crit != null) critical = candidate;
        }
        Assert.NotNull(critical);
        Assert.Equal(0, crit.Side);
        Assert.Equal(1, critical.Log[crit.LogIndex].Side);
        Assert.Equal(new[] { crit.LogIndex }, LogFilter.Apply(critical,
            new LogQuery { OnlyImportant = true, Side = 1, Kinds = new HashSet<string> { "dmg" } }));
        Assert.Empty(LogFilter.Apply(critical, new LogQuery { OnlyImportant = true, Side = 0, Kinds = new HashSet<string> { "dmg" } }));
    }

    [Theory]
    [InlineData(2.0, "赛道克制")]
    [InlineData(0.5, "赛道被克")]
    public void ActualTypeMatchupIsImportantWithoutDependingOnCriticalHits(double multiplier, string text)
    {
        var db = Content();
        db.Chart["talk"]["talk"] = multiplier;
        var b = NonCriticalHit(db);
        var damage = Assert.Single(b.Events, e => e.Kind == BattleEventKind.Damage);
        Assert.Contains(text, b.Log[damage.LogIndex].Text);
        Assert.Contains(damage.LogIndex, Important(b));
    }

    static Battle NonCriticalHit(ContentDb db)
    {
        for (uint seed = 1; seed <= 64; seed++)
        {
            var b = Make(db, seed);
            b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            if (!b.Events.Any(e => e.Kind == BattleEventKind.Crit)) return b;
        }
        throw new InvalidOperationException("固定种子范围没有产生普通命中，测试前提失效。");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(100, true)]
    public void ContactPersonaDistinguishesSuccessfulTriggerFromFailedRoll(int chance, bool included)
    {
        var db = Content();
        db.Personas["log_contact"] = new PersonaDef
        {
            Id = "log_contact", Name = "接触判定", Code = "T_CONTACT_STATUS", Status = "log_weak",
            Params = new Dictionary<string, double> { { "chance", chance } }
        };
        db.Streamer(Teams[1][0]).Persona = "log_contact";
        var b = NonCriticalHit(db);
        int index = Assert.Single(Enumerable.Range(0, b.Log.Count), i => b.Log[i].Kind == "trait");
        Assert.Contains(included ? "判定：成功" : "判定：未触发", b.Log[index].Text);
        Assert.Equal(included, Important(b).Contains(index));
    }

    [Fact]
    public void PersonaHealingAndDamageMultiplierAreImportantWithoutATraitEvent()
    {
        var db = Content();
        db.Personas["log_heal"] = new PersonaDef
        {
            Id = "log_heal", Name = "测试回复", Code = "T_REGEN", Params = new Dictionary<string, double> { { "frac", 0.1 } }
        };
        db.Streamer(Teams[1][0]).Persona = "log_heal";
        var b = Make(db);
        b.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        var heal = Assert.Single(b.Events, e => e.Kind == BattleEventKind.Heal);
        Assert.StartsWith("【人设·", b.Log[heal.LogIndex].Text);
        Assert.Equal("heal", b.Log[heal.LogIndex].Kind);
        Assert.Contains(heal.LogIndex, Important(b));

        var multiplierDb = Content();
        multiplierDb.Personas["log_reduce"] = new PersonaDef
        {
            Id = "log_reduce", Name = "测试减伤", Code = "T_INTERACT_REDUCE",
            Params = new Dictionary<string, double> { { "mul", 0.5 } }
        };
        multiplierDb.Streamer(Teams[1][0]).Persona = "log_reduce";
        var reduced = NonCriticalHit(multiplierDb);
        var damage = Assert.Single(reduced.Events, e => e.Kind == BattleEventKind.Damage);
        Assert.Contains("人设·测试减伤", reduced.Log[damage.LogIndex].Text);
        Assert.Contains(damage.LogIndex, Important(reduced));
    }

    [Fact]
    public void FieldApplicationAndRealHealingAreImportantButZeroMarkChangeIsNot()
    {
        var b = Make();
        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Skill(1));
        var added = Assert.Single(b.Events, e => e.Kind == BattleEventKind.FieldAdd && e.Id == "log_regen");
        var healed = Assert.Single(b.Events, e => e.Kind == BattleEventKind.Heal);
        Assert.Equal(10, healed.Amount);
        Assert.Contains(added.LogIndex, Important(b));
        Assert.Contains(healed.LogIndex, Important(b));
        var db = Content();
        Skill(db, "log_setup", new EffectDef { At = "post", Code = "ADD_MARK", Target = "self", Field = "log_mark", N = 0 });
        var zero = Make(db);
        zero.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());
        var mark = Assert.Single(zero.Events, e => e.Kind == BattleEventKind.FieldAdd);
        Assert.Equal(0, mark.Amount);
        Assert.DoesNotContain(mark.LogIndex, Important(zero));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualFieldReflectionAndDamageMultiplierAreImportant(bool reflection)
    {
        var db = Content();
        db.Fields["log_regen"].Heal = 0;
        db.Fields["log_regen"].ReflectDamage = reflection ? 0.5 : 0;
        db.Fields["log_regen"].DmgTakenMul = reflection ? 1 : 0.5;
        Battle b = null;
        for (uint seed = 1; seed <= 64; seed++)
        {
            b = Make(db, seed);
            b.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(3));
            b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            if (!b.Events.Any(e => e.Kind == BattleEventKind.Crit)) break;
        }
        Assert.DoesNotContain(b.Events, e => e.Kind == BattleEventKind.Crit);
        var damage = Assert.Single(b.Events, e => e.Kind == BattleEventKind.Damage && e.Side == (reflection ? 0 : 1));
        Assert.Contains(reflection ? "反弹：" : "测试回复场 ×0.5", b.Log[damage.LogIndex].Text);
        Assert.Contains(damage.LogIndex, Important(b));
        if (reflection)
        {
            var ordinary = Assert.Single(b.Events, e => e.Kind == BattleEventKind.Damage && e.Side == 1);
            Assert.DoesNotContain(ordinary.LogIndex, Important(b));
        }
    }

    [Fact]
    public void RealFieldWeakStatusImmunityIsImportantWithoutInventingAStatusAdd()
    {
        var db = Content();
        db.Fields["log_regen"].Heal = 0;
        db.Fields["log_regen"].ImmuneWeak = true;
        Skill(db, "log_control_skill", new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "log_weak" });
        var b = Make(db);
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(3));
        b.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        int blocked = Assert.Single(Enumerable.Range(0, b.Log.Count), i => b.Log[i].Text.EndsWith("挡下", StringComparison.Ordinal));
        Assert.Contains("测试弱化", b.Log[blocked].Text);
        Assert.DoesNotContain(b.Events, e => e.Kind == BattleEventKind.StatusAdd && e.Id == "log_weak");
        Assert.Contains(blocked, Important(b));
    }

    [Fact]
    public void ActualFieldPeriodicDamageIsImportantWithoutBeingACriticalOrKnockout()
    {
        var db = Content();
        db.Fields["log_regen"].Heal = 0;
        db.Fields["log_regen"].Dot = 0.1;
        var b = Make(db);
        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());
        var damage = Assert.Single(b.Events, e => e.Kind == BattleEventKind.Damage);
        Assert.Equal(Node.TurnEnd, damage.Node);
        Assert.Equal(100, damage.Amount);
        Assert.DoesNotContain(b.Events, e => e.Kind == BattleEventKind.Crit || e.Kind == BattleEventKind.Faint);
        Assert.Contains(damage.LogIndex, Important(b));
    }

    [Fact]
    public void AwakenedFighterStillLosingItsSelectedActionRemainsAnImportantControlResult()
    {
        var db = Content();
        db.Statuses["log_control"].WakeOnHit = true;
        Skill(db, "log_setup", new EffectDef { At = "post", Code = "STATUS", Target = "self", Status = "log_control" });
        var b = Make(db);
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(3));
        Assert.NotNull(b.ControlOf(b.Active(1)));
        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(1));
        int skipped = Assert.Single(Enumerable.Range(0, b.Log.Count),
            i => b.Log[i].Text.EndsWith("刚回过神来，本回合来不及行动", StringComparison.Ordinal));
        Assert.Null(b.ControlOf(b.Active(1)));
        Assert.Contains(skipped, Important(b));
    }

    [Theory]
    [InlineData("【人设·甲】")]
    [InlineData("「舰长续费」：")]
    public void OrdinaryHealingDoesNotBecomeAPersonaOrFieldTriggerBecauseOfTheFightersName(string name)
    {
        var db = Content();
        db.Fields["log_regen"].Name = "舰长续费";
        db.Streamer(Teams[0][0]).Name = name;
        Skill(db, "log_setup", new EffectDef { At = "post", Code = "HEAL", Target = "self", Frac = 0.1 });
        var b = Make(db);
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(1));
        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());
        var heal = Assert.Single(b.Events, e => e.Kind == BattleEventKind.Heal);
        Assert.Equal(10, heal.Amount);
        Assert.Equal(name + " 人气 +10", b.Log[heal.LogIndex].Text);
        Assert.DoesNotContain(heal.LogIndex, Important(b));
    }

    [Fact]
    public void OrdinaryPoweredDamageDoesNotBecomeReflectionBecauseOfTheFightersName()
    {
        var db = Content();
        db.Fields["mirror"].Name = "镜面回击";
        db.Streamer(Teams[1][0]).Name = "「镜面回击」反弹：甲";
        var b = NonCriticalHit(db);
        var damage = Assert.Single(b.Events, e => e.Kind == BattleEventKind.Damage);
        Assert.StartsWith("「镜面回击」反弹：", b.Log[damage.LogIndex].Text);
        Assert.DoesNotContain(damage.LogIndex, Important(b));
    }

    [Theory]
    [InlineData(0.2, "1,4", true)]
    [InlineData(0.0, "1", false)]
    public void ActualMarkPowerRecognizesCommaDecimalCultureAndIgnoresIdentityMultiplier(double per, string printedMultiplier, bool included)
    {
        var db = Content();
        Skill(db, "log_setup", new EffectDef { At = "post", Code = "ADD_MARK", Target = "self", Field = "log_mark", N = 2 });
        db.Skills["log_hit"].Effects.Add(new EffectDef { At = "pre", Code = "POWER_PER_MARK", Target = "self", Field = "log_mark", Per = per, Consume = true });
        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var b = Make(db);
            b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());
            b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            int check = Assert.Single(Enumerable.Range(0, b.Log.Count),
                i => b.Log[i].Text.StartsWith("〔命中前判定〕测试印记", StringComparison.Ordinal));
            Assert.EndsWith("2 层 → 威力 ×" + printedMultiplier, b.Log[check].Text);
            Assert.Equal(included, Important(b).Contains(check));
            Assert.Equal(Json(LogFilter.GroupByTurn(b)), Json(LogFilter.GroupByTurn(Replay.Run(db, b.Record))));
        }
        finally { CultureInfo.CurrentCulture = original; CultureInfo.CurrentUICulture = originalUi; }
    }

    [Fact]
    public void KnockoutReplacementAndEndAreImportantWithoutInventingAKnockoutSource()
    {
        var b = Make();
        b.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());
        Assert.Equal(Phase.Replace, b.Phase);
        b.Replace(1, 2);
        b.Surrender(0);
        var expectedEvents = b.Events.Where(e => e.Kind == BattleEventKind.Faint || e.Kind == BattleEventKind.Switch ||
            e.Kind == BattleEventKind.Enter && e.Node == Node.Replace || e.Kind == BattleEventKind.End).ToArray();
        Assert.Equal(4, expectedEvents.Length);
        var important = Important(b);
        Assert.All(expectedEvents, e => Assert.Contains(e.LogIndex, important));
        Assert.Equal(important.OrderBy(i => i).Distinct(), important);
        var groups = LogFilter.GroupByTurn(b);
        Assert.All(groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Summary)));
        var faint = Assert.Single(expectedEvents, e => e.Kind == BattleEventKind.Faint);
        Assert.Equal("第1回合：" + b.Log[faint.LogIndex].Text, groups[0].Summary);
    }

    [Fact]
    public void ActualBattleAndReplayHaveIdenticalFiltersAndGroupsWithoutConsumingRng()
    {
        var b = Make();
        b.ResolveTurn(BattleAction.Skill(2), BattleAction.Skill(1));
        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Skill(1));
        b.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
        b.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());
        b.Replace(1, 1);
        b.Surrender(1);
        var replay = Replay.Run(b.Db, BattleRecord.FromJson(b.Record.ToJson()));
        var clone = b.Clone();
        string before = Snapshot(b), replayBefore = Snapshot(replay);
        var queries = new[]
        {
            null, new LogQuery { Turn = 2 }, new LogQuery { Side = 1 },
            new LogQuery { OnlyImportant = true },
            new LogQuery { Turn = 2, Side = 0, Text = "测试", OnlyImportant = true },
            new LogQuery { Kinds = new HashSet<string> { "dmg", "heal", "status" }, Text = "人气" }
        };
        foreach (var q in queries) Assert.Equal(LogFilter.Apply(b, q), LogFilter.Apply(replay, q));
        Assert.Equal(Json(LogFilter.GroupByTurn(b)), Json(LogFilter.GroupByTurn(replay)));
        Assert.Equal(before, Snapshot(b));
        Assert.Equal(replayBefore, Snapshot(replay));
        Assert.Equal(clone.Random(), b.Random());
        Assert.Equal(clone.StateHash(), b.StateHash());
    }

    [Fact]
    public void ThousandLineSearchCompletesWithinFiveMillisecondsAfterWarmup()
    {
        var b = Make(blank: true);
        for (int i = 0; i < 1000; i++)
            b.Log.Add(Row(i / 20 + 1, i % 2, "dmg", (i % 2 == 0 ? "ＡLPHA" : "ＢETA") + " 人气变化记录 " + i));
        var q = new LogQuery { Text = "alpha" };
        for (int i = 0; i < 20; i++) Assert.Equal(500, LogFilter.Apply(b, q).Count);
        const int repeats = 200;
        int matches = 0;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < repeats; i++) matches += LogFilter.Apply(b, q).Count;
        clock.Stop();
        double milliseconds = clock.Elapsed.TotalMilliseconds / repeats;
        output.WriteLine("1000 行完整文字搜索，预热后 {0} 次平均 {1:F4} ms。", repeats, milliseconds);
        Assert.Equal(500 * repeats, matches);
        Assert.True(milliseconds < 5, "1000 行搜索平均耗时 " + milliseconds.ToString("F4", CultureInfo.InvariantCulture) + " ms，须小于 5 ms。");
    }
}
