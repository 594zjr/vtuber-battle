using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T86_HotseatTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    static IList<string>[] Teams()
    {
        return new IList<string>[]
        {
            new List<string> { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new List<string> { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        };
    }

    static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    static HotseatSession Session(ContentDb db, BattleOptions options, IList<string>[] teams = null)
    {
        teams = teams ?? Teams();
        return new HotseatSession(db, teams[0], teams[1], "甲方", "乙方", options);
    }

    static Battle Direct(ContentDb db, BattleOptions options)
    {
        return new Battle(db, Teams(), new[] { "甲方", "乙方" }, options);
    }

    static ContentDb Fixture()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["hotseat_neutral"] = new PersonaDef
        {
            Id = "hotseat_neutral", Name = "测试人设", Code = "T_LOWHP_POWER"
        };
        db.Skills["hotseat_hit"] = new SkillDef
        {
            Id = "hotseat_hit", Name = "测试互动", Type = "talk", Cat = "interact", Power = 30, Pp = 20, Acc = 73
        };
        db.Skills["hotseat_ko"] = new SkillDef
        {
            Id = "hotseat_ko", Name = "测试下播", Type = "talk", Cat = "status", Pp = 20, Prio = 1,
            Effects = new List<EffectDef>
            {
                new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 99999 }
            }
        };
        db.Skills["hotseat_both"] = new SkillDef
        {
            Id = "hotseat_both", Name = "测试双下播", Type = "talk", Cat = "status", Pp = 20, Prio = 1,
            Effects = new List<EffectDef>
            {
                new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 99999 },
                new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "self", N = 99999 }
            }
        };
        db.Skills["hotseat_setup"] = new SkillDef
        {
            Id = "hotseat_setup", Name = "测试准备", Type = "talk", Cat = "status", Pp = 20,
            Effects = new List<EffectDef>
            {
                new EffectDef { At = "post", Code = "STAGE", Target = "self", Changes = new Dictionary<string, int> { ["atk"] = 1 } }
            }
        };
        foreach (var definition in db.Streamers)
        {
            definition.Persona = "hotseat_neutral";
            definition.Skills = new List<string> { "hotseat_hit", "hotseat_ko", "hotseat_both", "hotseat_setup" };
            definition.Fifth = "hotseat_hit";
            definition.Stats["hp"] = 200;
            foreach (string stat in ContentDb.StatKeys) definition.Stats[stat] = 100;
        }
        return db;
    }

    static string Visible(HotseatSession session)
    {
        return Json(new
        {
            session.Phase, session.Winner,
            Record = session.Record.ToJson(), Log = session.Log,
            View0 = session.ViewFor(0), View1 = session.ViewFor(1)
        });
    }

    static string State(HotseatSession session)
    {
        return Json(new { session.WhoseTurn, session.NeedsHandover, Visible = Visible(session) });
    }

    static void RejectWithoutChange(HotseatSession session, Action action)
    {
        string before = State(session);
        var error = Record.Exception(action);
        Assert.True(error is ArgumentException || error is InvalidOperationException,
            "非法操作应明确拒绝，实际结果：" + (error == null ? "未抛异常" : error.GetType().Name));
        Assert.Equal(before, State(session));
    }

    static void AssertCurrent(Battle direct, HotseatSession session)
    {
        Assert.Equal(direct.Phase, session.Phase);
        Assert.Equal(direct.Winner, session.Winner);
        Assert.Equal(direct.Record.ToJson(), session.Record.ToJson());
        Assert.Equal(Json(direct.Log), Json(session.Log));
        for (int side = 0; side < 2; side++)
        {
            var view = session.ViewFor(side);
            Assert.Equal(direct.Sides[side].Active, view.Sides[side].Active);
            Assert.Equal(direct.Sides[side].Team.Select(f => f.Def.Id), view.Sides[side].Team.Select(f => f.Id));
            Assert.Equal(direct.Sides[side].Team.Select(f => f.Hp), view.Sides[side].Team.Select(f => f.Hp));
            if (direct.Phase == Phase.Choose) Assert.Equal(Json(BattleViewModel.From(direct, side)), Json(view));
            else Assert.Empty(view.Skills);
        }
    }

    static void AssertComplete(Battle expected, Battle actual)
    {
        Assert.Equal(Phase.Over, expected.Phase);
        Assert.Equal(expected.Phase, actual.Phase);
        Assert.Equal(expected.Winner, actual.Winner);
        Assert.Equal(expected.Turn, actual.Turn);
        Assert.Equal(expected.StateHash(), actual.StateHash());
        Assert.Equal(expected.Record.ToJson(), actual.Record.ToJson());
        Assert.Equal(Json(expected.Log), Json(actual.Log));
        Assert.Equal(Json(expected.Events), Json(actual.Events));
        // 比较副本的下一随机数，避免校验本身改变后续比较的基准。
        Assert.Equal(expected.Clone().Random(), actual.Clone().Random());
    }

    static void Pair(HotseatSession session, Action<int> submit)
    {
        Assert.False(session.NeedsHandover);
        int first = session.WhoseTurn;
        Assert.InRange(first, 0, 1);
        string before = Visible(session);
        submit(first);
        Assert.Equal(before, Visible(session));
        Assert.Equal(1 - first, session.WhoseTurn);
        Assert.True(session.NeedsHandover);
        session.ConfirmHandover();
        Assert.False(session.NeedsHandover);
        submit(1 - first);
        Assert.False(session.NeedsHandover);
        int next = session.Phase == Phase.Over ? -1 : 1 - first;
        if (session.Phase == Phase.Replace)
        {
            bool need0 = session.ViewFor(0).SwitchTargets.Count > 0;
            bool need1 = session.ViewFor(1).SwitchTargets.Count > 0;
            if (need0 != need1) next = need0 ? 0 : 1;
        }
        Assert.Equal(next, session.WhoseTurn);
    }

    static void Turn(HotseatSession session, Battle direct, BattleAction a0, BattleAction a1)
    {
        Pair(session, side => session.Submit(side == 0 ? a0 : a1));
        direct.ResolveTurn(a0, a1);
        AssertCurrent(direct, session);
    }

    static void Replacements(HotseatSession session, Battle direct)
    {
        int[] choices = Enumerable.Range(0, 2).Select(side => direct.Need[side]
            ? direct.Sides[side].Team.FindIndex(f => !f.Fainted) : -1).ToArray();
        if (direct.Need.All(need => need)) Pair(session, side => session.SubmitReplace(choices[side]));
        else
        {
            int side = direct.Need[0] ? 0 : 1;
            Assert.Equal(side, session.WhoseTurn);
            Assert.False(session.NeedsHandover);
            session.SubmitReplace(choices[side]);
        }
        for (int side = 0; side < 2; side++) if (choices[side] >= 0) direct.Replace(side, choices[side]);
        AssertCurrent(direct, session);
    }

    static void FinishFixture(HotseatSession session, Battle direct)
    {
        int guard = 30;
        while (direct.Phase != Phase.Over && guard-- > 0)
        {
            if (direct.Phase == Phase.Replace) Replacements(session, direct);
            else Turn(session, direct, BattleAction.Skill(1), BattleAction.Pass());
        }
        AssertComplete(direct, session.CloneCompletedBattle());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FullMatchMatchesDirectBattleAndReplayIncludingLogsEventsAndRandomStream(bool ban, bool lead)
    {
        var db = BattleTests.LoadPack("original");
        var options = new BattleOptions { Seed = 86, BanOne = ban, LeadPick = lead };
        var session = Session(db, options);
        var direct = Direct(db, options);
        Assert.Equal(0, session.WhoseTurn);
        Assert.False(session.NeedsHandover);
        int guard = 500;
        while (direct.Phase != Phase.Over && guard-- > 0)
        {
            if (direct.Phase == Phase.Ban)
            {
                Pair(session, side => session.SubmitBan(side == 0 ? 4 : 1));
                direct.ChooseBan(0, 4);
                direct.ChooseBan(1, 1);
            }
            else if (direct.Phase == Phase.Lead)
            {
                Pair(session, side => session.SubmitLead(side == 0 ? 3 : 2));
                direct.ChooseLead(0, 3);
                direct.ChooseLead(1, 2);
            }
            else if (direct.Phase == Phase.Replace) Replacements(session, direct);
            else
            {
                // AI 只在独立副本上选招，两种对战方式收到完全相同的行动且不借用会话内部 RNG。
                var a0 = Ai.Choose(direct.Clone(), 0);
                var a1 = Ai.Choose(direct.Clone(), 1);
                Turn(session, direct, a0, a1);
            }
            AssertCurrent(direct, session);
        }
        Assert.Equal(Phase.Over, session.Phase);
        Assert.Equal(-1, session.WhoseTurn);
        Assert.False(session.NeedsHandover);
        AssertComplete(direct, session.CloneCompletedBattle());
        AssertComplete(direct, Replay.Run(db, BattleRecord.FromJson(session.Record.ToJson())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstActionDoesNotLeakThroughEitherViewLogOrRecord(bool switchAction)
    {
        var db = Fixture();
        var options = new BattleOptions { Seed = 86 };
        var session = Session(db, options);
        var direct = Direct(db, options);
        string before = Visible(session);
        var action = switchAction ? BattleAction.SwitchTo(3) : BattleAction.Skill(0);
        session.Submit(action);
        Assert.Equal(before, Visible(session));
        Assert.Empty(session.Record.Events);
        RejectWithoutChange(session, () => session.Submit(BattleAction.Pass()));
        RejectWithoutChange(session, () => session.SubmitBan(1));
        RejectWithoutChange(session, () => session.SubmitLead(1));
        RejectWithoutChange(session, () => session.SubmitReplace(1));
        session.ConfirmHandover();
        Assert.Equal(before, Visible(session));
        RejectWithoutChange(session, () => session.Submit(BattleAction.Skill(99)));
        session.Submit(BattleAction.Pass());
        direct.ResolveTurn(action, BattleAction.Pass());
        AssertCurrent(direct, session);
        FinishFixture(session, direct);
    }

    [Fact]
    public void LaterPlayerKeepsTheComputerAndStartsTheNextPairOfChoices()
    {
        var db = Fixture();
        var options = new BattleOptions { Seed = 86, BanOne = true, LeadPick = true };
        var session = Session(db, options);
        Assert.Equal(0, session.WhoseTurn);
        Pair(session, side => session.SubmitBan(side == 0 ? 4 : 1));
        Assert.Equal(Phase.Lead, session.Phase);
        Assert.Equal(1, session.WhoseTurn);
        Assert.All(session.ViewFor(0).Sides, side => Assert.Equal(5, side.Team.Count));
        Pair(session, side => session.SubmitLead(side == 0 ? 3 : 2));
        Assert.Equal(Phase.Choose, session.Phase);
        Assert.Equal(0, session.WhoseTurn);
        Assert.Equal(new[] { "hane", "bell", "shiori", "lumi", "momo" }, session.ViewFor(0).Sides[0].Team.Select(f => f.Id));
        Assert.Equal(new[] { "nyako", "kei", "yuan", "buck", "bee" }, session.ViewFor(1).Sides[1].Team.Select(f => f.Id));
        Pair(session, side => session.Submit(BattleAction.Pass()));
        Assert.Equal(1, session.WhoseTurn);
        Pair(session, side => session.Submit(BattleAction.Pass()));
        Assert.Equal(0, session.WhoseTurn);
        Assert.Equal(new[] { 0, 1, 0, 1 }, session.Record.Events.Take(4).Select(e => e.Side));
        Assert.Equal(Teams()[0], session.Record.Teams[0]);
        Assert.Equal(Teams()[1], session.Record.Teams[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SingleReplacementNeedsNoHandoverAndRemainsLegalWhenSwitchingIsLocked(int losingSide)
    {
        var db = Fixture();
        var options = new BattleOptions { Seed = 86, Modifiers = new List<string> { "no_switch" } };
        var session = Session(db, options);
        var direct = Direct(db, options);
        var choices = new[] { BattleAction.Pass(), BattleAction.Pass() };
        choices[1 - losingSide] = BattleAction.Skill(1);
        // 单方补位会把持机方直接交给需要补位的一方，因此不使用普通双选后的持机断言。
        session.Submit(choices[0]);
        session.ConfirmHandover();
        session.Submit(choices[1]);
        direct.ResolveTurn(choices[0], choices[1]);
        Assert.Equal(Phase.Replace, session.Phase);
        Assert.Equal(losingSide, session.WhoseTurn);
        Assert.False(session.NeedsHandover);
        Assert.Equal(Enumerable.Range(1, 5), session.ViewFor(losingSide).SwitchTargets);
        Assert.Empty(session.ViewFor(losingSide).Skills);
        RejectWithoutChange(session, () => session.SubmitReplace(0));
        RejectWithoutChange(session, () => session.SubmitReplace(6));
        RejectWithoutChange(session, () => session.Submit(BattleAction.Pass()));
        session.SubmitReplace(2);
        direct.Replace(losingSide, 2);
        Assert.Equal(losingSide, session.WhoseTurn);
        Assert.False(session.NeedsHandover);
        AssertCurrent(direct, session);
        FinishFixture(session, direct);
    }

    [Fact]
    public void BothReplacementChoicesWaitForConfirmationAndResolveInSideOrder()
    {
        var db = Fixture();
        var options = new BattleOptions { Seed = 86 };
        var session = Session(db, options);
        var direct = Direct(db, options);
        Turn(session, direct, BattleAction.Skill(2), BattleAction.Pass());
        Assert.Equal(Phase.Replace, session.Phase);
        Assert.All(direct.Need, need => Assert.True(need));
        Assert.Equal(1, session.WhoseTurn);
        string before = Visible(session);
        session.SubmitReplace(2);
        Assert.Equal(before, Visible(session));
        Assert.Equal(0, session.WhoseTurn);
        Assert.True(session.NeedsHandover);
        Assert.DoesNotContain(session.Record.Events, e => e.Type == BattleRecordEventType.Replace);
        RejectWithoutChange(session, () => session.SubmitReplace(3));
        session.ConfirmHandover();
        RejectWithoutChange(session, () => session.SubmitReplace(0));
        session.SubmitReplace(3);
        direct.Replace(0, 3);
        direct.Replace(1, 2);
        AssertCurrent(direct, session);
        Assert.Equal(new[] { 0, 1 }, session.Record.Events.Where(e => e.Type == BattleRecordEventType.Replace).Select(e => e.Side));
        Assert.Equal(0, session.WhoseTurn);
        FinishFixture(session, direct);
    }

    [Fact]
    public void InvalidChoicesAtEveryPhaseLeaveThePendingChoiceAndRandomStreamUntouched()
    {
        var db = Fixture();
        var options = new BattleOptions { Seed = 86, BanOne = true, LeadPick = true };
        var session = Session(db, options);
        var direct = Direct(db, options);
        RejectWithoutChange(session, () => session.ViewFor(-1));
        RejectWithoutChange(session, () => session.ViewFor(2));
        RejectWithoutChange(session, () => session.CloneCompletedBattle());
        RejectWithoutChange(session, () => session.SubmitBan(-1));
        RejectWithoutChange(session, () => session.SubmitBan(6));
        RejectWithoutChange(session, () => session.SubmitLead(0));
        RejectWithoutChange(session, () => session.Submit(BattleAction.Pass()));
        Pair(session, side => session.SubmitBan(5));
        direct.ChooseBan(0, 5);
        direct.ChooseBan(1, 5);
        RejectWithoutChange(session, () => session.SubmitLead(-1));
        RejectWithoutChange(session, () => session.SubmitLead(5));
        RejectWithoutChange(session, () => session.SubmitBan(0));
        RejectWithoutChange(session, () => session.SubmitReplace(1));
        Pair(session, side => session.SubmitLead(0));
        direct.ChooseLead(0, 0);
        direct.ChooseLead(1, 0);
        foreach (var action in new[]
        {
            BattleAction.Skill(-1), BattleAction.Skill(5), BattleAction.SwitchTo(0), BattleAction.SwitchTo(5),
            new BattleAction { Type = (ActionType)99 }, new BattleAction { Type = ActionType.Struggle },
            new BattleAction { Type = ActionType.PassControlled }
        }) RejectWithoutChange(session, () => session.Submit(action));
        RejectWithoutChange(session, () => session.SubmitLead(0));
        RejectWithoutChange(session, () => session.SubmitBan(0));
        RejectWithoutChange(session, () => session.SubmitReplace(1));
        Turn(session, direct, BattleAction.Skill(0), BattleAction.Skill(0));
        FinishFixture(session, direct);
        RejectWithoutChange(session, () => session.Submit(BattleAction.Pass()));
        RejectWithoutChange(session, () => session.SubmitReplace(1));
        RejectWithoutChange(session, () => session.SubmitLead(1));
        RejectWithoutChange(session, () => session.SubmitBan(1));
    }

    [Fact]
    public void ReturnedViewsLogsAndRecordsAreIndependentIncludingNestedCollections()
    {
        var db = Fixture();
        var options = new BattleOptions { Seed = 86, Modifiers = new List<string> { "hype_start" } };
        var teams = Teams();
        var session = Session(db, options, teams);
        var direct = Direct(db, options);
        Turn(session, direct, BattleAction.Skill(3), BattleAction.Pass());
        string before = State(session);
        var view = session.ViewFor(0);
        view.Sides[0].Name = "改名";
        view.Sides[0].Team[0].Hp = -10;
        view.Sides[0].Team[0].Stages["atk"] = 99;
        view.Sides[0].Team[0].Statuses[0].Stacks = 99;
        view.Sides[1].Team.Clear();
        view.Skills[0].Pp = -1;
        view.Skills[3].Effects[0].Text = "更改说明";
        view.SwitchTargets.Clear();
        view.NodeStrip.Clear();
        var log = session.Log;
        log[0].Text = "更改日志";
        log[0].SnapHp[0][0] = -1;
        log[0].SnapActive[0] = 4;
        var record = session.Record;
        record.Names[0] = "更改录像名字";
        record.Teams[0].Clear();
        record.Modifiers.Clear();
        record.Events[0].A0 = BattleAction.Skill(4);
        record.Events.Clear();
        teams[0].Clear();
        options.Modifiers.Clear();
        Assert.Equal(before, State(session));
        FinishFixture(session, direct);
    }

    [Fact]
    public void ControlledPlayerCanSubmitControlledPassButCannotUseASkillOrStruggle()
    {
        var db = Fixture();
        db.Skills["hotseat_setup"].Prio = 1;
        db.Skills["hotseat_setup"].Effects.Add(new EffectDef
        {
            At = "post", Code = "STATUS", Target = "foe", Status = "stall"
        });
        var options = new BattleOptions { Seed = 86 };
        var session = Session(db, options);
        var direct = Direct(db, options);
        Turn(session, direct, BattleAction.Skill(3), BattleAction.Pass());
        Assert.Equal(1, session.WhoseTurn);
        Assert.NotNull(session.ViewFor(1).ControlledReason);
        Assert.All(session.ViewFor(1).Skills, skill => Assert.False(skill.Usable));
        RejectWithoutChange(session, () => session.Submit(BattleAction.Skill(0)));
        RejectWithoutChange(session, () => session.Submit(new BattleAction { Type = ActionType.Struggle }));
        Turn(session, direct, BattleAction.Pass(), new BattleAction { Type = ActionType.PassControlled });
        Assert.Null(session.ViewFor(1).ControlledReason);
        Assert.Equal(ActionType.PassControlled, session.Record.Events.Last().A1.Type);
        FinishFixture(session, direct);
    }

    [Fact]
    public void ExhaustedSkillsAreRejectedAndExplicitStruggleCanCompleteTheBattle()
    {
        var db = Fixture();
        foreach (string id in new[] { "hotseat_hit", "hotseat_ko", "hotseat_both", "hotseat_setup" }) db.Skills[id].Pp = 0;
        var options = new BattleOptions { Seed = 86 };
        var session = Session(db, options);
        var direct = Direct(db, options);
        Assert.True(session.ViewFor(0).Struggle);
        RejectWithoutChange(session, () => session.Submit(BattleAction.Skill(0)));
        RejectWithoutChange(session, () => session.Submit(new BattleAction { Type = ActionType.PassControlled }));
        int guard = 300;
        while (direct.Phase != Phase.Over && guard-- > 0)
        {
            if (direct.Phase == Phase.Replace) Replacements(session, direct);
            else Turn(session, direct, new BattleAction { Type = ActionType.Struggle }, new BattleAction { Type = ActionType.Struggle });
        }
        AssertComplete(direct, session.CloneCompletedBattle());
        AssertComplete(direct, Replay.Run(db, session.Record));
    }

    [Fact]
    public void CompletedBattleIsAnIndependentSnapshotAndDrawHasNoNextPlayer()
    {
        var db = Fixture();
        var options = new BattleOptions { Seed = 86 };
        var session = Session(db, options);
        var direct = Direct(db, options);
        for (int i = 0; i < Battle.TurnCap; i++) Turn(session, direct, BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(-1, session.Winner);
        Assert.Equal(-1, session.WhoseTurn);
        Assert.False(session.NeedsHandover);
        var snapshot = session.CloneCompletedBattle();
        AssertComplete(direct, snapshot);
        string before = State(session);
        snapshot.Active(0).Hp = 0;
        snapshot.Active(0).Stages["atk"] = -6;
        snapshot.Active(0).Skills[0].Pp = 0;
        snapshot.Sides[1].Team.Clear();
        snapshot.Log[0].Text = "更改副本";
        snapshot.Log[0].SnapHp[0][0] = -1;
        snapshot.Events[0].Amount = 99;
        snapshot.Record.Events[0].A0 = BattleAction.Skill(1);
        snapshot.Random();
        Assert.Equal(before, State(session));
        AssertComplete(direct, session.CloneCompletedBattle());
        AssertComplete(direct, Replay.Run(db, session.Record));
    }
}
