// T-45 验收测试：每日挑战。实现后把本文件移到 Engine.Tests/ 下。
using System;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T45_DailyChallengeTests
{
    [Fact]
    public void SameDateSameChallengeDifferentDateDifferent()
    {
        var db = BattleTests.LoadPack("original");
        var a = DailyChallenge.For(db, new DateTime(2026, 10, 15));
        var b = DailyChallenge.For(db, new DateTime(2026, 10, 15, 23, 59, 0));
        Assert.Equal(a.ToJson(), b.ToJson());
        var days = Enumerable.Range(0, 30).Select(i => DailyChallenge.For(db, new DateTime(2026, 10, 1).AddDays(i)).ToJson()).ToList();
        Assert.True(days.Distinct().Count() >= 28);
    }

    [Fact]
    public void ChallengeIsPlayableAndLegal()
    {
        foreach (var pack in new[] { "original", "fan_asoul_vr" })
        {
            var db = BattleTests.LoadPack(pack);
            for (int i = 0; i < 20; i++)
            {
                var c = DailyChallenge.For(db, new DateTime(2026, 1, 1).AddDays(i));
                Assert.Equal(6, c.PlayerTeam.Count);
                Assert.Equal(6, c.EnemyTeam.Count);
                Assert.Empty(TeamCheck.Validate(db, null, c.PlayerTeam));
                Assert.InRange(c.Modifiers.Count, 1, 2);
                Assert.All(c.Modifiers, m => Assert.Contains(BattleModifiers.All, x => x.Id == m));
                Assert.False(string.IsNullOrEmpty(c.Title));
                var b = c.Start();
                Assert.Equal(c.PlayerTeam[0], b.Active(0).Def.Id);
            }
        }
    }
}
