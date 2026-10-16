using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T127_EliminationBestOfTests
{
    static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    static ContentDb Content()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["t127_neutral"] = new PersonaDef
        {
            Id = "t127_neutral", Name = "稳定发挥", Code = "T_INTERACT_REDUCE", Params = { { "mul", 1 } }
        };
        foreach (var item in new[] { (Id: "chip", Damage: 10), (Id: "chunk", Damage: 35), (Id: "finish", Damage: 10000), (Id: "wait", Damage: 0) })
            db.Skills["t127_" + item.Id] = new SkillDef
            {
                Id = "t127_" + item.Id, Name = "测试动作" + item.Id, Type = "song", Cat = "status", Pp = 20,
                Effects = item.Damage == 0 ? new List<EffectDef>() : new List<EffectDef>
                { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = item.Damage } }
            };
        for (int index = 0; index < db.Streamers.Count; index++)
        {
            var unit = db.Streamers[index];
            unit.Persona = "t127_neutral";
            unit.Type = "song"; unit.Type2 = null;
            unit.Stats["hp"] = 100;
            foreach (string stat in ContentDb.StatKeys) unit.Stats[stat] = 100;
            unit.Stats["spd"] = 200 - index;
            unit.Skills = new List<string> { "t127_chip", "t127_chunk", "t127_finish", "t127_wait" };
            unit.Fifth = "t127_finish";
        }
        db.Validate();
        return db;
    }

    static Ruleset Rule(int bestOf = 3)
    {
        return new Ruleset { Id = "t127", Name = "多局验收", BestOf = bestOf, Modifiers = new List<string>() };
    }

    static string Code(ContentDb db, int player)
    {
        return TeamCode.Encode(new TeamPreset
        {
            Name = "验收阵容", PackId = db.PackId,
            Ids = db.Streamers.Skip(player % 2 * 6).Take(6).Select(unit => unit.Id).ToList()
        });
    }

    static Tournament Make(ContentDb db, string format = "single", int bestOf = 3, int count = 2,
        uint? seed = 127, IList<int> order = null, bool reset = true, Ruleset rule = null)
    {
        var options = format == "roundrobin" ? null : new TournamentOptions
        { Seed = order == null ? seed : null, SeedOrder = order, ResetFinal = reset, AutoTiebreak = false };
        var tournament = new Tournament(db, format, options: options, ruleset: rule ?? Rule(bestOf));
        for (int player = 0; player < count; player++) tournament.AddPlayer("选手" + player, Code(db, player));
        return tournament;
    }

    static Battle Finish(Battle battle, int winner)
    {
        if (winner == -1) battle.ResolveSurrenders(true, true);
        else battle.Surrender(1 - winner);
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(winner, battle.Winner);
        var replay = Replay.Run(battle.Db, BattleRecord.FromJson(battle.Record.ToJson()));
        Assert.Equal(battle.StateHash(), replay.StateHash());
        Assert.Equal(battle.Record.ToJson(), replay.Record.ToJson());
        return battle;
    }

    static Battle Report(Tournament tournament, Pairing pairing, int winner)
    {
        int index = pairing.Games.Count;
        var battle = Finish(tournament.CreateGame(pairing), winner);
        Assert.Equal(DerivedSeed(tournament.Seed ?? 0, pairing.Id, index), battle.Seed);
        tournament.ReportGame(pairing, battle);
        Assert.Equal(index + 1, pairing.Games.Count);
        var saved = pairing.Games[index];
        Assert.Equal(index, saved.Attempt);
        Assert.Equal(battle.Seed, saved.Seed);
        Assert.Equal(winner, saved.WinnerSide);
        Assert.Equal(Hash(battle.Record.ToJson()), saved.RecordHash);
        return battle;
    }

    static uint DerivedSeed(uint seed, int pairing, int index)
    {
        unchecked
        {
            uint value = seed ^ ((uint)pairing * 0x9E3779B9u) ^ ((uint)index * 0x85EBCA6Bu) ^ 0x54423138u;
            value = (value ^ (value >> 16)) * 0x7FEB352Du;
            value = (value ^ (value >> 15)) * 0x846CA68Bu;
            return value ^ (value >> 16);
        }
    }

    static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    static Exception ChineseFailure(Action action)
    {
        var error = Record.Exception(action);
        Assert.NotNull(error);
        Assert.Matches("[\\u4e00-\\u9fff]", error.Message);
        return error;
    }

    static int[] Results(string sequence)
    {
        return sequence.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
    }

    [Theory]
    [InlineData("single", 3, "0,0", 0)]
    [InlineData("single", 3, "1,1", 1)]
    [InlineData("single", 3, "0,1,0", 0)]
    [InlineData("single", 3, "1,0,1", 1)]
    [InlineData("single", 3, "0,-1,0", 0)]
    [InlineData("single", 5, "1,1,1", 1)]
    [InlineData("single", 5, "0,1,0,1,0", 0)]
    [InlineData("single", 5, "0,-1,0,-1,0", 0)]
    [InlineData("double", 3, "0,1,0", 0)]
    [InlineData("roundrobin", 3, "1,-1,1", 1)]
    [InlineData("roundrobin", 5, "0,0,0", 0)]
    [InlineData("single", 1, "0", 0)]
    public void FirstSideToTheWinThresholdWinsTheSeriesAndNoExtraGameIsCreated(string format, int bestOf, string sequence, int winner)
    {
        var tournament = Make(Content(), format, bestOf);
        Assert.True(tournament.HasRuleset);
        Assert.True(tournament.AutoTiebreak);
        Assert.Equal(bestOf, tournament.BestOf);
        var pairing = Assert.Single(tournament.NextRound());
        int[] results = Results(sequence);
        for (int index = 0; index < results.Length; index++)
        {
            Assert.Null(pairing.WinnerSide);
            Assert.Null(tournament.PendingDecision(pairing));
            Assert.Same(pairing, Assert.Single(tournament.NextRound()));
            Report(tournament, pairing, results[index]);
            if (index + 1 < results.Length) Assert.Null(pairing.WinnerSide);
        }
        Assert.Equal(winner, pairing.WinnerSide);
        Assert.Equal(bestOf / 2 + 1, pairing.Games.Count(game => game.WinnerSide == winner));
        Assert.Equal(results, pairing.Games.Select(game => game.WinnerSide));
        Assert.False(pairing.AdvancedBySeed);
        Assert.Null(tournament.PendingDecision(pairing));
        Assert.Equal(1, tournament.Standings().Single(row => row.Player == (winner == 0 ? pairing.Player0 : pairing.Player1)).Wins);
        string saved = tournament.ToJson();
        ChineseFailure(() => tournament.CreateGame(pairing));
        ChineseFailure(() => tournament.ReportResult(pairing, winner));
        Assert.Equal(saved, tournament.ToJson());
    }

    [Theory]
    [InlineData("single", "0,-1,-1")]
    [InlineData("single", "-1,1,-1")]
    [InlineData("roundrobin", "0,-1,-1")]
    [InlineData("roundrobin", "0,1,-1")]
    [InlineData("roundrobin", "-1,-1,-1")]
    public void UnspecifiedSeriesOutcomesPauseWithoutInventingAWinnerOrAnExtraGame(string format, string sequence)
    {
        var db = Content();
        var tournament = Make(db, format);
        var pairing = Assert.Single(tournament.NextRound());
        foreach (int result in Results(sequence)) Report(tournament, pairing, result);
        string reason = tournament.PendingDecision(pairing);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Matches("[\\u4e00-\\u9fff]", reason);
        Assert.Null(pairing.WinnerSide);
        Assert.False(pairing.AdvancedBySeed);
        Assert.False(tournament.IsFinished);
        Assert.Null(tournament.Champion);
        Assert.All(tournament.Standings(), row => { Assert.Equal(0, row.Wins); Assert.Equal(0, row.Losses); });
        string saved = tournament.ToJson();
        Assert.Contains(reason, TournamentReport.Build(tournament, null).ToText());
        ChineseFailure(() => tournament.CreateGame(pairing));
        ChineseFailure(() => tournament.ReportResult(pairing, 0));
        ChineseFailure(() => tournament.ReportResult(pairing, -1));
        Assert.Same(pairing, Assert.Single(tournament.NextRound()));
        Assert.Equal(saved, tournament.ToJson());
        var restored = Tournament.FromJson(db, saved);
        var resumedPair = Assert.Single(restored.Pairings);
        Assert.Equal(reason, restored.PendingDecision(resumedPair));
        Assert.Equal(saved, restored.ToJson());
        Assert.Equal(TournamentReport.Build(tournament, null).ToJson(), TournamentReport.Build(restored, null).ToJson());
        ChineseFailure(() => restored.CreateGame(resumedPair));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 0)]
    [InlineData(5, 2)]
    public void TiedNormalSeriesUsesSuddenDeathAndStopsAtItsFirstDecisiveExtraGame(int bestOf, int extraDraws)
    {
        var tournament = Make(Content(), bestOf: bestOf, seed: uint.MaxValue);
        var pairing = Assert.Single(tournament.NextRound());
        for (int index = 0; index < bestOf + extraDraws; index++)
        {
            Report(tournament, pairing, -1);
            Assert.Null(pairing.WinnerSide);
            Assert.Null(tournament.PendingDecision(pairing));
        }
        Report(tournament, pairing, 1);
        Assert.Equal(1, pairing.WinnerSide);
        Assert.False(pairing.AdvancedBySeed);
        Assert.True(tournament.IsFinished);
        Assert.Equal(pairing.Player1, tournament.Champion);
        Assert.Equal(bestOf + extraDraws + 1, pairing.Games.Count);
        Assert.Equal(1, pairing.Games.Count(game => game.WinnerSide == 1));
        Assert.Equal(Enumerable.Range(0, pairing.Games.Count), pairing.Games.Select(game => game.Attempt));
        Assert.Equal(pairing.Games.Count, pairing.Games.Select(game => game.Seed).Distinct().Count());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void ThreeExtraDrawsAdvanceTheHigherSeedWithoutChangingAnyActualDraw(int bestOf)
    {
        var tournament = Make(Content(), bestOf: bestOf, order: new[] { 1, 0 });
        var pairing = Assert.Single(tournament.NextRound());
        for (int index = 0; index < bestOf + 3; index++)
        {
            Report(tournament, pairing, -1);
            if (index < bestOf + 2) Assert.Null(pairing.WinnerSide);
        }
        Assert.True(pairing.AdvancedBySeed);
        Assert.Equal(1, tournament.Champion);
        Assert.Equal(1, pairing.WinnerSide == 0 ? pairing.Player0 : pairing.Player1);
        Assert.All(pairing.Games, game => Assert.Equal(-1, game.WinnerSide));
        Assert.Equal(bestOf + 3, pairing.Games.Count);
        Assert.Contains("加赛 3 次仍平，按种子顺位晋级", TournamentReport.Build(tournament, null).ToText());
        ChineseFailure(() => tournament.CreateGame(pairing));
        ChineseFailure(() => tournament.GameSeed(pairing, -1));
        ChineseFailure(() => tournament.GameSeed(pairing, bestOf + 3));
    }

    [Fact]
    public void FallbackUsesSeedRankEvenWhenTheHigherSeedIsTheSecondSide()
    {
        var tournament = Make(Content(), count: 4, order: new[] { 3, 1, 2, 0 });
        var opening = tournament.NextRound();
        Assert.Equal(2, opening.Count);
        foreach (var pairing in opening)
        {
            int winner = pairing.Player0 == 3 ? 1 : 0;
            Report(tournament, pairing, winner); Report(tournament, pairing, winner);
        }
        var final = Assert.Single(tournament.NextRound());
        Assert.Equal(0, final.Player0);
        Assert.Equal(1, final.Player1);
        for (int index = 0; index < 6; index++) Report(tournament, final, -1);
        Assert.Equal(1, final.WinnerSide);
        Assert.Equal(1, tournament.Champion);
        Assert.True(final.AdvancedBySeed);
    }

    [Fact]
    public void NormalDrawsCanComeFromCompleteTurnCapGamesBeforeEnteringTiebreak()
    {
        var db = Content();
        var tournament = Make(db);
        var pairing = Assert.Single(tournament.NextRound());
        for (int index = 0; index < 3; index++)
        {
            var battle = tournament.CreateGame(pairing);
            for (int turn = 1; turn <= Battle.TurnCap; turn++) battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            Assert.Equal(Phase.Over, battle.Phase);
            Assert.Equal(-1, battle.Winner);
            Assert.Equal(Battle.TurnCap, battle.Record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn));
            Assert.Equal(battle.StateHash(), Replay.Run(db, battle.Record).StateHash());
            tournament.ReportGame(pairing, battle);
            Assert.Null(pairing.WinnerSide);
        }
        Assert.Null(tournament.PendingDecision(pairing));
        Report(tournament, pairing, 0);
        Assert.Equal(0, pairing.WinnerSide);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoubleEliminationCountsSeriesLossesAndResetsOnlyAfterACompleteFinal(bool reset)
    {
        var tournament = Make(Content(), "double", order: new[] { 0, 1 }, reset: reset);
        var winners = Assert.Single(tournament.NextRound());
        Report(tournament, winners, 0); Report(tournament, winners, 0);
        var final = Assert.Single(tournament.NextRound());
        Assert.Equal("final", final.Bracket);
        Report(tournament, final, 1);
        Assert.Null(final.WinnerSide);
        Assert.Same(final, Assert.Single(tournament.NextRound()));
        Assert.DoesNotContain(tournament.Pairings, pair => pair.Bracket == "reset");
        Report(tournament, final, 1);
        if (reset)
        {
            Assert.False(tournament.IsFinished);
            var resetFinal = Assert.Single(tournament.NextRound());
            Assert.Equal("reset", resetFinal.Bracket);
            Assert.Equal(final.Player0, resetFinal.Player0);
            Assert.Equal(final.Player1, resetFinal.Player1);
            Report(tournament, resetFinal, 1); Report(tournament, resetFinal, 1);
        }
        Assert.True(tournament.IsFinished);
        Assert.Equal(1, tournament.Champion);
        Assert.Equal(reset ? 3 : 2, tournament.Pairings.Count);
        Assert.Equal(reset ? 6 : 4, tournament.Pairings.Sum(pair => pair.Games.Count));
        Assert.Equal(reset ? 2 : 1, tournament.Standings().Single(row => row.Player == 0).Losses);
        Assert.Equal(1, tournament.Standings().Single(row => row.Player == 1).Losses);
    }

    [Theory]
    [InlineData("single", 5, 4, 3)]
    [InlineData("roundrobin", 3, 3, 3)]
    public void ByesNeverCreateGamesAndRoundRobinScoresSeriesRatherThanIndividualWins(string format, int count, int matches, int byes)
    {
        var tournament = Make(Content(), format, count: count);
        int guard = 0;
        while (!tournament.IsFinished && guard++ < 20)
            foreach (var pair in tournament.NextRound())
            {
                if (pair.IsBye) { Assert.Empty(pair.Games); continue; }
                Report(tournament, pair, 0); Report(tournament, pair, 0);
            }
        Assert.True(tournament.IsFinished);
        Assert.Equal(matches, tournament.Pairings.Count(pair => !pair.IsBye));
        Assert.Equal(byes, tournament.Pairings.Count(pair => pair.IsBye));
        Assert.Equal(matches * 2, tournament.Pairings.Sum(pair => pair.Games.Count));
        Assert.Equal(matches + byes, tournament.Standings().Sum(row => row.Wins));
        foreach (var bye in tournament.Pairings.Where(pair => pair.IsBye))
        {
            Assert.Empty(bye.Games);
            Assert.False(bye.AdvancedBySeed);
            ChineseFailure(() => tournament.CreateGame(bye));
            ChineseFailure(() => tournament.GameSeed(bye, 0));
        }
    }

    [Theory]
    [InlineData("single", 4)]
    [InlineData("double", 4)]
    [InlineData("roundrobin", 3)]
    public void ReloadingAfterEveryGameAndRoundMatchesAnUninterruptedTournament(string format, int count)
    {
        var db = Content();
        var uninterrupted = Make(db, format, count: count);
        var resumed = Tournament.FromJson(db, uninterrupted.ToJson());
        int checkpoints = 0, guard = 0;
        while (!uninterrupted.IsFinished && guard++ < 30)
        {
            int[] ids = uninterrupted.NextRound().Where(pair => !pair.IsBye).Select(pair => pair.Id).ToArray();
            Assert.Equal(ids, resumed.NextRound().Where(pair => !pair.IsBye).Select(pair => pair.Id));
            Assert.Equal(uninterrupted.ToJson(), resumed.ToJson());
            resumed = Tournament.FromJson(db, resumed.ToJson());
            foreach (int id in ids)
            {
                var pair = uninterrupted.Pairings[id];
                while (!pair.WinnerSide.HasValue)
                {
                    var resumedPair = resumed.Pairings[id];
                    Assert.Equal(uninterrupted.CreateGame(pair).Record.ToJson(), resumed.CreateGame(resumedPair).Record.ToJson());
                    int index = pair.Games.Count;
                    int result = format == "roundrobin" ? (index == 1 ? 1 : 0) : (index < 4 ? -1 : id % 2);
                    Report(uninterrupted, pair, result);
                    Report(resumed, resumedPair, result);
                    Assert.Equal(uninterrupted.ToJson(), resumed.ToJson());
                    resumed = Tournament.FromJson(db, resumed.ToJson());
                    Assert.Equal(uninterrupted.ToJson(), resumed.ToJson());
                    Assert.Equal(TournamentReport.Build(uninterrupted, null).ToJson(), TournamentReport.Build(resumed, null).ToJson());
                    checkpoints++;
                }
            }
        }
        Assert.True(uninterrupted.IsFinished);
        Assert.True(resumed.IsFinished);
        Assert.Equal(uninterrupted.Champion, resumed.Champion);
        Assert.True(checkpoints >= 9);
        Assert.Equal(uninterrupted.ToJson(), resumed.ToJson());
        Assert.Equal(TournamentReport.Build(uninterrupted, null).ToText(), TournamentReport.Build(resumed, null).ToText());
    }

    [Fact]
    public void AnUnreportedPartialGameDoesNotBecomeAPhantomGameWhenResuming()
    {
        var db = Content();
        var tournament = Make(db);
        var pair = Assert.Single(tournament.NextRound());
        var original = tournament.CreateGame(pair);
        string saved = tournament.ToJson(), initialRecord = original.Record.ToJson();
        ulong initialHash = original.StateHash();
        original.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        Assert.Equal(35, BattleSummary.From(original).Sides[0].Sum(fighter => fighter.DamageDealt));
        Assert.NotEqual(initialHash, original.StateHash());
        Assert.Equal(saved, tournament.ToJson());
        var resumed = Tournament.FromJson(db, saved);
        var next = resumed.CreateGame(Assert.Single(resumed.Pairings));
        Assert.Equal(initialRecord, next.Record.ToJson());
        Assert.Equal(initialHash, next.StateHash());
        Assert.Empty(pair.Games);
        Assert.Empty(resumed.Pairings[0].Games);
    }

    [Fact]
    public void RulesAreCopiedAndEveryGameStartsWithFreshBanLeadTeamsPpAndHitPoints()
    {
        var db = Content();
        var rule = Rule();
        rule.BanOne = true; rule.LeadPick = true;
        rule.TurnSeconds = 17; rule.BankSeconds = 29;
        rule.Modifiers.AddRange(new[] { "half_pp", "hp_x2" });
        string frozen = rule.ToJson();
        var tournament = Make(db, rule: rule);
        rule.BestOf = 5; rule.BanOne = false; rule.LeadPick = false; rule.Modifiers.Clear();
        rule.Name = "外部改名"; rule.TurnSeconds = 99;
        var exposed = tournament.Ruleset;
        exposed.Modifiers.Add("hype_start"); exposed.BankSeconds = 0; exposed.BestOf = 1;
        Assert.NotSame(exposed, tournament.Ruleset);
        Assert.Equal(frozen, tournament.Ruleset.ToJson());
        var pairing = Assert.Single(tournament.NextRound());
        var first = tournament.CreateGame(pairing);
        Assert.Equal(Phase.Ban, first.Phase);
        foreach (var side in first.Sides)
        {
            Assert.Equal(6, side.Team.Count);
            Assert.All(side.Team, fighter =>
            {
                Assert.Equal(200, fighter.Hp); Assert.Equal(200, fighter.MaxHp);
                Assert.All(fighter.Skills, skill => { Assert.Equal(10, skill.Pp); Assert.Equal(10, skill.Max); });
            });
        }
        first.ChooseBan(0, 5); first.ChooseBan(1, 4);
        Assert.Equal(Phase.Lead, first.Phase);
        first.ChooseLead(0, 2); first.ChooseLead(1, 3);
        first.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        Assert.All(first.Sides, side => Assert.Equal(5, side.Team.Count));
        Assert.Equal(190, first.Active(0).Hp);
        Assert.Equal(190, first.Active(1).Hp);
        Assert.Equal(9, first.Active(0).Skills[0].Pp);
        Assert.Equal(9, first.Active(1).Skills[0].Pp);
        Finish(first, 0);
        tournament.ReportGame(pairing, first);
        string firstRecord = first.Record.ToJson();
        var second = tournament.CreateGame(pairing);
        Assert.Equal(Phase.Ban, second.Phase);
        for (int side = 0; side < 2; side++)
        {
            Assert.False(second.HasChosenBan(side)); Assert.False(second.HasChosenLead(side));
            Assert.Equal(tournament.Players[side == 0 ? pairing.Player0 : pairing.Player1].Team, second.Sides[side].Team.Select(fighter => fighter.Def.Id));
            Assert.All(second.Sides[side].Team, fighter =>
            {
                Assert.Equal(200, fighter.Hp); Assert.Equal(fighter.MaxHp, fighter.Hp);
                Assert.All(fighter.Skills, skill => Assert.Equal(10, skill.Pp));
            });
        }
        var reference = new Battle(db,
            new IList<string>[] { tournament.Players[pairing.Player0].Team.ToList(), tournament.Players[pairing.Player1].Team.ToList() },
            new[] { tournament.Players[pairing.Player0].Name, tournament.Players[pairing.Player1].Name },
            new BattleOptions { Seed = tournament.GameSeed(pairing, 1), BanOne = true, LeadPick = true, Modifiers = new List<string> { "half_pp", "hp_x2" } });
        Assert.Equal(reference.StateHash(), second.StateHash());
        Assert.Equal(reference.Record.ToJson(), second.Record.ToJson());
        Assert.Equal(reference.Random(), second.Random());
        second.ChooseBan(0, 0);
        Assert.Equal(firstRecord, first.Record.ToJson());
        Assert.Equal(frozen, Tournament.FromJson(db, tournament.ToJson()).Ruleset.ToJson());
    }

    [Fact]
    public void SeasonRulesAreAppliedAtRegistrationAndCannotBeChangedThroughTheCaller()
    {
        var db = Content();
        db.Seasons["t127_season"] = new SeasonDef { Name = "验收赛季", Banned = new List<string> { db.Streamers[0].Id } };
        var rule = Rule(); rule.Season = "t127_season";
        var tournament = new Tournament(db, "single", ruleset: rule);
        string before = tournament.ToJson();
        rule.Season = null;
        ChineseFailure(() => tournament.AddPlayer("不合规队", Code(db, 0)));
        Assert.Equal(before, tournament.ToJson());
        tournament.AddPlayer("合规甲", Code(db, 1));
        tournament.AddPlayer("合规乙", Code(db, 1));
        Assert.Equal("t127_season", tournament.Ruleset.Season);
        var restored = Tournament.FromJson(db, tournament.ToJson());
        Assert.Equal(tournament.ToJson(), restored.ToJson());
        Assert.Equal(2, restored.Players.Count);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("even")]
    [InlineData("six")]
    [InlineData("negative")]
    [InlineData("season")]
    [InlineData("modifier")]
    [InlineData("turnClock")]
    [InlineData("bankClock")]
    [InlineData("swiss")]
    public void InvalidRulesFailBeforeAnyTournamentIsStarted(string error)
    {
        var db = Content();
        var rule = Rule();
        switch (error)
        {
            case "zero": rule.BestOf = 0; break;
            case "even": rule.BestOf = 2; break;
            case "six": rule.BestOf = 6; break;
            case "negative": rule.BestOf = -1; break;
            case "season": rule.Season = "absent"; break;
            case "modifier": rule.Modifiers.Add("absent"); break;
            case "turnClock": rule.TurnSeconds = -1; break;
            case "bankClock": rule.BankSeconds = -1; break;
        }
        ChineseFailure(() => new Tournament(db, error == "swiss" ? "swiss" : "single", ruleset: rule));
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("names")]
    [InlineData("teamOrder")]
    [InlineData("ban")]
    [InlineData("lead")]
    [InlineData("modifiers")]
    [InlineData("unfinished")]
    [InlineData("state")]
    public void InvalidOrDifferentGameCannotChangeTheSeries(string invalid)
    {
        var db = Content();
        var rule = Rule(); rule.BanOne = true; rule.LeadPick = true; rule.Modifiers.Add("half_pp");
        var tournament = Make(db, rule: rule);
        var pair = Assert.Single(tournament.NextRound());
        string before = tournament.ToJson();
        var names = new[] { tournament.Players[pair.Player0].Name, tournament.Players[pair.Player1].Name };
        IList<string>[] teams = { tournament.Players[pair.Player0].Team.ToList(), tournament.Players[pair.Player1].Team.ToList() };
        var options = new BattleOptions { Seed = tournament.GameSeed(pair, 0), BanOne = true, LeadPick = true, Modifiers = new List<string> { "half_pp" } };
        if (invalid == "seed") options.Seed = unchecked(options.Seed.Value + 1);
        if (invalid == "names") names[0] = "另一个名字";
        if (invalid == "teamOrder") teams[0] = teams[0].Reverse().ToList();
        if (invalid == "ban") options.BanOne = false;
        if (invalid == "lead") options.LeadPick = false;
        if (invalid == "modifiers") options.Modifiers.Clear();
        var battle = new Battle(db, teams, names, options);
        if (invalid != "unfinished") Finish(battle, 0);
        if (invalid == "state") battle.Sides[0].Team[0].Hp--;
        string record = battle.Record.ToJson(); ulong state = battle.StateHash();
        ChineseFailure(() => tournament.ReportGame(pair, battle));
        Assert.Equal(before, tournament.ToJson());
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(state, battle.StateHash());
        Assert.Empty(pair.Games); Assert.Null(pair.WinnerSide);
    }

    [Fact]
    public void ManualResultsDuplicatesForeignPairsAndChangedContentAreRejectedAtomically()
    {
        var db = Content();
        var tournament = Make(db);
        var pair = Assert.Single(tournament.NextRound());
        string empty = tournament.ToJson();
        ChineseFailure(() => tournament.ReportResult(pair, 0));
        ChineseFailure(() => tournament.ReportResult(pair, -1));
        var foreign = Make(db);
        var foreignPair = Assert.Single(foreign.NextRound());
        ChineseFailure(() => tournament.CreateGame(foreignPair));
        ChineseFailure(() => tournament.PendingDecision(foreignPair));
        ChineseFailure(() => tournament.PendingDecision(null));
        Assert.Equal(empty, tournament.ToJson());
        var battle = Report(tournament, pair, 0);
        string once = tournament.ToJson();
        ChineseFailure(() => tournament.ReportGame(pair, battle));
        Assert.Equal(once, tournament.ToJson());
        db.Skills["t127_chip"].Effects[0].N++;
        ChineseFailure(() => tournament.CreateGame(pair));
        Assert.Equal(once, tournament.ToJson());
        Assert.Single(pair.Games);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("rulesetMissing")]
    [InlineData("rulesetNull")]
    [InlineData("bestOf")]
    [InlineData("bestOfType")]
    [InlineData("modifier")]
    [InlineData("clock")]
    [InlineData("extraRule")]
    [InlineData("autoTiebreak")]
    [InlineData("attempt")]
    [InlineData("seed")]
    [InlineData("recordHash")]
    [InlineData("winner")]
    [InlineData("advanced")]
    [InlineData("earlyResult")]
    [InlineData("extraGame")]
    public void VersionFourRejectsMalformedRulesAndImpossibleSeriesHistories(string defect)
    {
        var db = Content();
        var tournament = Make(db, bestOf: 5);
        var pair = Assert.Single(tournament.NextRound());
        for (int index = 0; index < 8; index++) Report(tournament, pair, -1);
        string before = tournament.ToJson();
        var root = JsonNode.Parse(before).AsObject();
        Assert.Equal(4, root["version"].GetValue<int>());
        var savedPair = root["roundsPlayed"][0][0].AsObject();
        var games = savedPair["games"].AsArray();
        switch (defect)
        {
            case "version": root["version"] = 3; break;
            case "rulesetMissing": root.Remove("ruleset"); break;
            case "rulesetNull": root["ruleset"] = null; break;
            case "bestOf": root["ruleset"]["bestOf"] = 4; break;
            case "bestOfType": root["ruleset"]["bestOf"] = "5"; break;
            case "modifier": root["ruleset"]["modifiers"].AsArray().Add("absent"); break;
            case "clock": root["ruleset"]["bankSeconds"] = -1; break;
            case "extraRule": root["ruleset"]["unexpected"] = true; break;
            case "autoTiebreak": root["autoTiebreak"] = false; break;
            case "attempt": games[3]["attempt"] = 4; break;
            case "seed": games[3]["seed"] = unchecked(pair.Games[3].Seed + 1); break;
            case "recordHash": games[3]["recordHash"] = "invalid"; break;
            case "winner": savedPair["winnerSide"] = 1 - pair.WinnerSide.Value; break;
            case "advanced": savedPair["advancedBySeed"] = false; break;
            case "earlyResult": games[0]["winnerSide"] = 0; break;
            case "extraGame": var extra = games[7].DeepClone(); extra["attempt"] = 8; games.Add(extra); break;
        }
        var error = Assert.Throws<FormatException>(() => Tournament.FromJson(db, root.ToJsonString()));
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
        Assert.Equal(before, tournament.ToJson());
        Assert.Equal(before, Tournament.FromJson(db, before).ToJson());
    }

    [Theory]
    [InlineData("roundrobin", false, 1)]
    [InlineData("single", false, 2)]
    [InlineData("single", true, 3)]
    public void OmittingRulesetKeepsTheLegacyFormatAndOriginalOneGameRules(string format, bool autoTiebreak, int version)
    {
        var db = Content();
        var tournament = new Tournament(db, format, options: format == "single"
            ? new TournamentOptions { Seed = 127, AutoTiebreak = autoTiebreak } : null);
        for (int player = 0; player < 2; player++) tournament.AddPlayer("选手" + player, Code(db, player));
        Assert.False(tournament.HasRuleset);
        Assert.Null(tournament.Ruleset);
        Assert.Equal(1, tournament.BestOf);
        var pair = Assert.Single(tournament.NextRound());
        if (autoTiebreak) Report(tournament, pair, 0);
        else tournament.ReportResult(pair, 0);
        Assert.Equal(0, pair.WinnerSide);
        using var json = JsonDocument.Parse(tournament.ToJson());
        Assert.Equal(version, json.RootElement.GetProperty("version").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("ruleset", out _));
        Assert.Equal(tournament.ToJson(), Tournament.FromJson(db, tournament.ToJson()).ToJson());
        if (autoTiebreak) ChineseFailure(() => tournament.GameSeed(pair, 4));
    }

    [Fact]
    public void AllEightBestOfFiveGamesArchiveIndependentlyAndReportTheirActualResults()
    {
        using var files = new TestDirectory();
        var db = Content();
        var tournament = Make(db, bestOf: 5);
        var pair = Assert.Single(tournament.NextRound());
        var library = new ReplayLibrary(files.Root, maxCount: 1, clock: () => Epoch);
        var ids = new List<string>(); var records = new List<string>();
        for (int index = 0; index < 8; index++)
        {
            string checkpoint = tournament.ToJson();
            var battle = tournament.CreateGame(pair);
            if (index == 0) battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
            Finish(battle, -1);
            string record = battle.Record.ToJson(); ulong state = battle.StateHash();
            string id = library.SaveTournamentGame(tournament, pair, battle, index);
            Assert.Equal(checkpoint, tournament.ToJson());
            Assert.Equal(id, library.SaveTournamentGame(tournament, pair, battle, index));
            if (index == 4)
            {
                // 模拟录像已落盘但赛事进度尚未保存时退出；新对象必须复用同一份录像。
                var restored = Tournament.FromJson(db, checkpoint);
                var restoredPair = Assert.Single(restored.Pairings);
                var restoredBattle = Finish(restored.CreateGame(restoredPair), -1);
                Assert.Equal(record, restoredBattle.Record.ToJson());
                Assert.Equal(id, new ReplayLibrary(files.Root, maxCount: 1, clock: () => Epoch.AddDays(1))
                    .SaveTournamentGame(restored, restoredPair, restoredBattle, index));
                Assert.Equal(checkpoint, restored.ToJson());
            }
            tournament.ReportGame(pair, battle);
            string reported = tournament.ToJson();
            Assert.Equal(id, library.SaveTournamentGame(tournament, pair, battle, index));
            Assert.Equal(reported, tournament.ToJson());
            Assert.Equal(record, library.Load(id).ToJson());
            Assert.Equal(record, battle.Record.ToJson()); Assert.Equal(state, battle.StateHash());
            ids.Add(id); records.Add(record);
        }
        Assert.Equal(8, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(8, library.List().Count);
        Assert.All(library.List(), entry => { Assert.False(entry.IsCorrupt); Assert.Equal(-1, entry.Winner); });
        Assert.True(pair.AdvancedBySeed);
        string eighthPath = Path.Combine(files.Root, ids[7] + ".json");
        using var beforeNote = JsonDocument.Parse(File.ReadAllText(eighthPath));
        string tag = beforeNote.RootElement.GetProperty("library").GetProperty("tournament").GetRawText();
        Assert.Equal(3, beforeNote.RootElement.GetProperty("library").GetProperty("tournament").GetProperty("version").GetInt32());
        Assert.Equal(7, beforeNote.RootElement.GetProperty("library").GetProperty("tournament").GetProperty("attempt").GetInt32());
        library.SetNote(ids[7], "决胜加赛归档");
        using (var afterNote = JsonDocument.Parse(File.ReadAllText(eighthPath)))
            Assert.Equal(tag, afterNote.RootElement.GetProperty("library").GetProperty("tournament").GetRawText());
        Assert.Equal(records[7], library.Load(ids[7]).ToJson());
        library.SetNote(ids[7], null);
        Assert.Equal(8, library.List().Count);
        var before = Snapshot(files.Root);
        string progress = tournament.ToJson();
        var report = TournamentReport.Build(tournament, ReplayLibrary.OpenReadOnly(files.Root));
        using var document = JsonDocument.Parse(report.ToJson());
        var root = document.RootElement;
        Assert.Equal(3, root.GetProperty("version").GetInt32());
        Assert.Equal(5, root.GetProperty("bestOf").GetInt32());
        Assert.Equal(tournament.Ruleset.ToJson(), root.GetProperty("ruleset").GetRawText());
        AssertCoverage(root, 8, 8, 0, 0);
        var match = ReportMatch(root, pair.Id);
        Assert.Equal(new[] { 0, 0 }, match.GetProperty("wins").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(8, match.GetProperty("draws").GetInt32());
        Assert.Equal(JsonValueKind.Null, match.GetProperty("pendingDecision").ValueKind);
        Assert.True(match.GetProperty("advancedBySeed").GetBoolean());
        var games = match.GetProperty("games").EnumerateArray().ToArray();
        Assert.Equal(8, games.Length);
        for (int index = 0; index < games.Length; index++)
        {
            Assert.Equal(index, games[index].GetProperty("attempt").GetInt32());
            Assert.Equal(index + 1, games[index].GetProperty("gameNumber").GetInt32());
            if (index < 5) Assert.Equal(JsonValueKind.Null, games[index].GetProperty("tiebreakNumber").ValueKind);
            else Assert.Equal(index - 4, games[index].GetProperty("tiebreakNumber").GetInt32());
            Assert.Equal(DerivedSeed(tournament.Seed.Value, pair.Id, index), games[index].GetProperty("seed").GetUInt32());
            Assert.Equal(-1, games[index].GetProperty("winnerSide").GetInt32());
            Assert.Equal(Hash(records[index]), games[index].GetProperty("recordHash").GetString());
            Assert.Equal(ids[index], games[index].GetProperty("replay").GetProperty("id").GetString());
            Assert.Equal("saved", games[index].GetProperty("replay").GetProperty("status").GetString());
        }
        Assert.Equal(35, root.GetProperty("maxDamage").GetProperty("amount").GetInt32());
        Assert.Equal(ids[0], root.GetProperty("maxDamage").GetProperty("replayId").GetString());
        Assert.Contains("第 8 局（加赛 3）", report.ToText());
        Assert.Equal(progress, tournament.ToJson());
        Assert.Equal(before, Snapshot(files.Root));
        library.Delete(ids[2]);
        using var missing = JsonDocument.Parse(TournamentReport.Build(tournament, ReplayLibrary.OpenReadOnly(files.Root)).ToJson());
        AssertCoverage(missing.RootElement, 8, 7, 1, 0);
        Assert.Equal(progress, tournament.ToJson());
    }

    [Fact]
    public void PausedSeriesStillReportsItsScoresAndAllCompletedReplays()
    {
        using var files = new TestDirectory();
        var tournament = Make(Content(), "roundrobin");
        var pair = Assert.Single(tournament.NextRound());
        var library = new ReplayLibrary(files.Root, clock: () => Epoch);
        var battles = new List<Battle>();
        foreach (int result in new[] { 0, -1, -1 })
        {
            int index = pair.Games.Count;
            var battle = Finish(tournament.CreateGame(pair), result);
            library.SaveTournamentGame(tournament, pair, battle, index);
            tournament.ReportGame(pair, battle);
            battles.Add(battle);
        }
        string pending = tournament.ToJson();
        Assert.NotNull(tournament.PendingDecision(pair));
        Assert.Equal(3, library.List().Count);
        for (int index = 0; index < battles.Count; index++)
            library.SaveTournamentGame(tournament, pair, battles[index], index);
        Assert.Equal(3, library.List().Count);
        using var report = JsonDocument.Parse(TournamentReport.Build(tournament, ReplayLibrary.OpenReadOnly(files.Root)).ToJson());
        AssertCoverage(report.RootElement, 3, 3, 0, 0);
        var match = ReportMatch(report.RootElement, pair.Id);
        Assert.Equal(new[] { 1, 0 }, match.GetProperty("wins").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(2, match.GetProperty("draws").GetInt32());
        Assert.Equal(tournament.PendingDecision(pair), match.GetProperty("pendingDecision").GetString());
        Assert.Equal(JsonValueKind.Null, match.GetProperty("winnerSide").ValueKind);
        Assert.Equal(pending, tournament.ToJson());
    }

    [Fact]
    public void LegacyArchiveTagsStillRejectAttemptFourAndNewTagsRejectAttemptEight()
    {
        using var files = new TestDirectory();
        var tournament = Make(Content(), bestOf: 5);
        var pair = Assert.Single(tournament.NextRound());
        var library = new ReplayLibrary(files.Root, clock: () => Epoch);
        for (int index = 0; index < 4; index++) Report(tournament, pair, -1);
        var battle = Finish(tournament.CreateGame(pair), -1);
        string id = library.SaveTournamentGame(tournament, pair, battle, 4);
        string path = Path.Combine(files.Root, id + ".json");
        string valid = File.ReadAllText(path);
        var oldTag = JsonNode.Parse(valid);
        oldTag["library"]["tournament"]["version"] = 2;
        File.WriteAllText(path, oldTag.ToJsonString(), new UTF8Encoding(false));
        Assert.Throws<FormatException>(() => library.Load(id));
        Assert.True(Assert.Single(library.List()).IsCorrupt);
        var outOfRange = JsonNode.Parse(valid);
        outOfRange["library"]["tournament"]["attempt"] = 8;
        File.WriteAllText(path, outOfRange.ToJsonString(), new UTF8Encoding(false));
        Assert.Throws<FormatException>(() => library.Load(id));
        Assert.True(Assert.Single(library.List()).IsCorrupt);
        File.WriteAllText(path, valid, new UTF8Encoding(false));
        Assert.Equal(battle.Record.ToJson(), library.Load(id).ToJson());
        Assert.False(Assert.Single(library.List()).IsCorrupt);
    }

    [Fact]
    public void ArchiveIdentitySeparatesTheEntireRulesetAndLegacyModeEvenForIdenticalFirstGames()
    {
        using var files = new TestDirectory();
        var db = Content();
        var library = new ReplayLibrary(files.Root, clock: () => Epoch);
        var keys = new List<string>(); var ids = new List<string>(); var records = new List<string>();
        for (int variant = 0; variant < 5; variant++)
        {
            var ruleset = Rule(variant == 1 ? 5 : 3);
            if (variant == 2) ruleset.TurnSeconds = 15;
            if (variant == 3) ruleset.BankSeconds = 30;
            var tournament = new Tournament(db, "single", options: new TournamentOptions { Seed = 127, AutoTiebreak = true },
                ruleset: variant == 4 ? null : ruleset);
            for (int player = 0; player < 2; player++) tournament.AddPlayer("选手" + player, Code(db, player));
            var pair = Assert.Single(tournament.NextRound());
            var battle = Finish(tournament.CreateGame(pair), -1);
            string id = library.SaveTournamentGame(tournament, pair, battle, 0);
            using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(files.Root, id + ".json")));
            var tag = file.RootElement.GetProperty("library").GetProperty("tournament");
            Assert.Equal(variant == 4 ? 2 : 3, tag.GetProperty("version").GetInt32());
            keys.Add(tag.GetProperty("key").GetString()); ids.Add(id); records.Add(battle.Record.ToJson());
            tournament.ReportGame(pair, battle);
            using var report = JsonDocument.Parse(TournamentReport.Build(tournament, ReplayLibrary.OpenReadOnly(files.Root)).ToJson());
            AssertCoverage(report.RootElement, 1, 1, 0, 0);
            Assert.Equal(id, Assert.Single(ReportMatch(report.RootElement, pair.Id).GetProperty("games").EnumerateArray())
                .GetProperty("replay").GetProperty("id").GetString());
        }
        Assert.Equal(5, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(5, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Single(records.Distinct(StringComparer.Ordinal));
    }

    static JsonElement ReportMatch(JsonElement root, int id)
    {
        return Assert.Single(root.GetProperty("rounds").EnumerateArray()
            .SelectMany(round => round.GetProperty("matches").EnumerateArray()), match => match.GetProperty("pairingId").GetInt32() == id);
    }

    static void AssertCoverage(JsonElement root, int completed, int archived, int missing, int invalid)
    {
        var coverage = root.GetProperty("coverage");
        Assert.Equal(completed, coverage.GetProperty("completedGames").GetInt32());
        Assert.Equal(archived, coverage.GetProperty("archivedGames").GetInt32());
        Assert.Equal(missing, coverage.GetProperty("missingGames").GetInt32());
        Assert.Equal(invalid, coverage.GetProperty("invalidGames").GetInt32());
    }

    static string[] Snapshot(string directory)
    {
        return Directory.GetFiles(directory, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(directory, path) + "\n" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = "t127-" + Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string source = "")
        {
            string tests = Path.GetDirectoryName(Path.GetFullPath(source));
            if (Path.GetFileName(tests) != "Engine.Tests") throw new InvalidOperationException("验收临时目录只能位于当前测试工程。");
            parent = Path.GetFullPath(Path.Combine(tests, "bin", "test-temp"));
            Root = Path.Combine(parent, name);
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            string target = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(target) != parent || Path.GetFileName(target) != name)
                throw new InvalidOperationException("只能删除本测试创建的独立临时目录。");
            if (!Directory.Exists(target)) return;
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("验收临时目录不能是链接。");
            Directory.Delete(target, true);
        }
    }
}
