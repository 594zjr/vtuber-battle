using System;
using System.Collections.Generic;
using StreamDuel.Core;
using Xunit;

public class T55_TurnClockEdgeTests
{
    static Battle Make(bool ban = false)
    {
        var db = BattleTests.LoadPack("original");
        return new Battle(db, new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "甲方", "乙方" }, new BattleOptions { Seed = 55, BanOne = ban });
    }

    [Fact]
    public void ConstructorInitializesBothSidesAndTickOnlyChargesTheChosenSide()
    {
        var clock = new TurnClock(10, 30);
        Assert.Equal(10, clock.TurnLeft(0));
        Assert.Equal(10, clock.TurnLeft(1));
        Assert.Equal(30, clock.BankLeft(0));
        Assert.Equal(30, clock.BankLeft(1));
        Assert.False(clock.Tick(0, 3));
        Assert.Equal(7, clock.TurnLeft(0));
        Assert.Equal(10, clock.TurnLeft(1));
        Assert.False(clock.TimedOut(0));
        Assert.False(clock.TimedOut(1));
    }

    [Fact]
    public void ExactBasicBoundaryPreservesBankAndExactTotalBoundaryTimesOut()
    {
        var clock = new TurnClock(2.5, 7.5);
        Assert.False(clock.Tick(0, 2.5));
        Assert.Equal(0, clock.TurnLeft(0));
        Assert.Equal(7.5, clock.BankLeft(0));
        Assert.True(clock.Tick(0, 7.5));
        Assert.Equal(0, clock.BankLeft(0));
        Assert.True(clock.Tick(0, 0));
        Assert.False(clock.Tick(1, 0));
    }

    [Fact]
    public void ZeroBasicBudgetImmediatelyUsesBank()
    {
        var clock = new TurnClock(0, 0.5);
        Assert.False(clock.TimedOut(0));
        Assert.False(clock.Tick(0, 0));
        Assert.False(clock.Tick(0, 0.25));
        Assert.Equal(0, clock.TurnLeft(0));
        Assert.Equal(0.25, clock.BankLeft(0));
        Assert.True(clock.Tick(0, 0.25));
    }

    [Fact]
    public void ZeroBankBudgetStillAllowsTheWholeBasicBudget()
    {
        var clock = new TurnClock(0.5, 0);
        Assert.False(clock.Tick(0, 0.25));
        Assert.Equal(0.25, clock.TurnLeft(0));
        Assert.Equal(0, clock.BankLeft(0));
        Assert.True(clock.Tick(0, 0.25));
    }

    [Fact]
    public void NoBudgetIsAlreadyTimedOutAndSubmittingDoesNotEraseThatResult()
    {
        var clock = new TurnClock(0, 0);
        Assert.True(clock.TimedOut(0));
        Assert.True(clock.TimedOut(1));
        Assert.True(clock.Tick(0, 0));
        clock.Submit(0);
        Assert.True(clock.Tick(0, 100));
        clock.StartTurn();
        Assert.True(clock.TimedOut(0));
        Assert.True(clock.TimedOut(1));
    }

    [Fact]
    public void SubmissionFreezesBothBalancesButTheOtherSideKeepsRunning()
    {
        var clock = new TurnClock(10, 30);
        clock.Tick(0, 3);
        clock.Submit(0);
        clock.Submit(0);
        Assert.False(clock.Tick(0, double.MaxValue));
        Assert.Equal(7, clock.TurnLeft(0));
        Assert.Equal(30, clock.BankLeft(0));
        Assert.False(clock.Tick(1, 12));
        Assert.Equal(0, clock.TurnLeft(1));
        Assert.Equal(28, clock.BankLeft(1));

        clock.StartTurn();
        Assert.Equal(10, clock.TurnLeft(0));
        Assert.Equal(10, clock.TurnLeft(1));
        Assert.Equal(30, clock.BankLeft(0));
        Assert.Equal(28, clock.BankLeft(1));
        Assert.False(clock.Tick(0, 11));
        Assert.Equal(29, clock.BankLeft(0));
    }

    [Fact]
    public void NewPhaseRestoresBasicTimeButNeverRefillsAnExhaustedBank()
    {
        var clock = new TurnClock(3, 1);
        Assert.True(clock.Tick(0, 4));
        clock.Submit(0);
        Assert.True(clock.TimedOut(0));
        Assert.True(clock.Tick(0, 0));
        clock.StartTurn();
        Assert.False(clock.TimedOut(0));
        Assert.Equal(3, clock.TurnLeft(0));
        Assert.Equal(0, clock.BankLeft(0));
        Assert.Equal(1, clock.BankLeft(1));
        Assert.True(clock.Tick(0, 3));
    }

    [Fact]
    public void FractionalTicksCarryOnlyTheirExcessIntoThePersistentBank()
    {
        var clock = new TurnClock(0.5, 0.75);
        Assert.False(clock.Tick(1, 0.25));
        Assert.False(clock.Tick(1, 0.5));
        Assert.Equal(0, clock.TurnLeft(1));
        Assert.Equal(0.5, clock.BankLeft(1));
        clock.Submit(1);
        clock.StartTurn();
        Assert.Equal(0.5, clock.TurnLeft(1));
        Assert.Equal(0.5, clock.BankLeft(1));
        Assert.True(clock.Tick(1, 1));
    }

    [Fact]
    public void MaximumFiniteBudgetsDoNotOverflowWhenCombined()
    {
        var clock = new TurnClock(double.MaxValue, double.MaxValue);
        Assert.False(clock.Tick(0, double.MaxValue));
        Assert.Equal(0, clock.TurnLeft(0));
        Assert.Equal(double.MaxValue, clock.BankLeft(0));
        Assert.True(clock.Tick(0, double.MaxValue));
        Assert.Equal(0, clock.TurnLeft(0));
        Assert.Equal(0, clock.BankLeft(0));
        Assert.Equal(double.MaxValue, clock.TurnLeft(1));
        Assert.Equal(double.MaxValue, clock.BankLeft(1));
    }

    [Fact]
    public void OversizedTickClampsAtZeroAndTimeoutStaysLatchedForThisPhase()
    {
        var clock = new TurnClock(2, 3);
        Assert.True(clock.Tick(1, double.MaxValue));
        Assert.Equal(0, clock.TurnLeft(1));
        Assert.Equal(0, clock.BankLeft(1));
        clock.Submit(1);
        Assert.True(clock.Tick(1, double.MaxValue));
        Assert.True(clock.TimedOut(1));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-double.Epsilon)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidBudgetsAndTicksAreRejectedEvenForSubmittedSides(double seconds)
    {
        Assert.Equal("turnSeconds", Assert.Throws<ArgumentOutOfRangeException>(() => new TurnClock(seconds, 1)).ParamName);
        Assert.Equal("bankSeconds", Assert.Throws<ArgumentOutOfRangeException>(() => new TurnClock(1, seconds)).ParamName);
        var clock = new TurnClock(10, 30);
        clock.Tick(0, 2);
        clock.Submit(1);
        Assert.Equal("seconds", Assert.Throws<ArgumentOutOfRangeException>(() => clock.Tick(0, seconds)).ParamName);
        Assert.Equal("seconds", Assert.Throws<ArgumentOutOfRangeException>(() => clock.Tick(1, seconds)).ParamName);
        Assert.Equal(8, clock.TurnLeft(0));
        Assert.Equal(10, clock.TurnLeft(1));
        Assert.Equal(30, clock.BankLeft(0));
        Assert.Equal(30, clock.BankLeft(1));
        Assert.False(clock.TimedOut(0));
        Assert.False(clock.TimedOut(1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void EverySideTakingEntryPointRejectsInvalidSides(int side)
    {
        var clock = new TurnClock(1, 1);
        var battle = Make();
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Tick(side, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Submit(side));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.TurnLeft(side));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.BankLeft(side));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.TimedOut(side));
        Assert.Throws<ArgumentOutOfRangeException>(() => TurnClock.TimeoutAction(battle, side));
        Assert.Throws<ArgumentOutOfRangeException>(() => TurnClock.TimeoutReplacement(battle, side));
    }

    [Fact]
    public void TimeoutHelpersRejectNullBattle()
    {
        Assert.Throws<ArgumentNullException>(() => TurnClock.TimeoutAction(null, 0));
        Assert.Throws<ArgumentNullException>(() => TurnClock.TimeoutReplacement(null, 0));
    }

    [Fact]
    public void DefaultActionRemainsRawPassAndHelpersDoNotMutateBattleOrRng()
    {
        var battle = Make();
        foreach (var skill in battle.Active(0).Skills) skill.Pp = 0;
        battle.Active(1).Statuses.Add(new StatusInst { Id = "stall", Left = 1 });
        ulong hash = battle.StateHash();
        string record = battle.Record.ToJson();
        int logs = battle.Log.Count;
        var expected = battle.Clone();
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(ActionType.Pass, TurnClock.TimeoutAction(battle, side).Type);
            Assert.Equal(1, TurnClock.TimeoutReplacement(battle, side));
        }
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(logs, battle.Log.Count);
        Assert.Equal(expected.Random(), battle.Random());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ReplacementSelectsLowestLivingBenchIndexAndSkipsInconsistentDeadStates(int side)
    {
        var battle = Make();
        var state = battle.Sides[side];
        state.Active = 3;
        state.Team[0].Fainted = true;
        state.Team[0].Hp = 20;
        state.Team[1].Hp = 0;
        state.Team[2].Hp = -1;
        Assert.Equal(4, TurnClock.TimeoutReplacement(battle, side));
        state.Team[0].Fainted = false;
        state.Team[0].Hp = 1;
        Assert.Equal(0, TurnClock.TimeoutReplacement(battle, side));
    }

    [Fact]
    public void NoBenchCandidateReturnsMinusOneEvenWhenTheActiveFighterIsAlive()
    {
        var battle = Make();
        var state = battle.Sides[0];
        for (int index = 1; index < state.Team.Count; index++)
        {
            state.Team[index].Fainted = true;
            state.Team[index].Hp = 0;
        }
        Assert.Equal(-1, TurnClock.TimeoutReplacement(battle, 0));
        state.Team[0].Fainted = true;
        state.Team[0].Hp = 0;
        Assert.Equal(-1, TurnClock.TimeoutReplacement(battle, 0));
    }

    [Fact]
    public void ReplacementUsesActualRosterLengthAfterBans()
    {
        var battle = Make(true);
        battle.ChooseBan(0, 5);
        battle.ChooseBan(1, 5);
        Assert.Equal(5, battle.Sides[0].Team.Count);
        for (int index = 1; index < 4; index++) battle.Sides[0].Team[index].Fainted = true;
        Assert.Equal(4, TurnClock.TimeoutReplacement(battle, 0));
    }
}
