using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T25_ProfileIntegrationTests
{
    static readonly IList<string>[] Teams =
    {
        new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
        new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
    };

    static readonly string[] Banned = { "bell", "nyako" };

    static Battle MakeAfterBanAndLead()
    {
        var battle = new Battle(BattleTests.LoadPack("original"), Teams, new[] { "A", "B" },
            new BattleOptions { Seed = 17, BanOne = true, LeadPick = true });
        battle.ChooseBan(0, 2);
        battle.ChooseBan(1, 0);
        Assert.Equal(Phase.Lead, battle.Phase);
        battle.ChooseLead(0, 4);
        battle.ChooseLead(1, 3);
        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.Equal("momo", battle.Active(0).Def.Id);
        Assert.Equal("kasumi", battle.Active(1).Def.Id);
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(5, battle.Sides[side].Team.Count);
            Assert.Equal(6, battle.Record.Teams[side].Count);
            Assert.Contains(Banned[side], battle.Record.Teams[side]);
        }
        return battle;
    }

    static void AssertStats(Battle battle, int playerSide, PackStats stats)
    {
        bool won = battle.Winner == playerSide;
        Assert.Equal(won ? 1 : 0, stats.Wins);
        Assert.Equal(battle.Winner >= 0 && !won ? 1 : 0, stats.Losses);
        Assert.Equal(battle.Winner == -1 ? 1 : 0, stats.Draws);
        var expected = Teams[playerSide].Where(id => id != Banned[playerSide])
            .OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, stats.Picks.Keys.OrderBy(id => id, StringComparer.Ordinal));
        Assert.DoesNotContain(Banned[playerSide], stats.Picks.Keys);
        Assert.DoesNotContain(Banned[playerSide], stats.PickWins.Keys);
        foreach (string id in expected)
        {
            Assert.Equal(1, stats.Picks[id]);
            Assert.Equal(won ? 1 : 0, stats.PickWins.TryGetValue(id, out int wins) ? wins : 0);
        }
        Assert.All(stats.PickWins.Keys, id => Assert.Contains(id, expected));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, -1)]
    public void RecordsOnlyFinalRosterIncludingFaintedLeadAndUnenteredBench(int playerSide, int winner)
    {
        var battle = MakeAfterBanAndLead();
        string lead = battle.Active(playerSide).Def.Id;
        var bench = battle.Sides[playerSide].Team.Skip(1).Select(f => f.Def.Id).ToArray();
        Assert.Equal(4, bench.Length);
        Assert.DoesNotContain(battle.Record.Events, e => e.Type == BattleRecordEventType.Turn || e.Type == BattleRecordEventType.Replace);

        // 真实禁用、首发后只设置结局，分别隔离胜负平统计与下播、未登场名单。
        battle.Active(playerSide).Hp = 0;
        battle.Active(playerSide).Fainted = true;
        battle.Phase = Phase.Over;
        battle.Winner = winner;
        var profile = new Profile();
        profile.Record(battle, playerSide);

        var stats = Assert.Single(profile.Packs).Value;
        AssertStats(battle, playerSide, stats);
        Assert.Equal(1, stats.Picks[lead]);
        Assert.All(bench, id => Assert.Equal(1, stats.Picks[id]));
    }

    [Fact]
    public void CompletedBannedBattleRecordsFivePicksFromEitherPerspective()
    {
        var battle = MakeAfterBanAndLead();
        int guard = 0;
        while (battle.Phase != Phase.Over && guard++ < 500)
        {
            if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
            else
            {
                Assert.Equal(Phase.Replace, battle.Phase);
                for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
            }
        }
        Assert.Equal(Phase.Over, battle.Phase);
        for (int side = 0; side < 2; side++)
        {
            var profile = new Profile();
            profile.Record(battle, side);
            AssertStats(battle, side, Assert.Single(profile.Packs).Value);
            Assert.Equal(profile.ToJson(), Profile.FromJson(profile.ToJson()).ToJson());
        }
    }
}
