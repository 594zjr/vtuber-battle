// T-57 验收测试：排位分与段位规则。实现后把本文件移到 Engine.Tests/ 下。
using StreamDuel.Core;
using Xunit;

public class T57_RatingTests
{
    [Fact]
    public void EloIsZeroSumAndFavoursUpsets()
    {
        var a = new PlayerRating { Rating = 1500 }; var b = new PlayerRating { Rating = 1500 };
        RatingSystem.Apply(a, b, 1.0);
        Assert.Equal(3000, a.Rating + b.Rating);
        Assert.True(a.Rating > 1500);
        var low = new PlayerRating { Rating = 1200 }; var high = new PlayerRating { Rating = 1800 };
        int before = low.Rating;
        RatingSystem.Apply(low, high, 1.0);
        Assert.True(low.Rating - before > 16);
    }

    [Fact]
    public void PlacementGamesUseLargerK()
    {
        var a = new PlayerRating { Rating = 1500 }; var b = new PlayerRating { Rating = 1500, Games = 50 };
        RatingSystem.Apply(a, b, 1.0);
        Assert.True(a.Rating - 1500 > 1500 - b.Rating);
        Assert.Equal(1, a.Games); Assert.Equal(51, b.Games);
    }

    [Fact]
    public void TiersCoverAllRatingsInOrder()
    {
        var tiers = RatingSystem.Tiers;
        Assert.True(tiers.Count >= 5);
        Assert.Equal(RatingSystem.TierOf(0).Name, tiers[0].Name);
        Assert.Equal(RatingSystem.TierOf(5000).Name, tiers[tiers.Count - 1].Name);
        for (int i = 1; i < tiers.Count; i++) Assert.True(tiers[i].MinRating > tiers[i - 1].MinRating);
        Assert.Equal(new PlayerRating { Rating = 1600, Games = 3 }.ToJson(), PlayerRating.FromJson(new PlayerRating { Rating = 1600, Games = 3 }.ToJson()).ToJson());
    }
}
