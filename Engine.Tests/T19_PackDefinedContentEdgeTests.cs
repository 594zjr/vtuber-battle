using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T19_PackDefinedContentEdgeTests
{
    // T-20 新增包内定义后，旧格式兼容性仍需使用变更前的固定输入。
    const string LegacyAsoulVrPack = @"{
  ""id"": ""fan_asoul_vr"",
  ""name"": ""同人包：A-SOUL × VirtuaReal"",
  ""notice"": ""非官方同人作品（个人二次创作），免费、非商业，与 A-SOUL 及其运营方、VirtuaReal 及各位主播本人无关。A-SOUL 成员依据《二创规则说明5.0》收录；VirtuaReal 未见公开的二创规范，按同人惯例收录，公司或本人提出即下架。只使用公开艺名和公开人设，形象为自制占位图形，不使用任何官方立绘、模型、音频或标志。"",
  ""_readme"": ""每位主播复用一套原创技能组；赛道只是玩法分配，不代表主播本人的直播内容。personaName/personaDesc 只改人设显示名和说明，不改机制。只写公开人设，不写现实事件、中之人或负面内容。名单只收 2026-10 仍在活动的成员。"",
  ""skillNames"": { ""stir_drama"": ""热场"", ""rage_collab"": ""激情连麦"", ""delete_save"": ""全力一击"", ""dig_history"": ""回放轰炸"", ""thick_skin"": ""沉着应对"", ""yolo_rush"": ""全力冲锋"", ""pull_cable"": ""强制断线"", ""hex"": ""压迫感"", ""grudge_hit"": ""重击"" },
  ""fieldNames"": { ""blackhist"": ""循环回放"" },
  ""statusNames"": { ""flame"": ""过热"", ""hater"": ""刷屏干扰"" },
  ""streamers"": [
    { ""id"": ""bella"",   ""name"": ""贝拉"",       ""org"": ""A-SOUL"",     ""type"": ""song"", ""stats"": { ""hp"": 340, ""atk"": 220, ""def"": 220, ""spa"": 340, ""sdef"": 230, ""spd"": 360 }, ""persona"": ""punctual"",   ""personaName"": ""舞台中心"", ""skills"": [""warm_up"", ""full_volume"", ""go_live_early"", ""noise_cancel""], ""fifth"": ""high_note"", ""blurb"": ""A-SOUL 队长，舞蹈担当，一上场就抢节奏。"" },
    { ""id"": ""diana"",   ""name"": ""嘉然"",       ""org"": ""A-SOUL"",     ""type"": ""meme"", ""stats"": { ""hp"": 390, ""atk"": 280, ""def"": 260, ""spa"": 260, ""sdef"": 260, ""spd"": 300 }, ""persona"": ""teamspirit"", ""personaName"": ""嘉心糖应援"", ""skills"": [""meme_punch"", ""cat_walk"", ""lucky_draw"", ""sudden_cute""], ""fifth"": ""iconic_scene"", ""blurb"": ""A-SOUL 成员，粉丝叫「嘉心糖」，有应援在就不知疲倦。"" },
    { ""id"": ""eileen"",  ""name"": ""乃琳"",       ""org"": ""A-SOUL"",     ""type"": ""talk"", ""stats"": { ""hp"": 420, ""atk"": 230, ""def"": 270, ""spa"": 290, ""sdef"": 290, ""spd"": 240 }, ""persona"": ""family"",     ""personaName"": ""温柔大姐姐"", ""skills"": [""roast_chain"", ""siphon_fans"", ""lullaby_talk"", ""ride_trend""], ""fifth"": ""captain_renew"", ""blurb"": ""A-SOUL 成员，温柔沉稳，队友越多越安心。"" },
    { ""id"": ""nana7mi"", ""name"": ""七海Nana7mi"", ""org"": ""VirtuaReal"", ""type"": ""game"", ""stats"": { ""hp"": 380, ""atk"": 330, ""def"": 250, ""spa"": 220, ""sdef"": 240, ""spd"": 320 }, ""persona"": ""comeback"",   ""personaName"": ""鲨鱼反咬"", ""skills"": [""aim_burst"", ""quick_mic"", ""yolo_rush"", ""pregame""], ""fifth"": ""endgame"", ""blurb"": ""VirtuaReal 2 期成员，鲨鱼人设，越到残局越凶。"" },
    { ""id"": ""koichi"",  ""name"": ""中单光一"",   ""org"": ""VirtuaReal"", ""type"": ""game"", ""stats"": { ""hp"": 470, ""atk"": 270, ""def"": 340, ""spa"": 240, ""sdef"": 280, ""spd"": 150 }, ""persona"": ""trollback"",  ""personaName"": ""稳住中路"", ""personaDesc"": ""被互动类技能击中后，30% 使攻击方「卡麦」。"", ""personaStatus"": ""stall"", ""skills"": [""rage_collab"", ""stir_drama"", ""thick_skin"", ""map_cannon""], ""fifth"": ""delete_save"", ""blurb"": ""VirtuaReal 2 期成员，游戏主播，皮厚耐打。"" },
    { ""id"": ""aza"",     ""name"": ""阿萨Aza"",    ""org"": ""VirtuaReal"", ""type"": ""asmr"", ""stats"": { ""hp"": 460, ""atk"": 260, ""def"": 300, ""spa"": 250, ""sdef"": 300, ""spd"": 180 }, ""persona"": ""healing"",    ""personaName"": ""细水长流"", ""personaDesc"": ""回合结束时回复 1/10 最大人气。"", ""skills"": [""whisper_tide"", ""wave_mic"", ""deep_breath"", ""mod_guard""], ""fifth"": ""late_reply"", ""blurb"": ""VirtuaReal 6 期成员，耐打，慢慢回血。"" },
    { ""id"": ""eine"",    ""name"": ""艾因Eine"",   ""org"": ""VirtuaReal"", ""type"": ""art"",  ""stats"": { ""hp"": 400, ""atk"": 200, ""def"": 250, ""spa"": 320, ""sdef"": 320, ""spd"": 270 }, ""persona"": ""pure"",       ""personaName"": ""一期前辈"", ""skills"": [""sketch"", ""redraw"", ""thick_paint"", ""one_stroke""], ""fifth"": ""final_draft"", ""blurb"": ""VirtuaReal 1 期成员，稳重，会自我调整。"" }
  ]
}
";

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
        string packJson = packId == "fan_asoul_vr" ? LegacyAsoulVrPack : Res(Path.Combine("Packs", packId + ".json"));
        var pack = JsonNode.Parse(packJson).AsObject();
        var db = Load(pack);

        db.Validate();
        Assert.Equal(packId, db.PackId);
        Assert.Equal(pack["streamers"].AsArray().Count, db.Streamers.Count);
        Assert.All(db.Skills.Values, skill => Assert.False(skill.PackDefined));
        Assert.All(db.Personas.Values, persona => Assert.False(persona.PackDefined));
    }
}
