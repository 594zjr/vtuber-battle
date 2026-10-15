using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T97_HotseatTextTests
{
    const string FirstName = "甲\n中之人";
    static readonly string SecondName = "乙\u200b\n" + string.Concat(Enumerable.Repeat("🦊", 20));
    static readonly string[] DisplayNames = { "甲***", "乙" + string.Concat(Enumerable.Repeat("🦊", 11)) };
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    static IList<string>[] Teams()
    {
        return new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        };
    }

    static ContentDb Fixture()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["t97_hotseat_neutral"] = new PersonaDef
        {
            Id = "t97_hotseat_neutral", Name = "测试人设", Code = "T_LOWHP_POWER"
        };
        foreach (string id in new[] { "t97_hotseat_ko", "t97_hotseat_both" })
        {
            var skill = new SkillDef { Id = id, Name = "测试下播", Type = "talk", Cat = "status", Pp = 20, Prio = 1 };
            skill.Effects.Add(new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 99999 });
            if (id == "t97_hotseat_both")
                skill.Effects.Add(new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "self", N = 99999 });
            db.Skills[id] = skill;
        }
        foreach (var fighter in db.Streamers)
        {
            fighter.Persona = "t97_hotseat_neutral";
            fighter.Stats["spd"] = 100;
            fighter.Skills = new List<string> { "t97_hotseat_ko", "t97_hotseat_both", "t97_hotseat_ko", "t97_hotseat_ko" };
            fighter.Fifth = "t97_hotseat_ko";
        }
        return db;
    }

    static HotseatSession Session(ContentDb db, BattleOptions options)
    {
        var teams = Teams();
        return new HotseatSession(db, teams[0], teams[1], FirstName, SecondName, options);
    }

    static Battle Direct(ContentDb db, BattleOptions options)
    {
        return new Battle(db, Teams(), new[] { FirstName, SecondName }, options);
    }

    static string Visible(HotseatSession session)
    {
        return Json(new
        {
            session.Phase, session.Winner, session.Log, session.DisplayLog,
            Record = session.Record.ToJson(), View0 = session.ViewFor(0), View1 = session.ViewFor(1)
        });
    }

    static void Pair(HotseatSession session, Action<int> submit)
    {
        Assert.False(session.NeedsHandover);
        int first = session.WhoseTurn;
        string before = Visible(session);
        submit(first);
        Assert.True(session.NeedsHandover);
        Assert.Equal(1 - first, session.WhoseTurn);
        Assert.Equal(before, Visible(session));
        session.ConfirmHandover();
        Assert.Equal(before, Visible(session));
        submit(1 - first);
    }

    static void AssertCurrent(Battle direct, HotseatSession session)
    {
        Assert.Equal(direct.Phase, session.Phase);
        Assert.Equal(direct.Record.ToJson(), session.Record.ToJson());
        Assert.Equal(Json(direct.Log), Json(session.Log));
        Assert.Equal(Json(TextInput.ForDisplay(direct).Log), Json(session.DisplayLog));
        Assert.Equal(new[] { FirstName, SecondName }, session.Record.Names);
        for (int side = 0; side < 2; side++) Assert.Equal(DisplayNames, session.ViewFor(side).Sides.Select(team => team.Name));
    }

    [Theory]
    [InlineData(Phase.Choose)]
    [InlineData(Phase.Ban)]
    [InlineData(Phase.Lead)]
    [InlineData(Phase.Replace)]
    public void FirstChoiceCannotLeakIntoDisplayLogOrNameViewsInAnyPairedPhase(Phase phase)
    {
        var db = Fixture();
        var options = new BattleOptions { Seed = 97, BanOne = phase == Phase.Ban, LeadPick = phase == Phase.Lead };
        var session = Session(db, options);
        var direct = Direct(db, options);
        if (phase == Phase.Replace)
        {
            Pair(session, side => session.Submit(side == 0 ? BattleAction.Skill(1) : BattleAction.Pass()));
            direct.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        }
        Assert.Equal(phase, session.Phase);
        AssertCurrent(direct, session);
        Assert.Throws<InvalidOperationException>(() => session.CloneCompletedBattle());
        if (phase == Phase.Choose)
        {
            Pair(session, side => session.Submit(side == 0 ? BattleAction.Skill(0) : BattleAction.Pass()));
            direct.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        }
        else if (phase == Phase.Ban)
        {
            Pair(session, side => session.SubmitBan(side == 0 ? 5 : 4));
            direct.ChooseBan(0, 5);
            direct.ChooseBan(1, 4);
        }
        else if (phase == Phase.Lead)
        {
            Pair(session, side => session.SubmitLead(side == 0 ? 2 : 3));
            direct.ChooseLead(0, 2);
            direct.ChooseLead(1, 3);
        }
        else
        {
            Pair(session, side => session.SubmitReplace(side == 0 ? 2 : 3));
            direct.Replace(0, 2);
            direct.Replace(1, 3);
        }
        AssertCurrent(direct, session);
    }

    [Fact]
    public void DisplayLogIsReadOnlyAndItsEntriesAndSnapshotsCannotModifyTheSession()
    {
        var db = Fixture();
        var options = new BattleOptions { Seed = 97 };
        var session = Session(db, options);
        var direct = Direct(db, options);
        AssertCurrent(direct, session);
        string before = Visible(session);
        var first = session.DisplayLog;
        var second = session.DisplayLog;
        Assert.NotSame(first, second);
        Assert.NotSame(first[0], second[0]);
        Assert.NotSame(first[0].SnapHp[0], second[0].SnapHp[0]);
        Assert.NotSame(first[0].SnapActive, second[0].SnapActive);
        Assert.Throws<NotSupportedException>(() => ((IList<LogEntry>)first).Clear());
        first[0].Text = "外部修改";
        first[0].SnapHp[0][0] = -1;
        first[0].SnapActive[0] = 5;
        Assert.Equal(before, Visible(session));
        string saved = Json(second);
        Pair(session, side => session.Submit(BattleAction.Pass()));
        direct.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        Assert.Equal(saved, Json(second));
        AssertCurrent(direct, session);
    }

    [Fact]
    public void CompleteHotseatBattleShowsSafeWinnerButPreservesOriginalReplayAndRandomStream()
    {
        var db = Fixture();
        var options = new BattleOptions { Seed = 97, BanOne = true, LeadPick = true };
        var session = Session(db, options);
        var direct = Direct(db, options);
        Pair(session, side => session.SubmitBan(5));
        direct.ChooseBan(0, 5);
        direct.ChooseBan(1, 5);
        Pair(session, side => session.SubmitLead(1));
        direct.ChooseLead(0, 1);
        direct.ChooseLead(1, 1);
        int guard = 30;
        while (direct.Phase != Phase.Over && guard-- > 0)
        {
            AssertCurrent(direct, session);
            if (direct.Phase == Phase.Choose)
            {
                Pair(session, side => session.Submit(side == 0 ? BattleAction.Skill(0) : BattleAction.Pass()));
                direct.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            }
            else
            {
                Assert.False(session.NeedsHandover);
                Assert.Equal(1, session.WhoseTurn);
                int to = direct.Sides[1].Team.FindIndex(fighter => !fighter.Fainted);
                session.SubmitReplace(to);
                direct.Replace(1, to);
            }
        }
        Assert.Equal(Phase.Over, session.Phase);
        Assert.Equal(0, session.Winner);
        AssertCurrent(direct, session);
        Assert.Equal("甲*** 获胜", session.DisplayLog.Last().Text);
        Assert.Equal(FirstName + " 获胜", session.Log.Last().Text);
        Assert.All(session.DisplayLog, entry =>
        {
            Assert.DoesNotContain(FirstName, entry.Text);
            Assert.DoesNotContain(SecondName, entry.Text);
        });
        var completed = session.CloneCompletedBattle();
        var replayed = Replay.Run(db, session.Record);
        foreach (var actual in new[] { completed, replayed })
        {
            Assert.Equal(direct.StateHash(), actual.StateHash());
            Assert.Equal(direct.Record.ToJson(), actual.Record.ToJson());
            Assert.Equal(Json(direct.Log), Json(actual.Log));
            Assert.Equal(Json(direct.Events), Json(actual.Events));
            Assert.Equal(direct.Clone().Random(), actual.Clone().Random());
        }
    }
}
