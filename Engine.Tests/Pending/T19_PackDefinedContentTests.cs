// T-19 验收测试：内容包可以自带技能、人设、直播间效果、状态。实现后把本文件移到 Engine.Tests/ 下。
using System;
using System.IO;
using StreamDuel.Core;
using Xunit;

public class T19_PackDefinedContentTests
{
    static string Res(string rel) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", rel));

    // 在原创包 JSON 的开头插入额外字段
    static string PackWith(string extra)
    {
        var pack = Res(Path.Combine("Packs", "original.json"));
        int i = pack.IndexOf('{');
        return pack.Substring(0, i + 1) + extra + "," + pack.Substring(i + 1);
    }

    const string Extra = @"
      ""skills"": { ""pk_signature"": { ""name"": ""测试招牌"", ""type"": ""song"", ""cat"": ""talent"", ""power"": 90, ""pp"": 5,
                    ""effects"": [ { ""at"": ""post"", ""code"": ""FIELD"", ""target"": ""self"", ""field"": ""pk_glow"", ""turns"": 2 } ] } },
      ""fields"": { ""pk_glow"": { ""name"": ""测试光环"", ""kind"": ""turn"", ""dispel"": true, ""desc"": ""测试用"" } },
      ""statuses"": { ""pk_dizzy"": { ""name"": ""测试眩晕"", ""cat"": ""control"", ""skip"": 1, ""desc"": ""测试用"" } },
      ""personas"": { ""pk_persona"": { ""name"": ""测试人设"", ""code"": ""T_REGEN"", ""node"": ""回合结束"", ""params"": { ""frac"": 0.05 }, ""desc"": ""测试用"" } }";

    [Fact]
    public void PackDefinitionsAreLoadedIntoThatPackOnly()
    {
        var rules = Res("StreamDuelRules.json");
        var db = ContentDb.FromJson(rules, PackWith(Extra));
        Assert.True(db.Skills.ContainsKey("pk_signature"));
        Assert.True(db.Fields.ContainsKey("pk_glow"));
        Assert.True(db.Statuses.ContainsKey("pk_dizzy"));
        Assert.True(db.Personas.ContainsKey("pk_persona"));
        db.Validate();

        var plain = ContentDb.FromJson(rules, Res(Path.Combine("Packs", "original.json")));
        Assert.False(plain.Skills.ContainsKey("pk_signature"));
    }

    [Fact]
    public void PackSkillCanBeUsedInBattle()
    {
        var db = ContentDb.FromJson(Res("StreamDuelRules.json"), PackWith(Extra));
        db.Streamer("bell").Fifth = "pk_signature";
        db.Streamer("bell").Persona = "pk_persona";
        db.Validate();
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" } }, new[] { "你", "对手" }, 3);
        b.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());
        Assert.True(b.Active(0).HasField("pk_glow"));
    }

    [Fact]
    public void PackIdThatClashesWithRulesIsRejected()
    {
        var clash = @"""skills"": { ""warm_up"": { ""name"": ""冒名"", ""type"": ""song"", ""cat"": ""talent"", ""power"": 10, ""pp"": 5 } }";
        var ex = Assert.Throws<KeyError>(() => ContentDb.FromJson(Res("StreamDuelRules.json"), PackWith(clash)));
        Assert.Contains("warm_up", ex.Message);
    }
}
