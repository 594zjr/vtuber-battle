using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T71_OnlineViewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacementViewOffersLegalReservesEvenWhenVoluntarySwitchingIsLocked(bool modifier)
    {
        var db = BattleTests.LoadPack("original");
        var team = new[] { "bell", "enka", "shiori", "lumi", "hane", "yuan" };
        var battle = new Battle(db, new IList<string>[] { team, team }, new[] { "我方", "对手" },
            new BattleOptions { Seed = 71, Modifiers = modifier ? new List<string> { "no_switch" } : null });
        if (!modifier) battle.Active(0).Fields.Add(new FieldInst { Id = "lockin", Turns = 3 });
        Assert.Empty(BattleViewModel.From(battle, 0).SwitchTargets);

        battle.Active(0).Hp = 0;
        battle.Active(0).Fainted = true;
        battle.Sides[0].Team[2].Hp = 0;
        battle.Sides[0].Team[2].Fainted = true;
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(Phase.Replace, battle.Phase);
        Assert.True(battle.Need[0]);
        Assert.Empty(battle.LegalFor(0).Switches);
        ulong hash = battle.StateHash();
        var view = BattleViewModel.From(battle, 0);
        Assert.Equal(new[] { 1, 3, 4, 5 }, view.SwitchTargets);
        Assert.Empty(BattleViewModel.From(battle, 1).SwitchTargets);
        Assert.Equal(hash, battle.StateHash());
        foreach (int target in view.SwitchTargets.ToArray())
        {
            var copy = battle.Clone();
            copy.Replace(0, target);
            Assert.Equal(target, copy.Sides[0].Active);
            Assert.False(copy.Need[0]);
        }
    }
}
