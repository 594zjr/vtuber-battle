using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T40_BattleEventsEdgeTests
{
    static readonly string[][] Teams =
    {
        new[] { "kei", "enka", "shiori", "lumi", "hane", "momo" },
        new[] { "bell", "yuan", "nyako", "buck", "kasumi", "bee" }
    };

    static ContentDb Load()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["event_neutral"] = new PersonaDef { Id = "event_neutral", Name = "测试人设", Code = "T_LOWHP_POWER" };
        Skill(db, "event_hit").Power = 1;
        db.Skills["event_hit"].Cat = "interact";
        Skill(db, "event_miss").Acc = 0;
        Skill(db, "event_heal", Effect("HEAL", "self", frac: 1));
        Skill(db, "event_setup");
        Skill(db, "event_fifth");
        foreach (var definition in db.Streamers)
        {
            definition.Persona = "event_neutral";
            definition.Skills = new List<string> { "event_hit", "event_miss", "event_heal", "event_setup" };
            definition.Fifth = "event_fifth";
        }
        for (int side = 0; side < Teams.Length; side++)
            for (int slot = 0; slot < Teams[side].Length; slot++)
                db.Streamer(Teams[side][slot]).Stats["spd"] = 200 - side * 100 - slot;
        return db;
    }

    static SkillDef Skill(ContentDb db, string id, params EffectDef[] effects)
    {
        var skill = new SkillDef { Id = id, Name = "测试「" + id + "」", Type = "talk", Cat = "status", Pp = 20, Prio = 5 };
        skill.Effects.AddRange(effects);
        db.Skills[id] = skill;
        return skill;
    }

    static EffectDef Effect(string code, string target = "foe", int n = 0, double frac = 0)
    {
        return new EffectDef { Code = code, At = "post", Target = target, N = n, Frac = frac };
    }

    static void Persona(ContentDb db, string fighter, string code, string parameter = null, double value = 0)
    {
        string id = "event_" + code;
        var persona = new PersonaDef { Id = id, Name = "测试人设", Code = code };
        if (parameter != null) persona.Params[parameter] = value;
        db.Personas[id] = persona;
        db.Streamer(fighter).Persona = id;
    }

    static Battle Make(ContentDb db = null, uint seed = 40, bool openingChoices = false)
    {
        return new Battle(db ?? Load(), Teams.Select(team => (IList<string>)team.ToArray()).ToArray(),
            new[] { "甲方", "乙方" }, new BattleOptions { Seed = seed, BanOne = openingChoices, LeadPick = openingChoices });
    }

    static BattleEvent[] Events(Battle battle, BattleEventKind kind, int side, int slot, string id = null)
    {
        return battle.Events.Where(e => e.Kind == kind && e.Side == side && e.Slot == slot && (id == null || e.Id == id)).ToArray();
    }

    static string EventDump(Battle battle)
    {
        return string.Join("\n", battle.Events.Select(e => $"{e.LogIndex}|{e.Turn}|{e.Node}|{e.Kind}|{e.Side}|{e.Slot}|{e.Id}|{e.Amount}"));
    }

    static string LogDump(Battle battle)
    {
        return string.Join("\n", battle.Log.Select(e => $"{e.Turn}|{e.Node}|{e.Kind}|{e.Side}|{e.Text}|" +
            string.Join(",", e.SnapActive) + "|" + string.Join("/", e.SnapHp.Select(hp => string.Join(",", hp)))));
    }

    static void AssertLinks(Battle battle)
    {
        int previous = -1;
        foreach (var e in battle.Events)
        {
            Assert.InRange(e.LogIndex, 0, battle.Log.Count - 1);
            Assert.True(e.LogIndex >= previous);
            previous = e.LogIndex;
            Assert.Equal(battle.Log[e.LogIndex].Turn, e.Turn);
            Assert.Equal(battle.Log[e.LogIndex].Node, e.Node);
            if (e.Kind == BattleEventKind.Damage || e.Kind == BattleEventKind.Heal) Assert.True(e.Amount > 0);
            if (e.Kind == BattleEventKind.StatusRemove || e.Kind == BattleEventKind.FieldRemove) Assert.Equal(0, e.Amount);
        }
    }

    [Fact]
    public void OverkillAndHealingAtTheCapReportOnlyActualHpChanges()
    {
        var db = Load();
        Skill(db, "event_setup", Effect("FIXED_DAMAGE", "self", 7), Effect("HEAL", "self", frac: 1),
            Effect("HEAL", "self", frac: 1), Effect("FIXED_DAMAGE", n: 99999), Effect("FIXED_DAMAGE", n: 99999));
        var b = Make(db);
        int maximum = b.Active(1).MaxHp;

        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());

        Assert.Equal(7, Assert.Single(Events(b, BattleEventKind.Damage, 0, 0)).Amount);
        Assert.Equal(7, Assert.Single(Events(b, BattleEventKind.Heal, 0, 0)).Amount);
        Assert.Equal(maximum, Assert.Single(Events(b, BattleEventKind.Damage, 1, 0)).Amount);
        Assert.Single(Events(b, BattleEventKind.Faint, 1, 0));
        Assert.Equal(0, b.Active(1).Hp);
        Assert.Equal(b.Active(0).MaxHp, b.Active(0).Hp);
        AssertLinks(b);
    }

    [Fact]
    public void EndureReportsTheReducedDamageThenOnlyOneFaintOnTheNextHit()
    {
        var db = Load();
        Persona(db, "bell", "T_ENDURE");
        Skill(db, "event_setup", Effect("FIXED_DAMAGE", n: 99999));
        var b = Make(db);
        int maximum = b.Active(1).MaxHp;

        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());
        Assert.Equal(1, b.Active(1).Hp);
        Assert.Empty(Events(b, BattleEventKind.Faint, 1, 0));
        Assert.Equal(maximum - 1, Assert.Single(Events(b, BattleEventKind.Damage, 1, 0)).Amount);

        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());
        Assert.Equal(new[] { maximum - 1, 1 }, Events(b, BattleEventKind.Damage, 1, 0).Select(e => e.Amount));
        Assert.Single(Events(b, BattleEventKind.Faint, 1, 0));
        AssertLinks(b);
    }

    [Theory]
    [InlineData("recoil")]
    [InlineData("reflection")]
    [InlineData("dot")]
    public void IndirectDamageIsAttributedToTheRecipientAndItsActualNode(string mode)
    {
        var db = Load();
        if (mode == "recoil") db.Skills["event_hit"].Effects.Add(Effect("RECOIL", "self", frac: 0.5));
        var b = Make(db);
        if (mode == "reflection") b.Active(1).Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        if (mode == "dot") b.Active(0).Statuses.Add(new StatusInst { Id = "hater", Left = 2 });
        int hp0 = b.Active(0).Hp, hp1 = b.Active(1).Hp;

        b.ResolveTurn(mode == "dot" ? BattleAction.Pass() : BattleAction.Skill(0), BattleAction.Pass());

        var damage = Assert.Single(Events(b, BattleEventKind.Damage, 0, 0));
        Assert.Equal(hp0 - b.Active(0).Hp, damage.Amount);
        Assert.Equal(mode == "dot" ? Node.TurnEnd : Node.First, damage.Node);
        Assert.Equal(hp1 - b.Active(1).Hp, Events(b, BattleEventKind.Damage, 1, 0).Sum(e => e.Amount));
        AssertLinks(b);
    }

    [Fact]
    public void SkillCostAndPpReductionUseSignedActualDeltasAndFreePpDoesNotInventACost()
    {
        var db = Load();
        Skill(db, "event_setup", Effect("PP_REDUCE", "self", 99));
        var b = Make(db);
        b.Active(0).Skills[3].Pp = 3;

        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());

        Assert.Equal(new[] { -1, -2 }, Events(b, BattleEventKind.PpChange, 0, 0, "event_setup").Select(e => e.Amount));
        Assert.Equal(0, b.Active(0).Skills[3].Pp);
        int count = b.Events.Count;
        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());
        Assert.DoesNotContain(b.Events.Skip(count), e => e.Kind == BattleEventKind.PpChange || e.Kind == BattleEventKind.Act);

        var freeDb = Load();
        Persona(freeDb, "kei", "T_FREE_PP_SLOT6");
        var free = Make(freeDb);
        free.Active(0).Skills[0].Pp = 0;
        free.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.Single(Events(free, BattleEventKind.Act, 0, 0, "event_hit"));
        Assert.Empty(Events(free, BattleEventKind.PpChange, 0, 0));
        AssertLinks(b);
        AssertLinks(free);
    }

    [Fact]
    public void AllyPpRestorationReportsEachLivingBenchRecipientWithItsOwnSlot()
    {
        var db = Load();
        Persona(db, "enka", "T_ALLY_PP_ON_ENTER", "n", 2);
        var b = Make(db);
        foreach (var fighter in b.Sides[0].Team)
            foreach (var skill in fighter.Skills) skill.Pp = skill.Max - 1;
        b.Sides[0].Team[2].Hp = 0;
        b.Sides[0].Team[2].Fainted = true;

        b.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());

        var changes = b.Events.Where(e => e.Kind == BattleEventKind.PpChange).ToArray();
        var recipients = b.Sides[0].Team.Where(f => f.Slot != 2 && !f.Fainted).ToArray();
        Assert.Equal(recipients.Sum(f => f.Skills.Count), changes.Length);
        foreach (var fighter in recipients)
            foreach (var skill in fighter.Skills)
            {
                var change = Assert.Single(Events(b, BattleEventKind.PpChange, 0, fighter.Slot - 1, skill.Id));
                Assert.Equal(1, change.Amount);
                Assert.Equal(Node.Switch, change.Node);
                Assert.Equal(skill.Max, skill.Pp);
            }
        AssertLinks(b);
    }

    [Fact]
    public void ControlExpiryAndCleanseIdentifyTheRemovedStatusesWithoutParsingText()
    {
        var db = Load();
        db.Statuses["event_control"] = new StatusDef { Id = "event_control", Name = "测试控制", Cat = "control", Skip = 2 };
        db.Statuses["event_weak"] = new StatusDef { Id = "event_weak", Name = "测试弱化", Cat = "weak", Turns = 5, Cleanse = true };
        Skill(db, "event_setup", new EffectDef { Code = "STATUS", At = "post", Target = "self", Status = "event_weak" },
            new EffectDef { Code = "STATUS", At = "post", Target = "foe", Status = "event_control" },
            new EffectDef { Code = "STATUS", At = "post", Target = "foe", Status = "event_control" });
        Skill(db, "event_heal", Effect("CLEANSE", "self"));
        var b = Make(db);

        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Skill(0));
        var added = Assert.Single(Events(b, BattleEventKind.StatusAdd, 1, 0, "event_control"), e => e.Node == Node.First);
        Assert.Equal(2, added.Amount);
        Assert.Equal(5, Assert.Single(Events(b, BattleEventKind.StatusAdd, 0, 0, "event_weak"), e => e.Node == Node.First).Amount);
        b.ResolveTurn(BattleAction.Skill(2), BattleAction.Skill(0));

        Assert.Single(Events(b, BattleEventKind.StatusRemove, 0, 0, "event_weak"));
        Assert.Single(Events(b, BattleEventKind.StatusRemove, 1, 0, "event_control"));
        Assert.Equal(2, Assert.Single(Events(b, BattleEventKind.FieldAdd, 1, 0, "reconnect")).Amount);
        Assert.Empty(Events(b, BattleEventKind.Act, 1, 0));
        Assert.Null(b.ControlOf(b.Active(1)));
        Assert.False(b.Active(0).HasStatus("event_weak"));
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Single(Events(b, BattleEventKind.FieldRemove, 1, 0, "reconnect"));
        AssertLinks(b);
    }

    [Fact]
    public void BeingHitRemovesSleepAfterItsDamageEvent()
    {
        var db = Load();
        db.Statuses["event_sleep"] = new StatusDef { Id = "event_sleep", Name = "测试挂机", Cat = "control", Skip = 3, WakeOnHit = true };
        Skill(db, "event_setup", new EffectDef { Code = "STATUS", At = "post", Target = "foe", Status = "event_sleep" }, Effect("FIXED_DAMAGE", n: 1));
        var b = Make(db);

        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());

        var added = Assert.Single(Events(b, BattleEventKind.StatusAdd, 1, 0, "event_sleep"));
        var damage = Assert.Single(Events(b, BattleEventKind.Damage, 1, 0));
        var removed = Assert.Single(Events(b, BattleEventKind.StatusRemove, 1, 0, "event_sleep"));
        Assert.Equal(3, added.Amount);
        Assert.True(added.LogIndex < damage.LogIndex && damage.LogIndex < removed.LogIndex);
        Assert.False(b.Active(1).HasStatus("event_sleep"));
        AssertLinks(b);
    }

    [Fact]
    public void DispelAndSwitchEmitEveryRemovalAndSignedStageResetWhileKeepingStatuses()
    {
        var db = Load();
        db.Fields["event_turn"] = new FieldDef { Id = "event_turn", Name = "测试回合", Kind = "turn", Dispel = true };
        db.Fields["event_mark"] = new FieldDef { Id = "event_mark", Name = "测试印记", Kind = "mark", MaxStacks = 3 };
        db.Statuses["event_weak"] = new StatusDef { Id = "event_weak", Name = "测试弱化", Cat = "weak", Turns = 5, Cleanse = true };
        Skill(db, "event_setup", new EffectDef { Code = "STAGE", At = "post", Target = "foe", Changes = new Dictionary<string, int> { { "atk", 99 }, { "def", -99 } } },
            new EffectDef { Code = "FIELD", At = "post", Target = "foe", Field = "event_turn", Turns = 4 },
            new EffectDef { Code = "ADD_MARK", At = "post", Target = "foe", Field = "event_mark", N = 99 },
            new EffectDef { Code = "STATUS", At = "post", Target = "foe", Status = "event_weak" });
        Skill(db, "event_heal", Effect("DISPEL_TURNS"), Effect("DISPEL_BOOST"));
        var b = Make(db);
        b.Active(1).Stages["atk"] = 5;
        b.Active(1).Stages["def"] = -5;

        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());
        Assert.Equal(1, Assert.Single(Events(b, BattleEventKind.StageChange, 1, 0, "atk")).Amount);
        Assert.Equal(-1, Assert.Single(Events(b, BattleEventKind.StageChange, 1, 0, "def")).Amount);
        Assert.Equal(4, Assert.Single(Events(b, BattleEventKind.FieldAdd, 1, 0, "event_turn")).Amount);
        Assert.Equal(3, Assert.Single(Events(b, BattleEventKind.FieldAdd, 1, 0, "event_mark")).Amount);
        b.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        Assert.Single(Events(b, BattleEventKind.FieldRemove, 1, 0, "event_turn"));
        Assert.Empty(Events(b, BattleEventKind.FieldRemove, 1, 0, "event_mark"));
        Assert.Equal(new[] { 1, -6 }, Events(b, BattleEventKind.StageChange, 1, 0, "atk").Select(e => e.Amount));
        var leaving = b.Active(1);
        int remaining = leaving.Statuses.Single().Left;
        b.ResolveTurn(BattleAction.Pass(), BattleAction.SwitchTo(1));

        Assert.Equal(new[] { -1, 6 }, Events(b, BattleEventKind.StageChange, 1, 0, "def").Select(e => e.Amount));
        Assert.Single(Events(b, BattleEventKind.FieldRemove, 1, 0, "event_mark"));
        Assert.Single(Events(b, BattleEventKind.Switch, 1, 0));
        Assert.Single(Events(b, BattleEventKind.Enter, 1, 1));
        Assert.Empty(Events(b, BattleEventKind.StatusRemove, 1, 0, "event_weak"));
        Assert.Equal(remaining, leaving.Statuses.Single().Left);
        AssertLinks(b);
    }

    [Fact]
    public void StealBoostRecordsBothFightersActualDeltasOnItsSingleLogLine()
    {
        var db = Load();
        Skill(db, "event_setup", Effect("STEAL_BOOST"));
        var b = Make(db);
        b.Active(0).Stages["atk"] = 5;
        b.Active(1).Stages["atk"] = 3;
        b.Active(1).Stages["def"] = -2;

        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());

        var gained = Assert.Single(Events(b, BattleEventKind.StageChange, 0, 0, "atk"));
        var lost = Assert.Single(Events(b, BattleEventKind.StageChange, 1, 0, "atk"));
        Assert.Equal(1, gained.Amount);
        Assert.Equal(-3, lost.Amount);
        Assert.Equal(lost.LogIndex, gained.LogIndex);
        Assert.Empty(Events(b, BattleEventKind.StageChange, 1, 0, "def"));
        AssertLinks(b);
    }

    [Fact]
    public void MissAndCriticalEventsIdentifyTheActingSkillAndPreserveTheRandomStream()
    {
        var miss = Make();
        miss.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        Assert.Single(Events(miss, BattleEventKind.Act, 0, 0, "event_miss"));
        Assert.Single(Events(miss, BattleEventKind.Miss, 0, 0, "event_miss"));
        Assert.DoesNotContain(miss.Events, e => e.Kind == BattleEventKind.Damage || e.Kind == BattleEventKind.Crit);
        bool sawCritical = false, sawOrdinary = false;
        for (uint seed = 1; seed <= 64; seed++)
        {
            var b = Make(seed: seed);
            var prediction = b.Clone();
            var expected = prediction.CalcDamage(0, b.Db.Skills["event_hit"], 1, false);
            b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            var damage = Assert.Single(Events(b, BattleEventKind.Damage, 1, 0));
            Assert.Equal(expected.Dmg, damage.Amount);
            Assert.Equal(expected.Crit ? 1 : 0, Events(b, BattleEventKind.Crit, 0, 0, "event_hit").Length);
            Assert.Equal(prediction.Random(), b.Random());
            sawCritical |= expected.Crit;
            sawOrdinary |= !expected.Crit;
            foreach (int index in Enumerable.Range(0, b.Log.Count).Where(i => b.Log[i].Kind == "act"))
                Assert.Single(b.Events, e => e.Kind == BattleEventKind.Act && e.LogIndex == index);
            AssertLinks(b);
        }
        Assert.True(sawCritical && sawOrdinary);
    }

    [Fact]
    public void CloneHasIndependentEventObjectsAndNeitherListAllowsExternalMutation()
    {
        var b = Make();
        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(1));
        var copy = b.Clone();
        string original = EventDump(b);
        Assert.Equal(original, EventDump(copy));
        Assert.NotSame(b.Events, copy.Events);
        for (int i = 0; i < b.Events.Count; i++) Assert.NotSame(b.Events[i], copy.Events[i]);
        foreach (var list in new[] { b.Events, copy.Events })
        {
            Assert.False(list is List<BattleEvent>);
            if (list is IList<BattleEvent> mutable)
            {
                Assert.True(mutable.IsReadOnly);
                Assert.Throws<NotSupportedException>(() => mutable.Add(new BattleEvent()));
                Assert.Throws<NotSupportedException>(() => mutable[0] = new BattleEvent());
                Assert.Throws<NotSupportedException>(() => mutable.Clear());
            }
        }
        ulong hash = copy.StateHash();
        string record = copy.Record.ToJson(), logs = LogDump(copy);
        var changed = copy.Events[0];
        changed.LogIndex = -9;
        changed.Turn = -9;
        changed.Node = "独立节点";
        changed.Kind = BattleEventKind.Miss;
        changed.Side = -9;
        changed.Slot = -9;
        changed.Id = "独立事件";
        changed.Amount = -9;
        Assert.Equal(original, EventDump(b));
        Assert.Equal(hash, copy.StateHash());
        Assert.Equal(record, copy.Record.ToJson());
        Assert.Equal(logs, LogDump(copy));
        copy.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(original, EventDump(b));
    }

    [Fact]
    public void ReadingEventsDoesNotChangeLogsRecordStateOrRngAndReplayReproducesThem()
    {
        var b = Make();
        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(1));
        b.ResolveTurn(BattleAction.Skill(2), BattleAction.Skill(0));
        var control = b.Clone();
        string logs = LogDump(b), record = b.Record.ToJson(), events = EventDump(b);
        ulong hash = b.StateHash();
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(events, EventDump(b));
            AssertLinks(b);
        }
        Assert.Equal(logs, LogDump(b));
        Assert.Equal(record, b.Record.ToJson());
        Assert.Equal(hash, b.StateHash());
        var replay = Replay.Run(b.Db, BattleRecord.FromJson(record));
        Assert.Equal(events, EventDump(replay));
        Assert.Equal(logs, LogDump(replay));
        Assert.Equal(record, replay.Record.ToJson());
        Assert.Equal(hash, replay.StateHash());
        Assert.Equal(control.Random(), b.Random());
    }

    [Fact]
    public void BanAndLeadUseTheFinalZeroBasedSlotsForEnterSwitchDamageAndReplacement()
    {
        var db = Load();
        Skill(db, "event_setup", Effect("FIXED_DAMAGE", n: 99999));
        var b = Make(db, openingChoices: true);
        b.ChooseBan(0, 2);
        b.ChooseBan(1, 0);
        b.ChooseLead(0, 4);
        Assert.DoesNotContain(b.Events, e => e.Kind == BattleEventKind.Enter);
        b.ChooseLead(1, 3);
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(5, b.Sides[side].Team.Count);
            var enter = Assert.Single(Events(b, BattleEventKind.Enter, side, 0));
            Assert.Equal(b.Active(side).Def.Id, enter.Id);
        }

        b.ResolveTurn(BattleAction.SwitchTo(2), BattleAction.Pass());
        Assert.Single(Events(b, BattleEventKind.Switch, 0, 0));
        Assert.Equal(b.Active(0).Def.Id, Assert.Single(Events(b, BattleEventKind.Enter, 0, 2)).Id);
        b.ResolveTurn(BattleAction.Skill(3), BattleAction.Pass());
        Assert.Single(Events(b, BattleEventKind.Act, 0, 2, "event_setup"));
        Assert.Single(Events(b, BattleEventKind.Damage, 1, 0));
        b.Replace(1, 4);
        Assert.Equal(Node.Replace, Assert.Single(Events(b, BattleEventKind.Enter, 1, 4)).Node);
        foreach (var e in b.Events.Where(e => e.Slot >= 0))
        {
            Assert.InRange(e.Side, 0, 1);
            Assert.InRange(e.Slot, 0, 4);
        }
        AssertLinks(b);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothKnockoutAndTurnCapProduceOneFinalEndEvent(bool turnCap)
    {
        var db = Load();
        Skill(db, "event_setup", Effect("FIXED_DAMAGE", n: 99999));
        var b = Make(db);
        if (turnCap) b.Turn = Battle.TurnCap;
        else foreach (var fighter in b.Sides[1].Team.Skip(1)) { fighter.Hp = 0; fighter.Fainted = true; }

        b.ResolveTurn(turnCap ? BattleAction.Pass() : BattleAction.Skill(3), BattleAction.Pass());

        Assert.Equal(Phase.Over, b.Phase);
        var ended = Assert.Single(b.Events, e => e.Kind == BattleEventKind.End);
        Assert.Equal(Node.Over, ended.Node);
        Assert.Equal(b.Log.Count - 1, ended.LogIndex);
        AssertLinks(b);
    }
}
