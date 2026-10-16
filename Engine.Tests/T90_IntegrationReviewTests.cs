using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T90_IntegrationReviewTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    static IList<string>[] Teams(ContentDb db) => new IList<string>[]
    {
        db.Streamers.Take(6).Select(f => f.Id).ToArray(),
        db.Streamers.Skip(6).Take(6).Select(f => f.Id).ToArray()
    };

    static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);

    static int Query(Battle battle, int side, AiLevel level)
    {
        var identical = battle.Clone();
        var random = battle.Clone();
        double beforeDraw = random.Random(), afterOneDraw = random.Random();
        string record = battle.Record.ToJson(), logs = Json(battle.Log), events = Json(battle.Events);
        string teams = Json(battle.Sides), content = battle.Db.ContentHash();
        int choice = battle.Phase == Phase.Ban ? Ai.ChooseBan(battle, side, level) : Ai.ChooseLead(battle, side, level);
        int repeated = identical.Phase == Phase.Ban ? Ai.ChooseBan(identical, side, level) : Ai.ChooseLead(identical, side, level);
        Assert.Equal(choice, repeated);
        Assert.Equal(identical.StateHash(), battle.StateHash());
        Assert.Contains(battle.Clone().Random(), new[] { beforeDraw, afterOneDraw });
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(logs, Json(battle.Log));
        Assert.Equal(events, Json(battle.Events));
        Assert.Equal(teams, Json(battle.Sides));
        Assert.Equal(content, battle.Db.ContentHash());
        int candidateSide = battle.Phase == Phase.Ban ? 1 - side : side;
        Assert.InRange(choice, 0, battle.Sides[candidateSide].Team.Count - 1);
        Assert.False(battle.Sides[candidateSide].Team[choice].Fainted);
        return choice;
    }

    static void Open(Battle battle, AiLevel level)
    {
        for (int side = 0; side < 2 && battle.Phase == Phase.Ban; side++)
            battle.ChooseBan(side, Query(battle, side, level));
        Assert.All(battle.Sides, team => Assert.Equal(5, team.Team.Count));
        for (int side = 0; side < 2; side++)
        {
            int choice = Query(battle, side, level);
            string chosenId = battle.Sides[side].Team[choice].Def.Id;
            battle.ChooseLead(side, choice);
            if (side == 1) Assert.Equal(chosenId, battle.Active(side).Def.Id);
        }
        Assert.Equal(Phase.Choose, battle.Phase);
    }

    static void AssertReplay(Battle battle)
    {
        var replay = Replay.Run(battle.Db, BattleRecord.FromJson(battle.Record.ToJson()));
        Assert.Equal(battle.StateHash(), replay.StateHash());
        Assert.Equal(battle.Record.ToJson(), replay.Record.ToJson());
        Assert.Equal(Json(battle.Log), Json(replay.Log));
        Assert.Equal(Json(battle.Events), Json(replay.Events));
        var original = battle.Clone();
        for (int i = 0; i < 8; i++) Assert.Equal(original.Random(), replay.Random());
    }

    [Theory]
    [InlineData(AiLevel.Hard, false)]
    [InlineData(AiLevel.Expert, false)]
    [InlineData(AiLevel.Hard, true)]
    [InlineData(AiLevel.Expert, true)]
    public void RealThreeGameMatchKeepsRulesModifiersAndRecordedOpeningRandomness(AiLevel level, bool modified)
    {
        var db = BattleTests.LoadPack("original_wave3");
        var rules = new Ruleset
        {
            Id = "opening-review", BestOf = 3, BanOne = true, LeadPick = true, TurnSeconds = 10, BankSeconds = 30,
            Modifiers = modified ? new List<string> { "hp_x2", "half_pp", "hype_start", "stage_cap_3", "no_switch" } : new List<string>()
        };
        var teams = Teams(db);
        string content = db.ContentHash();
        var match = new Match(db, rules, teams, new[] { "甲方", "乙方" }, uint.MaxValue);
        for (int game = 0; game < 3; game++)
        {
            var battle = match.StartNextGame();
            Assert.Equal(unchecked(uint.MaxValue + (uint)game), battle.Seed);
            Assert.Equal(teams[0], battle.Record.Teams[0]);
            Assert.Equal(teams[1], battle.Record.Teams[1]);
            Assert.All(battle.Sides, side => Assert.Equal(6, side.Team.Count));
            Open(battle, level);
            foreach (var side in battle.Sides)
            {
                foreach (var fighter in side.Team)
                {
                    Assert.Equal(fighter.Def.Stats["hp"] * (modified ? 2 : 1), fighter.MaxHp);
                    foreach (var skill in fighter.Skills)
                        Assert.Equal(modified ? (db.Skills[skill.Id].Pp + 1) / 2 : db.Skills[skill.Id].Pp, skill.Max);
                }
            }
            if (modified)
            {
                Assert.NotNull(battle.Active(0).FieldOf("hype"));
                Assert.NotNull(battle.Active(1).FieldOf("hype"));
                Assert.Empty(battle.LegalFor(0).Switches);
                Assert.Empty(battle.LegalFor(1).Switches);
            }
            battle.ResolveTurn(Ai.Choose(battle, 0, AiLevel.Normal), Ai.Choose(battle, 1, AiLevel.Normal));
            if (game < 2) battle.Surrender(game);
            else battle.ResolveSurrenders(true, true);
            AssertReplay(battle);
            match.ReportResult(battle);
            Assert.Equal(content, db.ContentHash());
            Assert.Equal(game == 2, match.Over);
        }
        Assert.Equal(new[] { 1, 1 }, match.Wins);
        Assert.Equal(1, match.Draws);
    }

    [Theory]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void OpponentStyleCopiesKeepPublicOpeningChoicesAndTheOtherSideUnchanged(AiLevel level)
    {
        var db = BattleTests.LoadPack("original_wave3");
        var team = db.Streamers.Take(6).Select(f => f.Id).ToArray();
        var original = new Battle(db, new IList<string>[] { team, team }, new[] { "甲方", "乙方" },
            new BattleOptions { Seed = 90, BanOne = true, LeadPick = true, Modifiers = new List<string> { "hype_start" } });
        var styled = original.Clone();
        string content = db.ContentHash();
        var styles = db.Streamers.ToDictionary(f => f.Id, f => f.AiStyle);
        AiOpponents.ApplyStyle(styled, 1, new AiOpponent
        {
            Id = "review", Name = "测试陪练", PackId = db.PackId, Team = team.ToList(), Level = level, Style = "aggressive"
        });
        foreach (bool ban in new[] { true, false })
        {
            for (int side = 0; side < 2; side++)
            {
                int expected = Query(original, side, level), actual = Query(styled, side, level);
                Assert.Equal(expected, actual);
                if (ban) { original.ChooseBan(side, expected); styled.ChooseBan(side, actual); }
                else { original.ChooseLead(side, expected); styled.ChooseLead(side, actual); }
                Assert.Equal(original.StateHash(), styled.StateHash());
                Assert.Equal(original.Record.ToJson(), styled.Record.ToJson());
            }
        }
        foreach (var fighter in styled.Sides[0].Team)
        {
            Assert.Same(db.Streamer(fighter.Def.Id), fighter.Def);
            Assert.Equal(styles[fighter.Def.Id], fighter.Def.AiStyle);
        }
        foreach (var fighter in styled.Sides[1].Team)
        {
            Assert.NotSame(db.Streamer(fighter.Def.Id), fighter.Def);
            Assert.Equal("aggressive", fighter.Def.AiStyle);
        }
        Assert.Equal(content, db.ContentHash());
        Assert.All(db.Streamers, fighter => Assert.Equal(styles[fighter.Id], fighter.AiStyle));
        styled.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        styled.Surrender(0);
        AssertReplay(styled);
    }

    [Theory]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void MixedPrivateSkillsAndNewEffectsRemainIsolatedDuringOpeningPreviews(AiLevel level)
    {
        string contentRoot = Path.Combine(AppContext.BaseDirectory, "Content");
        var waveJson = JsonNode.Parse(File.ReadAllText(Path.Combine(contentRoot, "Packs", "original_wave3.json")));
        waveJson["skills"]["t90_review_combo"] = JsonSerializer.SerializeToNode(new
        {
            name = "开场预览组合验收", type = "talk", cat = "talent", power = 40, pp = 15, acc = 100,
            effects = new object[]
            {
                new { code = "STAGE_COPY", at = "pre", target = "foe" },
                new { code = "STAGE_SWAP", at = "post", target = "foe" },
                new { code = "PP_RESTORE", at = "post", target = "self", n = 2 },
                new { code = "DELAYED_DAMAGE", at = "post", target = "foe", n = 17, turns = 1 }
            }
        });
        foreach (var streamer in waveJson["streamers"].AsArray()) streamer["skills"][0] = "t90_review_combo";
        var wave = ContentDb.FromJson(File.ReadAllText(Path.Combine(contentRoot, "StreamDuelRules.json")), waveJson.ToJsonString());
        var ordinary = BattleTests.LoadPack("original");
        var db = ContentDb.Merge(ordinary, wave);
        string firstHash = ordinary.ContentHash(), secondHash = wave.ContentHash(), mixedHash = db.ContentHash();
        IList<string>[] teams =
        {
            ordinary.Streamers.Take(3).Concat(wave.Streamers.Take(3)).Select(f => f.Id).ToArray(),
            ordinary.Streamers.Skip(3).Take(3).Concat(wave.Streamers.Skip(3).Take(3)).Select(f => f.Id).ToArray()
        };
        var battle = new Battle(db, teams, new[] { "混合甲队", "混合乙队" },
            new BattleOptions { Seed = 90, BanOne = true, LeadPick = true, Modifiers = new List<string> { "half_pp", "hp_x2" } });
        Open(battle, level);
        var positions = battle.Sides.Select(side => side.Team.FindIndex(f => f.Skills[0].Id == "t90_review_combo")).ToArray();
        Assert.All(positions, index => Assert.True(index >= 0));
        battle.ResolveTurn(
            battle.Sides[0].Active == positions[0] ? BattleAction.Pass() : BattleAction.SwitchTo(positions[0]),
            battle.Sides[1].Active == positions[1] ? BattleAction.Pass() : BattleAction.SwitchTo(positions[1]));
        Assert.Equal("t90_review_combo", battle.Active(0).Skills[0].Id);
        Assert.Equal("t90_review_combo", battle.Active(1).Skills[0].Id);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        Assert.Contains(battle.Events, entry => entry.Kind == BattleEventKind.FieldAdd && entry.Id == ContentDb.DelayedDamageFieldId);
        Assert.Contains(battle.Events, entry => entry.Kind == BattleEventKind.FieldRemove && entry.Id == ContentDb.DelayedDamageFieldId);
        battle.ResolveSurrenders(true, true);
        AssertReplay(battle);
        Assert.Equal(firstHash, ordinary.ContentHash());
        Assert.Equal(secondHash, wave.ContentHash());
        Assert.Equal(mixedHash, db.ContentHash());
    }
}
