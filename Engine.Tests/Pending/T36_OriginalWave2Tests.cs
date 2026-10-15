// T-36 验收测试：原创主播第二期内容包。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T36_OriginalWave2Tests
{
    [Fact]
    public void Wave2PackIsCompleteAndOriginal()
    {
        var db = BattleTests.LoadPack("original_wave2");
        db.Validate();
        Assert.Equal(12, db.Streamers.Count);
        Assert.True(string.IsNullOrEmpty(db.PackNotice) || !db.PackNotice.Contains("同人"));
        var others = new[] { "original", "fan_asoul_vr", "fan_holo_niji" }.Select(BattleTests.LoadPack).ToList();
        var otherNames = others.SelectMany(o => o.Streamers.Select(s => s.Name)).ToHashSet();
        var otherIds = others.SelectMany(o => o.Streamers.Select(s => s.Id)).ToHashSet();
        foreach (var s in db.Streamers)
        {
            Assert.DoesNotContain(s.Name, otherNames);
            Assert.DoesNotContain(s.Id, otherIds);
            Assert.StartsWith("og2_", s.Fifth);
            Assert.StartsWith("og2_", s.Persona.Split('#').Last());
            Assert.InRange(s.Blurb.Length, 1, 40);
            Assert.True(s.Org == null || s.Org == "" || s.Org == "原创");
        }
        Assert.True(db.Streamers.Select(s => s.Type).Distinct().Count() == 8, "8 个赛道都要有人");
        Assert.True(db.Streamers.Count(s => s.Type2 != null) >= 3, "至少 3 位双赛道");
    }
}
