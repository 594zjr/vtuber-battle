// T-05 验收测试：给界面用的伤害与先手预览。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T05_PreviewTests
{
    static Battle Make(uint seed)
    {
        var db = BattleTests.LoadPack("original");
        return new Battle(db, new System.Collections.Generic.IList<string>[] {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" } }, new[] { "你", "对手" }, seed);
    }

    [Fact]
    public void DamagePreviewIsARangeAndStatusSkillsAreZero()
    {
        var b = Make(1);
        int fullVolume = b.Active(0).Skills.FindIndex(x => x.Id == "full_volume");
        var p = b.PreviewDamage(0, fullVolume);
        Assert.True(p.Min > 0 && p.Min <= p.Max);
        Assert.Equal(1.0, p.Eff);                     // 歌回 打 硬核 = ×1
        int goLive = b.Active(0).Skills.FindIndex(x => x.Id == "go_live_early");
        var z = b.PreviewDamage(0, goLive);
        Assert.Equal(0, z.Min); Assert.Equal(0, z.Max);
    }

    [Fact]
    public void PreviewMaxCoversRealNonCritDamage()
    {
        // 预览不含暴击：Max 是随机 1.0、无暴击时的伤害，Min 是随机 0.85 时的伤害
        for (uint seed = 1; seed <= 30; seed++)
        {
            var b = Make(seed);
            int idx = b.Active(0).Skills.FindIndex(x => x.Id == "full_volume");
            var p = b.PreviewDamage(0, idx);
            int hp = b.Active(1).Hp;
            int from = b.Log.Count;
            b.ResolveTurn(BattleAction.Skill(idx), BattleAction.Pass());
            if (b.Log.Skip(from).Any(l => l.Text.Contains("没有命中") || l.Text.Contains("暴击"))) continue;
            int dealt = hp - b.Active(1).Hp;
            Assert.InRange(dealt, p.Min, p.Max);
        }
    }

    [Fact]
    public void PreviewDoesNotConsumeRandomness()
    {
        string Run(bool peek)
        {
            var b = Make(42);
            for (int t = 0; t < 10 && b.Phase == Phase.Choose; t++)
            {
                if (peek) { for (int i = 0; i < 5; i++) b.PreviewDamage(0, i); b.PreviewOrder(BattleAction.Skill(0), BattleAction.Skill(0)); }
                b.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
            }
            return string.Join("\n", b.Log.Select(l => l.Text));
        }
        Assert.Equal(Run(false), Run(true));
    }

    [Fact]
    public void OrderPreviewFollowsPriorityThenSpeed()
    {
        var b = Make(1);
        int warm = b.Active(0).Skills.FindIndex(x => x.Id == "warm_up");   // 先制 +2（第一回合人设再 +1）
        Assert.Equal(0, b.PreviewOrder(BattleAction.Skill(warm), BattleAction.Skill(0)).First);
        Assert.Equal(1, b.PreviewOrder(BattleAction.SwitchTo(1), BattleAction.Skill(0)).First); // 换人方不行动，另一方就是先手
        Assert.Contains("先制", b.PreviewOrder(BattleAction.Skill(warm), BattleAction.Skill(0)).Reason);
    }
}
