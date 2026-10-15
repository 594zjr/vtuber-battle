using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using StreamDuel.Core;
using Xunit;

public class T98_SurrenderDrawTests
{
    [Theory]
    [InlineData(Phase.Choose, 0)]
    [InlineData(Phase.Choose, 1)]
    [InlineData(Phase.Replace, 0)]
    [InlineData(Phase.Replace, 1)]
    [InlineData(Phase.Lead, 0)]
    [InlineData(Phase.Lead, 1)]
    [InlineData(Phase.Ban, 0)]
    [InlineData(Phase.Ban, 1)]
    public void SurrenderImmediatelyEndsEveryExistingSelectionPhaseWithoutRunningAnotherAction(Phase phase, int side)
    {
        var battle = AtPhase(phase);
        var before = battle.Clone();
        string[] teams = TeamState(battle);
        int eventStart = battle.Events.Count;

        battle.Surrender(side);

        AssertEnded(battle, 1 - side);
        Assert.Equal(before.Turn, battle.Turn);
        Assert.Equal(teams, TeamState(battle));
        Assert.DoesNotContain(battle.Events.Skip(eventStart), e => e.Kind == BattleEventKind.Act
            || e.Kind == BattleEventKind.Damage || e.Kind == BattleEventKind.PpChange);
        AssertSameRandom(before, battle);
        Assert.Equal(3, battle.Record.Version);
        AssertReplayCloneAndCursor(battle);
    }

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 0)]
    [InlineData(true, true, -1)]
    public void TurnSubmissionChecksBothSurrendersBeforeEitherFightersLethalAction(bool left, bool right, int winner)
    {
        var battle = Make();
        var before = battle.Clone();
        int eventStart = battle.Events.Count;
        battle.ResolveTurn(left ? BattleAction.Surrender() : BattleAction.Skill(1),
            right ? BattleAction.Surrender() : BattleAction.Skill(1));

        AssertEnded(battle, winner);
        Assert.Equal(before.Turn, battle.Turn);
        Assert.Equal(TeamState(before), TeamState(battle));
        Assert.DoesNotContain(battle.Events.Skip(eventStart), e => e.Kind == BattleEventKind.Act
            || e.Kind == BattleEventKind.Damage || e.Kind == BattleEventKind.PpChange);
        AssertSameRandom(before, battle);
        Assert.Equal(3, battle.Record.Version);
        AssertReplayCloneAndCursor(battle);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SurrenderIsNotNormalizedIntoControlPassOrStruggle(int side)
    {
        var battle = Make();
        battle.Active(side).Statuses.Add(new StatusInst { Id = "disconnect", Left = 2 });
        foreach (var skill in battle.Active(side).Skills) skill.Pp = 0;
        var before = battle.Clone();
        battle.ResolveTurn(side == 0 ? BattleAction.Surrender() : BattleAction.Skill(1),
            side == 1 ? BattleAction.Surrender() : BattleAction.Skill(1));

        AssertEnded(battle, 1 - side);
        Assert.Equal(TeamState(before), TeamState(battle));
        AssertSameRandom(before, battle);
    }

    [Theory]
    [InlineData(Phase.Choose)]
    [InlineData(Phase.Replace)]
    [InlineData(Phase.Lead)]
    [InlineData(Phase.Ban)]
    public void AcceptedDrawCanEndEachExistingSelectionPhaseAndReplaysWithoutInventingActions(Phase phase)
    {
        var battle = AtPhase(phase);
        var before = battle.Clone();
        battle.OfferDraw(0);
        Assert.Equal(phase, battle.Phase);
        battle.RespondDraw(1, true);

        AssertEnded(battle, -1);
        Assert.Equal(before.Turn, battle.Turn);
        Assert.Equal(TeamState(before), TeamState(battle));
        AssertSameRandom(before, battle);
        Assert.Equal(3, battle.Record.Version);
        AssertReplayCloneAndCursor(battle);
    }

    [Fact]
    public void RejectedDrawPreservesGameplayAndItsRecordedResponseSurvivesLaterSurrender()
    {
        var battle = Make();
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        var before = battle.Clone();
        battle.OfferDraw(1);
        Assert.NotEqual(before.StateHash(), battle.StateHash());
        battle.RespondDraw(0, false);

        Assert.Equal(before.Phase, battle.Phase);
        Assert.Equal(before.Winner, battle.Winner);
        Assert.Equal(before.Turn, battle.Turn);
        Assert.Equal(TeamState(before), TeamState(battle));
        Assert.Equal(BattleSummary.From(before).ToText(), BattleSummary.From(battle).ToText());
        AssertSameRandom(before, battle);
        Assert.DoesNotContain(battle.Events, e => e.Kind == BattleEventKind.End);
        Assert.Equal(3, battle.Record.Version);
        AssertReplayCloneAndCursor(battle);

        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        battle.Surrender(1);
        AssertReplayCloneAndCursor(battle);
    }

    [Fact]
    public void DrawCooldownIsPerSideAndOpensExactlyFiveTurnsAfterTheOffer()
    {
        var battle = Make();
        Assert.Equal(1, battle.Turn);
        Assert.True(battle.CanOfferDraw(0));
        battle.OfferDraw(0);
        battle.RespondDraw(1, false);
        Assert.False(battle.CanOfferDraw(0));
        Assert.True(battle.CanOfferDraw(1));
        battle.OfferDraw(1);
        battle.RespondDraw(0, false);

        for (int turn = 1; turn < 6; turn++)
        {
            Assert.Equal(turn, battle.Turn);
            for (int side = 0; side < 2; side++)
            {
                Assert.False(battle.CanOfferDraw(side));
                AssertRejectedWithoutMutation(battle, () => battle.OfferDraw(side));
            }
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        }
        Assert.Equal(6, battle.Turn);
        Assert.True(battle.CanOfferDraw(0));
        Assert.True(battle.CanOfferDraw(1));
        battle.OfferDraw(0);
        battle.RespondDraw(1, true);
        AssertEnded(battle, -1);
        AssertReplayCloneAndCursor(battle);
    }

    [Fact]
    public void OnlyTheOpponentCanAnswerAndInvalidResponsesDoNotAlterThePendingOffer()
    {
        var battle = Make();
        AssertRejectedWithoutMutation(battle, () => battle.RespondDraw(1, true));
        battle.OfferDraw(0);
        AssertRejectedWithoutMutation(battle, () => battle.RespondDraw(0, true));
        AssertRejectedWithoutMutation(battle, () => battle.RespondDraw(0, false));
        AssertRejectedWithoutMutation(battle, () => battle.OfferDraw(0));
        battle.RespondDraw(1, false);
        AssertRejectedWithoutMutation(battle, () => battle.RespondDraw(1, true));
        AssertReplayCloneAndCursor(battle);
    }

    [Fact]
    public void CloneIndependentlyPreservesPendingDrawAndBothSidesCooldowns()
    {
        var battle = Make();
        battle.OfferDraw(0);
        var copy = battle.Clone();
        AssertSameBattle(battle, copy);
        string original = battle.Record.ToJson();
        ulong hash = battle.StateHash();

        copy.RespondDraw(1, false);
        Assert.Equal(original, battle.Record.ToJson());
        Assert.Equal(hash, battle.StateHash());
        Assert.NotEqual(battle.StateHash(), copy.StateHash());
        Assert.False(copy.CanOfferDraw(0));
        Assert.True(copy.CanOfferDraw(1));
        battle.RespondDraw(1, true);
        AssertEnded(battle, -1);
        Assert.Equal(Phase.Choose, copy.Phase);
        copy.OfferDraw(1);
        copy.RespondDraw(0, true);
        AssertReplayCloneAndCursor(copy);
        AssertReplayCloneAndCursor(battle);
    }

    [Fact]
    public void SimultaneousOffersStayIndependentAndTheirArrivalOrderDoesNotChangeStateHash()
    {
        var battle = Make();
        var reversed = Make();
        battle.OfferDraw(0);
        battle.OfferDraw(1);
        reversed.OfferDraw(1);
        reversed.OfferDraw(0);
        Assert.Equal(battle.StateHash(), reversed.StateHash());
        Assert.True(battle.HasDrawOffer(0));
        Assert.True(battle.HasDrawOffer(1));

        battle.RespondDraw(0, false);
        Assert.True(battle.HasDrawOffer(0));
        Assert.False(battle.HasDrawOffer(1));
        Assert.False(battle.CanOfferDraw(0));
        Assert.False(battle.CanOfferDraw(1));
        battle.RespondDraw(1, true);
        AssertEnded(battle, -1);
        Assert.False(battle.HasDrawOffer(0));
        Assert.False(battle.HasDrawOffer(1));
        AssertReplayCloneAndCursor(battle);
    }

    [Fact]
    public void SurrenderWithAPendingOfferEndsOnceAndRejectsFurtherTerminalOperations()
    {
        var battle = Make();
        battle.OfferDraw(0);
        battle.Surrender(1);
        AssertEnded(battle, 0);
        Assert.False(battle.CanOfferDraw(0));
        Assert.False(battle.CanOfferDraw(1));
        AssertRejectedWithoutMutation(battle, () => battle.Surrender(0));
        AssertRejectedWithoutMutation(battle, () => battle.OfferDraw(1));
        AssertRejectedWithoutMutation(battle, () => battle.RespondDraw(1, true));
        AssertRejectedWithoutMutation(battle, () => battle.ResolveTurn(BattleAction.Surrender(), BattleAction.Surrender()));
        AssertReplayCloneAndCursor(battle);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void InvalidSideIsRejectedBeforeAnyStateOrHistoryChanges(int side)
    {
        var battle = Make();
        foreach (Action operation in new Action[]
        {
            () => battle.Surrender(side), () => battle.OfferDraw(side),
            () => battle.RespondDraw(side, true), () => Ai.AcceptDraw(battle, side)
        })
        {
            var before = battle.Clone();
            Assert.ThrowsAny<ArgumentException>(operation);
            AssertSameBattle(before, battle);
        }
    }

    [Theory]
    [InlineData(1300, 1000, true)]
    [InlineData(1301, 1000, false)]
    [InlineData(1299, 1000, true)]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, true)]
    public void AiDrawDecisionUsesWholeTeamCurrentHpAndAStrictThirtyPercentThreshold(int own, int foe, bool accept)
    {
        var battle = Make();
        SetTotalHp(battle, 0, own);
        SetTotalHp(battle, 1, foe);
        var before = battle.Clone();
        Assert.Equal(accept, Ai.AcceptDraw(battle, 0));
        Assert.Equal(foe * 10L <= own * 13L, Ai.AcceptDraw(battle, 1));
        AssertSameBattle(before, battle);
    }

    [Fact]
    public void AiDrawDecisionDoesNotOverflowWhenSixHpValuesExceedInt32InTotal()
    {
        var battle = Make();
        foreach (var fighter in battle.Sides.SelectMany(s => s.Team)) fighter.Hp = int.MaxValue;
        Assert.True(Ai.AcceptDraw(battle, 0));
        foreach (var fighter in battle.Sides[1].Team) fighter.Hp = 1;
        Assert.False(Ai.AcceptDraw(battle, 0));
        Assert.True(Ai.AcceptDraw(battle, 1));
    }

    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    [InlineData(AiLevel.Expert)]
    public void ExistingAiLevelsNeverInitiateSurrenderAndNormalGamesUseVersionThree(AiLevel level)
    {
        var battle = Make();
        Assert.Equal(3, battle.Record.Version);
        for (int turn = 0; turn < 2; turn++)
        {
            var left = Ai.Choose(battle, 0, level);
            var right = Ai.Choose(battle, 1, level);
            Assert.NotEqual(ActionType.Surrender, left.Type);
            Assert.NotEqual(ActionType.Surrender, right.Type);
            battle.ResolveTurn(left, right);
            for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, level));
        }
        Assert.Equal(3, battle.Record.Version);
        Assert.Equal(3, BattleRecord.FromJson(battle.Record.ToJson()).Version);
        Assert.True(Replay.Check(battle.Db, battle.Record).CanReplay);
    }

    [Fact]
    public void ProfilesCountSurrenderAsLossAndAgreementAsDrawForBothPerspectives()
    {
        var loss = Make();
        loss.Surrender(0);
        var draw = Make();
        draw.OfferDraw(1);
        draw.RespondDraw(0, true);
        for (int side = 0; side < 2; side++)
        {
            var profile = new Profile();
            profile.Record(loss, side);
            profile.Record(draw, side);
            var stats = profile.Packs[loss.Db.PackId];
            Assert.Equal(side == 0 ? 0 : 1, stats.Wins);
            Assert.Equal(side == 0 ? 1 : 0, stats.Losses);
            Assert.Equal(1, stats.Draws);
            foreach (var fighter in loss.Sides[side].Team)
            {
                Assert.Equal(2, stats.Picks[fighter.Def.Id]);
                Assert.Equal(side == 0 ? 0 : 1, stats.PickWins[fighter.Def.Id]);
            }
            Assert.Equal(profile.ToJson(), Profile.FromJson(profile.ToJson()).ToJson());
        }
    }

    [Theory]
    [InlineData("surrender-ban", "2ec0ca0dced5ab04", 1, 2)]
    [InlineData("draw-accepted", "6356d162fb42293e", -1, 5)]
    public void FrozenVersionThreeRecordsRetainTheirOriginalTerminalHash(string scenario, string hash, int winner, int count)
    {
        string json = File.ReadAllText(Path.Combine(GoldenDirectory(), "t98-v3", scenario + ".json"));
        var record = BattleRecord.FromJson(json);
        // 共用已经冻结的历史内容快照，正式包调平衡不应改写终局协议样例。
        var db = ContentDb.FromJson(File.ReadAllText(Path.Combine(GoldenDirectory(), "content", "StreamDuelRules.json")),
            File.ReadAllText(Path.Combine(GoldenDirectory(), "content", "original.json")));
        Assert.Equal("63a758a4fa663be9", db.ContentHash());
        Assert.Equal(3, record.Version);
        Assert.Equal(count, record.Events.Count);
        Assert.True(Replay.Check(db, record).CanReplay);
        var replayed = Replay.Run(db, record);
        AssertEnded(replayed, winner);
        Assert.Equal(hash, replayed.StateHash().ToString("x16", CultureInfo.InvariantCulture));

        var ids = db.Streamers.Select(s => s.Id).ToArray();
        var fresh = new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "甲方", "乙方" }, new BattleOptions { Seed = 98, BanOne = scenario == "surrender-ban", LeadPick = scenario == "surrender-ban" });
        if (scenario == "surrender-ban")
        {
            fresh.ChooseBan(1, 4);
            fresh.Surrender(0);
        }
        else
        {
            fresh.OfferDraw(0);
            fresh.RespondDraw(1, false);
            fresh.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            fresh.OfferDraw(1);
            fresh.RespondDraw(0, true);
        }
        Assert.Equal(record.ToJson(), fresh.Record.ToJson());
        AssertSameBattle(fresh, replayed);
        AssertReplayCloneAndCursor(replayed);
        Assert.Equal(json, File.ReadAllText(Path.Combine(GoldenDirectory(), "t98-v3", scenario + ".json")));
    }

    [Theory]
    [InlineData("surrender")]
    [InlineData("drawOffer")]
    [InlineData("drawResponse")]
    public void NewEventsCannotBeMislabelledAsHistoricalVersionOneOrTwo(string ending)
    {
        var battle = Make();
        if (ending == "surrender") battle.Surrender(0);
        else
        {
            battle.OfferDraw(0);
            if (ending == "drawResponse") battle.RespondDraw(1, false);
        }
        string json = battle.Record.ToJson();
        foreach (int oldVersion in new[] { 1, 2 })
        {
            var record = BattleRecord.FromJson(json);
            record.Version = oldVersion;
            var check = Replay.Check(battle.Db, record);
            Assert.False(check.CanReplay);
            Assert.Contains("版本 3", check.Reason);
            Assert.Throws<FormatException>(() => record.ToJson());
            Assert.Throws<FormatException>(() => Replay.Run(battle.Db, record));
            Assert.Throws<FormatException>(() => BattleRecord.FromJson(json.Replace("\"version\":3", "\"version\":" + oldVersion)));
        }
    }

    static string GoldenDirectory([CallerFilePath] string source = "")
    {
        return Path.Combine(Path.GetDirectoryName(source), "Golden", "replays");
    }

    static ContentDb FixtureDb()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["t98_none"] = new PersonaDef
        {
            Id = "t98_none", Name = "测试人设", Code = "T_LOWHP_POWER",
            Params = new Dictionary<string, double> { ["below"] = 0, ["mul"] = 1 }
        };
        db.Skills["t98_wait"] = new SkillDef { Id = "t98_wait", Name = "测试等待", Type = "talk", Cat = "status", Pp = 20 };
        db.Skills["t98_hit"] = new SkillDef
        {
            Id = "t98_hit", Name = "测试下播", Type = "talk", Cat = "status", Pp = 20,
            Effects = { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 10000 } }
        };
        foreach (var streamer in db.Streamers)
        {
            streamer.Persona = "t98_none";
            streamer.Stats["hp"] = 1000;
            streamer.Skills = new List<string> { "t98_wait", "t98_hit", "warm_up", "quick_mic" };
            streamer.Fifth = "t98_wait";
        }
        return db;
    }

    static Battle Make(bool ban = false, bool lead = false)
    {
        var db = FixtureDb();
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        return new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "甲方", "乙方" }, new BattleOptions { Seed = 98, BanOne = ban, LeadPick = lead });
    }

    static Battle AtPhase(Phase phase)
    {
        var battle = Make(phase == Phase.Ban, phase == Phase.Lead);
        if (phase == Phase.Ban) battle.ChooseBan(1, 4);
        if (phase == Phase.Lead) battle.ChooseLead(1, 2);
        if (phase == Phase.Replace) battle.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        Assert.Equal(phase, battle.Phase);
        return battle;
    }

    static void SetTotalHp(Battle battle, int side, int total)
    {
        foreach (var fighter in battle.Sides[side].Team) { fighter.Hp = 0; fighter.Fainted = true; }
        // 把大部分人气放在替补，防止实现只比较场上主播。
        battle.Active(side).Hp = Math.Min(1, total);
        battle.Active(side).Fainted = total == 0;
        int remaining = total - battle.Active(side).Hp;
        foreach (var fighter in battle.Sides[side].Team.Skip(1))
        {
            fighter.Hp = Math.Min(fighter.MaxHp, remaining);
            fighter.Fainted = fighter.Hp == 0;
            remaining -= fighter.Hp;
        }
        Assert.Equal(0, remaining);
    }

    static string[] TeamState(Battle battle)
    {
        return battle.Sides.SelectMany(side => side.Team.Select(f => string.Join("|", side.Active, f.Def.Id,
            f.Hp, f.MaxHp, f.Fainted, f.LastSkillIndex, string.Join(",", f.Skills.Select(s => s.Id + ":" + s.Pp)),
            string.Join(",", f.Statuses.Select(s => s.Id + ":" + s.Left)),
            string.Join(",", f.Fields.Select(s => s.Id + ":" + s.Turns + ":" + s.Stacks)),
            string.Join(",", ContentDb.StatKeys.Select(k => f.Stages[k]))))).ToArray();
    }

    static string[] Logs(Battle battle) => battle.Log.Select(e => string.Join("|", e.Turn, e.Node, e.Kind,
        e.Side, e.Text, string.Join(",", e.SnapActive), string.Join(",", e.SnapHp[0]), string.Join(",", e.SnapHp[1]))).ToArray();

    static string[] Events(Battle battle) => battle.Events.Select(e => string.Join("|", e.LogIndex, e.Turn,
        e.Node, e.Kind, e.Side, e.Slot, e.Id, e.Amount)).ToArray();

    static void AssertEnded(Battle battle, int winner)
    {
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(winner, battle.Winner);
        var end = Assert.Single(battle.Events, e => e.Kind == BattleEventKind.End);
        Assert.Equal(winner, end.Side);
        Assert.Equal(Node.Over, end.Node);
        Assert.Equal(battle.Turn, end.Turn);
        Assert.InRange(end.LogIndex, 0, battle.Log.Count - 1);
        Assert.Equal(Node.Over, battle.Log[end.LogIndex].Node);
        Assert.Matches("[\u4e00-\u9fff]", battle.Log[end.LogIndex].Text);
    }

    static void AssertSameRandom(Battle expected, Battle actual)
    {
        var left = expected.Clone();
        var right = actual.Clone();
        for (int i = 0; i < 8; i++) Assert.Equal(left.Random(), right.Random());
    }

    static void AssertSameBattle(Battle expected, Battle actual)
    {
        Assert.Equal(expected.StateHash(), actual.StateHash());
        Assert.Equal(expected.Phase, actual.Phase);
        Assert.Equal(expected.Winner, actual.Winner);
        Assert.Equal(expected.Turn, actual.Turn);
        Assert.Equal(TeamState(expected), TeamState(actual));
        Assert.Equal(Logs(expected), Logs(actual));
        Assert.Equal(Events(expected), Events(actual));
        Assert.Equal(expected.Record.ToJson(), actual.Record.ToJson());
        Assert.Equal(BattleSummary.From(expected).ToText(), BattleSummary.From(actual).ToText());
        AssertSameRandom(expected, actual);
    }

    static void AssertRejectedWithoutMutation(Battle battle, Action operation)
    {
        var before = battle.Clone();
        var error = Assert.Throws<InvalidOperationException>(operation);
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
        AssertSameBattle(before, battle);
    }

    static void AssertReplayCloneAndCursor(Battle battle)
    {
        string json = battle.Record.ToJson();
        var record = BattleRecord.FromJson(json);
        Assert.Equal(json, record.ToJson());
        Assert.True(Replay.Check(battle.Db, record).CanReplay);
        AssertSameBattle(battle, Replay.Run(battle.Db, record));
        AssertSameBattle(battle, battle.Clone());
        var cursor = new ReplayCursor(battle.Db, record, 1);
        cursor.ToEnd();
        AssertSameBattle(battle, cursor.Current);
        cursor.Seek(0);
        while (cursor.Next()) { }
        AssertSameBattle(battle, cursor.Current);
        Assert.Equal(json, battle.Record.ToJson());
    }
}
