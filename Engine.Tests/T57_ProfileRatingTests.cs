using System;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T57_ProfileRatingTests
{
    const string Legacy = "{\"packs\":{\"original\":{\"draws\":1,\"losses\":2,\"pickWins\":{\"bell\":3},\"picks\":{\"bell\":6},\"wins\":3}}}";

    [Fact]
    public void ProfilesWithoutRatingsKeepTheirOriginalJsonExactly()
    {
        var old = Profile.FromJson(Legacy);
        Assert.Empty(old.Ratings);
        Assert.Equal(Legacy, old.ToJson());
        Assert.Equal("{\"packs\":{}}", new Profile().ToJson());
        Assert.Equal("{\"packs\":{}}", Profile.FromJson("{\"packs\":{},\"ratings\":{}}").ToJson());
        Assert.Equal("默认", Profile.DefaultRatingFormat);
    }

    [Fact]
    public void FormatRatingsAreOrdinalIndependentAndHaveDeterministicSerialization()
    {
        string[] formats = { "默认", "ranked", "Ranked", "赛制\"\\\n🚀" };
        var profile = Profile.FromJson(Legacy);
        var reverse = Profile.FromJson(Legacy);
        for (int i = 0; i < formats.Length; i++)
            profile.Ratings.Add(formats[i], new PlayerRating { Rating = -50 + i * 400, Games = i });
        for (int i = formats.Length - 1; i >= 0; i--)
            reverse.Ratings.Add(formats[i], new PlayerRating { Rating = -50 + i * 400, Games = i });
        Assert.Equal(profile.ToJson(), reverse.ToJson());
        var back = Profile.FromJson(profile.ToJson());
        Assert.Equal(profile.ToJson(), back.ToJson());
        Assert.Equal(formats.OrderBy(s => s, StringComparer.Ordinal), back.Ratings.Keys.OrderBy(s => s, StringComparer.Ordinal));
        Assert.Equal(350, back.Ratings["ranked"].Rating);
        Assert.Equal(750, back.Ratings["Ranked"].Rating);
        Assert.Equal(3, back.Packs["original"].Wins);
        back.Ratings["ranked"].Rating++;
        Assert.Equal(350, profile.Ratings["ranked"].Rating);
        Assert.Equal(750, back.Ratings["Ranked"].Rating);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("{\"默认\":null}")]
    [InlineData("{\"默认\":{\"games\":-1,\"rating\":1500}}")]
    [InlineData("{\"默认\":{\"games\":1,\"rating\":1500.5}}")]
    [InlineData("{\"正常\":{\"games\":1,\"rating\":1500},\"坏的\":{\"games\":\"2\"}}")]
    public void MalformedRatingsResetTheEntireProfileInsteadOfKeepingPartialStats(string ratings)
    {
        string json = Legacy.Substring(0, Legacy.Length - 1) + ",\"ratings\":" + ratings + "}";
        var profile = Profile.FromJson(json);
        Assert.Empty(profile.Packs);
        Assert.Empty(profile.Ratings);
        Assert.Equal("{\"packs\":{}}", profile.ToJson());
    }

    [Fact]
    public void SeparateProfilesDoNotShareDefaultRatingStorage()
    {
        var a = new Profile();
        var b = new Profile();
        a.Ratings[Profile.DefaultRatingFormat] = new PlayerRating();
        Assert.Empty(b.Ratings);
        var c = Profile.FromJson("{\"packs\":{},\"ratings\":{\"默认\":{}}}");
        Assert.Equal(1500, c.Ratings[Profile.DefaultRatingFormat].Rating);
        Assert.Equal(0, c.Ratings[Profile.DefaultRatingFormat].Games);
    }
}
