// T-65 验收测试：数值平衡。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T65_BalanceTests
{
    [Fact]
    public void WinRatesAreDeterministic()
    {
        var db = BattleTests.LoadPack("original");
        var a = BalanceCheck.WinRates(db, 10, 1, AiLevel.Normal);
        var b = BalanceCheck.WinRates(db, 10, 1, AiLevel.Normal);
        Assert.Equal(db.Streamers.Count, a.Count);
        foreach (var kv in a) Assert.Equal(kv.Value, b[kv.Key]);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("original_wave2")]
    [InlineData("fan_asoul_vr")]
    [InlineData("fan_holo_niji")]
    [InlineData("original_wave3")]
    public void EveryStreamerIsWithinRange(string pack)
    {
        var db = BattleTests.LoadPack(pack);
        // 小样本不能代表平衡结论；使用报告相同的固定样本量和正式验收区间。
        var rates = BalanceCheck.WinRates(db, 5000, 2026, AiLevel.Normal);
        var bad = rates.Where(kv => kv.Value < 0.35 || kv.Value > 0.65).Select(kv => kv.Key + "=" + kv.Value.ToString("P0")).ToList();
        Assert.True(bad.Count == 0, pack + " 胜率越界：" + string.Join("，", bad));
    }
}
