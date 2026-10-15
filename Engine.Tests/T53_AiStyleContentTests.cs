using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T53_AiStyleContentTests
{
    static string Resource(string relative)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", relative));
    }

    static JsonObject Pack(string name = "original")
    {
        return JsonNode.Parse(Resource(Path.Combine("Packs", name + ".json"))).AsObject();
    }

    static JsonObject Streamer(JsonObject pack, string id)
    {
        return pack["streamers"].AsArray().OfType<JsonObject>().Single(s => s["id"].GetValue<string>() == id);
    }

    static ContentDb Load(JsonObject pack)
    {
        return ContentDb.FromJson(Resource("StreamDuelRules.json"), pack.ToJsonString());
    }

    [Theory]
    [InlineData("aggressive")]
    [InlineData("defensive")]
    [InlineData("tricky")]
    public void OptionalAiStyleLoadsForOnlyTheNamedStreamer(string style)
    {
        var pack = Pack();
        Streamer(pack, "bell")["aiStyle"] = style;

        var db = Load(pack);

        Assert.Equal(style, db.Streamer("bell").AiStyle);
        Assert.All(db.Streamers.Where(s => s.Id != "bell"), s => Assert.Null(s.AiStyle));
        db.Validate();
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("0.5")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void JsonRejectsNonStringStylesWithTheStreamerAndField(string value)
    {
        var pack = Pack();
        Streamer(pack, "bell")["aiStyle"] = JsonNode.Parse(value);

        var error = Assert.Throws<KeyError>(() => Load(pack));

        Assert.Contains("bell", error.Message);
        Assert.Contains("aiStyle", error.Message);
        Assert.Contains("字符串", error.Message);
    }

    [Theory]
    [InlineData("no_such_style")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Aggressive")]
    [InlineData("DEFENSIVE")]
    [InlineData("Tricky")]
    [InlineData("aggressive ")]
    public void UnknownAndNonExactStylesFailJsonLoadingAndDirectValidation(string style)
    {
        var pack = Pack();
        Streamer(pack, "bell")["aiStyle"] = style;
        var readError = Assert.Throws<KeyError>(() => Load(pack));
        Assert.Contains("bell", readError.Message);
        Assert.Contains("aiStyle", readError.Message);
        Assert.Contains(style, readError.Message);

        var db = BattleTests.LoadPack("original");
        db.Streamer("bell").AiStyle = style;
        var validateError = Assert.Throws<KeyError>(() => db.Validate());
        Assert.Equal(readError.Message, validateError.Message);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("original_wave2")]
    [InlineData("fan_asoul_vr")]
    [InlineData("fan_holo_niji")]
    public void ExistingPacksKeepNoStyleAndExplicitNullHasTheSameFingerprint(string name)
    {
        var old = BattleTests.LoadPack(name);
        Assert.All(old.Streamers, s => Assert.Null(s.AiStyle));
        var pack = Pack(name);
        foreach (var streamer in pack["streamers"].AsArray()) streamer["aiStyle"] = null;

        var explicitNull = Load(pack);

        Assert.All(explicitNull.Streamers, s => Assert.Null(s.AiStyle));
        Assert.Equal(old.ContentHash(), explicitNull.ContentHash());
        explicitNull.Validate();
    }

    [Fact]
    public void FingerprintTracksStyleAndItsOwnerAndReturnsToLegacyValueWhenCleared()
    {
        var db = BattleTests.LoadPack("original");
        string legacy = db.ContentHash();
        db.Streamer("bell").AiStyle = "aggressive";
        string aggressive = db.ContentHash();
        Assert.NotEqual(legacy, aggressive);

        db.Streamer("bell").AiStyle = "defensive";
        Assert.NotEqual(aggressive, db.ContentHash());
        db.Streamer("bell").AiStyle = null;
        db.Streamer("kei").AiStyle = "aggressive";
        Assert.NotEqual(aggressive, db.ContentHash());

        db.Streamer("kei").AiStyle = null;
        Assert.Equal(legacy, db.ContentHash());
    }

    [Fact]
    public void StyledFingerprintIsStableAcrossRosterOrderAndCultureWithoutReorderingTheRoster()
    {
        var db = BattleTests.LoadPack("original");
        var streamers = new[] { db.Streamer("hane"), db.Streamer("lumi"), db.Streamer("momo") };
        var ids = new[] { "I", "ı", "ä" };
        var styles = new[] { "aggressive", "defensive", "tricky" };
        for (int i = 0; i < streamers.Length; i++)
        {
            streamers[i].Id = ids[i];
            streamers[i].AiStyle = styles[i];
        }
        db.Validate();
        string expected = db.ContentHash();
        db.Streamers.Reverse();
        string[] order = db.Streamers.Select(s => s.Id).ToArray();
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "tr-TR", "zh-CN", "en-US" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Assert.Equal(expected, db.ContentHash());
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
        Assert.Equal(order, db.Streamers.Select(s => s.Id));
    }

    [Fact]
    public void ReplayFingerprintRejectsStyleChangesBeforeReplayingActions()
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        var battle = new Battle(db, new System.Collections.Generic.IList<string>[]
        {
            ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray()
        }, new[] { "甲", "乙" }, 53);
        db.Streamer("bell").AiStyle = "aggressive";

        var error = Assert.Throws<InvalidOperationException>(() => Replay.Run(db, battle.Record));

        Assert.Contains("内容不一致", error.Message);
    }
}
