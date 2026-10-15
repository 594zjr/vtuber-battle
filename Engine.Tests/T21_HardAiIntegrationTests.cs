using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T21_HardAiIntegrationTests
{
    const string RiskySkill = "t21_hard_self_damage";
    const string SafeSkill = "t21_hard_safe_hit";

    [Fact]
    public void HardAvoidsPreSelfKnockoutAfterEndureWasAlreadyUsed()
    {
        var battle = MakeSpentEndureBattle();
        var forced = MakeSpentEndureBattle();
        var untouched = MakeSpentEndureBattle();
        var fighter = battle.Active(0);
        Assert.True(fighter.EndureUsed);
        Assert.True(battle.Active(1).FirstControlImmunityUsed);
        Assert.Equal(200, fighter.Hp);
        Assert.Equal(new[] { 0, 1 }, battle.LegalFor(0).Skills.Where(slot => slot.Usable).Select(slot => slot.Index));
        Assert.True(battle.PreviewDamage(0, 0).Min >= battle.Active(1).Hp);

        // 用真实结算确认高伤技能已无法靠第二次保命越过前置自伤。
        forced.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.True(forced.Active(0).Fainted);
        Assert.Equal(1000, forced.Active(1).Hp);
        Assert.Contains(forced.Log, entry => entry.Side == 0 && entry.Text.Contains("技能中断"));
        string before = Snapshot(battle);

        var action = Ai.Choose(battle, 0, AiLevel.Hard);

        Assert.Equal(before, Snapshot(battle));
        for (int i = 0; i < 8; i++) Assert.Equal(untouched.Random(), battle.Random());
        Assert.Equal(ActionType.Skill, action.Type);
        Assert.Equal(1, action.Index);
        Assert.Equal(SafeSkill, fighter.Skills[action.Index].Id);

        int from = battle.Log.Count;
        battle.ResolveTurn(action, BattleAction.Pass());
        Assert.False(fighter.Fainted);
        Assert.True(fighter.EndureUsed);
        Assert.True(battle.Active(1).Hp < 1000);
        Assert.DoesNotContain(battle.Log.Skip(from), entry => entry.Text.Contains("技能中断"));
    }

    [Theory]
    [InlineData("T_KO_HEAL")]
    [InlineData("T_FIRST_CONTROL_IMMUNE")]
    [InlineData("T_ENDURE")]
    [InlineData("T_ALLY_PP_ON_ENTER")]
    [InlineData("T_STAGE_ON_HIT")]
    public void HardCompletesAndReplaysBattlesWithEachNewPersona(string code)
    {
        var db = BattleTests.LoadPack("original");
        var persona = new PersonaDef { Id = "t21_hard_match_persona", Name = "困难对局人设", Code = code };
        if (code == "T_KO_HEAL") persona.Params["frac"] = 0.25;
        if (code == "T_ALLY_PP_ON_ENTER") persona.Params["n"] = 1;
        if (code == "T_STAGE_ON_HIT") persona.Params["def"] = 1;
        db.Personas.Add(persona.Id, persona);
        foreach (var streamer in db.Streamers) streamer.Persona = persona.Id;
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "你", "对手" }, 21);
        int guard = 500;
        while (battle.Phase != Phase.Over && guard-- > 0)
        {
            if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(Ai.Choose(battle, 0, AiLevel.Hard), Ai.Choose(battle, 1, AiLevel.Hard));
            else
                for (int side = 0; side < 2; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, AiLevel.Hard));
        }

        Assert.Equal(Phase.Over, battle.Phase);
        Assert.InRange(battle.Winner, -1, 1);
        string json = battle.Record.ToJson();
        var replayed = Replay.Run(db, BattleRecord.FromJson(json));
        Assert.Equal(battle.Winner, replayed.Winner);
        Assert.Equal(Snapshot(battle), Snapshot(replayed));
        Assert.Equal(json, replayed.Record.ToJson());
        for (int i = 0; i < 8; i++) Assert.Equal(battle.Random(), replayed.Random());
    }

    static Battle MakeSpentEndureBattle()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas.Add("t21_hard_endure", new PersonaDef { Id = "t21_hard_endure", Name = "一次保命", Code = "T_ENDURE" });
        db.Personas.Add("t21_hard_control", new PersonaDef { Id = "t21_hard_control", Name = "首次免控", Code = "T_FIRST_CONTROL_IMMUNE" });
        db.Skills.Add(RiskySkill, new SkillDef
        {
            Id = RiskySkill, Name = "前置自伤", Type = "tech", Cat = "talent", Power = 5000, Pp = 10,
            Effects = { new EffectDef { At = "pre", Code = "FIXED_DAMAGE", Target = "self", N = 250 } }
        });
        db.Skills.Add(SafeSkill, new SkillDef { Id = SafeSkill, Name = "安全攻击", Type = "tech", Cat = "talent", Power = 20, Pp = 10 });
        db.Skills.Add("t21_hard_prepare", new SkillDef
        {
            Id = "t21_hard_prepare", Name = "准备状态", Type = "talk", Cat = "status", Pp = 10,
            Effects =
            {
                new EffectDef { At = "pre", Code = "FIXED_DAMAGE", Target = "self", N = 250 },
                new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "stall" },
                new EffectDef { At = "post", Code = "HEAL", Target = "self", Frac = 1 }
            }
        });
        var mine = db.Streamer("bell");
        mine.Persona = "t21_hard_endure";
        mine.Stats["hp"] = 200;
        mine.Stats["spa"] = 100;
        mine.Stats["spd"] = 1000;
        var foe = db.Streamer("kei");
        foe.Persona = "t21_hard_control";
        foe.Stats["hp"] = 1000;
        foe.Stats["spa"] = 100;
        foe.Stats["sdef"] = 100;
        foe.Stats["spd"] = 1;
        var battle = new Battle(db, new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "你", "对手" }, 7);
        foreach (var slot in battle.Active(0).Skills) slot.Pp = 0;
        battle.Active(0).Skills[2] = new SkillSlot { Id = "t21_hard_prepare", Pp = 1, Max = 1 };
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        battle.Active(0).Skills[0] = new SkillSlot { Id = RiskySkill, Pp = 5, Max = 10 };
        battle.Active(0).Skills[1] = new SkillSlot { Id = SafeSkill, Pp = 5, Max = 10 };
        foreach (var slot in battle.Active(1).Skills)
        {
            slot.Id = SafeSkill;
            slot.Pp = 5;
        }
        return battle;
    }

    static string Snapshot(Battle battle)
    {
        return JsonSerializer.Serialize(battle, new JsonSerializerOptions { IncludeFields = true });
    }
}
