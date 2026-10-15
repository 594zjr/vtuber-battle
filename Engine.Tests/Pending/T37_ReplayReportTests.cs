// T-37 验收测试：把一局对战导出成可读战报。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T37_ReplayReportTests
{
    static Battle Play(uint seed)
    {
        var db = BattleTests.LoadPack("fan_asoul_vr");
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] { ids.Take(6).ToList(), ids.Skip(1).Take(6).ToList() }, new[] { "甲", "乙" }, seed);
        int guard = 0;
        while (b.Phase != Phase.Over && guard++ < 500)
        {
            if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
            else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
        }
        return b;
    }

    [Fact]
    public void ReportContainsEveryLogLineInOrder()
    {
        var b = Play(4);
        var md = BattleReport.Markdown(b);
        int pos = 0;
        foreach (var l in b.Log)
        {
            int at = md.IndexOf(l.Text, pos, System.StringComparison.Ordinal);
            Assert.True(at >= 0, "战报缺少：" + l.Text);
            pos = at;
        }
        Assert.Contains("非官方同人作品", md); // 同人包的声明要带上
        Assert.Contains("## 第 1 回合", md);
        Assert.Contains(b.Winner == -1 ? "平局" : b.Sides[b.Winner].Name, md);
    }

    [Fact]
    public void ReportIsDeterministic()
    {
        Assert.Equal(BattleReport.Markdown(Play(4)), BattleReport.Markdown(Play(4)));
    }
}
