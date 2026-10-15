// T-44 验收测试：闯关模式规则层。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T44_CampaignTests
{
    static void Finish(Battle b, int playerSide, bool playerWins)
    {
        // 直接把输的一方全部下播，模拟打完
        int loser = playerWins ? 1 - playerSide : playerSide;
        int guard = 0;
        while (b.Phase != Phase.Over && guard++ < 50)
        {
            foreach (var f in b.Sides[loser].Team) if (!f.Fainted) f.Hp = 1;
            if (b.Phase == Phase.Choose)
            {
                var db = b.Db;
                db.Skills["test_finish"] = new SkillDef { Id = "test_finish", Name = "测试终结", Type = "tech", Cat = "status", Pp = 99, Prio = 9,
                    Effects = { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 99999 } } };
                b.Active(1 - loser).Skills[0] = new SkillSlot { Id = "test_finish", Pp = 99, Max = 99 };
                var win = BattleAction.Skill(0); var pass = BattleAction.Pass();
                b.ResolveTurn(loser == 1 ? win : pass, loser == 0 ? win : pass);
            }
            else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
        }
    }

    [Fact]
    public void OriginalPackShipsAnEightStageCampaign()
    {
        var db = BattleTests.LoadPack("original");
        var c = db.Campaigns["main"];
        Assert.Equal(8, c.Stages.Count);
        Assert.All(c.Stages, st => { Assert.Equal(6, st.Enemy.Count); Assert.False(string.IsNullOrEmpty(st.Intro)); });
        Assert.True(c.Stages.Select(s => s.Ai).Distinct().Count() >= 2);
    }

    [Fact]
    public void StagesUnlockInOrderAndProgressRoundTrips()
    {
        var db = BattleTests.LoadPack("original");
        var p = new CampaignProgress();
        Assert.True(CampaignRunner.IsUnlocked(db, p, "main", 0));
        Assert.False(CampaignRunner.IsUnlocked(db, p, "main", 1));
        var team = new[] { "enka", "shiori", "lumi", "hane", "momo", "nyako" };
        var b = CampaignRunner.Start(db, "main", 0, team, 1);
        Assert.Equal(team[0], b.Sides[0].Team[0].Def.Id);
        Assert.Equal(db.Campaigns["main"].Stages[0].Enemy[0], b.Sides[1].Team[0].Def.Id);
        Finish(b, 0, false);
        CampaignRunner.Complete(db, p, "main", 0, b);
        Assert.False(CampaignRunner.IsUnlocked(db, p, "main", 1));
        var b2 = CampaignRunner.Start(db, "main", 0, team, 2);
        Finish(b2, 0, true);
        CampaignRunner.Complete(db, p, "main", 0, b2);
        Assert.True(CampaignRunner.IsUnlocked(db, p, "main", 1));
        Assert.Equal(p.ToJson(), CampaignProgress.FromJson(p.ToJson()).ToJson());
        Assert.Throws<System.InvalidOperationException>(() => CampaignRunner.Start(db, "main", 3, team, 1));
    }
}
