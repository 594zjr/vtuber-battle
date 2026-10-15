using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T114_DamageBreakdownTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    [Theory]
    [InlineData("original")]
    [InlineData("fan_holo_niji")]
    [InlineData("fan_asoul_vr")]
    [InlineData("original_wave2")]
    public void TwentyCompleteGamesPerPackIndependentlyRecalculateEveryDamageWithoutChangingTheBattle(string pack)
    {
        var db = BattleTests.LoadPack(pack);
        string content = db.ContentHash();
        var ids = db.Streamers.Select(streamer => streamer.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        int totalDamageEvents = 0;
        for (int game = 0; game < 20; game++)
        {
            var teams = new IList<string>[]
            {
                Enumerable.Range(0, 6).Select(slot => ids[(game + slot) % ids.Length]).ToArray(),
                Enumerable.Range(0, 6).Select(slot => ids[ids.Length - 1 - (game * 3 + slot) % ids.Length]).ToArray()
            };
            var options = new BattleOptions
            {
                Seed = (uint)(1140 + game), BanOne = game % 2 == 1, LeadPick = true,
                Modifiers = game % 4 == 3 ? new List<string> { "half_pp", "hype_start" } : new List<string>()
            };
            var enabled = new Battle(db, teams, new[] { "拆解甲", "拆解乙" }, options);
            var disabled = new Battle(db, teams, new[] { "拆解甲", "拆解乙" }, options) { CaptureDamageBreakdowns = false };
            Assert.True(enabled.CaptureDamageBreakdowns);
            for (int step = 0; step < 500 && enabled.Phase != Phase.Over; step++)
            {
                Advance(enabled);
                Advance(disabled);
                Assert.Equal(enabled.StateHash(), disabled.StateHash());
                Assert.Equal(enabled.Record.ToJson(), disabled.Record.ToJson());
            }
            Assert.Equal(Phase.Over, enabled.Phase);
            Assert.Equal(Phase.Over, disabled.Phase);
            var damage = enabled.Events.Where(entry => entry.Kind == BattleEventKind.Damage).ToArray();
            Assert.NotEmpty(damage);
            totalDamageEvents += damage.Length;
            foreach (var entry in damage) AssertDamage(enabled, entry);
            Assert.All(enabled.Events.Where(entry => entry.Kind != BattleEventKind.Damage), entry => Assert.Null(entry.DamageBreakdown));
            Assert.All(disabled.Events, entry => Assert.Null(entry.DamageBreakdown));
            Assert.Equal(Json(enabled.Log), Json(disabled.Log));
            Assert.Equal(EventMetadata(enabled), EventMetadata(disabled));
            string record = enabled.Record.ToJson();
            Assert.DoesNotContain("breakdown", record, StringComparison.OrdinalIgnoreCase);
            var replay = Replay.Run(db, BattleRecord.FromJson(disabled.Record.ToJson()));
            Assert.Equal(enabled.StateHash(), replay.StateHash());
            Assert.Equal(EventMetadata(enabled), EventMetadata(replay));
            Assert.Equal(DamageTexts(enabled), DamageTexts(replay));
            for (int i = 0; i < 3; i++)
            {
                double next = enabled.Random();
                Assert.Equal(next, disabled.Random());
                Assert.Equal(next, replay.Random());
            }
        }
        Assert.True(totalDamageEvents >= 20);
        Assert.Equal(content, db.ContentHash());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InteractionFormulaUsesRealStagesDualTypeStatusPersonasAndTheOriginalRandomDraws(bool critical)
    {
        var db = Fixture();
        var attacker = db.Streamers[0];
        var defender = db.Streamers[6];
        attacker.Persona = "t114_execute";
        defender.Persona = "t114_guard";
        defender.Type = "talk";
        defender.Type2 = "horror";
        db.Personas["t114_execute"] = new PersonaDef
        {
            Id = "t114_execute", Name = "测试追击", Code = "T_EXECUTE",
            Params = new Dictionary<string, double> { ["below"] = 1, ["mul"] = 1.2 }
        };
        db.Personas["t114_guard"] = new PersonaDef
        {
            Id = "t114_guard", Name = "测试抗压", Code = "T_INTERACT_REDUCE",
            Params = new Dictionary<string, double> { ["mul"] = 0.75 }
        };
        db.Statuses["t114_weak"] = new StatusDef { Id = "t114_weak", Name = "测试互动弱化", Cat = "weak", Turns = 2, InteractMul = 0.8 };
        db.Fields["t114_shield"] = new FieldDef { Id = "t114_shield", Name = "测试护场", Kind = "turn", Dispel = true, DmgTakenMul = 0.7 };
        var skill = db.Skills["t114_hit"];
        skill.Effects.AddRange(new[]
        {
            new EffectDef { At = "pre", Code = "FIXED_DAMAGE", Target = "foe", N = 100 },
            new EffectDef { At = "pre", Code = "POWER_MUL", Mul = 1.25 },
            new EffectDef { At = "pre", Code = "STAGE", Target = "self", Changes = new Dictionary<string, int> { ["atk"] = -1 } },
            new EffectDef { At = "pre", Code = "STAGE", Target = "foe", Changes = new Dictionary<string, int> { ["def"] = 1 } },
            new EffectDef { At = "pre", Code = "STATUS", Target = "self", Status = "t114_weak" },
            new EffectDef { At = "pre", Code = "FIELD", Target = "foe", Field = "t114_shield", Turns = 2 }
        });
        uint seed = SeedForCritical(critical);
        var random = new Rng(seed);
        Assert.Equal(critical, random.Next() < 1.0 / 16);
        double factor = 0.85 + random.Next() * 0.15;
        double attack = Math.Floor(151 * (2.0 / 3));
        double defense = Math.Floor(103 * 1.5);
        double power = 40 * 1.25;
        double multiplier = 1 * 1.5 * 4 * 0.8 * 1.2 * 0.7 * 0.75;
        if (critical) multiplier *= 1.5;
        int expected = Math.Max(1, (int)Math.Floor((50 * power * attack / defense / 50 + 2) * multiplier * factor));
        var battle = Make(db, seed);

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        var hits = battle.Events.Where(entry => entry.Kind == BattleEventKind.Damage && entry.Side == 1).ToArray();
        Assert.Equal(2, hits.Length);
        Assert.Equal(100, hits[0].Amount);
        Assert.Equal(expected, hits[1].Amount);
        Assert.Equal(critical, battle.Events.Any(entry => entry.Kind == BattleEventKind.Crit));
        Assert.Equal(100, battle.StatVal(battle.Active(0), "atk"));
        Assert.Equal(154, battle.StatVal(battle.Active(1), "def"));
        foreach (var entry in hits) AssertDamage(battle, entry);
        Assert.Contains("测试互动弱化", hits[1].DamageBreakdown.ToText());
        Assert.Contains("测试追击", hits[1].DamageBreakdown.ToText());
        Assert.Contains("测试护场", hits[1].DamageBreakdown.ToText());
        Assert.Contains("测试抗压", hits[1].DamageBreakdown.ToText());
        var breakdown = hits[1].DamageBreakdown;
        AssertConstant(breakdown, "技能威力", 40);
        AssertConstant(breakdown, "技能效果威力倍率", 1.25);
        AssertConstant(breakdown, "基础互动", 151);
        AssertConstant(breakdown, "互动等级", -1);
        AssertConstant(breakdown, "基础抗压", 103);
        AssertConstant(breakdown, "抗压等级", 1);
        AssertConstant(breakdown, "本赛道加成", 1.5);
        AssertConstant(breakdown, "赛道克制倍率", 4);
        AssertConstant(breakdown, "状态·测试互动弱化", 0.8);
        AssertConstant(breakdown, "攻击人设·测试追击", 1.2);
        AssertConstant(breakdown, "受伤直播间效果·测试护场", 0.7);
        AssertConstant(breakdown, "防守人设·测试抗压", 0.75);
        AssertConstant(breakdown, "暴击标记", critical ? 1 : 0);
        AssertConstant(breakdown, "随机系数", factor);
        AssertConstant(breakdown, "受击前人气", 9900);
    }

    [Fact]
    public void TalentBuffRemainsInTheSnapshotAfterItsConsumableFieldIsRemoved()
    {
        var db = Fixture();
        var skill = db.Skills["t114_hit"];
        skill.Cat = "talent";
        skill.Power = 37;
        db.Fields["t114_talent"] = new FieldDef { Id = "t114_talent", Name = "测试才艺场", Kind = "turn", TalentMul = 1.3, Consume = true, Dispel = true };
        skill.Effects.AddRange(new[]
        {
            new EffectDef { At = "pre", Code = "STAGE", Target = "self", Changes = new Dictionary<string, int> { ["spa"] = 1 } },
            new EffectDef { At = "pre", Code = "STAGE", Target = "foe", Changes = new Dictionary<string, int> { ["sdef"] = -1 } },
            new EffectDef { At = "pre", Code = "FIELD", Target = "self", Field = "t114_talent", Turns = 2 }
        });
        uint seed = SeedForCritical(false);
        var random = new Rng(seed);
        Assert.False(random.Next() < 1.0 / 16);
        double factor = 0.85 + random.Next() * 0.15;
        double attack = Math.Floor(157 * 1.5), defense = Math.Floor(109 * (2.0 / 3));
        int expected = Math.Max(1, (int)Math.Floor((50 * 37.0 * attack / defense / 50 + 2) * (1 * 1.5 * 2 * 1.3) * factor));
        var battle = Make(db, seed);

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        var hit = Assert.Single(battle.Events, entry => entry.Kind == BattleEventKind.Damage);
        Assert.Equal(expected, hit.Amount);
        AssertDamage(battle, hit);
        Assert.False(battle.Active(0).HasField("t114_talent"));
        Assert.Contains(battle.Events, entry => entry.Kind == BattleEventKind.FieldRemove && entry.Id == "t114_talent");
        Assert.Contains("测试才艺场", hit.DamageBreakdown.ToText());
        AssertConstant(hit.DamageBreakdown, "基础才艺", 157);
        AssertConstant(hit.DamageBreakdown, "才艺等级", 1);
        AssertConstant(hit.DamageBreakdown, "基础定力", 109);
        AssertConstant(hit.DamageBreakdown, "定力等级", -1);
        AssertConstant(hit.DamageBreakdown, "攻击直播间效果·测试才艺场", 1.3);
    }

    [Fact]
    public void ConsumedPowerMarksKeepTheirOriginalLayerCountAndPerLayerBonus()
    {
        var db = Fixture();
        var hit = db.Skills["t114_hit"];
        hit.Effects.AddRange(new[]
        {
            new EffectDef { At = "pre", Code = "ADD_MARK", Target = "self", Field = "hype", N = 3 },
            new EffectDef { At = "pre", Code = "POWER_PER_MARK", Field = "hype", Per = 0.2, Consume = true }
        });
        uint seed = SeedForCritical(false);
        var random = new Rng(seed);
        Assert.False(random.Next() < 1.0 / 16);
        double factor = 0.85 + random.Next() * 0.15;
        int expected = Math.Max(1, (int)Math.Floor((50 * (40 * (1 + 0.2 * 3)) * 151 / 103 / 50 + 2) * (1 * 1.5 * 2) * factor));
        var battle = Make(db, seed);

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        var entry = Assert.Single(battle.Events, item => item.Kind == BattleEventKind.Damage);
        Assert.Equal(expected, entry.Amount);
        AssertDamage(battle, entry);
        Assert.False(battle.Active(0).HasField("hype"));
        AssertConstant(entry.DamageBreakdown, "威力印记层数·热度", 3);
        AssertConstant(entry.DamageBreakdown, "每层威力加成·热度", 0.2);
    }

    [Fact]
    public void FixedDamageIsUnaffectedByAttackDefenseTypeOrIncomingDamageMultipliers()
    {
        var db = Fixture();
        var skill = db.Skills["t114_fixed"];
        skill.Effects.Add(new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 37 });
        db.Fields["t114_shield"] = new FieldDef { Id = "t114_shield", Name = "测试极强护场", Kind = "turn", DmgTakenMul = 0.01 };
        skill.Effects.Insert(0, new EffectDef { At = "pre", Code = "FIELD", Target = "foe", Field = "t114_shield", Turns = 2 });
        db.Streamers[6].Stats["def"] = 100000;
        db.Streamers[6].Type = "song";
        var battle = Make(db);

        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());

        var hit = Assert.Single(battle.Events, entry => entry.Kind == BattleEventKind.Damage);
        Assert.Equal(37, hit.Amount);
        Assert.Contains("固定伤害", hit.DamageBreakdown.Kind);
        AssertDamage(battle, hit);
        AssertConstant(hit.DamageBreakdown, "固定伤害点数", 37);
    }

    [Fact]
    public void ReflectionAndRecoilUseActualHpClippedDamageRatherThanTheUncappedFormula()
    {
        var db = Fixture();
        db.Streamers[6].Stats["hp"] = 37;
        db.Fields["t114_reflect"] = new FieldDef { Id = "t114_reflect", Name = "测试反弹场", Kind = "turn", ReflectDamage = 0.3 };
        db.Skills["t114_setup"].Effects.Add(new EffectDef { At = "post", Code = "FIELD", Target = "self", Field = "t114_reflect", Turns = 3 });
        db.Skills["t114_hit"].Power = 100;
        db.Skills["t114_hit"].Effects.Add(new EffectDef { At = "post", Code = "RECOIL", Target = "self", Frac = 0.25 });
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(1));
        int start = battle.Events.Count;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        var damage = battle.Events.Skip(start).Where(entry => entry.Kind == BattleEventKind.Damage).ToArray();
        Assert.Equal(3, damage.Length);
        Assert.Equal(new[] { 1, 0, 0 }, damage.Select(entry => entry.Side));
        Assert.Equal(new[] { 37, 11, 9 }, damage.Select(entry => entry.Amount));
        Assert.Contains("反弹", damage[1].DamageBreakdown.Kind);
        Assert.Contains("反噬", damage[2].DamageBreakdown.Kind);
        foreach (var entry in damage) AssertDamage(battle, entry);
        AssertConstant(damage[0].DamageBreakdown, "受击前人气", 37);
        AssertConstant(damage[1].DamageBreakdown, "本次实际直接伤害", 37);
        AssertConstant(damage[1].DamageBreakdown, "反弹比例", 0.3);
        AssertConstant(damage[2].DamageBreakdown, "本次实际直接伤害", 37);
        AssertConstant(damage[2].DamageBreakdown, "反噬比例", 0.25);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1)]
    public void OneTimeEndureIsAppliedAfterHpClampingAndZeroDamageDoesNotEmitAPhantomBreakdown(int hp)
    {
        var db = Fixture();
        db.Streamers[6].Stats["hp"] = hp;
        db.Personas["t114_endure"] = new PersonaDef { Id = "t114_endure", Name = "测试保命", Code = "T_ENDURE" };
        db.Streamers[6].Persona = "t114_endure";
        db.Skills["t114_fixed"].Effects.Add(new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 100 });
        var battle = Make(db);

        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());

        Assert.Equal(1, battle.Active(1).Hp);
        Assert.True(battle.Active(1).EndureUsed);
        var first = battle.Events.Where(entry => entry.Kind == BattleEventKind.Damage).ToArray();
        if (hp == 1) Assert.Empty(first);
        else
        {
            var hit = Assert.Single(first);
            Assert.Equal(hp - 1, hit.Amount);
            Assert.Contains("保命", hit.DamageBreakdown.ToText());
            AssertDamage(battle, hit);
        }
        int count = battle.Events.Count;
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        var last = Assert.Single(battle.Events.Skip(count), entry => entry.Kind == BattleEventKind.Damage);
        Assert.Equal(1, last.Amount);
        AssertDamage(battle, last);
    }

    [Theory]
    [InlineData("status", false, 12)]
    [InlineData("status", true, 24)]
    [InlineData("field", false, 7)]
    [InlineData("field", true, 15)]
    public void EndOfTurnPercentageDamageUsesCurrentMaximumHpAndExpiresAtItsDeclaredTime(string kind, bool doubled, int expected)
    {
        var db = Fixture();
        db.Streamers[6].Stats["hp"] = 101;
        if (kind == "status")
        {
            db.Statuses["t114_dot"] = new StatusDef { Id = "t114_dot", Name = "测试持续状态", Cat = "weak", Turns = 2, Dot = 0.123, Cleanse = true };
            db.Skills["t114_setup"].Effects.Add(new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "t114_dot" });
        }
        else
        {
            db.Fields["t114_dot"] = new FieldDef { Id = "t114_dot", Name = "测试持续场", Kind = "turn", Dot = 0.077, Dispel = true };
            db.Skills["t114_setup"].Effects.Add(new EffectDef { At = "post", Code = "FIELD", Target = "foe", Field = "t114_dot", Turns = 2 });
        }
        var battle = Make(db, modifiers: doubled ? new List<string> { "hp_x2" } : null);
        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        var damage = battle.Events.Where(entry => entry.Kind == BattleEventKind.Damage).ToArray();
        Assert.Equal(2, damage.Length);
        Assert.Equal(new[] { 1, 2 }, damage.Select(entry => entry.Turn));
        Assert.All(damage, entry =>
        {
            Assert.Equal(Node.TurnEnd, entry.Node);
            Assert.Equal(expected, entry.Amount);
            Assert.Contains("测试持续", entry.DamageBreakdown.Source);
            AssertDamage(battle, entry);
            AssertConstant(entry.DamageBreakdown, "最大人气", doubled ? 202 : 101);
            AssertConstant(entry.DamageBreakdown, "持续伤害比例", kind == "status" ? 0.123 : 0.077);
        });
    }

    [Theory]
    [InlineData(15, 1)]
    [InlineData(31, 1)]
    [InlineData(101, 6)]
    public void SuddenDeathBeginsOnTurnTenAndKeepsIntegerDivisionAndMinimumOne(int hp, int expected)
    {
        var db = Fixture();
        foreach (var streamer in db.Streamers) streamer.Stats["hp"] = hp;
        var battle = Make(db, modifiers: new List<string> { "sudden_death" });
        for (int turn = 0; turn < 9; turn++) battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.DoesNotContain(battle.Events, entry => entry.Kind == BattleEventKind.Damage);

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        var damage = battle.Events.Where(entry => entry.Kind == BattleEventKind.Damage).ToArray();
        Assert.Equal(2, damage.Length);
        Assert.All(damage, entry =>
        {
            Assert.Equal(10, entry.Turn);
            Assert.Equal(Node.TurnEnd, entry.Node);
            Assert.Equal(expected, entry.Amount);
            Assert.Contains("人气骤降", entry.DamageBreakdown.Source);
            AssertDamage(battle, entry);
            AssertConstant(entry.DamageBreakdown, "最大人气", hp);
            AssertConstant(entry.DamageBreakdown, "伤害除数", 16);
        });
    }

    [Fact]
    public void StruggleAndItsRecoilBothReceiveBreakdownsWithoutChangingTheRawRecordedPasses()
    {
        var db = Fixture();
        foreach (string id in new[] { "t114_hit", "t114_setup", "t114_fixed", "t114_idle" }) db.Skills[id].Pp = 0;
        var battle = Make(db);

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        var damage = battle.Events.Where(entry => entry.Kind == BattleEventKind.Damage).ToArray();
        Assert.Equal(4, damage.Length);
        Assert.Equal(ActionType.Pass, Assert.Single(battle.Record.Events).A0.Type);
        Assert.Equal(ActionType.Pass, battle.Record.Events[0].A1.Type);
        foreach (var entry in damage) AssertDamage(battle, entry);
        Assert.Contains(damage, entry => entry.DamageBreakdown.Source.Contains("硬撑"));
        Assert.Equal(Math.Max(1, (int)(damage[0].Amount * 0.25)), damage[1].Amount);
        Assert.Equal(Math.Max(1, (int)(damage[2].Amount * 0.25)), damage[3].Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClonePreservesCaptureChoiceAndKeepsExistingDiagnosticsIsolatedFromLaterSimulation(bool capture)
    {
        var battle = Make(Fixture());
        battle.CaptureDamageBreakdowns = capture;
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        string before = DamageTexts(battle);
        ulong state = battle.StateHash();
        var clone = battle.Clone();
        Assert.Equal(capture, clone.CaptureDamageBreakdowns);
        Assert.Equal(before, DamageTexts(clone));
        Assert.Equal(battle.Events.Count, clone.Events.Count);
        for (int i = 0; i < battle.Events.Count; i++) Assert.NotSame(battle.Events[i], clone.Events[i]);
        foreach (var entry in battle.Events.Where(entry => entry.DamageBreakdown != null))
        {
            var steps = Assert.IsAssignableFrom<IList<DamageStep>>(entry.DamageBreakdown.Steps);
            Assert.Throws<NotSupportedException>(() => steps.Clear());
        }

        clone.CaptureDamageBreakdowns = !capture;
        clone.Active(0).Hp--;
        clone.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(capture, battle.CaptureDamageBreakdowns);
        Assert.Equal(state, battle.StateHash());
        Assert.Equal(before, DamageTexts(battle));
    }

    [Fact]
    public void OldDamageSnapshotDoesNotReadMutableContentAgain()
    {
        var db = Fixture();
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        var entry = Assert.Single(battle.Events, item => item.Kind == BattleEventKind.Damage);
        string text = entry.DamageBreakdown.ToText();
        double result = Recompute(entry.DamageBreakdown);

        db.Skills["t114_hit"].Power = 9999;
        db.Skills["t114_hit"].Name = "后来改名";
        db.Streamers[0].Stats["atk"] = 9999;

        Assert.Equal(text, entry.DamageBreakdown.ToText());
        Assert.Equal(result, Recompute(entry.DamageBreakdown));
        Assert.Equal(entry.Amount, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ViewModelReadsOnlyCapturedDamageWithoutChangingHistoryOrRandomness(bool capture)
    {
        var battle = Make(Fixture());
        battle.CaptureDamageBreakdowns = capture;
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        var hit = Assert.Single(battle.Events, entry => entry.Kind == BattleEventKind.Damage);
        var untouched = battle.Clone();
        string record = battle.Record.ToJson(), logs = Json(battle.Log), events = EventMetadata(battle);
        ulong hash = battle.StateHash();

        string explanation = BattleViewModel.DamageExplanation(hit);
        var lines = BattleViewModel.DamageExplanationForLog(battle, hit.LogIndex);

        if (capture)
        {
            Assert.Equal(hit.DamageBreakdown.ToText(), explanation);
            Assert.Equal(explanation, Assert.Single(lines));
        }
        else
        {
            Assert.Equal(string.Empty, explanation);
            Assert.Empty(lines);
        }
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<string>>(lines).Add("篡改"));
        Assert.Equal(string.Empty, BattleViewModel.DamageExplanation(battle.Events.First(entry => entry.Kind == BattleEventKind.Info)));
        Assert.Empty(BattleViewModel.DamageExplanationForLog(battle, 0));
        Assert.Throws<ArgumentNullException>(() => BattleViewModel.DamageExplanation(null));
        Assert.Throws<ArgumentNullException>(() => BattleViewModel.DamageExplanationForLog(null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BattleViewModel.DamageExplanationForLog(battle, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BattleViewModel.DamageExplanationForLog(battle, battle.Log.Count));
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(logs, Json(battle.Log));
        Assert.Equal(events, EventMetadata(battle));
        Assert.Equal(untouched.Random(), battle.Random());
    }

    [Fact]
    public void HotseatExplanationDoesNotRevealPendingActionsOrChangeHandoverAndHistory()
    {
        var db = Fixture();
        var teamA = db.Streamers.Take(6).Select(streamer => streamer.Id).ToArray();
        var teamB = db.Streamers.Skip(6).Take(6).Select(streamer => streamer.Id).ToArray();
        var session = new HotseatSession(db, teamA, teamB, "甲", "乙", new BattleOptions { Seed = 114 });
        var untouched = new HotseatSession(db, teamA, teamB, "甲", "乙", new BattleOptions { Seed = 114 });
        foreach (var current in new[] { session, untouched })
        {
            current.Submit(BattleAction.Skill(0));
            current.ConfirmHandover();
            current.Submit(BattleAction.Pass());
        }
        string[] settled = Enumerable.Range(0, session.Log.Count)
            .SelectMany(index => session.DamageExplanationForLog(index)).ToArray();
        Assert.Single(settled);
        string logs = Json(session.Log), record = session.Record.ToJson();
        int count = session.Log.Count;
        session.Submit(BattleAction.Skill(0));
        untouched.Submit(BattleAction.Skill(0));
        Assert.True(session.NeedsHandover);
        Assert.Equal(0, session.WhoseTurn);

        var duringHandover = Enumerable.Range(0, count).SelectMany(index => session.DamageExplanationForLog(index)).ToArray();

        Assert.Equal(settled, duringHandover);
        Assert.Equal(count, session.Log.Count);
        Assert.Equal(record, session.Record.ToJson());
        Assert.Equal(logs, Json(session.Log));
        Assert.True(session.NeedsHandover);
        Assert.Equal(0, session.WhoseTurn);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.DamageExplanationForLog(count));
        foreach (var current in new[] { session, untouched })
        {
            current.ConfirmHandover();
            current.Submit(BattleAction.Pass());
            current.Surrender();
        }
        var inspectedBattle = session.CloneCompletedBattle();
        var controlBattle = untouched.CloneCompletedBattle();
        Assert.Equal(controlBattle.StateHash(), inspectedBattle.StateHash());
        Assert.Equal(controlBattle.Record.ToJson(), inspectedBattle.Record.ToJson());
        Assert.Equal(controlBattle.Random(), inspectedBattle.Random());
    }

    static void Advance(Battle battle)
    {
        if (battle.Phase == Phase.Ban)
            for (int side = 0; side < 2; side++) battle.ChooseBan(side, Ai.ChooseBan(battle, side));
        else if (battle.Phase == Phase.Lead)
            for (int side = 0; side < 2; side++) battle.ChooseLead(side, Ai.ChooseLead(battle, side));
        else if (battle.Phase == Phase.Choose) battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
        else if (battle.Phase == Phase.Replace)
            for (int side = 0; side < 2; side++)
                if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
    }

    static void AssertDamage(Battle battle, BattleEvent entry)
    {
        Assert.Equal(BattleEventKind.Damage, entry.Kind);
        Assert.True(entry.Amount > 0);
        Assert.NotNull(entry.DamageBreakdown);
        Assert.Matches("[\u3400-\u9fff]", entry.DamageBreakdown.Kind);
        Assert.Matches("[\u3400-\u9fff]", entry.DamageBreakdown.Source);
        Assert.Equal(entry.Amount, Recompute(entry.DamageBreakdown));
        Assert.InRange(entry.LogIndex, 1, battle.Log.Count - 1);
        var previous = battle.Log[entry.LogIndex - 1].SnapHp[entry.Side][entry.Slot];
        var current = battle.Log[entry.LogIndex].SnapHp[entry.Side][entry.Slot];
        Assert.Equal(entry.Amount, previous - current);
        Assert.False(string.IsNullOrWhiteSpace(entry.DamageBreakdown.ToText()));
    }

    static double Recompute(DamageBreakdown breakdown)
    {
        Assert.NotEmpty(breakdown.Steps);
        var results = new double[breakdown.Steps.Count];
        for (int i = 0; i < results.Length; i++)
        {
            var step = breakdown.Steps[i];
            Assert.Matches("[\u3400-\u9fff]", step.Name);
            if (step.Operation == DamageOperation.Constant) results[i] = step.Value;
            else
            {
                Assert.InRange(step.Left, 0, i - 1);
                double left = results[step.Left];
                if (step.Operation == DamageOperation.FloorToInt) results[i] = (int)Math.Floor(left);
                else if (step.Operation == DamageOperation.TruncateToInt) results[i] = (int)left;
                else
                {
                    Assert.InRange(step.Right, 0, i - 1);
                    double right = results[step.Right];
                    switch (step.Operation)
                    {
                        case DamageOperation.Add: results[i] = left + right; break;
                        case DamageOperation.Subtract: results[i] = left - right; break;
                        case DamageOperation.Multiply: results[i] = left * right; break;
                        case DamageOperation.Divide: Assert.NotEqual(0, right); results[i] = left / right; break;
                        case DamageOperation.Minimum: results[i] = Math.Min(left, right); break;
                        case DamageOperation.Maximum: results[i] = Math.Max(left, right); break;
                        default: throw new InvalidOperationException("未知公开运算：" + step.Operation);
                    }
                }
            }
            Assert.False(double.IsNaN(results[i]) || double.IsInfinity(results[i]));
        }
        return results[results.Length - 1];
    }

    static void AssertConstant(DamageBreakdown breakdown, string name, double expected)
    {
        var step = Assert.Single(breakdown.Steps, item => item.Name == name && item.Operation == DamageOperation.Constant);
        Assert.Equal(expected, step.Value);
    }

    static uint SeedForCritical(bool critical)
    {
        for (uint seed = 1; seed <= 1000; seed++)
            if ((new Rng(seed).Next() < 1.0 / 16) == critical) return seed;
        throw new InvalidOperationException("固定种子范围应包含暴击和普通结果。");
    }

    static ContentDb Fixture()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["t114_neutral"] = new PersonaDef { Id = "t114_neutral", Name = "测试中性人设", Code = "T_LOWHP_POWER" };
        foreach (string id in new[] { "t114_hit", "t114_setup", "t114_fixed", "t114_idle" })
            db.Skills[id] = new SkillDef { Id = id, Name = "测试技能" + id.Substring(5), Type = "song", Cat = "status", Pp = 100 };
        db.Skills["t114_hit"].Power = 40;
        db.Skills["t114_hit"].Cat = "interact";
        foreach (var streamer in db.Streamers)
        {
            streamer.Persona = "t114_neutral";
            streamer.Type = "talk";
            streamer.Type2 = null;
            streamer.Stats = new Dictionary<string, int> { ["hp"] = 10000, ["atk"] = 151, ["def"] = 103, ["spa"] = 157, ["sdef"] = 109, ["spd"] = 100 };
            streamer.Skills = new List<string> { "t114_hit", "t114_setup", "t114_fixed", "t114_idle" };
            streamer.Fifth = "t114_idle";
        }
        db.Streamers[0].Type = "song";
        db.Streamers[0].Stats["spd"] = 300;
        db.Validate();
        return db;
    }

    static Battle Make(ContentDb db, uint seed = 114, List<string> modifiers = null)
    {
        return new Battle(db, new IList<string>[]
        {
            db.Streamers.Take(6).Select(streamer => streamer.Id).ToArray(),
            db.Streamers.Skip(6).Take(6).Select(streamer => streamer.Id).ToArray()
        }, new[] { "甲", "乙" }, new BattleOptions { Seed = seed, Modifiers = modifiers });
    }

    static string DamageTexts(Battle battle) => Json(battle.Events.Select(entry => entry.DamageBreakdown?.ToText()).ToArray());
    static string EventMetadata(Battle battle) => Json(battle.Events.Select(entry => new
    {
        entry.Kind, entry.LogIndex, entry.Turn, entry.Side, entry.Slot, entry.Amount, entry.Id, entry.Node
    }).ToArray());
    static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
