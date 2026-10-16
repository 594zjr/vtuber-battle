using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T64_MatchRoomTests
{
    static readonly string[] A = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] B = { "yuan", "nyako", "buck", "kasumi", "bee", "enka" };

    static Ruleset Rule(int bestOf = 1)
    {
        return new Ruleset { Id = "room-test", Name = "房间测试", BestOf = bestOf, Modifiers = new List<string>() };
    }

    static MatchRoom Prepared(ContentDb db, Ruleset rule)
    {
        var room = new MatchRoom(db, rule, "房主");
        room.Join("客人");
        Assert.Empty(room.SubmitTeam(0, A));
        Assert.Empty(room.SubmitTeam(1, B));
        room.SetReady(0, true);
        room.SetReady(1, true);
        Assert.Equal(RoomState.Ready, room.State);
        return room;
    }

    static void AssertReplay(ContentDb db, Battle battle)
    {
        string record = battle.Record.ToJson();
        var replay = Replay.Run(db, BattleRecord.FromJson(record));
        Assert.Equal(battle.StateHash(), replay.StateHash());
        Assert.Equal(battle.Log.Select(line => line.Node + ":" + line.Text),
            replay.Log.Select(line => line.Node + ":" + line.Text));
        Assert.Equal(record, battle.Record.ToJson());
    }

    [Fact]
    public void TwoPlayersCanPrepareWithdrawStartAndReportOneCompletedGame()
    {
        var db = BattleTests.LoadPack("original");
        var room = new MatchRoom(db, Rule(), "房主");
        Assert.Equal(RoomState.Setup, room.State);
        Assert.Equal("房主", room.HostName);
        Assert.False(room.HasGuest);
        Assert.Null(room.GuestName);
        Assert.Null(room.ComputerLevel);
        Assert.Null(room.CurrentBattle);
        Assert.Null(room.Clock);
        Assert.Equal(new[] { 0, 0 }, room.Wins);
        room.Join("客人");
        Assert.True(room.HasGuest);
        Assert.Equal("客人", room.GuestName);
        Assert.Empty(room.SubmitTeam(0, A));
        Assert.Empty(room.SubmitTeam(1, B));
        room.SetReady(0, true);
        Assert.Equal(RoomState.Setup, room.State);
        Assert.Equal(new[] { true, false }, room.Ready);
        room.SetReady(1, true);
        Assert.Equal(RoomState.Ready, room.State);
        room.SetReady(1, false);
        Assert.Equal(RoomState.Setup, room.State);
        Assert.Equal(new[] { true, false }, room.Ready);
        room.SetReady(1, true);

        var battle = room.Start(6401);

        Assert.Same(battle, room.CurrentBattle);
        Assert.Equal(RoomState.InGame, room.State);
        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.Equal((uint)6401, battle.Seed);
        Assert.Equal(new[] { "房主", "客人" }, battle.Record.Names);
        Assert.Null(room.Clock);
        battle.Surrender(1);
        Assert.Equal(RoomState.InGame, room.State);
        Assert.Equal(new[] { 0, 0 }, room.Wins);
        room.ReportGame(battle);
        Assert.Equal(RoomState.Finished, room.State);
        Assert.Equal(new[] { 1, 0 }, room.Wins);
        Assert.Equal(0, room.Draws);
        Assert.Same(battle, room.CurrentBattle);
        AssertReplay(db, battle);
    }

    [Fact]
    public void SubmittingOneTeamKeepsTheOtherPlayersPreparation()
    {
        var db = BattleTests.LoadPack("original");
        var rule = Rule();
        rule.Season = "s1";
        var room = new MatchRoom(db, rule, "房主");
        room.Join("客人");
        Assert.Empty(room.SubmitTeam(0, A));
        room.SetReady(0, true);

        Assert.Empty(room.SubmitTeam(1, B));
        Assert.Equal(new[] { true, false }, room.Ready);
        Assert.Equal(RoomState.Setup, room.State);
        room.SetReady(1, true);
        Assert.Equal(RoomState.Ready, room.State);
        room.SetReady(1, false);
        Assert.Empty(room.SubmitTeam(0, A.Reverse().ToArray()));
        Assert.Equal(new[] { false, false }, room.Ready);
        room.SetReady(0, true);
        room.SetReady(1, true);
        Assert.Equal(A.Reverse(), room.Start(6402).Record.Teams[0]);
    }

    [Fact]
    public void HostCanChangeSeasonAndRulesBeforeBothPlayersPrepareAgain()
    {
        var db = BattleTests.LoadPack("original");
        var room = new MatchRoom(db, Rule(), "房主");
        room.Join("客人");
        var banned = new[] { "kei", "enka", "shiori", "lumi", "hane", "momo" };
        Assert.Empty(room.SubmitTeam(0, banned));
        Assert.Empty(room.SubmitTeam(1, B));
        room.SetReady(1, true);
        var ranked = Rulesets.All.Single(r => r.Id == "ranked");
        ranked.Season = "s1";

        room.SetRuleset(0, ranked);

        Assert.Equal(new[] { false, false }, room.Ready);
        Assert.Equal(RoomState.Setup, room.State);
        Assert.Equal(banned, room.Teams[0]);
        Assert.Throws<KeyError>(() => room.SetReady(0, true));
        Assert.Equal(ranked.CheckTeam(db, banned), room.SubmitTeam(0, banned));
        Assert.Empty(room.SubmitTeam(0, A));
        room.SetReady(0, true);
        room.SetReady(1, true);
        Assert.Equal(Phase.Ban, room.Start(6403).Phase);
        Assert.Equal(10.0, room.Clock.TurnLeft(0));
        Assert.Equal(30.0, room.Clock.BankLeft(0));
    }

    [Fact]
    public void EachRankedGameStartsWithOriginalSixAndFreshBanLeadAndModifiers()
    {
        var db = BattleTests.LoadPack("original");
        var rule = Rulesets.All.Single(r => r.Id == "ranked");
        rule.Modifiers.AddRange(new[] { "hype_start", "half_pp" });
        var room = Prepared(db, rule);
        var first = room.Start(6404);
        Assert.Equal(Phase.Ban, first.Phase);
        first.ChooseBan(0, 4);
        Assert.Equal(Phase.Ban, first.Phase);
        Assert.All(first.Sides, side => Assert.Equal(6, side.Team.Count));
        first.ChooseBan(1, 0);
        Assert.Equal(Phase.Lead, first.Phase);
        Assert.DoesNotContain(first.Sides[0].Team, fighter => fighter.Def.Id == "bell");
        Assert.DoesNotContain(first.Sides[1].Team, fighter => fighter.Def.Id == "bee");
        first.ChooseLead(0, 2);
        first.ChooseLead(1, 1);
        Assert.Equal("lumi", first.Active(0).Def.Id);
        Assert.Equal("nyako", first.Active(1).Def.Id);
        Assert.Equal(Phase.Choose, first.Phase);
        Assert.Equal(new[] { "half_pp", "hype_start" }, first.Modifiers);
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(1, first.Active(side).FieldOf("hype").Stacks);
            foreach (var fighter in first.Sides[side].Team)
            foreach (var skill in fighter.Skills)
            {
                Assert.Equal((db.Skills[skill.Id].Pp + 1) / 2, skill.Max);
                Assert.Equal(skill.Max, skill.Pp);
            }
        }
        first.ResolveTurn(Ai.Choose(first, 0), Ai.Choose(first, 1));
        first.Surrender(1);
        string completedRecord = first.Record.ToJson();
        room.ReportGame(first);
        Assert.Equal(RoomState.BetweenGames, room.State);

        var second = room.NextGame();

        Assert.NotSame(first, second);
        Assert.Equal(RoomState.InGame, room.State);
        Assert.Equal((uint)6405, second.Seed);
        Assert.Equal(Phase.Ban, second.Phase);
        Assert.Equal(A, second.Sides[0].Team.Select(fighter => fighter.Def.Id));
        Assert.Equal(B, second.Sides[1].Team.Select(fighter => fighter.Def.Id));
        Assert.Equal(A, room.Teams[0]);
        Assert.Equal(B, room.Teams[1]);
        Assert.Equal(new[] { "half_pp", "hype_start" }, second.Record.Modifiers);
        Assert.Empty(second.Record.Events);
        for (int side = 0; side < 2; side++)
        {
            Assert.False(second.HasChosenBan(side));
            Assert.False(second.HasChosenLead(side));
            foreach (var fighter in second.Sides[side].Team)
            {
                Assert.Equal(fighter.MaxHp, fighter.Hp);
                Assert.False(fighter.Fainted);
                Assert.Empty(fighter.Statuses);
                Assert.All(fighter.Skills, skill => Assert.Equal(skill.Max, skill.Pp));
            }
        }
        Assert.Equal(completedRecord, first.Record.ToJson());
        AssertReplay(db, first);
        second.ChooseBan(0, 0);
        second.ChooseBan(1, 5);
        second.ChooseLead(0, 0);
        second.ChooseLead(1, 0);
        Assert.Equal("bell", second.Active(0).Def.Id);
        Assert.Equal("nyako", second.Active(1).Def.Id);
    }

    [Theory]
    [InlineData(10, 30)]
    [InlineData(0, 12)]
    [InlineData(8, 0)]
    [InlineData(0, 0)]
    public void ClockIsOptionalAndEveryGameGetsIndependentFullBudgets(int turnSeconds, int bankSeconds)
    {
        var db = BattleTests.LoadPack("original");
        var rule = Rule(3);
        rule.TurnSeconds = turnSeconds;
        rule.BankSeconds = bankSeconds;
        var room = Prepared(db, rule);
        var first = room.Start(6405);
        var firstClock = room.Clock;
        bool enabled = turnSeconds != 0 || bankSeconds != 0;
        var hash = first.StateHash();
        string record = first.Record.ToJson();
        var untouched = first.Clone();
        if (enabled)
        {
            Assert.NotNull(firstClock);
            Assert.Equal((double)turnSeconds, firstClock.TurnLeft(0));
            Assert.Equal((double)bankSeconds, firstClock.BankLeft(0));
            Assert.True(firstClock.Tick(0, turnSeconds + bankSeconds));
            firstClock.Submit(1);
            Assert.False(firstClock.Tick(1, turnSeconds + bankSeconds + 1));
        }
        else Assert.Null(firstClock);
        Assert.Equal(hash, first.StateHash());
        Assert.Equal(record, first.Record.ToJson());
        Assert.Equal(untouched.Random(), first.Clone().Random());
        first.Surrender(1);
        room.ReportGame(first);
        Assert.Same(firstClock, room.Clock);

        var second = room.NextGame();

        Assert.NotSame(first, second);
        if (enabled)
        {
            Assert.NotSame(firstClock, room.Clock);
            for (int side = 0; side < 2; side++)
            {
                Assert.Equal((double)turnSeconds, room.Clock.TurnLeft(side));
                Assert.Equal((double)bankSeconds, room.Clock.BankLeft(side));
                Assert.False(room.Clock.TimedOut(side));
            }
            Assert.True(room.Clock.Tick(1, turnSeconds + bankSeconds));
            Assert.True(firstClock.TimedOut(0));
            Assert.False(firstClock.TimedOut(1));
        }
        else Assert.Null(room.Clock);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void SeriesEndsAsSoonAsOneSideReachesItsRequiredWins(int bestOf)
    {
        var db = BattleTests.LoadPack("original");
        var room = Prepared(db, Rule(bestOf));
        var battle = room.Start(6406);
        int needed = bestOf / 2 + 1;
        for (int game = 0; game < needed; game++)
        {
            Assert.Equal((uint)(6406 + game), battle.Seed);
            battle.Surrender(0);
            room.ReportGame(battle);
            Assert.Equal(new[] { 0, game + 1 }, room.Wins);
            Assert.Equal(0, room.Draws);
            Assert.Equal(game + 1 == needed ? RoomState.Finished : RoomState.BetweenGames, room.State);
            AssertReplay(db, battle);
            if (game + 1 < needed) battle = room.NextGame();
        }
        Assert.Same(battle, room.CurrentBattle);
        Assert.Throws<InvalidOperationException>(() => room.NextGame());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void DrawsConsumeGameSlotsAndSequentialSeedsWrapWithoutExtraGames(int bestOf)
    {
        var db = BattleTests.LoadPack("original");
        var room = Prepared(db, Rule(bestOf));
        uint seed = uint.MaxValue - 1;
        var battle = room.Start(seed);
        for (int game = 0; game < bestOf; game++)
        {
            Assert.Equal(unchecked(seed + (uint)game), battle.Seed);
            battle.OfferDraw(0);
            battle.RespondDraw(1, true);
            room.ReportGame(battle);
            Assert.Equal(-1, battle.Winner);
            Assert.Equal(new[] { 0, 0 }, room.Wins);
            Assert.Equal(game + 1, room.Draws);
            Assert.Equal(game + 1 == bestOf ? RoomState.Finished : RoomState.BetweenGames, room.State);
            AssertReplay(db, battle);
            if (game + 1 < bestOf) battle = room.NextGame();
        }
        Assert.Throws<InvalidOperationException>(() => room.NextGame());
    }

    [Fact]
    public void RematchRetainsParticipantsTeamsAndRulesButCreatesNewScoreBattleAndClock()
    {
        var db = BattleTests.LoadPack("original");
        var rule = Rule();
        rule.Season = "s1";
        rule.TurnSeconds = 7;
        rule.BankSeconds = 19;
        rule.Modifiers.Add("half_pp");
        var room = Prepared(db, rule);
        var first = room.Start(6407);
        var oldClock = room.Clock;
        oldClock.Tick(0, 12);
        first.Surrender(1);
        room.ReportGame(first);

        room.Rematch();

        Assert.Equal(RoomState.Setup, room.State);
        Assert.Equal("房主", room.HostName);
        Assert.Equal("客人", room.GuestName);
        Assert.True(room.HasGuest);
        Assert.Equal(A, room.Teams[0]);
        Assert.Equal(B, room.Teams[1]);
        Assert.Equal(rule.ToJson(), room.Ruleset.ToJson());
        Assert.Equal(new[] { false, false }, room.Ready);
        Assert.Equal(new[] { 0, 0 }, room.Wins);
        Assert.Equal(0, room.Draws);
        Assert.Null(room.CurrentBattle);
        Assert.Null(room.Clock);
        room.SetReady(0, true);
        room.SetReady(1, true);
        var rematch = room.Start(6408);
        Assert.NotSame(first, rematch);
        Assert.NotSame(oldClock, room.Clock);
        Assert.Equal((uint)6408, rematch.Seed);
        Assert.Equal(19.0, room.Clock.BankLeft(0));
        rematch.Surrender(0);
        room.ReportGame(rematch);
        Assert.Equal(new[] { 0, 1 }, room.Wins);
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void AiFactoryIsDeterministicReadyAndDoesNotChooseBattleActions(AiLevel level)
    {
        var db = BattleTests.LoadPack("original");
        string contentHash = db.ContentHash();
        var rule = Rule();
        rule.Season = "s1";
        rule.BanOne = rule.LeadPick = true;
        var room = MatchRoom.VsAi(db, rule, "玩家", level, 6491);
        var repeated = MatchRoom.VsAi(db, rule, "玩家", level, 6491);
        Assert.Equal(RoomState.Setup, room.State);
        Assert.True(room.HasGuest);
        Assert.Equal(level, room.ComputerLevel);
        Assert.False(string.IsNullOrWhiteSpace(room.GuestName));
        Assert.Equal(new[] { false, true }, room.Ready);
        Assert.Empty(room.Teams[0]);
        Assert.Equal(repeated.Teams[1], room.Teams[1]);
        Assert.Empty(rule.CheckTeam(db, room.Teams[1]));
        Assert.Null(room.CurrentBattle);
        Assert.Null(room.Clock);

        Assert.Empty(room.SubmitTeam(0, A));
        Assert.True(room.Ready[1]);
        room.SetReady(0, true);
        Assert.Equal(RoomState.Ready, room.State);
        var battle = room.Start(6492);
        var expected = new Battle(db, room.Teams, new[] { room.HostName, room.GuestName },
            new BattleOptions { Seed = 6492, BanOne = true, LeadPick = true });
        Assert.Equal(Phase.Ban, battle.Phase);
        Assert.False(battle.HasChosenBan(1));
        Assert.Empty(battle.Record.Events);
        Assert.Equal(expected.StateHash(), battle.StateHash());
        Assert.Equal(expected.Record.ToJson(), battle.Record.ToJson());
        Assert.Equal(expected.Clone().Random(), battle.Clone().Random());
        Assert.Equal(contentHash, db.ContentHash());
    }

    [Fact]
    public void AiRematchKeepsItsTeamAndLevelButRequiresBothSidesToPrepareAgain()
    {
        var db = BattleTests.LoadPack("original");
        var room = MatchRoom.VsAi(db, Rule(), "玩家", AiLevel.Hard, 6493);
        var aiTeam = room.Teams[1].ToArray();
        Assert.Empty(room.SubmitTeam(0, A));
        room.SetReady(0, true);
        var battle = room.Start(6494);
        battle.Surrender(0);
        room.ReportGame(battle);

        room.Rematch();

        Assert.Equal(AiLevel.Hard, room.ComputerLevel);
        Assert.Equal(aiTeam, room.Teams[1]);
        Assert.Equal(new[] { false, false }, room.Ready);
        room.SetReady(0, true);
        Assert.Equal(RoomState.Setup, room.State);
        room.SetReady(1, true);
        Assert.Equal(RoomState.Ready, room.State);
        Assert.Equal((uint)6495, room.Start(6495).Seed);
    }

    [Fact]
    public void RealAiBattleRunsThroughTheRoomAndItsRecordReplaysExactly()
    {
        var db = BattleTests.LoadPack("original");
        var rule = Rulesets.All.Single(r => r.Id == "chaos");
        rule.BanOne = rule.LeadPick = true;
        var room = MatchRoom.VsAi(db, rule, "玩家", AiLevel.Normal, 6496);
        Assert.Empty(room.SubmitTeam(0, A));
        room.SetReady(0, true);
        var battle = room.Start(6497);
        int steps = 0;
        while (battle.Phase != Phase.Over && steps++ < 500)
        {
            if (battle.Phase == Phase.Ban)
            {
                battle.ChooseBan(0, Ai.ChooseBan(battle, 0));
                battle.ChooseBan(1, Ai.ChooseBan(battle, 1));
            }
            else if (battle.Phase == Phase.Lead)
            {
                battle.ChooseLead(0, Ai.ChooseLead(battle, 0));
                battle.ChooseLead(1, Ai.ChooseLead(battle, 1));
            }
            else if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(Ai.Choose(battle, 0, AiLevel.Normal), Ai.Choose(battle, 1, room.ComputerLevel.Value));
            else
                for (int side = 0; side < 2; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, AiLevel.Normal));
        }

        Assert.Equal(Phase.Over, battle.Phase);
        Assert.True(battle.Turn > 1);
        Assert.Equal(RoomState.InGame, room.State);
        room.ReportGame(battle);
        Assert.Equal(RoomState.Finished, room.State);
        Assert.Equal(1, room.Wins.Sum() + room.Draws);
        AssertReplay(db, battle);
    }
}
