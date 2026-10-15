// T-39 验收测试：自动组队建议。实现后把本文件移到 Engine.Tests/ 下。
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T39_TeamBuilderTests
{
    [Fact]
    public void SuggestionIsLegalAndKeepsFixedPicks()
    {
        var db = BattleTests.LoadPack("original");
        for (uint seed = 1; seed <= 30; seed++)
        {
            var team = TeamBuilder.Suggest(db, "s1", new[] { "enka" }, seed);
            Assert.Equal(6, team.Count);
            Assert.Equal("enka", team[0]);
            Assert.Empty(TeamCheck.Validate(db, "s1", team));
        }
    }

    [Fact]
    public void SuggestionCoversManyTracksAndIsDeterministic()
    {
        var db = BattleTests.LoadPack("original");
        var a = TeamBuilder.Suggest(db, null, new string[0], 3);
        var b = TeamBuilder.Suggest(db, null, new string[0], 3);
        Assert.Equal(a, b);
        Assert.True(a.Select(id => db.Streamer(id).Type).Distinct().Count() >= 5);
    }

    [Fact]
    public void ImpossibleRequestReturnsErrors()
    {
        var db = BattleTests.LoadPack("original");
        // kei 在 s1 禁用池里
        var ex = Assert.Throws<System.InvalidOperationException>(() => TeamBuilder.Suggest(db, "s1", new[] { "kei" }, 1));
        Assert.Contains("禁用", ex.Message);
    }

    [Fact]
    public void WorksWithSmallPacks()
    {
        var db = BattleTests.LoadPack("fan_asoul_vr");
        var t = TeamBuilder.Suggest(db, null, new string[0], 1);
        Assert.Equal(6, t.Distinct().Count());
    }
}
