using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StreamDuel.Core;
using Xunit;
using Xunit.Abstractions;

public class T105_LogFilterReviewEffectsTests
{
    readonly ITestOutputHelper output;
    public T105_LogFilterReviewEffectsTests(ITestOutputHelper output) { this.output = output; }
    static readonly string[][] Teams =
    {
        new[] { "kei", "enka", "shiori", "lumi", "hane", "momo" },
        new[] { "bell", "yuan", "nyako", "buck", "kasumi", "bee" }
    };

    static ContentDb Content()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["review_neutral"] = new PersonaDef
        {
            Id = "review_neutral", Name = "安静的人设", Code = "T_LOWHP_POWER"
        };
        db.Statuses["review_control"] = new StatusDef
        {
            Id = "review_control", Name = "测试控制", Cat = "control", Skip = 1
        };
        db.Statuses["review_weak"] = new StatusDef
        {
            Id = "review_weak", Name = "测试弱化", Cat = "weak", Turns = 1
        };
        db.Fields["review_field"] = new FieldDef
        {
            Id = "review_field", Name = "测试直播间", Kind = "turn", Dispel = true
        };
        db.Chart["talk"]["talk"] = 1;
        AddSkill(db, "review_hit").Power = 40;
        db.Skills["review_hit"].Cat = "interact";
        AddSkill(db, "review_fixed", new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 10 });
        AddSkill(db, "review_status", new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "review_weak" });
        AddSkill(db, "review_field_skill", new EffectDef { At = "post", Code = "FIELD", Target = "self", Field = "review_field", Turns = 2 });
        foreach (var fighter in db.Streamers)
        {
            fighter.Persona = "review_neutral";
            fighter.Type = "talk";
            fighter.Type2 = null;
            fighter.Stats["hp"] = 1000;
            fighter.Skills = new List<string> { "review_hit", "review_fixed", "review_status", "review_field_skill" };
            fighter.Fifth = "review_fixed";
        }
        for (int side = 0; side < 2; side++)
            for (int slot = 0; slot < 6; slot++)
                db.Streamer(Teams[side][slot]).Stats["spd"] = 200 - side * 100 - slot;
        return db;
    }

    static SkillDef AddSkill(ContentDb db, string id, params EffectDef[] effects)
    {
        var skill = new SkillDef { Id = id, Name = id, Type = "talk", Cat = "status", Pp = 50, Prio = 5, Acc = 100 };
        skill.Effects.AddRange(effects);
        db.Skills[id] = skill;
        return skill;
    }

    static Battle Make(ContentDb db, uint seed = 105)
    {
        db.Validate();
        return new Battle(db, Teams.Select(team => (IList<string>)team.ToArray()).ToArray(),
            new[] { "甲方", "乙方" }, new BattleOptions { Seed = seed });
    }

    static List<int> Important(Battle battle)
    {
        return LogFilter.Apply(battle, new LogQuery { OnlyImportant = true });
    }

    static Battle NonCriticalHit(ContentDb db, bool setField)
    {
        for (uint seed = 1; seed <= 64; seed++)
        {
            var battle = Make(db, seed);
            if (setField) battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(3));
            battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            if (!battle.Events.Any(e => e.Kind == BattleEventKind.Crit)) return battle;
        }
        throw new InvalidOperationException("固定种子范围中没有非暴击命中，测试前提失效。");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualMultiplierWithCommaInSourceNameRemainsImportant(bool persona)
    {
        var db = Content();
        if (persona)
        {
            db.Personas["review_reduce"] = new PersonaDef
            {
                Id = "review_reduce", Name = "甲，乙", Code = "T_INTERACT_REDUCE",
                Params = new Dictionary<string, double> { { "mul", 0.5 } }
            };
            db.Streamer(Teams[1][0]).Persona = "review_reduce";
        }
        else
        {
            db.Fields["review_field"].Name = "测试，护盾";
            db.Fields["review_field"].DmgTakenMul = 0.5;
        }
        var battle = NonCriticalHit(db, !persona);
        var damage = Assert.Single(battle.Events, e => e.Kind == BattleEventKind.Damage);
        Assert.DoesNotContain(battle.Events, e => e.Kind == BattleEventKind.Crit);
        Assert.DoesNotContain("赛道克制", battle.Log[damage.LogIndex].Text);
        Assert.DoesNotContain("赛道被克", battle.Log[damage.LogIndex].Text);
        Assert.Contains(persona ? "人设·甲，乙" : "测试，护盾", battle.Log[damage.LogIndex].Text);
        Assert.Contains(damage.LogIndex, Important(battle));
    }

    [Fact]
    public void ActualFieldHealingAllowsItsDelimiterInsideTheFieldName()
    {
        var db = Content();
        db.Fields["review_field"].Name = "甲」：乙";
        db.Fields["review_field"].Heal = 0.1;
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Skill(3));
        var healed = Assert.Single(battle.Events, e => e.Kind == BattleEventKind.Heal);
        Assert.Equal(10, healed.Amount);
        Assert.StartsWith("「甲」：乙」：", battle.Log[healed.LogIndex].Text);
        Assert.Contains(healed.LogIndex, Important(battle));
    }

    [Fact]
    public void ActualFieldReflectionAllowsItsDelimiterInsideTheFieldName()
    {
        var db = Content();
        db.Fields["review_field"].Name = "甲」反弹：乙";
        db.Fields["review_field"].ReflectDamage = 0.5;
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(3));
        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        var reflected = Assert.Single(battle.Events, e => e.Kind == BattleEventKind.Damage && e.Side == 0);
        Assert.Equal(5, reflected.Amount);
        Assert.StartsWith("「甲」反弹：乙」反弹：", battle.Log[reflected.LogIndex].Text);
        Assert.Contains(reflected.LogIndex, Important(battle));
    }

    [Fact]
    public void ActualControlSkipAllowsItsDelimiterInsideTheStatusName()
    {
        var db = Content();
        db.Statuses["review_control"].Name = "甲 处于「乙";
        db.Skills["review_status"].Effects[0].Status = "review_control";
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Skill(1));
        var skipped = Assert.Single(Enumerable.Range(0, battle.Log.Count),
            i => battle.Log[i].Kind == "status" && battle.Log[i].Text.Contains("无法行动"));
        Assert.Contains("处于「甲 处于「乙」，无法行动", battle.Log[skipped].Text);
        Assert.Contains(skipped, Important(battle));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActualWeakStatusBlockAllowsDelimitersInsideSourceNames(bool statusName)
    {
        var db = Content();
        if (statusName) db.Statuses["review_weak"].Name = "甲 的「乙";
        else db.Fields["review_field"].Name = "甲」被乙";
        db.Fields["review_field"].ImmuneWeak = true;
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(3));
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        var blocked = Assert.Single(Enumerable.Range(0, battle.Log.Count),
            i => battle.Log[i].Kind == "status" && battle.Log[i].Text.EndsWith("挡下", StringComparison.Ordinal));
        Assert.DoesNotContain(battle.Events, e => e.Kind == BattleEventKind.StatusAdd && e.Id == "review_weak");
        Assert.Contains(blocked, Important(battle));
    }

    [Fact]
    public void FightersNameCannotTurnOrdinaryFixedDamageIntoTypeEffectiveness()
    {
        var db = Content();
        db.Streamer(Teams[1][0]).Name = "甲 人气 -10，赛道克制";
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        var damage = Assert.Single(battle.Events, e => e.Kind == BattleEventKind.Damage);
        Assert.Equal(10, damage.Amount);
        Assert.Equal("甲 人气 -10，赛道克制 受到固定伤害，人气 -10", battle.Log[damage.LogIndex].Text);
        Assert.DoesNotContain(battle.Events, e => e.Kind == BattleEventKind.Crit);
        Assert.DoesNotContain(damage.LogIndex, Important(battle));
    }
    [Fact]
    public void FightersNameCannotTurnOrdinaryWeakStatusIntoAControlSkip()
    {
        var db = Content();
        db.Streamer(Teams[1][0]).Name = "甲 处于「测试控制」，无法行动";
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        var weakEvents = battle.Events.Where(e => e.Id == "review_weak").ToArray();
        Assert.Equal(new[] { BattleEventKind.StatusAdd, BattleEventKind.StatusRemove }, weakEvents.Select(e => e.Kind));
        Assert.All(weakEvents, e => Assert.Contains("甲 处于「测试控制」，无法行动", battle.Log[e.LogIndex].Text));
        Assert.All(weakEvents, e => Assert.DoesNotContain(e.LogIndex, Important(battle)));
    }

    [Fact]
    public void MultiActionControlKeepsBothSkippedActionsImportant()
    {
        var db = Content();
        db.Statuses["review_control"].Name = "甲 处于「乙";
        db.Statuses["review_control"].Skip = 2;
        db.Skills["review_status"].Effects[0].Status = "review_control";
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Skill(1));
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(1));
        var skipped = Enumerable.Range(0, battle.Log.Count)
            .Where(i => battle.Log[i].Kind == "status" && battle.Log[i].Text.Contains("无法行动")).ToArray();
        Assert.Equal(2, skipped.Length);
        Assert.EndsWith("（还要跳过 1 次）", battle.Log[skipped[0]].Text);
        Assert.All(skipped, index => Assert.Contains(index, Important(battle)));
        Assert.Equal(Important(battle), Important(Replay.Run(db, battle.Record)));
    }

    [Theory]
    [InlineData("弱化，人设·甲（互动类伤害）×0.5")]
    [InlineData("弱化，测试，护盾 ×0.5")]
    public void WeakStatusNameCannotImpersonateAnImportantMultiplierSource(string name)
    {
        var db = Content();
        db.Statuses["review_weak"].Name = name;
        db.Statuses["review_weak"].InteractMul = 0.5;
        db.Statuses["review_weak"].Turns = 3;
        db.Skills["review_status"].Effects[0].Target = "self";
        db.Personas["unused_reduce"] = new PersonaDef { Id = "unused_reduce", Name = "甲", Code = "T_INTERACT_REDUCE" };
        db.Fields["review_field"].Name = "测试，护盾";
        db.Fields["review_field"].DmgTakenMul = 0.5;
        Battle battle = null;
        for (uint seed = 1; seed <= 64; seed++)
        {
            battle = Make(db, seed);
            battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
            battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            if (!battle.Events.Any(e => e.Kind == BattleEventKind.Crit)) break;
        }
        Assert.DoesNotContain(battle.Events, e => e.Kind == BattleEventKind.Crit);
        var damage = Assert.Single(battle.Events, e => e.Kind == BattleEventKind.Damage);
        Assert.EndsWith("（" + name + " ×0.5）", battle.Log[damage.LogIndex].Text);
        Assert.DoesNotContain(damage.LogIndex, Important(battle));
    }

    [Fact]
    public void MissingOrInvalidHistoricalSlotsDoNotInventControlEvents()
    {
        var battle = Make(Content());
        var snapshots = new[] { null, Array.Empty<int>(), new[] { 0 }, new[] { 0, -1 }, new[] { 0, 99 } };
        var indices = new List<int>();
        foreach (var snapshot in snapshots)
        {
            indices.Add(battle.Log.Count);
            battle.Log.Add(new LogEntry
            {
                Turn = 1, Side = 1, Kind = "status", Node = Node.Second, SnapActive = snapshot,
                Text = battle.Active(1).Name + " 处于「测试控制」，无法行动"
            });
        }
        Assert.All(indices, index => Assert.DoesNotContain(index, Important(battle)));
        Assert.All(indices, index => Assert.Contains(index, LogFilter.Apply(battle, null)));
    }

    [Fact]
    public void TruncatedControlTextIsNotAnImportantResultOrAnException()
    {
        var battle = Make(Content());
        int index = battle.Log.Count;
        battle.Log.Add(new LogEntry
        {
            Turn = 1, Side = 1, Kind = "status", Node = Node.Second, SnapActive = new[] { 0, 0 },
            Text = battle.Active(1).Name + " 处于「测试控制」，无法行动（还要跳过 次）"
        });
        Assert.DoesNotContain(index, Important(battle));
        Assert.Contains(index, LogFilter.Apply(battle, null));
    }

    [Fact]
    public void ThousandRealRowsWithImportantAndTextFiltersStayWithinFiveMilliseconds()
    {
        var db = Content();
        var skill = db.Skills["review_field_skill"];
        skill.Pp = 200;
        skill.Effects.Add(new EffectDef { At = "post", Code = "HEAL", Target = "self", Frac = 0.1 });
        skill.Effects.Add(new EffectDef { At = "post", Code = "STATUS", Target = "self", Status = "review_weak" });
        var battle = Make(db);
        while (battle.Log.Count < 1000 && battle.Phase != Phase.Over)
            battle.ResolveTurn(BattleAction.Skill(3), BattleAction.Skill(3));
        Assert.InRange(battle.Log.Count, 1000, 1020);
        int expected = battle.Events.Count(e => e.Kind == BattleEventKind.FieldAdd);
        Assert.True(expected > 0);
        var query = new LogQuery { OnlyImportant = true, Text = "测试直播间" };
        for (int i = 0; i < 20; i++) Assert.Equal(expected, LogFilter.Apply(battle, query).Count);
        const int repeats = 200;
        int matches = 0;
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < repeats; i++) matches += LogFilter.Apply(battle, query).Count;
        timer.Stop();
        double milliseconds = timer.Elapsed.TotalMilliseconds / repeats;
        output.WriteLine("{0} 行真实日志，重要性 + 文字筛选 {1} 次平均 {2:F4} ms。", battle.Log.Count, repeats, milliseconds);
        Assert.Equal(expected * repeats, matches);
        Assert.True(milliseconds < 5, "组合筛选平均耗时 " + milliseconds + " ms，须小于 5 ms。");
    }
}
