using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T46_ModifierEdgeTests
{
    static readonly IList<string>[] Teams =
    {
        new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
        new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
    };

    static ContentDb Db()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["modifier_neutral"] = new PersonaDef { Id = "modifier_neutral", Name = "测试人设", Code = "T_LOWHP_POWER" };
        foreach (var streamer in db.Streamers)
        {
            streamer.Persona = "modifier_neutral";
            streamer.Stats["hp"] = 160;
        }
        return db;
    }

    static Battle Make(ContentDb db, string[] modifiers, bool ban = false, bool lead = false)
    {
        return new Battle(db, Teams, new[] { "甲方", "乙方" }, new BattleOptions
        {
            Seed = 46, BanOne = ban, LeadPick = lead, Modifiers = modifiers.ToList()
        });
    }

    static void Persona(ContentDb db, string streamer, string code, params (string key, double value)[] values)
    {
        string id = "modifier_" + streamer;
        var persona = new PersonaDef { Id = id, Name = "测试人设", Code = code };
        foreach (var value in values) persona.Params[value.key] = value.value;
        db.Personas[id] = persona;
        db.Streamer(streamer).Persona = id;
    }

    static SkillDef Equip(Battle battle, int side, params EffectDef[] effects)
    {
        string id = "modifier_skill_" + side;
        var skill = new SkillDef { Id = id, Name = "修饰器测试技能", Type = "talk", Cat = "status", Pp = 20, Prio = 5 };
        skill.Effects.AddRange(effects);
        battle.Db.Skills[id] = skill;
        battle.Active(side).Skills[0] = new SkillSlot { Id = id, Pp = 20, Max = 20 };
        return skill;
    }

    static EffectDef FixedDamage(int amount)
    {
        return new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = amount };
    }

    static void Faint(Fighter fighter)
    {
        fighter.Hp = 0;
        fighter.Fainted = true;
    }

    [Fact]
    public void EditingPublicCatalogDoesNotChangeRulesOrFutureCatalogs()
    {
        var catalog = BattleModifiers.All;
        var definition = Assert.Single(catalog, item => item.Id == "half_pp");
        string name = definition.Name;
        string description = definition.Desc;
        definition.Id = "modified_id";
        definition.Name = "已修改名称";
        definition.Desc = "已修改说明";

        var fresh = Assert.Single(BattleModifiers.All, item => item.Id == "half_pp");
        Assert.Equal(name, fresh.Name);
        Assert.Equal(description, fresh.Desc);
        Assert.DoesNotContain(BattleModifiers.All, item => item.Id == "modified_id");
        var list = Assert.IsAssignableFrom<IList<ModifierDef>>(catalog);
        Assert.Throws<NotSupportedException>(() => list.Add(new ModifierDef { Id = "extra" }));
        var battle = Make(Db(), new[] { "half_pp" });
        Assert.Contains("half_pp", battle.Modifiers);
        Assert.Contains(battle.Log, entry => entry.Node == Node.Start && entry.Kind == "rule" && entry.Text.Contains(name));
    }

    [Fact]
    public void HalfPpAppliesToEverySlotAndLeavesSharedDefinitionsUnchanged()
    {
        var db = Db();
        db.Skills["warm_up"].Pp = int.MaxValue;
        db.Skills["high_note"].Pp = 1;
        db.Skills["full_volume"].Pp = 3;
        var originalPp = db.Skills.ToDictionary(pair => pair.Key, pair => pair.Value.Pp);
        var battle = Make(db, new[] { "half_pp" });

        foreach (var slot in battle.Sides.SelectMany(side => side.Team).SelectMany(fighter => fighter.Skills))
        {
            int expected = originalPp[slot.Id] / 2 + originalPp[slot.Id] % 2;
            Assert.Equal(expected, slot.Max);
            Assert.Equal(expected, slot.Pp);
        }
        Assert.Equal(1073741824, battle.Active(0).Skills[0].Max);
        foreach (var definition in db.Skills) Assert.Equal(originalPp[definition.Key], definition.Value.Pp);
        var plain = Make(db, Array.Empty<string>());
        Assert.Equal(int.MaxValue, plain.Active(0).Skills[0].Pp);
    }

    [Fact]
    public void EntryPpRecoveryUsesHalvedCapsAndDoesNotRefillTheEntrant()
    {
        var db = Db();
        Persona(db, "enka", "T_ALLY_PP_ON_ENTER", ("n", 100));
        var battle = Make(db, new[] { "half_pp" });
        foreach (var slot in battle.Sides[0].Team.SelectMany(fighter => fighter.Skills)) slot.Pp = 0;

        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());

        Assert.Equal("enka", battle.Active(0).Def.Id);
        Assert.All(battle.Active(0).Skills, slot => Assert.Equal(0, slot.Pp));
        Assert.All(battle.Sides[0].Team.Where(fighter => fighter != battle.Active(0)).SelectMany(fighter => fighter.Skills), slot =>
        {
            Assert.Equal((db.Skills[slot.Id].Pp + 1) / 2, slot.Max);
            Assert.Equal(slot.Max, slot.Pp);
        });
    }

    [Fact]
    public void DoubleHpAppliesToBothFullTeamsWithoutEditingDefinitions()
    {
        var db = Db();
        var initial = db.Streamers.ToDictionary(streamer => streamer.Id, streamer => streamer.Stats["hp"]);
        var doubled = Make(db, new[] { "hp_x2" });
        var plain = Make(db, Array.Empty<string>());

        foreach (var fighter in doubled.Sides.SelectMany(side => side.Team))
        {
            Assert.Equal(initial[fighter.Def.Id] * 2, fighter.MaxHp);
            Assert.Equal(fighter.MaxHp, fighter.Hp);
            Assert.Equal(initial[fighter.Def.Id], fighter.Def.Stats["hp"]);
        }
        Assert.All(plain.Sides.SelectMany(side => side.Team), fighter => Assert.Equal(initial[fighter.Def.Id], fighter.MaxHp));
    }

    [Fact]
    public void DoubleHpRejectsOverflowButAcceptsTheLargestSafeBaseHp()
    {
        var db = Db();
        db.Streamer("bell").Stats["hp"] = int.MaxValue / 2;
        Assert.Equal(int.MaxValue - 1, Make(db, new[] { "hp_x2" }).Active(0).MaxHp);
        db.Streamer("bell").Stats["hp"]++;

        var error = Assert.Throws<KeyError>(() => Make(db, new[] { "hp_x2" }));

        Assert.Contains("hp_x2", error.Message);
        Assert.Contains("人气", error.Message);
    }

    [Theory]
    [InlineData(5, 3)]
    [InlineData(-5, -3)]
    public void StageEffectsClampEveryStatAtBothLimits(int change, int expected)
    {
        var battle = Make(Db(), new[] { "stage_cap_3" });
        Equip(battle, 0, new EffectDef
        {
            At = "post", Code = "STAGE", Target = "self",
            Changes = ContentDb.StatKeys.ToDictionary(stat => stat, stat => change)
        });

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.All(battle.Active(0).Stages.Values, stage => Assert.Equal(expected, stage));
    }

    [Fact]
    public void StealingBoostsHonorsTheLowerCapAndKeepsEnemyDebuffs()
    {
        var battle = Make(Db(), new[] { "stage_cap_3" });
        battle.Active(0).Stages["atk"] = 2;
        battle.Active(0).Stages["def"] = -2;
        battle.Active(1).Stages["atk"] = 3;
        battle.Active(1).Stages["def"] = 1;
        battle.Active(1).Stages["spd"] = -2;
        Equip(battle, 0, new EffectDef { At = "pre", Code = "STEAL_BOOST", Target = "foe" });

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(3, battle.Active(0).Stages["atk"]);
        Assert.Equal(-1, battle.Active(0).Stages["def"]);
        Assert.Equal(0, battle.Active(1).Stages["atk"]);
        Assert.Equal(0, battle.Active(1).Stages["def"]);
        Assert.Equal(-2, battle.Active(1).Stages["spd"]);
    }

    [Fact]
    public void OnHitPersonaAndEndOfTurnPersonaUseTheSameStageCap()
    {
        var db = Db();
        Persona(db, "bell", "T_STAGE_ON_HIT", ("def", 2));
        Persona(db, "kei", "T_SPEED_IF_FIRST", ("n", 5));
        var battle = Make(db, new[] { "stage_cap_3" });
        battle.Active(0).Stages["def"] = 2;
        battle.Active(1).Stages["spd"] = 2;
        var attack = Equip(battle, 1);
        attack.Cat = "interact";
        attack.Power = 1;

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));

        Assert.Equal(3, battle.Active(0).Stages["def"]);
        Assert.Equal(3, battle.Active(1).Stages["spd"]);
    }

    [Fact]
    public void NoSwitchRemovesBothLegalListsAndNormalizesManualSwitchRequests()
    {
        var battle = Make(Db(), new[] { "no_switch" });
        var first = battle.Active(0);
        var second = battle.Active(1);
        Assert.Empty(battle.LegalFor(0).Switches);
        Assert.Empty(battle.LegalFor(1).Switches);

        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.SwitchTo(1));

        Assert.Same(first, battle.Active(0));
        Assert.Same(second, battle.Active(1));
        Assert.Equal(first.MaxHp, first.Hp);
        Assert.Equal(second.MaxHp, second.Hp);
        Assert.DoesNotContain(battle.Log, entry => entry.Node == Node.Switch);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectedSwitchStillNormalizesControlOrEmptyPp(bool controlled)
    {
        var battle = Make(Db(), new[] { "no_switch" });
        var fighter = battle.Active(0);
        if (controlled) fighter.Statuses.Add(new StatusInst { Id = "disconnect", Left = 2 });
        else foreach (var skill in fighter.Skills) skill.Pp = 0;
        int foeHp = battle.Active(1).Hp;

        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());

        Assert.Same(fighter, battle.Active(0));
        if (controlled)
        {
            Assert.Equal(1, fighter.Statuses.Single(status => status.Id == "disconnect").Left);
            Assert.Equal(foeHp, battle.Active(1).Hp);
        }
        else
        {
            Assert.True(battle.Active(1).Hp < foeHp);
            Assert.Equal(-1, fighter.LastSkillIndex);
        }
    }

    [Fact]
    public void NoSwitchDoesNotPreventReplacementAfterFainting()
    {
        var battle = Make(Db(), new[] { "no_switch" });
        Equip(battle, 1, FixedDamage(999));
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        Assert.True(battle.Need[0]);
        Assert.Equal(Phase.Replace, battle.Phase);

        battle.Replace(0, 1);

        Assert.Equal("enka", battle.Active(0).Def.Id);
        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.False(battle.Need[0]);
        Assert.Empty(battle.LegalFor(0).Switches);
    }

    [Fact]
    public void OpeningHypeWaitsForBansAndLeadsAndNeverReturnsOnReentryOrReplacement()
    {
        var battle = Make(Db(), new[] { "hype_start" }, ban: true, lead: true);
        Assert.All(battle.Sides.SelectMany(side => side.Team), fighter => Assert.False(fighter.HasField("hype")));
        battle.ChooseBan(0, 0);
        battle.ChooseBan(1, 0);
        Assert.Equal(Phase.Lead, battle.Phase);
        battle.ChooseLead(0, 2);
        Assert.All(battle.Sides.SelectMany(side => side.Team), fighter => Assert.False(fighter.HasField("hype")));

        battle.ChooseLead(1, 3);

        Assert.Equal("lumi", battle.Active(0).Def.Id);
        Assert.Equal("kasumi", battle.Active(1).Def.Id);
        foreach (var side in battle.Sides)
        {
            Assert.Equal(1, side.Team[side.Active].FieldOf("hype").Stacks);
            Assert.All(side.Team.Where(fighter => fighter != side.Team[side.Active]), fighter => Assert.False(fighter.HasField("hype")));
        }
        Assert.Equal(2, battle.Log.Count(entry => entry.Node == Node.Start && entry.Kind == "field" && entry.Text.Contains("热度")));
        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.SwitchTo(0), BattleAction.Pass());
        Assert.False(battle.Active(0).HasField("hype"));
        Equip(battle, 1, FixedDamage(999));
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        battle.Replace(0, 2);
        Assert.False(battle.Active(0).HasField("hype"));
        Assert.Equal(1, battle.Active(1).FieldOf("hype").Stacks);
    }

    [Theory]
    [InlineData(15, 1)]
    [InlineData(17, 1)]
    [InlineData(31, 1)]
    [InlineData(32, 2)]
    public void SuddenDeathStartsAtTenFloorsDamageAndLeavesBenchesUntouched(int hp, int damage)
    {
        var db = Db();
        foreach (var streamer in db.Streamers) streamer.Stats["hp"] = hp;
        var battle = Make(db, new[] { "sudden_death" });
        for (int i = 0; i < 9; i++) battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(10, battle.Turn);
        Assert.All(battle.Sides.SelectMany(side => side.Team), fighter => Assert.Equal(hp, fighter.Hp));
        int logStart = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        foreach (var side in battle.Sides)
        {
            Assert.Equal(hp - damage, side.Team[side.Active].Hp);
            Assert.All(side.Team.Where(fighter => fighter != side.Team[side.Active]), fighter => Assert.Equal(hp, fighter.Hp));
        }
        var losses = battle.Log.Skip(logStart).Where(entry => entry.Kind == "dmg").ToList();
        Assert.Equal(2, losses.Count);
        Assert.All(losses, entry =>
        {
            Assert.Equal(10, entry.Turn);
            Assert.Equal(Node.TurnEnd, entry.Node);
            Assert.Contains("人气骤降", entry.Text);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuddenDeathFollowsOwnStatusFieldAndPersonaUsingTheEffectiveHpCap(bool doubled)
    {
        var db = Db();
        Persona(db, "bell", "T_REGEN", ("frac", 0.125));
        var battle = Make(db, doubled ? new[] { "sudden_death", "hp_x2" } : new[] { "sudden_death" });
        var fighter = battle.Active(0);
        fighter.Hp = fighter.MaxHp - 10;
        fighter.Statuses.Add(new StatusInst { Id = "hater", Left = 3 });
        fighter.Fields.Add(new FieldInst { Id = "sub", Turns = 3 });
        battle.Turn = 10;
        int logStart = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        Assert.Equal(fighter.MaxHp - fighter.MaxHp / 16, fighter.Hp);
        var effects = battle.Log.Skip(logStart).Where(entry => entry.Side == 0 && entry.Node == Node.TurnEnd
            && (entry.Kind == "dmg" || entry.Kind == "heal")).ToList();
        Assert.Equal(new[] { "dmg", "heal", "heal", "dmg" }, effects.Select(entry => entry.Kind));
        Assert.Equal(fighter.MaxHp, effects[2].SnapHp[0][0]);
        Assert.Contains("人气骤降", effects[3].Text);
    }

    [Theory]
    [InlineData("T_KO_HEAL")]
    [InlineData("T_STAGE_ON_HIT")]
    public void SuddenDeathTriggersFaintCurseButNotReflectionOrDirectHitPersonas(string foePersona)
    {
        var db = Db();
        Persona(db, "bell", "T_FAINT_CURSE", ("n", -5));
        Persona(db, "kei", foePersona, ("frac", 0.5), ("def", 2));
        var battle = Make(db, new[] { "sudden_death", "stage_cap_3" });
        battle.Active(0).Hp = 5;
        battle.Active(1).Hp = 80;
        battle.Active(0).Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        battle.Active(1).Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        battle.Turn = 10;

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        Assert.True(battle.Active(0).Fainted);
        Assert.True(battle.Need[0]);
        Assert.Equal(Phase.Replace, battle.Phase);
        Assert.Equal(70, battle.Active(1).Hp);
        Assert.All(battle.Active(1).Stages.Values, stage => Assert.Equal(-3, stage));
        Assert.Contains(battle.Log, entry => entry.Side == 0 && entry.Node == Node.TurnEnd && entry.Kind == "faint");
        Assert.DoesNotContain(battle.Log, entry => entry.Kind == "heal" || entry.Text.Contains("反弹："));
    }

    [Fact]
    public void SuddenDeathCanConsumeEndureOnlyOnce()
    {
        var db = Db();
        Persona(db, "bell", "T_ENDURE");
        var battle = Make(db, new[] { "sudden_death" });
        battle.Active(0).Hp = 1;
        battle.Turn = 10;

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        Assert.Equal(1, battle.Active(0).Hp);
        Assert.True(battle.Active(0).EndureUsed);
        Assert.False(battle.Active(0).Fainted);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.True(battle.Active(0).Fainted);
        Assert.True(battle.Need[0]);
    }

    [Fact]
    public void FirstTeamEliminatedAtTurnEndStopsTheOtherSuddenDeathTick()
    {
        var battle = Make(Db(), new[] { "sudden_death" });
        foreach (var side in battle.Sides)
        {
            side.Team[side.Active].Hp = 1;
            foreach (var fighter in side.Team.Where(fighter => fighter != side.Team[side.Active])) Faint(fighter);
        }
        Assert.True(battle.StatVal(battle.Active(0), "spd") > battle.StatVal(battle.Active(1), "spd"));
        battle.Turn = 10;
        int logStart = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(1, battle.Winner);
        Assert.Equal(1, battle.Active(1).Hp);
        Assert.DoesNotContain(battle.Log.Skip(logStart), entry => entry.Side == 1 && entry.Node == Node.TurnEnd && entry.Kind == "dmg");
        Assert.Equal(Node.Over, battle.Log.Last().Node);
    }

    [Fact]
    public void AlreadyFaintedFromStatusDoesNotReceiveAnExtraSuddenDeathTick()
    {
        var battle = Make(Db(), new[] { "sudden_death" });
        battle.Active(0).Hp = 10;
        battle.Active(0).Statuses.Add(new StatusInst { Id = "hater", Left = 3 });
        battle.Turn = 10;
        int logStart = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        Assert.True(battle.Active(0).Fainted);
        var damage = Assert.Single(battle.Log.Skip(logStart), entry => entry.Side == 0 && entry.Kind == "dmg");
        Assert.DoesNotContain("人气骤降", damage.Text);
        Assert.Equal(150, battle.Active(1).Hp);
    }
}
