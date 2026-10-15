using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T111_HotseatTimerTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    [Fact]
    public void DefaultAndExplicitZeroLimitsNeverSubmitAnAction()
    {
        var db = Fixture();
        var teams = Teams(db);
        var defaultSession = new HotseatSession(db, teams[0], teams[1], "甲", "乙", Options());
        var explicitSession = Session(db, 0);
        foreach (var session in new[] { defaultSession, explicitSession })
        {
            Assert.Equal(0, session.TurnTimeLimitSeconds);
            Assert.Equal(0, session.TurnTimeLeftSeconds);
            string before = State(session);
            foreach (double elapsed in new[] { 0, 10, 60, 1000000, double.MaxValue }) Assert.Empty(session.Tick(elapsed));
            Assert.Equal(before, State(session));
        }
        Assert.Equal(defaultSession.Record.ToJson(), explicitSession.Record.ToJson());
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(60)]
    public void EachSupportedLimitExpiresExactlyAtItsDeadlineAndOnlyForTheCurrentPlayer(int limit)
    {
        var db = Fixture();
        var session = Session(db, limit);
        string visible = Visible(session);
        Assert.Equal(limit, session.TurnTimeLimitSeconds);
        Assert.Equal(limit, session.TurnTimeLeftSeconds);
        Assert.Empty(session.Tick(limit - 0.25));
        Assert.Equal(0.25, session.TurnTimeLeftSeconds, 10);

        var timeout = Assert.Single(session.Tick(0.25));

        AssertTimeout(timeout, 0, BattleAction.Skill(0));
        Assert.Contains(db.Skills["t111_s0"].Name, timeout.Text);
        Assert.Equal(visible, Visible(session));
        Assert.True(session.NeedsHandover);
        Assert.Equal(1, session.WhoseTurn);
        Assert.Equal(limit, session.TurnTimeLeftSeconds);
        Assert.Empty(session.Tick(double.MaxValue));
        Assert.Equal(visible, Visible(session));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(40)]
    [InlineData(int.MaxValue)]
    public void UnsupportedLimitsAreRejectedWithoutChangingContent(int limit)
    {
        var db = Fixture();
        string before = db.ContentHash();

        Assert.Throws<ArgumentOutOfRangeException>(() => Session(db, limit));

        Assert.Equal(before, db.ContentHash());
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-0.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidElapsedTimeLeavesCurrentCountdownAndPrivateChoicesUntouched(double elapsed)
    {
        var session = Session(Fixture());
        Assert.Empty(session.Tick(3));
        string before = State(session);

        Assert.Throws<ArgumentOutOfRangeException>(() => session.Tick(elapsed));

        Assert.Equal(before, State(session));
        Assert.Empty(session.Tick(6));
        Assert.Single(session.Tick(1));
        string handover = State(session);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Tick(elapsed));
        Assert.Equal(handover, State(session));
    }

    [Fact]
    public void OneHundredDecimalTicksMatchOneExactDeadlineWithoutEarlyOrLateSubmission()
    {
        var db = Fixture();
        var split = Session(db);
        var whole = Session(db);
        for (int i = 0; i < 99; i++) Assert.Empty(split.Tick(0.1));
        Assert.Equal(0.1, split.TurnTimeLeftSeconds, 10);

        var incremental = Assert.Single(split.Tick(0.1));
        var single = Assert.Single(whole.Tick(10));

        Assert.Equal(Json(single), Json(incremental));
        Assert.Equal(State(whole), State(split));
        Assert.Empty(split.Tick(0));
    }

    [Fact]
    public void ElapsedTimeMayContinueIntoTheSamePlayersNextTurnButStopsAtHandover()
    {
        var db = Fixture();
        var whole = Session(db);
        var split = Session(db);
        foreach (var session in new[] { whole, split })
        {
            session.Submit(BattleAction.Skill(3));
            session.ConfirmHandover();
            Assert.Equal(1, session.WhoseTurn);
        }

        var timeouts = whole.Tick(25);
        var segments = new List<HotseatTimeout>();
        foreach (double elapsed in new[] { 4, 6, 10, 5 }) segments.AddRange(split.Tick(elapsed));

        Assert.Equal(2, timeouts.Count);
        Assert.All(timeouts, timeout => AssertTimeout(timeout, 1, BattleAction.Skill(0)));
        Assert.Equal(Json(timeouts), Json(segments));
        Assert.Equal(State(whole), State(split));
        var turn = Assert.Single(whole.Record.Events);
        AssertAction(BattleAction.Skill(3), turn.A0);
        AssertAction(BattleAction.Skill(0), turn.A1);
        Assert.True(whole.NeedsHandover);
        Assert.Equal(0, whole.WhoseTurn);
        Assert.Equal(10, whole.TurnTimeLeftSeconds);
        whole.ConfirmHandover();
        Assert.Empty(whole.Tick(9));
        Assert.Single(whole.Tick(1));
        Assert.Equal(2, whole.Record.Events.Count);
    }

    [Fact]
    public void PartialRemainderIsChargedToTheSamePlayerAfterSettlingTheirSecondSubmission()
    {
        var session = Session(Fixture());
        session.Submit(BattleAction.Pass());
        session.ConfirmHandover();

        AssertTimeout(Assert.Single(session.Tick(15)), 1, BattleAction.Skill(0));

        Assert.False(session.NeedsHandover);
        Assert.Equal(1, session.WhoseTurn);
        Assert.Equal(5, session.TurnTimeLeftSeconds);
        Assert.Single(session.Record.Events);
        AssertTimeout(Assert.Single(session.Tick(5)), 1, BattleAction.Skill(0));
        Assert.True(session.NeedsHandover);
        Assert.Single(session.Record.Events);
    }

    [Fact]
    public void FirstPlayersOvershootIsDiscardedAndConfirmationStartsTheOtherPlayersFullBudget()
    {
        var session = Session(Fixture());

        AssertTimeout(Assert.Single(session.Tick(double.MaxValue)), 0, BattleAction.Skill(0));

        Assert.True(session.NeedsHandover);
        Assert.Empty(session.Record.Events);
        Assert.Empty(session.Tick(double.MaxValue));
        session.ConfirmHandover();
        Assert.Equal(10, session.TurnTimeLeftSeconds);
        Assert.Empty(session.Tick(9.5));
        AssertTimeout(Assert.Single(session.Tick(0.5)), 1, BattleAction.Skill(0));
        Assert.Single(session.Record.Events);
    }

    [Theory]
    [InlineData("ban")]
    [InlineData("lead")]
    public void PreBattleSelectionsDoNotSpendTheChooseBudget(string phase)
    {
        var options = Options();
        options.BanOne = phase == "ban";
        options.LeadPick = phase == "lead";
        var session = Session(Fixture(), options: options);
        string before = State(session);

        Assert.Empty(session.Tick(double.MaxValue));

        Assert.Equal(before, State(session));
        if (phase == "ban") SubmitPair(session, _ => session.SubmitBan(5));
        else SubmitPair(session, _ => session.SubmitLead(1));
        Assert.Equal(Phase.Choose, session.Phase);
        Assert.Equal(10, session.TurnTimeLeftSeconds);
        Assert.Empty(session.Tick(9));
        Assert.Single(session.Tick(1));
    }

    [Fact]
    public void ForcedReplacementPausesTimeAndTheNewFighterDoesNotInheritThePreviousSkill()
    {
        var db = Fixture();
        SetKo(db.Skills["t111_s3"]);
        var session = Session(db);
        Round(session, BattleAction.Skill(3), BattleAction.Skill(2));
        Assert.Equal(Phase.Replace, session.Phase);
        Assert.Equal(1, session.WhoseTurn);
        string before = State(session);

        Assert.Empty(session.Tick(double.MaxValue));

        Assert.Equal(before, State(session));
        session.SubmitReplace(1);
        Assert.Equal(Phase.Choose, session.Phase);
        Assert.Equal(10, session.TurnTimeLeftSeconds);
        AssertTimeout(Assert.Single(session.Tick(10)), 1, BattleAction.Skill(0));
    }

    [Fact]
    public void OverIsPermanentlyPausedAndCannotProduceMoreActions()
    {
        var session = Session(Fixture());
        session.Tick(4);
        session.Surrender();
        Assert.Equal(Phase.Over, session.Phase);
        Assert.Equal(0, session.TurnTimeLeftSeconds);
        string before = State(session);

        Assert.Empty(session.Tick(double.MaxValue));

        Assert.Equal(before, State(session));
    }

    [Fact]
    public void InvalidManualSubmissionDoesNotRestartTheCountdown()
    {
        var session = Session(Fixture());
        session.Tick(4);
        string before = State(session);

        Assert.Throws<InvalidOperationException>(() => session.Submit(BattleAction.Skill(99)));

        Assert.Equal(before, State(session));
        Assert.Empty(session.Tick(5));
        Assert.Single(session.Tick(1));
    }

    [Fact]
    public void PreviousRoundsUsableSkillIsPreferredOverTheFirstSlot()
    {
        var session = Session(Fixture());
        Round(session, BattleAction.Skill(2), BattleAction.Pass());
        session.Submit(BattleAction.Pass());
        session.ConfirmHandover();
        Assert.Equal(0, session.WhoseTurn);

        AssertTimeout(Assert.Single(session.Tick(10)), 0, BattleAction.Skill(2));

        AssertAction(BattleAction.Skill(2), session.Record.Events[1].A0);
    }

    [Fact]
    public void ExhaustedPreviousSkillFallsBackToTheFirstCurrentlyUsableSlot()
    {
        var db = Fixture();
        db.Skills["t111_s0"].Pp = 0;
        db.Skills["t111_s2"].Pp = 1;
        var session = Session(db);
        Round(session, BattleAction.Skill(2), BattleAction.Pass());
        session.Submit(BattleAction.Pass());
        session.ConfirmHandover();
        var options = session.ViewFor(0).Skills;
        Assert.False(options[0].Usable);
        Assert.False(options[2].Usable);
        Assert.True(options[1].Usable);

        AssertTimeout(Assert.Single(session.Tick(10)), 0, BattleAction.Skill(1));

        AssertAction(BattleAction.Skill(1), session.Record.Events[1].A0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PassingOrSwitchingLastTurnClearsAnOlderSkillPreference(bool switched)
    {
        var session = Session(Fixture());
        Round(session, BattleAction.Skill(2), BattleAction.Pass());
        Round(session, switched ? BattleAction.SwitchTo(1) : BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(0, session.WhoseTurn);

        AssertTimeout(Assert.Single(session.Tick(10)), 0, BattleAction.Skill(0));

        Assert.Equal(2, session.Record.Events.Count);
    }

    [Fact]
    public void FreePpMakesThePreviousZeroPpSkillLegalForTimeoutSelection()
    {
        var db = Fixture();
        SetAllPp(db, 0);
        db.Streamers[0].Persona = "teamspirit";
        var session = Session(db);
        Round(session, BattleAction.Skill(2), BattleAction.Pass());
        session.Submit(BattleAction.Pass());
        session.ConfirmHandover();
        var view = session.ViewFor(0);
        Assert.True(view.FreePp);
        Assert.Equal(0, view.Skills[2].Pp);
        Assert.True(view.Skills[2].Usable);

        AssertTimeout(Assert.Single(session.Tick(10)), 0, BattleAction.Skill(2));

        AssertAction(BattleAction.Skill(2), session.Record.Events[1].A0);
        Assert.Equal(0, session.ViewFor(0).Skills[2].Pp);
    }

    [Fact]
    public void NoPpSubmitsRawPassAndLetsTheEnginePerformStruggle()
    {
        var db = Fixture();
        SetAllPp(db, 0);
        var session = Session(db);
        Assert.True(session.ViewFor(0).Struggle);

        AssertTimeout(Assert.Single(session.Tick(10)), 0, BattleAction.Pass());
        session.ConfirmHandover();
        session.Submit(BattleAction.Pass());

        var turn = Assert.Single(session.Record.Events);
        AssertAction(BattleAction.Pass(), turn.A0);
        AssertAction(BattleAction.Pass(), turn.A1);
        Assert.Contains(session.Log, entry => entry.Text.Contains("硬撑"));
        AssertReplayVisible(db, session);
    }

    [Fact]
    public void ControlledPlayerSubmitsRawPassWithoutSelectingAnOtherwiseAvailableSkill()
    {
        var db = Fixture();
        db.Skills["t111_s3"].Prio = 1;
        db.Skills["t111_s3"].Effects.Add(new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "disconnect" });
        var session = Session(db);
        Round(session, BattleAction.Pass(), BattleAction.Skill(3));
        session.Submit(BattleAction.Pass());
        session.ConfirmHandover();
        Assert.False(string.IsNullOrEmpty(session.ViewFor(0).ControlledReason));

        AssertTimeout(Assert.Single(session.Tick(10)), 0, BattleAction.Pass());

        AssertAction(BattleAction.Pass(), session.Record.Events[1].A0);
        Assert.Contains(session.Log, entry => entry.Side == 0 && entry.Text.Contains("无法行动"));
        AssertReplayVisible(db, session);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TimingOutOnePlayerNeitherLeaksNorReplacesTheOtherPlayersChoice(bool firstTimesOut)
    {
        var db = Fixture();
        var session = Session(db);
        string visible = Visible(session);
        if (firstTimesOut) AssertTimeout(Assert.Single(session.Tick(10)), 0, BattleAction.Skill(0));
        else session.Submit(BattleAction.Skill(3));
        Assert.Equal(visible, Visible(session));
        Assert.True(session.NeedsHandover);
        Assert.Empty(session.Record.Events);
        string pending = State(session);
        Assert.Empty(session.Tick(12345));
        Assert.Equal(pending, State(session));
        session.ConfirmHandover();
        if (firstTimesOut) session.Submit(BattleAction.Skill(2));
        else AssertTimeout(Assert.Single(session.Tick(10)), 1, BattleAction.Skill(0));

        var turn = Assert.Single(session.Record.Events);
        AssertAction(BattleAction.Skill(firstTimesOut ? 0 : 3), turn.A0);
        AssertAction(BattleAction.Skill(firstTimesOut ? 2 : 0), turn.A1);
        AssertReplayVisible(db, session);
    }

    [Fact]
    public void FullTimedMatchMatchesNormalSubmissionsAndItsCompleteReplayIncludingRandomState()
    {
        var db = Fixture();
        var timed = Session(db);
        var normal = Session(db, 0);
        int expired = 0;
        while (timed.Phase != Phase.Over && expired < 300)
        {
            if (timed.NeedsHandover) timed.ConfirmHandover();
            if (normal.NeedsHandover) normal.ConfirmHandover();
            int side = timed.WhoseTurn;
            var timeout = Assert.Single(timed.Tick(10));
            AssertTimeout(timeout, side, BattleAction.Skill(0));
            Assert.Equal(side, normal.WhoseTurn);
            normal.Submit(BattleAction.Skill(0));
            expired++;
            Assert.Equal(normal.Record.ToJson(), timed.Record.ToJson());
        }
        Assert.Equal(240, expired);
        Assert.Equal(Phase.Over, timed.Phase);
        Assert.Equal(120, timed.Record.Events.Count);
        var actual = timed.CloneCompletedBattle();
        var expected = normal.CloneCompletedBattle();
        var replay = Replay.Run(db, timed.Record);
        foreach (var battle in new[] { expected, replay })
        {
            Assert.Equal(actual.StateHash(), battle.StateHash());
            Assert.Equal(actual.Record.ToJson(), battle.Record.ToJson());
            Assert.Equal(Json(actual.Log), Json(battle.Log));
            Assert.Equal(Json(actual.Events), Json(battle.Events));
            Assert.Equal(actual.Clone().Random(), battle.Clone().Random());
        }
    }

    static HotseatSession Session(ContentDb db, int limit = 10, BattleOptions options = null)
    {
        var teams = Teams(db);
        return new HotseatSession(db, teams[0], teams[1], "甲", "乙", options ?? Options(), turnTimeLimitSeconds: limit);
    }

    static BattleOptions Options() => new BattleOptions { Seed = 111 };
    static IList<string>[] Teams(ContentDb db) => new IList<string>[]
    {
        db.Streamers.Take(6).Select(streamer => streamer.Id).ToArray(),
        db.Streamers.Skip(6).Take(6).Select(streamer => streamer.Id).ToArray()
    };

    static ContentDb Fixture()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["t111_neutral"] = new PersonaDef { Id = "t111_neutral", Name = "测试人设", Code = "T_LOWHP_POWER" };
        for (int i = 0; i < 5; i++)
        {
            string id = "t111_s" + i;
            db.Skills[id] = new SkillDef { Id = id, Name = "测试准备" + i, Type = "talk", Cat = "status", Pp = 500 };
        }
        foreach (var streamer in db.Streamers)
        {
            streamer.Persona = "t111_neutral";
            streamer.Type = "talk";
            streamer.Type2 = null;
            streamer.Stats["hp"] = 1000;
            foreach (string stat in ContentDb.StatKeys) streamer.Stats[stat] = 100;
            streamer.Skills = Enumerable.Range(0, 4).Select(i => "t111_s" + i).ToList();
            streamer.Fifth = "t111_s4";
        }
        db.Validate();
        return db;
    }

    static void SetAllPp(ContentDb db, int pp)
    {
        for (int i = 0; i < 5; i++) db.Skills["t111_s" + i].Pp = pp;
    }

    static void SetKo(SkillDef skill)
    {
        skill.Prio = 1;
        skill.Effects.Add(new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 100000 });
    }

    static void Round(HotseatSession session, BattleAction a0, BattleAction a1)
    {
        SubmitPair(session, side => session.Submit(side == 0 ? a0 : a1));
    }

    static void SubmitPair(HotseatSession session, Action<int> submit)
    {
        if (session.NeedsHandover) session.ConfirmHandover();
        int side = session.WhoseTurn;
        submit(side);
        Assert.True(session.NeedsHandover);
        session.ConfirmHandover();
        submit(1 - side);
    }

    static void AssertTimeout(HotseatTimeout timeout, int side, BattleAction action)
    {
        Assert.Equal(side, timeout.Side);
        AssertAction(action, timeout.Action);
        Assert.Contains("超时，已自动选择", timeout.Text);
    }

    static void AssertAction(BattleAction expected, BattleAction actual)
    {
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Index, actual.Index);
    }

    static void AssertReplayVisible(ContentDb db, HotseatSession session)
    {
        var replay = Replay.Run(db, session.Record);
        Assert.Equal(Json(replay.Log), Json(session.Log));
        Assert.Equal(session.Record.ToJson(), replay.Record.ToJson());
        Assert.Equal(replay.Phase, session.Phase);
        for (int side = 0; side < 2; side++) Assert.Equal(Json(BattleViewModel.From(replay, side)), Json(session.ViewFor(side)));
    }

    static string Visible(HotseatSession session) => Json(new
    {
        session.Phase, session.Winner, Record = session.Record.ToJson(), session.Log, A = session.ViewFor(0), B = session.ViewFor(1)
    });
    static string State(HotseatSession session) => Json(new
    {
        session.WhoseTurn, session.NeedsHandover, session.TurnTimeLeftSeconds, Visible = Visible(session)
    });
    static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
