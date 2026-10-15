using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

[Collection("Localization current")]
public class T74_BattleReviewTests
{
    static Battle Make(bool original = false, uint seed = 74)
    {
        var db = BattleTests.LoadPack("original");
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        if (!original)
        {
            db.Personas["review_neutral"] = new PersonaDef { Id = "review_neutral", Name = "测试人设", Code = "T_LOWHP_POWER" };
            db.Skills["review_idle"] = new SkillDef { Id = "review_idle", Name = "等待", Type = "talk", Cat = "status", Pp = 40 };
            for (int i = 0; i < db.Streamers.Count; i++)
            {
                var streamer = db.Streamers[i];
                streamer.Persona = "review_neutral";
                streamer.Type = "talk";
                streamer.Type2 = null;
                streamer.Stats["hp"] = 1000;
                foreach (string stat in ContentDb.StatKeys) streamer.Stats[stat] = 100;
                streamer.Stats["spd"] = i < 6 ? 300 : 200;
                streamer.Skills = Enumerable.Repeat("review_idle", 4).ToList();
                streamer.Fifth = "review_idle";
            }
        }
        return new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() }, new[] { "甲方", "乙方" }, seed);
    }

    static SkillDef Equip(Battle battle, int side, int index, string id, params EffectDef[] effects)
    {
        var skill = new SkillDef { Id = id, Name = "复盘测试技能", Type = "talk", Cat = "status", Pp = 40 };
        skill.Effects.AddRange(effects);
        battle.Db.Skills[id] = skill;
        battle.Active(side).Skills[index] = new SkillSlot { Id = id, Pp = 40, Max = 40 };
        return skill;
    }

    static EffectDef Fixed(int amount, string target = "foe")
    {
        return new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = target, N = amount };
    }

    static EffectDef Control(string status = "disconnect", string at = "post")
    {
        return new EffectDef { At = at, Code = "STATUS", Target = "foe", Status = status };
    }

    static List<KeyMoment> Moments(Battle battle, string kind, int side = 0)
    {
        return BattleReview.KeyMoments(battle, side, 1000).Where(moment => moment.Kind == kind).ToList();
    }

    static string Dump<T>(T value)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true });
    }

    static void ReplaceNext(Battle battle, int side)
    {
        int next = Enumerable.Range(0, battle.Sides[side].Team.Count).First(index => !battle.Sides[side].Team[index].Fainted);
        battle.Replace(side, next);
    }

    [Fact]
    public void CompletedSeededBattleProducesOrderedLimitedDeterministicReviewWithoutChangingAnything()
    {
        var battle = Make(original: true);
        int guard = 0;
        while (battle.Phase != Phase.Over && guard++ < 500)
        {
            if (battle.Phase == Phase.Choose) battle.ResolveTurn(Ai.Choose(battle, 0), Ai.Choose(battle, 1));
            else for (int side = 0; side < 2; side++) if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
        }
        Assert.Equal(Phase.Over, battle.Phase);
        ulong hash = battle.StateHash();
        string before = Dump(battle);
        string events = Dump(battle.Events);
        string logs = Dump(battle.Log);
        string record = battle.Record.ToJson();
        var untouched = battle.Clone();

        var first = BattleReview.KeyMoments(battle, 0);
        var second = BattleReview.KeyMoments(battle, 0);

        Assert.NotEmpty(first);
        Assert.True(first.Count <= 5);
        Assert.Contains(first, moment => moment.Kind == "ko");
        Assert.Equal(first.OrderBy(moment => moment.Turn).Select(moment => moment.Turn), first.Select(moment => moment.Turn));
        Assert.All(first, moment => Assert.False(string.IsNullOrWhiteSpace(moment.Text)));
        Assert.Equal(Dump(first), Dump(second));
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(before, Dump(battle));
        Assert.Equal(events, Dump(battle.Events));
        Assert.Equal(logs, Dump(battle.Log));
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(untouched.Random(), battle.Random());
    }

    [Fact]
    public void InvalidViewerArgumentsAreRejectedAndNonpositiveLimitsReturnNothing()
    {
        var battle = Make();
        Assert.Throws<ArgumentNullException>(() => BattleReview.KeyMoments(null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BattleReview.KeyMoments(battle, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BattleReview.KeyMoments(battle, 2));
        Assert.Empty(BattleReview.KeyMoments(battle, 0, 0));
        Assert.Empty(BattleReview.KeyMoments(battle, 0, -1));
        Assert.Empty(BattleReview.KeyMoments(battle, 0));
    }

    [Fact]
    public void SwingUsesTheLargestCompletedTurnDeltaAndReversesForTheOtherViewer()
    {
        var battle = Make();
        Equip(battle, 0, 0, "review_small", Fixed(10));
        Equip(battle, 0, 1, "review_large", Fixed(300));
        Equip(battle, 0, 2, "review_late", Fixed(20));
        for (int slot = 0; slot < 3; slot++) battle.ResolveTurn(BattleAction.Skill(slot), BattleAction.Pass());

        var own = Assert.Single(Moments(battle, "swing"));
        var foe = Assert.Single(Moments(battle, "swing", 1));

        Assert.Equal(2, own.Turn);
        Assert.Equal(300, own.Swing);
        Assert.Equal(own.Turn, foe.Turn);
        Assert.Equal(-own.Swing, foe.Swing);
        Assert.Equal(Dump(new[] { own }), Dump(BattleReview.KeyMoments(battle, 0, 1)));
    }

    [Fact]
    public void EqualSwingCandidatesUseTheirHistoricalEventOrderBeforeApplyingMax()
    {
        var battle = Make();
        string ownName = battle.Active(0).Name;
        string foeName = battle.Active(1).Name;
        Equip(battle, 0, 0, "review_double_faint", Fixed(2000), Fixed(2000, "self"));
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        var all = Moments(battle, "ko");
        var limited = BattleReview.KeyMoments(battle, 0, 1);

        Assert.Equal(2, all.Count);
        Assert.All(all, moment => { Assert.Equal(1, moment.Turn); Assert.Equal(0, moment.Swing); });
        Assert.Contains(foeName, all[0].Text);
        Assert.Contains(ownName, all[1].Text);
        Assert.Equal(Dump(new[] { all[0] }), Dump(limited));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("self")]
    [InlineData("dot")]
    public void KoRequiresAnActualFaintAndReportsTheVictimWithoutInventingAnAttacker(string cause)
    {
        var battle = Make();
        int victimSide = cause == "direct" ? 1 : 0;
        string victim = battle.Active(victimSide).Name;
        string other = battle.Active(1 - victimSide).Name;
        if (cause == "dot")
        {
            Equip(battle, 0, 0, "review_dot", Fixed(900, "self"),
                new EffectDef { At = "post", Code = "STATUS", Target = "self", Status = "hater" });
        }
        else Equip(battle, 0, 0, "review_ko", Fixed(2000, cause == "self" ? "self" : "foe"));
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.True(battle.Active(victimSide).Fainted);

        var moment = Assert.Single(Moments(battle, "ko"));

        Assert.Equal(1, moment.Turn);
        Assert.Contains(victim, moment.Text);
        Assert.DoesNotContain(other, moment.Text);
    }

    [Fact]
    public void SurvivingLethalDamageWithEndureIsNotAKo()
    {
        var battle = Make();
        battle.Db.Personas["review_endure"] = new PersonaDef { Id = "review_endure", Name = "保命测试", Code = "T_ENDURE" };
        battle.Active(1).Def.Persona = "review_endure";
        Equip(battle, 0, 0, "review_lethal", Fixed(2000));

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());

        Assert.Equal(1, battle.Active(1).Hp);
        Assert.False(battle.Active(1).Fainted);
        Assert.Empty(Moments(battle, "ko"));
    }

    [Fact]
    public void ControlMomentRequiresTheControlledFighterToActuallyLoseAnAction()
    {
        var battle = Make();
        Equip(battle, 0, 0, "review_control", Control()).Prio = 5;
        string target = battle.Active(1).Name;

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));

        Assert.Contains(battle.Events, entry => entry.Kind == BattleEventKind.StatusAdd && entry.Side == 1);
        Assert.DoesNotContain(battle.Events, entry => entry.Kind == BattleEventKind.Act && entry.Side == 1);
        var moment = Assert.Single(Moments(battle, "control"));
        Assert.Equal(1, moment.Turn);
        Assert.Contains(target, moment.Text);
    }

    [Fact]
    public void AControlMessageInsideAFighterNameDoesNotInventAnotherLostAction()
    {
        var battle = Make();
        battle.Active(1).Def.Name = "甲 处于「假状态」，无法行动";
        Equip(battle, 0, 0, "review_name_control", Control("stall")).Prio = 5;
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        int previousCount = Moments(battle, "control").Count;
        Assert.Equal(1, previousCount);
        Equip(battle, 0, 1, "review_name_weak", Control("hater")).Prio = 5;

        battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Skill(0));

        Assert.Contains(battle.Events, entry => entry.Turn == 2 && entry.Side == 1 && entry.Kind == BattleEventKind.StatusAdd && entry.Id == "hater");
        Assert.Contains(battle.Events, entry => entry.Turn == 2 && entry.Side == 1 && entry.Kind == BattleEventKind.Act);
        Assert.Equal(previousCount, Moments(battle, "control").Count);
    }

    [Theory]
    [InlineData("immune")]
    [InlineData("late")]
    [InlineData("woken")]
    [InlineData("fainted")]
    [InlineData("switched")]
    public void ApplyingOrAttemptingControlAloneDoesNotProveASkippedAction(string scenario)
    {
        var battle = Make();
        var skill = Equip(battle, 0, 0, "review_control_attempt", Control());
        skill.Prio = scenario == "late" || scenario == "switched" ? -1 : 5;
        if (scenario == "immune") battle.Active(1).Fields.Add(new FieldInst { Id = "reconnect", Turns = 3 });
        if (scenario == "woken")
        {
            skill.Effects[0] = Control("afk", "pre");
            skill.Power = 30;
            skill.Cat = "interact";
        }
        if (scenario == "fainted") skill.Effects.Add(Fixed(2000));

        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        if (scenario == "switched")
        {
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.SwitchTo(1));
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        }

        Assert.Empty(Moments(battle, "control"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelayedControlUsesTheTurnOfTheLostActionIncludingWakeAfterNormalization(bool wake)
    {
        var battle = Make();
        Equip(battle, 0, 0, "review_late_control", Control(wake ? "afk" : "disconnect")).Prio = -1;
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        Assert.Empty(Moments(battle, "control"));
        var attack = Equip(battle, 0, 1, "review_wake");
        attack.Power = 30;
        attack.Cat = "interact";
        attack.Prio = 5;

        battle.ResolveTurn(wake ? BattleAction.Skill(1) : BattleAction.Pass(), BattleAction.Skill(0));

        if (wake) Assert.Contains(battle.Log, entry => entry.Turn == 2 && entry.Side == 1 && entry.Text.Contains("刚回过神"));
        var moment = Assert.Single(Moments(battle, "control"));
        Assert.Equal(2, moment.Turn);
    }

    [Fact]
    public void VoluntarySwitchTracksActualResistanceAndLaterEffectiveHitsOncePerDirection()
    {
        var battle = Make();
        battle.Sides[0].Team[1].Def.Type = "song";
        var enemyAttack = Equip(battle, 1, 0, "review_enemy_attack");
        enemyAttack.Power = 40;
        enemyAttack.Cat = "interact";
        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Skill(0));
        var ownAttack = Equip(battle, 0, 0, "review_song_attack");
        ownAttack.Type = "song";
        ownAttack.Power = 40;
        ownAttack.Cat = "interact";
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));

        var moments = Moments(battle, "switch");

        Assert.Equal(2, moments.Count);
        Assert.Equal(new[] { 1, 2 }, moments.Select(moment => moment.Turn));
        Assert.All(moments, moment => Assert.Contains(battle.Active(0).Name, moment.Text));
    }

    [Theory]
    [InlineData("neutral")]
    [InlineData("fixed")]
    [InlineData("replacement")]
    public void AChangeOfFighterWithoutVoluntaryEffectiveDamageIsNotAUsefulSwitch(string scenario)
    {
        var battle = Make();
        battle.Sides[0].Team[1].Def.Type = "song";
        if (scenario == "replacement")
        {
            Equip(battle, 1, 0, "review_enemy_ko", Fixed(2000));
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
            battle.Replace(0, 1);
        }
        var attack = Equip(battle, 1, 0, "review_enemy_check", scenario == "fixed" ? new[] { Fixed(50) } : Array.Empty<EffectDef>());
        if (scenario != "fixed")
        {
            attack.Power = 40;
            attack.Cat = "interact";
            if (scenario == "neutral") attack.Type = "game";
        }

        battle.ResolveTurn(scenario == "replacement" ? BattleAction.Pass() : BattleAction.SwitchTo(1), BattleAction.Skill(0));

        Assert.Empty(Moments(battle, "switch"));
    }

    static Battle ComebackGame(int losses, bool viewerWins, out Battle unfinished)
    {
        var battle = Make();
        var kill = Equip(battle, 0, 0, "review_team_ko", Fixed(2000));
        kill.Prio = 5;
        foreach (var side in battle.Sides)
            foreach (var fighter in side.Team) fighter.Skills[0] = new SkillSlot { Id = kill.Id, Pp = 40, Max = 40 };
        for (int i = 0; i < losses; i++)
        {
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
            ReplaceNext(battle, 0);
        }
        unfinished = battle.Clone();
        while (battle.Phase != Phase.Over)
        {
            battle.ResolveTurn(viewerWins ? BattleAction.Skill(0) : BattleAction.Pass(), viewerWins ? BattleAction.Pass() : BattleAction.Skill(0));
            if (battle.Phase == Phase.Replace) ReplaceNext(battle, viewerWins ? 1 : 0);
        }
        return battle;
    }

    [Fact]
    public void ComebackStartsAtTheFirstHalfHpDeficitAndRequiresTheViewersActualVictory()
    {
        var battle = ComebackGame(3, true, out var unfinished);

        Assert.Empty(Moments(unfinished, "comeback"));
        Assert.Equal(0, battle.Winner);
        var moment = Assert.Single(Moments(battle, "comeback"));
        Assert.Equal(3, moment.Turn);
        Assert.Equal(6000, moment.Swing);
        Assert.Empty(Moments(battle, "comeback", 1));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void SmallerDeficitsAndLossesDoNotBecomeComebackClaims(int losses, bool viewerWins)
    {
        var battle = ComebackGame(losses, viewerWins, out _);

        Assert.Empty(Moments(battle, "comeback"));
    }

    [Fact]
    public void ReviewTextUsesInjectedLocalizationWhileKeepingTheHistoricalFighterName()
    {
        var battle = Make();
        string victim = battle.Active(1).Name;
        Equip(battle, 0, 0, "review_localized_ko", Fixed(2000));
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        var previous = Loc.Current;
        try
        {
            Loc.Current = Loc.FromJson(JsonSerializer.Serialize(new
            {
                language = "test",
                texts = new Dictionary<string, string>
                {
                    { "review.side.self", "SELF" }, { "review.side.foe", "FOE" },
                    { "review.ko", "KO[{0}|{1}|{2}|{3}|{4}]" },
                    { "review.swing", "SWING[{0}|{1}|{2}|{3}]" }
                }
            }));

            var moments = BattleReview.KeyMoments(battle, 0, 1000);

            Assert.Equal("KO[1|FOE|" + victim + "|6|5]", Assert.Single(moments, moment => moment.Kind == "ko").Text);
            Assert.Equal("SWING[1|0|+1000|+1000]", Assert.Single(moments, moment => moment.Kind == "swing").Text);
        }
        finally { Loc.Current = previous; }
    }

    [Fact]
    public void ReviewDependsOnHistoricalLogsAndEventsRatherThanCurrentFightersDefinitionsOrWinner()
    {
        var battle = ComebackGame(3, true, out _);
        string expected = Dump(BattleReview.KeyMoments(battle, 0, 1000));
        var sides = battle.Sides.ToArray();
        var streamers = battle.Db.Streamers;
        var skills = battle.Db.Skills;
        var statuses = battle.Db.Statuses;
        var fields = battle.Db.Fields;
        var personas = battle.Db.Personas;
        var phase = battle.Phase;
        int winner = battle.Winner;
        try
        {
            battle.Sides[0] = battle.Sides[1] = null;
            battle.Db.Streamers = null;
            battle.Db.Skills = null;
            battle.Db.Statuses = null;
            battle.Db.Fields = null;
            battle.Db.Personas = null;
            battle.Phase = Phase.Choose;
            battle.Winner = 1;

            Assert.Equal(expected, Dump(BattleReview.KeyMoments(battle, 0, 1000)));
        }
        finally
        {
            battle.Sides[0] = sides[0];
            battle.Sides[1] = sides[1];
            battle.Db.Streamers = streamers;
            battle.Db.Skills = skills;
            battle.Db.Statuses = statuses;
            battle.Db.Fields = fields;
            battle.Db.Personas = personas;
            battle.Phase = phase;
            battle.Winner = winner;
        }
    }
}
