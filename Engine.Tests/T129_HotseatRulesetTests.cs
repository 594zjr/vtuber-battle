using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T129_HotseatRulesetTests
{
    static readonly string[] A = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] B = { "yuan", "nyako", "buck", "kasumi", "bee", "enka" };
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    static Ruleset Rule(int bestOf = 3, int turn = 0, int bank = 0)
    {
        return new Ruleset
        {
            Id = "hotseat-series", Name = "同屏赛制测试", BestOf = bestOf,
            TurnSeconds = turn, BankSeconds = bank, Modifiers = new List<string>()
        };
    }

    static HotseatSession Session(ContentDb db, Ruleset rule, uint seed = 129)
    {
        return new HotseatSession(db, A, B, "甲方", "乙方", rule, seed);
    }

    static Battle Direct(ContentDb db, BattleOptions options)
    {
        return new Battle(db, new IList<string>[] { A, B }, new[] { "甲方", "乙方" }, options);
    }

    static ContentDb Fixture()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["t129_neutral"] = new PersonaDef
        {
            Id = "t129_neutral", Name = "测试中性人设", Code = "T_LOWHP_POWER"
        };
        db.Skills["t129_hit"] = new SkillDef
        {
            Id = "t129_hit", Name = "测试互动", Type = "talk", Cat = "interact", Power = 30, Pp = 20, Acc = 73
        };
        db.Skills["t129_ko"] = new SkillDef
        {
            Id = "t129_ko", Name = "测试下播", Type = "talk", Cat = "status", Pp = 20, Acc = 100, Prio = 1,
            Effects = new List<EffectDef> { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 99999 } }
        };
        db.Skills["t129_both"] = new SkillDef
        {
            Id = "t129_both", Name = "测试双方补位", Type = "talk", Cat = "status", Pp = 20, Acc = 100, Prio = 1,
            Effects = new List<EffectDef>
            {
                new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 99999 },
                new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "self", N = 99999 }
            }
        };
        db.Skills["t129_wait"] = new SkillDef
        {
            Id = "t129_wait", Name = "测试等待", Type = "talk", Cat = "status", Pp = 20, Acc = 100
        };
        foreach (var streamer in db.Streamers)
        {
            streamer.Persona = "t129_neutral";
            streamer.Skills = new List<string> { "t129_hit", "t129_ko", "t129_both", "t129_wait" };
            streamer.Fifth = "t129_hit";
            foreach (string stat in ContentDb.StatKeys) streamer.Stats[stat] = 100;
            streamer.Stats["hp"] = 200;
        }
        return db;
    }

    static string Visible(HotseatSession session)
    {
        return Json(new
        {
            session.Phase, session.Winner, Record = session.Record.ToJson(), session.Log,
            session.DisplayLog, View0 = session.ViewFor(0), View1 = session.ViewFor(1)
        });
    }

    static string State(HotseatSession session)
    {
        return Json(new
        {
            Visible = Visible(session), session.WhoseTurn, session.NeedsHandover,
            session.GameNumber, session.MatchOver, session.Wins, session.Draws,
            Clock = session.Clock == null ? null : new[]
            {
                session.Clock.TurnLeft(0), session.Clock.TurnLeft(1),
                session.Clock.BankLeft(0), session.Clock.BankLeft(1)
            }
        });
    }

    static void ConfirmIfNeeded(HotseatSession session)
    {
        if (session.NeedsHandover) session.ConfirmHandover();
    }

    static void Pair(HotseatSession session, Action<int> submit, bool verifyPrivacy = false)
    {
        ConfirmIfNeeded(session);
        int first = session.WhoseTurn;
        string before = verifyPrivacy ? Visible(session) : null;
        submit(first);
        Assert.True(session.NeedsHandover);
        Assert.Equal(1 - first, session.WhoseTurn);
        if (verifyPrivacy) Assert.Equal(before, Visible(session));
        session.ConfirmHandover();
        if (verifyPrivacy) Assert.Equal(before, Visible(session));
        submit(1 - first);
    }

    static void FinishWithWinner(HotseatSession session, int winner)
    {
        ConfirmIfNeeded(session);
        Assert.Equal(Phase.Choose, session.Phase);
        if (session.WhoseTurn == winner)
        {
            session.Submit(BattleAction.Pass());
            session.ConfirmHandover();
        }
        session.Surrender();
        Assert.Equal(Phase.Over, session.Phase);
        Assert.Equal(winner, session.Winner);
    }

    static void AssertComplete(ContentDb db, HotseatSession session, Battle direct = null)
    {
        var completed = session.CloneCompletedBattle();
        Assert.Equal(Phase.Over, completed.Phase);
        string record = session.Record.ToJson();
        var replay = Replay.Run(db, BattleRecord.FromJson(record));
        Assert.Equal(completed.StateHash(), replay.StateHash());
        Assert.Equal(Json(completed.Log), Json(replay.Log));
        Assert.Equal(completed.Clone().Random(), replay.Clone().Random());
        if (direct != null)
        {
            Assert.Equal(direct.StateHash(), completed.StateHash());
            Assert.Equal(direct.Record.ToJson(), record);
            Assert.Equal(Json(direct.Log), Json(completed.Log));
            Assert.Equal(direct.Clone().Random(), completed.Clone().Random());
        }
        Assert.Equal(record, session.Record.ToJson());
    }

    [Fact]
    public void BestOfThreeAutomaticallyReportsScoresAndFinishesAfterTheDecidingGame()
    {
        var db = BattleTests.LoadPack("original");
        var session = Session(db, Rule());
        Assert.True(session.UsesRuleset);
        Assert.False(session.MatchOver);
        Assert.Equal(1, session.GameNumber);
        Assert.Equal(new[] { 0, 0 }, session.Wins);
        var saved = new List<BattleRecord>();
        int[] winners = { 0, 1, 0 };
        for (int game = 0; game < winners.Length; game++)
        {
            Assert.Equal((uint)(129 + game), session.Record.Seed);
            Assert.Equal(game + 1, session.GameNumber);
            FinishWithWinner(session, winners[game]);
            Assert.Equal(winners.Take(game + 1).Count(winner => winner == 0), session.Wins[0]);
            Assert.Equal(winners.Take(game + 1).Count(winner => winner == 1), session.Wins[1]);
            Assert.Equal(0, session.Draws);
            Assert.Equal(game == 2, session.MatchOver);
            saved.Add(session.Record);
            AssertComplete(db, session);
            if (game < 2)
            {
                session.NextGame();
                Assert.True(session.NeedsHandover);
                Assert.Equal(0, session.WhoseTurn);
                Assert.Equal(Phase.Choose, session.Phase);
                Assert.Empty(session.Record.Events);
            }
        }
        Assert.Equal(new[] { 2, 1 }, session.Wins);
        Assert.Equal(3, saved.Select(record => record.Seed).Distinct().Count());
        Assert.All(saved, record => Assert.Equal(Phase.Over, Replay.Run(db, record).Phase));
        string before = State(session);
        Assert.Throws<InvalidOperationException>(() => session.NextGame());
        Assert.Equal(before, State(session));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TwoConsecutiveWinsEndBestOfThreeWithoutStartingAThirdGame(int winner)
    {
        var db = BattleTests.LoadPack("original");
        var session = Session(db, Rule());
        FinishWithWinner(session, winner);
        Assert.False(session.MatchOver);
        session.NextGame();
        FinishWithWinner(session, winner);
        Assert.True(session.MatchOver);
        Assert.Equal(2, session.GameNumber);
        Assert.Equal(2, session.Wins[winner]);
        Assert.Equal(0, session.Wins[1 - winner]);
        Assert.Throws<InvalidOperationException>(() => session.NextGame());
        AssertComplete(db, session);
    }

    [Fact]
    public void RealDrawsUseAllThreeGameSlotsAndSeedsWrapAtUintBoundary()
    {
        var db = Fixture();
        uint seed = uint.MaxValue - 1;
        var session = Session(db, Rule(), seed);
        for (int game = 0; game < 3; game++)
        {
            Assert.Equal(unchecked(seed + (uint)game), session.Record.Seed);
            // 不改私有 Battle，双方等血且全程等待，通过真实回合上限得到平局。
            for (int turn = 0; turn < Battle.TurnCap && session.Phase != Phase.Over; turn++)
                Pair(session, side => session.Submit(BattleAction.Pass()));
            Assert.Equal(Phase.Over, session.Phase);
            Assert.Equal(-1, session.Winner);
            Assert.Equal(new[] { 0, 0 }, session.Wins);
            Assert.Equal(game + 1, session.Draws);
            Assert.Equal(game == 2, session.MatchOver);
            AssertComplete(db, session);
            if (game < 2) session.NextGame();
        }
        Assert.Throws<InvalidOperationException>(() => session.NextGame());
    }

    [Fact]
    public void EveryGamesBanAndLeadChoicesRemainPrivateUntilBothPlayersSubmit()
    {
        var db = Fixture();
        var rule = Rule();
        rule.BanOne = rule.LeadPick = true;
        rule.Modifiers.AddRange(new[] { "hype_start", "half_pp" });
        var session = Session(db, rule);
        for (int game = 0; game < 2; game++)
        {
            Assert.Equal(Phase.Ban, session.Phase);
            Assert.Empty(session.Record.Events);
            Assert.Equal(A, session.ViewFor(0).Sides[0].Team.Select(fighter => fighter.Id));
            Assert.Equal(B, session.ViewFor(1).Sides[1].Team.Select(fighter => fighter.Id));
            Pair(session, side => session.SubmitBan(side == 0 ? 4 : 0), true);
            Assert.Equal(Phase.Lead, session.Phase);
            Assert.Equal(2, session.Record.Events.Count);
            Assert.All(session.Record.Events, entry => Assert.Equal(BattleRecordEventType.Ban, entry.Type));
            Assert.All(session.ViewFor(0).Sides, side => Assert.Equal(5, side.Team.Count));
            Pair(session, side => session.SubmitLead(side == 0 ? 2 : 1), true);
            Assert.Equal(Phase.Choose, session.Phase);
            Assert.Equal(4, session.Record.Events.Count);
            Assert.Equal(new[] { 0, 1, 0, 1 }, session.Record.Events.Select(entry => entry.Side));
            Assert.Equal("lumi", session.ViewFor(0).Sides[0].Team[0].Id);
            Assert.Equal("nyako", session.ViewFor(0).Sides[1].Team[0].Id);
            Assert.Equal(new[] { "half_pp", "hype_start" }, session.Record.Modifiers);
            Assert.All(session.ViewFor(0).Skills, skill => Assert.Equal(10, skill.MaxPp));
            FinishWithWinner(session, 0);
            AssertComplete(db, session);
            if (game == 0) session.NextGame();
        }
        Assert.True(session.MatchOver);
    }

    [Theory]
    [InlineData(Phase.Ban)]
    [InlineData(Phase.Lead)]
    [InlineData(Phase.Choose)]
    public void NextGameBeforeCurrentGameEndsCannotDiscardAHiddenChoice(Phase phase)
    {
        var db = Fixture();
        var rule = Rule(3, 3, 5);
        rule.BanOne = phase == Phase.Ban;
        rule.LeadPick = phase == Phase.Lead;
        var session = Session(db, rule);
        string before = State(session);
        Assert.Throws<InvalidOperationException>(() => session.NextGame());
        Assert.Equal(before, State(session));
        Action<int> submit = side =>
        {
            if (phase == Phase.Ban) session.SubmitBan(side + 1);
            else if (phase == Phase.Lead) session.SubmitLead(side + 1);
            else session.Submit(BattleAction.Skill(side));
        };
        submit(0);
        before = State(session);
        Assert.Throws<InvalidOperationException>(() => session.NextGame());
        Assert.Equal(before, State(session));
        session.ConfirmHandover();
        submit(1);
        Assert.NotEmpty(session.Record.Events);
    }

    [Fact]
    public void FailedNextGameRetainsCompletedRecordScoreClockAndSeedUntilRetry()
    {
        var db = BattleTests.LoadPack("original");
        var session = Session(db, Rule(3, 3, 5), uint.MaxValue);
        FinishWithWinner(session, 0);
        string before = State(session);
        var clock = session.Clock;
        string oldName = db.Streamer("bell").Name;
        db.Streamer("bell").Name = "变化后的主播名称";
        Assert.Throws<KeyError>(() => session.NextGame());
        db.Streamer("bell").Name = oldName;
        Assert.Equal(before, State(session));
        Assert.Same(clock, session.Clock);
        session.NextGame();
        Assert.Equal(2, session.GameNumber);
        Assert.Equal((uint)0, session.Record.Seed);
        Assert.Equal(new[] { 1, 0 }, session.Wins);
    }

    [Fact]
    public void RulesTeamsAndScoreSnapshotsCannotRewriteFutureGames()
    {
        var db = Fixture();
        var rule = Rule();
        rule.Modifiers.Add("half_pp");
        var teamA = A.ToList();
        var teamB = B.ToList();
        var session = new HotseatSession(db, teamA, teamB, "甲方", "乙方", rule, 129);
        string expectedRules = rule.ToJson();
        rule.BestOf = 1;
        rule.Modifiers.Clear();
        teamA.Clear();
        teamB.Reverse();
        var exposed = session.Ruleset;
        exposed.BestOf = 5;
        exposed.Modifiers.Add("no_switch");
        Assert.Equal(expectedRules, session.Ruleset.ToJson());
        FinishWithWinner(session, 0);
        var wins = session.Wins;
        wins[0] = 20;
        Assert.Equal(new[] { 1, 0 }, session.Wins);
        Assert.False(session.MatchOver);
        session.NextGame();
        Assert.Equal(A, session.Record.Teams[0]);
        Assert.Equal(B, session.Record.Teams[1]);
        Assert.Equal(new[] { "half_pp" }, session.Record.Modifiers);
        Assert.Equal(expectedRules, session.Ruleset.ToJson());
        Assert.All(session.ViewFor(0).Skills, skill => Assert.Equal(10, skill.Pp));
    }

    [Fact]
    public void RulesetConstructorValidatesItsSeasonAndBothCompleteTeams()
    {
        var db = BattleTests.LoadPack("original");
        var rule = Rule();
        rule.Season = "s1";
        var banned = new[] { "kei", "enka", "shiori", "lumi", "hane", "momo" };
        var error = Assert.Throws<KeyError>(() => new HotseatSession(db, A, banned, "甲方", "乙方", rule, 129));
        Assert.All(rule.CheckTeam(db, banned), reason => Assert.Contains(reason, error.Message));
        Assert.Throws<KeyError>(() => new HotseatSession(db, A.Take(5).ToArray(), B, "甲方", "乙方", rule, 129));
        Assert.Throws<KeyError>(() => new HotseatSession(db, A, Enumerable.Repeat("bell", 6).ToArray(), "甲方", "乙方", rule, 129));
        Assert.Equal(Phase.Choose, Session(db, rule).Phase);
        rule.Season = "missing-season";
        Assert.Throws<KeyError>(() => Session(db, rule));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void LegacyConstructorStillMatchesDirectBattleRecordsLogsHashAndRandom(bool ban, bool lead)
    {
        var db = Fixture();
        var options = new BattleOptions { Seed = 129, BanOne = ban, LeadPick = lead };
        var session = new HotseatSession(db, A, B, "甲方", "乙方", options);
        var direct = Direct(db, options);
        Assert.False(session.UsesRuleset);
        Assert.Null(session.Clock);
        Assert.False(session.NeedsHandover);
        Assert.Equal(0, session.WhoseTurn);
        if (ban)
        {
            Pair(session, side => session.SubmitBan(side == 0 ? 4 : 0));
            direct.ChooseBan(0, 4);
            direct.ChooseBan(1, 0);
        }
        if (lead)
        {
            Pair(session, side => session.SubmitLead(side + 1));
            direct.ChooseLead(0, 1);
            direct.ChooseLead(1, 2);
        }
        Pair(session, side => session.Submit(BattleAction.Skill(0)));
        direct.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        for (int step = 0; step < 20 && direct.Phase != Phase.Over; step++)
        {
            Assert.Equal(direct.Record.ToJson(), session.Record.ToJson());
            Assert.Equal(Json(direct.Log), Json(session.Log));
            string before = State(session);
            Assert.False(session.TickRuleset(999));
            Assert.Equal(before, State(session));
            if (direct.Phase == Phase.Replace)
            {
                int side = session.WhoseTurn;
                int index = session.ViewFor(side).SwitchTargets.Min();
                session.SubmitReplace(index);
                direct.Replace(side, index);
            }
            else
            {
                Pair(session, side => session.Submit(side == 0 ? BattleAction.Skill(1) : BattleAction.Pass()));
                direct.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
            }
        }
        AssertComplete(db, session, direct);
        Assert.Throws<InvalidOperationException>(() => session.NextGame());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void LegacyConstructorKeepsSupportingSmallTeamsOutsideMatchRules(int size, bool ban)
    {
        var db = Fixture();
        var teams = new IList<string>[] { A.Take(size).ToList(), B.Take(size).ToList() };
        var options = new BattleOptions { Seed = 129, BanOne = ban, LeadPick = true };
        var session = new HotseatSession(db, teams[0], teams[1], "甲方", "乙方", options);
        var direct = new Battle(db, teams, new[] { "甲方", "乙方" }, options);
        if (ban)
        {
            Pair(session, side => session.SubmitBan(1));
            direct.ChooseBan(0, 1);
            direct.ChooseBan(1, 1);
        }
        Pair(session, side => session.SubmitLead(0));
        direct.ChooseLead(0, 0);
        direct.ChooseLead(1, 0);
        FinishWithWinner(session, 0);
        direct.Surrender(1);
        Assert.False(session.UsesRuleset);
        Assert.Null(session.Ruleset);
        AssertComplete(db, session, direct);
    }

    [Fact]
    public void BanLeadAndUnconfirmedHandoverPauseTimeWithoutChangingSecretChoices()
    {
        var db = Fixture();
        var rule = Rule(3, 3, 5);
        rule.BanOne = rule.LeadPick = true;
        var session = Session(db, rule);
        Action assertPaused = () =>
        {
            string before = State(session);
            Assert.False(session.TickRuleset(10000));
            Assert.Equal(before, State(session));
        };
        foreach (Phase phase in new[] { Phase.Ban, Phase.Lead })
        {
            Assert.Equal(phase, session.Phase);
            assertPaused();
            Action submit = () => { if (phase == Phase.Ban) session.SubmitBan(5); else session.SubmitLead(0); };
            string visible = Visible(session);
            submit();
            assertPaused();
            Assert.Equal(visible, Visible(session));
            session.ConfirmHandover();
            assertPaused();
            Assert.Equal(visible, Visible(session));
            submit();
        }
        Assert.Equal(Phase.Choose, session.Phase);
        Assert.Equal(0, session.WhoseTurn);
        Assert.False(session.TickRuleset(2));
        Assert.Equal(1.0, session.Clock.TurnLeft(0));
        Assert.Equal(3.0, session.Clock.TurnLeft(1));
        session.Submit(BattleAction.Pass());
        assertPaused();
        session.ConfirmHandover();
        Assert.False(session.TickRuleset(1));
        Assert.Equal(1.0, session.Clock.TurnLeft(0));
        Assert.Equal(2.0, session.Clock.TurnLeft(1));
    }

    [Fact]
    public void TimerChargesOnlyCurrentPlayerKeepsBanksAndResetsBasicTimePerStage()
    {
        var db = Fixture();
        var session = Session(db, Rule(3, 3, 5));
        var direct = Direct(db, new BattleOptions { Seed = 129 });
        Assert.False(session.TickRuleset(4));
        Assert.Equal(0.0, session.Clock.TurnLeft(0));
        Assert.Equal(4.0, session.Clock.BankLeft(0));
        Assert.Equal(3.0, session.Clock.TurnLeft(1));
        Assert.Equal(5.0, session.Clock.BankLeft(1));
        session.Submit(BattleAction.Pass());
        Assert.False(session.TickRuleset(100));
        session.ConfirmHandover();
        Assert.False(session.TickRuleset(5));
        Assert.Equal(3.0, session.Clock.BankLeft(1));
        session.Submit(BattleAction.Pass());
        direct.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(1, session.WhoseTurn);
        Assert.Equal(3.0, session.Clock.TurnLeft(0));
        Assert.Equal(3.0, session.Clock.TurnLeft(1));
        Assert.Equal(4.0, session.Clock.BankLeft(0));
        Assert.Equal(3.0, session.Clock.BankLeft(1));
        string record = session.Record.ToJson();
        Assert.True(session.TickRuleset(6));
        Assert.Equal(record, session.Record.ToJson());
        Assert.True(session.NeedsHandover);
        Assert.False(session.TickRuleset(100));
        session.ConfirmHandover();
        Assert.True(session.TickRuleset(7));
        direct.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(direct.Record.ToJson(), session.Record.ToJson());
        Assert.Equal(0.0, session.Clock.BankLeft(0));
        Assert.Equal(0.0, session.Clock.BankLeft(1));
        Assert.Equal(3.0, session.Clock.TurnLeft(0));
        Assert.Equal(3.0, session.Clock.TurnLeft(1));
        int loser = session.WhoseTurn;
        session.Surrender();
        direct.Surrender(loser);
        AssertComplete(db, session, direct);
        string finished = State(session);
        Assert.False(session.TickRuleset(100));
        Assert.Equal(finished, State(session));
    }

    [Fact]
    public void ReplacementTimeoutUsesLowestLivingSlotEvenWhenSwitchingIsForbidden()
    {
        var db = Fixture();
        var rule = Rule(3, 3, 5);
        rule.Modifiers.Add("no_switch");
        var session = Session(db, rule);
        var direct = Direct(db, new BattleOptions { Seed = 129, Modifiers = new List<string> { "no_switch" } });
        Assert.False(session.TickRuleset(1));
        session.Submit(BattleAction.Skill(1));
        session.ConfirmHandover();
        Assert.False(session.TickRuleset(4));
        session.Submit(BattleAction.Pass());
        direct.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        Assert.Equal(Phase.Replace, session.Phase);
        Assert.Equal(1, session.WhoseTurn);
        Assert.False(session.NeedsHandover);
        Assert.Equal(3.0, session.Clock.TurnLeft(1));
        Assert.Equal(4.0, session.Clock.BankLeft(1));
        Assert.Equal(5.0, session.Clock.BankLeft(0));
        string before = State(session);
        Assert.Throws<InvalidOperationException>(() => session.NextGame());
        Assert.Equal(before, State(session));
        Assert.True(session.TickRuleset(7));
        direct.Replace(1, 1);
        Assert.Equal(Phase.Choose, session.Phase);
        Assert.Equal(1, session.ViewFor(1).Sides[1].Active);
        Assert.Equal(3.0, session.Clock.TurnLeft(1));
        Assert.Equal(0.0, session.Clock.BankLeft(1));
        Assert.Equal(direct.Record.ToJson(), session.Record.ToJson());
        session.Surrender();
        direct.Surrender(1);
        AssertComplete(db, session, direct);
    }

    [Fact]
    public void BothReplacementTimeoutsStayPrivateAndResolveInSideOrder()
    {
        var db = Fixture();
        var session = Session(db, Rule(3, 2, 0));
        var direct = Direct(db, new BattleOptions { Seed = 129 });
        Pair(session, side => session.Submit(side == 0 ? BattleAction.Skill(2) : BattleAction.Pass()));
        direct.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        Assert.Equal(Phase.Replace, session.Phase);
        Assert.Equal(1, session.WhoseTurn);
        string before = Visible(session);
        Assert.True(session.TickRuleset(200));
        Assert.Equal(before, Visible(session));
        Assert.True(session.NeedsHandover);
        Assert.Equal(2.0, session.Clock.TurnLeft(0));
        Assert.False(session.TickRuleset(100));
        session.ConfirmHandover();
        Assert.Equal(before, Visible(session));
        Assert.True(session.TickRuleset(200));
        direct.Replace(0, 1);
        direct.Replace(1, 1);
        Assert.Equal(2.0, session.Clock.TurnLeft(0));
        Assert.Equal(2.0, session.Clock.TurnLeft(1));
        Assert.Equal(direct.Record.ToJson(), session.Record.ToJson());
        Assert.Equal(new[] { 0, 1 }, session.Record.Events.Where(entry => entry.Type == BattleRecordEventType.Replace).Select(entry => entry.Side));
        int loser = session.WhoseTurn;
        session.Surrender();
        direct.Surrender(loser);
        AssertComplete(db, session, direct);
    }

    [Fact]
    public void NewGameHasFreshClockAndDoesNotChargeTheCrossGameHandover()
    {
        var db = Fixture();
        var session = Session(db, Rule(3, 2, 3));
        var firstClock = session.Clock;
        Assert.True(session.TickRuleset(5));
        session.ConfirmHandover();
        session.Surrender();
        Assert.Equal(0.0, firstClock.BankLeft(0));
        session.NextGame();
        Assert.NotSame(firstClock, session.Clock);
        Assert.True(session.NeedsHandover);
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(2.0, session.Clock.TurnLeft(side));
            Assert.Equal(3.0, session.Clock.BankLeft(side));
            Assert.False(session.Clock.TimedOut(side));
        }
        string before = State(session);
        Assert.False(session.TickRuleset(100));
        Assert.Equal(before, State(session));
        session.ConfirmHandover();
        Assert.False(session.TickRuleset(1));
        Assert.Equal(1.0, session.Clock.TurnLeft(0));
        Assert.Equal(2.0, session.Clock.TurnLeft(1));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    public void ZeroBudgetsMeanDisabledClockOrImmediateUseOfTheOnlyAvailableBudget(int turn, int bank)
    {
        var db = Fixture();
        var session = Session(db, Rule(3, turn, bank));
        if (turn == 0 && bank == 0)
        {
            Assert.Null(session.Clock);
            string before = State(session);
            Assert.False(session.TickRuleset(100));
            Assert.Equal(before, State(session));
        }
        else
        {
            Assert.NotNull(session.Clock);
            Assert.True(session.TickRuleset(5));
            Assert.True(session.NeedsHandover);
            Assert.Equal(1, session.WhoseTurn);
            Assert.Equal((double)turn, session.Clock.TurnLeft(1));
            Assert.Equal((double)bank, session.Clock.BankLeft(1));
        }
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidElapsedTimeIsRejectedWithoutLosingThePendingAction(double seconds)
    {
        var db = Fixture();
        var session = Session(db, Rule(3, 3, 5));
        session.Submit(BattleAction.Skill(0));
        string before = State(session);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.TickRuleset(seconds));
        Assert.Equal(before, State(session));
        session.ConfirmHandover();
        session.Submit(BattleAction.Pass());
        Assert.Single(session.Record.Events);
        Assert.Equal(ActionType.Skill, session.Record.Events[0].A0.Type);
    }
}
