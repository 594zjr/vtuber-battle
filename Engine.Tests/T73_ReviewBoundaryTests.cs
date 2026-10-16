using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T73_ReviewBoundaryTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    static object Effect(string code, string target = "foe", int n = 0, int turns = -1)
    {
        return new { code, target, n, turns, at = "post" };
    }

    static object Skill(int pp, params object[] effects)
    {
        return new { name = "边界验收技能", type = "talk", cat = "status", power = 0, pp, prio = 5, effects };
    }

    static ContentDb Load(int restore = 4, int damage = 37, int turns = 3,
        string target = "foe", string foePersona = "review_neutral")
    {
        var pack = new
        {
            id = "t73_boundary", name = "效果边界验收包", notice = "仅供规则测试使用。",
            skills = new Dictionary<string, object>
            {
                ["review_delay"] = Skill(10, Effect("DELAYED_DAMAGE", target, damage, turns)),
                // 参数故意写 foe；恢复 PP 的语义始终指向施放者。
                ["review_restore"] = Skill(10, Effect("PP_RESTORE", "foe", restore)),
                ["review_prepare"] = Skill(10,
                    new { code = "STAGE", at = "post", target = "self", changes = new { atk = 5 } },
                    new { code = "STAGE", at = "post", target = "foe", changes = new { def = 6 } }),
                ["review_copy"] = Skill(10, Effect("STAGE_COPY")),
                ["review_swap"] = Skill(7, Effect("STAGE_SWAP"))
            },
            personas = new Dictionary<string, object>
            {
                ["review_neutral"] = new { name = "中性测试人设", code = "T_LOWHP_POWER", @params = new { below = 0, mul = 1 } },
                ["review_endure"] = new { name = "保命测试人设", code = "T_ENDURE" },
                ["review_curse"] = new { name = "下播测试人设", code = "T_FAINT_CURSE", @params = new { n = -1 } }
            },
            streamers = Enumerable.Range(0, 6).Select(index => new
            {
                id = "r" + index, name = "验收主播" + index, type = "talk",
                persona = index == 1 ? foePersona : "review_neutral",
                stats = new { hp = 300, atk = 80, def = 80, spa = 80, sdef = 80, spd = 120 - index * 10 },
                skills = new[] { "review_delay", "review_restore", "review_prepare", "review_copy" },
                fifth = "review_swap"
            }).ToArray()
        };
        return ContentDb.FromJson(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", "StreamDuelRules.json")),
            JsonSerializer.Serialize(pack));
    }

    static Battle Make(ContentDb db = null, bool singleFighter = false, params string[] modifiers)
    {
        var teams = singleFighter
            ? new IList<string>[] { new[] { "r0" }, new[] { "r1" } }
            : new IList<string>[]
            {
                new[] { "r0", "r1", "r2", "r3", "r4", "r5" },
                new[] { "r1", "r2", "r3", "r4", "r5", "r0" }
            };
        return new Battle(db ?? Load(), teams, new[] { "甲方", "乙方" }, new BattleOptions
        {
            Seed = 73060, Modifiers = new List<string>(modifiers)
        });
    }

    static string Json(object value) { return JsonSerializer.Serialize(value, JsonOptions); }

    static void AssertReplay(Battle battle)
    {
        var replay = Replay.Run(battle.Db, BattleRecord.FromJson(battle.Record.ToJson()));
        Assert.Equal(battle.StateHash(), replay.StateHash());
        Assert.Equal(Json(battle.Log), Json(replay.Log));
        Assert.Equal(Json(battle.Events), Json(replay.Events));
        Assert.Equal(battle.Record.ToJson(), replay.Record.ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingDelayAndDrawStateBothSurviveCloneReplayAndTermination(bool acceptDraw)
    {
        var battle = Make();
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        battle.OfferDraw(0);
        var copy = battle.Clone();
        Assert.Equal(battle.StateHash(), copy.StateHash());
        Assert.True(copy.HasDrawOffer(0));
        Assert.NotSame(battle.Active(1).FieldOf("delayed_damage"), copy.Active(1).FieldOf("delayed_damage"));

        copy.RespondDraw(1, false);
        // 回应只改变和局状态，不能因为追加延迟伤害哈希而遗漏这个维度。
        Assert.NotEqual(battle.StateHash(), copy.StateHash());
        ulong declinedHash = copy.StateHash();
        copy.Active(1).FieldOf("delayed_damage").PendingDamage++;
        Assert.NotEqual(declinedHash, copy.StateHash());
        Assert.Equal(37, battle.Active(1).FieldOf("delayed_damage").PendingDamage);

        battle.RespondDraw(1, acceptDraw);
        if (!acceptDraw) battle.Surrender(0);
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(acceptDraw ? -1 : 1, battle.Winner);
        Assert.Equal(2, battle.Active(1).FieldOf("delayed_damage").Turns);
        Assert.Equal(300, battle.Active(1).Hp);
        Assert.Equal(BattleEventKind.End, battle.Events.Last().Kind);
        Assert.Equal(Node.Over, battle.Log.Last().Node);
        Assert.Equal(battle.StateHash(), battle.Clone().StateHash());
        AssertReplay(battle);

        ulong finalHash = battle.StateHash();
        string finalLog = Json(battle.Log), finalRecord = battle.Record.ToJson();
        Assert.Throws<InvalidOperationException>(() => battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass()));
        Assert.Equal(finalHash, battle.StateHash());
        Assert.Equal(finalLog, Json(battle.Log));
        Assert.Equal(finalRecord, battle.Record.ToJson());
    }

    [Theory]
    [InlineData(int.MaxValue, 5, 4)]
    [InlineData(0, 4, 0)]
    [InlineData(-7, 4, 0)]
    public void RestoreHonorsHalfPpCapsAndNonpositiveAmountsWithoutOverflow(int amount, int expectedPp, int restoredCount)
    {
        var battle = Make(Load(restore: amount), false, "half_pp", "stage_cap_3");
        Assert.Equal(new[] { 5, 5, 5, 5, 4 }, battle.Active(0).Skills.Select(skill => skill.Max));
        foreach (int slot in new[] { 0, 2, 3, 4 })
            battle.ResolveTurn(BattleAction.Skill(slot), BattleAction.Pass());
        int eventStart = battle.Events.Count;

        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());

        Assert.Equal(new[] { expectedPp, expectedPp, expectedPp, expectedPp, 3 },
            battle.Active(0).Skills.Select(skill => skill.Pp));
        Assert.Equal(new[] { 5, 5, 5, 5, 4 }, battle.Active(1).Skills.Select(skill => skill.Pp));
        var restored = battle.Events.Skip(eventStart)
            .Where(entry => entry.Kind == BattleEventKind.PpChange && entry.Amount > 0).ToArray();
        Assert.Equal(restoredCount, restored.Length);
        Assert.All(restored, entry =>
        {
            Assert.Equal(0, entry.Side);
            Assert.Equal(1, entry.Amount);
            Assert.NotEqual("review_swap", entry.Id);
        });
        AssertReplay(battle);
    }

    [Theory]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void AiSearchWithPendingDamageDoesNotMutateLiveFieldsHistoryOrDrawOffer(AiLevel level)
    {
        var battle = Make();
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        battle.OfferDraw(0);
        var copy = battle.Clone();
        string sides = Json(battle.Sides), log = Json(battle.Log), events = Json(battle.Events);
        string record = battle.Record.ToJson();

        BattleAction actual = Ai.Choose(battle, 1, level), repeated = Ai.Choose(copy, 1, level);

        Assert.Equal(actual.Type, repeated.Type);
        Assert.Equal(actual.Index, repeated.Index);
        Assert.Equal(battle.StateHash(), copy.StateHash());
        Assert.True(actual.Type == ActionType.Skill || actual.Type == ActionType.Switch);
        var legal = battle.LegalFor(1);
        if (actual.Type == ActionType.Skill) Assert.True(legal.Skills[actual.Index].Usable);
        else Assert.Contains(actual.Index, legal.Switches);
        Assert.Equal(sides, Json(battle.Sides));
        Assert.Equal(log, Json(battle.Log));
        Assert.Equal(events, Json(battle.Events));
        Assert.Equal(record, battle.Record.ToJson());
        Assert.True(battle.HasDrawOffer(0));
        Assert.Equal(1, battle.Active(1).FieldOf("delayed_damage").Turns);
        Assert.Equal(37, battle.Active(1).FieldOf("delayed_damage").PendingDamage);
    }

    [Fact]
    public void FieldViewsShowCurrentDamageAndCountdownWithoutChangingOlderSnapshots()
    {
        var battle = Make();
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        var first = BattleViewModel.From(battle, 0);
        var firstChip = Assert.Single(first.Sides[1].Team[0].Statuses, chip => chip.Id == "delayed_damage");
        Assert.False(firstChip.IsStatus);
        Assert.True(firstChip.CanDispel);
        Assert.Equal(2, firstChip.Turns);
        Assert.Contains("剩余 2 回合", firstChip.Tooltip);
        Assert.Contains("37 点固定伤害", firstChip.Tooltip);

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        var second = BattleViewModel.From(battle, 1);
        var secondChip = Assert.Single(second.Sides[1].Team[0].Statuses, chip => chip.Id == "delayed_damage");
        Assert.Equal(1, secondChip.Turns);
        Assert.Contains("剩余 1 回合", secondChip.Tooltip);
        Assert.Contains("37 点固定伤害", secondChip.Tooltip);
        Assert.Equal(2, firstChip.Turns);
        Assert.Contains("剩余 2 回合", firstChip.Tooltip);

        var display = TextInput.ForDisplay(battle);
        Assert.Equal(battle.StateHash(), display.StateHash());
        display.Active(1).FieldOf("delayed_damage").PendingDamage = 99;
        Assert.Equal(37, battle.Active(1).FieldOf("delayed_damage").PendingDamage);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.DoesNotContain(BattleViewModel.From(battle, 0).Sides[1].Team[0].Statuses,
            chip => chip.Id == "delayed_damage");
        Assert.Equal(1, secondChip.Turns);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void StageTransferSkillViewsUseTheActiveStageCap(int skillIndex)
    {
        var battle = Make(null, false, "stage_cap_3");
        var effect = battle.Db.Skills[battle.Active(0).Skills[skillIndex].Id].Effects.Single();
        var description = Describe.Effect(battle.Db, effect, battle);
        var view = BattleViewModel.From(battle, 0).Skills[skillIndex].Effects.Single();
        Assert.Contains("+3", description.Text);
        Assert.DoesNotContain("+6", description.Text);
        Assert.Contains("+6", Describe.Effect(battle.Db, effect).Text);
        Assert.Equal(description.Text, view.Text);
        Assert.Equal(description.Dispel, view.Dispel);
        Assert.Equal(Describe.EffectTiming("post"), view.Timing);
        Assert.Contains("消强化", view.Dispel);
    }

    [Fact]
    public void DelayHonorsEndureOnceAndItsSecondApplicationCanFaintTheTarget()
    {
        var battle = Make(Load(damage: 999, turns: 1, foePersona: "review_endure"));
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.Equal(1, battle.Active(1).Hp);
        Assert.False(battle.Active(1).Fainted);
        Assert.True(battle.Active(1).EndureUsed);
        Assert.Null(battle.Active(1).FieldOf("delayed_damage"));
        Assert.Equal(299, Assert.Single(battle.Events, entry => entry.Kind == BattleEventKind.Damage).Amount);

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.True(battle.Active(1).Fainted);
        Assert.Equal(Phase.Replace, battle.Phase);
        Assert.Equal(new[] { 299, 1 }, battle.Events.Where(entry => entry.Kind == BattleEventKind.Damage).Select(entry => entry.Amount));
        Assert.Single(battle.Events, entry => entry.Kind == BattleEventKind.Faint && entry.Side == 1);
        AssertReplay(battle);
    }

    [Fact]
    public void DelayFaintPersonaRunsBeforeTheFinalEndEvent()
    {
        // 单人队伍仅隔离最后一名下播的引擎边界，不绕过录像中的实际技能行动。
        var battle = Make(Load(damage: 300, turns: 1, foePersona: "review_curse"), true);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(0, battle.Winner);
        Assert.All(battle.Active(0).Stages.Values, stage => Assert.Equal(-1, stage));
        var changes = battle.Events.Where(entry => entry.Kind == BattleEventKind.StageChange).ToArray();
        Assert.Equal(5, changes.Length);
        Assert.All(changes, entry =>
        {
            Assert.Equal(0, entry.Side);
            Assert.Equal(-1, entry.Amount);
            Assert.Equal(Node.TurnEnd, entry.Node);
        });
        Assert.Equal(BattleEventKind.End, battle.Events.Last().Kind);
        Assert.Equal(Node.Over, battle.Log.Last().Node);
        AssertReplay(battle);
    }

    [Fact]
    public void FinalDelayedFaintStopsTheOtherSidesDueDamage()
    {
        var battle = Make(Load(damage: 300, turns: 1, target: "self"), true);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(1, battle.Winner);
        Assert.True(battle.Active(0).Fainted);
        Assert.Equal(300, battle.Active(1).Hp);
        Assert.Equal(1, battle.Active(1).FieldOf("delayed_damage").Turns);
        Assert.Equal(300, battle.Active(1).FieldOf("delayed_damage").PendingDamage);
        var damage = Assert.Single(battle.Events, entry => entry.Kind == BattleEventKind.Damage);
        Assert.Equal(0, damage.Side);
        Assert.Equal(300, damage.Amount);
        Assert.Equal(Node.TurnEnd, damage.Node);
        Assert.Equal(BattleEventKind.End, battle.Events.Last().Kind);
        Assert.Equal(Node.Over, battle.Log.Last().Node);
        AssertReplay(battle);
    }
}
