// T-50 验收测试：成就（规则层）。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T50_AchievementsTests
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
    public void DefinitionsAreCompleteAndUnique()
    {
        var all = Achievements.All;
        Assert.True(all.Count >= 20);
        Assert.Equal(all.Count, all.Select(a => a.Id).Distinct().Count());
        Assert.All(all, a => { Assert.False(string.IsNullOrEmpty(a.Name)); Assert.False(string.IsNullOrEmpty(a.Desc)); });
    }

    [Fact]
    public void FirstWinUnlocksOnceAndStateRoundTrips()
    {
        var st = new AchievementState();
        Battle won = null;
        for (uint seed = 1; won == null; seed++) { var b = Play(seed); if (b.Winner == 0) won = b; }
        var got = Achievements.Evaluate(st, won, 0, new Profile());
        Assert.Contains(got, a => a.Id == "first_win");
        Assert.DoesNotContain(Achievements.Evaluate(st, won, 0, new Profile()), a => a.Id == "first_win");
        Assert.Equal(st.ToJson(), AchievementState.FromJson(st.ToJson()).ToJson());
    }

    [Fact]
    public void EvaluationIsDeterministic()
    {
        var a = Achievements.Evaluate(new AchievementState(), Play(4), 0, new Profile()).Select(x => x.Id);
        var b = Achievements.Evaluate(new AchievementState(), Play(4), 0, new Profile()).Select(x => x.Id);
        Assert.Equal(a, b);
    }
}
