using System;
using System.Collections;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T57_RatingEdgeTests
{
    [Theory]
    [InlineData(0, 0, 1.0, 1520, 1480)]
    [InlineData(9, 9, 0.0, 1480, 1520)]
    [InlineData(10, 10, 1.0, 1512, 1488)]
    [InlineData(9, 10, 1.0, 1520, 1488)]
    [InlineData(10, 9, 0.0, 1488, 1520)]
    [InlineData(9, 10, 0.5, 1500, 1500)]
    public void EachPlayersPreMatchGamesSelectsTheirOwnK(int gamesA, int gamesB, double score, int ratingA, int ratingB)
    {
        var a = new PlayerRating { Games = gamesA };
        var b = new PlayerRating { Games = gamesB };
        RatingSystem.Apply(a, b, score);
        Assert.Equal(ratingA, a.Rating);
        Assert.Equal(ratingB, b.Rating);
        Assert.Equal(gamesA + 1, a.Games);
        Assert.Equal(gamesB + 1, b.Games);
    }

    [Theory]
    [InlineData(0, 1.0, 1204, 796)]
    [InlineData(0, 0.0, 1164, 836)]
    [InlineData(0, 0.5, 1184, 816)]
    [InlineData(10, 1.0, 1202, 798)]
    [InlineData(10, 0.0, 1178, 822)]
    [InlineData(10, 0.5, 1190, 810)]
    public void FourHundredPointGapUsesTheStandardTenToOneExpectation(int games, double score, int ratingA, int ratingB)
    {
        var a = new PlayerRating { Rating = 1200, Games = games };
        var b = new PlayerRating { Rating = 800, Games = games };
        RatingSystem.Apply(a, b, score);
        Assert.Equal(ratingA, a.Rating);
        Assert.Equal(ratingB, b.Rating);
    }

    [Fact]
    public void EqualKRemainsZeroSumAndSwappingPlayersHasTheSameResult()
    {
        int[] ratings = { -700, 0, 1199, 1500, 1501, 1837, 4000 };
        foreach (int games in new[] { 0, 9, 10, 100 })
        foreach (int left in ratings)
        foreach (int right in ratings)
        foreach (double score in new[] { 0.0, 0.5, 1.0 })
        {
            var a = new PlayerRating { Rating = left, Games = games };
            var b = new PlayerRating { Rating = right, Games = games };
            var reversedA = new PlayerRating { Rating = right, Games = games };
            var reversedB = new PlayerRating { Rating = left, Games = games };
            RatingSystem.Apply(a, b, score);
            RatingSystem.Apply(reversedA, reversedB, 1 - score);
            Assert.Equal(left + right, a.Rating + b.Rating);
            Assert.Equal(a.ToJson(), reversedB.ToJson());
            Assert.Equal(b.ToJson(), reversedA.ToJson());
        }
    }

    [Fact]
    public void ExtremeRatingsDoNotOverflowTheExpectationDifferenceOrClampNegativeScores()
    {
        var low = new PlayerRating { Rating = int.MinValue };
        var high = new PlayerRating { Rating = int.MaxValue };
        RatingSystem.Apply(low, high, 1);
        Assert.Equal(int.MinValue + 40, low.Rating);
        Assert.Equal(int.MaxValue - 40, high.Rating);
        Assert.Equal(1, low.Games);
        Assert.Equal(1, high.Games);

        var a = new PlayerRating { Rating = 0 };
        var b = new PlayerRating { Rating = 0 };
        RatingSystem.Apply(a, b, 0);
        Assert.Equal(-20, a.Rating);
        Assert.Equal(20, b.Rating);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(0.25)]
    [InlineData(0.75)]
    [InlineData(2.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidScoresRejectWithoutChangingEitherPlayer(double score)
    {
        var a = new PlayerRating { Rating = 1600, Games = 9 };
        var b = new PlayerRating { Rating = 1400, Games = 10 };
        int[] before = Snapshot(a, b);
        Assert.Throws<ArgumentOutOfRangeException>(() => RatingSystem.Apply(a, b, score));
        Assert.Equal(before, Snapshot(a, b));
    }

    [Fact]
    public void NullAliasedAndNegativeGameInputsRejectAtomically()
    {
        var a = new PlayerRating();
        var b = new PlayerRating();
        int[] before = Snapshot(a, b);
        Assert.Throws<ArgumentNullException>(() => RatingSystem.Apply(null, b, 1));
        Assert.Throws<ArgumentNullException>(() => RatingSystem.Apply(a, null, 1));
        Assert.Throws<ArgumentException>(() => RatingSystem.Apply(a, a, 1));
        Assert.Equal(before, Snapshot(a, b));
        a.Games = -1;
        before = Snapshot(a, b);
        Assert.Throws<ArgumentOutOfRangeException>(() => RatingSystem.Apply(a, b, 1));
        Assert.Equal(before, Snapshot(a, b));
        Assert.Throws<ArgumentOutOfRangeException>(() => RatingSystem.Apply(b, a, 1));
        Assert.Equal(before, Snapshot(a, b));
    }

    [Theory]
    [InlineData(int.MaxValue, 0, int.MaxValue, 0)]
    [InlineData(int.MinValue, 0, int.MinValue, 0)]
    [InlineData(1500, int.MaxValue, 1500, 9)]
    [InlineData(1500, 9, 1500, int.MaxValue)]
    public void OverflowInAnyUpdatedFieldLeavesAllFourFieldsUnchanged(int ratingA, int gamesA, int ratingB, int gamesB)
    {
        var a = new PlayerRating { Rating = ratingA, Games = gamesA };
        var b = new PlayerRating { Rating = ratingB, Games = gamesB };
        int[] before = Snapshot(a, b);
        Assert.Throws<OverflowException>(() => RatingSystem.Apply(a, b, 1));
        Assert.Equal(before, Snapshot(a, b));
    }

    [Fact]
    public void TiersCoverEachBoundaryAndCannotBeReordered()
    {
        var tiers = RatingSystem.Tiers;
        Assert.True(tiers.Count >= 5);
        Assert.Equal(0, tiers[0].MinRating);
        Assert.Equal(tiers[0], RatingSystem.TierOf(int.MinValue));
        Assert.Equal(tiers[tiers.Count - 1], RatingSystem.TierOf(int.MaxValue));
        Assert.Equal(tiers.Count, tiers.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count());
        for (int i = 0; i < tiers.Count; i++)
        {
            Assert.Matches("[\u4e00-\u9fff]", tiers[i].Name);
            Assert.Equal(tiers[i], RatingSystem.TierOf(tiers[i].MinRating));
            if (i > 0) Assert.Equal(tiers[i - 1], RatingSystem.TierOf(tiers[i].MinRating - 1));
        }
        var list = Assert.IsAssignableFrom<IList>(tiers);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }

    [Theory]
    [InlineData("{}", 1500, 0)]
    [InlineData("{\"rating\":-2}", -2, 0)]
    [InlineData("{\"games\":8}", 1500, 8)]
    [InlineData("{\"rating\":-2147483648,\"games\":2147483647}", int.MinValue, int.MaxValue)]
    [InlineData("{\"rating\":2147483647,\"games\":0}", int.MaxValue, 0)]
    public void RatingJsonPreservesSignedBoundsAndDefaultsOnlyMissingFields(string json, int rating, int games)
    {
        var player = PlayerRating.FromJson(json);
        Assert.Equal(rating, player.Rating);
        Assert.Equal(games, player.Games);
        Assert.Equal(player.ToJson(), PlayerRating.FromJson(player.ToJson()).ToJson());
        Assert.Equal("{\"games\":0,\"rating\":1500}", new PlayerRating().ToJson());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"rating\":null}")]
    [InlineData("{\"rating\":\"1500\"}")]
    [InlineData("{\"rating\":1.5}")]
    [InlineData("{\"rating\":2147483648}")]
    [InlineData("{\"rating\":-2147483649}")]
    [InlineData("{\"games\":-1}")]
    [InlineData("{\"games\":0.5}")]
    [InlineData("{\"games\":2147483648}")]
    [InlineData("{\"games\":true}")]
    [InlineData("{\"games\":0}{}")]
    public void InvalidRatingJsonProducesChineseFormatErrors(string json)
    {
        var error = Assert.Throws<FormatException>(() => PlayerRating.FromJson(json));
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
    }

    static int[] Snapshot(PlayerRating a, PlayerRating b)
    {
        return new[] { a.Rating, a.Games, b.Rating, b.Games };
    }
}
