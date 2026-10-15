using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T25_LeadSnapshotIntegrationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BanSnapshotsKeepFighterIdentitiesAfterBothNonzeroLeadsAndReplay(int firstSide)
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["lead_snapshot_none"] = new PersonaDef
        {
            Id = "lead_snapshot_none", Name = "快照测试人设", Code = "TEST_NONE"
        };
        for (int i = 0; i < db.Streamers.Count; i++)
        {
            db.Streamers[i].Persona = "lead_snapshot_none";
            db.Streamers[i].Stats["hp"] = 100 + i * 17;
        }
        var battle = new Battle(db, new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "甲方", "乙方" }, new BattleOptions { Seed = 25, BanOne = true, LeadPick = true });
        battle.ChooseBan(0, 1);
        battle.ChooseBan(1, 3);
        Assert.Equal(Phase.Lead, battle.Phase);
        Assert.Equal(2, battle.Log.Count);
        Assert.All(battle.Log, entry => Assert.Equal("ban", entry.Kind));

        var originalTeams = battle.Sides.Select(side => side.Team.Select(fighter => fighter.Def.Id).ToArray()).ToArray();
        var originalHp = battle.Sides.Select(side => side.Team.ToDictionary(fighter => fighter.Def.Id, fighter => fighter.MaxHp)).ToArray();
        Assert.Equal(10, originalHp.SelectMany(side => side.Values).Distinct().Count());
        var choices = new[] { 4, 2 };
        string[] beforeLead = FullLog(battle);

        battle.ChooseLead(firstSide, choices[firstSide]);

        Assert.Equal(Phase.Lead, battle.Phase);
        Assert.Equal(beforeLead, FullLog(battle));
        for (int side = 0; side < 2; side++)
            Assert.Equal(originalTeams[side], battle.Sides[side].Team.Select(fighter => fighter.Def.Id));

        battle.ChooseLead(1 - firstSide, choices[1 - firstSide]);

        Assert.Equal(Phase.Choose, battle.Phase);
        var bans = battle.Log.Where(entry => entry.Kind == "ban").ToArray();
        Assert.Equal(2, bans.Length);
        for (int side = 0; side < 2; side++)
        {
            var finalTeam = battle.Sides[side].Team;
            Assert.Equal(originalTeams[side][choices[side]], finalTeam[0].Def.Id);
            Assert.Equal(originalTeams[side].Where((id, index) => index != choices[side]),
                finalTeam.Skip(1).Select(fighter => fighter.Def.Id));
            foreach (var entry in bans)
            {
                Assert.Equal(finalTeam.Select(fighter => originalHp[side][fighter.Def.Id]), entry.SnapHp[side]);
                Assert.Equal(1, entry.SnapActive[side]);
                Assert.Equal(originalTeams[side][0], finalTeam[entry.SnapActive[side]].Def.Id);
                Assert.Equal(originalHp[side][originalTeams[side][0]], entry.SnapHp[side][entry.SnapActive[side]]);
            }
            Assert.All(battle.Log.Skip(bans.Length), entry => Assert.Equal(0, entry.SnapActive[side]));
        }

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        string json = battle.Record.ToJson();
        var replayed = Replay.Run(db, BattleRecord.FromJson(json));

        Assert.Equal(FullLog(battle), FullLog(replayed));
        Assert.Equal(json, replayed.Record.ToJson());
        for (int side = 0; side < 2; side++)
            Assert.Equal(battle.Sides[side].Team.Select(fighter => fighter.Def.Id),
                replayed.Sides[side].Team.Select(fighter => fighter.Def.Id));
    }

    static string[] FullLog(Battle battle)
    {
        return battle.Log.Select(entry => string.Join("|", entry.Turn, entry.Node, entry.Kind, entry.Side, entry.Text,
            string.Join(",", entry.SnapActive), string.Join(",", entry.SnapHp[0]), string.Join(",", entry.SnapHp[1]))).ToArray();
    }
}
