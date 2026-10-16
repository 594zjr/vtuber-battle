// T-60 验收测试：动画时间线。实现后把本文件移到 Engine.Tests/ 下。
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T60_TimelineTests
{
    static Battle Play(uint seed)
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        var b = new Battle(db, new IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() }, new[] { "A", "B" }, seed);
        int guard = 0;
        while (b.Phase != Phase.Over && guard++ < 500)
        {
            if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
            else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
        }
        return b;
    }

    [Fact]
    public void CuesAreOrderedAndCoverEveryVisibleEvent()
    {
        var b = Play(3);
        var cues = BattleTimeline.Build(b.Events, 0, b.Events.Count);
        for (int i = 1; i < cues.Count; i++) Assert.True(cues[i].Start >= cues[i - 1].Start - 1e-9, "提示的开始时间必须不减");
        foreach (var c in cues) Assert.True(c.Duration >= 0);
        for (int i = 0; i < b.Events.Count; i++)
        {
            var k = b.Events[i].Kind;
            if (k == BattleEventKind.PpChange || k == BattleEventKind.Info) continue;
            Assert.Contains(cues, c => c.EventIndex == i);
        }
        Assert.Contains(cues, c => c.Kind == CueKind.End);
    }

    [Fact]
    public void DamageMakesHitFlashFloatAndHpBarTogether()
    {
        var b = Play(5);
        var cues = BattleTimeline.Build(b.Events, 0, b.Events.Count);
        int di = Enumerable.Range(0, b.Events.Count).First(i => b.Events[i].Kind == BattleEventKind.Damage);
        var mine = cues.Where(c => c.EventIndex == di).ToList();
        foreach (var k in new[] { CueKind.Hit, CueKind.Flash, CueKind.FloatText, CueKind.HpBar }) Assert.Contains(mine, c => c.Kind == k);
        Assert.Single(mine.Select(c => c.Start).Distinct());
        var ft = mine.First(c => c.Kind == CueKind.FloatText);
        Assert.Equal("damage", ft.Style);
        Assert.Contains(b.Events[di].Amount.ToString(), ft.Text);
    }

    [Fact]
    public void SpeedScalesAndZeroSkips()
    {
        var b = Play(7);
        var one = BattleTimeline.Build(b.Events, 0, b.Events.Count, 1.0);
        var two = BattleTimeline.Build(b.Events, 0, b.Events.Count, 2.0);
        var skip = BattleTimeline.Build(b.Events, 0, b.Events.Count, 0);
        Assert.Equal(one.Count, two.Count);
        Assert.Equal(BattleTimeline.TotalDuration(one) / 2, BattleTimeline.TotalDuration(two), 6);
        Assert.Equal(one.Count, skip.Count);
        Assert.All(skip, c => { Assert.Equal(0, c.Start); Assert.Equal(0, c.Duration); });
    }

    [Fact]
    public void SameInputSameOutputAndRangesSplit()
    {
        var b = Play(9);
        string Dump(List<AnimCue> l) => string.Join(";", l.Select(c => c.Kind + "@" + c.Start.ToString("F3") + "/" + c.EventIndex + "/" + c.Text));
        Assert.Equal(Dump(BattleTimeline.Build(b.Events, 0, b.Events.Count)), Dump(BattleTimeline.Build(b.Events, 0, b.Events.Count)));
        int mid = b.Events.Count / 2;
        var tail = BattleTimeline.Build(b.Events, mid, b.Events.Count);
        Assert.All(tail, c => Assert.InRange(c.EventIndex, mid, b.Events.Count - 1));
        Assert.Equal(0, tail.Min(c => c.Start), 6);
    }
}
