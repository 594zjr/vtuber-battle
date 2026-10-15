// T-35 验收测试：专家难度（向前模拟）。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T35_ExpertAiTests
{
    static int[] Match(int games)
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        int[] wins = new int[3];
        for (int g = 0; g < games; g++)
        {
            int rot = g % ids.Length;
            var order = ids.Skip(rot).Concat(ids.Take(rot)).ToArray();
            int expertSide = g % 2;
            var b = new Battle(db, new System.Collections.Generic.IList<string>[] { order.Take(6).ToList(), order.Skip(6).Take(6).ToList() }, new[] { "A", "B" }, (uint)(1000 + g));
            int guard = 0;
            while (b.Phase != Phase.Over && guard++ < 500)
            {
                if (b.Phase == Phase.Choose)
                    b.ResolveTurn(Ai.Choose(b, 0, expertSide == 0 ? AiLevel.Expert : AiLevel.Hard), Ai.Choose(b, 1, expertSide == 1 ? AiLevel.Expert : AiLevel.Hard));
                else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s, s == expertSide ? AiLevel.Expert : AiLevel.Hard));
            }
            if (b.Winner == expertSide) wins[0]++; else if (b.Winner == -1) wins[2]++; else wins[1]++;
        }
        return wins;
    }

    [Fact]
    public void ExpertBeatsHard()
    {
        var w = Match(200);
        Assert.True(w[0] >= 0.55 * (w[0] + w[1]), $"专家胜 {w[0]}，负 {w[1]}，平 {w[2]}");
    }

    [Fact]
    public void ExpertDoesNotChangeTheRealBattle()
    {
        var db = BattleTests.LoadPack("original");
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" } }, new[] { "A", "B" }, 9);
        var h = b.StateHash();
        var a1 = Ai.Choose(b, 0, AiLevel.Expert);
        // 专家只在克隆上模拟；除了打破平局用到的随机数，不改变对战
        var c = new Battle(db, new System.Collections.Generic.IList<string>[] {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" } }, new[] { "A", "B" }, 9);
        var a2 = Ai.Choose(c, 0, AiLevel.Expert);
        Assert.Equal(a1.ToWire(), a2.ToWire());
        Assert.Equal(b.Log.Count, c.Log.Count);
        Assert.Equal(b.Active(0).Hp, b.Active(0).MaxHp);
    }
}
