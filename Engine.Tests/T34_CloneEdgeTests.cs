using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T34_CloneEdgeTests
{
    static ContentDb Db()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["clone_none"] = new PersonaDef { Id = "clone_none", Name = "拷贝测试", Code = "TEST_NONE" };
        AddSkill(db, "clone_poke", new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 10 });
        AddSkill(db, "clone_kill", new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 99999 });
        AddSkill(db, "clone_control", new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "disconnect" });
        AddSkill(db, "clone_wait");
        foreach (var streamer in db.Streamers)
        {
            streamer.Persona = "clone_none";
            streamer.Stats["hp"] = 100;
            streamer.Skills = new List<string> { "clone_poke", "clone_kill", "clone_control", "clone_wait" };
            streamer.Fifth = "clone_wait";
        }
        return db;
    }

    static void AddSkill(ContentDb db, string id, params EffectDef[] effects)
    {
        var skill = new SkillDef { Id = id, Name = "拷贝测试技能", Type = "talk", Cat = "status", Pp = 50, Prio = 5 };
        skill.Effects.AddRange(effects);
        db.Skills[id] = skill;
    }

    static Battle Make(ContentDb db = null, bool ban = false, bool lead = false)
    {
        return new Battle(db ?? Db(), new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "甲方", "乙方" }, new BattleOptions { Seed = 34, BanOne = ban, LeadPick = lead });
    }

    static string[] FullLog(Battle battle)
    {
        return battle.Log.Select(entry => string.Join("|", entry.Turn, entry.Node, entry.Kind, entry.Side, entry.Text,
            string.Join(",", entry.SnapActive), string.Join(",", entry.SnapHp[0]), string.Join(",", entry.SnapHp[1]))).ToArray();
    }

    static void EqualBattle(Battle expected, Battle actual)
    {
        Assert.Equal(expected.Seed, actual.Seed);
        Assert.Equal(expected.StateHash(), actual.StateHash());
        Assert.Equal(expected.Record.ToJson(), actual.Record.ToJson());
        Assert.Equal(FullLog(expected), FullLog(actual));
        Assert.Equal(BattleSummary.From(expected).ToText(), BattleSummary.From(actual).ToText());
    }

    [Fact]
    public void EveryFighterCanBeEditedInACloneWithoutChangingTheOriginal()
    {
        var battle = Make();
        foreach (var fighter in battle.Sides.SelectMany(side => side.Team))
        {
            fighter.Hp -= fighter.Slot;
            fighter.LastSkillIndex = fighter.Slot % fighter.Skills.Count;
            fighter.EnteredTurn = fighter.Slot + 1;
            fighter.FirstControlImmunityUsed = fighter.Slot % 2 == 0;
            fighter.EndureUsed = fighter.Slot % 2 != 0;
            fighter.Stages["def"] = fighter.Slot - 3;
            fighter.Statuses.Add(new StatusInst { Id = "hater", Left = fighter.Slot });
            fighter.Fields.Add(new FieldInst { Id = "mirror", Turns = fighter.Slot, Stacks = 2 });
            fighter.Skills[0].Pp -= fighter.Slot;
        }
        ulong before = battle.StateHash();
        string[] beforeLog = FullLog(battle);
        var clone = battle.Clone();
        EqualBattle(battle, clone);
        Assert.Same(battle.Db, clone.Db);

        foreach (var fighter in clone.Sides.SelectMany(side => side.Team))
        {
            fighter.Hp--;
            fighter.FirstControlImmunityUsed = !fighter.FirstControlImmunityUsed;
            fighter.EndureUsed = !fighter.EndureUsed;
            fighter.Stages["def"] = 6;
            fighter.Statuses[0].Left++;
            fighter.Statuses.Add(new StatusInst { Id = "disconnect", Left = 2 });
            fighter.Fields[0].Turns++;
            fighter.Fields[0].Stacks++;
            fighter.Fields.Add(new FieldInst { Id = "reconnect", Turns = 2 });
            fighter.Skills[0].Pp = 0;
            fighter.Skills.RemoveAt(4);
        }
        clone.Sides[0].Team.Reverse();
        clone.Sides[1].Team.RemoveAt(5);
        clone.Sides[0].Name = "试算分支";
        clone.Need[0] = true;
        clone.ActedFirst[1] = true;

        Assert.Equal(before, battle.StateHash());
        Assert.Equal(beforeLog, FullLog(battle));
        Assert.Equal("甲方", battle.Sides[0].Name);
        Assert.All(battle.Sides, side => Assert.Equal(6, side.Team.Count));
        Assert.All(battle.Sides.SelectMany(side => side.Team), fighter =>
        {
            Assert.Single(fighter.Statuses);
            Assert.Single(fighter.Fields);
            Assert.Equal(5, fighter.Skills.Count);
        });
    }

    [Fact]
    public void CloneKeepsTheCurrentRandomPositionWithoutAdvancingTheOriginal()
    {
        var battle = Make();
        for (int i = 0; i < 7; i++) battle.Random();
        ulong before = battle.StateHash();
        var clone = battle.Clone();
        Assert.Equal(before, battle.StateHash());
        Assert.Equal(before, clone.StateHash());

        double[] expected = Enumerable.Range(0, 12).Select(_ => clone.Random()).ToArray();

        Assert.Equal(before, battle.StateHash());
        Assert.NotEqual(before, clone.StateHash());
        Assert.Equal(expected, Enumerable.Range(0, 12).Select(_ => battle.Random()));
        Assert.Equal(battle.StateHash(), clone.StateHash());
    }

    [Fact]
    public void EditingRecordEventsNamesAndNestedTeamListsNeverTouchesTheOriginalRecord()
    {
        var battle = Make(ban: true, lead: true);
        battle.ChooseBan(0, 1);
        battle.ChooseBan(1, 2);
        battle.ChooseLead(0, 3);
        battle.ChooseLead(1, 4);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        string before = battle.Record.ToJson();
        var clone = battle.Clone();
        Assert.Equal(before, clone.Record.ToJson());

        clone.Record.Names[0] = "改名";
        clone.Record.Teams[0][0] = "改队员";
        clone.Record.Teams[1].RemoveAt(5);
        clone.Record.Events[0].To = 5;
        clone.Record.Events[0].RngState = 99;
        clone.Record.Events.Last().A0 = BattleAction.Pass();
        clone.Record.Events.Add(new BattleRecordEvent { Type = BattleRecordEventType.Turn, A0 = BattleAction.Pass(), A1 = BattleAction.Pass() });
        clone.Record.Seed++;

        Assert.Equal(before, battle.Record.ToJson());
        string changedClone = clone.Record.ToJson();
        battle.Record.Events.Clear();
        Assert.Equal(changedClone, clone.Record.ToJson());
    }

    [Fact]
    public void EditingExistingLogEntriesAndTheirSnapshotsNeverTouchesOriginalHistory()
    {
        var battle = Make();
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        string[] before = FullLog(battle);
        var clone = battle.Clone();

        clone.Log[0].Text = "试算日志";
        clone.Log[0].SnapActive[0] = 4;
        clone.Log[0].SnapHp[0][0] = 1;
        clone.Log[0].SnapHp[1] = new[] { 1 };
        clone.Log.RemoveAt(1);

        Assert.Equal(before, FullLog(battle));
        string[] changedClone = FullLog(clone);
        battle.Log.Clear();
        Assert.Equal(changedClone, FullLog(clone));
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(2, "saved-content-fingerprint")]
    public void ClonePreservesRecordVersionAndSavedFingerprintWithoutRecomputingThem(int version, string fingerprint)
    {
        var battle = Make();
        battle.Record.Version = version;
        battle.Record.ContentHash = fingerprint;

        var clone = battle.Clone();

        EqualBattle(battle, clone);
        Assert.Equal(version, clone.Record.Version);
        Assert.Equal(fingerprint, clone.Record.ContentHash);
        clone.Record.Version = 99;
        clone.Record.ContentHash = "changed";
        Assert.Equal(version, battle.Record.Version);
        Assert.Equal(fingerprint, battle.Record.ContentHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartiallyConfirmedBanCanFinishIndependentlyWithOrWithoutLeadSelection(bool lead)
    {
        var battle = Make(ban: true, lead: lead);
        battle.ChooseBan(1, 3);
        var clone = battle.Clone();
        EqualBattle(battle, clone);
        Assert.True(clone.HasChosenBan(1));
        Assert.False(clone.HasChosenBan(0));
        ulong before = battle.StateHash();

        clone.ChooseBan(0, 2);

        Assert.Equal(before, battle.StateHash());
        Assert.Equal(Phase.Ban, battle.Phase);
        Assert.False(battle.HasChosenBan(0));
        Assert.Equal(6, battle.Sides[0].Team.Count);
        Assert.Equal(lead ? Phase.Lead : Phase.Choose, clone.Phase);
        Assert.All(clone.Sides, side => Assert.Equal(5, side.Team.Count));
        battle.ChooseBan(0, 2);
        EqualBattle(battle, clone);
        if (lead)
        {
            for (int side = 0; side < 2; side++)
            {
                clone.ChooseLead(side, side + 2);
                battle.ChooseLead(side, side + 2);
                EqualBattle(battle, clone);
            }
        }
        clone.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        EqualBattle(battle, clone);
    }

    [Fact]
    public void FinishingClonedLeadSelectionDoesNotRemapOriginalBanSnapshots()
    {
        var db = Db();
        for (int i = 0; i < db.Streamers.Count; i++) db.Streamers[i].Stats["hp"] += i * 13;
        var battle = Make(db, ban: true, lead: true);
        battle.ChooseBan(0, 1);
        battle.ChooseBan(1, 3);
        battle.ChooseLead(1, 4);
        var clone = battle.Clone();
        string[] beforeLog = FullLog(battle);
        ulong before = battle.StateHash();
        Assert.True(clone.HasChosenLead(1));
        Assert.False(clone.HasChosenLead(0));

        clone.ChooseLead(0, 2);

        Assert.Equal(before, battle.StateHash());
        Assert.Equal(beforeLog, FullLog(battle));
        Assert.Equal(Phase.Lead, battle.Phase);
        Assert.False(battle.HasChosenLead(0));
        Assert.NotEqual(battle.Log[0].SnapHp[0], clone.Log[0].SnapHp[0]);
        battle.ChooseLead(0, 2);
        EqualBattle(battle, clone);
    }

    [Fact]
    public void CumulativeSummaryFollowsClonedFightersAcrossSwitchesAndFurtherDamage()
    {
        var battle = Make();
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Skill(0));
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.SwitchTo(2));
        var clone = battle.Clone();
        EqualBattle(battle, clone);
        string before = BattleSummary.From(battle).ToText();
        ulong beforeHash = battle.StateHash();

        clone.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));

        Assert.Equal(before, BattleSummary.From(battle).ToText());
        Assert.Equal(beforeHash, battle.StateHash());
        var advanced = BattleSummary.From(clone);
        Assert.Equal(10, advanced.Sides[0][0].DamageDealt);
        Assert.Equal(20, advanced.Sides[0][1].DamageDealt);
        Assert.Equal(20, advanced.Sides[0][1].DamageTaken);
        Assert.Equal(2, advanced.Sides[0][1].SkillUses["clone_poke"]);
        Assert.Equal(3, advanced.Sides[0][1].TurnsActive);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        EqualBattle(battle, clone);

        string cloneBefore = BattleSummary.From(clone).ToText();
        battle.ResolveTurn(BattleAction.SwitchTo(0), BattleAction.Pass());
        Assert.Equal(cloneBefore, BattleSummary.From(clone).ToText());
    }

    [Theory]
    [InlineData("T_ENDURE")]
    [InlineData("T_FIRST_CONTROL_IMMUNE")]
    public void SpentPersonaProtectionStaysSpentOnCloneAndOriginal(string code)
    {
        var db = Db();
        db.Personas["clone_protection"] = new PersonaDef { Id = "clone_protection", Name = "拷贝保护", Code = code };
        db.Streamer("bell").Persona = "clone_protection";
        var battle = Make(db);
        int attack = code == "T_ENDURE" ? 1 : 2;
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(attack));
        Assert.False(battle.Active(0).Fainted);
        Assert.Null(battle.ControlOf(battle.Active(0)));
        var clone = battle.Clone();
        ulong before = battle.StateHash();

        clone.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(attack));

        Assert.Equal(before, battle.StateHash());
        if (code == "T_ENDURE") Assert.True(clone.Active(0).Fainted);
        else Assert.NotNull(clone.ControlOf(clone.Active(0)));
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(attack));
        EqualBattle(battle, clone);
    }

    [Fact]
    public void CloneDuringDoubleReplacementPreservesBothNeedsAndFaintStatistics()
    {
        var db = Db();
        db.Skills["clone_poke"].Cat = "interact";
        db.Skills["clone_poke"].Power = 10000;
        db.Skills["clone_poke"].Effects.Clear();
        db.Skills["clone_poke"].Effects.Add(new EffectDef { At = "post", Code = "RECOIL", Frac = 1 });
        var battle = Make(db);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.Equal(Phase.Replace, battle.Phase);
        Assert.All(battle.Need, needed => Assert.True(needed));
        var clone = battle.Clone();
        EqualBattle(battle, clone);
        for (int side = 0; side < 2; side++)
        {
            ulong before = battle.StateHash();
            clone.Replace(side, side + 1);
            Assert.Equal(before, battle.StateHash());
            battle.Replace(side, side + 1);
            EqualBattle(battle, clone);
        }

        Assert.Equal(Phase.Choose, clone.Phase);
        clone.ResolveTurn(BattleAction.Skill(3), BattleAction.Skill(3));
        battle.ResolveTurn(BattleAction.Skill(3), BattleAction.Skill(3));
        EqualBattle(battle, clone);
        Assert.All(BattleSummary.From(clone).Sides, side =>
        {
            Assert.True(side[0].Fainted);
            Assert.Equal(1, side[0].Kos);
        });
    }

    [Fact]
    public void FinishedBattleClonesKeepFinalHistoryStatisticsAndRejectFurtherTurns()
    {
        var battle = Make();
        for (int i = 0; i < 6; i++)
        {
            battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
            if (battle.Phase == Phase.Replace) battle.Replace(1, i + 1);
        }
        Assert.Equal(Phase.Over, battle.Phase);
        var clone = battle.Clone();
        EqualBattle(battle, clone);
        Assert.Equal(0, clone.Winner);
        Assert.Equal(6, BattleSummary.From(clone).Sides[0][0].Kos);
        ulong before = clone.StateHash();

        Assert.Throws<InvalidOperationException>(() => clone.ResolveTurn(BattleAction.Pass(), BattleAction.Pass()));
        Assert.Throws<InvalidOperationException>(() => clone.Replace(1, 0));

        Assert.Equal(before, clone.StateHash());
        EqualBattle(battle, clone);
    }
}
