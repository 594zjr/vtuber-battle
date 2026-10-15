// T-53 验收测试：主播专属的电脑出招风格。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T53_AiStyleTests
{
    static string Play(ContentDb db, uint seed)
    {
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() }, new[] { "A", "B" }, seed);
        int guard = 0;
        while (b.Phase != Phase.Over && guard++ < 500)
        {
            if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
            else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
        }
        return string.Join("\n", b.Log.Select(l => l.Text));
    }

    [Fact]
    public void PresetsExistAndNoStyleMeansDefault()
    {
        foreach (var name in new[] { "aggressive", "defensive", "tricky" }) Assert.NotNull(AiStyles.Get(name));
        Assert.Null(AiStyles.Get("no_such"));
        var db = BattleTests.LoadPack("original");
        Assert.All(db.Streamers, s => Assert.Null(s.AiStyle));
    }

    [Fact]
    public void StyleChangesChoicesForThatStreamer()
    {
        var plain = BattleTests.LoadPack("original");
        var styled = BattleTests.LoadPack("original");
        foreach (var s in styled.Streamers) s.AiStyle = "aggressive";
        bool differs = false;
        for (uint seed = 1; seed <= 10 && !differs; seed++) differs = Play(plain, seed) != Play(styled, seed);
        Assert.True(differs);
    }

    [Fact]
    public void UnknownStyleIsRejected()
    {
        var db = BattleTests.LoadPack("original");
        db.Streamer("kei").AiStyle = "berserk";
        Assert.Throws<KeyError>(() => db.Validate());
    }
}
