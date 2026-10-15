using System.Collections.Generic;
using StreamDuel.Core;
using Xunit;

public class T21_PersonaDescriptionTests
{
    static PersonaDef Persona(string code)
    {
        return new PersonaDef { Id = "description_test", Name = "测试人设", Code = code, Node = "旧时机", Desc = "旧说明" };
    }

    [Fact]
    public void KoHealDescriptionUsesFractionAndStatesTheBattleEndBoundary()
    {
        var persona = Persona("T_KO_HEAL");
        persona.Params["frac"] = 0.25;
        var description = Describe.Persona(BattleTests.LoadPack("original"), persona);

        Assert.Equal("击倒对手后", description.At);
        Assert.Contains("1/4", description.Text);
        Assert.Contains("向下取整", description.Text);
        Assert.Contains("不超过最大人气", description.Text);
        Assert.Contains("含固定伤害", description.Text);
        Assert.Contains("对战未结束", description.Text);
        Assert.Contains("回复即时结算", description.Dispel);

        persona.Params["frac"] = 0.125;
        Assert.Contains("1/8", Describe.Persona(BattleTests.LoadPack("original"), persona).Text);
    }

    [Fact]
    public void FirstControlImmunityDescriptionStatesEntryResetAndExistingProtection()
    {
        var description = Describe.Persona(BattleTests.LoadPack("original"), Persona("T_FIRST_CONTROL_IMMUNE"));

        Assert.Equal("被施加控制类状态时", description.At);
        Assert.Contains("第一次", description.Text);
        Assert.Contains("下场再登场时重新计算", description.Text);
        Assert.Contains("重连保护或已有控制", description.Text);
        Assert.Contains("不消耗本次免疫", description.Text);
    }

    [Fact]
    public void EndureDescriptionStatesBattleLifetimeAndReflectionExclusion()
    {
        var description = Describe.Persona(BattleTests.LoadPack("original"), Persona("T_ENDURE"));

        Assert.Equal("受到伤害时", description.At);
        Assert.Contains("每场对战仅一次", description.Text);
        Assert.Contains("保留 1 点人气", description.Text);
        Assert.Contains("下场不重置", description.Text);
        Assert.Contains("反弹伤害不触发", description.Text);
    }

    [Fact]
    public void AllyPpDescriptionUsesAmountAndOnlyLivingBenchAllies()
    {
        var persona = Persona("T_ALLY_PP_ON_ENTER");
        persona.Params["n"] = 3;
        var description = Describe.Persona(BattleTests.LoadPack("original"), persona);

        Assert.Equal("登场时", description.At);
        Assert.Contains("己方后排所有还在播的队友", description.Text);
        Assert.Contains("每个技能 PP +3", description.Text);
        Assert.Contains("不超过各技能上限", description.Text);
        Assert.Contains("PP 回复即时结算", description.Dispel);
    }

    [Fact]
    public void StageDescriptionUsesStatNamesAndSeparatesPersonaFromDispellableBoosts()
    {
        var db = BattleTests.LoadPack("original");
        var persona = Persona("T_STAGE_ON_HIT");
        persona.Params = new Dictionary<string, double> { { "spd", 2 }, { "def", 1 } };
        var description = Describe.Persona(db, persona);

        Assert.Equal("被击中后", description.At);
        Assert.Contains("每次受到普通技能直接伤害", description.Text);
        Assert.Contains("实际扣除人气且没有下播", description.Text);
        Assert.Contains("抗压+1、反应+2", description.Text);
        Assert.Contains("最高 +6", description.Text);
        Assert.Contains("固定伤害、持续伤害、反噬和反弹不触发", description.Text);
        Assert.Contains("反弹使行动方下播后，不再触发", description.Text);
        Assert.Contains("人设不可消除", description.Dispel);
        Assert.Contains("能力提升可被消强化，下场清零", description.Dispel);
    }

    [Theory]
    [InlineData("T_KO_HEAL")]
    [InlineData("T_FIRST_CONTROL_IMMUNE")]
    [InlineData("T_ENDURE")]
    [InlineData("T_ALLY_PP_ON_ENTER")]
    [InlineData("T_STAGE_ON_HIT")]
    public void EveryNewPersonaHasGeneratedTimingTextAndRemovalRules(string code)
    {
        var persona = Persona(code);
        persona.Params["frac"] = 0.25;
        persona.Params["n"] = 2;
        persona.Params["def"] = 1;
        var description = Describe.Persona(BattleTests.LoadPack("original"), persona);

        Assert.False(string.IsNullOrWhiteSpace(description.At));
        Assert.False(string.IsNullOrWhiteSpace(description.Text));
        Assert.NotEqual("旧时机", description.At);
        Assert.NotEqual("旧说明", description.Text);
        Assert.Contains("人设不可消除", description.Dispel);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("fan_holo_niji")]
    [InlineData("fan_asoul_vr")]
    public void ExistingPersonasKeepContentPackTimingAndDescription(string pack)
    {
        var db = BattleTests.LoadPack(pack);
        var existingCodes = new HashSet<string>
        {
            "T_LOWHP_POWER", "T_EXECUTE", "T_REGEN", "T_BENCH_GUARD", "T_INTERACT_REDUCE", "T_FIRST_TURN_PRIO",
            "T_TURNSTART_CLEANSE", "T_FREE_PP_SLOT6", "T_CONTACT_STATUS", "T_ENTER_FIELD", "T_FAINT_CURSE", "T_SPEED_IF_FIRST"
        };
        foreach (var persona in db.Personas.Values)
        {
            if (!existingCodes.Contains(persona.Code)) continue;
            var description = Describe.Persona(db, persona);
            Assert.Equal(persona.Node, description.At);
            Assert.Equal(persona.Desc, description.Text);
            Assert.Equal("人设不可消除", description.Dispel);
        }
    }
}
