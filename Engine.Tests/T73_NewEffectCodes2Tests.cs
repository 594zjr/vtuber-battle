using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T73_NewEffectCodes2Tests
{
    static object Effect(string code, string target = "foe", int n = 0, int turns = -1, string at = "post")
    {
        return new { code, target, n, turns, at };
    }

    static object Stage(string target, params int[] changes)
    {
        return new
        {
            code = "STAGE", at = "post", target,
            changes = ContentDb.StatKeys.Select((key, index) => new { key, value = changes[index] }).ToDictionary(p => p.key, p => p.value)
        };
    }

    static object Skill(params object[] effects)
    {
        return new { name = "包内演示技能", type = "talk", cat = "status", power = 0, pp = 10, prio = 5, effects };
    }

    static ContentDb Load(params (string id, object definition)[] replacements)
    {
        return ContentDb.FromJson(RulesJson(), PackJson(replacements));
    }

    static string RulesJson()
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", "StreamDuelRules.json"));
    }

    static string WithMember(string json, string key, object value)
    {
        var members = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
        members[key] = value;
        return JsonSerializer.Serialize(members);
    }

    static string PackJson(params (string id, object definition)[] replacements)
    {
        var skills = new Dictionary<string, object>
        {
            ["pk_swap"] = Skill(Effect("STAGE_SWAP")),
            ["pk_copy"] = Skill(Effect("STAGE_COPY")),
            ["pk_restore"] = Skill(Effect("PP_RESTORE", "self", n: 4)),
            ["pk_delay"] = Skill(Effect("DELAYED_DAMAGE", n: 37, turns: 2)),
            ["pk_prepare"] = Skill(Stage("self", 5, -2, 6, 0, -1), Stage("foe", 2, 4, 0, -5, 1))
        };
        foreach (var replacement in replacements) skills[replacement.id] = replacement.definition;
        var pack = new
        {
            id = "t73_demo", name = "四种效果码演示包", notice = "仅供规则测试使用。",
            skills,
            personas = new Dictionary<string, object>
            {
                ["pk_neutral"] = new { name = "测试人设", code = "T_LOWHP_POWER", @params = new { below = 0, mul = 1 } },
                ["pk_on_hit"] = new { name = "受击强化测试", code = "T_STAGE_ON_HIT", @params = new { def = 2 } }
            },
            streamers = Enumerable.Range(0, 6).Select(index => new
            {
                id = "p" + index, name = "测试主播" + index, type = "talk", persona = "pk_neutral",
                stats = new { hp = 300, atk = 80, def = 80, spa = 80, sdef = 80, spd = 120 - index * 10 },
                skills = new[] { "pk_swap", "pk_copy", "pk_restore", "pk_delay" }, fifth = "pk_prepare"
            }).ToArray()
        };
        return JsonSerializer.Serialize(pack);
    }

    static Battle Make(ContentDb db = null, bool capped = false)
    {
        return new Battle(db ?? Load(), new IList<string>[]
        {
            new[] { "p0", "p1", "p2", "p3", "p4", "p5" },
            new[] { "p1", "p2", "p3", "p4", "p5", "p0" }
        }, new[] { "甲方", "乙方" }, new BattleOptions
        {
            Seed = 73, Modifiers = capped ? new List<string> { "stage_cap_3" } : null
        });
    }

    static int[] Stages(Fighter fighter) { return ContentDb.StatKeys.Select(key => fighter.Stages[key]).ToArray(); }
    static BattleEvent[] Since(Battle b, int count, BattleEventKind kind)
    {
        return b.Events.Skip(count).Where(e => e.Kind == kind).ToArray();
    }

    static void AssertLink(Battle b, BattleEvent entry, string node, int turn)
    {
        Assert.Equal(node, entry.Node);
        Assert.Equal(turn, entry.Turn);
        Assert.InRange(entry.LogIndex, 0, b.Log.Count - 1);
        Assert.Equal(node, b.Log[entry.LogIndex].Node);
        Assert.Equal(turn, b.Log[entry.LogIndex].Turn);
    }

    static void AssertStageDeltas(Battle b, BattleEvent[] events, int side, int[] expected, int turn)
    {
        var own = events.Where(e => e.Side == side).ToArray();
        Assert.Equal(expected.Count(n => n != 0), own.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            if (expected[index] == 0) continue;
            var change = Assert.Single(own, e => e.Id == ContentDb.StatKeys[index]);
            Assert.Equal(expected[index], change.Amount);
            Assert.Equal(0, change.Slot);
            AssertLink(b, change, Node.First, turn);
        }
    }

    static string Events(Battle b)
    {
        return JsonSerializer.Serialize(b.Events, new JsonSerializerOptions { IncludeFields = true });
    }

    static string Logs(Battle b)
    {
        return JsonSerializer.Serialize(b.Log, new JsonSerializerOptions { IncludeFields = true });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwapExchangesAllFiveSignedStagesAndEmitsActualDeltas(bool capped)
    {
        var b = Make(capped: capped);
        b.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());
        int[] ownBefore = Stages(b.Active(0)), foeBefore = Stages(b.Active(1));
        Assert.Equal(capped ? new[] { 3, -2, 3, 0, -1 } : new[] { 5, -2, 6, 0, -1 }, ownBefore);
        Assert.Equal(capped ? new[] { 2, 3, 0, -3, 1 } : new[] { 2, 4, 0, -5, 1 }, foeBefore);
        int from = b.Events.Count;

        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(foeBefore, Stages(b.Active(0)));
        Assert.Equal(ownBefore, Stages(b.Active(1)));
        var changes = Since(b, from, BattleEventKind.StageChange);
        AssertStageDeltas(b, changes, 0, capped ? new[] { -1, 5, -3, -3, 2 } : new[] { -3, 6, -6, -5, 2 }, 2);
        AssertStageDeltas(b, changes, 1, capped ? new[] { 1, -5, 3, 3, -2 } : new[] { 3, -6, 6, 5, -2 }, 2);
        Assert.All(b.Sides.SelectMany(side => side.Team).SelectMany(f => f.Stages.Values), stage => Assert.InRange(stage, capped ? -3 : -6, capped ? 3 : 6));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyAssignsPositiveLevelsExactlyAndKeepsOwnValuesForNonpositiveTargets(bool capped)
    {
        var b = Make(capped: capped);
        b.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());
        int[] foeBefore = Stages(b.Active(1));
        int from = b.Events.Count;

        b.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());

        Assert.Equal(capped ? new[] { 2, 3, 3, 0, 1 } : new[] { 2, 4, 6, 0, 1 }, Stages(b.Active(0)));
        Assert.Equal(foeBefore, Stages(b.Active(1)));
        var changes = Since(b, from, BattleEventKind.StageChange);
        AssertStageDeltas(b, changes, 0, capped ? new[] { -1, 5, 0, 0, 2 } : new[] { -3, 6, 0, 0, 2 }, 2);
        Assert.DoesNotContain(changes, e => e.Side == 1);
    }

    [Theory]
    [InlineData("STAGE_SWAP", 0)]
    [InlineData("STAGE_COPY", 1)]
    public void UsingTheStageEffectOnSelfDoesNotInventAnyStageChange(string code, int slot)
    {
        string id = slot == 0 ? "pk_swap" : "pk_copy";
        var b = Make(Load((id, Skill(Effect(code, "self")))));
        b.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());
        int[] ownBefore = Stages(b.Active(0)), foeBefore = Stages(b.Active(1));
        int from = b.Events.Count;

        b.ResolveTurn(BattleAction.Skill(slot), BattleAction.Pass());

        Assert.Equal(ownBefore, Stages(b.Active(0)));
        Assert.Equal(foeBefore, Stages(b.Active(1)));
        Assert.Empty(Since(b, from, BattleEventKind.StageChange));
    }

    [Theory]
    [InlineData("STAGE_SWAP", 0)]
    [InlineData("STAGE_COPY", 1)]
    public void StageEffectAfterKnockingOutItsTargetDoesNotCopyOrSwapTheFaintedFighter(string code, int slot)
    {
        string id = slot == 0 ? "pk_swap" : "pk_copy";
        var b = Make(Load((id, Skill(Effect("FIXED_DAMAGE", n: 99999), Effect(code)))));
        b.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());
        int[] ownBefore = Stages(b.Active(0)), foeBefore = Stages(b.Active(1));
        int from = b.Events.Count;

        b.ResolveTurn(BattleAction.Skill(slot), BattleAction.Pass());

        Assert.Equal(Phase.Replace, b.Phase);
        Assert.True(b.Active(1).Fainted);
        Assert.Equal(ownBefore, Stages(b.Active(0)));
        Assert.Equal(foeBefore, Stages(b.Active(1)));
        Assert.Empty(Since(b, from, BattleEventKind.StageChange));
    }

    [Fact]
    public void PpRestoreUsesActualCappedAmountsAndNeverRestoresTheFifthSkill()
    {
        var b = Make();
        b.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());
        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        for (int i = 0; i < 4; i++) b.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        var fighter = b.Active(0);
        Assert.Equal(new[] { 9, 6, 10, 10, 9 }, fighter.Skills.Select(s => s.Pp));
        int from = b.Events.Count, turn = b.Turn;

        b.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());

        Assert.Equal(new[] { 10, 10, 10, 10, 9 }, fighter.Skills.Select(s => s.Pp));
        var restored = Since(b, from, BattleEventKind.PpChange).Where(e => e.Amount > 0).ToArray();
        Assert.Equal(3, restored.Length);
        Assert.Equal(1, Assert.Single(restored, e => e.Id == "pk_swap").Amount);
        Assert.Equal(4, Assert.Single(restored, e => e.Id == "pk_copy").Amount);
        Assert.Equal(1, Assert.Single(restored, e => e.Id == "pk_restore").Amount);
        Assert.All(restored, e => { Assert.Equal(0, e.Side); Assert.Equal(0, e.Slot); AssertLink(b, e, Node.First, turn); });
        Assert.DoesNotContain(restored, e => e.Id == "pk_delay" || e.Id == "pk_prepare");
    }

    [Fact]
    public void PpRestoreAtFullPpOnlyReturnsItsOwnUsageCost()
    {
        var b = Make();
        int from = b.Events.Count;

        b.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());

        Assert.All(b.Active(0).Skills, slot => Assert.Equal(slot.Max, slot.Pp));
        var changes = Since(b, from, BattleEventKind.PpChange);
        Assert.Equal(new[] { -1, 1 }, changes.Select(e => e.Amount));
        Assert.All(changes, e => { Assert.Equal("pk_restore", e.Id); Assert.Equal(0, e.Side); AssertLink(b, e, Node.First, 1); });
    }

    [Theory]
    [InlineData(1, "foe", 1)]
    [InlineData(2, "foe", 1)]
    [InlineData(1, "self", 0)]
    public void DelayCountsTheApplicationTurnAndRemovesItselfBeforeDamage(int turns, string target, int side)
    {
        var b = Make(Load(("pk_delay", Skill(Effect("DELAYED_DAMAGE", target, n: 37, turns: turns)))));
        Assert.Equal("turn", b.Db.Fields["delayed_damage"].Kind);
        Assert.True(b.Db.Fields["delayed_damage"].Dispel);

        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());

        var added = Assert.Single(b.Events, e => e.Kind == BattleEventKind.FieldAdd && e.Id == "delayed_damage");
        Assert.Equal(side, added.Side);
        Assert.Equal(0, added.Slot);
        Assert.Equal(turns, added.Amount);
        AssertLink(b, added, Node.First, 1);
        if (turns == 2)
        {
            var field = Assert.Single(b.Active(side).Fields);
            Assert.Equal(1, field.Turns);
            Assert.Equal(37, field.PendingDamage);
            Assert.Equal(300, b.Active(side).Hp);
            b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        }

        Assert.Null(b.Active(side).FieldOf("delayed_damage"));
        Assert.Equal(263, b.Active(side).Hp);
        var removed = Assert.Single(b.Events, e => e.Kind == BattleEventKind.FieldRemove && e.Id == "delayed_damage");
        var damage = Assert.Single(b.Events, e => e.Kind == BattleEventKind.Damage);
        Assert.Equal(side, removed.Side);
        Assert.Equal(side, damage.Side);
        Assert.Equal(0, removed.Amount);
        Assert.Equal(37, damage.Amount);
        Assert.Equal(0, damage.Slot);
        AssertLink(b, removed, Node.TurnEnd, turns);
        AssertLink(b, damage, Node.TurnEnd, turns);
        Assert.True(removed.LogIndex < damage.LogIndex);
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(99999, 0)]
    public void DelayZeroAndOverkillReportOnlyActualDamage(int amount, int hp)
    {
        var b = Make(Load(("pk_delay", Skill(Effect("DELAYED_DAMAGE", n: amount, turns: 1)))));

        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());

        Assert.Equal(hp, b.Active(1).Hp);
        Assert.Null(b.Active(1).FieldOf("delayed_damage"));
        var damage = b.Events.Where(e => e.Kind == BattleEventKind.Damage).ToArray();
        if (amount == 0)
        {
            Assert.Empty(damage);
            Assert.Equal(Phase.Choose, b.Phase);
        }
        else
        {
            Assert.Equal(300, Assert.Single(damage).Amount);
            AssertLink(b, damage[0], Node.TurnEnd, 1);
            Assert.Equal(Phase.Replace, b.Phase);
            Assert.True(b.Active(1).Fainted);
            Assert.Single(b.Events, e => e.Kind == BattleEventKind.Faint && e.Side == 1);
        }
    }

    [Fact]
    public void DelayIsFixedDamageThatWakesAfkWithoutReflectionOrOnHitStages()
    {
        var db = Load(("pk_delay", Skill(
            new { code = "FIELD", at = "pre", target = "foe", field = "mirror", turns = 5 },
            new { code = "FIELD", at = "pre", target = "foe", field = "shield", turns = 5 },
            new { code = "STATUS", at = "pre", target = "foe", status = "afk" },
            Effect("DELAYED_DAMAGE", n: 37, turns: 1))));
        db.Streamer("p1").Persona = "pk_on_hit";
        var b = Make(db);

        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());

        Assert.Equal(300, b.Active(0).Hp);
        Assert.Equal(263, b.Active(1).Hp);
        Assert.Equal(new[] { 0, 0, 0, 0, 0 }, Stages(b.Active(1)));
        Assert.DoesNotContain(b.Active(1).Statuses, s => s.Id == "afk");
        Assert.NotNull(b.Active(1).FieldOf("mirror"));
        Assert.NotNull(b.Active(1).FieldOf("shield"));
        Assert.DoesNotContain(b.Events, e => e.Kind == BattleEventKind.StageChange || e.Kind == BattleEventKind.Crit);
        var damage = Assert.Single(b.Events, e => e.Kind == BattleEventKind.Damage);
        Assert.Equal(1, damage.Side);
        Assert.Equal(37, damage.Amount);
        var woke = Assert.Single(b.Events, e => e.Kind == BattleEventKind.StatusRemove && e.Id == "afk");
        AssertLink(b, woke, Node.TurnEnd, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DispelOrSwitchCancelsThePendingDamage(bool switchOut)
    {
        var b = Make(Load(("pk_swap", Skill(Effect("DISPEL_TURNS"))),
            ("pk_delay", Skill(Effect("DELAYED_DAMAGE", n: 37, turns: 3)))));
        var original = b.Active(1);
        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());
        Assert.NotNull(original.FieldOf("delayed_damage"));
        int from = b.Events.Count;

        b.ResolveTurn(switchOut ? BattleAction.Pass() : BattleAction.Skill(0),
            switchOut ? BattleAction.SwitchTo(1) : BattleAction.Pass());

        Assert.Null(original.FieldOf("delayed_damage"));
        var removed = Assert.Single(Since(b, from, BattleEventKind.FieldRemove), e => e.Id == "delayed_damage");
        Assert.Equal(1, removed.Side);
        Assert.Equal(0, removed.Slot);
        AssertLink(b, removed, switchOut ? Node.Switch : Node.First, 2);
        for (int i = 0; i < 3; i++) b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(300, original.Hp);
        Assert.Equal(300, b.Active(1).Hp);
        Assert.DoesNotContain(b.Events, e => e.Kind == BattleEventKind.Damage);
    }

    [Fact]
    public void EarlyKnockoutClearsThePendingEffectBeforeReplacement()
    {
        var b = Make(Load(("pk_swap", Skill(Effect("FIXED_DAMAGE", n: 99999)))));
        var original = b.Active(1);
        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());
        int from = b.Events.Count;

        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(Phase.Replace, b.Phase);
        Assert.True(original.Fainted);
        Assert.Null(original.FieldOf("delayed_damage"));
        var removed = Assert.Single(Since(b, from, BattleEventKind.FieldRemove), e => e.Id == "delayed_damage");
        Assert.Equal(1, removed.Side);
        Assert.Equal(0, removed.Slot);
        AssertLink(b, removed, Node.First, 2);
        b.Replace(1, 1);
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Null(original.FieldOf("delayed_damage"));
        Assert.Equal(300, b.Active(1).Hp);
        Assert.Single(b.Events, e => e.Kind == BattleEventKind.Damage);
    }

    [Fact]
    public void RefreshReplacesBothTheRemainingTimeAndDamageWithoutStacking()
    {
        var b = Make(Load(("pk_swap", Skill(Effect("DELAYED_DAMAGE", n: 13, turns: 3)))));
        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());

        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        var field = Assert.Single(b.Active(1).Fields);
        Assert.Equal("delayed_damage", field.Id);
        Assert.Equal(2, field.Turns);
        Assert.Equal(13, field.PendingDamage);
        Assert.Equal(300, b.Active(1).Hp);
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(1, field.Turns);
        Assert.Equal(300, b.Active(1).Hp);
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        Assert.Null(b.Active(1).FieldOf("delayed_damage"));
        Assert.Equal(287, b.Active(1).Hp);
        Assert.Equal(new[] { 2, 3 }, b.Events.Where(e => e.Kind == BattleEventKind.FieldAdd && e.Id == "delayed_damage").Select(e => e.Amount));
        var damage = Assert.Single(b.Events, e => e.Kind == BattleEventKind.Damage);
        Assert.Equal(13, damage.Amount);
        AssertLink(b, damage, Node.TurnEnd, 4);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void InvalidDelayParametersAreRejectedWhenLoadingThePack(int turns, int amount)
    {
        Assert.Throws<KeyError>(() => Load(("pk_delay", Skill(Effect("DELAYED_DAMAGE", n: amount, turns: turns)))));
    }

    [Theory]
    [InlineData("turns", 1.5)]
    [InlineData("n", 0.5)]
    [InlineData("turns", 2147483648d)]
    [InlineData("n", 2147483648d)]
    [InlineData("n", "37")]
    [InlineData("turns", null)]
    public void DelayParametersMustBeIntegersWithoutCoercionOrOverflow(string parameter, object value)
    {
        var effect = new Dictionary<string, object>
        {
            ["code"] = "DELAYED_DAMAGE", ["at"] = "post", ["target"] = "foe", ["turns"] = 2, ["n"] = 37
        };
        effect[parameter] = value;

        Assert.Throws<KeyError>(() => Load(("pk_delay", Skill(effect))));
    }

    [Theory]
    [InlineData("definition")]
    [InlineData("effect")]
    [InlineData("persona")]
    [InlineData("struggle")]
    public void ReservedDelayFieldCannotBeDeclaredOrReferencedThroughOtherEntrances(string entrance)
    {
        string rules = RulesJson(), pack = PackJson(("pk_delay", Skill()));
        var fieldEffect = new { code = "FIELD", at = "post", target = "self", field = "delayed_damage", turns = 2 };
        if (entrance == "definition")
            pack = WithMember(pack, "fields", new Dictionary<string, object>
            {
                ["delayed_damage"] = new { name = "覆盖保留效果", kind = "turn", dispel = true }
            });
        else if (entrance == "effect")
            pack = PackJson(("pk_delay", Skill()), ("pk_swap", Skill(fieldEffect)));
        else if (entrance == "persona")
            pack = WithMember(pack, "personas", new Dictionary<string, object>
            {
                ["pk_neutral"] = new { name = "测试人设", code = "T_ENTER_FIELD", field = "delayed_damage" }
            });
        else
            rules = WithMember(rules, "struggle", Skill(fieldEffect));

        var error = Assert.Throws<KeyError>(() => ContentDb.FromJson(rules, pack));
        Assert.Contains("保留", error.Message);
    }

    [Theory]
    [InlineData("STAGE_SWAP")]
    [InlineData("STAGE_COPY")]
    [InlineData("PP_RESTORE")]
    [InlineData("DELAYED_DAMAGE")]
    public void NewEffectsAlsoRequireTimingWhenUsedByStruggle(string code)
    {
        string rules = WithMember(RulesJson(), "struggle", Skill(Effect(code, n: 37, turns: 2, at: null)));

        var error = Assert.Throws<KeyError>(() => ContentDb.FromJson(rules, PackJson(("pk_delay", Skill()))));
        Assert.Contains("at", error.Message);
    }

    [Fact]
    public void DelayInStruggleAloneRegistersItsFieldAndIsRevalidated()
    {
        string rules = WithMember(RulesJson(), "struggle", Skill(Effect("DELAYED_DAMAGE", n: 37, turns: 2)));
        var db = ContentDb.FromJson(rules, PackJson(("pk_delay", Skill())));

        Assert.True(db.Fields["delayed_damage"].Dispel);
        Assert.Equal("turn", db.Fields["delayed_damage"].Kind);
        db.Struggle.Effects.Single().Turns = 0;
        Assert.Throws<KeyError>(() => db.Validate());
    }

    [Fact]
    public void AllFourEffectsAndPendingDamageSurviveCloneHashAndReplay()
    {
        var b = Make();
        foreach (int slot in new[] { 4, 0, 1, 2, 3 }) b.ResolveTurn(BattleAction.Skill(slot), BattleAction.Pass());
        var original = b.Active(1).FieldOf("delayed_damage");
        Assert.NotNull(original);
        var copy = b.Clone();
        var copied = copy.Active(1).FieldOf("delayed_damage");
        Assert.NotSame(original, copied);
        Assert.Equal(37, copied.PendingDamage);
        Assert.Equal(b.StateHash(), copy.StateHash());
        Assert.Equal(Events(b), Events(copy));
        Assert.Equal(Logs(b), Logs(copy));
        Assert.Equal(b.Record.ToJson(), copy.Record.ToJson());

        copied.PendingDamage++;
        Assert.NotEqual(b.StateHash(), copy.StateHash());
        Assert.Equal(37, original.PendingDamage);
        copied.PendingDamage--;
        Assert.Equal(b.StateHash(), copy.StateHash());
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        copy.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        var replay = Replay.Run(b.Db, BattleRecord.FromJson(b.Record.ToJson()));

        foreach (var equivalent in new[] { copy, replay })
        {
            Assert.Equal(b.StateHash(), equivalent.StateHash());
            Assert.Equal(Events(b), Events(equivalent));
            Assert.Equal(Logs(b), Logs(equivalent));
            Assert.Equal(b.Record.ToJson(), equivalent.Record.ToJson());
            Assert.Equal(b.Clone().Random(), equivalent.Clone().Random());
        }
    }

    [Theory]
    [InlineData("pk_swap")]
    [InlineData("pk_copy")]
    [InlineData("pk_restore")]
    [InlineData("pk_delay")]
    public void NewEffectsDescribeTheirTimingAndDispelRules(string skillId)
    {
        var db = Load();
        var effect = db.Skills[skillId].Effects.Single();
        var post = Describe.Effect(db, effect);
        effect.At = "pre";
        var pre = Describe.Effect(db, effect);

        Assert.False(string.IsNullOrWhiteSpace(post.Text));
        Assert.False(string.IsNullOrWhiteSpace(post.Dispel));
        Assert.False(string.IsNullOrWhiteSpace(pre.At));
        Assert.NotEqual(pre.At, post.At);
        Assert.Equal(post.Dispel, pre.Dispel);
        Assert.DoesNotContain("未知", post.Text);
        Assert.DoesNotContain("describe.", post.Text + post.Dispel + post.At);
    }
}
