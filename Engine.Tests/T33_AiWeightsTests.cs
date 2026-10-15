// T-33 验收测试：电脑对手的权重可配置。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T33_AiWeightsTests
{
    static string Play(uint seed, System.Func<Battle, int, BattleAction> choose)
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() }, new[] { "A", "B" }, seed);
        int guard = 0;
        while (b.Phase != Phase.Over && guard++ < 500)
        {
            if (b.Phase == Phase.Choose) b.ResolveTurn(choose(b, 0), choose(b, 1));
            else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
        }
        return string.Join("\n", b.Log.Select(l => l.Node + l.Text));
    }

    [Fact]
    public void DefaultWeightsReproduceNormalExactly()
    {
        for (uint seed = 1; seed <= 10; seed++)
            Assert.Equal(Play(seed, (b, s) => Ai.Choose(b, s)),
                         Play(seed, (b, s) => Ai.Choose(b, s, AiLevel.Normal, AiWeights.Default)));
    }

    [Fact]
    public void WeightsRoundTripAndChangeBehaviour()
    {
        var w = AiWeights.Default.Copy();
        Assert.Equal(AiWeights.Default.ToJson(), AiWeights.FromJson(AiWeights.Default.ToJson()).ToJson());
        var names = w.Names().ToList();
        Assert.True(names.Count >= 8);
        foreach (var n in names) w.Set(n, w.Get(n) * 3 + 1);
        Assert.NotEqual(AiWeights.Default.ToJson(), w.ToJson());
        bool differs = false;
        for (uint seed = 1; seed <= 10 && !differs; seed++)
            differs = Play(seed, (b, s) => Ai.Choose(b, s)) != Play(seed, (b, s) => Ai.Choose(b, s, AiLevel.Normal, w));
        Assert.True(differs);
    }
}
