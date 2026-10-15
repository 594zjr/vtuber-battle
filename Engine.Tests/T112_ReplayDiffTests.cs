using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T112_ReplayDiffTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    static ContentDb Content()
    {
        // 固定的小内容库避免正式内容包调数值改变分歧位置和诊断数值。
        var db = new ContentDb { PackId = "t112_fixture", PackName = "录像差异测试", PackNotice = "独立测试数据" };
        db.Types["talk"] = new TypeDef { Id = "talk", Name = "聊天", Color = "#808080" };
        db.Chart["talk"] = new Dictionary<string, double> { { "talk", 1 } };
        db.Personas["neutral"] = new PersonaDef { Id = "neutral", Name = "测试人设", Code = "T_LOWHP_POWER" };
        db.Statuses["diff_weak"] = new StatusDef { Id = "diff_weak", Name = "测试弱化", Cat = "weak", Turns = 7, Cleanse = true };
        db.Fields["diff_aura"] = new FieldDef { Id = "diff_aura", Name = "测试直播间", Kind = "turn", Dispel = true };
        Skill(db, "fixed", Fixed(7));
        Skill(db, "enrich",
            new EffectDef { At = "post", Code = "STAGE", Target = "foe", Changes = new Dictionary<string, int> { { "atk", 2 }, { "def", -1 } } },
            new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "diff_weak" },
            new EffectDef { At = "post", Code = "FIELD", Target = "foe", Field = "diff_aura", Turns = 7 }, Fixed(7));
        Skill(db, "heal", new EffectDef { At = "post", Code = "HEAL", Target = "self", Frac = 0.1 });
        Skill(db, "wait");
        Skill(db, "finish", Fixed(99999));
        db.Struggle = new SkillDef { Id = "struggle", Name = "测试硬撑", Type = "talk", Cat = "interact", Power = 20, Pp = 1 };
        for (int index = 0; index < 12; index++)
            db.Streamers.Add(new StreamerDef
            {
                Id = "u" + index.ToString("D2"), Name = "测试主播" + index, Type = "talk", Persona = "neutral",
                Stats = new Dictionary<string, int>
                {
                    { "hp", 1000 }, { "atk", 100 }, { "def", 100 }, { "spa", 100 }, { "sdef", 100 }, { "spd", 200 - index }
                },
                Skills = new List<string> { "fixed", "enrich", "heal", "wait" }, Fifth = "finish"
            });
        db.Validate();
        return db;
    }

    static EffectDef Fixed(int n) { return new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = n }; }

    static void Skill(ContentDb db, string id, params EffectDef[] effects)
    {
        db.Skills[id] = new SkillDef
        {
            Id = id, Name = "测试技能 " + id, Type = "talk", Cat = "status", Pp = 20, Acc = 100,
            Effects = effects.ToList()
        };
    }

    static Battle Make(ContentDb db, bool ban = false, bool lead = false, List<string> modifiers = null)
    {
        return new Battle(db, new IList<string>[]
        {
            db.Streamers.Take(6).Select(s => s.Id).ToArray(), db.Streamers.Skip(6).Take(6).Select(s => s.Id).ToArray()
        }, new[] { "甲方", "乙方" }, new BattleOptions { Seed = 112, BanOne = ban, LeadPick = lead, Modifiers = modifiers });
    }

    static Battle Passes(ContentDb db, int turns)
    {
        var battle = Make(db);
        for (int i = 0; i < turns; i++) battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        return battle;
    }

    static BattleRecord Copy(BattleRecord source) { return BattleRecord.FromJson(source.ToJson()); }
    static string Json<T>(T value) { return JsonSerializer.Serialize(value, JsonOptions); }

    static BattleRecord Prefix(BattleRecord source, int position)
    {
        var copy = Copy(source);
        int turns = 0, count = 0;
        foreach (var entry in copy.Events)
        {
            if (entry.Type == BattleRecordEventType.Turn && ++turns > position) break;
            count++;
        }
        copy.Events.RemoveRange(count, copy.Events.Count - count);
        return copy;
    }

    static void AssertHashesAt(ContentDb db, BattleRecord a, BattleRecord b, ReplayDiffResult diff, int position)
    {
        // 从头执行裁剪后的记录作为 oracle，不复用差异比较器或回看游标的分组状态。
        ulong expectedA = Replay.RunBestEffort(db, Prefix(a, position)).StateHash();
        ulong expectedB = Replay.RunBestEffort(db, Prefix(b, position)).StateHash();
        Assert.NotEqual(expectedA, expectedB);
        Assert.Equal(position, diff.FirstStateDifferenceTurn);
        Assert.Equal(expectedA, diff.StateHashA);
        Assert.Equal(expectedB, diff.StateHashB);
    }

    static void AssertNoDifferences(ReplayDiffResult result)
    {
        Assert.True(result.SameOpening);
        Assert.Empty(result.OpeningDifferences);
        Assert.Null(result.FirstActionDifferenceTurn);
        Assert.Null(result.FirstStateDifferenceTurn);
        Assert.Null(result.EndDifferenceTurn);
        Assert.Null(result.ShorterSide);
        Assert.Null(result.StateHashA);
        Assert.Null(result.StateHashB);
        Assert.Empty(result.Differences);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARecordingComparedWithItselfHasNoActionStateOrLengthDifference(bool finished)
    {
        var db = Content();
        var battle = Passes(db, 4);
        if (finished) battle.Surrender(1);
        string recordBefore = Json(battle.Record), dbBefore = Json(db);

        var result = ReplayDiff.Compare(db, battle.Record, battle.Record);

        AssertNoDifferences(result);
        Assert.Equal(4, result.TurnsA);
        Assert.Equal(4, result.TurnsB);
        Assert.Equal(recordBefore, Json(battle.Record));
        Assert.Equal(dbBefore, Json(db));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void AChangedActionReportsItsFirstTurnAndTheHashAtThatPositionInsteadOfTheFinalHash(int changedTurn)
    {
        var db = Content();
        var a = Make(db);
        var b = Make(db);
        for (int turn = 1; turn <= 5; turn++)
        {
            a.ResolveTurn(turn == changedTurn ? BattleAction.Skill(0) : BattleAction.Pass(), BattleAction.Pass());
            b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        }

        var result = ReplayDiff.Compare(db, a.Record, b.Record);

        Assert.True(result.SameOpening);
        Assert.Equal(changedTurn, result.FirstActionDifferenceTurn);
        AssertHashesAt(db, a.Record, b.Record, result, changedTurn);
        Assert.Equal(5, result.TurnsA);
        Assert.Equal(5, result.TurnsB);
        Assert.Null(result.EndDifferenceTurn);
        Assert.NotEmpty(result.Differences);
        Assert.Contains(result.Differences, line => line.Contains("[u06]") && line.Contains("人气 HP") && line.Contains("993") && line.Contains("1000"));
    }

    [Theory]
    [InlineData("seed", "种子")]
    [InlineData("team-order", "队伍")]
    [InlineData("lead", "首发")]
    [InlineData("ban", "禁用")]
    [InlineData("modifiers", "修饰器")]
    [InlineData("pack", "内容包")]
    public void DifferentOpeningExplainsTheHeaderFieldWithoutExecutingInvalidTrailingHistory(string field, string label)
    {
        var db = Content();
        var a = Copy(Make(db).Record);
        a.Events.Add(new BattleRecordEvent { Type = BattleRecordEventType.Replace, Side = 0, To = 1 });
        var b = Copy(a);
        switch (field)
        {
            case "seed": b.Seed++; break;
            case "team-order": b.Teams[0] = b.Teams[0].Reverse().ToArray(); break;
            case "lead": b.LeadPick = true; break;
            case "ban": b.BanOne = true; break;
            case "modifiers": b.Modifiers = new List<string> { "half_pp" }; break;
            case "pack": b.PackId = "another_pack"; break;
        }
        string beforeA = Json(a), beforeB = Json(b);

        var result = ReplayDiff.Compare(db, a, b);

        Assert.False(result.SameOpening);
        Assert.Contains(result.OpeningDifferences, line => line.Contains("开局不同") && line.Contains(label));
        if (field == "seed") Assert.Contains("开局不同：种子", string.Join("\n", result.OpeningDifferences));
        Assert.Null(result.FirstActionDifferenceTurn);
        Assert.Null(result.FirstStateDifferenceTurn);
        Assert.Null(result.StateHashA);
        Assert.Null(result.StateHashB);
        Assert.Equal(beforeA, Json(a));
        Assert.Equal(beforeB, Json(b));
    }

    [Fact]
    public void SupportedVersionNamesAndContentFingerprintDoNotInventOpeningOrStateDifferences()
    {
        var db = Content();
        var a = Copy(Passes(db, 3).Record);
        var b = Copy(a);
        b.Version = 1;
        b.Names = new[] { "不同的甲方昵称", "另一位乙方" };
        b.ContentHash = "older_content_fingerprint";
        string before = Json(b);

        var result = ReplayDiff.Compare(db, a, b);

        AssertNoDifferences(result);
        Assert.Contains(result.Warnings, warning => warning.Contains("内容指纹不同") && warning.Contains("结果可能不同"));
        Assert.Equal(before, Json(b));
    }

    [Fact]
    public void ModifierOrderingAndDuplicatesUseTheSameNormalizedOpeningWithoutMutatingTheLists()
    {
        var db = Content();
        var battle = Make(db, modifiers: new List<string> { "hp_x2", "half_pp" });
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        var a = Copy(battle.Record);
        var b = Copy(a);
        b.Modifiers = new List<string> { "hp_x2", "half_pp", "hp_x2" };
        string before = Json(b);

        AssertNoDifferences(ReplayDiff.Compare(db, a, b));

        Assert.Equal(before, Json(b));
        Assert.Equal(new[] { "hp_x2", "half_pp", "hp_x2" }, b.Modifiers);
    }

    [Fact]
    public void DifferentRawActionsMayNormalizeToTheSameStateAndStillReportTheActionDifference()
    {
        var db = Content();
        var a = Make(db);
        var b = Make(db);
        for (int turn = 1; turn <= 4; turn++)
        {
            a.ResolveTurn(turn == 3 ? BattleAction.Skill(99) : BattleAction.Pass(), BattleAction.Pass());
            b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        }
        Assert.Equal(a.StateHash(), b.StateHash());

        var result = ReplayDiff.Compare(db, a.Record, b.Record);

        Assert.True(result.SameOpening);
        Assert.Equal(3, result.FirstActionDifferenceTurn);
        Assert.Null(result.FirstStateDifferenceTurn);
        Assert.Null(result.StateHashA);
        Assert.Null(result.StateHashB);
        Assert.NotEmpty(result.Differences);
    }

    [Fact]
    public void RandomCheckpointOnlyDifferenceIsReportedEvenWhenTheNextCheckpointMakesFinalStatesEqual()
    {
        var db = Content();
        var a = Copy(Passes(db, 3).Record);
        var b = Copy(a);
        b.Events[1].RngState = unchecked(b.Events[1].RngState.Value + 1);
        Assert.Equal(Replay.Run(db, a).StateHash(), Replay.Run(db, b).StateHash());
        Assert.Equal(Json(Replay.Run(db, Prefix(a, 2)).Sides), Json(Replay.Run(db, Prefix(b, 2)).Sides));

        var result = ReplayDiff.Compare(db, a, b);

        Assert.Null(result.FirstActionDifferenceTurn);
        AssertHashesAt(db, a, b, result, 2);
        Assert.Contains(result.Differences, line => line.Contains("随机状态 RNG"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ShorterRecordingReportsTheFirstMissingTurnAndWhichInputEnded(int shorterSide)
    {
        var db = Content();
        var full = Copy(Passes(db, 4).Record);
        var shorter = Prefix(full, 2);
        var a = shorterSide == 0 ? shorter : full;
        var b = shorterSide == 0 ? full : shorter;

        var result = ReplayDiff.Compare(db, a, b);

        Assert.True(result.SameOpening);
        Assert.Equal(3, result.EndDifferenceTurn);
        Assert.Equal(shorterSide, result.ShorterSide);
        Assert.Equal(shorterSide == 0 ? 2 : 4, result.TurnsA);
        Assert.Equal(shorterSide == 0 ? 4 : 2, result.TurnsB);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void MissingFinalNegotiationEventsAreDetectedEvenWhenTurnCountsAreEqual(int turns)
    {
        var db = Content();
        var a = Passes(db, turns);
        var b = Passes(db, turns);
        b.OfferDraw(0);
        b.RespondDraw(1, false);

        var result = ReplayDiff.Compare(db, a.Record, b.Record);

        Assert.True(result.SameOpening);
        Assert.Equal(turns, result.TurnsA);
        Assert.Equal(turns, result.TurnsB);
        Assert.Equal(turns, result.EndDifferenceTurn);
        Assert.Equal(0, result.ShorterSide);
        Assert.Equal(turns, result.FirstActionDifferenceTurn);
        AssertHashesAt(db, a.Record, b.Record, result, turns);
    }

    [Fact]
    public void ExtraNegotiationInAnEarlierGroupIsNotMisreportedAsAnEarlyEnd()
    {
        var db = Content();
        var a = Passes(db, 1);
        var b = Passes(db, 1);
        b.OfferDraw(0);
        b.RespondDraw(1, false);
        a.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        var result = ReplayDiff.Compare(db, a.Record, b.Record);

        Assert.Equal(1, result.FirstActionDifferenceTurn);
        AssertHashesAt(db, a.Record, b.Record, result, 1);
        Assert.Equal(2, result.TurnsA);
        Assert.Equal(2, result.TurnsB);
        Assert.Null(result.EndDifferenceTurn);
        Assert.Null(result.ShorterSide);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BanAndLeadChoicesAreActionsAtPositionZeroRatherThanDifferentHeaders(bool changeLead)
    {
        var db = Content();
        var a = Make(db, ban: true, lead: true);
        var b = Make(db, ban: true, lead: true);
        a.ChooseBan(0, 5);
        b.ChooseBan(0, changeLead ? 5 : 4);
        a.ChooseBan(1, 5);
        b.ChooseBan(1, 5);
        a.ChooseLead(0, 1);
        b.ChooseLead(0, changeLead ? 2 : 1);
        a.ChooseLead(1, 1);
        b.ChooseLead(1, 1);
        a.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        var result = ReplayDiff.Compare(db, a.Record, b.Record);

        Assert.True(result.SameOpening);
        Assert.Equal(0, result.FirstActionDifferenceTurn);
        AssertHashesAt(db, a.Record, b.Record, result, 0);
        Assert.Equal(1, result.TurnsA);
        Assert.Equal(1, result.TurnsB);
    }

    [Fact]
    public void ReplacementDifferenceBelongsToTheCompletedTurnEvenThoughBattleTurnAlreadyAdvanced()
    {
        var db = Content();
        var a = Make(db);
        var b = Make(db);
        a.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());
        b.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());
        Assert.Equal(2, a.Turn);
        Assert.Equal(Phase.Replace, a.Phase);
        a.Replace(1, 1);
        b.Replace(1, 2);

        var result = ReplayDiff.Compare(db, a.Record, b.Record);

        Assert.Equal(1, result.FirstActionDifferenceTurn);
        AssertHashesAt(db, a.Record, b.Record, result, 1);
        Assert.Null(result.EndDifferenceTurn);
        Assert.Equal(1, result.TurnsA);
        Assert.Equal(1, result.TurnsB);
    }

    [Fact]
    public void DiagnosticsIncludeExactHpStagesStatusAndFieldValuesAtTheFirstDifference()
    {
        var db = Content();
        var a = Make(db);
        var b = Make(db);
        a.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());

        var result = ReplayDiff.Compare(db, a.Record, b.Record);

        AssertHashesAt(db, a.Record, b.Record, result, 1);
        Assert.Contains(result.Differences, line => line.Contains("[u06]") && line.Contains("人气 HP") && line.Contains("993") && line.Contains("1000"));
        Assert.Contains(result.Differences, line => line.Contains("[u06]") && line.Contains("能力 atk") && line.Contains("录像1=2") && line.Contains("录像2=0"));
        Assert.Contains(result.Differences, line => line.Contains("[u06]") && line.Contains("能力 def") && line.Contains("录像1=-1") && line.Contains("录像2=0"));
        Assert.Contains(result.Differences, line => line.Contains("[u06]") && line.Contains("状态[0].id") && line.Contains("diff_weak") && line.Contains("不存在"));
        Assert.Contains(result.Differences, line => line.Contains("[u06]") && line.Contains("状态[0].剩余") && line.Contains("录像1=6"));
        Assert.Contains(result.Differences, line => line.Contains("[u06]") && line.Contains("直播间效果[0].id") && line.Contains("diff_aura"));
        Assert.Contains(result.Differences, line => line.Contains("[u06]") && line.Contains("直播间效果[0].回合") && line.Contains("录像1=6"));
    }

    [Fact]
    public void DiagnosticsIncludeTheSixthSlotAndTheNowBenchedFirstSlot()
    {
        var db = Content();
        var a = Make(db);
        var b = Make(db);
        a.ResolveTurn(BattleAction.SwitchTo(5), BattleAction.Skill(0));
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Skill(0));
        Assert.Equal(993, a.Sides[0].Team[5].Hp);
        Assert.Equal(993, b.Sides[0].Team[0].Hp);

        var result = ReplayDiff.Compare(db, a.Record, b.Record);

        Assert.Contains(result.Differences, line => line.Contains("[u00]") && line.Contains("人气 HP") && line.Contains("录像1=1000") && line.Contains("录像2=993"));
        Assert.Contains(result.Differences, line => line.Contains("[u05]") && line.Contains("人气 HP") && line.Contains("录像1=993") && line.Contains("录像2=1000"));
    }

    [Fact]
    public void DiagnosticsRetainFaintedBenchMembersStatusAndFieldDifferencesAfterReplacement()
    {
        var db = Content();
        db.Skills["enrich"].Effects.Last().N = 99999;
        var a = Make(db);
        var b = Make(db);
        a.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        b.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());
        a.Replace(1, 1);
        b.Replace(1, 1);
        Assert.True(a.Sides[1].Team[0].Fainted);
        Assert.Equal(1, a.Sides[1].Active);

        var result = ReplayDiff.Compare(db, a.Record, b.Record);

        Assert.Contains(result.Differences, line => line.Contains("[u06]") && line.Contains("能力 atk"));
        Assert.Contains(result.Differences, line => line.Contains("[u06]") && line.Contains("状态[0].id") && line.Contains("diff_weak"));
        Assert.Contains(result.Differences, line => line.Contains("[u06]") && line.Contains("直播间效果[0].id") && line.Contains("diff_aura"));
    }

    [Theory]
    [InlineData("future")]
    [InlineData("missing-pack")]
    [InlineData("missing-unit")]
    [InlineData("null-event")]
    [InlineData("unknown-event")]
    [InlineData("bad-modifier")]
    public void UnsupportedOrMalformedRecordsUseTheExistingCompatibilityReason(string defect)
    {
        var db = Content();
        var a = Copy(Passes(db, 1).Record);
        var b = Copy(a);
        switch (defect)
        {
            case "future": b.Version = BattleRecord.CurrentVersion + 1; break;
            case "missing-pack": a.PackId = b.PackId = "missing_pack"; break;
            case "missing-unit": a.Teams[0][0] = b.Teams[0][0] = "missing_unit"; break;
            case "null-event": b.Events.Add(null); break;
            case "unknown-event": b.Events.Add(new BattleRecordEvent { Type = (BattleRecordEventType)999 }); break;
            case "bad-modifier": a.Modifiers = new List<string> { "unknown" }; b.Modifiers = new List<string> { "unknown" }; break;
        }
        string beforeA = Json(a), beforeB = Json(b), beforeDb = Json(db);
        var compatibility = Replay.Check(db, b);
        Assert.False(compatibility.CanReplay);

        var error = Record.Exception(() => ReplayDiff.Compare(db, a, b));

        Assert.NotNull(error);
        Assert.Contains(compatibility.Reason, error.Message);
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
        Assert.Equal(beforeA, Json(a));
        Assert.Equal(beforeB, Json(b));
        Assert.Equal(beforeDb, Json(db));
    }

    [Fact]
    public void FutureVersionIsRejectedEvenWhenTheSeedAlreadyDiffersAndMissingContentCannotBeSilentlyCompared()
    {
        var db = Content();
        var a = Copy(Make(db).Record);
        var b = Copy(a);
        b.Seed++;
        b.Version = BattleRecord.CurrentVersion + 1;
        var future = Record.Exception(() => ReplayDiff.Compare(db, a, b));
        Assert.NotNull(future);
        Assert.Contains("录像来自更新的版本", future.Message);

        var missing = Record.Exception(() => ReplayDiff.Compare(null, a, Copy(a)));
        Assert.NotNull(missing);
        Assert.Matches("[\u4e00-\u9fff]", missing.Message);
    }

    [Fact]
    public void InvalidEventPhaseIsNotHiddenByHeaderCompatibility()
    {
        var db = Content();
        var a = Copy(Make(db).Record);
        var b = Copy(a);
        b.Events.Add(new BattleRecordEvent { Type = BattleRecordEventType.Replace, Side = 0, To = 1 });
        Assert.True(Replay.Check(db, b).CanReplay);
        string before = Json(b);

        var error = Record.Exception(() => ReplayDiff.Compare(db, a, b));

        Assert.NotNull(error);
        Assert.Contains("补位", error.Message);
        Assert.Equal(before, Json(b));
    }

    [Fact]
    public void RepeatedComparisonLeavesRecordsContentAndTheirReusableCollectionsUnchanged()
    {
        var db = Content();
        var a = Passes(db, 2);
        var b = Passes(db, 2);
        a.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        var recordA = Copy(a.Record);
        var recordB = Copy(b.Record);
        var teams = recordA.Teams;
        var events = recordA.Events;
        var firstEvent = recordA.Events[0];
        string aBefore = Json(recordA), bBefore = Json(recordB), dbBefore = Json(db);
        string contentHash = db.ContentHash();
        ulong liveA = a.StateHash(), liveB = b.StateHash();
        var first = ReplayDiff.Compare(db, recordA, recordB);

        for (int i = 0; i < 3; i++) Assert.Equal(Json(first), Json(ReplayDiff.Compare(db, recordA, recordB)));

        Assert.Equal(aBefore, Json(recordA));
        Assert.Equal(bBefore, Json(recordB));
        Assert.Equal(dbBefore, Json(db));
        Assert.Equal(contentHash, db.ContentHash());
        Assert.Equal(liveA, a.StateHash());
        Assert.Equal(liveB, b.StateHash());
        Assert.Same(teams, recordA.Teams);
        Assert.Same(events, recordA.Events);
        Assert.Same(firstEvent, recordA.Events[0]);
        foreach (var list in new[] { first.OpeningDifferences, first.Differences, first.Warnings })
            if (list is IList<string> writable) Assert.Throws<NotSupportedException>(() => writable.Add("外部修改"));
    }
}
