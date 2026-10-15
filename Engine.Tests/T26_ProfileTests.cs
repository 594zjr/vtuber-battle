// T-26 验收测试：玩家档案（战绩统计）。实现后把本文件移到 Engine.Tests/ 下。
using System;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T26_ProfileTests
{
    static Battle Play(uint seed)
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() }, new[] { "A", "B" }, seed);
        int guard = 0;
        while (b.Phase != Phase.Over && guard++ < 500)
        {
            if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
            else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
        }
        return b;
    }

    [Fact]
    public void RecordsCountsAndRoundTrips()
    {
        var p = new Profile();
        int wins = 0, losses = 0, draws = 0;
        for (uint seed = 1; seed <= 10; seed++)
        {
            var b = Play(seed);
            p.Record(b, 0);
            if (b.Winner == 0) wins++; else if (b.Winner == 1) losses++; else draws++;
        }
        var st = p.Packs["original"];
        Assert.Equal(wins, st.Wins); Assert.Equal(losses, st.Losses); Assert.Equal(draws, st.Draws);
        Assert.Equal(10, st.Picks["enka"]);
        Assert.Equal(wins, st.PickWins["enka"]);

        var back = Profile.FromJson(p.ToJson());
        Assert.Equal(p.ToJson(), back.ToJson());
    }

    [Fact]
    public void UnfinishedBattleIsRejected()
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() }, new[] { "A", "B" }, 1);
        Assert.Throws<InvalidOperationException>(() => new Profile().Record(b, 0));
    }

    [Fact]
    public void EmptyOrBrokenJsonGivesEmptyProfile()
    {
        Assert.Empty(Profile.FromJson("").Packs);
        Assert.Empty(Profile.FromJson("{ 这不是 json").Packs);
    }
}
