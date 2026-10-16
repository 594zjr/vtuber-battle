using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using StreamDuel.Balance;
using Xunit;

public class T130_CountReviewTests
{
    [Theory]
    [InlineData(1L, 2L, 2L, 0L)]
    [InlineData(1L, 2L, 0L, 0L)]
    [InlineData(1L, 2L, 1L, 1L)]
    [InlineData(2L, 4L, 1L, 3L)]
    [InlineData(long.MaxValue - 1, long.MaxValue, long.MaxValue, 0L)]
    [InlineData(long.MaxValue - 1, long.MaxValue, 0L, 0L)]
    [InlineData(long.MaxValue / 2 + 1, long.MaxValue, 2L, long.MaxValue - 2)]
    public void ImpossibleResultsAreRejectedEvenWhenAppearancesAndWilsonValuesAreValid(
        long battles, long games, long wins, long draws)
    {
        var snapshot = Snapshot(games, games, wins, draws);
        string validJson = snapshot.ToJson();
        Assert.Equal(validJson, BalanceSnapshot.FromJson(validJson).ToJson());

        // 只减少实际对局数，先证明胜率、区间和其余字段本身有效。
        snapshot.Packs[0].Battles = battles;
        Assert.True(games <= battles || games - battles <= battles);
        var input = JsonNode.Parse(validJson);
        input["packs"][0]["battles"] = battles;
        string impossibleJson = input.ToJsonString();

        var error = Assert.Throws<FormatException>(() => snapshot.Validate());
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
        Assert.Throws<FormatException>(() => BalanceSnapshot.FromJson(impossibleJson));
        Assert.Throws<FormatException>(() => snapshot.ToJson());
    }

    [Theory]
    [InlineData(1L, 2L, 1L, 0L)]
    [InlineData(1L, 2L, 0L, 2L)]
    [InlineData(1L, 1L, 0L, 1L)]
    [InlineData(2L, 3L, 1L, 1L)]
    [InlineData(3L, 4L, 1L, 3L)]
    [InlineData(long.MaxValue, long.MaxValue, long.MaxValue, 0L)]
    [InlineData(long.MaxValue, long.MaxValue, 0L, 0L)]
    [InlineData(long.MaxValue / 2 + 1, long.MaxValue, 0L, long.MaxValue)]
    [InlineData(long.MaxValue / 2 + 1, long.MaxValue, long.MaxValue / 2, 0L)]
    public void RealizableMirrorAndOddDrawCountsRoundTripAtLongBoundaries(
        long battles, long games, long wins, long draws)
    {
        var snapshot = Snapshot(battles, games, wins, draws);
        snapshot.Validate();
        string json = snapshot.ToJson();
        var restored = BalanceSnapshot.FromJson(json);
        Assert.Equal(json, restored.ToJson());
        var pack = Assert.Single(restored.Packs);
        var streamer = Assert.Single(pack.Streamers);
        Assert.Equal(battles, pack.Battles);
        Assert.Equal(games, streamer.Games);
        Assert.Equal(wins, streamer.Wins);
        Assert.Equal(draws, streamer.Draws);
        Assert.Equal((wins + draws / 2.0) / games, streamer.WinRate);
        var comparison = BalanceCompare.Compare(snapshot, restored);
        Assert.False(comparison.ConditionsDiffer);
        Assert.False(comparison.HasSignificantChanges);
        Assert.Equal(0.0, Assert.Single(comparison.Rows).Delta.Value);
    }

    static BalanceSnapshot Snapshot(long battles, long games, long wins, long draws)
    {
        const double z = 1.959963984540054;
        double n = games;
        double rate = (wins + draws / 2.0) / n;
        double denominator = 1 + z * z / n;
        double middle = (rate + z * z / (2 * n)) / denominator;
        double margin = z * Math.Sqrt(rate * (1 - rate) / n + z * z / (4 * n * n)) / denominator;
        return new BalanceSnapshot
        {
            GamesPerStreamer = 1,
            Seed = 130,
            Level = "easy",
            AiVersion = "count-review",
            Packs = new List<BalancePackSnapshot>
            {
                new BalancePackSnapshot
                {
                    PackId = "count-review",
                    ContentHash = "count-review-content",
                    Battles = battles,
                    Streamers = new List<BalanceStreamerSnapshot>
                    {
                        new BalanceStreamerSnapshot
                        {
                            Id = "count-review-streamer",
                            Name = "计数审查主播",
                            Games = games,
                            Wins = wins,
                            Draws = draws,
                            WinRate = rate,
                            Low95 = rate == 0 ? 0 : Math.Max(0, middle - margin),
                            High95 = rate == 1 ? 1 : Math.Min(1, middle + margin)
                        }
                    }
                }
            }
        };
    }
}
