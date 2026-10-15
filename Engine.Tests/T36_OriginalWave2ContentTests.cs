using System;
using System.Linq;
using Xunit;

public class T36_OriginalWave2ContentTests
{
    [Fact]
    public void EachStreamerHasIndependentPackDefinedPersonaAndFifthSkill()
    {
        var db = BattleTests.LoadPack("original_wave2");

        Assert.Equal("original_wave2", db.PackId);
        Assert.Equal("原创主播·第二期", db.PackName);
        Assert.Equal(12, db.Streamers.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(12, db.Streamers.Select(s => s.Persona.Split('#').Last()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(12, db.Streamers.Select(s => s.Fifth).Distinct(StringComparer.Ordinal).Count());
        foreach (var streamer in db.Streamers)
        {
            var persona = db.Personas[streamer.Persona];
            var fifth = db.Skills[streamer.Fifth];
            Assert.StartsWith("og2_", persona.Id.Split('#').Last());
            Assert.StartsWith("og2_", fifth.Id);
            Assert.True(persona.PackDefined, streamer.Name + " 的人设必须在本包定义");
            Assert.True(fifth.PackDefined, streamer.Name + " 的第五技能必须在本包定义");
        }
    }

    [Fact]
    public void EveryFourSkillCombinationIsDifferentRegardlessOfSlotOrder()
    {
        var db = BattleTests.LoadPack("original_wave2");
        foreach (var streamer in db.Streamers)
        {
            Assert.Equal(4, streamer.Skills.Distinct(StringComparer.Ordinal).Count());
        }

        var combinations = db.Streamers
            .Select(s => string.Join("\n", s.Skills.OrderBy(id => id, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal);

        Assert.Equal(12, combinations.Count());
    }
}
