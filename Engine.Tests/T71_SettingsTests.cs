using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T71_SettingsTests
{
    [Theory]
    [InlineData(0.5, 1, 1280, 720)]
    [InlineData(1, 10, 1600, 900)]
    [InlineData(2, 300, 1920, 1080)]
    public void DisplayAndPlaybackPreferencesPersistAlongsideExistingSettings(double speed, int seconds, int width, int height)
    {
        var settings = new GameSettings
        {
            AnimationSpeed = speed, TurnSeconds = seconds, Width = width, Height = height,
            Fullscreen = true, TurnTimer = true, Animations = false, FastPlay = speed == 2,
            AiLevel = AiLevel.Expert, DefaultPack = "original_wave2"
        };
        var directory = Path.Combine(AppContext.BaseDirectory, "T71-settings", Guid.NewGuid().ToString("N"));
        try
        {
            new FileSaveStore(directory).SaveSettings(settings);
            var actual = new FileSaveStore(directory).LoadSettings();
            Assert.Equal(speed, actual.AnimationSpeed);
            Assert.Equal(seconds, actual.TurnSeconds);
            Assert.Equal(width, actual.Width);
            Assert.Equal(height, actual.Height);
            Assert.True(actual.Fullscreen);
            Assert.True(actual.TurnTimer);
            Assert.False(actual.Animations);
            Assert.Equal(settings.FastPlay, actual.FastPlay);
            Assert.Equal(AiLevel.Expert, actual.AiLevel);
            Assert.Equal("original_wave2", actual.DefaultPack);
            Assert.Equal(settings.ToJson(), actual.ToJson());
            var fields = Assert.IsType<Dictionary<string, object>>(MiniJson.Parse(actual.ToJson()));
            Assert.Equal(fields.Keys.OrderBy(key => key, StringComparer.Ordinal), fields.Keys);
        }
        finally
        {
            string full = Path.GetFullPath(directory);
            string parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "T71-settings"));
            if (Path.GetDirectoryName(full) != parent) throw new InvalidOperationException("只能清理当前验收目录。");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }

    [Theory]
    [InlineData("{\"animationSpeed\":0,\"turnSeconds\":0,\"width\":0,\"height\":0}")]
    [InlineData("{\"animationSpeed\":1.5,\"turnSeconds\":301,\"width\":7681,\"height\":4321}")]
    [InlineData("{\"animationSpeed\":\"2\",\"turnSeconds\":\"30\",\"width\":true,\"height\":null,\"fullscreen\":1}")]
    [InlineData("{\"animationSpeed\":-1,\"turnSeconds\":1.5,\"width\":1280.5,\"height\":720.5}")]
    public void InvalidNewPreferencesDoNotReplaceSafeDefaults(string json)
    {
        var actual = GameSettings.FromJson(json);
        Assert.Equal(1, actual.AnimationSpeed);
        Assert.Equal(10, actual.TurnSeconds);
        Assert.Equal(1600, actual.Width);
        Assert.Equal(900, actual.Height);
        Assert.False(actual.Fullscreen);
    }

    [Fact]
    public void OldFastPlayAndSkipPreferencesRetainTheirMeaningWithoutNewRequiredFields()
    {
        var actual = GameSettings.FromJson("{\"fastPlay\":true,\"animations\":false,\"turnTimer\":true}");
        Assert.True(actual.FastPlay);
        Assert.False(actual.Animations);
        Assert.True(actual.TurnTimer);
        Assert.Equal(1, actual.AnimationSpeed);
        Assert.Equal(10, actual.TurnSeconds);
        Assert.DoesNotContain("animationSpeed", actual.ToJson());
        Assert.DoesNotContain("width", actual.ToJson());
    }

    [Fact]
    public void NewNumericFieldsUseInvariantJsonAndInvalidInMemoryValuesCannotWriteNonfiniteNumbers()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var valid = new GameSettings { AnimationSpeed = 0.5, TurnSeconds = 45 };
            Assert.Contains("\"animationSpeed\":0.5", valid.ToJson());
            Assert.Equal(0.5, GameSettings.FromJson(valid.ToJson()).AnimationSpeed);
            foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1 })
            {
                var bad = new GameSettings { AnimationSpeed = invalid, TurnSeconds = -1, Width = int.MaxValue, Height = -1 };
                Assert.Equal(new GameSettings().ToJson(), bad.ToJson());
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
