using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T49_RegressionTests
{
    static Battle Make(string lead0 = "kei", string lead1 = "bell")
    {
        var db = BattleTests.LoadPack("original");
        IList<string> Team(string lead)
        {
            return new[] { lead }.Concat(db.Streamers.Select(streamer => streamer.Id).Where(id => id != lead)).Take(6).ToArray();
        }
        return new Battle(db, new[] { Team(lead0), Team(lead1) }, new[] { "甲方", "乙方" }, 49);
    }

    static void FaintReserves(Battle battle, int side)
    {
        foreach (var fighter in battle.Sides[side].Team.Skip(1))
        {
            fighter.Hp = 0;
            fighter.Fainted = true;
        }
    }

    [Fact]
    public void LastFighterLeechFaintEndsBeforeHealingOpponent()
    {
        var battle = Make();
        FaintReserves(battle, 0);
        battle.Active(0).Hp = 1;
        battle.Active(0).Statuses.Add(new StatusInst { Id = "siphon", Left = 5 });
        battle.Active(1).Hp = 100;
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(1, battle.Winner);
        Assert.Equal(0, battle.Active(0).Hp);
        Assert.True(battle.Active(0).Fainted);
        Assert.Equal(100, battle.Active(1).Hp);
        Assert.DoesNotContain(battle.Log.Skip(from), entry => entry.Kind == "heal");
        Assert.Single(battle.Log.Skip(from), entry => entry.Node == Node.Over);
        Assert.Equal(Node.Over, battle.Log.Last().Node);
    }

    [Fact]
    public void LeechFaintStillHealsOpponentWhenReservesRemain()
    {
        var battle = Make();
        var target = battle.Active(0);
        target.Hp = 1;
        target.Statuses.Add(new StatusInst { Id = "siphon", Left = 5 });
        battle.Active(1).Hp = 100;
        int expectedHeal = Math.Max(1, (int)(target.MaxHp * battle.Db.Statuses["siphon"].Dot));
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        Assert.Equal(Phase.Replace, battle.Phase);
        Assert.True(battle.Need[0]);
        Assert.False(battle.Need[1]);
        Assert.True(target.Fainted);
        Assert.Equal(100 + expectedHeal, battle.Active(1).Hp);
        var heal = Assert.Single(battle.Log.Skip(from), entry => entry.Kind == "heal");
        Assert.Equal(1, heal.Side);
        Assert.Equal(Node.TurnEnd, heal.Node);
        Assert.Contains("引流", heal.Text);
        Assert.DoesNotContain(battle.Log.Skip(from), entry => entry.Node == Node.Over);
    }

    [Fact]
    public void TerminalDotSkipsOtherSidesRemainingHealingAndStageGain()
    {
        var battle = Make("bell", "bee");
        FaintReserves(battle, 0);
        battle.Active(0).Hp = 1;
        battle.Active(0).Statuses.Add(new StatusInst { Id = "hater", Left = 4 });
        var other = battle.Active(1);
        other.Hp = 100;
        other.Stages["spd"] = -1;
        var early = new FieldInst { Id = "early", Turns = 2 };
        var healing = new FieldInst { Id = "sub", Turns = 2 };
        other.Fields.Add(early);
        other.Fields.Add(healing);
        int overclock = other.Skills.FindIndex(skill => skill.Id == "overclock");
        Assert.True(overclock >= 0);
        int from = battle.Log.Count;

        // 先制让小蜂先行动，超频后反应仍低于铃铛，因此结束阶段先由铃铛的持续伤害终局。
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(overclock));

        Assert.True(battle.ActedFirst[1]);
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(1, battle.Winner);
        Assert.Equal(100, other.Hp);
        Assert.Equal(1, other.Stages["spa"]);
        Assert.Equal(0, other.Stages["spd"]);
        Assert.Equal(2, early.Turns);
        Assert.Equal(2, healing.Turns);
        Assert.DoesNotContain(battle.Log.Skip(from), entry => entry.Node == Node.TurnEnd && entry.Side == 1);
        Assert.Equal(Node.Over, battle.Log.Last().Node);
    }
}
