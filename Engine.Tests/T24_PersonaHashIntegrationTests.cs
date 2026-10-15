using System.Collections.Generic;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T24_PersonaHashIntegrationTests
{
    [Theory]
    [InlineData("T_ENDURE", 0, 0)]
    [InlineData("T_ENDURE", 1, 5)]
    [InlineData("T_FIRST_CONTROL_IMMUNE", 0, 0)]
    [InlineData("T_FIRST_CONTROL_IMMUNE", 1, 5)]
    public void PersonaUseFlagsChangeTheHashForActiveAndBenchFighters(string code, int side, int index)
    {
        var battle = Make(code);
        var fighter = battle.Sides[side].Team[index];
        ulong before = battle.StateHash();

        SetUsed(fighter, code, true);
        Assert.NotEqual(before, battle.StateHash());
        SetUsed(fighter, code, false);
        Assert.Equal(before, battle.StateHash());
    }

    [Theory]
    [InlineData("T_ENDURE")]
    [InlineData("T_FIRST_CONTROL_IMMUNE")]
    public void ConsumedPersonaFlagsRemainInReplayHashesWithoutHashingSideEffects(string code)
    {
        var battle = Make(code);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        var owner = battle.Active(0);
        Assert.True(code == "T_ENDURE" ? owner.EndureUsed : owner.FirstControlImmunityUsed);
        if (code == "T_ENDURE") Assert.Equal(1, owner.Hp);
        else Assert.Null(battle.ControlOf(owner));
        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());

        var replay = Replay.Run(battle.Db, BattleRecord.FromJson(battle.Record.ToJson()));
        string before = Snapshot(battle);
        string replayBefore = Snapshot(replay);
        string summaryBefore = BattleSummary.From(battle).ToText();
        string replaySummaryBefore = BattleSummary.From(replay).ToText();
        ulong hash = battle.StateHash();

        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(hash, replay.StateHash());
        Assert.Equal(owner.FirstControlImmunityUsed, replay.Sides[0].Team[0].FirstControlImmunityUsed);
        Assert.Equal(owner.EndureUsed, replay.Sides[0].Team[0].EndureUsed);
        Assert.Equal(before, Snapshot(battle));
        Assert.Equal(replayBefore, Snapshot(replay));
        Assert.Equal(summaryBefore, BattleSummary.From(battle).ToText());
        Assert.Equal(replaySummaryBefore, BattleSummary.From(replay).ToText());
        Assert.Equal(summaryBefore, replaySummaryBefore);
        for (int i = 0; i < 8; i++) Assert.Equal(replay.Random(), battle.Random());
    }

    static Battle Make(string code)
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["hash_persona"] = new PersonaDef { Id = "hash_persona", Name = "哈希测试人设", Code = code };
        foreach (var streamer in db.Streamers) streamer.Persona = "hash_persona";
        var skill = new SkillDef
        {
            Id = "hash_trigger", Name = "哈希测试技能", Type = "tech", Cat = "status", Pp = 10, Prio = 5
        };
        skill.Effects.Add(code == "T_ENDURE"
            ? new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 99999 }
            : new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "disconnect" });
        db.Skills[skill.Id] = skill;
        db.Streamer("bell").Skills[0] = skill.Id;
        return new Battle(db, new IList<string>[]
        {
            new[] { "kei", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "bell", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "你", "对手" }, 24);
    }

    static void SetUsed(Fighter fighter, string code, bool value)
    {
        if (code == "T_ENDURE") fighter.EndureUsed = value;
        else fighter.FirstControlImmunityUsed = value;
    }

    static string Snapshot(Battle battle)
    {
        return JsonSerializer.Serialize(battle, new JsonSerializerOptions { IncludeFields = true });
    }
}
