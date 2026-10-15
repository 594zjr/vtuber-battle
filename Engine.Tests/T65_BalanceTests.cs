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
    public void EveryStreamerIsWithinRange(string pack)
    {
        var db = BattleTests.LoadPack(pack);
        var rates = BalanceCheck.WinRates(db, 60, 2026, AiLevel.Normal);
        var bad = rates.Where(kv => kv.Value < 0.30 || kv.Value > 0.70).Select(kv => kv.Key + "=" + kv.Value.ToString("P0")).ToList();
        Assert.True(bad.Count == 0, pack + " 胜率越界：" + string.Join("，", bad));
    }
}
