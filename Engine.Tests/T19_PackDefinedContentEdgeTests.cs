using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T19_PackDefinedContentEdgeTests
{
    const string Definitions = @"{
      ""skills"": { ""pk_signature"": { ""name"": ""测试招牌"", ""type"": ""song"", ""cat"": ""interact"", ""power"": 10, ""pp"": 5,
        ""effects"": [
          { ""at"": ""post"", ""code"": ""STATUS"", ""target"": ""foe"", ""status"": ""pk_static"" },
          { ""at"": ""post"", ""code"": ""FIELD"", ""target"": ""foe"", ""field"": ""pk_glow"", ""turns"": 3 }
        ] } },
      ""personas"": {
        ""pk_entry"": { ""name"": ""测试登场"", ""code"": ""T_ENTER_FIELD"", ""node"": ""登场时"", ""field"": ""pk_glow"", ""params"": { ""turns"": 3 } },
        ""pk_contact"": { ""name"": ""测试互动"", ""code"": ""T_CONTACT_STATUS"", ""node"": ""被击中后"", ""status"": ""pk_static"", ""params"": { ""chance"": 100 } }
      },
      ""fields"": { ""pk_glow"": { ""name"": ""测试光环"", ""kind"": ""turn"", ""dispel"": true, ""prio"": 1 } },
      ""statuses"": { ""pk_static"": { ""name"": ""测试杂音"", ""cat"": ""weak"", ""turns"": 3, ""dot"": 0.05, ""cleanse"": true } }
    }";

    static string Res(string relative)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", relative));
    }

    static JsonObject OriginalPack()
    {
        return JsonNode.Parse(Res(Path.Combine("Packs", "original.json"))).AsObject();
    }

    static JsonObject Streamer(JsonObject pack, string id)
    {
        return pack["streamers"].AsArray().OfType<JsonObject>().Single(s => s["id"].GetValue<string>() == id);
    }

    static JsonObject DefinedPack(bool definitionsFirst = false)
    {
        var pack = OriginalPack();
        Streamer(pack, "bell")["fifth"] = "pk_signature";
        Streamer(pack, "bell")["persona"] = "pk_entry";
        Streamer(pack, "enka")["persona"] = "pk_contact";
        var definitions = JsonNode.Parse(Definitions).AsObject();
        var result = new JsonObject();
        var first = definitionsFirst ? definitions : pack;
        var second = definitionsFirst ? pack : definitions;
        foreach (var item in first) result.Add(item.Key, item.Value.DeepClone());
        foreach (var item in second) result.Add(item.Key, item.Value.DeepClone());
        return result;
    }

    static ContentDb Load(JsonObject pack)
    {
        return ContentDb.FromJson(Res("StreamDuelRules.json"), pack.ToJsonString());
    }

    [Theory]
    [InlineData("skills", "warm_up")]
    [InlineData("personas", "punctual")]
    [InlineData("fields", "shield")]
    [InlineData("statuses", "stall")]
    public void EveryDefinitionCategoryRejectsRuleIdCollisions(string category, string id)
    {
        var pack = OriginalPack();
        var rules = JsonNode.Parse(Res("StreamDuelRules.json"));
        pack[category] = new JsonObject { [id] = rules[category][id].DeepClone() };

        var error = Assert.Throws<KeyError>(() => Load(pack));

        Assert.Contains(id, error.Message);
        Assert.Contains(category, error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefinitionsCanFollowOrPrecedeStreamersAndWorkTogetherInBattle(bool definitionsFirst)
    {
        var db = Load(DefinedPack(definitionsFirst));
        var battle = new Battle(db, new IList<string>[]
        {
            new[] { "bell", "shiori", "lumi", "hane", "momo", "kei" },
            new[] { "enka", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "你", "对手" }, 3);

        Assert.True(battle.Active(0).HasField("pk_glow"));
        Assert.False(battle.Active(1).HasField("pk_glow"));
        battle.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());

        Assert.True(battle.Active(0).HasStatus("pk_static"));
        Assert.True(battle.Active(1).HasStatus("pk_static"));
        Assert.True(battle.Active(1).HasField("pk_glow"));
    }

    [Fact]
    public void RenamesAndNestedMutationsStayInsideOneDatabase()
    {
        var plain = Load(OriginalPack());
        var independent = Load(DefinedPack());
        var pack = DefinedPack();
        pack["skillNames"] = new JsonObject { ["pk_signature"] = "本包技能名", ["warm_up"] = "本包开嗓" };
        pack["fieldNames"] = new JsonObject { ["pk_glow"] = "本包效果名", ["shield"] = "本包护盘" };
        pack["statusNames"] = new JsonObject { ["pk_static"] = "本包状态名", ["stall"] = "本包卡麦" };
        Streamer(pack, "bell")["personaName"] = "本包登场名";
        var renamed = Load(pack);

        Assert.Equal("本包技能名", renamed.Skills["pk_signature"].Name);
        Assert.Equal("本包效果名", renamed.Fields["pk_glow"].Name);
        Assert.Equal("本包状态名", renamed.Statuses["pk_static"].Name);
        Assert.Equal("本包登场名", renamed.Personas[renamed.Streamer("bell").Persona].Name);
        renamed.Skills["pk_signature"].Effects[0].Status = "stall";
        renamed.Personas["pk_entry"].Params["turns"] = 99;
        renamed.Fields["pk_glow"].Prio = 99;
        renamed.Statuses["pk_static"].Turns = 99;

        Assert.Equal("测试招牌", independent.Skills["pk_signature"].Name);
        Assert.Equal("pk_static", independent.Skills["pk_signature"].Effects[0].Status);
        Assert.Equal("测试登场", independent.Personas["pk_entry"].Name);
        Assert.Equal(3, independent.Personas["pk_entry"].P("turns"));
        Assert.Equal("测试光环", independent.Fields["pk_glow"].Name);
        Assert.Equal(1, independent.Fields["pk_glow"].Prio);
        Assert.Equal("测试杂音", independent.Statuses["pk_static"].Name);
        Assert.Equal(3, independent.Statuses["pk_static"].Turns);
        Assert.Equal(3, renamed.Personas[renamed.Streamer("bell").Persona].P("turns"));
        Assert.Equal(plain.Skills["warm_up"].Name, independent.Skills["warm_up"].Name);
        Assert.Equal(plain.Fields["shield"].Name, independent.Fields["shield"].Name);
        Assert.Equal(plain.Statuses["stall"].Name, independent.Statuses["stall"].Name);
        Assert.NotEqual(plain.Skills["warm_up"].Name, renamed.Skills["warm_up"].Name);
        Assert.NotEqual(plain.Fields["shield"].Name, renamed.Fields["shield"].Name);
        Assert.NotEqual(plain.Statuses["stall"].Name, renamed.Statuses["stall"].Name);
        Assert.False(plain.Skills.ContainsKey("pk_signature"));
        Assert.False(plain.Personas.ContainsKey("pk_entry"));
        Assert.False(plain.Fields.ContainsKey("pk_glow"));
        Assert.False(plain.Statuses.ContainsKey("pk_static"));
    }

    [Theory]
    [InlineData("type", "赛道")]
    [InlineData("status", "状态")]
    [InlineData("field", "效果")]
    [InlineData("at", "at")]
    public void InvalidPackSkillReferencesOrMissingTimingAreRejected(string property, string label)
    {
        var pack = DefinedPack();
        var skill = pack["skills"]["pk_signature"];
        if (property == "type") skill["type"] = "pk_missing";
        else if (property == "status") skill["effects"][0]["status"] = "pk_missing";
        else if (property == "field") skill["effects"][1]["field"] = "pk_missing";
        else skill["effects"][0].AsObject().Remove("at");

        var error = Assert.Throws<KeyError>(() => Load(pack));

        Assert.Contains("pk_signature", error.Message);
        Assert.Contains(label, error.Message);
        if (property != "at") Assert.Contains("pk_missing", error.Message);
    }

    [Theory]
    [InlineData("pk_entry", "field", "效果", true)]
    [InlineData("pk_entry", "field", "效果", false)]
    [InlineData("pk_contact", "status", "状态", true)]
    [InlineData("pk_contact", "status", "状态", false)]
    public void InvalidPersonaReferencesAreRejectedEvenWhenNoStreamerUsesThem(string id, string property, string label, bool used)
    {
        var pack = DefinedPack();
        pack["personas"][id][property] = "pk_missing";
        if (!used)
        {
            Streamer(pack, "bell")["persona"] = "punctual";
            Streamer(pack, "enka")["persona"] = "comeback";
        }

        var error = Assert.Throws<KeyError>(() => Load(pack));

        Assert.Contains(id, error.Message);
        Assert.Contains("pk_missing", error.Message);
        Assert.Contains(label, error.Message);
    }

    [Fact]
    public void PublicValidationRechecksUnusedPersonaReferencesAfterChanges()
    {
        var db = Load(DefinedPack());
        db.Streamer("bell").Persona = "punctual";
        db.Personas["pk_entry"].Field = "missing_after_load";

        var error = Assert.Throws<KeyError>(() => db.Validate());

        Assert.Contains("pk_entry", error.Message);
        Assert.Contains("missing_after_load", error.Message);
    }

    [Theory]
    [InlineData("skills", "pk_signature", "STATUS", "status")]
    [InlineData("skills", "pk_signature", "FIELD", "field")]
    [InlineData("skills", "pk_signature", "ADD_MARK", "field")]
    [InlineData("skills", "pk_signature", "POWER_PER_MARK", "field")]
    [InlineData("personas", "pk_contact", "T_CONTACT_STATUS", "status")]
    [InlineData("personas", "pk_entry", "T_ENTER_FIELD", "field")]
    public void ReferenceUsingCodesCannotOmitTheirRequiredReference(string category, string id, string code, string property)
    {
        var pack = DefinedPack();
        var definition = pack[category][id];
        var referenceOwner = category == "skills" ? definition["effects"][0].AsObject() : definition.AsObject();
        referenceOwner["code"] = code;
        if (category == "skills") referenceOwner["at"] = "pre";
        referenceOwner.Remove(property);

        var error = Assert.Throws<KeyError>(() => Load(pack));

        Assert.Contains(id, error.Message);
        Assert.Contains(property, error.Message);
    }

    [Fact]
    public void PersonaDisplayCopyCannotOverwriteAnExplicitPackPersona()
    {
        var pack = DefinedPack();
        Streamer(pack, "bell")["personaName"] = "专属显示名";
        pack["personas"]["bell#pk_entry"] = pack["personas"]["pk_contact"].DeepClone();

        var error = Assert.Throws<KeyError>(() => Load(pack));

        Assert.Contains("bell#pk_entry", error.Message);
        Assert.Contains("personas", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersonaDisplayCopiesPreserveTheirSourceMarker(bool packDefined)
    {
        var pack = packDefined ? DefinedPack() : OriginalPack();
        var baseId = packDefined ? "pk_entry" : "punctual";
        Streamer(pack, "bell")["personaName"] = "专属显示名";
        var db = Load(pack);
        var persona = db.Personas[db.Streamer("bell").Persona];

        Assert.NotEqual(baseId, persona.Id);
        Assert.Equal("专属显示名", persona.Name);
        Assert.Equal(packDefined, persona.PackDefined);
        Assert.Equal(packDefined, db.Personas[baseId].PackDefined);
        Assert.False(db.Skills["warm_up"].PackDefined);
        if (packDefined) Assert.True(db.Skills["pk_signature"].PackDefined);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("fan_asoul_vr")]
    [InlineData("fan_holo_niji")]
    public void ExistingPackagesRemainValidWithoutExtraDefinitionObjects(string packId)
    {
        var pack = JsonNode.Parse(Res(Path.Combine("Packs", packId + ".json"))).AsObject();
        var db = Load(pack);

        db.Validate();
        Assert.Equal(packId, db.PackId);
        Assert.Equal(pack["streamers"].AsArray().Count, db.Streamers.Count);
        Assert.All(db.Skills.Values, skill => Assert.False(skill.PackDefined));
        Assert.All(db.Personas.Values, persona => Assert.False(persona.PackDefined));
    }
}
