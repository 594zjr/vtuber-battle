// T-34 验收测试：对战深拷贝。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T34_CloneTests
{
    static Battle Make(uint seed)
    {
        var db = BattleTests.LoadPack("original");
        return new Battle(db, new System.Collections.Generic.IList<string>[] {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" } }, new[] { "A", "B" }, seed);
    }

    static void Step(Battle b)
    {
        if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
        else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
    }

    [Fact]
    public void CloneMatchesAndEvolvesIdentically()
    {
        var b = Make(6);
        for (int i = 0; i < 5; i++) Step(b);
        var c = b.Clone();
        Assert.Equal(b.StateHash(), c.StateHash());
        Assert.Equal(b.Log.Count, c.Log.Count);
        for (int i = 0; i < 30 && b.Phase != Phase.Over; i++) { Step(b); Step(c); Assert.Equal(b.StateHash(), c.StateHash()); }
    }

    [Fact]
    public void CloneIsIndependent()
    {
        var b = Make(6);
        var before = b.StateHash();
        var c = b.Clone();
        c.Active(0).Hp = 1;
        c.Active(0).Skills[0].Pp = 0;
        c.Active(0).Stages["atk"] = 3;
        c.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        Assert.Equal(before, b.StateHash());
        Assert.NotEqual(1, b.Active(0).Hp);
        Assert.Equal(0, b.Active(0).Stages["atk"]);
    }

    [Fact]
    public void CloneDoesNotTouchOriginalRecord()
    {
        var b = Make(6);
        var json = b.Record.ToJson();
        var c = b.Clone();
        c.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(json, b.Record.ToJson());
    }
}
