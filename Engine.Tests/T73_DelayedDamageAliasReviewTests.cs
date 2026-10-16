using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public sealed class T73_DelayedDamageAliasReviewTests
{
    [Theory]
    [InlineData(null, "延迟伤害")]
    [InlineData("预约爆发", "预约爆发")]
    public void LifecycleLogsUseTheFieldDisplayNameAndKeepDefaultText(string alias, string name)
    {
        var battle = Make(alias);
        var target = battle.Active(1);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        var chip = Assert.Single(BattleViewModel.From(battle, 0).Sides[1].Team[0].Statuses,
            item => item.Id == ContentDb.DelayedDamageFieldId);
        Assert.Equal(name, chip.Name);
        Assert.Equal(1, target.FieldOf(ContentDb.DelayedDamageFieldId).Turns);
        Assert.Equal(37, target.FieldOf(ContentDb.DelayedDamageFieldId).PendingDamage);

        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        Assert.Equal(2, target.FieldOf(ContentDb.DelayedDamageFieldId).Turns);
        Assert.Equal(13, target.FieldOf(ContentDb.DelayedDamageFieldId).PendingDamage);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        Assert.Equal(new[]
        {
            "测试主播1 获得「" + name + "」（2 回合，含本回合；到期固定伤害 37，可被消回合，下场清除）",
            "测试主播1 的「" + name + "」剩余 1 回合，到期固定伤害 37",
            "测试主播1 刷新「" + name + "」（3 回合，含本回合；到期固定伤害 13，可被消回合，下场清除）",
            "测试主播1 的「" + name + "」剩余 2 回合，到期固定伤害 13",
            "测试主播1 的「" + name + "」剩余 1 回合，到期固定伤害 13",
            "测试主播1 的「" + name + "」到期（固定伤害 13）",
            "测试主播1 因「" + name + "」人气 -13"
        }, NamedLogs(battle, name));
        Assert.Null(target.FieldOf(ContentDb.DelayedDamageFieldId));
        Assert.Equal(287, target.Hp);
        var damage = Assert.Single(battle.Events, entry => entry.Kind == BattleEventKind.Damage);
        Assert.Equal(13, damage.Amount);
        Assert.Equal(Node.TurnEnd, damage.Node);
        Assert.Equal(4, damage.Turn);
        Assert.Equal(new[] { 2, 3 }, battle.Events
            .Where(entry => entry.Kind == BattleEventKind.FieldAdd && entry.Id == ContentDb.DelayedDamageFieldId)
            .Select(entry => entry.Amount));
        Assert.Single(battle.Events, entry => entry.Kind == BattleEventKind.FieldRemove
            && entry.Id == ContentDb.DelayedDamageFieldId);
    }

    [Theory]
    [InlineData(null, "延迟伤害")]
    [InlineData("预约爆发", "预约爆发")]
    public void EarlyKnockoutLogUsesTheFieldDisplayNameWithoutApplyingPendingDamage(string alias, string name)
    {
        var battle = Make(alias);
        var target = battle.Active(1);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());

        Assert.Equal(new[]
        {
            "测试主播1 获得「" + name + "」（2 回合，含本回合；到期固定伤害 37，可被消回合，下场清除）",
            "测试主播1 的「" + name + "」剩余 1 回合，到期固定伤害 37",
            "测试主播1 下播，清除「" + name + "」（待结算固定伤害 37）"
        }, NamedLogs(battle, name));
        Assert.True(target.Fainted);
        Assert.Equal(Phase.Replace, battle.Phase);
        Assert.Null(target.FieldOf(ContentDb.DelayedDamageFieldId));
        Assert.Equal(300, Assert.Single(battle.Events, entry => entry.Kind == BattleEventKind.Damage).Amount);
        var removed = Assert.Single(battle.Events, entry => entry.Kind == BattleEventKind.FieldRemove
            && entry.Id == ContentDb.DelayedDamageFieldId);
        Assert.Equal(Node.First, removed.Node);
        Assert.Equal(2, removed.Turn);
    }

    static string[] NamedLogs(Battle battle, string name)
    {
        return battle.Log.Where(entry => entry.Text.Contains("「" + name + "」", StringComparison.Ordinal))
            .Select(entry => entry.Text).ToArray();
    }

    static object Skill(params object[] effects)
    {
        return new { name = "测试技能", type = "talk", cat = "status", power = 0, pp = 10, prio = 5, effects };
    }

    static Battle Make(string alias)
    {
        var pack = new
        {
            id = "t73_alias_review", name = "延迟效果显示复验", notice = "仅供测试。",
            skills = new Dictionary<string, object>
            {
                ["pk_delay"] = Skill(new { code = "DELAYED_DAMAGE", at = "post", target = "foe", turns = 2, n = 37 }),
                ["pk_refresh"] = Skill(new { code = "DELAYED_DAMAGE", at = "post", target = "foe", turns = 3, n = 13 }),
                ["pk_hit"] = Skill(new { code = "FIXED_DAMAGE", at = "post", target = "foe", n = 9999 }),
                ["pk_wait"] = Skill(),
                ["pk_fifth"] = Skill()
            },
            personas = new Dictionary<string, object>
            {
                ["pk_neutral"] = new { name = "测试人设", code = "T_LOWHP_POWER", @params = new { below = 0, mul = 1 } }
            },
            fieldNames = alias == null ? new Dictionary<string, string>() :
                new Dictionary<string, string> { [ContentDb.DelayedDamageFieldId] = alias },
            streamers = Enumerable.Range(0, 6).Select(index => new
            {
                id = "p" + index, name = "测试主播" + index, type = "talk", persona = "pk_neutral",
                stats = new { hp = 300, atk = 80, def = 80, spa = 80, sdef = 80, spd = 120 - index * 10 },
                skills = new[] { "pk_delay", "pk_refresh", "pk_hit", "pk_wait" }, fifth = "pk_fifth"
            }).ToArray()
        };
        string rules = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", "StreamDuelRules.json"));
        var db = ContentDb.FromJson(rules, JsonSerializer.Serialize(pack));
        return new Battle(db, new IList<string>[]
        {
            new[] { "p0", "p1", "p2", "p3", "p4", "p5" },
            new[] { "p1", "p2", "p3", "p4", "p5", "p0" }
        }, new[] { "甲方", "乙方" }, seed: 73);
    }
}
