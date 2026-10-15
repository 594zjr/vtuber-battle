// T-20 验收测试：A-SOUL × VirtuaReal 每位主播有独有人设和招牌技能。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T20_AsoulVrSignatureTests
{
    [Fact]
    public void EveryStreamerHasOwnPersonaAndSignatureSkill()
    {
        var db = BattleTests.LoadPack("fan_asoul_vr");
        var plain = BattleTests.LoadPack("original");
        db.Validate();
        Assert.Equal(7, db.Streamers.Count);
        foreach (var s in db.Streamers)
        {
            var basePersona = s.Persona.Split('#').Last();
            Assert.StartsWith("av_", basePersona);
            Assert.StartsWith("av_", s.Fifth);
            Assert.False(plain.Skills.ContainsKey(s.Fifth));      // 招牌技能只在这个包里
            Assert.False(plain.Personas.ContainsKey(basePersona));
            Assert.InRange(s.Blurb.Length, 1, 40);
        }
        Assert.Equal(7, db.Streamers.Select(s => s.Persona.Split('#').Last()).Distinct().Count());
        Assert.Equal(7, db.Streamers.Select(s => s.Fifth).Distinct().Count());
    }

    [Fact]
    public void SignatureEffectsAllDeclareTimingAndDispel()
    {
        var db = BattleTests.LoadPack("fan_asoul_vr");
        foreach (var s in db.Streamers)
            foreach (var e in db.Skills[s.Fifth].Effects)
            {
                Assert.True(e.At == "pre" || e.At == "post");
                var t = Describe.Effect(db, e);
                Assert.False(string.IsNullOrEmpty(t.Text));
                Assert.False(string.IsNullOrEmpty(t.Dispel));
            }
    }
}
