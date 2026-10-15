using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StreamDuel.Core;
using Xunit;

public class T99_TurnCapTests
{
    static readonly IList<string>[] Teams =
    {
        new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
        new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
    };

    static ContentDb Content(int hp = 1000, int damage = 100)
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["t99_neutral"] = new PersonaDef
        {
            Id = "t99_neutral", Name = "稳定发挥", Code = "T_INTERACT_REDUCE", Node = Node.First,
            Desc = "互动伤害保持原值", Params = { { "mul", 1 } }
        };
        db.Skills["t99_hit"] = Skill("t99_hit", "固定输出",
            new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = damage });
        db.Skills["t99_heal"] = Skill("t99_heal", "恢复人气",
            new EffectDef { At = "post", Code = "HEAL", Target = "self", Frac = 1 });
        db.Skills["t99_self"] = Skill("t99_self", "自耗人气",
            new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "self", N = 400 });
        db.Skills["t99_wait"] = Skill("t99_wait", "等待片刻");
        db.Fields["t99_heal"] = new FieldDef
        {
            Id = "t99_heal", Name = "回末恢复", Kind = "turn", When = Node.TurnEnd,
            Desc = "回合结束恢复十分之一人气", Heal = 0.1, Dispel = true
        };
        foreach (var streamer in db.Streamers)
        {
            streamer.Persona = "t99_neutral";
            streamer.Stats["hp"] = hp;
            streamer.Stats["spd"] = 100;
            streamer.Skills = new List<string> { "t99_hit", "t99_heal", "t99_self", "t99_wait" };
            streamer.Fifth = "t99_wait";
        }
        // 内容在构局前固定并通过校验，录像只用合法行动重放，不依赖事后改技能。
        db.Validate();
        return db;
    }

    static SkillDef Skill(string id, string name, params EffectDef[] effects)
    {
        return new SkillDef
        {
            Id = id, Name = name, Type = "song", Cat = "status", Pp = 1000,
            Effects = effects.ToList()
        };
    }

    static Battle Make(bool capped = true, ContentDb db = null, bool ban = false, bool lead = false, int? rulesVersion = null)
    {
        return new Battle(db ?? Content(), Teams, new[] { "甲方", "乙方" }, new BattleOptions
        {
            Seed = 99, BanOne = ban, LeadPick = lead, RulesVersion = rulesVersion ?? (capped ? 3 : 2),
            Modifiers = capped ? new List<string> { "turn_cap_60" } : null
        });
    }

    static Battle MakeDefault(ContentDb db = null)
    {
        // 不显式填规则版本，才能验证调用方未指定版本时真正采用的新默认值。
        return new Battle(db ?? Content(), Teams, new[] { "甲方", "乙方" }, new BattleOptions { Seed = 99 });
    }

    static void Pass(Battle battle) { battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass()); }

    static void AtCap(Battle battle)
    {
        battle.Turn = battle.EffectiveTurnCap;
        Pass(battle);
    }

    static void HitThenHeal(Battle battle, int attacker)
    {
        battle.ResolveTurn(attacker == 0 ? BattleAction.Skill(0) : BattleAction.Pass(),
            attacker == 1 ? BattleAction.Skill(0) : BattleAction.Pass());
        battle.ResolveTurn(attacker == 1 ? BattleAction.Skill(1) : BattleAction.Pass(),
            attacker == 0 ? BattleAction.Skill(1) : BattleAction.Pass());
    }

    static void Ended(Battle battle, int winner, string reason, string keyword = null)
    {
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(winner, battle.Winner);
        Assert.Equal(reason, battle.EndReason);
        Assert.Equal(reason, BattleSummary.From(battle).EndReason);
        var end = Assert.Single(battle.Events, item => item.Kind == BattleEventKind.End);
        Assert.Equal(winner, end.Side);
        Assert.Equal(Node.Over, end.Node);
        Assert.Equal(battle.Log.Count - 1, end.LogIndex);
        Assert.Equal(Node.Over, battle.Log.Last().Node);
        if (keyword != null) Assert.Contains(keyword, battle.Log.Last().Text);
        if (battle.EffectiveTurnCap == 60 || battle.RulesVersion >= 3 || reason != "回合上限")
            Assert.Contains(winner < 0 ? "平局" : battle.Sides[winner].Name, battle.Log.Last().Text);
        int logs = battle.Log.Count, events = battle.Events.Count, recordEvents = battle.Record.Events.Count;
        Assert.Throws<InvalidOperationException>(() => Pass(battle));
        Assert.Equal(logs, battle.Log.Count);
        Assert.Equal(events, battle.Events.Count);
        Assert.Equal(recordEvents, battle.Record.Events.Count);
    }

    static string[] FullLog(Battle battle)
    {
        return battle.Log.Select(entry => string.Join("|", entry.Turn, entry.Node, entry.Kind, entry.Side, entry.Text,
            string.Join(",", entry.SnapActive), string.Join(",", entry.SnapHp[0]), string.Join(",", entry.SnapHp[1]))).ToArray();
    }

    static string[] FullEvents(Battle battle)
    {
        return battle.Events.Select(item => string.Join("|", item.LogIndex, item.Turn, item.Node,
            item.Kind, item.Side, item.Slot, item.Id, item.Amount)).ToArray();
    }

    [Fact]
    public void ModifierIsExplicitAndLegacyVersionTwoStaysAt120()
    {
        var normal = Make(false);
        var capped = Make();
        Assert.Equal(120, Battle.TurnCap);
        Assert.Equal(120, normal.EffectiveTurnCap);
        Assert.Equal(60, capped.EffectiveTurnCap);
        Assert.Equal(2, normal.Record.Version);
        Assert.Equal(2, normal.RulesVersion);
        Assert.Equal(3, capped.RulesVersion);
        Assert.DoesNotContain("turn_cap_60", normal.Record.ToJson());
        Assert.Equal(new[] { "turn_cap_60" }, capped.Record.Modifiers);
        var definition = Assert.Single(BattleModifiers.All, item => item.Id == "turn_cap_60");
        Assert.False(string.IsNullOrWhiteSpace(definition.Name));
        Assert.Contains("60", definition.Desc);
        Assert.Equal("", capped.EndReason);
        Assert.Equal("", BattleSummary.From(capped).EndReason);
    }

    [Theory]
    [InlineData(true, 60)]
    [InlineData(false, 120)]
    public void EndsAfterExactlyTheConfiguredNumberOfCompleteTurns(bool capped, int limit)
    {
        var battle = Make(capped);
        for (int turn = 1; turn < limit; turn++) Pass(battle);
        Assert.Equal(limit, battle.Turn);
        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.Equal("", battle.EndReason);
        Assert.DoesNotContain(battle.Events, item => item.Kind == BattleEventKind.End);
        Pass(battle);
        Ended(battle, -1, "回合上限", capped ? "平局" : "剩余人气");
        Assert.Equal(limit, battle.Turn);
        Assert.Equal(limit, BattleSummary.From(battle).Turns);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void MoreSurvivorsWinsEvenWithLessRemainingHp(int winner, bool useDefault)
    {
        var battle = useDefault ? MakeDefault() : Make();
        foreach (var fighter in battle.Sides[winner].Team) fighter.Hp = 1;
        var fainted = battle.Sides[1 - winner].Team.Last();
        fainted.Hp = 0;
        fainted.Fainted = true;
        Assert.True(battle.Sides[winner].Team.Sum(fighter => fighter.HpRatio)
            < battle.Sides[1 - winner].Team.Sum(fighter => fighter.HpRatio));
        AtCap(battle);
        Ended(battle, winner, "回合上限", "人数");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void HpRatioPrecedesDamageAndDoesNotCompareRawHp(int winner, bool useDefault)
    {
        var battle = useDefault ? MakeDefault() : Make();
        HitThenHeal(battle, 1 - winner);
        foreach (var fighter in battle.Sides[winner].Team) { fighter.MaxHp = 100; fighter.Hp = 51; }
        foreach (var fighter in battle.Sides[1 - winner].Team) { fighter.MaxHp = 1000; fighter.Hp = 500; }
        Assert.Equal(100, BattleSummary.From(battle).Sides[1 - winner].Sum(stats => stats.DamageDealt));
        Assert.True(battle.Sides[winner].Team.Sum(fighter => fighter.Hp)
            < battle.Sides[1 - winner].Team.Sum(fighter => fighter.Hp));
        AtCap(battle);
        Ended(battle, winner, "回合上限", "人气");
        Assert.DoesNotContain("按累计伤害判定", battle.Log.Last().Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactHpComparisonDetectsADifferenceBelowDoublePrecision(bool useDefault)
    {
        var battle = useDefault ? MakeDefault() : Make();
        battle.Active(0).Hp = battle.Active(1).Hp = 1;
        battle.Active(0).MaxHp = int.MaxValue - 1;
        battle.Active(1).MaxHp = int.MaxValue;
        Assert.Equal(battle.Sides[0].Team.Sum(fighter => fighter.HpRatio),
            battle.Sides[1].Team.Sum(fighter => fighter.HpRatio));
        AtCap(battle);
        Ended(battle, 0, "回合上限", "人气");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MathematicallyEqualFractionsRemainATieDespiteFloatingPointSummation(bool useDefault)
    {
        var battle = useDefault ? MakeDefault() : Make();
        foreach (var fighter in battle.Sides[0].Team) { fighter.Hp = 1; fighter.MaxHp = 10; }
        for (int slot = 0; slot < 6; slot++)
        {
            battle.Sides[1].Team[slot].Hp = 1;
            battle.Sides[1].Team[slot].MaxHp = slot < 2 ? 5 : 20;
        }
        Assert.NotEqual(battle.Sides[0].Team.Sum(fighter => fighter.HpRatio),
            battle.Sides[1].Team.Sum(fighter => fighter.HpRatio));
        AtCap(battle);
        Ended(battle, -1, "回合上限", "平局");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void ActualDamageBreaksTheTieAfterTheOpponentHeals(int winner, bool useDefault)
    {
        var battle = useDefault ? MakeDefault() : Make();
        HitThenHeal(battle, winner);
        Assert.All(battle.Sides.SelectMany(side => side.Team), fighter => Assert.Equal(fighter.MaxHp, fighter.Hp));
        Assert.Equal(100, BattleSummary.From(battle).Sides[winner].Sum(stats => stats.DamageDealt));
        Assert.Contains(battle.Events, item => item.Kind == BattleEventKind.Heal && item.Amount == 100);
        AtCap(battle);
        Ended(battle, winner, "回合上限", "伤害");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelfDamageDoesNotOutscoreRealDamageToTheOpponent(bool useDefault)
    {
        var battle = useDefault ? MakeDefault() : Make();
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Skill(0));
        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        var summary = BattleSummary.From(battle);
        Assert.Equal(0, summary.Sides[0].Sum(stats => stats.DamageDealt));
        Assert.Equal(100, summary.Sides[1].Sum(stats => stats.DamageDealt));
        Assert.Equal(500, summary.Sides[0].Sum(stats => stats.DamageTaken));
        AtCap(battle);
        Ended(battle, 1, "回合上限", "伤害");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualSurvivorsRatiosAndActualDamageEndInADraw(bool useDefault)
    {
        var battle = useDefault ? MakeDefault() : Make();
        HitThenHeal(battle, 0);
        HitThenHeal(battle, 1);
        Assert.Equal(100, BattleSummary.From(battle).Sides[0].Sum(stats => stats.DamageDealt));
        Assert.Equal(100, BattleSummary.From(battle).Sides[1].Sum(stats => stats.DamageDealt));
        AtCap(battle);
        Ended(battle, -1, "回合上限", "平局");
    }

    [Fact]
    public void TurnEndHealingIsAppliedBeforeTheCapDecision()
    {
        var battle = Make();
        battle.Active(0).Hp -= 100;
        battle.Active(0).Fields.Add(new FieldInst { Id = "t99_heal", Turns = 1 });
        AtCap(battle);
        Assert.Equal(battle.Active(0).MaxHp, battle.Active(0).Hp);
        Assert.Empty(battle.Active(0).Fields);
        var heal = Assert.Single(battle.Events, item => item.Kind == BattleEventKind.Heal);
        Assert.Equal(Node.TurnEnd, heal.Node);
        Assert.True(heal.LogIndex < battle.Log.Count - 1);
        Ended(battle, -1, "回合上限", "平局");
    }

    [Fact]
    public void TheCapFinishesBeforeReplacementIfAReserveStillSurvives()
    {
        var battle = Make();
        battle.Active(0).Hp = 100;
        battle.Turn = 60;
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        Assert.True(battle.Active(0).Fainted);
        Assert.Equal(5, battle.Sides[0].Team.Count(fighter => !fighter.Fainted));
        Ended(battle, 1, "回合上限", "人数");
        Assert.DoesNotContain(battle.Log, entry => entry.Node == Node.Replace);
        Assert.Throws<InvalidOperationException>(() => battle.Replace(0, 1));
    }

    [Fact]
    public void KnockoutOnTheLastTurnTakesPriorityOverTheCap()
    {
        var battle = Make();
        foreach (var fighter in battle.Sides[1].Team.Skip(1)) { fighter.Hp = 0; fighter.Fainted = true; }
        battle.Active(1).Hp = 100;
        battle.Turn = 60;
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Ended(battle, 0, "击倒全灭");
        Assert.DoesNotContain(battle.Log.Where(entry => entry.Node == Node.Over), entry => entry.Text.Contains("回合上限"));
    }

    [Fact]
    public void BothSidesKnockedOutKeepTheKnockoutReasonForTheDraw()
    {
        var battle = Make();
        foreach (var fighter in battle.Sides.SelectMany(side => side.Team))
        { fighter.Hp = 0; fighter.Fainted = true; }
        // 当前伤害逐次立即判胜，没有同时击倒的公开行动；这里只验证既有双方全灭分支的结束原因。
        var checkOver = typeof(Battle).GetMethod("CheckOver", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(checkOver);
        Assert.True((bool)checkOver.Invoke(battle, null));
        Ended(battle, -1, "击倒全灭");
    }

    [Fact]
    public void ExplicitLegacy120RetainsItsHpDecisionAndExactEndingLog()
    {
        var battle = Make(false);
        foreach (var fighter in battle.Sides[0].Team) fighter.Hp = 1;
        battle.Sides[1].Team.Last().Hp = 0;
        battle.Sides[1].Team.Last().Fainted = true;
        AtCap(battle);
        Ended(battle, 1, "回合上限");
        Assert.Equal("达到 120 回合上限，按剩余人气判定", battle.Log.Last().Text);
        Assert.Equal(2, battle.Record.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClonesRetainAccumulatedDamageAndCanEvolveIndependently(bool aiCopy)
    {
        var battle = Make();
        HitThenHeal(battle, 0);
        battle.Turn = 58;
        ulong beforeHash = battle.StateHash();
        string beforeRecord = battle.Record.ToJson();
        var copy = aiCopy
            ? (Battle)typeof(Battle).GetMethod("CopyForHardAi", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(battle, null)
            : battle.Clone();
        Assert.Equal(60, copy.EffectiveTurnCap);
        Assert.Equal("", copy.EndReason);
        HitThenHeal(copy, 1);
        Pass(copy);
        Ended(copy, -1, "回合上限", "平局");
        Assert.Equal(beforeHash, battle.StateHash());
        Assert.Equal(beforeRecord, battle.Record.ToJson());
        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.Equal("", battle.EndReason);
        AtCap(battle);
        Ended(battle, 0, "回合上限", "伤害");
        var completedCopy = battle.Clone();
        Assert.Equal(battle.EndReason, completedCopy.EndReason);
        Assert.Equal(battle.StateHash(), completedCopy.StateHash());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegalSixtyTurnRecordsReplayWithMatchingHashLogEventsAndSummary(bool openingChoices)
    {
        var battle = Make(ban: openingChoices, lead: openingChoices);
        if (openingChoices)
        {
            battle.ChooseBan(1, 5);
            battle.ChooseBan(0, 5);
            battle.ChooseLead(0, 1);
            battle.ChooseLead(1, 2);
        }
        HitThenHeal(battle, 0);
        while (battle.Phase != Phase.Over) Pass(battle);
        Ended(battle, 0, "回合上限", "伤害");
        Assert.Equal(60, BattleSummary.From(battle).Turns);
        var record = BattleRecord.FromJson(battle.Record.ToJson());
        Assert.True(Replay.Check(battle.Db, record).CanReplay);
        var replay = Replay.Run(battle.Db, record);
        Assert.Equal(battle.Record.ToJson(), replay.Record.ToJson());
        Assert.Equal(battle.StateHash(), replay.StateHash());
        Assert.Equal(FullLog(battle), FullLog(replay));
        Assert.Equal(FullEvents(battle), FullEvents(replay));
        Assert.Equal(BattleSummary.From(battle).ToText(), BattleSummary.From(replay).ToText());
        Assert.Equal(BattleSummary.From(battle).EndReason, BattleSummary.From(replay).EndReason);
        Ended(replay, 0, "回合上限", "伤害");
        for (int draw = 0; draw < 4; draw++) Assert.Equal(battle.Random(), replay.Random());
    }

    [Fact]
    public void DamageAccumulationBeyondIntMaxStillWinsAndReplays()
    {
        var battle = Make(db: Content(2000000000, 1000000000));
        for (int cycle = 0; cycle < 30; cycle++) HitThenHeal(battle, 0);
        Assert.Equal(30000000000L, battle.Events.Where(item => item.Kind == BattleEventKind.Damage && item.Side == 1)
            .Sum(item => (long)item.Amount));
        Assert.All(battle.Sides.SelectMany(side => side.Team), fighter => Assert.Equal(fighter.MaxHp, fighter.Hp));
        Ended(battle, 0, "回合上限", "伤害");
        var replay = Replay.Run(battle.Db, BattleRecord.FromJson(battle.Record.ToJson()));
        Assert.Equal(battle.StateHash(), replay.StateHash());
        Assert.Equal(FullLog(battle), FullLog(replay));
        Ended(replay, 0, "回合上限", "伤害");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyTheNewModifierHashesDamageHistoryWhenOtherStateIsEqual(bool capped)
    {
        var db = Content();
        var selfDamage = Make(capped, db);
        var enemyDamage = Make(capped, db);
        selfDamage.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        selfDamage.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        HitThenHeal(enemyDamage, 0);
        for (int side = 0; side < 2; side++)
        {
            for (int slot = 0; slot < 6; slot++)
            {
                var expected = selfDamage.Sides[side].Team[slot];
                var actual = enemyDamage.Sides[side].Team[slot];
                Assert.Equal(expected.Hp, actual.Hp);
                actual.LastSkillIndex = expected.LastSkillIndex;
                for (int skill = 0; skill < expected.Skills.Count; skill++) actual.Skills[skill].Pp = expected.Skills[skill].Pp;
            }
        }
        Assert.Equal(selfDamage.Turn, enemyDamage.Turn);
        Assert.Equal(selfDamage.Phase, enemyDamage.Phase);
        Assert.Equal(selfDamage.ActedFirst, enemyDamage.ActedFirst);
        Assert.Equal(selfDamage.Need, enemyDamage.Need);
        for (int draw = 0; draw < 4; draw++) Assert.Equal(selfDamage.Random(), enemyDamage.Random());
        // 旧规则的相等对照证明伤害归因以外的哈希状态已对齐。
        if (capped) Assert.NotEqual(selfDamage.StateHash(), enemyDamage.StateHash());
        else Assert.Equal(selfDamage.StateHash(), enemyDamage.StateHash());
    }

    [Fact]
    public void EditingLogsRecordsAndSummarySnapshotsCannotRewriteAccumulatedDamage()
    {
        var battle = Make();
        HitThenHeal(battle, 0);
        ulong hash = battle.StateHash();
        battle.Log.Clear();
        battle.Record.Events.Clear();
        var summary = BattleSummary.From(battle);
        foreach (var stats in summary.Sides[0]) stats.DamageDealt = 0;
        foreach (var stats in summary.Sides[1]) stats.DamageDealt = int.MaxValue;
        Assert.Equal(hash, battle.StateHash());
        AtCap(battle);
        Ended(battle, 0, "回合上限", "伤害");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TurnCapWinnerNameIsCleanedOnlyInTheDisplayProjection(bool useDefault)
    {
        string rawName = "甲方\n\u200b超长超长超长超长超长超长名字";
        var battle = new Battle(Content(), Teams, new[] { rawName, "乙方" }, new BattleOptions
        {
            Seed = 99, Modifiers = useDefault ? null : new List<string> { "turn_cap_60" }
        });
        HitThenHeal(battle, 0);
        AtCap(battle);
        string rawLog = battle.Log.Last().Text;
        string rawRecord = battle.Record.ToJson();
        ulong hash = battle.StateHash();
        var display = TextInput.ForDisplay(battle);
        string cleanName = TextInput.Clean(rawName, TextInputKind.PlayerName).Text;

        Assert.Contains(rawName + " 获胜", rawLog);
        Assert.Equal(rawLog.Replace(rawName + " 获胜", cleanName + " 获胜"), display.Log.Last().Text);
        Assert.DoesNotContain("\n", display.Log.Last().Text);
        Assert.Equal("回合上限", BattleSummary.From(display).EndReason);
        Assert.Equal(hash, display.StateHash());
        Assert.Equal(rawLog, battle.Log.Last().Text);
        Assert.Equal(rawRecord, battle.Record.ToJson());
        Assert.Equal(rawName, battle.Sides[0].Name);
    }

    [Fact]
    public void UnspecifiedRulesUseVersionThreeAndTheOriginal120TurnLimit()
    {
        var options = new BattleOptions { Seed = 99 };
        var battle = new Battle(Content(), Teams, new[] { "甲方", "乙方" }, options);
        Assert.Equal(3, options.RulesVersion);
        Assert.Equal(3, BattleRecord.DefaultVersion);
        Assert.Equal(3, new BattleRecord().Version);
        Assert.Equal(3, battle.RulesVersion);
        Assert.Equal(3, battle.Record.Version);
        Assert.Null(battle.Record.RulesVersion);
        Assert.DoesNotContain("\"rulesVersion\"", battle.Record.ToJson());
        Assert.Equal(120, battle.EffectiveTurnCap);
        Assert.Equal(3, new Battle(battle.Db, Teams, new[] { "甲方", "乙方" }, 99u).RulesVersion);
        var property = typeof(Battle).GetProperty(nameof(Battle.RulesVersion));
        Assert.NotNull(property);
        Assert.Null(property.GetSetMethod());
        options.RulesVersion = 2;
        Assert.Equal(3, battle.RulesVersion);
        HitThenHeal(battle, 0);
        while (battle.Turn < 120) Pass(battle);
        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.Equal("", battle.EndReason);
        Assert.DoesNotContain(battle.Events, item => item.Kind == BattleEventKind.End);
        Pass(battle);
        Ended(battle, 0, "回合上限", "累计伤害");
        Assert.Equal(120, battle.Turn);
        Assert.Equal(120, BattleSummary.From(battle).Turns);
        TerminationCopiesMatch(battle);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void NewBattlesRejectUnsupportedRulesVersions(int version)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Battle(Content(), Teams,
            new[] { "甲方", "乙方" }, new BattleOptions { Seed = 99, RulesVersion = version }));
    }

    [Fact]
    public void TheSameLegal120TurnActionsUseVersionThreeDamageAndVersionTwoLegacyDecisions()
    {
        var db = Content();
        var current = MakeDefault(db);
        var legacy = Make(false, db);
        HitThenHeal(current, 0);
        HitThenHeal(legacy, 0);
        while (current.Phase != Phase.Over) { Pass(current); Pass(legacy); }
        Ended(current, 0, "回合上限", "累计伤害");
        Ended(legacy, -1, "回合上限");
        Assert.Equal("达到 120 回合上限，按剩余人气判定", legacy.Log.Last().Text);
        Assert.Equal(120, current.Record.Events.Count(item => item.Type == BattleRecordEventType.Turn));
        Assert.Equal(FullLog(current).Take(current.Log.Count - 1), FullLog(legacy).Take(legacy.Log.Count - 1));
        string currentJson = current.Record.ToJson();
        var oldRecord = BattleRecord.FromJson(currentJson);
        oldRecord.Version = 2;
        Assert.Null(oldRecord.RulesVersion);
        Assert.Equal(legacy.Record.ToJson(), oldRecord.ToJson());
        var oldReplay = Replay.Run(db, BattleRecord.FromJson(oldRecord.ToJson()));
        Assert.Equal(2, oldReplay.RulesVersion);
        Assert.Equal(legacy.StateHash(), oldReplay.StateHash());
        Assert.Equal(FullLog(legacy), FullLog(oldReplay));
        Assert.Equal(FullEvents(legacy), FullEvents(oldReplay));
        Ended(oldReplay, -1, "回合上限");
        Assert.Equal(currentJson, current.Record.ToJson());
        TerminationCopiesMatch(current);
        TerminationCopiesMatch(legacy);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void FullAndHardClonesKeepFrozenRulesDespiteExternallyEditedRecordMetadata(int version, bool aiCopy)
    {
        var battle = Make(false, rulesVersion: version);
        HitThenHeal(battle, 0);
        int changedVersion = version == 3 ? 2 : 3;
        battle.Record.Version = changedVersion;
        battle.Record.RulesVersion = changedVersion;
        var copy = aiCopy
            ? (Battle)typeof(Battle).GetMethod("CopyForHardAi", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(battle, null)
            : battle.Clone();
        Assert.Equal(version, battle.RulesVersion);
        Assert.Equal(version, copy.RulesVersion);
        Assert.Equal(changedVersion, copy.Record.Version);
        Assert.Equal((int?)changedVersion, copy.Record.RulesVersion);
        copy.Record.Version = version;
        copy.Record.RulesVersion = null;
        Assert.Equal(changedVersion, battle.Record.Version);
        Assert.Equal((int?)changedVersion, battle.Record.RulesVersion);
        AtCap(copy);
        int winner = version == 3 ? 0 : -1;
        Ended(copy, winner, "回合上限", version == 3 ? "累计伤害" : "剩余人气");
        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.Equal("", battle.EndReason);
        AtCap(battle);
        Ended(battle, winner, "回合上限", version == 3 ? "累计伤害" : "剩余人气");
        Assert.Equal(version, battle.Clone().RulesVersion);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ReplayCursorFreezesRulesAndItsInputRecordAcrossSeekSnapshots(int version)
    {
        var battle = Make(false, rulesVersion: version);
        HitThenHeal(battle, 0);
        while (battle.Phase != Phase.Over) Pass(battle);
        var record = BattleRecord.FromJson(battle.Record.ToJson());
        var cursor = new ReplayCursor(battle.Db, record, snapshotEvery: 7);
        record.Version = version == 3 ? 2 : 3;
        record.RulesVersion = record.Version;
        record.Events.Clear();
        Assert.Equal(120, cursor.MaxTurn);
        foreach (int turn in new[] { 0, 2, 7, 63, 119 })
        {
            cursor.Seek(turn);
            Assert.Equal(version, cursor.Current.RulesVersion);
            Assert.Equal(version, cursor.Current.Record.Version);
            Assert.Null(cursor.Current.Record.RulesVersion);
            Assert.Equal("", cursor.Current.EndReason);
        }
        Assert.True(cursor.Next());
        Ended(cursor.Current, version == 3 ? 0 : -1, "回合上限");
        Assert.Equal(battle.StateHash(), cursor.Current.StateHash());
        Assert.Equal(FullLog(battle), FullLog(cursor.Current));
        Assert.Equal(FullEvents(battle), FullEvents(cursor.Current));
        Assert.True(cursor.Prev());
        Assert.Equal(version, cursor.Current.RulesVersion);
        Assert.Equal("", cursor.Current.EndReason);
        cursor.ToEnd();
        Assert.Equal(version, cursor.Current.RulesVersion);
        Assert.Equal(battle.Record.ToJson(), cursor.Current.Record.ToJson());
        Assert.Equal(battle.StateHash(), cursor.Current.StateHash());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void AgreementFormatUpgradePreservesTheOriginal120TurnRules(int version, bool reject)
    {
        var battle = Make(false, rulesVersion: version);
        HitThenHeal(battle, 0);
        battle.OfferDraw(1);
        if (reject) battle.RespondDraw(0, false);
        Assert.Equal(3, battle.Record.Version);
        Assert.Equal(version, battle.RulesVersion);
        Assert.Equal(version < 3 ? (int?)version : null, battle.Record.RulesVersion);
        Assert.Equal(!reject, battle.HasDrawOffer(1));
        Assert.Equal("", battle.EndReason);
        string json = battle.Record.ToJson();
        if (version < 3) Assert.Contains("\"rulesVersion\":" + version, json);
        else Assert.DoesNotContain("\"rulesVersion\"", json);
        var parsed = BattleRecord.FromJson(json);
        Assert.Equal(battle.Record.RulesVersion, parsed.RulesVersion);
        Assert.True(Replay.Check(battle.Db, parsed).CanReplay);
        Assert.Equal(version, Replay.Run(battle.Db, parsed).RulesVersion);
        Assert.Equal(version, battle.Clone().RulesVersion);
        Assert.Equal(battle.Record.RulesVersion, battle.Clone().Record.RulesVersion);
        while (battle.Phase != Phase.Over) Pass(battle);
        Ended(battle, version == 3 ? 0 : -1, "回合上限");
        Assert.Equal(120, battle.Turn);
        Assert.Equal(100, BattleSummary.From(battle).Sides[0].Sum(stats => stats.DamageDealt));
        TerminationCopiesMatch(battle);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OrdinaryRecordsOmitRedundantRulesOverridesAndReplayTheirFormatRules(int version)
    {
        var battle = Make(false, rulesVersion: version);
        HitThenHeal(battle, 0);
        Assert.Null(battle.Record.RulesVersion);
        string json = battle.Record.ToJson();
        Assert.DoesNotContain("\"rulesVersion\"", json);
        battle.Record.RulesVersion = version;
        Assert.Equal(json, battle.Record.ToJson());
        var parsed = BattleRecord.FromJson(json);
        Assert.Null(parsed.RulesVersion);
        var replay = Replay.Run(battle.Db, parsed);
        Assert.Equal(version, replay.RulesVersion);
        Assert.Equal(battle.StateHash(), replay.StateHash());
        Assert.Equal(FullLog(battle), FullLog(replay));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    public void ExplicitOlderRulesOverridesSurviveJsonReplayAndCursor(int format, int rules)
    {
        var battle = Make(false, rulesVersion: rules);
        HitThenHeal(battle, 0);
        while (battle.Phase != Phase.Over) Pass(battle);
        battle.Record.Version = format;
        battle.Record.RulesVersion = rules;
        string json = battle.Record.ToJson();
        Assert.Contains("\"rulesVersion\":" + rules, json);
        var parsed = BattleRecord.FromJson(json);
        Assert.Equal((int?)rules, parsed.RulesVersion);
        Assert.Equal(format, parsed.Version);
        Assert.Equal(json, parsed.ToJson());
        Assert.True(Replay.Check(battle.Db, parsed).CanReplay);
        TerminationCopiesMatch(battle);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("4")]
    [InlineData("2147483648")]
    [InlineData("2.5")]
    [InlineData("\"2\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void JsonRejectsNonIntegerOrUnsupportedRulesOverrides(string value)
    {
        string json = MakeDefault().Record.ToJson();
        json = json.Substring(0, json.Length - 1) + ",\"rulesVersion\":" + value + "}";
        Assert.Throws<FormatException>(() => BattleRecord.FromJson(json));
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(3, -1)]
    [InlineData(3, 4)]
    [InlineData(3, int.MaxValue)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    public void EveryRecordEntryPointRejectsUnsupportedOrNewerThanFormatRules(int format, int rules)
    {
        var battle = MakeDefault();
        var record = battle.Record;
        record.Version = format;
        string json = record.ToJson();
        record.RulesVersion = rules;
        Assert.Throws<FormatException>(() => record.ToJson());
        Assert.Throws<FormatException>(() => Replay.Run(battle.Db, record));
        var check = Replay.Check(battle.Db, record);
        Assert.False(check.CanReplay);
        Assert.False(string.IsNullOrWhiteSpace(check.Reason));
        json = json.Substring(0, json.Length - 1) + ",\"rulesVersion\":" + rules + "}";
        Assert.Throws<FormatException>(() => BattleRecord.FromJson(json));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ExplicitSixtyTurnModifierKeepsFourTierDecisionEvenForLegacyRules(int version)
    {
        var battle = Make(rulesVersion: version);
        HitThenHeal(battle, 0);
        while (battle.Phase != Phase.Over) Pass(battle);
        Assert.Equal(version, battle.RulesVersion);
        Assert.Equal(version, battle.Record.Version);
        Assert.Equal(60, battle.Turn);
        Ended(battle, 0, "回合上限", "累计伤害");
        TerminationCopiesMatch(battle);
    }

    sealed class VersionTransport : INetTransport
    {
        readonly Queue<string> incoming = new Queue<string>();
        public VersionTransport Peer;
        public void Send(string message) { Peer.incoming.Enqueue(message); }
        public bool TryReceive(out string message)
        {
            message = incoming.Count == 0 ? null : incoming.Dequeue();
            return message != null;
        }
    }

    static void PumpVersionSessions(NetSession host, NetSession guest)
    {
        for (int step = 0; step < 20; step++) { host.Poll(); guest.Poll(); }
        Assert.Null(host.Error);
        Assert.Null(guest.Error);
    }

    static void NetworkTurn(NetSession host, NetSession guest, BattleAction first, BattleAction second)
    {
        Assert.Equal(NetState.Choose, host.State);
        Assert.Equal(NetState.Choose, guest.State);
        host.Submit(first);
        guest.Submit(second);
        PumpVersionSessions(host, guest);
        Assert.Equal(host.Battle.StateHash(), guest.Battle.StateHash());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NetworkProtocolsThreeAndFourUseTheSameDefaultRulesAsLocalBattles(bool termination)
    {
        var db = Content();
        var first = new VersionTransport();
        var second = new VersionTransport();
        first.Peer = second;
        second.Peer = first;
        var host = new NetSession(db, first, true, Teams[0], "甲方", 99, enableTermination: termination);
        var guest = new NetSession(db, second, false, Teams[1], "乙方", null, enableTermination: termination);
        PumpVersionSessions(host, guest);
        Assert.Equal(termination ? 4 : 3, host.SessionProtocolVersion);
        Assert.Equal(host.SessionProtocolVersion, guest.SessionProtocolVersion);
        Assert.Equal(3, host.Battle.RulesVersion);
        Assert.Equal(3, guest.Battle.RulesVersion);
        Assert.Equal(3, host.Battle.Record.Version);
        Assert.Equal(3, guest.Battle.Record.Version);
        Assert.Null(host.Battle.Record.RulesVersion);
        Assert.Null(guest.Battle.Record.RulesVersion);
        NetworkTurn(host, guest, BattleAction.Skill(0), BattleAction.Pass());
        NetworkTurn(host, guest, BattleAction.Pass(), BattleAction.Skill(1));
        if (termination)
        {
            Assert.True(host.CanOfferDraw);
            host.OfferDraw();
            PumpVersionSessions(host, guest);
            Assert.True(guest.CanRespondDraw);
            guest.RespondDraw(false);
            PumpVersionSessions(host, guest);
            Assert.Equal(3, host.Battle.Record.Version);
            Assert.Equal(3, guest.Battle.Record.Version);
            Assert.Equal(3, host.Battle.RulesVersion);
            Assert.Equal(3, guest.Battle.RulesVersion);
            Assert.Null(host.Battle.Record.RulesVersion);
            Assert.Null(guest.Battle.Record.RulesVersion);
            Assert.Equal("", host.Battle.EndReason);
        }
        while (host.Battle.Phase != Phase.Over)
            NetworkTurn(host, guest, BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(NetState.Over, host.State);
        Assert.Equal(NetState.Over, guest.State);
        Assert.Equal(3, host.Battle.RulesVersion);
        Assert.Equal(3, guest.Battle.RulesVersion);
        Assert.Equal(3, host.Battle.Record.Version);
        Assert.Equal(3, guest.Battle.Record.Version);
        Assert.Null(host.Battle.Record.RulesVersion);
        Assert.Null(guest.Battle.Record.RulesVersion);
        Assert.Equal(120, host.Battle.Turn);
        Assert.Equal(100, BattleSummary.From(host.Battle).Sides[0].Sum(stats => stats.DamageDealt));
        Assert.Equal(host.Battle.Record.ToJson(), guest.Battle.Record.ToJson());
        Assert.Equal(FullLog(host.Battle), FullLog(guest.Battle));
        Ended(host.Battle, 0, "回合上限", "累计伤害");
        Ended(guest.Battle, 0, "回合上限", "累计伤害");
        TerminationCopiesMatch(host.Battle);
    }

    static void TerminationCopiesMatch(Battle battle)
    {
        string record = battle.Record.ToJson();
        ulong hash = battle.StateHash();
        var replay = Replay.Run(battle.Db, BattleRecord.FromJson(record));
        var cursor = new ReplayCursor(battle.Db, BattleRecord.FromJson(record), snapshotEvery: 1);
        Assert.Equal("", cursor.Current.EndReason);
        while (cursor.Next()) { }
        var stepped = cursor.Current.Clone();
        Assert.True(cursor.Prev());
        Assert.Equal("", cursor.Current.EndReason);
        Assert.Equal("", BattleSummary.From(cursor.Current).EndReason);
        cursor.ToEnd();
        foreach (var copy in new[] { battle.Clone(), replay, stepped, cursor.Current })
        {
            Assert.Equal(battle.RulesVersion, copy.RulesVersion);
            Assert.Equal(battle.Phase, copy.Phase);
            Assert.Equal(battle.Winner, copy.Winner);
            Assert.Equal(battle.EndReason, copy.EndReason);
            Assert.Equal(battle.EndReason, BattleSummary.From(copy).EndReason);
            Assert.Equal(record, copy.Record.ToJson());
            Assert.Equal(hash, copy.StateHash());
            Assert.Equal(FullLog(battle), FullLog(copy));
            Assert.Equal(FullEvents(battle), FullEvents(copy));
            Assert.Equal(BattleSummary.From(battle).ToText(), BattleSummary.From(copy).ToText());
            Assert.Equal(battle.Clone().Random(), copy.Clone().Random());
            if (battle.Phase == Phase.Over) Ended(copy, battle.Winner, battle.EndReason);
            else Assert.DoesNotContain(copy.Events, item => item.Kind == BattleEventKind.End);
        }
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(record, battle.Record.ToJson());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, -1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, -1)]
    public void SurrenderReasonSurvivesCloneReplayAndCursorAfterRealDamage(bool capped, int surrendering)
    {
        var battle = Make(capped);
        HitThenHeal(battle, 0);
        Assert.Equal(100, BattleSummary.From(battle).Sides[0].Sum(stats => stats.DamageDealt));
        Assert.Equal("", battle.EndReason);
        int turn = battle.Turn;
        int damageEvents = battle.Events.Count(item => item.Kind == BattleEventKind.Damage);
        if (surrendering < 0) battle.ResolveSurrenders(true, true);
        else battle.Surrender(surrendering);
        int winner = surrendering < 0 ? -1 : 1 - surrendering;
        Ended(battle, winner, "投降", "投降");
        Assert.Equal(turn, battle.Turn);
        Assert.Equal(damageEvents, battle.Events.Count(item => item.Kind == BattleEventKind.Damage));
        Assert.Equal(2, BattleSummary.From(battle).Turns);
        TerminationCopiesMatch(battle);
        int logCount = battle.Log.Count, eventCount = battle.Events.Count;
        Assert.Throws<InvalidOperationException>(() => battle.Surrender(0));
        Assert.Throws<InvalidOperationException>(() => battle.ResolveSurrenders(true, true));
        Assert.Equal(logCount, battle.Log.Count);
        Assert.Equal(eventCount, battle.Events.Count);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public void RejectedDrawHasNoEndReasonAndAcceptedDrawKeepsAgreementReasonEverywhere(bool capped, int offering)
    {
        var battle = Make(capped);
        HitThenHeal(battle, 0);
        battle.OfferDraw(offering);
        Assert.Equal("", battle.EndReason);
        Assert.Equal("", BattleSummary.From(battle).EndReason);
        battle.RespondDraw(1 - offering, false);
        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.Equal("", battle.EndReason);
        Assert.Equal("", BattleSummary.From(battle).EndReason);
        Assert.DoesNotContain(battle.Events, item => item.Kind == BattleEventKind.End);
        TerminationCopiesMatch(battle);

        battle.OfferDraw(1 - offering);
        battle.RespondDraw(offering, true);
        Ended(battle, -1, "和局", "同意和局");
        Assert.Equal(100, BattleSummary.From(battle).Sides[0].Sum(stats => stats.DamageDealt));
        Assert.Equal(2, BattleSummary.From(battle).Turns);
        TerminationCopiesMatch(battle);
        int logCount = battle.Log.Count, eventCount = battle.Events.Count;
        Assert.Throws<InvalidOperationException>(() => battle.OfferDraw(0));
        Assert.Throws<InvalidOperationException>(() => battle.RespondDraw(1, true));
        Assert.Equal(logCount, battle.Log.Count);
        Assert.Equal(eventCount, battle.Events.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DrawOffersDoNotReplaceTheCapReasonOrChangeItsRulesAfterVersionUpgrade(bool reject)
    {
        var battle = Make();
        HitThenHeal(battle, 0);
        battle.OfferDraw(1);
        if (reject) battle.RespondDraw(0, false);
        Assert.Equal(3, battle.Record.Version);
        Assert.Equal("", battle.EndReason);
        Assert.Equal(!reject, battle.HasDrawOffer(1));
        Assert.Equal(60, battle.EffectiveTurnCap);
        while (battle.Phase != Phase.Over) Pass(battle);
        Ended(battle, 0, "回合上限", "累计伤害");
        Assert.Equal(60, battle.Turn);
        Assert.Equal(60, BattleSummary.From(battle).Turns);
        Assert.Equal(3, battle.Record.Version);
        Assert.Equal(new[] { "turn_cap_60" }, battle.Record.Modifiers);
        TerminationCopiesMatch(battle);
    }

    [Theory]
    [InlineData(false, "surrender")]
    [InlineData(true, "surrender")]
    [InlineData(false, "double")]
    [InlineData(true, "double")]
    [InlineData(false, "draw")]
    [InlineData(true, "draw")]
    [InlineData(false, "cap")]
    [InlineData(true, "cap")]
    public void TerminationAndCapTemplatesRemainSafeWithoutChangingRawHistoryOrHash(bool capped, string ending)
    {
        string first = "甲 投降，乙\n\u200b" + new string('长', 18);
        string second = "乙 同意和局，双方平局\u202e" + new string('名', 18);
        var battle = new Battle(Content(), Teams, new[] { first, second }, new BattleOptions
        {
            Seed = 99, RulesVersion = capped ? 3 : 2, Modifiers = capped ? new List<string> { "turn_cap_60" } : null
        });
        HitThenHeal(battle, 0);
        int logStart = battle.Log.Count;
        if (ending == "surrender") battle.Surrender(1);
        else if (ending == "double") battle.ResolveSurrenders(true, true);
        else if (ending == "draw")
        {
            battle.OfferDraw(0);
            battle.RespondDraw(1, false);
            battle.OfferDraw(1);
            battle.RespondDraw(0, true);
        }
        else while (battle.Phase != Phase.Over) Pass(battle);
        string expectedReason = ending == "cap" ? "回合上限" : ending == "draw" ? "和局" : "投降";
        int winner = ending == "surrender" || ending == "cap" && capped ? 0 : -1;
        Ended(battle, winner, expectedReason);
        var rawLog = FullLog(battle);
        string rawRecord = battle.Record.ToJson();
        ulong hash = battle.StateHash();
        var display = TextInput.ForDisplay(battle);
        string cleanFirst = TextInput.Clean(first, TextInputKind.PlayerName).Text;
        string cleanSecond = TextInput.Clean(second, TextInputKind.PlayerName).Text;
        string expectedLast = ending == "surrender" ? cleanSecond + " 投降，" + cleanFirst + " 获胜"
            : ending == "double" ? "双方同时投降，平局"
            : ending == "draw" ? cleanFirst + " 同意和局，双方平局"
            : capped ? "回合上限（60 回合）：双方剩余人数和剩余人气比例总和相同，按累计伤害判定，" + cleanFirst + " 获胜"
            : "达到 120 回合上限，按剩余人气判定";
        Assert.Equal(expectedLast, display.Log.Last().Text);
        if (ending == "draw")
            Assert.Equal(new[]
            {
                cleanFirst + " 提议和局", cleanSecond + " 拒绝和局，继续对战",
                cleanSecond + " 提议和局", cleanFirst + " 同意和局，双方平局"
            }, display.Log.Skip(logStart).Select(entry => entry.Text));
        foreach (var entry in display.Log)
        {
            // 按实际字符检查；文化比较可能把零宽格式字符视为空串。
            Assert.DoesNotContain("\n", entry.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("\u200b", entry.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("\u202e", entry.Text, StringComparison.Ordinal);
        }
        Assert.Equal(expectedReason, display.EndReason);
        Assert.Equal(expectedReason, BattleSummary.From(display).EndReason);
        Assert.Equal(hash, display.StateHash());
        Assert.Equal(FullEvents(battle), FullEvents(display));
        Assert.Equal(rawLog, FullLog(battle));
        Assert.Equal(rawRecord, battle.Record.ToJson());
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(new[] { first, second }, battle.Record.Names);
        Assert.Equal(new[] { first, second }, battle.Sides.Select(side => side.Name));
        Assert.Equal(FullLog(display), FullLog(TextInput.ForDisplay(display)));
        var replay = Replay.Run(battle.Db, BattleRecord.FromJson(rawRecord));
        Assert.Equal(hash, replay.StateHash());
        Assert.Equal(rawLog, FullLog(replay));
        Assert.Equal(FullLog(display), FullLog(TextInput.ForDisplay(replay)));
    }
}
