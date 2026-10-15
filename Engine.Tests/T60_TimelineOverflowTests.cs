using System;
using StreamDuel.Core;
using Xunit;

public class T60_TimelineOverflowTests
{
    static BattleEvent[] DamageEvents()
    {
        return new[]
        {
            new BattleEvent { Kind = BattleEventKind.Damage, Amount = 10 },
            new BattleEvent { Kind = BattleEventKind.Damage, Amount = 20 },
            new BattleEvent { Kind = BattleEventKind.Damage, Amount = 30 }
        };
    }

    [Theory]
    [InlineData(double.Epsilon)]
    [InlineData(1e-308)]
    public void SpeedThatOverflowsACueOrItsEndIsRejected(double speed)
    {
        var events = DamageEvents();

        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            BattleTimeline.Build(events, 0, events.Length, speed));

        Assert.Equal("speed", error.ParamName);
    }

    [Fact]
    public void VerySmallSpeedStillWorksWhenEveryCueAndItsEndRemainFinite()
    {
        var events = DamageEvents();

        var cues = BattleTimeline.Build(events, 0, events.Length, 1e-300);

        Assert.Equal(12, cues.Count);
        Assert.All(cues, cue =>
        {
            Assert.True(double.IsFinite(cue.Start));
            Assert.True(double.IsFinite(cue.Duration));
            Assert.True(double.IsFinite(cue.Start + cue.Duration));
        });
        double total = BattleTimeline.TotalDuration(cues);
        Assert.True(double.IsFinite(total));
        Assert.InRange(total, 2.39e300, 2.41e300);
    }
}
