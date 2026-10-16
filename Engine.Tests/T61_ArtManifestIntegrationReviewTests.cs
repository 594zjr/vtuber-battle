using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T61_ArtManifestIntegrationReviewTests
{
    static string Resource(string path) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", path));
    static string Rules => Resource("StreamDuelRules.json");

    static JsonObject Pack(string id = "original") => JsonNode.Parse(Resource(Path.Combine("Packs", id + ".json"))).AsObject();
    static ContentDb Load(JsonObject pack, PackPolicy policy = null) => ContentDb.FromJson(Rules, pack.ToJsonString(), policy);

    static JsonObject CustomPack(string id, string prefix, string art = null)
    {
        var pack = Pack();
        pack["id"] = id;
        pack.Remove("seasons");
        foreach (var streamer in pack["streamers"].AsArray()) streamer["id"] = prefix + streamer["id"].GetValue<string>();
        if (art != null) pack["art"] = JsonNode.Parse(art);
        return pack;
    }

    static PackPolicy Disabled(params string[] ids) => PackPolicy.FromJson(JsonSerializer.Serialize(new
    {
        disabledStreamers = ids,
        reason = ids.ToDictionary(id => id, id => "测试下架：" + id)
    }));

    [Fact]
    public void MixedPortraitsKeepEachSourceDirectoryAndPartialOverridesInEitherOrder()
    {
        var first = CustomPack("first", "a_", @"{
            ""portraitDir"":""Local Artwork/活动"",
            ""streamers"":{
                ""a_bell"":{""portrait"":""concert/bell_v2"",""accent"":""#112233""},
                ""a_enka"":{""accent"":""#AABBCC""},
                ""a_shiori"":{""portrait"":""shiori_v2""}
            }
        }");
        var a = Load(first);
        var b = Load(CustomPack("second", "b_"));
        var forward = ContentDb.Merge(a, b);
        var reverse = ContentDb.Merge(b, a);

        Assert.Equal("Local Artwork/活动/concert/bell_v2", forward.Art.PortraitPath("a_bell"));
        Assert.Equal("Local Artwork/活动/a_enka", forward.Art.PortraitPath("a_enka"));
        Assert.Equal("Local Artwork/活动/shiori_v2", forward.Art.PortraitPath("a_shiori"));
        Assert.Equal("Portraits/second/b_bell", forward.Art.PortraitPath("b_bell"));
        Assert.Equal("#112233", forward.Art.Accent("a_bell"));
        Assert.Equal("#AABBCC", forward.Art.Accent("a_enka"));
        Assert.Equal(a.Types[a.Streamer("a_shiori").Type].Color, forward.Art.Accent("a_shiori"));
        foreach (var streamer in forward.Streamers)
        {
            var source = streamer.Id.StartsWith("a_", StringComparison.Ordinal) ? a : b;
            Assert.Equal(source.Art.PortraitPath(streamer.Id), forward.Art.PortraitPath(streamer.Id));
            Assert.Equal(forward.Art.PortraitPath(streamer.Id), reverse.Art.PortraitPath(streamer.Id));
            Assert.Equal(source.Art.Accent(streamer.Id), forward.Art.Accent(streamer.Id));
            Assert.Equal(forward.Art.Accent(streamer.Id), reverse.Art.Accent(streamer.Id));
        }
        forward.Validate();
        reverse.Validate();
    }

    [Fact]
    public void MixedDefaultQueriesBindToIndependentCopiedDefinitionsAndFrozenSourceDirectories()
    {
        var source = Load(CustomPack("first", "a_"));
        var mixed = ContentDb.Merge(source);
        string originalColor = mixed.Art.Accent("a_bell");
        source.Types["song"].Color = "#010203";
        source.Skills["full_volume"].Cat = "interact";
        source.Statuses["stall"].Cat = "weak";
        source.PackId = "renamed";

        Assert.Equal(originalColor, mixed.Art.Accent("a_bell"));
        Assert.Equal("beam", mixed.Art.SkillFx("full_volume"));
        Assert.Equal("lock", mixed.Art.StatusIcon("stall"));
        Assert.Equal("Portraits/first/a_bell", mixed.Art.PortraitPath("a_bell"));
        Assert.Equal("Portraits/renamed/a_bell", source.Art.PortraitPath("a_bell"));
        mixed.Types["song"].Color = "#040506";
        Assert.Equal("#040506", mixed.Art.Accent("a_bell"));
        Assert.Equal("#010203", source.Art.Accent("a_bell"));
        Assert.Equal("slash", source.Art.SkillFx("full_volume"));
        Assert.Equal("down", source.Art.StatusIcon("stall"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedExplicitOverridesCombineWithDefaultsOrIdenticalOverrides(bool bothExplicit)
    {
        const string art = @"{""skillFx"":{""full_volume"":""wave"",""struggle"":""ring""},""statusIcons"":{""stall"":""star"",""reconnect"":""heart""}}";
        var a = Load(CustomPack("first", "a_", art));
        var b = Load(CustomPack("second", "b_", bothExplicit ? art : null));

        foreach (var mixed in new[] { ContentDb.Merge(a, b), ContentDb.Merge(b, a) })
        {
            Assert.Equal("wave", mixed.Art.SkillFx("full_volume"));
            Assert.Equal("ring", mixed.Art.SkillFx("struggle"));
            Assert.Equal("star", mixed.Art.StatusIcon("stall"));
            Assert.Equal("heart", mixed.Art.StatusIcon("reconnect"));
        }
        if (!bothExplicit)
        {
            Assert.Equal("beam", b.Art.SkillFx("full_volume"));
            Assert.Equal("slash", b.Art.SkillFx("struggle"));
            Assert.Equal("lock", b.Art.StatusIcon("stall"));
            Assert.Equal("shield", b.Art.StatusIcon("reconnect"));
        }
    }

    [Theory]
    [InlineData("skillFx", "full_volume", "wave", "beam")]
    [InlineData("skillFx", "struggle", "ring", "slash")]
    [InlineData("statusIcons", "stall", "star", "lock")]
    [InlineData("statusIcons", "reconnect", "heart", "shield")]
    public void ConflictingExplicitOverridesFailDeterministicallyEvenWhenOneEqualsTheDefault(string category, string id, string left, string right)
    {
        var aPack = CustomPack("first", "a_");
        var bPack = CustomPack("second", "b_");
        aPack["art"] = new JsonObject { [category] = new JsonObject { [id] = left } };
        bPack["art"] = new JsonObject { [category] = new JsonObject { [id] = right } };
        var a = Load(aPack);
        var b = Load(bPack);
        string aHash = a.ContentHash(), bHash = b.ContentHash();

        var forward = Assert.Throws<KeyError>(() => ContentDb.Merge(a, b));
        var reverse = Assert.Throws<KeyError>(() => ContentDb.Merge(b, a));

        Assert.Equal(forward.Message, reverse.Message);
        Assert.Contains(category + "." + id, forward.Message);
        Assert.Contains("冲突", forward.Message);
        Assert.Equal(aHash, a.ContentHash());
        Assert.Equal(bHash, b.ContentHash());
        Assert.Equal(left, category == "skillFx" ? a.Art.SkillFx(id) : a.Art.StatusIcon(id));
        Assert.Equal(right, category == "skillFx" ? b.Art.SkillFx(id) : b.Art.StatusIcon(id));
    }

    [Fact]
    public void PrivateSkillStatusAndFieldArtSurvivesMergingWithTheSharedRules()
    {
        var pack = CustomPack("first", "a_", @"{""skillFx"":{""art_pulse"":""burst""},""statusIcons"":{""art_control"":""heart"",""art_guard"":""up""}}");
        pack["skills"] = JsonNode.Parse(@"{""art_pulse"":{""name"":""测试招式"",""type"":""song"",""cat"":""talent"",""power"":30,""pp"":10}}");
        pack["statuses"] = JsonNode.Parse(@"{""art_control"":{""name"":""测试控制"",""cat"":""control"",""skip"":1}}");
        pack["fields"] = JsonNode.Parse(@"{""art_guard"":{""name"":""测试保护"",""kind"":""protect"",""when"":""被施加控制时"",""dispel"":false,""immuneControl"":true}}");

        var mixed = ContentDb.Merge(Load(pack), Load(CustomPack("second", "b_")));

        Assert.Equal("burst", mixed.Art.SkillFx("art_pulse"));
        Assert.Equal("heart", mixed.Art.StatusIcon("art_control"));
        Assert.Equal("up", mixed.Art.StatusIcon("art_guard"));
        Assert.Equal("beam", mixed.Art.SkillFx("full_volume"));
        Assert.Equal("lock", mixed.Art.StatusIcon("stall"));
    }

    [Fact]
    public void ArtOnlyChangesInMixedSourcesPreserveBattleHashRandomStreamAndReplay()
    {
        var aPack = CustomPack("first", "a_");
        var bPack = CustomPack("second", "b_");
        var plain = ContentDb.Merge(Load(aPack), Load(bPack));
        aPack["art"] = JsonNode.Parse(@"{""portraitDir"":""Artwork/活动"",""streamers"":{""a_bell"":{""accent"":""#123456"",""portrait"":""bell_v2""}},""skillFx"":{""full_volume"":""wave""},""statusIcons"":{""stall"":""star""}}");
        var decorated = ContentDb.Merge(Load(bPack), Load(aPack));
        Assert.Equal(plain.ContentHash(), decorated.ContentHash());
        IList<string>[] teams = MixedBattle.RandomTeams(plain, 61);
        var first = new Battle(plain, teams, new[] { "甲", "乙" }, 61);
        var second = new Battle(decorated, teams, new[] { "甲", "乙" }, 61);
        ulong beforeQueries = second.StateHash();
        foreach (var streamer in decorated.Streamers)
        {
            decorated.Art.PortraitPath(streamer.Id);
            decorated.Art.Accent(streamer.Id);
        }
        foreach (string id in decorated.Skills.Keys) decorated.Art.SkillFx(id);
        foreach (string id in decorated.Statuses.Keys.Concat(decorated.Fields.Keys)) decorated.Art.StatusIcon(id);
        Assert.Equal(beforeQueries, second.StateHash());
        first.ResolveTurn(Ai.Choose(first, 0), Ai.Choose(first, 1));
        second.ResolveTurn(Ai.Choose(second, 0), Ai.Choose(second, 1));
        first.Surrender(0);
        second.Surrender(0);

        Assert.Equal(first.StateHash(), second.StateHash());
        Assert.Equal(first.Record.ToJson(), second.Record.ToJson());
        var replay = Replay.Run(decorated, BattleRecord.FromJson(first.Record.ToJson()));
        Assert.Equal(first.StateHash(), replay.StateHash());
        Assert.Equal(first.Winner, replay.Winner);
        Assert.Equal(Phase.Over, replay.Phase);
        double next = first.Random();
        Assert.Equal(next, second.Random());
        Assert.Equal(next, replay.Random());
    }

    [Fact]
    public void DownlistingPrunesOnlyRemovedStreamerArtAndRemainingMixedContentStillValidates()
    {
        var pack = Pack();
        pack["art"] = JsonNode.Parse(@"{""portraitDir"":""Artwork/original"",""streamers"":{""bell"":{""portrait"":""bell_v2"",""accent"":""#112233""},""enka"":{""portrait"":""enka_v2""}},""skillFx"":{""full_volume"":""wave""}}");
        var policy = Disabled("bell");
        var filtered = Load(pack, policy);
        var fan = Load(Pack("fan_asoul_vr"), policy);
        var mixed = ContentDb.Merge(filtered, fan);

        filtered.Validate();
        mixed.Validate();
        Assert.Equal(11, filtered.Streamers.Count);
        Assert.Equal(18, mixed.Streamers.Count);
        Assert.Equal("Artwork/original/enka_v2", filtered.Art.PortraitPath("enka"));
        Assert.Equal(filtered.Art.PortraitPath("enka"), mixed.Art.PortraitPath("enka"));
        Assert.Equal("wave", mixed.Art.SkillFx("full_volume"));
        foreach (var db in new[] { filtered, mixed })
        {
            Assert.Contains(policy.StreamerReason("bell"), Assert.Throws<KeyError>(() => db.Art.PortraitPath("bell")).Message);
            Assert.Contains(policy.StreamerReason("bell"), Assert.Throws<KeyError>(() => db.Art.Accent("bell")).Message);
        }
        Assert.Equal("Artwork/original/bell_v2", Load(pack).Art.PortraitPath("bell"));
        Assert.Equal(ContentDb.Merge(Load(Pack(), policy), fan).ContentHash(), mixed.ContentHash());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DownlistingNeverHidesAnInitiallyUnknownArtReference(bool alsoRemoveKnownStreamer)
    {
        var pack = Pack();
        pack["art"] = JsonNode.Parse(@"{""streamers"":{""unknown_art"":{""portrait"":""placeholder""}}}");
        var policy = alsoRemoveKnownStreamer ? Disabled("unknown_art", "bell") : Disabled("unknown_art");

        Assert.Contains("unknown_art", Assert.Throws<KeyError>(() => Load(pack, policy)).Message);
    }

    [Fact]
    public void ManualRosterRemovalDoesNotLeaveAnAllowedDanglingArtReference()
    {
        var pack = Pack();
        pack["art"] = JsonNode.Parse(@"{""streamers"":{""bell"":{""portrait"":""bell_v2""}}}");
        var db = Load(pack);
        db.Streamers.RemoveAll(s => s.Id == "bell");
        db.Seasons.Clear();

        Assert.Contains("bell", Assert.Throws<KeyError>(() => db.Validate()).Message);
    }

    [Theory]
    [InlineData("a:b", "a%3Ab")]
    [InlineData("a%3Ab", "a%253Ab")]
    [InlineData("a\\b", "a%5Cb")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("a<b>c", "a%3Cb%3Ec")]
    [InlineData("a\"b", "a%22b")]
    [InlineData("a|b?c*d", "a%7Cb%3Fc%2Ad")]
    [InlineData("a,b", "a,b")]
    [InlineData("活动_😀", "活动_😀")]
    public void LogicalPackIdsHaveSafeDistinctDefaultDirectoriesAndKeepSourcePathsAfterMerge(string id, string encoded)
    {
        var db = Load(CustomPack(id, "a_"));
        var other = Load(CustomPack("second", "b_"));
        string path = "Portraits/" + encoded + "/a_bell";

        Assert.Equal(id, db.PackId);
        Assert.Equal(path, db.Art.PortraitPath("a_bell"));
        Assert.Equal(path, ContentDb.Merge(db, other).Art.PortraitPath("a_bell"));
    }

    [Theory]
    [InlineData("a:b/../c")]
    [InlineData("HTTP:assets")]
    [InlineData("a:b/./c")]
    [InlineData("a:b//c")]
    [InlineData("/a:b")]
    [InlineData("a:b\n")]
    [InlineData("a:b/ c")]
    public void DefaultDirectoryEncodingCannotBypassExistingPathSafetyBoundaries(string id)
    {
        Assert.Contains(id, Assert.Throws<KeyError>(() => Load(CustomPack(id, "a_"))).Message);
    }
}
