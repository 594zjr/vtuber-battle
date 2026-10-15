using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T43_GameSettingsEdgeTests
{
    [Fact]
    public void NewSettingsUseTheBattleDefaultsAndEmitSortedKeys()
    {
        var settings = new GameSettings();

        Assert.False(settings.FastPlay);
        Assert.False(settings.TurnTimer);
        Assert.True(settings.Animations);
        Assert.Equal(AiLevel.Normal, settings.AiLevel);
        Assert.Equal("fan_asoul_vr", settings.DefaultPack);
        Assert.Equal("zh-CN", settings.Language);
        Assert.Equal(GameSettings.CurrentVersion, settings.Version);
        var parsed = Assert.IsType<Dictionary<string, object>>(MiniJson.Parse(settings.ToJson()));
        Assert.Equal(new[] { "aiLevel", "animations", "defaultPack", "fastPlay", "language", "turnTimer", "version" }, parsed.Keys);
        Assert.Equal(parsed.Keys.OrderBy(key => key, StringComparer.Ordinal), parsed.Keys);
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void AllAiLevelsAndBattleOptionsRoundTripWithEscapedUnicode(AiLevel level)
    {
        var settings = new GameSettings
        {
            FastPlay = true, TurnTimer = true, Animations = false, AiLevel = level,
            DefaultPack = "自定义\"包\\路径/\n\t\r\b\f\0\u0001\ud83d\ude80", Language = "zh-\ud800-CN"
        };
        string json = settings.ToJson();

        var restored = GameSettings.FromJson(json);

        Assert.Equal(json, restored.ToJson());
        Assert.Equal(settings.DefaultPack, restored.DefaultPack);
        Assert.Equal(settings.Language, restored.Language);
        Assert.Equal(level, restored.AiLevel);
        Assert.DoesNotContain(json, c => c < ' ');
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    [InlineData("不是JSON")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("{\"fastPlay\":true")]
    [InlineData("{\"fastPlay\":true,}")]
    [InlineData("{\"fastPlay\":true} trailing")]
    [InlineData("{\"fastPlay\":true,\"language\":\"\\q\"}")]
    [InlineData("{\"fastPlay\":true,\"language\":\"bad\ntext\"}")]
    [InlineData("{\"fastPlay\":true,\"version\":01}")]
    [InlineData("{\"fastPlay\":true,\"version\":1e999}")]
    [InlineData("{\"fastPlay\":true,\"version\":+1}")]
    public void BrokenOrNonObjectJsonReturnsAllDefaults(string json)
    {
        Assert.Equal(new GameSettings().ToJson(), GameSettings.FromJson(json).ToJson());
    }

    [Fact]
    public void ExcessiveUnknownFieldNestingReturnsDefaultsInsteadOfOverflowingTheStack()
    {
        string json = "{\"fastPlay\":true,\"unknown\":" + new string('[', 100) + "0" + new string(']', 100) + "}";

        Assert.Equal(new GameSettings().ToJson(), GameSettings.FromJson(json).ToJson());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":0}")]
    [InlineData("{\"version\":99}")]
    public void AbsentOldOrUnknownVersionsBecomeTheCurrentVersion(string json)
    {
        var settings = GameSettings.FromJson(json);

        Assert.Equal(GameSettings.CurrentVersion, settings.Version);
        Assert.Equal(new GameSettings().ToJson(), settings.ToJson());
    }

    [Fact]
    public void MissingAndWronglyTypedFieldsDefaultIndividuallyWithoutDiscardingValidOptions()
    {
        var settings = GameSettings.FromJson("{\"fastPlay\":true,\"turnTimer\":\"true\",\"animations\":null," +
            "\"defaultPack\":12,\"language\":\"\",\"aiLevel\":99,\"unknown\":{\"enabled\":true}}");

        Assert.True(settings.FastPlay);
        Assert.False(settings.TurnTimer);
        Assert.True(settings.Animations);
        Assert.Equal("fan_asoul_vr", settings.DefaultPack);
        Assert.Equal("zh-CN", settings.Language);
        Assert.Equal(AiLevel.Normal, settings.AiLevel);
        Assert.Equal(GameSettings.CurrentVersion, settings.Version);
    }

    [Theory]
    [InlineData("\"hard\"", AiLevel.Hard)]
    [InlineData("3", AiLevel.Expert)]
    [InlineData("-1", AiLevel.Normal)]
    [InlineData("1.5", AiLevel.Normal)]
    [InlineData("\"not-a-level\"", AiLevel.Normal)]
    [InlineData("\"Easy, Hard\"", AiLevel.Normal)]
    [InlineData("\"3\"", AiLevel.Normal)]
    [InlineData("false", AiLevel.Normal)]
    public void AiLevelAcceptsKnownNamesAndIntegersButRejectsUnknownValues(string value, AiLevel expected)
    {
        var settings = GameSettings.FromJson("{\"turnTimer\":true,\"aiLevel\":" + value + "}");

        Assert.Equal(expected, settings.AiLevel);
        Assert.True(settings.TurnTimer);
    }

    [Fact]
    public void KeyOrderAndSerializationAreStableAcrossInputOrderAndCurrentCulture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var left = GameSettings.FromJson("{\"version\":0,\"turnTimer\":true,\"fastPlay\":true,\"aiLevel\":\"Hard\"}");
            var right = GameSettings.FromJson("{\"aiLevel\":\"Hard\",\"fastPlay\":true,\"turnTimer\":true}");

            Assert.Equal(left.ToJson(), right.ToJson());
            Assert.Equal(left.ToJson(), GameSettings.FromJson(left.ToJson()).ToJson());
            Assert.EndsWith("\"version\":1}", left.ToJson());
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void DefaultResultsAreIndependentMutableInstances()
    {
        var first = GameSettings.FromJson("broken");
        first.FastPlay = true;
        first.DefaultPack = "changed";

        Assert.Equal(new GameSettings().ToJson(), GameSettings.FromJson("broken").ToJson());
    }
}
