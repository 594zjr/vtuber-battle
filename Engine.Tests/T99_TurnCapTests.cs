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

    static Battle Make(bool capped = true, ContentDb db = null, bool ban = false, bool lead = false)
    {
        return new Battle(db ?? Content(), Teams, new[] { "甲方", "乙方" }, new BattleOptions
        {
            Seed = 99, BanOne = ban, LeadPick = lead,
            Modifiers = capped ? new List<string> { "turn_cap_60" } : null
        });
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
        if (battle.EffectiveTurnCap == 60 || reason != "回合上限")
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
    public void ModifierIsExplicitAndTheDefaultStaysAt120WithVersionTwo()
    {
        var normal = Make(false);
        var capped = Make();
        Assert.Equal(120, Battle.TurnCap);
        Assert.Equal(120, normal.EffectiveTurnCap);
        Assert.Equal(60, capped.EffectiveTurnCap);
        Assert.Equal(2, normal.Record.Version);
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
    [InlineData(0)]
    [InlineData(1)]
    public void MoreSurvivorsWinsEvenWithLessRemainingHp(int winner)
    {
        var battle = Make();
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
    [InlineData(0)]
    [InlineData(1)]
    public void HpRatioPrecedesDamageAndDoesNotCompareRawHp(int winner)
    {
        var battle = Make();
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

    [Fact]
    public void ExactHpComparisonDetectsADifferenceBelowDoublePrecision()
    {
        var battle = Make();
        battle.Active(0).Hp = battle.Active(1).Hp = 1;
        battle.Active(0).MaxHp = int.MaxValue - 1;
        battle.Active(1).MaxHp = int.MaxValue;
        Assert.Equal(battle.Sides[0].Team.Sum(fighter => fighter.HpRatio),
            battle.Sides[1].Team.Sum(fighter => fighter.HpRatio));
        AtCap(battle);
        Ended(battle, 0, "回合上限", "人气");
    }

    [Fact]
    public void MathematicallyEqualFractionsRemainATieDespiteFloatingPointSummation()
    {
        var battle = Make();
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
    [InlineData(0)]
    [InlineData(1)]
    public void ActualDamageBreaksTheTieAfterTheOpponentHeals(int winner)
    {
        var battle = Make();
        HitThenHeal(battle, winner);
        Assert.All(battle.Sides.SelectMany(side => side.Team), fighter => Assert.Equal(fighter.MaxHp, fighter.Hp));
        Assert.Equal(100, BattleSummary.From(battle).Sides[winner].Sum(stats => stats.DamageDealt));
        Assert.Contains(battle.Events, item => item.Kind == BattleEventKind.Heal && item.Amount == 100);
        AtCap(battle);
        Ended(battle, winner, "回合上限", "伤害");
    }

    [Fact]
    public void SelfDamageDoesNotOutscoreRealDamageToTheOpponent()
    {
        var battle = Make();
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Skill(0));
        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        var summary = BattleSummary.From(battle);
        Assert.Equal(0, summary.Sides[0].Sum(stats => stats.DamageDealt));
        Assert.Equal(100, summary.Sides[1].Sum(stats => stats.DamageDealt));
        Assert.Equal(500, summary.Sides[0].Sum(stats => stats.DamageTaken));
        AtCap(battle);
        Ended(battle, 1, "回合上限", "伤害");
    }

    [Fact]
    public void EqualSurvivorsRatiosAndActualDamageEndInADraw()
    {
        var battle = Make();
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
    public void Default120RetainsItsLegacyHpDecisionAndExactEndingLog()
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
        // 默认规则的相等对照证明伤害归因以外的哈希状态已对齐。
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

    [Fact]
    public void TurnCapWinnerNameIsCleanedOnlyInTheDisplayProjection()
    {
        string rawName = "甲方\n\u200b超长超长超长超长超长超长名字";
        var battle = new Battle(Content(), Teams, new[] { rawName, "乙方" }, new BattleOptions
        {
            Seed = 99, Modifiers = new List<string> { "turn_cap_60" }
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
}
