using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T85_AiRegressionTests
{
    static readonly Lazy<AiLadderResult> SmallSample = new Lazy<AiLadderResult>(
        () => AiRegression.Run(BattleTests.LoadPack("original"), 20, 1));

    static readonly AiLevel[] Levels = { AiLevel.Easy, AiLevel.Normal, AiLevel.Hard, AiLevel.Expert };

    static JsonObject BaselineJson(int higherWins = 60, int draws = 0, double allowedDrop = 0.05)
    {
        var matchups = new JsonArray();
        for (int lower = 0; lower < Levels.Length; lower++)
        {
            for (int higher = lower + 1; higher < Levels.Length; higher++)
            {
                matchups.Add(new JsonObject
                {
                    ["lower"] = Levels[lower].ToString(),
                    ["higher"] = Levels[higher].ToString(),
                    ["lowerWins"] = 100 - higherWins - draws,
                    ["higherWins"] = higherWins,
                    ["draws"] = draws,
                    ["truncated"] = 0
                });
            }
        }
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["samplingVersion"] = 1,
            ["packId"] = "original",
            ["contentHash"] = "0123456789abcdef",
            ["gamesPerPair"] = 100,
            ["seed"] = 1,
            ["maxTurns"] = 120,
            ["allowedDrop"] = allowedDrop,
            ["matchups"] = matchups
        };
    }

    static AiRegressionBaseline Parse(JsonObject json)
    {
        return AiRegressionBaseline.FromJson(json.ToJsonString());
    }

    [Fact]
    public void TwentyGamesPerPairPreserveDifficultyOrderWithExpertHardTieAllowance()
    {
        var result = SmallSample.Value;
        Assert.Equal("original", result.PackId);
        Assert.Equal(20, result.GamesPerPair);
        Assert.Equal(1u, result.Seed);
        Assert.Equal(120, result.MaxTurns);
        Assert.Equal(6, result.Matchups.Count);
        foreach (var matchup in result.Matchups)
        {
            Assert.Equal(20, matchup.Games);
            Assert.Equal(matchup.Games, matchup.LowerWins + matchup.HigherWins + matchup.Draws);
            Assert.Equal(0, matchup.Truncated);
            string context = matchup.Higher + " 对 " + matchup.Lower + "：胜 " + matchup.HigherWins
                + "，负 " + matchup.LowerWins + "，平 " + matchup.Draws;
            if (matchup.Lower == AiLevel.Hard && matchup.Higher == AiLevel.Expert)
                Assert.True(matchup.HigherWinRate >= 0.5, context);
            else
                Assert.True(matchup.HigherWinRate > 0.5, context);
        }
    }

    [Fact]
    public void SameParametersAndEquivalentContentOrderProduceIdenticalResultsWithoutMutatingContent()
    {
        var db = BattleTests.LoadPack("original");
        db.Streamers.Reverse();
        string beforeHash = db.ContentHash();
        string[] beforeOrder = db.Streamers.Select(streamer => streamer.Id).ToArray();
        var repeated = AiRegression.Run(db, 20, 1);
        Assert.Equal(AiRegressionBaseline.FromResult(SmallSample.Value).ToJson(),
            AiRegressionBaseline.FromResult(repeated).ToJson());
        Assert.Equal(beforeHash, db.ContentHash());
        Assert.Equal(beforeOrder, db.Streamers.Select(streamer => streamer.Id).ToArray());
    }

    [Fact]
    public void MatrixUsesAllGamesIncludingDrawsAndBothDirections()
    {
        var result = Parse(BaselineJson(60, 10)).Result;
        foreach (var level in Levels) Assert.Equal(0.5, result.WinRate(level, level));
        foreach (var matchup in result.Matchups)
        {
            Assert.Equal(0.6, matchup.HigherWinRate);
            Assert.Equal(0.6, result.WinRate(matchup.Higher, matchup.Lower));
            Assert.Equal(0.3, result.WinRate(matchup.Lower, matchup.Higher));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => result.WinRate((AiLevel)99, AiLevel.Easy));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.WinRate(AiLevel.Easy, (AiLevel)99));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void GamesMustBePositiveAndEven(int games)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AiRegression.Run(BattleTests.LoadPack("original"), games, 1));
    }

    [Fact]
    public void NullContentIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => AiRegression.Run(null, 2, 1));
    }

    [Fact]
    public void CheckedInBaselineContainsOriginalHundredGameResultsAndRoundTrips()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ai-baseline.json"));
        var baseline = AiRegressionBaseline.FromJson(json);
        var result = baseline.Result;
        Assert.Equal("original", result.PackId);
        Assert.Matches("^[0-9a-f]{16}$", result.ContentHash);
        Assert.Equal(100, result.GamesPerPair);
        Assert.Equal(1u, result.Seed);
        Assert.Equal(120, result.MaxTurns);
        Assert.Equal(1, result.SamplingVersion);
        Assert.Equal(0.05, baseline.AllowedDrop);
        Assert.Equal(6, result.Matchups.Count);
        Assert.All(result.Matchups, matchup =>
        {
            Assert.Equal(100, matchup.Games);
            Assert.Equal(0, matchup.Truncated);
        });
        Assert.Equal(baseline.ToJson(), AiRegressionBaseline.FromJson(baseline.ToJson()).ToJson());
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(55, true)]
    [InlineData(54, false)]
    [InlineData(50, false)]
    public void BaselineCheckEnforcesAbsolutePercentagePointDropAndStrictHigherWinRate(int higherWins, bool passes)
    {
        var baseline = Parse(BaselineJson());
        var actual = Parse(BaselineJson(higherWins)).Result;
        var failures = baseline.Check(actual);
        if (passes) Assert.Empty(failures);
        else
        {
            foreach (var matchup in actual.Matchups)
                Assert.Contains(failures, failure => failure.Contains(matchup.Higher + " 对 " + matchup.Lower));
        }
    }

    [Fact]
    public void DrawsAreNotExcludedFromTheStrictDifficultyOrderingCheck()
    {
        var baseline = Parse(BaselineJson(60, 0, 1));
        var actual = Parse(BaselineJson(48, 50)).Result;
        Assert.Equal(6, baseline.Check(actual).Count);
    }

    [Theory]
    [InlineData("packId", "another-pack")]
    [InlineData("seed", 2)]
    [InlineData("gamesPerPair", 200)]
    [InlineData("maxTurns", 121)]
    public void DifferentComparisonMetadataCannotSilentlyPass(string field, object value)
    {
        var changed = BaselineJson();
        changed[field] = JsonSerializer.SerializeToNode(value);
        if (field == "gamesPerPair")
        {
            foreach (var matchup in changed["matchups"].AsArray())
            {
                matchup["lowerWins"] = 80;
                matchup["higherWins"] = 120;
            }
        }
        Assert.NotEmpty(Parse(BaselineJson()).Check(Parse(changed).Result));
    }

    [Fact]
    public void ContentHashIsRecordedAsProvenanceWhileChangedRulesCanBeCompared()
    {
        var changed = BaselineJson();
        changed["contentHash"] = "fedcba9876543210";
        var actual = Parse(changed).Result;
        Assert.Equal("fedcba9876543210", actual.ContentHash);
        Assert.Empty(Parse(BaselineJson()).Check(actual));
    }

    [Fact]
    public void TruncatedGamesRemainDrawsAndFailComparison()
    {
        var changed = BaselineJson(60, 1);
        changed["matchups"][0]["truncated"] = 1;
        var actual = Parse(changed).Result;
        Assert.Equal(1, actual.Matchups[0].Draws);
        Assert.Equal(1, actual.Matchups[0].Truncated);
        Assert.NotEmpty(Parse(BaselineJson()).Check(actual));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    public void MalformedOrIncompleteBaselineIsRejected(string json)
    {
        Assert.Throws<FormatException>(() => AiRegressionBaseline.FromJson(json));
    }

    public static IEnumerable<object[]> InvalidBaselineValues()
    {
        yield return new object[] { "schemaVersion", "2" };
        yield return new object[] { "samplingVersion", "2" };
        yield return new object[] { "packId", "\"\"" };
        yield return new object[] { "contentHash", "\"not-a-hash\"" };
        yield return new object[] { "gamesPerPair", "0" };
        yield return new object[] { "gamesPerPair", "3" };
        yield return new object[] { "gamesPerPair", "100.5" };
        yield return new object[] { "seed", "-1" };
        yield return new object[] { "seed", "4294967296" };
        yield return new object[] { "maxTurns", "0" };
        yield return new object[] { "allowedDrop", "-0.01" };
        yield return new object[] { "allowedDrop", "1.01" };
        yield return new object[] { "allowedDrop", "\"NaN\"" };
        yield return new object[] { "allowedDrop", "\"Infinity\"" };
        yield return new object[] { "allowedDrop", "1e309" };
        yield return new object[] { "matchups", "[]" };
    }

    [Theory]
    [MemberData(nameof(InvalidBaselineValues))]
    public void UnsupportedSchemaAndOutOfRangeValuesAreRejected(string field, string jsonValue)
    {
        var json = BaselineJson();
        json[field] = JsonNode.Parse(jsonValue);
        Assert.Throws<FormatException>(() => AiRegressionBaseline.FromJson(json.ToJsonString()));
    }

    [Theory]
    [InlineData("lower", "\"Expert\"")]
    [InlineData("higher", "\"Easy\"")]
    [InlineData("lowerWins", "-1")]
    [InlineData("higherWins", "61")]
    [InlineData("draws", "1")]
    [InlineData("truncated", "1")]
    public void InvalidMatchupLevelsCountsAndTruncationAreRejected(string field, string jsonValue)
    {
        var json = BaselineJson();
        json["matchups"][0][field] = JsonNode.Parse(jsonValue);
        Assert.Throws<FormatException>(() => AiRegressionBaseline.FromJson(json.ToJsonString()));
    }

    [Fact]
    public void DuplicateMatchupsCannotReplaceMissingPairs()
    {
        var json = BaselineJson();
        var matchups = json["matchups"].AsArray();
        matchups[1] = matchups[0].DeepClone();
        Assert.Throws<FormatException>(() => AiRegressionBaseline.FromJson(json.ToJsonString()));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void BaselineCreationRejectsNonFiniteOrOutOfRangeDrop(double allowedDrop)
    {
        var result = Parse(BaselineJson()).Result;
        Assert.Throws<ArgumentOutOfRangeException>(() => AiRegressionBaseline.FromResult(result, allowedDrop));
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void BaselineSerializationIsCultureIndependentAndEscapesText(string culture)
    {
        var json = BaselineJson();
        json["packId"] = "测试\"包\\名\n";
        var baseline = Parse(json);
        string expected = baseline.ToJson();
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            string actual = baseline.ToJson();
            Assert.Equal(expected, actual);
            Assert.Equal(baseline.Result.PackId, AiRegressionBaseline.FromJson(actual).Result.PackId);
            Assert.Equal(0.05, AiRegressionBaseline.FromJson(actual).AllowedDrop);
            using var document = JsonDocument.Parse(actual);
            Assert.Equal(0.05, document.RootElement.GetProperty("allowedDrop").GetDouble());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
