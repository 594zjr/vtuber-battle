// T-40 验收测试：结构化战斗事件。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T40_BattleEventsTests
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
    public void HpEventsAccountForEveryPointOfHp()
    {
        for (uint seed = 1; seed <= 10; seed++)
        {
            var b = Play(seed);
            for (int s = 0; s < 2; s++)
                for (int i = 0; i < 6; i++)
                {
                    var f = b.Sides[s].Team[i];
                    int delta = b.Events.Where(e => e.Side == s && e.Slot == i && (e.Kind == BattleEventKind.Damage || e.Kind == BattleEventKind.Heal))
                                        .Sum(e => e.Kind == BattleEventKind.Damage ? -e.Amount : e.Amount);
                    Assert.Equal(f.Hp - f.MaxHp, delta);
                    Assert.Equal(f.Fainted ? 1 : 0, b.Events.Count(e => e.Side == s && e.Slot == i && e.Kind == BattleEventKind.Faint));
                }
        }
    }

    [Fact]
    public void EventsPointAtTheirLogLineAndAreOrdered()
    {
        var b = Play(3);
        int last = -1;
        foreach (var e in b.Events)
        {
            Assert.InRange(e.LogIndex, 0, b.Log.Count - 1);
            Assert.True(e.LogIndex >= last);
            last = e.LogIndex;
            Assert.Equal(b.Log[e.LogIndex].Node, e.Node);
            Assert.Equal(b.Log[e.LogIndex].Turn, e.Turn);
        }
        Assert.Equal(b.Log.Count(l => l.Kind == "act"), b.Events.Count(e => e.Kind == BattleEventKind.Act));
        Assert.Contains(b.Events, e => e.Kind == BattleEventKind.Switch || e.Kind == BattleEventKind.Enter);
    }

    [Fact]
    public void EventsAreDeterministicAndCloned()
    {
        string Dump(Battle x) => string.Join("\n", x.Events.Select(e => $"{e.LogIndex}|{e.Kind}|{e.Side}|{e.Slot}|{e.Amount}|{e.Id}"));
        var a = Play(7);
        Assert.Equal(Dump(a), Dump(Play(7)));
        var c = a.Clone();
        Assert.Equal(Dump(a), Dump(c));
    }
}
