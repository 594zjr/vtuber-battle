using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T54_ReviewTests
{
    static readonly string[] A = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] B = { "yuan", "nyako", "buck", "kasumi", "bee", "enka" };

    static IList<string>[] Teams() => new IList<string>[] { A.ToArray(), B.ToArray() };

    [Theory]
    [InlineData(Phase.Ban, "surrender")]
    [InlineData(Phase.Ban, "double_surrender")]
    [InlineData(Phase.Ban, "draw")]
    [InlineData(Phase.Lead, "surrender")]
    [InlineData(Phase.Lead, "double_surrender")]
    [InlineData(Phase.Lead, "draw")]
    [InlineData(Phase.Choose, "surrender")]
    [InlineData(Phase.Choose, "double_surrender")]
    [InlineData(Phase.Choose, "draw")]
    public void RealTerminationResultsReplayAndNextGameResetsAllOpeningAndDrawState(Phase phase, string termination)
    {
        var db = BattleTests.LoadPack("original");
        var rule = new Ruleset
        {
            Id = "review", Season = "s1", BestOf = 3, BanOne = true, LeadPick = true,
            Modifiers = new List<string> { "hype_start", "half_pp", "half_pp" },
            TurnSeconds = 10, BankSeconds = 30
        };
        var names = new[] { "甲", "乙" };
        var match = new Match(db, rule, Teams(), names, uint.MaxValue);
        var first = match.StartNextGame();
        if (phase != Phase.Ban)
        {
            first.ChooseBan(0, 0);
            first.ChooseBan(1, 0);
        }
        if (phase == Phase.Choose)
        {
            first.ChooseLead(0, 2);
            first.ChooseLead(1, 3);
        }
        Assert.Equal(phase, first.Phase);
        if (phase == Phase.Choose)
            first.ResolveTurn(Ai.Choose(first, 0), Ai.Choose(first, 1));
        first.OfferDraw(0);
        Assert.True(first.HasDrawOffer(0));
        if (termination == "surrender") first.Surrender(0);
        else if (termination == "double_surrender") first.ResolveSurrenders(true, true);
        else first.RespondDraw(1, true);
        int winner = termination == "surrender" ? 1 : -1;
        Assert.Equal(Phase.Over, first.Phase);
        Assert.Equal(winner, first.Winner);
        Assert.False(first.HasDrawOffer(0));
        Assert.Equal(BattleRecord.TerminationVersion, first.Record.Version);
        var replay = AssertReplay(db, first);

        Assert.Throws<InvalidOperationException>(() => match.ReportResult(replay));
        Assert.Equal(new[] { 0, 0 }, match.Wins);
        Assert.Equal(0, match.Draws);
        match.ReportResult(first);
        Assert.Throws<InvalidOperationException>(() => match.ReportResult(first));
        Assert.False(match.Over);
        Assert.Equal(new[] { 0, winner == 1 ? 1 : 0 }, match.Wins);
        Assert.Equal(winner == -1 ? 1 : 0, match.Draws);

        var second = match.StartNextGame();
        var fresh = new Battle(db, Teams(), names, new BattleOptions
        {
            Seed = 0, BanOne = true, LeadPick = true, Modifiers = rule.Modifiers
        });
        Assert.NotSame(first, second);
        Assert.Equal(0u, second.Seed);
        Assert.Equal(Phase.Ban, second.Phase);
        Assert.Equal(A, second.Sides[0].Team.Select(fighter => fighter.Def.Id));
        Assert.Equal(B, second.Sides[1].Team.Select(fighter => fighter.Def.Id));
        Assert.Equal(fresh.StateHash(), second.StateHash());
        Assert.Equal(fresh.Record.ToJson(), second.Record.ToJson());
        Assert.True(second.CanOfferDraw(0));
        Assert.False(second.HasDrawOffer(0));
        for (int side = 0; side < 2; side++)
        {
            Assert.False(second.HasChosenBan(side));
            Assert.False(second.HasChosenLead(side));
            foreach (var fighter in second.Sides[side].Team)
            {
                Assert.Equal(fighter.MaxHp, fighter.Hp);
                Assert.All(fighter.Skills, skill => Assert.Equal(skill.Max, skill.Pp));
            }
        }
        Assert.Equal(fresh.Random(), second.Random());
        second.ResolveSurrenders(true, true);
        AssertReplay(db, second);
        match.ReportResult(second);
        Assert.False(match.Over);

        var third = match.StartNextGame();
        Assert.Equal(1u, third.Seed);
        third.OfferDraw(1);
        third.RespondDraw(0, true);
        AssertReplay(db, third);
        match.ReportResult(third);

        Assert.True(match.Over);
        Assert.Equal(new[] { 0, winner == 1 ? 1 : 0 }, match.Wins);
        Assert.Equal(winner == -1 ? 3 : 2, match.Draws);
        Assert.Throws<InvalidOperationException>(() => match.StartNextGame());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void RealTurnCapDrawsExhaustTheConfiguredSeriesAndReplayWithoutInventedResults(int bestOf)
    {
        var db = BattleTests.LoadPack("original");
        var teams = new IList<string>[] { A.ToArray(), A.ToArray() };
        var match = new Match(db, new Ruleset { BestOf = bestOf }, teams, new[] { "甲", "乙" }, 5400);
        for (int index = 0; index < bestOf; index++)
        {
            Assert.False(match.Over);
            var game = match.StartNextGame();
            Assert.Equal((uint)(5400 + index), game.Seed);
            for (int turn = 1; turn <= Battle.TurnCap; turn++)
            {
                Assert.Equal(Phase.Choose, game.Phase);
                Assert.Equal(turn, game.Turn);
                game.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            }
            Assert.Equal(Phase.Over, game.Phase);
            Assert.Equal(-1, game.Winner);
            Assert.Equal(Battle.TurnCap, game.Record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn));
            AssertReplay(db, game);
            match.ReportResult(game);
            Assert.Equal(index + 1, match.Draws);
            Assert.Equal(new[] { 0, 0 }, match.Wins);
        }
        Assert.True(match.Over);
        Assert.Throws<InvalidOperationException>(() => match.StartNextGame());
    }

    static Battle AssertReplay(ContentDb db, Battle game)
    {
        string json = game.Record.ToJson();
        var replay = Replay.Run(db, BattleRecord.FromJson(json));
        Assert.Equal(game.Phase, replay.Phase);
        Assert.Equal(game.Winner, replay.Winner);
        Assert.Equal(game.StateHash(), replay.StateHash());
        Assert.Equal(json, replay.Record.ToJson());
        Assert.Equal(game.Random(), replay.Random());
        return replay;
    }
}
