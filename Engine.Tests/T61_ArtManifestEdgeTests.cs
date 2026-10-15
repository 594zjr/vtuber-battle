using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T61_ArtManifestEdgeTests
{
    static string Rules => Read("StreamDuelRules.json");
    static string Original => Read(Path.Combine("Packs", "original.json"));
    static string Read(string path) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", path));
    static string Quote(string text) => JsonSerializer.Serialize(text);

    static string AppendFields(string pack, string fields)
    {
        int end = pack.LastIndexOf('}');
        return pack.Substring(0, end) + "," + fields + "}";
    }

    static ContentDb WithArt(string art, string definitions = null)
    {
        string fields = "\"art\":" + art;
        if (definitions != null) fields += "," + definitions;
        return ContentDb.FromJson(Rules, AppendFields(Original, fields));
    }

    static void AssertBadArt(string art, string location)
    {
        var error = Assert.Throws<KeyError>(() => WithArt(art).Validate());
        Assert.Contains(location, error.Message);
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("original_wave2")]
    [InlineData("fan_holo_niji")]
    [InlineData("fan_asoul_vr")]
    public void ExistingPacksAndEmptyArtKeepAllDefaultPortraitsAndPrimaryColors(string packId)
    {
        string pack = Read(Path.Combine("Packs", packId + ".json"));
        var omitted = ContentDb.FromJson(Rules, pack);
        var empty = ContentDb.FromJson(Rules, AppendFields(pack, "\"art\":{}"));

        foreach (var streamer in omitted.Streamers)
        {
            string path = "Portraits/" + omitted.PackId + "/" + streamer.Id;
            Assert.Equal(path, omitted.Art.PortraitPath(streamer.Id));
            Assert.Equal(path, empty.Art.PortraitPath(streamer.Id));
            Assert.Equal(omitted.Types[streamer.Type].Color, omitted.Art.Accent(streamer.Id));
            Assert.Equal(omitted.Art.Accent(streamer.Id), empty.Art.Accent(streamer.Id));
        }
        foreach (string id in omitted.Skills.Keys)
            Assert.Equal(omitted.Art.SkillFx(id), empty.Art.SkillFx(id));
        foreach (string id in omitted.Statuses.Keys.Concat(omitted.Fields.Keys))
            Assert.Equal(omitted.Art.StatusIcon(id), empty.Art.StatusIcon(id));
    }

    [Fact]
    public void PartialOverridesRemainIndependentAndDoNotLeakToAnotherContentDb()
    {
        var db = WithArt(@"{
            ""streamers"": {
                ""bell"": { ""portrait"": ""concert/bell_v2"" },
                ""enka"": { ""accent"": ""#Aa00Ff"" },
                ""shiori"": {}
            },
            ""skillFx"": { ""deep_breath"": ""burst"" },
            ""statusIcons"": { ""stall"": ""star"", ""reconnect"": ""heart"" }
        }");
        var other = BattleTests.LoadPack("original");

        Assert.Equal("Portraits/original/concert/bell_v2", db.Art.PortraitPath("bell"));
        Assert.Equal(other.Art.Accent("bell"), db.Art.Accent("bell"));
        Assert.Equal("#Aa00Ff", db.Art.Accent("enka"));
        Assert.Equal(other.Art.PortraitPath("enka"), db.Art.PortraitPath("enka"));
        Assert.Equal(other.Art.PortraitPath("shiori"), db.Art.PortraitPath("shiori"));
        Assert.Equal("burst", db.Art.SkillFx("deep_breath"));
        Assert.Equal("star", db.Art.StatusIcon("stall"));
        Assert.Equal("heart", db.Art.StatusIcon("reconnect"));
        Assert.Equal("Portraits/original/bell", other.Art.PortraitPath("bell"));
        Assert.Equal("heal", other.Art.SkillFx("deep_breath"));
        Assert.Equal("lock", other.Art.StatusIcon("stall"));
        Assert.Equal("shield", other.Art.StatusIcon("reconnect"));
    }

    [Fact]
    public void SecondaryTrackDoesNotReplacePrimaryAccent()
    {
        string pack = Original.Replace("\"id\": \"bell\",", "\"id\": \"bell\", \"type2\": \"tech\",");
        var db = ContentDb.FromJson(Rules, pack);
        Assert.Equal("tech", db.Streamer("bell").Type2);
        Assert.NotEqual(db.Types["song"].Color, db.Types["tech"].Color);
        Assert.Equal(db.Types["song"].Color, db.Art.Accent("bell"));
    }

    [Fact]
    public void DefaultsUseEffectKindsInsteadOfNamesAndKeepDamageSkillCategories()
    {
        var db = BattleTests.LoadPack("original");
        Assert.Equal("slash", db.Art.SkillFx("eat_danmaku"));
        Assert.Equal("beam", db.Art.SkillFx("ride_trend"));
        Assert.Equal("sparkle", db.Art.SkillFx("pregame"));
        Assert.Equal("heal", db.Art.SkillFx("deep_breath"));
        Assert.Equal("heal", db.Art.SkillFx("captain_renew"));
        Assert.Equal("lock", db.Art.StatusIcon("stall"));
        Assert.Equal("down", db.Art.StatusIcon("flame"));
        Assert.Equal("shield", db.Art.StatusIcon("reconnect"));
        Assert.Equal("mark", db.Art.StatusIcon("hype"));
        Assert.Equal("clock", db.Art.StatusIcon("shield"));
        Assert.Equal("slash", db.Art.SkillFx(db.Struggle.Id));
    }

    [Fact]
    public void PackLocalEffectsAreResolvedBeforeDefaultsAndOverrides()
    {
        const string definitions = @"
            ""statuses"": { ""art_local_control"": { ""name"": ""测试控制"", ""cat"": ""control"", ""skip"": 1 } },
            ""fields"": {
                ""art_local_protect"": { ""name"": ""测试保护"", ""kind"": ""protect"", ""when"": ""被施加控制时"", ""dispel"": false, ""immuneControl"": true },
                ""art_local_regen"": { ""name"": ""测试回复"", ""kind"": ""turn"", ""when"": ""回合结束"", ""dispel"": true, ""heal"": 0.1 }
            },
            ""skills"": {
                ""art_guard"": { ""name"": ""测试保护技能"", ""type"": ""tech"", ""cat"": ""status"", ""pp"": 5, ""effects"": [ { ""at"": ""post"", ""code"": ""FIELD"", ""target"": ""self"", ""field"": ""art_local_protect"" } ] },
                ""art_regen"": { ""name"": ""测试回复技能"", ""type"": ""tech"", ""cat"": ""status"", ""pp"": 5, ""effects"": [ { ""at"": ""post"", ""code"": ""FIELD"", ""target"": ""self"", ""field"": ""art_local_regen"" } ] },
                ""art_mixed"": { ""name"": ""测试回复保护"", ""type"": ""tech"", ""cat"": ""status"", ""pp"": 5, ""effects"": [ { ""at"": ""post"", ""code"": ""FIELD"", ""target"": ""self"", ""field"": ""art_local_protect"" }, { ""at"": ""post"", ""code"": ""HEAL"", ""frac"": 0.1 } ] },
                ""art_drain"": { ""name"": ""测试吸收"", ""type"": ""tech"", ""cat"": ""status"", ""pp"": 5, ""effects"": [ { ""at"": ""post"", ""code"": ""DRAIN"", ""frac"": 0.1 } ] }
            }";
        var defaults = WithArt("{}", definitions);
        Assert.Equal("shield", defaults.Art.SkillFx("art_guard"));
        Assert.Equal("heal", defaults.Art.SkillFx("art_regen"));
        Assert.Equal("heal", defaults.Art.SkillFx("art_mixed"));
        Assert.Equal("heal", defaults.Art.SkillFx("art_drain"));
        Assert.Equal("lock", defaults.Art.StatusIcon("art_local_control"));
        Assert.Equal("shield", defaults.Art.StatusIcon("art_local_protect"));
        Assert.Equal("clock", defaults.Art.StatusIcon("art_local_regen"));

        var overridden = WithArt(@"{ ""skillFx"": { ""art_mixed"": ""ring"" }, ""statusIcons"": { ""art_local_control"": ""star"", ""art_local_protect"": ""up"" } }", definitions);
        Assert.Equal("ring", overridden.Art.SkillFx("art_mixed"));
        Assert.Equal("star", overridden.Art.StatusIcon("art_local_control"));
        Assert.Equal("up", overridden.Art.StatusIcon("art_local_protect"));
    }

    [Fact]
    public void SharedStatusAndFieldIdUsesStatusDefaultAndStillAcceptsOverride()
    {
        const string definitions = @"""fields"": { ""stall"": { ""name"": ""同名回合效果"", ""kind"": ""turn"", ""when"": ""回合结束"", ""dispel"": true } }";
        Assert.Equal("lock", WithArt("{}", definitions).Art.StatusIcon("stall"));
        Assert.Equal("heart", WithArt(@"{ ""statusIcons"": { ""stall"": ""heart"" } }", definitions).Art.StatusIcon("stall"));
    }

    [Fact]
    public void EveryDocumentedFxAndIconCanBeExplicitlySelected()
    {
        string[] effects = { "slash", "burst", "wave", "sparkle", "beam", "ring", "shield", "heal" };
        string[] icons = { "lock", "down", "up", "shield", "mark", "clock", "heart", "star" };
        Assert.Equal(effects, ArtManifest.FxIds);
        Assert.Equal(icons, ArtManifest.IconIds);
        for (int i = 0; i < effects.Length; i++)
        {
            var db = WithArt("{\"skillFx\":{\"struggle\":" + Quote(effects[i]) + "},\"statusIcons\":{\"reconnect\":" + Quote(icons[i]) + "}}");
            Assert.Equal(effects[i], db.Art.SkillFx(db.Struggle.Id));
            Assert.Equal(icons[i], db.Art.StatusIcon("reconnect"));
        }
    }

    [Fact]
    public void ArtOnlyChangesDoNotChangeGameplayContentFingerprint()
    {
        var original = BattleTests.LoadPack("original");
        var decorated = WithArt(@"{ ""portraitDir"": ""Local Artwork/活动"", ""streamers"": { ""bell"": { ""accent"": ""#112233"", ""portrait"": ""concert/新装"" } }, ""skillFx"": { ""full_volume"": ""wave"" }, ""statusIcons"": { ""stall"": ""heart"" } }");
        Assert.Equal("Local Artwork/活动/concert/新装", decorated.Art.PortraitPath("bell"));
        Assert.NotEqual(original.Art.PortraitPath("bell"), decorated.Art.PortraitPath("bell"));
        Assert.Equal(original.ContentHash(), decorated.ContentHash());
    }

    [Theory]
    [InlineData("{\"streamers\":{\"full_volume\":{}}}", "full_volume")]
    [InlineData("{\"skillFx\":{\"stall\":\"wave\"}}", "stall")]
    [InlineData("{\"statusIcons\":{\"full_volume\":\"star\"}}", "full_volume")]
    [InlineData("{\"statusIcons\":{\"missing_field\":\"clock\"}}", "missing_field")]
    [InlineData("{\"skillFx\":{\"full_volume\":\"Wave\"}}", "full_volume")]
    [InlineData("{\"statusIcons\":{\"reconnect\":\"SHIELD\"}}", "reconnect")]
    public void InvalidReferencesAndCaseVariantsReportTheirOwner(string art, string id)
    {
        AssertBadArt(art, id);
    }

    [Theory]
    [InlineData("#fff")]
    [InlineData("#12345678")]
    [InlineData("#GG1122")]
    [InlineData("112233")]
    [InlineData(" #112233")]
    [InlineData("#112233\n")]
    public void AccentMustHaveExactlySixHexDigits(string accent)
    {
        AssertBadArt("{\"streamers\":{\"bell\":{\"accent\":" + Quote(accent) + "}}}", "bell");
    }

    [Theory]
    [InlineData("")]
    [InlineData("/absolute")]
    [InlineData("//server/share")]
    [InlineData("folder//file")]
    [InlineData("folder/")]
    [InlineData("./file")]
    [InlineData("folder/../file")]
    [InlineData("prefix..suffix")]
    [InlineData("C:relative")]
    [InlineData("folder\\file")]
    [InlineData("HTTP-assets/file")]
    [InlineData("folder\u0001/file")]
    [InlineData("folder/<file>")]
    [InlineData("folder/a|b")]
    [InlineData("folder/a?b")]
    [InlineData("folder/a*b")]
    public void UnsafeDirectoryAndPortraitPathsBothFailWithChineseLocation(string path)
    {
        AssertBadArt("{\"portraitDir\":" + Quote(path) + "}", "portraitDir");
        AssertBadArt("{\"streamers\":{\"bell\":{\"portrait\":" + Quote(path) + "}}}", "bell");
    }

    [Theory]
    [InlineData("bell.png")]
    [InlineData("variants/bell.v2")]
    public void PortraitFileMustBeAnExtensionlessResourceName(string portrait)
    {
        AssertBadArt("{\"streamers\":{\"bell\":{\"portrait\":" + Quote(portrait) + "}}}", "bell");
    }

    [Theory]
    [InlineData("pack", "bad/../pack")]
    [InlineData("streamer", "speaker.png")]
    public void GeneratedDefaultPathsAreValidatedToo(string target, string id)
    {
        string pack = target == "pack"
            ? Original.Replace("\"id\": \"original\"", "\"id\": " + Quote(id))
            : Original.Replace("\"enka\"", Quote(id));
        var error = Assert.Throws<KeyError>(() => ContentDb.FromJson(Rules, pack));
        Assert.Contains(id, error.Message);
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
    }

    [Theory]
    [InlineData("null", "art")]
    [InlineData("[]", "art")]
    [InlineData("17", "art")]
    [InlineData("{\"portraitDir\":42}", "portraitDir")]
    [InlineData("{\"portraitDir\":null}", "portraitDir")]
    [InlineData("{\"streamers\":[]}", "streamers")]
    [InlineData("{\"streamers\":{\"bell\":[]}}", "bell")]
    [InlineData("{\"streamers\":{\"bell\":null}}", "bell")]
    [InlineData("{\"streamers\":{\"bell\":{\"accent\":123456}}}", "bell")]
    [InlineData("{\"streamers\":{\"bell\":{\"portrait\":false}}}", "bell")]
    [InlineData("{\"skillFx\":[]}", "skillFx")]
    [InlineData("{\"skillFx\":{\"full_volume\":[]}}", "full_volume")]
    [InlineData("{\"statusIcons\":true}", "statusIcons")]
    [InlineData("{\"statusIcons\":{\"stall\":null}}", "stall")]
    public void MalformedArtTypesProduceContentErrorsInsteadOfSilentFallback(string art, string location)
    {
        AssertBadArt(art, location);
    }

    [Fact]
    public void UnknownQueriesFailExplicitlyInsteadOfFabricatingResources()
    {
        var art = BattleTests.LoadPack("original").Art;
        Assert.Throws<KeyError>(() => art.PortraitPath("unknown_portrait"));
        Assert.Throws<KeyError>(() => art.Accent("unknown_accent"));
        Assert.Throws<KeyError>(() => art.SkillFx("unknown_skill"));
        Assert.Throws<KeyError>(() => art.StatusIcon("unknown_status"));
    }
}
