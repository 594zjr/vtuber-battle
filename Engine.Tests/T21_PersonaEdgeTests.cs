using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T21_PersonaEdgeTests
{
    static ContentDb Load()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["edge_neutral"] = new PersonaDef
        {
            Id = "edge_neutral", Name = "边界测试", Code = "T_LOWHP_POWER"
        };
        foreach (var streamer in db.Streamers) streamer.Persona = "edge_neutral";
        return db;
    }

    static void SetPersona(ContentDb db, string id, string code, params (string key, double value)[] values)
    {
        string personaId = "edge_" + id;
        var persona = new PersonaDef { Id = personaId, Name = "边界人设", Code = code };
        foreach (var value in values) persona.Params[value.key] = value.value;
        db.Personas[personaId] = persona;
        db.Streamer(id).Persona = personaId;
    }

    static Battle Make(ContentDb db)
    {
        return new Battle(db, new IList<string>[]
        {
            new[] { "kei", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "bell", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "你", "对手" }, 21);
    }

    static SkillDef Equip(Battle battle, int side, string id, params EffectDef[] effects)
    {
        var skill = DefineSkill(battle.Db, id, effects);
        battle.Active(side).Skills[0] = new SkillSlot { Id = id, Pp = 20, Max = 20 };
        return skill;
    }

    static SkillDef DefineSkill(ContentDb db, string id, params EffectDef[] effects)
    {
        var skill = new SkillDef { Id = id, Name = "边界测试技能", Type = "talk", Cat = "status", Pp = 20, Prio = 5 };
        skill.Effects.AddRange(effects);
        db.Skills[id] = skill;
        return skill;
    }

    static EffectDef Fixed(int amount, string target = "foe")
    {
        return new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = target, N = amount };
    }

    static EffectDef Status(string id)
    {
        return new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = id };
    }

    [Fact]
    public void FirstControlImmunityResetsOnReentryButNotBetweenTurns()
    {
        var db = Load();
        SetPersona(db, "kei", "T_FIRST_CONTROL_IMMUNE");
        var battle = Make(db);
        var target = battle.Active(0);
        Equip(battle, 1, "edge_control", Status("disconnect"));

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        Assert.Null(battle.ControlOf(target));
        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.SwitchTo(0), BattleAction.Skill(0));
        Assert.Null(battle.ControlOf(target));
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));

        Assert.NotNull(battle.ControlOf(target));
        var blocked = battle.Log.Where(entry => entry.Side == 0 && entry.Text.Contains("控制无效")).ToList();
        Assert.Equal(2, blocked.Count);
        Assert.All(blocked, entry => Assert.Equal(Node.First, entry.Node));
    }

    [Fact]
    public void WeakStatusDoesNotConsumeFirstControlImmunity()
    {
        var db = Load();
        SetPersona(db, "kei", "T_FIRST_CONTROL_IMMUNE");
        var battle = Make(db);
        var target = battle.Active(0);
        Equip(battle, 1, "edge_weak_control", Status("hater"), Status("disconnect"));

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));

        Assert.True(target.HasStatus("hater"));
        Assert.Null(battle.ControlOf(target));
        Assert.Single(battle.Log, entry => entry.Side == 0 && entry.Text.Contains("控制无效"));
    }

    [Fact]
    public void EndureIsNotSpentByNonlethalDamageAndDoesNotResetOnSwitch()
    {
        var db = Load();
        SetPersona(db, "kei", "T_ENDURE");
        var battle = Make(db);
        var target = battle.Active(0);
        target.Hp = 30;
        Equip(battle, 1, "edge_twenty", Fixed(20));

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        Assert.Equal(10, target.Hp);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        Assert.Equal(1, target.Hp);
        Assert.False(target.Fainted);
        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.SwitchTo(0), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));

        Assert.True(target.Fainted);
        Assert.Equal(0, target.Hp);
        var faint = Assert.Single(battle.Log, entry => entry.Kind == "faint" && entry.Side == 0);
        Assert.Equal(Node.First, faint.Node);
        Assert.Equal(0, faint.SnapHp[0][0]);
        Assert.Contains(battle.Log, entry => entry.Side == 0 && entry.Kind == "trait" && entry.Node == Node.First && entry.SnapHp[0][0] == 1);
    }

    [Fact]
    public void EndureCannotSaveTheRecipientOfReflectedDamage()
    {
        var db = Load();
        SetPersona(db, "kei", "T_ENDURE");
        var battle = Make(db);
        battle.Active(0).Hp = 1;
        battle.Active(1).Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        Equip(battle, 0, "edge_reflect", Fixed(10));
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.True(battle.Active(0).Fainted);
        Assert.Equal(0, battle.Active(0).Hp);
        Assert.DoesNotContain(battle.Log.Skip(from), entry => entry.Side == 0 && entry.Kind == "trait");
    }

    [Fact]
    public void EnterPpRestorationIncludesAllLivingBenchSkillsButExcludesSelfAndFaintedAllies()
    {
        var db = Load();
        SetPersona(db, "kei", "T_ALLY_PP_ON_ENTER", ("n", 2));
        var battle = Make(db);
        foreach (var fighter in battle.Sides.SelectMany(side => side.Team))
            foreach (var skill in fighter.Skills) skill.Pp = skill.Max - 1;
        var dead = battle.Sides[0].Team[2];
        dead.Hp = 0;
        dead.Fainted = true;
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.SwitchTo(0), BattleAction.Pass());

        Assert.All(battle.Active(0).Skills, skill => Assert.Equal(skill.Max - 1, skill.Pp));
        Assert.All(dead.Skills, skill => Assert.Equal(skill.Max - 1, skill.Pp));
        Assert.All(battle.Sides[1].Team.SelectMany(fighter => fighter.Skills), skill => Assert.Equal(skill.Max - 1, skill.Pp));
        foreach (var ally in battle.Sides[0].Team.Where(fighter => fighter != battle.Active(0) && !fighter.Fainted))
        {
            Assert.All(ally.Skills, skill => Assert.Equal(skill.Max, skill.Pp));
            Assert.Contains(battle.Log.Skip(from), entry => entry.Node == Node.Switch && entry.Side == 0 && entry.Text.Contains(ally.Name) && entry.Text.Contains("PP"));
        }
    }

    [Theory]
    [InlineData("interact")]
    [InlineData("talent")]
    public void StageOnHitTriggersForEachSurvivedDirectHitAndCapsEachStat(string category)
    {
        var db = Load();
        SetPersona(db, "kei", "T_STAGE_ON_HIT", ("def", 2), ("spd", 1));
        var battle = Make(db);
        var target = battle.Active(0);
        target.Stages["def"] = 5;
        var skill = Equip(battle, 1, "edge_direct");
        skill.Cat = category;
        skill.Power = 1;
        int before = target.Hp;

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));

        Assert.True(target.Hp < before);
        Assert.False(target.Fainted);
        Assert.Equal(6, target.Stages["def"]);
        Assert.Equal(2, target.Stages["spd"]);
        Assert.Equal(2, battle.Log.Count(entry => entry.Kind == "stage" && entry.Side == 0 && entry.Node == Node.First));
    }

    [Theory]
    [InlineData("fixed")]
    [InlineData("weak")]
    [InlineData("reflection")]
    [InlineData("miss")]
    [InlineData("lethal")]
    public void StageOnHitIgnoresNonDirectMissedAndLethalDamage(string mode)
    {
        var db = Load();
        SetPersona(db, "kei", "T_STAGE_ON_HIT", ("def", 1));
        var battle = Make(db);
        var target = battle.Active(0);
        if (mode == "fixed") Equip(battle, 1, "edge_fixed", Fixed(5));
        else if (mode == "weak") target.Statuses.Add(new StatusInst { Id = "hater", Left = 2 });
        else if (mode == "reflection")
        {
            Equip(battle, 0, "edge_reflected", Fixed(10));
            battle.Active(1).Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        }
        else
        {
            var skill = Equip(battle, 1, "edge_nonhit");
            skill.Cat = "interact";
            skill.Power = 1;
            if (mode == "miss") skill.Acc = 0;
            else target.Hp = 1;
        }

        battle.ResolveTurn(mode == "reflection" ? BattleAction.Skill(0) : BattleAction.Pass(),
            mode == "weak" || mode == "reflection" ? BattleAction.Pass() : BattleAction.Skill(0));

        Assert.Equal(0, target.Stages["def"]);
        Assert.DoesNotContain(battle.Log, entry => entry.Kind == "stage" && entry.Side == 0);
        if (mode == "lethal") Assert.True(target.Fainted);
    }

    [Fact]
    public void LethalReflectionStopsTheRemainingAfterHitPersonaStep()
    {
        var db = Load();
        SetPersona(db, "kei", "T_STAGE_ON_HIT", ("def", 1));
        var battle = Make(db);
        var target = battle.Active(0);
        target.Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        battle.Active(1).Hp = 1;
        var skill = Equip(battle, 1, "edge_lethal_reflection");
        skill.Cat = "interact";
        skill.Power = 1;

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));

        Assert.True(target.Hp < target.MaxHp);
        Assert.False(target.Fainted);
        Assert.True(battle.Active(1).Fainted);
        Assert.Equal(0, target.Stages["def"]);
        Assert.DoesNotContain(battle.Log, entry => entry.Kind == "stage" && entry.Side == 0);
    }

    [Theory]
    [InlineData(7, 32)]
    [InlineData(100, 101)]
    public void KoHealUsesFloorAndCapAndOnlyCreditsOneKnockout(int initialHp, int expectedHp)
    {
        var db = Load();
        SetPersona(db, "kei", "T_KO_HEAL", ("frac", 0.25));
        var battle = Make(db);
        var attacker = battle.Active(0);
        attacker.MaxHp = 101;
        attacker.Hp = initialHp;
        Equip(battle, 0, "edge_double_kill", Fixed(99999), Fixed(99999));
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.True(battle.Active(1).Fainted);
        Assert.Equal(expectedHp, attacker.Hp);
        var healing = Assert.Single(battle.Log.Skip(from), entry => entry.Kind == "heal" && entry.Side == 0);
        Assert.Equal(Node.First, healing.Node);
        Assert.Equal(expectedHp, healing.SnapHp[0][0]);
        int faintIndex = battle.Log.FindIndex(from, entry => entry.Kind == "faint" && entry.Side == 1);
        Assert.True(faintIndex >= from && battle.Log.IndexOf(healing) > faintIndex);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("weak")]
    [InlineData("reflection")]
    public void KoHealDoesNotCreditAnOpponentsIndirectOrSelfInflictedKnockout(string mode)
    {
        var db = Load();
        SetPersona(db, "kei", "T_KO_HEAL", ("frac", 0.25));
        var battle = Make(db);
        var owner = battle.Active(0);
        owner.Hp = 10;
        battle.Active(1).Hp = 1;
        if (mode == "weak") battle.Active(1).Statuses.Add(new StatusInst { Id = "hater", Left = 2 });
        else Equip(battle, 1, "edge_indirect", Fixed(5, mode == "self" ? "self" : "foe"));
        if (mode == "reflection") owner.Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });

        battle.ResolveTurn(BattleAction.Pass(), mode == "weak" ? BattleAction.Pass() : BattleAction.Skill(0));

        Assert.True(battle.Active(1).Fainted);
        Assert.Equal(mode == "reflection" ? 5 : 10, owner.Hp);
        Assert.DoesNotContain(battle.Log, entry => entry.Kind == "heal" && entry.Side == 0);
    }

    [Fact]
    public void LastOpponentKnockoutEndsBattleBeforeKoHeal()
    {
        var db = Load();
        SetPersona(db, "kei", "T_KO_HEAL", ("frac", 0.25));
        var battle = Make(db);
        battle.Active(0).Hp = 10;
        foreach (var fighter in battle.Sides[1].Team.Skip(1))
        {
            fighter.Hp = 0;
            fighter.Fainted = true;
        }
        Equip(battle, 0, "edge_last_kill", Fixed(99999));

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(0, battle.Winner);
        Assert.Equal(10, battle.Active(0).Hp);
        Assert.Equal(Node.Over, battle.Log.Last().Node);
        Assert.DoesNotContain(battle.Log, entry => entry.Kind == "heal" && entry.Side == 0);
    }

    [Theory]
    [InlineData("T_KO_HEAL")]
    [InlineData("T_FIRST_CONTROL_IMMUNE")]
    [InlineData("T_ENDURE")]
    [InlineData("T_ALLY_PP_ON_ENTER")]
    [InlineData("T_STAGE_ON_HIT")]
    public void NewPersonaStateAndLogsReproduceFromRecordedActions(string code)
    {
        var db = Load();
        SetPersona(db, "kei", code, ("frac", 0.25), ("n", 2), ("def", 1));
        DefineSkill(db, "edge_replay_self", Fixed(15, "self"));
        DefineSkill(db, "edge_replay_kill", Fixed(99999));
        DefineSkill(db, "edge_replay_control", Status("disconnect"));
        var poke = DefineSkill(db, "edge_replay_poke");
        poke.Cat = "interact";
        poke.Power = 1;
        db.Streamer("kei").Skills = new List<string> { "edge_replay_self", "edge_replay_kill", "edge_replay_poke", "edge_replay_control" };
        db.Streamer("bell").Skills = new List<string>(db.Streamer("kei").Skills);
        db.Streamer("enka").Skills = new List<string>(db.Streamer("kei").Skills);
        var battle = Make(db);

        if (code == "T_KO_HEAL")
        {
            battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
            battle.Replace(1, 1);
        }
        else if (code == "T_ENDURE")
        {
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(1));
            battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
            battle.ResolveTurn(BattleAction.SwitchTo(0), BattleAction.Pass());
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(1));
            battle.Replace(0, 1);
        }
        else if (code == "T_ALLY_PP_ON_ENTER")
        {
            battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
            battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
            battle.ResolveTurn(BattleAction.SwitchTo(0), BattleAction.Pass());
        }
        else
        {
            int skill = code == "T_FIRST_CONTROL_IMMUNE" ? 3 : 2;
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(skill));
            battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
            battle.ResolveTurn(BattleAction.SwitchTo(0), BattleAction.Skill(skill));
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(skill));
        }

        string json = battle.Record.ToJson();
        var replay = Replay.Run(db, BattleRecord.FromJson(json));
        Assert.Equal(json, replay.Record.ToJson());
        Assert.Equal(battle.Log.Count, replay.Log.Count);
        for (int i = 0; i < battle.Log.Count; i++)
        {
            var expected = battle.Log[i];
            var actual = replay.Log[i];
            Assert.Equal(expected.Turn, actual.Turn);
            Assert.Equal(expected.Node, actual.Node);
            Assert.Equal(expected.Side, actual.Side);
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(expected.SnapActive, actual.SnapActive);
            Assert.Equal(expected.SnapHp[0], actual.SnapHp[0]);
            Assert.Equal(expected.SnapHp[1], actual.SnapHp[1]);
        }
        for (int side = 0; side < 2; side++)
        {
            for (int index = 0; index < battle.Sides[side].Team.Count; index++)
            {
                var expected = battle.Sides[side].Team[index];
                var actual = replay.Sides[side].Team[index];
                Assert.Equal(expected.Hp, actual.Hp);
                Assert.Equal(expected.Fainted, actual.Fainted);
                Assert.Equal(expected.FirstControlImmunityUsed, actual.FirstControlImmunityUsed);
                Assert.Equal(expected.EndureUsed, actual.EndureUsed);
                Assert.Equal(expected.Stages, actual.Stages);
                Assert.Equal(expected.Skills.Select(skill => skill.Pp), actual.Skills.Select(skill => skill.Pp));
            }
        }
        Assert.Equal(battle.Random(), replay.Random());
    }

    [Theory]
    [InlineData("T_KO_HEAL")]
    [InlineData("T_FIRST_CONTROL_IMMUNE")]
    [InlineData("T_ENDURE")]
    [InlineData("T_ALLY_PP_ON_ENTER")]
    [InlineData("T_STAGE_ON_HIT")]
    public void NormalAndEasyAiFinishBattlesWithEveryNewPersona(string code)
    {
        var db = Load();
        foreach (var streamer in db.Streamers)
            SetPersona(db, streamer.Id, code, ("frac", 0.25), ("n", 2), ("def", 1));
        var battle = Make(db);
        int steps = 0;
        while (battle.Phase != Phase.Over && steps++ < 500)
        {
            if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(Ai.Choose(battle, 0, AiLevel.Normal), Ai.Choose(battle, 1, AiLevel.Easy));
            else
                for (int side = 0; side < 2; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
        }

        Assert.Equal(Phase.Over, battle.Phase);
        Assert.InRange(battle.Winner, -1, 1);
        var replay = Replay.Run(db, BattleRecord.FromJson(battle.Record.ToJson()));
        Assert.Equal(battle.Winner, replay.Winner);
        Assert.Equal(battle.Log.Select(entry => entry.Text), replay.Log.Select(entry => entry.Text));
    }
}
