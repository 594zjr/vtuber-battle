// T-54 验收测试：赛制与三局两胜。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T54_RulesetTests
{
    static readonly string[] A = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] B = { "yuan", "nyako", "buck", "kasumi", "bee", "enka" };

    static void PlayOut(Battle b)
    {
        int guard = 0;
        while (b.Phase != Phase.Over && guard++ < 500)
        {
            if (b.Phase == Phase.Ban) { b.ChooseBan(0, Ai.ChooseBan(b, 0)); b.ChooseBan(1, Ai.ChooseBan(b, 1)); }
            else if (b.Phase == Phase.Lead) { b.ChooseLead(0, Ai.ChooseLead(b, 0)); b.ChooseLead(1, Ai.ChooseLead(b, 1)); }
            else if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
            else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
        }
    }

    [Fact]
    public void BuiltInRulesetsExistAndRoundTrip()
    {
        var all = Rulesets.All;
        Assert.Contains(all, r => r.Id == "casual");
        Assert.Contains(all, r => r.Id == "ranked" && r.BanOne && r.LeadPick && r.BestOf == 3);
        foreach (var r in all) Assert.Equal(r.ToJson(), Ruleset.FromJson(r.ToJson()).ToJson());
        Assert.Throws<KeyError>(() => Ruleset.FromJson("{\"id\":\"x\",\"name\":\"x\",\"bestOf\":2}").Validate(BattleTests.LoadPack("original")));
    }

    [Fact]
    public void TeamCheckUsesTheRulesetSeason()
    {
        var db = BattleTests.LoadPack("original");
        var r = Ruleset.FromJson("{\"id\":\"t\",\"name\":\"测试\",\"season\":\"s1\",\"bestOf\":1}");
        Assert.NotEmpty(r.CheckTeam(db, new[] { "kei", "enka", "shiori", "lumi", "hane", "momo" }));
        Assert.Empty(r.CheckTeam(db, A));
    }

    [Fact]
    public void BestOfThreeEndsAfterTwoWins()
    {
        var db = BattleTests.LoadPack("original");
        var m = new Match(db, Rulesets.All.First(r => r.Id == "ranked"), new[] { A, B }, new[] { "甲", "乙" }, 9);
        int games = 0;
        while (!m.Over && games < 5)
        {
            var b = m.StartNextGame();
            PlayOut(b);
            m.ReportResult(b);
            games++;
        }
        Assert.True(m.Over);
        Assert.InRange(games, 2, 3);
        Assert.True(m.Wins.Max() == 2 || m.Wins.Sum() + m.Draws == games);
        Assert.Throws<System.InvalidOperationException>(() => m.StartNextGame());
    }
}
