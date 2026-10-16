using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T64_MatchRoomEdgeTests
{
    static readonly string[] A = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] B = { "yuan", "nyako", "buck", "kasumi", "bee", "enka" };

    static Ruleset Rule()
    {
        return new Ruleset
        {
            Id = "room-edge", Name = "边界测试", BestOf = 3,
            Modifiers = new List<string>(), TurnSeconds = 10, BankSeconds = 30
        };
    }

    static MatchRoom Make(ContentDb db = null, Ruleset rule = null)
    {
        return new MatchRoom(db ?? BattleTests.LoadPack("original"), rule ?? Rule(), "房主");
    }

    static void Prepare(MatchRoom room)
    {
        room.Join("来宾");
        Assert.Empty(room.SubmitTeam(0, A));
        Assert.Empty(room.SubmitTeam(1, B));
        room.SetReady(0, true);
        room.SetReady(1, true);
    }

    [Fact]
    public void GuestOperationsRequireJoinAndASecondGuestCannotReplaceTheFirst()
    {
        var room = Make();
        var empty = new Snapshot(room);
        Assert.Throws<InvalidOperationException>(() => room.SubmitTeam(1, B));
        Assert.Throws<InvalidOperationException>(() => room.SetReady(1, false));
        Assert.Throws<InvalidOperationException>(() => room.SetReady(1, true));
        Assert.Throws<ArgumentNullException>(() => room.Join(null));
        empty.AssertUnchanged(room);

        room.Join("原来宾");
        Assert.Empty(room.SubmitTeam(1, B));
        room.SetReady(1, true);
        var joined = new Snapshot(room);
        Assert.Throws<InvalidOperationException>(() => room.Join("另一位来宾"));
        joined.AssertUnchanged(room);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void InvalidSidesAreRejectedWithoutChangingRoom(int side)
    {
        var room = Make();
        room.Join("来宾");
        Assert.Empty(room.SubmitTeam(0, A));
        room.SetReady(0, true);
        var before = new Snapshot(room);

        Assert.Throws<ArgumentOutOfRangeException>(() => room.SubmitTeam(side, B));
        Assert.Throws<ArgumentOutOfRangeException>(() => room.SetReady(side, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => room.SetReady(side, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => room.SetRuleset(side, Rule()));

        before.AssertUnchanged(room);
    }

    [Theory]
    [InlineData(RoomState.Setup)]
    [InlineData(RoomState.Ready)]
    [InlineData(RoomState.InGame)]
    [InlineData(RoomState.BetweenGames)]
    [InlineData(RoomState.Finished)]
    public void OutOfOrderOperationsDoNotAdvanceOrResetTheRoom(RoomState target)
    {
        var rule = Rule();
        if (target == RoomState.Finished) rule.BestOf = 1;
        var room = Make(rule: rule);
        if (target != RoomState.Setup) Prepare(room);
        if (target == RoomState.InGame || target == RoomState.BetweenGames || target == RoomState.Finished)
        {
            var battle = room.Start(64);
            if (target != RoomState.InGame)
            {
                battle.Surrender(1);
                room.ReportGame(battle);
            }
        }
        Assert.Equal(target, room.State);
        var before = new Snapshot(room);
        var invalid = new List<Action>();
        if (target != RoomState.Setup)
        {
            invalid.Add(() => room.Join("另一位"));
            invalid.Add(() => room.SetRuleset(Rule()));
            invalid.Add(() => room.SubmitTeam(0, A));
        }
        if (target != RoomState.Setup && target != RoomState.Ready)
            invalid.Add(() => room.SetReady(0, false));
        if (target != RoomState.Ready) invalid.Add(() => room.Start(999));
        if (target != RoomState.InGame) invalid.Add(() => room.ReportGame(room.CurrentBattle));
        if (target != RoomState.BetweenGames) invalid.Add(() => room.NextGame());
        if (target != RoomState.Finished) invalid.Add(() => room.Rematch());

        foreach (var action in invalid)
        {
            Assert.Throws<InvalidOperationException>(action);
            before.AssertUnchanged(room);
        }
    }

    [Fact]
    public void RulesAndAllPublishedCollectionsAreIndependentSnapshots()
    {
        var input = Rule();
        input.Modifiers.Add("half_pp");
        string originalRules = input.ToJson();
        var room = Make(rule: input);
        input.BestOf = 1;
        input.Modifiers.Clear();
        var exposedRule = room.Ruleset;
        exposedRule.Season = "missing";
        exposedRule.Modifiers.Add("unknown_modifier");
        Assert.Equal(originalRules, room.Ruleset.ToJson());

        room.Join("来宾");
        var submitted = A.ToList();
        Assert.Empty(room.SubmitTeam(0, submitted));
        Assert.Empty(room.SubmitTeam(1, B));
        submitted[0] = "missing";
        var exposedTeams = room.Teams;
        exposedTeams[0].Clear();
        exposedTeams[1] = new List<string>();
        Assert.Equal(A, room.Teams[0]);
        Assert.Equal(B, room.Teams[1]);

        room.SetReady(0, true);
        room.SetReady(1, true);
        var exposedReady = room.Ready;
        exposedReady[0] = exposedReady[1] = false;
        Assert.Equal(new[] { true, true }, room.Ready);
        var emptyWins = room.Wins;
        emptyWins[1] = 20;
        Assert.Equal(new[] { 0, 0 }, room.Wins);

        var battle = room.Start(64);
        Assert.Equal(A, battle.Record.Teams[0]);
        Assert.Equal(B, battle.Record.Teams[1]);
        Assert.Equal(new[] { "half_pp" }, battle.Modifiers);
        battle.Surrender(1);
        room.ReportGame(battle);
        var wins = room.Wins;
        wins[0] = wins[1] = 99;
        Assert.Equal(new[] { 1, 0 }, room.Wins);
        Assert.Equal(RoomState.BetweenGames, room.State);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("short")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("banned")]
    public void InvalidTeamKeepsPreviouslyAcceptedTeamAndItsReadyFlag(string kind)
    {
        var rule = Rule();
        rule.Season = "s1";
        var room = Make(rule: rule);
        room.Join("来宾");
        Assert.Empty(room.SubmitTeam(0, A));
        room.SetReady(0, true);
        var before = new Snapshot(room);
        IList<string> invalid = A.ToList();
        if (kind == "null") invalid = null;
        else if (kind == "short") invalid.RemoveAt(0);
        else invalid[0] = kind == "duplicate" ? invalid[1] : kind == "banned" ? "kei" : "missing";

        Assert.NotEmpty(room.SubmitTeam(0, invalid));

        before.AssertUnchanged(room);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("bestOf")]
    [InlineData("season")]
    [InlineData("modifier")]
    [InlineData("turn")]
    [InlineData("bank")]
    public void InvalidRulesOrGuestRuleChangeDoNotClearHostReadiness(string kind)
    {
        var room = Make();
        room.Join("来宾");
        Assert.Empty(room.SubmitTeam(0, A));
        room.SetReady(0, true);
        var before = new Snapshot(room);
        var candidate = Rule();
        if (kind == "bestOf") candidate.BestOf = 2;
        else if (kind == "season") candidate.Season = "missing";
        else if (kind == "modifier") candidate.Modifiers.Add("missing");
        else if (kind == "turn") candidate.TurnSeconds = -1;
        else if (kind == "bank") candidate.BankSeconds = -1;

        if (kind == "null") Assert.Throws<ArgumentNullException>(() => room.SetRuleset(null));
        else Assert.Throws<KeyError>(() => room.SetRuleset(candidate));
        before.AssertUnchanged(room);
        Assert.Throws<InvalidOperationException>(() => room.SetRuleset(1, Rule()));
        before.AssertUnchanged(room);
    }

    [Fact]
    public void SuccessfulRulesetReplacementCopiesInputAndClearsBothReadyFlags()
    {
        var room = Make();
        Prepare(room);
        room.SetReady(0, false);
        var candidate = Rule();
        candidate.Name = "替换赛制";
        candidate.Modifiers.Add("hype_start");
        string expected = candidate.ToJson();

        room.SetRuleset(0, candidate);
        candidate.Modifiers.Clear();
        candidate.TurnSeconds = 999;

        Assert.Equal(RoomState.Setup, room.State);
        Assert.Equal(new[] { false, false }, room.Ready);
        Assert.Equal(expected, room.Ruleset.ToJson());
        Assert.Equal(A, room.Teams[0]);
        Assert.Equal(B, room.Teams[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void EitherSideCanWithdrawReadyWithoutLosingTeams(int side)
    {
        var room = Make();
        Prepare(room);

        room.SetReady(side, false);

        Assert.Equal(RoomState.Setup, room.State);
        Assert.False(room.Ready[side]);
        Assert.True(room.Ready[1 - side]);
        Assert.Equal(A, room.Teams[0]);
        Assert.Equal(B, room.Teams[1]);
        Assert.Throws<InvalidOperationException>(() => room.Start(64));
        room.SetReady(side, true);
        Assert.Equal(RoomState.Ready, room.State);
    }

    [Fact]
    public void OnlyTheCurrentFinishedInstanceCanBeReportedAndFailuresHaveNoSideEffects()
    {
        var room = Make();
        Prepare(room);
        var battle = room.Start(64);
        room.Clock.Tick(0, 12);
        var active = new Snapshot(room);
        Assert.Throws<InvalidOperationException>(() => room.ReportGame(null));
        Assert.Throws<InvalidOperationException>(() => room.ReportGame(battle));
        active.AssertUnchanged(room);

        var foreignRoom = Make();
        Prepare(foreignRoom);
        var foreign = foreignRoom.Start(64);
        foreign.Surrender(0);
        Assert.Throws<InvalidOperationException>(() => room.ReportGame(foreign));
        active.AssertUnchanged(room);

        battle.Surrender(1);
        var ended = new Snapshot(room);
        Assert.Throws<InvalidOperationException>(() => room.ReportGame(battle.Clone()));
        ended.AssertUnchanged(room);
        room.ReportGame(battle);
        var reported = new Snapshot(room);
        Assert.Throws<InvalidOperationException>(() => room.ReportGame(battle));
        reported.AssertUnchanged(room);

        var next = room.NextGame();
        var nextActive = new Snapshot(room);
        Assert.Throws<InvalidOperationException>(() => room.ReportGame(battle));
        nextActive.AssertUnchanged(room);
        Assert.Same(next, room.CurrentBattle);
    }

    [Fact]
    public void StartRevalidatesTeamsChangedAfterReadyAndDoesNotPublishPartialState()
    {
        var db = BattleTests.LoadPack("original");
        var room = Make(db);
        Prepare(room);
        int index = db.Streamers.FindIndex(streamer => streamer.Id == A[0]);
        var removed = db.Streamers[index];
        db.Streamers.RemoveAt(index);
        var before = new Snapshot(room);

        Assert.Throws<KeyError>(() => room.Start(999));
        before.AssertUnchanged(room);

        db.Streamers.Insert(index, removed);
        var battle = room.Start(64);
        Assert.Equal(64u, battle.Seed);
        Assert.Same(battle, room.CurrentBattle);
        Assert.Equal(RoomState.InGame, room.State);
    }

    [Fact]
    public void FailedNextGameContentCheckKeepsOldClockAndDoesNotConsumeSeed()
    {
        var db = BattleTests.LoadPack("original");
        var room = Make(db);
        Prepare(room);
        var first = room.Start(64);
        room.Clock.Tick(0, 14);
        first.Surrender(1);
        room.ReportGame(first);
        var before = new Snapshot(room);
        string originalName = db.Skills["warm_up"].Name;
        db.Skills["warm_up"].Name = "局间内容改动";

        Assert.Throws<KeyError>(() => room.NextGame());
        before.AssertUnchanged(room);

        db.Skills["warm_up"].Name = originalName;
        var second = room.NextGame();
        Assert.Equal(65u, second.Seed);
        Assert.Equal(new[] { 1, 0 }, room.Wins);
        Assert.Same(second, room.CurrentBattle);
    }

    sealed class Snapshot
    {
        readonly RoomState state;
        readonly string rules, host, guest, record;
        readonly IList<string>[] teams;
        readonly bool[] ready;
        readonly int[] wins;
        readonly int draws;
        readonly Battle battle;
        readonly TurnClock clock;
        readonly ulong? hash;
        readonly double[] turnLeft, bankLeft;
        readonly bool[] timedOut;

        public Snapshot(MatchRoom room)
        {
            state = room.State; rules = room.Ruleset.ToJson();
            host = room.HostName; guest = room.GuestName;
            teams = room.Teams; ready = room.Ready; wins = room.Wins; draws = room.Draws;
            battle = room.CurrentBattle; clock = room.Clock;
            hash = battle?.StateHash(); record = battle?.Record.ToJson();
            if (clock == null) return;
            turnLeft = new[] { clock.TurnLeft(0), clock.TurnLeft(1) };
            bankLeft = new[] { clock.BankLeft(0), clock.BankLeft(1) };
            timedOut = new[] { clock.TimedOut(0), clock.TimedOut(1) };
        }

        public void AssertUnchanged(MatchRoom room)
        {
            Assert.Equal(state, room.State); Assert.Equal(rules, room.Ruleset.ToJson());
            Assert.Equal(host, room.HostName); Assert.Equal(guest, room.GuestName);
            Assert.Equal(teams[0], room.Teams[0]); Assert.Equal(teams[1], room.Teams[1]);
            Assert.Equal(ready, room.Ready); Assert.Equal(wins, room.Wins); Assert.Equal(draws, room.Draws);
            Assert.Same(battle, room.CurrentBattle); Assert.Same(clock, room.Clock);
            Assert.Equal(hash, room.CurrentBattle?.StateHash());
            Assert.Equal(record, room.CurrentBattle?.Record.ToJson());
            if (clock == null) return;
            for (int side = 0; side < 2; side++)
            {
                Assert.Equal(turnLeft[side], room.Clock.TurnLeft(side));
                Assert.Equal(bankLeft[side], room.Clock.BankLeft(side));
                Assert.Equal(timedOut[side], room.Clock.TimedOut(side));
            }
        }
    }
}
