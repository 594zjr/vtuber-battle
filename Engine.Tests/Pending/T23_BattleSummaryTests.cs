// T-23 验收测试：对战统计。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T23_BattleSummaryTests
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
    public void KosMatchFaintsOnTheOtherSide()
    {
        for (uint seed = 1; seed <= 20; seed++)
        {
            var b = Play(seed);
            var sum = BattleSummary.From(b);
            for (int s = 0; s < 2; s++)
            {
                int fainted = b.Sides[1 - s].Team.Count(f => f.Fainted);
                Assert.Equal(fainted, sum.Sides[s].Sum(f => f.Kos));
                Assert.Equal(6, sum.Sides[s].Count);
            }
        }
    }

    [Fact]
    public void DamageTakenMatchesHpLostAndSkillUsesMatchActions()
    {
        var b = Play(5);
        var sum = BattleSummary.From(b);
        for (int s = 0; s < 2; s++)
            for (int i = 0; i < 6; i++)
            {
                var f = b.Sides[s].Team[i];
                var st = sum.Sides[s][i];
                Assert.Equal(f.Def.Id, st.StreamerId);
                Assert.True(st.DamageTaken >= f.MaxHp - f.Hp); // 回复过的人可能受伤更多
                Assert.True(st.DamageDealt >= 0);
            }
        int acts = b.Log.Count(l => l.Kind == "act");
        int uses = sum.Sides.Sum(side => side.Sum(f => f.SkillUses.Values.Sum()));
        Assert.Equal(acts, uses);
    }

    [Fact]
    public void SummaryIsDeterministic()
    {
        Assert.Equal(BattleSummary.From(Play(9)).ToText(), BattleSummary.From(Play(9)).ToText());
    }
}
