using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public sealed class T73_IntegrationReviewTests
{
    const string Delay = ContentDb.DelayedDamageFieldId;

    [Theory]
    [InlineData("single")]
    [InlineData("with_plain")]
    [InlineData("with_delay")]
    public void MixedDelayedContentValidatesAndActuallyResolvesClonesAndReplays(string combination)
    {
        var first = Load("a", delayed: true);
        var second = combination == "single" ? null : Load("b", delayed: combination == "with_delay", amount: 13);
        string firstHash = first.ContentHash(), secondHash = second?.ContentHash();
        first.Validate();
        second?.Validate();

        var mixed = second == null ? ContentDb.Merge(first) : ContentDb.Merge(first, second);

        mixed.Validate();
        mixed.Validate();
        Assert.Equal(1, mixed.Fields.Keys.Count(id => id == Delay));
        Assert.Equal("turn", mixed.Fields[Delay].Kind);
        Assert.True(mixed.Fields[Delay].Dispel);
        Assert.NotSame(first.Fields[Delay], mixed.Fields[Delay]);
        Assert.Equal(firstHash, first.ContentHash());
        if (second != null)
        {
            Assert.Equal(secondHash, second.ContentHash());
            Assert.Equal(mixed.ContentHash(), ContentDb.Merge(second, first).ContentHash());
            if (combination == "with_delay") Assert.NotSame(second.Fields[Delay], mixed.Fields[Delay]);
            else Assert.False(second.Fields.ContainsKey(Delay));
        }

        var battle = new Battle(mixed, new IList<string>[] { Team(first), Team(second ?? first) },
            new[] { "甲", "乙" }, 73);
        battle.ResolveTurn(BattleAction.Skill(0), combination == "with_delay" ? BattleAction.Skill(0) : BattleAction.Pass());
        Assert.Equal(37, battle.Active(1).FieldOf(Delay).PendingDamage);
        Assert.Equal(1, battle.Active(1).FieldOf(Delay).Turns);
        Assert.Equal(300, battle.Active(1).Hp);
        var clone = battle.Clone();
        Assert.NotSame(battle.Active(1).FieldOf(Delay), clone.Active(1).FieldOf(Delay));
        Assert.Equal(battle.StateHash(), clone.StateHash());

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        clone.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(263, battle.Active(1).Hp);
        Assert.Equal(combination == "with_delay" ? 287 : 300, battle.Active(0).Hp);
        Assert.Null(battle.Active(0).FieldOf(Delay));
        Assert.Null(battle.Active(1).FieldOf(Delay));
        var replay = Replay.Run(mixed, BattleRecord.FromJson(battle.Record.ToJson()));
        foreach (var equivalent in new[] { clone, replay })
        {
            Assert.Equal(battle.StateHash(), equivalent.StateHash());
            Assert.Equal(battle.Record.ToJson(), equivalent.Record.ToJson());
            Assert.Equal(Serialize(battle.Log), Serialize(equivalent.Log));
            Assert.Equal(Serialize(battle.Events), Serialize(equivalent.Events));
            Assert.Equal(battle.Clone().Random(), equivalent.Clone().Random());
        }
    }

    [Theory]
    [InlineData(null, "到期提醒", "到期提醒")]
    [InlineData("到期提醒", null, "到期提醒")]
    [InlineData("到期提醒", "到期提醒", "到期提醒")]
    public void GeneratedFieldUsesTheSameExplicitAliasRulesAsSharedFields(string firstAlias, string secondAlias, string expected)
    {
        var first = Load("a", delayed: true, alias: firstAlias);
        var second = Load("b", delayed: true, alias: secondAlias);
        string firstHash = first.ContentHash(), secondHash = second.ContentHash();

        var mixed = ContentDb.Merge(first, second);
        var reversed = ContentDb.Merge(second, first);

        mixed.Validate();
        reversed.Validate();
        Assert.Equal(expected, mixed.Fields[Delay].Name);
        Assert.Equal(mixed.ContentHash(), reversed.ContentHash());
        Assert.Equal(firstAlias ?? "延迟伤害", first.Fields[Delay].Name);
        Assert.Equal(secondAlias ?? "延迟伤害", second.Fields[Delay].Name);
        Assert.Equal(firstHash, first.ContentHash());
        Assert.Equal(secondHash, second.ContentHash());
    }

    [Theory]
    [InlineData("甲方提醒", "乙方提醒")]
    [InlineData("延迟伤害", "乙方提醒")]
    public void ConflictingExplicitAliasesAreRejectedInEitherOrder(string firstAlias, string secondAlias)
    {
        var first = Load("a", delayed: true, alias: firstAlias);
        var second = Load("b", delayed: true, alias: secondAlias);
        foreach (var sources in new[] { new[] { first, second }, new[] { second, first } })
        {
            var error = Assert.Throws<KeyError>(() => ContentDb.Merge(sources));
            Assert.Contains("显示名冲突", error.Message);
            Assert.Contains(Delay, error.Message);
        }
    }

    [Fact]
    public void ChangingAnExplicitAliasAfterLoadingCannotSilentlyChangeTheMergeContract()
    {
        var first = Load("a", delayed: true, alias: "到期提醒");
        first.Fields[Delay].Name = "悄悄改名";

        var error = Assert.Throws<KeyError>(() => ContentDb.Merge(first));

        Assert.Contains("显示名已变更", error.Message);
        Assert.Contains(Delay, error.Message);
    }

    [Fact]
    public void GeneratedDefinitionAndEffectPayloadAreIndependentOfBothSources()
    {
        var first = Load("a", delayed: true);
        var second = Load("b", delayed: true);
        var mixed = ContentDb.Merge(first, second);
        Assert.NotSame(first.Fields[Delay], mixed.Fields[Delay]);
        Assert.NotSame(second.Fields[Delay], mixed.Fields[Delay]);

        mixed.Fields[Delay].Name = "混合显示";
        mixed.Skills["a_skill0"].Effects[0].N = 91;
        Assert.Equal("延迟伤害", first.Fields[Delay].Name);
        Assert.Equal("延迟伤害", second.Fields[Delay].Name);
        Assert.Equal(37, first.Skills["a_skill0"].Effects[0].N);
        string mixedHash = mixed.ContentHash();

        first.Fields[Delay].Name = "来源显示";
        second.Skills["b_skill0"].Effects[0].Turns = 5;
        Assert.Equal("混合显示", mixed.Fields[Delay].Name);
        Assert.Equal(2, mixed.Skills["b_skill0"].Effects[0].Turns);
        Assert.Equal(mixedHash, mixed.ContentHash());
    }

    [Theory]
    [InlineData("Dispel")]
    [InlineData("When")]
    [InlineData("Dot")]
    public void DifferentGeneratedDefinitionPropertiesAreRejectedEvenWithMatchingExplicitAliases(string property)
    {
        var first = Load("a", delayed: true, alias: "到期提醒");
        var second = Load("b", delayed: true, alias: "到期提醒");
        if (property == "Dispel") second.Fields[Delay].Dispel = false;
        else if (property == "When") second.Fields[Delay].When = "测试说明变更";
        else second.Fields[Delay].Dot = 0.125;
        first.Validate();
        second.Validate();

        foreach (var sources in new[] { new[] { first, second }, new[] { second, first } })
        {
            var error = Assert.Throws<KeyError>(() => ContentDb.Merge(sources));
            Assert.Contains("规则不一致", error.Message);
            Assert.Contains(Delay, error.Message);
        }
    }

    [Fact]
    public void MatchingGeneratedDefinitionChangesArePreservedWithoutAffectingTheSharedRulesBaseline()
    {
        var first = Load("a", delayed: true, alias: "到期提醒");
        var second = Load("b", delayed: true);
        var plain = Load("c", delayed: false);
        first.Fields[Delay].When = second.Fields[Delay].When = "共同说明";
        first.Fields[Delay].Dot = second.Fields[Delay].Dot = 0.125;
        string firstHash = first.ContentHash(), secondHash = second.ContentHash(), plainHash = plain.ContentHash();

        var mixed = ContentDb.Merge(first, second, plain);

        mixed.Validate();
        Assert.Equal("共同说明", mixed.Fields[Delay].When);
        Assert.Equal(0.125, mixed.Fields[Delay].Dot);
        Assert.Equal("到期提醒", mixed.Fields[Delay].Name);
        Assert.Equal(mixed.ContentHash(), ContentDb.Merge(plain, second, first).ContentHash());
        Assert.Equal(firstHash, first.ContentHash());
        Assert.Equal(secondHash, second.ContentHash());
        Assert.Equal(plainHash, plain.ContentHash());
    }

    [Fact]
    public void SingleSourceGeneratedDefinitionRemainsPresentAndIsAnIndependentCopy()
    {
        var source = Load("a", delayed: true, alias: "到期提醒");
        source.Fields[Delay].When = "来源说明";
        string sourceHash = source.ContentHash();

        var mixed = ContentDb.Merge(source);

        Assert.True(source.Fields.ContainsKey(Delay));
        Assert.NotSame(source.Fields[Delay], mixed.Fields[Delay]);
        Assert.Equal(sourceHash, source.ContentHash());
        mixed.Fields[Delay].When = "结果说明";
        Assert.Equal("来源说明", source.Fields[Delay].When);
        source.Fields[Delay].Desc = "之后修改来源";
        Assert.NotEqual(source.Fields[Delay].Desc, mixed.Fields[Delay].Desc);
        source.Validate();
        mixed.Validate();
    }

    [Fact]
    public void AnUndeclaredNameChangeCannotBorrowAnotherSourcesExplicitAlias()
    {
        var first = Load("a", delayed: true, alias: "到期提醒");
        var second = Load("b", delayed: true);
        second.Fields[Delay].Name = "到期提醒";

        var error = Assert.Throws<KeyError>(() => ContentDb.Merge(first, second));

        Assert.Contains("规则不一致", error.Message);
        Assert.Contains(Delay, error.Message);
    }

    [Fact]
    public void ReplacingTheGeneratedDefinitionAfterMergeStillViolatesTheReservedIdRule()
    {
        var mixed = ContentDb.Merge(Load("a", delayed: true));
        var old = mixed.Fields[Delay];
        mixed.Fields[Delay] = new FieldDef
        {
            Id = old.Id, Name = old.Name, Kind = old.Kind, When = old.When,
            Desc = old.Desc, Dispel = old.Dispel
        };

        var error = Assert.Throws<KeyError>(() => mixed.Validate());

        Assert.Contains("保留", error.Message);
        Assert.Contains(Delay, error.Message);
    }

    [Fact]
    public void GeneratedFieldDeduplicationDoesNotAllowRealPrivateFieldCollisions()
    {
        var first = Load("a", delayed: true, extraField: "private_collision");
        var second = Load("b", delayed: true, extraField: "private_collision");

        var error = Assert.Throws<KeyError>(() => ContentDb.Merge(first, second));

        Assert.Contains("直播间效果 id 冲突", error.Message);
        Assert.Contains("private_collision", error.Message);
    }

    [Fact]
    public void ADelayInSharedStruggleAloneRemainsMergeable()
    {
        var rules = JsonSerializer.Deserialize<Dictionary<string, object>>(RulesJson());
        rules["struggle"] = Skill(delayed: true, amount: 37);
        string rulesJson = JsonSerializer.Serialize(rules);
        var first = Load("a", delayed: false, rules: rulesJson);
        var second = Load("b", delayed: false, rules: rulesJson);

        var mixed = ContentDb.Merge(first, second);

        mixed.Validate();
        Assert.Equal("DELAYED_DAMAGE", mixed.Struggle.Effects.Single().Code);
        Assert.Equal(1, mixed.Fields.Keys.Count(id => id == Delay));
        Assert.True(mixed.Fields[Delay].Dispel);
        Assert.Equal(mixed.ContentHash(), ContentDb.Merge(second, first).ContentHash());
    }

    [Fact]
    public void PlainContentDoesNotGainAnUnusedGeneratedDefinitionDuringValidationOrMerge()
    {
        var first = Load("a", delayed: false);
        var second = Load("b", delayed: false);
        string firstHash = first.ContentHash(), secondHash = second.ContentHash();
        first.Validate();
        second.Validate();

        var mixed = ContentDb.Merge(first, second);
        mixed.Validate();

        Assert.False(first.Fields.ContainsKey(Delay));
        Assert.False(second.Fields.ContainsKey(Delay));
        Assert.False(mixed.Fields.ContainsKey(Delay));
        Assert.Equal(firstHash, first.ContentHash());
        Assert.Equal(secondHash, second.ContentHash());
        Assert.Equal(mixed.ContentHash(), ContentDb.Merge(second, first).ContentHash());
    }

    static string[] Team(ContentDb db) { return db.Streamers.Select(streamer => streamer.Id).ToArray(); }
    static string Serialize(object value) { return JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true }); }
    static string RulesJson() { return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", "StreamDuelRules.json")); }

    static object Skill(bool delayed, int amount)
    {
        object[] effects = delayed ? new object[]
        {
            new { code = "DELAYED_DAMAGE", at = "post", target = "foe", turns = 2, n = amount }
        } : Array.Empty<object>();
        return new { name = "演示技能", type = "talk", cat = "status", power = 0, pp = 10, effects };
    }

    static ContentDb Load(string id, bool delayed, string alias = null, int amount = 37, string extraField = null, string rules = null)
    {
        var skills = Enumerable.Range(0, 5).ToDictionary(index => id + "_skill" + index,
            index => Skill(delayed && index == 0, amount));
        var pack = new Dictionary<string, object>
        {
            ["id"] = "t73_" + id, ["name"] = "混合验收" + id, ["notice"] = "独立测试内容",
            ["skills"] = skills,
            ["personas"] = new Dictionary<string, object>
            {
                [id + "_neutral"] = new { name = "测试人设", code = "T_LOWHP_POWER", @params = new { below = 0, mul = 1 } }
            },
            ["streamers"] = Enumerable.Range(0, 6).Select(index => new
            {
                id = id + "_unit" + index, name = "演示主播" + index, type = "talk", persona = id + "_neutral",
                stats = new { hp = 300, atk = 100, def = 100, spa = 100, sdef = 100, spd = 120 - index },
                skills = Enumerable.Range(0, 4).Select(slot => id + "_skill" + slot).ToArray(), fifth = id + "_skill4",
                blurb = "混合内容回归验收"
            }).ToArray()
        };
        if (alias != null) pack["fieldNames"] = new Dictionary<string, string> { [Delay] = alias };
        if (extraField != null) pack["fields"] = new Dictionary<string, object>
        {
            [extraField] = new { name = "私有测试印记", kind = "mark", dispel = false, maxStacks = 3 }
        };
        return ContentDb.FromJson(rules ?? RulesJson(), JsonSerializer.Serialize(pack));
    }
}
