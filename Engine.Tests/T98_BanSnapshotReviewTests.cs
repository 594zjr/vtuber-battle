using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T98_BanSnapshotReviewTests
{
    static readonly string[][] Teams =
    {
        new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
        new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
    };

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 2)]
    public void RejectedDrawBeforeBanKeepsSnapshotsBoundToSurvivingFighters(int banned0, int banned1)
    {
        var db = BattleTests.LoadPack("original");
        var battle = Make(db);
        var baseline = Make(db);
        var originalHp = battle.Sides.Select(side => side.Team
            .ToDictionary(fighter => fighter.Def.Id, fighter => fighter.Hp)).ToArray();
        Assert.All(originalHp, side => Assert.True(side.Values.Distinct().Count() > 1));

        battle.OfferDraw(0);
        battle.RespondDraw(1, false);
        var negotiation = battle.Log.ToArray();
        Assert.Equal(2, negotiation.Length);
        Assert.All(negotiation, line => Assert.All(line.SnapHp, hp => Assert.Equal(6, hp.Length)));
        string[] beforeFirstBan = FullLog(battle);

        battle.ChooseBan(0, banned1);
        baseline.ChooseBan(0, banned1);
        Assert.Equal(beforeFirstBan, FullLog(battle));
        battle.ChooseBan(1, banned0);
        baseline.ChooseBan(1, banned0);
        Assert.Equal(Phase.Lead, battle.Phase);
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(5, battle.Sides[side].Team.Count);
            foreach (var line in negotiation)
            {
                Assert.Equal(battle.Sides[side].Team.Select(fighter => originalHp[side][fighter.Def.Id]), line.SnapHp[side]);
                Assert.Equal(0, line.SnapActive[side]);
            }
        }

        string[] firstSurvivors = battle.Sides.Select(side => side.Team[0].Def.Id).ToArray();
        battle.ChooseLead(0, 3);
        baseline.ChooseLead(0, 3);
        battle.ChooseLead(1, 2);
        baseline.ChooseLead(1, 2);
        Assert.Equal(Phase.Choose, battle.Phase);
        for (int side = 0; side < 2; side++)
        {
            var team = battle.Sides[side].Team;
            foreach (var line in negotiation)
            {
                Assert.Equal(team.Select(fighter => originalHp[side][fighter.Def.Id]), line.SnapHp[side]);
                Assert.Equal(1, line.SnapActive[side]);
                Assert.Equal(firstSurvivors[side], team[line.SnapActive[side]].Def.Id);
            }
        }

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        baseline.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        battle.Surrender(0);
        baseline.Surrender(0);
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(1, BattleSummary.From(battle).Turns);
        Assert.Equal(BattleSummary.From(baseline).ToText(), BattleSummary.From(battle).ToText());
        for (int side = 0; side < 2; side++)
            Assert.Equal(Moments(baseline, side), Moments(battle, side));

        string json = battle.Record.ToJson();
        var replayed = Replay.Run(db, BattleRecord.FromJson(json));
        var clone = battle.Clone();
        foreach (var copy in new[] { replayed, clone })
        {
            Assert.Equal(battle.StateHash(), copy.StateHash());
            Assert.Equal(json, copy.Record.ToJson());
            Assert.Equal(FullLog(battle), FullLog(copy));
            Assert.Equal(BattleSummary.From(battle).ToText(), BattleSummary.From(copy).ToText());
            for (int side = 0; side < 2; side++)
                Assert.Equal(BattleReport.Markdown(battle, side), BattleReport.Markdown(copy, side));
        }
        Assert.NotSame(battle.Log[0].SnapHp[0], clone.Log[0].SnapHp[0]);
        Assert.NotSame(battle.Log[0].SnapActive, clone.Log[0].SnapActive);
        for (int i = 0; i < 4; i++) Assert.Equal(replayed.Random(), clone.Random());
    }

    static Battle Make(ContentDb db)
    {
        return new Battle(db, new IList<string>[] { Teams[0], Teams[1] }, new[] { "甲方", "乙方" },
            new BattleOptions { Seed = 98, BanOne = true, LeadPick = true });
    }

    static string[] Moments(Battle battle, int side)
    {
        return BattleReview.KeyMoments(battle, side, 1000)
            .Select(moment => string.Join("|", moment.Turn, moment.Kind, moment.Swing, moment.Text)).ToArray();
    }

    static string[] FullLog(Battle battle)
    {
        return battle.Log.Select(line => string.Join("|", line.Turn, line.Node, line.Kind, line.Side, line.Text,
            string.Join(",", line.SnapActive), string.Join(",", line.SnapHp[0]), string.Join(",", line.SnapHp[1]))).ToArray();
    }
}
