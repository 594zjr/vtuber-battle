// T-61 验收测试：立绘与特效资源清单。实现后把本文件移到 Engine.Tests/ 下。
using System;
using System.IO;
using StreamDuel.Core;
using Xunit;

public class T61_ArtManifestTests
{
    static string Res(string rel) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", rel));
    static string Rules => Res("StreamDuelRules.json");
    static string Original => Res(Path.Combine("Packs", "original.json"));

    static ContentDb WithArt(string art)
    {
        var pack = Original.TrimEnd();
        pack = pack.Substring(0, pack.LastIndexOf('}')) + ", \"art\": " + art + " }";
        return ContentDb.FromJson(Rules, pack);
    }

    [Fact]
    public void DefaultsWithoutArtSection()
    {
        var db = BattleTests.LoadPack("original");
        Assert.NotNull(db.Art);
        Assert.Equal("Portraits/original/bell", db.Art.PortraitPath("bell"));
        Assert.Matches("^#[0-9a-fA-F]{6}$", db.Art.Accent("bell"));
        foreach (var sk in db.Skills.Keys) Assert.Contains(db.Art.SkillFx(sk), ArtManifest.FxIds);
        foreach (var st in db.Statuses.Keys) Assert.Contains(db.Art.StatusIcon(st), ArtManifest.IconIds);
        Assert.Equal("slash", db.Art.SkillFx(FirstSkill(db, "interact")));
    }

    static string FirstSkill(ContentDb db, string cat)
    {
        foreach (var kv in db.Skills) if (kv.Value.Cat == cat) return kv.Key;
        throw new InvalidOperationException(cat);
    }

    [Fact]
    public void ExplicitArtOverridesDefaults()
    {
        var db = WithArt("{ \"portraitDir\": \"Portraits/mine\", \"streamers\": { \"bell\": { \"accent\": \"#ffcc33\", \"portrait\": \"bell_v2\" } }, \"skillFx\": { \"full_volume\": \"wave\" } }");
        Assert.Equal("Portraits/mine/bell_v2", db.Art.PortraitPath("bell"));
        Assert.Equal("Portraits/mine/enka", db.Art.PortraitPath("enka"));
        Assert.Equal("#ffcc33", db.Art.Accent("bell"));
        Assert.Equal("wave", db.Art.SkillFx("full_volume"));
    }

    [Theory]
    [InlineData("{ \"skillFx\": { \"full_volume\": \"laser\" } }")]
    [InlineData("{ \"skillFx\": { \"no_such_skill\": \"wave\" } }")]
    [InlineData("{ \"streamers\": { \"nobody\": { \"accent\": \"#ffffff\" } } }")]
    [InlineData("{ \"streamers\": { \"bell\": { \"accent\": \"yellow\" } } }")]
    [InlineData("{ \"portraitDir\": \"../secret\" }")]
    [InlineData("{ \"portraitDir\": \"http://x/y\" }")]
    [InlineData("{ \"portraitDir\": \"C:/x\" }")]
    [InlineData("{ \"statusIcons\": { \"stall\": \"skull\" } }")]
    public void BadArtIsRejected(string art)
    {
        Assert.ThrowsAny<Exception>(() => WithArt(art).Validate());
    }
}
