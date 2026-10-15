// T-55 验收测试：对战计时（每回合限时 + 总时间）。实现后把本文件移到 Engine.Tests/ 下。
using StreamDuel.Core;
using Xunit;

public class T55_TurnClockTests
{
    [Fact]
    public void PerTurnLimitThenBank()
    {
        var c = new TurnClock(turnSeconds: 10, bankSeconds: 30);
        c.StartTurn();
        Assert.False(c.Tick(0, 9.0));
        Assert.Equal(1.0, c.TurnLeft(0), 3);
        Assert.False(c.Tick(0, 5.0));          // 超出的 4 秒从总时间扣
        Assert.Equal(26.0, c.BankLeft(0), 3);
        Assert.True(c.Tick(0, 26.5));          // 总时间用完
        Assert.True(c.TimedOut(0));
        Assert.False(c.TimedOut(1));
    }

    [Fact]
    public void SubmittingStopsTheClockAndNewTurnResets()
    {
        var c = new TurnClock(10, 30);
        c.StartTurn();
        c.Tick(1, 3);
        c.Submit(1);
        c.Tick(1, 100);
        Assert.False(c.TimedOut(1));
        Assert.Equal(30.0, c.BankLeft(1), 3);
        c.StartTurn();
        Assert.Equal(10.0, c.TurnLeft(1), 3);
    }

    [Fact]
    public void TimeoutActionIsLegal()
    {
        var db = BattleTests.LoadPack("original");
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" } }, new[] { "A", "B" }, 1);
        var a = TurnClock.TimeoutAction(b, 0);
        Assert.Equal(ActionType.Pass, a.Type);
        b.Sides[0].Team[0].Fainted = true; b.Sides[0].Team[0].Hp = 0; b.Need[0] = true;
        Assert.InRange(TurnClock.TimeoutReplacement(b, 0), 1, 5);
    }
}
