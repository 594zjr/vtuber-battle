// T-06 验收测试：电脑难度。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T06_AiLevelTests
{
    static int Play(ContentDb db, uint seed, AiLevel l0, AiLevel l1)
    {
        var all = db.Streamers.Select(s => s.Id).ToList();
        int k = (int)(seed % (uint)all.Count);
        var ids = all.Skip(k).Concat(all.Take(k)).ToList(); // 按种子轮换阵容，结果可复现
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() }, new[] { "A", "B" }, seed);
        int guard = 0;
        while (b.Phase != Phase.Over && guard++ < 500)
        {
            if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0, l0), Ai.Choose(b, 1, l1));
            else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
        }
        return b.Winner;
    }

    [Fact]
    public void NormalBeatsEasyMostOfTheTime()
    {
        var db = BattleTests.LoadPack("original");
        int normalWins = 0, games = 300;
        for (uint i = 0; i < games; i++)
        {
            bool normalFirst = i % 2 == 0; // 交换座位，避免座位偏差
            int w = normalFirst ? Play(db, i, AiLevel.Normal, AiLevel.Easy) : Play(db, i, AiLevel.Easy, AiLevel.Normal);
            if ((normalFirst && w == 0) || (!normalFirst && w == 1)) normalWins++;
        }
        Assert.True(normalWins >= games * 0.6, "普通难度只赢了 " + normalWins + "/" + games);
    }

    [Fact]
    public void TwoArgumentChooseStaysNormal()
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(s => s.Id).ToList();
        var a = new Battle(db, new System.Collections.Generic.IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() }, new[] { "A", "B" }, 3);
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() }, new[] { "A", "B" }, 3);
        Assert.Equal(Ai.Choose(a, 0).ToString(), Ai.Choose(b, 0, AiLevel.Normal).ToString());
    }
}
