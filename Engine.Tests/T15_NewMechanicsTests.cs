using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T15_NewMechanicsTests
{
    private static Battle Make(string lead0 = "bell", string lead1 = "kei")
    {
        var db = BattleTests.LoadPack("original");
        IList<string> Team(string lead)
        {
            return new[] { lead }.Concat(db.Streamers.Select(s => s.Id).Where(id => id != lead)).Take(6).ToArray();
        }
        return new Battle(db, new[] { Team(lead0), Team(lead1) }, new[] { "甲方", "乙方" }, 41);
    }

    private static SkillDef Equip(Battle battle, int side, params EffectDef[] effects)
    {
        string id = "test_t15_" + side;
        var skill = new SkillDef
        {
            Id = id, Name = "机制验收技能", Type = "talk", Cat = "status", Pp = 20, Prio = 10
        };
        skill.Effects.AddRange(effects);
        battle.Db.Skills[id] = skill;
        battle.Active(side).Skills[0] = new SkillSlot { Id = id, Pp = 20, Max = 20 };
        return skill;
    }

    private static EffectDef Field(string id, string target, int turns)
    {
        return new EffectDef { At = "post", Code = "FIELD", Target = target, Field = id, Turns = turns };
    }

    private static EffectDef FixedDamage(int damage)
    {
        return new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = damage };
    }

    [Fact]
    public void NewFieldDefinitionsLoadTheirMechanicsAndRemainDispellable()
    {
        var db = BattleTests.LoadPack("original");

        Assert.Equal(0.3, db.Fields["mirror"].ReflectDamage, 6);
        Assert.True(db.Fields["firewall"].ImmuneWeak);
        Assert.True(db.Fields["lockin"].PreventSwitch);
        foreach (string id in new[] { "mirror", "firewall", "lockin" })
        {
            Assert.Equal("turn", db.Fields[id].Kind);
            Assert.True(db.Fields[id].Dispel);
        }
    }

    [Fact]
    public void NewSkillsUseTheNewEffectsButAreNotAssignedToAnyStreamer()
    {
        var db = BattleTests.LoadPack("original");
        var steal = db.Skills["gain_takeover"];
        Assert.True(steal.Power > 0);
        Assert.Contains(steal.Effects, effect => effect.Code == "STEAL_BOOST" && effect.Target == "foe" && effect.At == "pre");
        var fieldSkills = new[]
        {
            new { Id = "mirror_setup", Field = "mirror", Target = "self", Turns = 3 },
            new { Id = "firewall_setup", Field = "firewall", Target = "self", Turns = 3 },
            new { Id = "lock_mic", Field = "lockin", Target = "foe", Turns = 2 }
        };
        foreach (var expected in fieldSkills)
        {
            var skill = db.Skills[expected.Id];
            Assert.Equal("status", skill.Cat);
            Assert.Equal(0, skill.Power);
            var effect = Assert.Single(skill.Effects);
            Assert.Equal("FIELD", effect.Code);
            Assert.Equal("post", effect.At);
            Assert.Equal(expected.Field, effect.Field);
            Assert.Equal(expected.Target, effect.Target);
            Assert.Equal(expected.Turns, effect.Turns);
        }
        foreach (var streamer in db.Streamers)
        {
            foreach (string id in fieldSkills.Select(skill => skill.Id).Concat(new[] { steal.Id }))
            {
                Assert.DoesNotContain(id, streamer.Skills);
                Assert.NotEqual(id, streamer.Fifth);
            }
        }
    }

    [Theory]
    [InlineData("pre")]
    [InlineData("post")]
    public void StealBoostTransfersOnlyPositiveStagesAndClampsAtSix(string at)
    {
        var battle = Make();
        Equip(battle, 0, new EffectDef { At = at, Code = "STEAL_BOOST", Target = "foe" });
        var source = battle.Active(1);
        var recipient = battle.Active(0);
        recipient.Stages["atk"] = 5;
        recipient.Stages["def"] = -3;
        recipient.Stages["spa"] = -2;
        recipient.Stages["sdef"] = 1;
        source.Stages["atk"] = 3;
        source.Stages["def"] = 2;
        source.Stages["spa"] = -4;
        source.Stages["spd"] = 2;
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(6, recipient.Stages["atk"]);
        Assert.Equal(-1, recipient.Stages["def"]);
        Assert.Equal(-2, recipient.Stages["spa"]);
        Assert.Equal(1, recipient.Stages["sdef"]);
        Assert.Equal(2, recipient.Stages["spd"]);
        Assert.Equal(0, source.Stages["atk"]);
        Assert.Equal(0, source.Stages["def"]);
        Assert.Equal(-4, source.Stages["spa"]);
        Assert.Equal(0, source.Stages["sdef"]);
        Assert.Equal(0, source.Stages["spd"]);
        var changes = battle.Log.Skip(from).Where(entry => entry.Kind == "stage").ToArray();
        Assert.Contains(changes, entry => entry.Side == 0 && entry.Node == Node.First);
        Assert.Contains(changes, entry => entry.Node == Node.First && entry.Text.Contains(source.Name));
        Assert.Contains(changes, entry => entry.Node == Node.First && entry.Text.Contains(recipient.Name));
        foreach (string stat in new[] { "atk", "def", "spd" })
            Assert.Contains(changes, entry => entry.Text.Contains(battle.Db.StatName(stat)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MirrorReflectsActualSkillDamageWithoutRecursionOrAfterHitPersona(bool bothHaveMirror)
    {
        var battle = Make("buck", "bell");
        battle.Db.Personas["trollback"].Params["chance"] = 100;
        var skill = Equip(battle, 0);
        skill.Cat = "interact";
        skill.Power = 40;
        battle.Active(1).Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        if (bothHaveMirror) battle.Active(0).Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        int attackerHp = battle.Active(0).Hp;
        int defenderHp = battle.Active(1).Hp;
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        int actualDamage = defenderHp - battle.Active(1).Hp;
        int reflectedDamage = Math.Max(1, (int)Math.Floor(actualDamage * 0.3));
        Assert.True(actualDamage > 0);
        Assert.Equal(attackerHp - reflectedDamage, battle.Active(0).Hp);
        var damage = battle.Log.Skip(from).Where(entry => entry.Kind == "dmg").ToArray();
        Assert.Equal(2, damage.Length);
        Assert.Equal(1, damage[0].Side);
        Assert.Equal(0, damage[1].Side);
        Assert.All(damage, entry => Assert.Equal(Node.First, entry.Node));
        Assert.Equal(attackerHp - reflectedDamage, damage[1].SnapHp[0][0]);
        Assert.Contains(battle.Db.Fields["mirror"].Name, damage[1].Text);
        Assert.False(battle.Active(1).HasStatus("flame"));
        Assert.DoesNotContain(battle.Log.Skip(from), entry => entry.Kind == "trait" && entry.Side == 0);
    }

    [Theory]
    [InlineData(1, 100, 1)]
    [InlineData(39, 100, 11)]
    [InlineData(100, 17, 5)]
    public void MirrorReflectsFixedDamageUsingActualHpLossAndMinimumOne(int requestedDamage, int defenderHp, int expectedReflection)
    {
        var battle = Make();
        Equip(battle, 0, FixedDamage(requestedDamage));
        battle.Active(1).Hp = defenderHp;
        battle.Active(1).Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        int attackerHp = battle.Active(0).Hp;
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(Math.Max(0, defenderHp - requestedDamage), battle.Active(1).Hp);
        Assert.Equal(attackerHp - expectedReflection, battle.Active(0).Hp);
        var reflected = Assert.Single(battle.Log.Skip(from), entry => entry.Kind == "dmg" && entry.Side == 0);
        Assert.Equal(Node.First, reflected.Node);
        Assert.Equal(attackerHp - expectedReflection, reflected.SnapHp[0][0]);
    }

    [Theory]
    [InlineData("pre")]
    [InlineData("post")]
    [InlineData("normal")]
    public void LethalReflectionStopsTheAttackersLaterEffectsAndDoesNotTriggerItsFaintPersona(string mode)
    {
        var battle = Make("yuan", "bell");
        var attacker = battle.Active(0);
        var defender = battle.Active(1);
        attacker.Hp = 1;
        defender.Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        var skill = Equip(battle, 0);
        skill.Cat = "interact";
        skill.Power = mode == "post" ? 0 : 40;
        if (mode != "normal")
        {
            var fixedDamage = FixedDamage(40);
            fixedDamage.At = mode;
            skill.Effects.Add(fixedDamage);
        }
        skill.Effects.Add(new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "disconnect" });
        int defenderHp = defender.Hp;
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.True(attacker.Fainted);
        Assert.Equal(Phase.Replace, battle.Phase);
        Assert.True(battle.Need[0]);
        Assert.False(defender.HasStatus("disconnect"));
        Assert.All(defender.Stages.Values, stage => Assert.Equal(0, stage));
        Assert.DoesNotContain(battle.Log.Skip(from), entry => entry.Kind == "trait" && entry.Side == 0);
        Assert.Single(battle.Log.Skip(from), entry => entry.Kind == "dmg" && entry.Side == 1);
        if (mode != "normal") Assert.Equal(defenderHp - 40, defender.Hp);
    }

    [Fact]
    public void DefeatingTheLastOpponentEndsTheBattleBeforeMirrorCanReflect()
    {
        var battle = Make();
        battle.Active(0).Hp = 1;
        battle.Active(1).Hp = 17;
        battle.Active(1).Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        foreach (var bench in battle.Sides[1].Team.Skip(1))
        {
            bench.Hp = 0;
            bench.Fainted = true;
        }
        Equip(battle, 0, FixedDamage(100));
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(0, battle.Winner);
        Assert.Equal(1, battle.Active(0).Hp);
        Assert.False(battle.Active(0).Fainted);
        Assert.DoesNotContain(battle.Log.Skip(from), entry => entry.Kind == "dmg" && entry.Side == 0);
        Assert.Equal(Node.Over, battle.Log.Last().Node);
    }

    [Fact]
    public void MirrorDoesNotReflectSelfInflictedFixedDamage()
    {
        var battle = Make();
        var effect = FixedDamage(17);
        effect.Target = "self";
        Equip(battle, 0, effect);
        battle.Active(0).Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        int hp = battle.Active(0).Hp;
        int foeHp = battle.Active(1).Hp;
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(hp - 17, battle.Active(0).Hp);
        Assert.Equal(foeHp, battle.Active(1).Hp);
        Assert.Single(battle.Log.Skip(from), entry => entry.Kind == "dmg");
    }

    [Fact]
    public void MirrorDoesNotReflectDamageOverTime()
    {
        var battle = Make();
        var fighter = battle.Active(0);
        fighter.Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        fighter.Statuses.Add(new StatusInst { Id = "hater", Left = 2 });
        int hp = fighter.Hp;
        int foeHp = battle.Active(1).Hp;
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        Assert.Equal(hp - Math.Max(1, (int)(fighter.MaxHp * battle.Db.Statuses["hater"].Dot)), fighter.Hp);
        Assert.Equal(foeHp, battle.Active(1).Hp);
        var damage = Assert.Single(battle.Log.Skip(from), entry => entry.Kind == "dmg");
        Assert.Equal(Node.TurnEnd, damage.Node);
        Assert.Equal(0, damage.Side);
    }

    [Fact]
    public void MirrorDoesNotReflectRecoilDamage()
    {
        var battle = Make();
        var skill = Equip(battle, 0, new EffectDef { At = "post", Code = "RECOIL", Frac = 0.5 });
        skill.Cat = "interact";
        skill.Power = 40;
        battle.Active(0).Fields.Add(new FieldInst { Id = "mirror", Turns = 3 });
        int hp = battle.Active(0).Hp;
        int foeHp = battle.Active(1).Hp;
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        int actualDamage = foeHp - battle.Active(1).Hp;
        Assert.True(actualDamage > 0);
        Assert.Equal(hp - Math.Max(1, (int)(actualDamage * 0.5)), battle.Active(0).Hp);
        Assert.Equal(2, battle.Log.Skip(from).Count(entry => entry.Kind == "dmg"));
    }

    [Theory]
    [InlineData("fixed_pre")]
    [InlineData("fixed_post")]
    [InlineData("recoil")]
    public void OrdinarySelfDamageCancelsLaterEffectsWhenTeammatesRemain(string mode)
    {
        var battle = Make();
        battle.Active(0).Hp = 1;
        var skill = Equip(battle, 0);
        if (mode == "recoil")
        {
            skill.Cat = "interact";
            skill.Power = 40;
            skill.Effects.Add(new EffectDef { At = "post", Code = "RECOIL", Frac = 0.5 });
        }
        else
        {
            skill.Effects.Add(new EffectDef
            {
                At = mode == "fixed_pre" ? "pre" : "post", Code = "FIXED_DAMAGE", Target = "self", N = 10
            });
        }
        skill.Effects.Add(new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "disconnect" });
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.True(battle.Active(0).Fainted);
        Assert.Equal(Phase.Replace, battle.Phase);
        Assert.True(battle.Need[0]);
        Assert.False(battle.Active(1).HasStatus("disconnect"));
        var actionLog = battle.Log.Skip(from).ToList();
        int faintIndex = actionLog.FindIndex(entry => entry.Kind == "faint" && entry.Side == 0);
        Assert.DoesNotContain(actionLog, entry => entry.Kind == "status" && entry.Side == 1 && entry.Text.Contains(battle.Db.Statuses["disconnect"].Name));
        var interruption = Assert.Single(actionLog, entry => entry.Side == 0 && entry.Text.Contains("技能中断"));
        Assert.True(faintIndex >= 0 && actionLog.IndexOf(interruption) > faintIndex);
        Assert.Equal(Node.First, interruption.Node);
    }

    [Fact]
    public void FirewallBlocksWeakStatusButAllowsControl()
    {
        var battle = Make();
        Equip(battle, 0,
            new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "flame" },
            new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "disconnect" });
        battle.Active(1).Fields.Add(new FieldInst { Id = "firewall", Turns = 3 });
        int hp = battle.Active(1).Hp;
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));

        Assert.False(battle.Active(1).HasStatus("flame"));
        Assert.True(battle.Active(1).HasStatus("disconnect"));
        Assert.Equal(1, battle.ControlOf(battle.Active(1)).Left);
        Assert.Equal(hp, battle.Active(1).Hp);
        Assert.Contains(battle.Log.Skip(from), entry => entry.Side == 1 && entry.Node == Node.First && entry.Text.Contains("被防火墙挡下"));
        Assert.DoesNotContain(battle.Log.Skip(from), entry => entry.Kind == "act" && entry.Side == 1);
    }

    [Fact]
    public void ApplyingFirewallDoesNotCleanseExistingWeakStatus()
    {
        var battle = Make();
        Equip(battle, 0, Field("firewall", "self", 3));
        var fighter = battle.Active(0);
        fighter.Statuses.Add(new StatusInst { Id = "hater", Left = 3 });
        int hp = fighter.Hp;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.True(fighter.HasField("firewall"));
        Assert.Equal(2, fighter.Statuses.Single(status => status.Id == "hater").Left);
        Assert.Equal(hp - Math.Max(1, (int)(fighter.MaxHp * battle.Db.Statuses["hater"].Dot)), fighter.Hp);
    }

    [Fact]
    public void FirewallPreventsRefreshingAnExistingWeakStatus()
    {
        var battle = Make();
        Equip(battle, 0, new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "hater" });
        var target = battle.Active(1);
        target.Statuses.Add(new StatusInst { Id = "hater", Left = 2 });
        target.Fields.Add(new FieldInst { Id = "firewall", Turns = 3 });

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(1, target.Statuses.Single(status => status.Id == "hater").Left);
        Assert.Contains(battle.Log, entry => entry.Side == 1 && entry.Text.Contains("被防火墙挡下"));
    }

    [Fact]
    public void LockinBlocksManualSwitchAndRestoresItAfterExpiry()
    {
        var battle = Make();
        Equip(battle, 0, Field("lockin", "foe", 2));

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        var locked = battle.Active(1);
        Assert.True(locked.HasField("lockin"));
        Assert.Empty(battle.LegalFor(1).Switches);
        Assert.NotEmpty(battle.LegalFor(0).Switches);
        int from = battle.Log.Count;

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.SwitchTo(1));

        Assert.Same(locked, battle.Active(1));
        Assert.False(locked.HasField("lockin"));
        Assert.Equal(5, battle.LegalFor(1).Switches.Count);
        Assert.DoesNotContain(battle.Log.Skip(from), entry => entry.Node == Node.Switch && entry.Side == 1);
        Assert.Contains(battle.Log.Skip(from), entry => entry.Node == Node.TurnEnd && entry.Side == 1 && entry.Text.Contains(battle.Db.Fields["lockin"].Name));

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.SwitchTo(1));

        Assert.Equal(1, battle.Sides[1].Active);
    }

    [Fact]
    public void DispelTurnsRemovesAllThreeNewFieldsAndImmediatelyRestoresSwitches()
    {
        var battle = Make();
        Equip(battle, 1, new EffectDef { At = "post", Code = "DISPEL_TURNS", Target = "foe" });
        foreach (string id in new[] { "mirror", "firewall", "lockin" })
            battle.Active(0).Fields.Add(new FieldInst { Id = id, Turns = 3 });
        Assert.Empty(battle.LegalFor(0).Switches);

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));

        foreach (string id in new[] { "mirror", "firewall", "lockin" }) Assert.False(battle.Active(0).HasField(id));
        Assert.Equal(5, battle.LegalFor(0).Switches.Count);
        Assert.Contains(battle.Log, entry => entry.Side == 0 && entry.Text.Contains("消回合") && entry.Text.Contains(battle.Db.Fields["lockin"].Name));
    }

    [Fact]
    public void LockinDoesNotPreventReplacementAfterFainting()
    {
        var battle = Make();
        var locked = battle.Active(0);
        locked.Hp = 17;
        locked.Fields.Add(new FieldInst { Id = "lockin", Turns = 2 });
        Equip(battle, 1, FixedDamage(100));

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));

        Assert.True(locked.Fainted);
        Assert.Equal(Phase.Replace, battle.Phase);
        Assert.True(battle.Need[0]);
        int replacement = Ai.ChooseReplacement(battle, 0);

        battle.Replace(0, replacement);

        Assert.Equal(replacement, battle.Sides[0].Active);
        Assert.NotSame(locked, battle.Active(0));
        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.False(battle.Active(0).HasField("lockin"));
        Assert.NotEmpty(battle.LegalFor(0).Switches);
    }

    [Fact]
    public void AiNeverSwitchesWhileLockedEvenWhenControlWouldNormallyEncourageSwitching()
    {
        var unlocked = Make();
        unlocked.Active(0).Statuses.Add(new StatusInst { Id = "disconnect", Left = 2 });
        var baseline = Enumerable.Range(0, 64).Select(_ => Ai.Choose(unlocked, 0)).ToArray();
        Assert.Contains(baseline, action => action.Type == ActionType.Switch);

        foreach (var level in new[] { AiLevel.Normal, AiLevel.Easy })
        {
            var locked = Make();
            locked.Active(0).Statuses.Add(new StatusInst { Id = "disconnect", Left = 2 });
            locked.Active(0).Fields.Add(new FieldInst { Id = "lockin", Turns = 2 });
            for (int i = 0; i < 64; i++) Assert.NotEqual(ActionType.Switch, Ai.Choose(locked, 0, level).Type);
        }
    }

    [Theory]
    [InlineData("lockin", 1)]
    [InlineData("mirror", 0)]
    public void AiPreservesTheOpponentsSwitchLockButValuesRemovingItsDefensiveField(string field, int expectedSkill)
    {
        var battle = Make();
        Equip(battle, 0, new EffectDef { At = "post", Code = "DISPEL_TURNS", Target = "foe" });
        const string attackId = "test_t15_light_attack";
        battle.Db.Skills[attackId] = new SkillDef
        {
            Id = attackId, Name = "轻点测试", Type = "talk", Cat = "interact", Power = 1, Pp = 20
        };
        battle.Active(0).Skills[1] = new SkillSlot { Id = attackId, Pp = 20, Max = 20 };
        for (int i = 2; i < battle.Active(0).Skills.Count; i++) battle.Active(0).Skills[i].Pp = 0;
        foreach (var slot in battle.Active(1).Skills) slot.Pp = 0;
        battle.Active(1).Fields.Add(new FieldInst { Id = field, Turns = 3 });

        var action = Ai.Choose(battle, 0);

        Assert.Equal(ActionType.Skill, action.Type);
        Assert.Equal(expectedSkill, action.Index);
    }
}
