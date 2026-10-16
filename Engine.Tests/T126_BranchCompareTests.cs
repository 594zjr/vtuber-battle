using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

[Collection("Localization current")]
public class T126_BranchCompareTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    static ContentDb Content(bool variedHp = false)
    {
        // 固定伤害隔离接手随机流变化；胜负和人气差只由本测试选择的真实行动决定。
        var db = new ContentDb { PackId = "t126_fixture", PackName = "结局对照验收", PackNotice = "独立测试数据" };
        db.Types["talk"] = new TypeDef { Id = "talk", Name = "聊天", Color = "#808080" };
        db.Chart["talk"] = new Dictionary<string, double> { { "talk", 1 } };
        db.Personas["neutral"] = new PersonaDef { Id = "neutral", Name = "测试人设", Code = "T_LOWHP_POWER" };
        foreach (var entry in new[] { (Id: "chip", Amount: 20), (Id: "large", Amount: 80), (Id: "finish", Amount: 99999) })
            db.Skills[entry.Id] = new SkillDef
            {
                Id = entry.Id, Name = "测试技能" + entry.Id, Type = "talk", Cat = "status", Pp = 40,
                Effects = new List<EffectDef> { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = entry.Amount } }
            };
        db.Skills["wait"] = new SkillDef { Id = "wait", Name = "测试等待", Type = "talk", Cat = "status", Pp = 40 };
        db.Struggle = new SkillDef { Id = "struggle", Name = "测试硬撑", Type = "talk", Cat = "interact", Power = 20, Pp = 1 };
        for (int index = 0; index < 12; index++)
            db.Streamers.Add(new StreamerDef
            {
                Id = "u" + index.ToString("D2"), Name = "测试主播" + index, Type = "talk", Persona = "neutral",
                Stats = new Dictionary<string, int>
                {
                    { "hp", variedHp ? 100 * (index % 6 + 1) : 100 }, { "atk", 100 }, { "def", 100 },
                    { "spa", 100 }, { "sdef", 100 }, { "spd", 200 - index }
                },
                Skills = new List<string> { "chip", "large", "finish", "wait" }, Fifth = "finish"
            });
        db.Validate();
        return db;
    }

    static Battle Make(ContentDb db, bool opening = false, bool doubleHp = false)
    {
        return new Battle(db, new IList<string>[]
        {
            db.Streamers.Take(6).Select(unit => unit.Id).ToArray(), db.Streamers.Skip(6).Take(6).Select(unit => unit.Id).ToArray()
        }, new[] { "甲方原名", "乙方原名" }, new BattleOptions
        {
            Seed = 126, LeadPick = opening, BanOne = opening,
            Modifiers = doubleHp ? new List<string> { "hp_x2" } : null
        });
    }

    static void ReplaceNeeded(Battle battle)
    {
        if (battle.Phase == Phase.Over) return;
        for (int side = 0; side < 2; side++)
            if (battle.Need[side]) battle.Replace(side, battle.LegalFor(side).Switches.First());
    }

    static Battle Source()
    {
        var battle = Make(Content());
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        ReplaceNeeded(battle);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        battle.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        ReplaceNeeded(battle);
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        battle.Surrender(1);
        return battle;
    }

    static void ContinueChoices(Battle battle, BattleRecord source, int fromTurn)
    {
        int turns = 0;
        foreach (var entry in source.Events)
        {
            if (entry.Type == BattleRecordEventType.Turn) turns++;
            if (turns < fromTurn) continue;
            // 只重做选择，不恢复原 RNG；接手的新随机流必须由真实公开接口维护。
            switch (entry.Type)
            {
                case BattleRecordEventType.Turn: battle.ResolveTurn(entry.A0, entry.A1); break;
                case BattleRecordEventType.Replace: battle.Replace(entry.Side, entry.To); break;
                case BattleRecordEventType.Surrender:
                    battle.ResolveSurrenders(entry.Side == -1 || entry.Side == 0, entry.Side == -1 || entry.Side == 1); break;
                case BattleRecordEventType.DrawOffer: battle.OfferDraw(entry.Side); break;
                case BattleRecordEventType.DrawResponse: battle.RespondDraw(entry.Side, entry.Accepted); break;
                default: throw new InvalidOperationException("夹具后缀出现未覆盖的选择事件。");
            }
        }
        Assert.Equal(Phase.Over, battle.Phase);
    }

    static Battle SameChoices(Battle source, int turn = 2, int humanSide = 0)
    {
        var branch = ReplayBranch.Start(source.Db, source.Record, turn, humanSide, AiLevel.Normal);
        ContinueChoices(branch, source.Record, turn);
        return branch;
    }

    static BattleRecord Copy(BattleRecord record) { return BattleRecord.FromJson(record.ToJson()); }
    static string Json<T>(T value) { return JsonSerializer.Serialize(value, JsonOptions); }

    static Exception ChineseFailure(Action action)
    {
        var error = Record.Exception(action);
        Assert.NotNull(error);
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
        return error;
    }

    static void EqualMoments(Battle replay, int viewer, int turn, IEnumerable<BranchCompareMoment> actual)
    {
        var expected = BattleReview.KeyMoments(replay, viewer, int.MaxValue).Where(moment => moment.Turn >= turn);
        Assert.Equal(expected.Select(moment => (moment.Turn, moment.Kind, moment.Text, moment.Swing)),
            actual.Select(moment => (moment.Turn, moment.Kind, moment.Text, moment.Swing)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RepeatingDeterministicChoicesReportsTheSameOutcomeWithoutMutatingInputs(int humanSide)
    {
        var source = Source();
        var branch = SameChoices(source, humanSide: humanSide);
        string sourceJson = source.Record.ToJson(), branchJson = branch.Record.ToJson(), content = Json(source.Db);
        var sourceEvents = source.Record.Events.ToArray();
        var branchEvents = branch.Record.Events.ToArray();
        var sourceList = source.Record.Events;
        var branchList = branch.Record.Events;
        ulong sourceHash = source.StateHash(), branchHash = branch.StateHash();

        var result = BranchCompare.Compare(source.Db, source.Record, branch.Record);

        Assert.Equal("结果相同", result.Conclusion);
        Assert.Equal(2, result.Turn);
        Assert.Equal(humanSide, result.HumanSide);
        foreach (var outcome in new[] { result.Original, result.Branch })
        {
            Assert.Equal(0, outcome.Winner);
            Assert.Equal(4, outcome.Turns);
            Assert.Equal(5, outcome.EndTurn);
            Assert.Equal(2, outcome.Sides.Count);
            Assert.Equal(new[] { 0, 1 }, outcome.Sides.Select(side => side.Side));
            Assert.Equal(new[] { "甲方原名", "乙方原名" }, outcome.Sides.Select(side => side.Name));
            Assert.Equal(new[] { 6, 4 }, outcome.Sides.Select(side => side.Remaining));
            Assert.Equal(new long[] { 580, 400 }, outcome.Sides.Select(side => side.Hp));
            Assert.All(outcome.Sides, side => Assert.Equal(600L, side.MaxHp));
            Assert.Equal(58000.0 / 600, outcome.Sides[0].HpPercent, 10);
            Assert.Equal(40000.0 / 600, outcome.Sides[1].HpPercent, 10);
        }
        Assert.Equal(sourceHash, result.Original.StateHash);
        Assert.Equal(branchHash, result.Branch.StateHash);
        Assert.NotEqual(sourceHash, branchHash);
        EqualMoments(Replay.Run(source.Db, source.Record), humanSide, 2, result.Original.KeyMoments);
        EqualMoments(Replay.Run(source.Db, branch.Record), humanSide, 2, result.Branch.KeyMoments);
        Assert.NotEmpty(result.Original.KeyMoments);
        Assert.All(result.Original.KeyMoments.Concat(result.Branch.KeyMoments), moment => Assert.True(moment.Turn >= 2));
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<BranchCompareSide>>(result.Original.Sides).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<BranchCompareMoment>>(result.Branch.KeyMoments).Clear());
        string first = Json(result);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(first, Json(BranchCompare.Compare(source.Db, source.Record, branch.Record)));
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Assert.Equal(sourceJson, source.Record.ToJson());
        Assert.Equal(branchJson, branch.Record.ToJson());
        Assert.Equal(content, Json(source.Db));
        Assert.Equal(sourceHash, source.StateHash());
        Assert.Equal(branchHash, branch.StateHash());
        Assert.Same(sourceList, source.Record.Events);
        Assert.Same(branchList, branch.Record.Events);
        for (int i = 0; i < sourceEvents.Length; i++) Assert.Same(sourceEvents[i], source.Record.Events[i]);
        for (int i = 0; i < branchEvents.Length; i++) Assert.Same(branchEvents[i], branch.Record.Events[i]);
    }

    [Theory]
    [InlineData(0, "原录像获胜，接手后落败")]
    [InlineData(1, "原录像落败，接手后获胜")]
    public void ChangedChoicesReportTheActualWinnerFromTheTakingOverPlayersPerspective(int humanSide, string conclusion)
    {
        var source = Source();
        var branch = ReplayBranch.Start(source.Db, source.Record, 2, humanSide, AiLevel.Normal);
        branch.Surrender(0);
        var result = BranchCompare.Compare(source.Db, source.Record, branch.Record);
        Assert.Equal(0, result.Original.Winner);
        Assert.Equal(1, result.Branch.Winner);
        Assert.Equal(conclusion, result.Conclusion);
        Assert.Equal(1, result.Branch.Turns);
        Assert.Equal(2, result.Branch.EndTurn);
    }

    [Theory]
    [InlineData(0, "接手后提前 3 回合获胜")]
    [InlineData(5, "接手后延后 2 回合获胜")]
    public void WinningSoonerOrLaterUsesActualActionCountRatherThanTheEndNodeTurn(int extraTurns, string conclusion)
    {
        var source = Source();
        var branch = ReplayBranch.Start(source.Db, source.Record, 2, 0, AiLevel.Normal);
        for (int i = 0; i < extraTurns; i++) branch.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        branch.Surrender(1);
        var result = BranchCompare.Compare(source.Db, source.Record, branch.Record);
        Assert.Equal(conclusion, result.Conclusion);
        Assert.Equal(1 + extraTurns, result.Branch.Turns);
        Assert.Equal(2 + extraTurns, result.Branch.EndTurn);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TotalHpPercentageUsesActualParticipatingTeamAndIncludesFaintedMembers(bool doubleHp)
    {
        var db = Content(variedHp: true);
        var source = Make(db, opening: true, doubleHp: doubleHp);
        source.ChooseBan(0, 5);
        source.ChooseBan(1, 5);
        source.ChooseLead(0, 4);
        source.ChooseLead(1, 4);
        source.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
        ReplaceNeeded(source);
        source.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        source.Surrender(1);
        var branch = SameChoices(source);
        var result = BranchCompare.Compare(db, source.Record, branch.Record);
        long maximum = doubleHp ? 3000 : 1500;
        long remaining = maximum - (doubleHp ? 1000 : 500) - 20;
        foreach (var outcome in new[] { result.Original, result.Branch })
        {
            Assert.Equal(5, outcome.Sides[0].Remaining);
            Assert.Equal(4, outcome.Sides[1].Remaining);
            Assert.Equal(maximum, outcome.Sides[0].Hp);
            Assert.Equal(maximum, outcome.Sides[0].MaxHp);
            Assert.Equal(100.0, outcome.Sides[0].HpPercent);
            Assert.Equal(maximum, outcome.Sides[1].MaxHp);
            Assert.Equal(remaining, outcome.Sides[1].Hp);
            Assert.Equal(100.0 * remaining / maximum, outcome.Sides[1].HpPercent, 10);
        }
    }

    [Fact]
    public void ReviewRetainsAllPostBranchMomentsWithoutTheDefaultFiveItemLimit()
    {
        var source = Make(Content());
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        while (source.Phase != Phase.Over)
        {
            source.ResolveTurn(BattleAction.Skill(2), BattleAction.Pass());
            ReplaceNeeded(source);
        }
        var branch = SameChoices(source);
        var result = BranchCompare.Compare(source.Db, source.Record, branch.Record);
        Assert.True(result.Original.KeyMoments.Count > 5);
        Assert.True(result.Branch.KeyMoments.Count > 5);
        EqualMoments(source, 0, 2, result.Original.KeyMoments);
        EqualMoments(branch, 0, 2, result.Branch.KeyMoments);
        Assert.All(result.Original.KeyMoments.Concat(result.Branch.KeyMoments), moment => Assert.True(moment.Turn >= 2));
        Assert.Equal(0, result.Original.Sides[1].Remaining);
        Assert.Equal(0L, result.Original.Sides[1].Hp);
        Assert.Equal(0.0, result.Original.Sides[1].HpPercent);
        Assert.Equal(0, result.Original.Winner);
        Assert.Equal(0, result.Branch.Winner);
        Assert.Equal(0, result.Branch.Sides[1].Remaining);
        Assert.Equal(0L, result.Branch.Sides[1].Hp);
    }

    [Fact]
    public void SameOutcomeDoesNotMeanIdenticalHpOrState()
    {
        var source = Source();
        var branch = ReplayBranch.Start(source.Db, source.Record, 2, 0, AiLevel.Normal);
        branch.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        ContinueChoices(branch, source.Record, 3);
        var result = BranchCompare.Compare(source.Db, source.Record, branch.Record);
        Assert.Equal("结果相同", result.Conclusion);
        Assert.Equal(result.Original.Turns, result.Branch.Turns);
        Assert.Equal(580L, result.Original.Sides[0].Hp);
        Assert.Equal(600L, result.Branch.Sides[0].Hp);
        Assert.NotEqual(result.Original.StateHash, result.Branch.StateHash);
    }

    [Theory]
    [InlineData(false, "原录像获胜，接手后平局")]
    [InlineData(true, "结果相同")]
    public void DrawsRemainDrawsRatherThanBeingClassifiedAsLosses(bool sourceDraw, string conclusion)
    {
        var source = Make(Content());
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        if (sourceDraw) source.ResolveSurrenders(true, true);
        else source.Surrender(1);
        var branch = ReplayBranch.Start(source.Db, source.Record, 1, 0, AiLevel.Normal);
        branch.ResolveSurrenders(true, true);
        var result = BranchCompare.Compare(source.Db, source.Record, branch.Record);
        Assert.Equal(-1, result.Branch.Winner);
        Assert.Equal(sourceDraw ? -1 : 0, result.Original.Winner);
        Assert.Equal(conclusion, result.Conclusion);
        Assert.Equal(0, result.Branch.Turns);
        Assert.Equal(1, result.Branch.EndTurn);
        Assert.All(result.Original.Sides.Concat(result.Branch.Sides), side =>
        {
            Assert.Equal(6, side.Remaining);
            Assert.Equal(600L, side.Hp);
            Assert.Equal(100.0, side.HpPercent);
        });
    }

    [Theory]
    [InlineData("other-source")]
    [InlineData("header")]
    [InlineData("prefix-action")]
    [InlineData("prefix-rng")]
    [InlineData("boundary-rng")]
    public void ValidReplayShapesCannotDisguiseWrongSourceHeadersOrPrefixes(string defect)
    {
        var source = Source();
        var branch = SameChoices(source);
        var original = Copy(source.Record);
        var changed = Copy(branch.Record);
        if (defect == "other-source") original.Names[0] += "另一个来源";
        else if (defect == "header") changed.Names[1] += "篡改";
        else if (defect == "prefix-action")
            changed.Events.First(entry => entry.Type == BattleRecordEventType.Turn).A1 = new BattleAction { Type = ActionType.Pass, Index = 99 };
        else if (defect == "prefix-rng") changed.Events.First(entry => entry.Type == BattleRecordEventType.Turn).RngState = null;
        else changed.Events.Single(entry => entry.Type == BattleRecordEventType.Branch).RngState ^= 1u;
        Assert.Equal(Phase.Over, Replay.Run(source.Db, original).Phase);
        Assert.Equal(Phase.Over, Replay.Run(source.Db, changed).Phase);
        string originalJson = original.ToJson(), changedJson = changed.ToJson(), content = Json(source.Db);
        ChineseFailure(() => BranchCompare.Compare(source.Db, original, changed));
        Assert.Equal(originalJson, original.ToJson());
        Assert.Equal(changedJson, changed.ToJson());
        Assert.Equal(content, Json(source.Db));
    }

    [Fact]
    public void NestedBranchComparesAgainstItsImmediateSourceAndPreservesEarlierBranchEvents()
    {
        var source = Source();
        var parent = SameChoices(source, turn: 2, humanSide: 0);
        var child = SameChoices(parent, turn: 3, humanSide: 1);
        Assert.Equal(2, child.Record.Events.Count(entry => entry.Type == BattleRecordEventType.Branch));
        var result = BranchCompare.Compare(source.Db, parent.Record, child.Record);
        Assert.Equal(3, result.Turn);
        Assert.Equal(1, result.HumanSide);
        Assert.Equal("结果相同", result.Conclusion);
        EqualMoments(parent, 1, 3, result.Original.KeyMoments);
        EqualMoments(child, 1, 3, result.Branch.KeyMoments);
        ChineseFailure(() => BranchCompare.Compare(source.Db, source.Record, child.Record));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyOptionalRngAndHeaderOmissionsRemainValidWithoutCanonicalizingInput(bool legacyHeaders)
    {
        var source = Source();
        var original = Copy(source.Record);
        foreach (var entry in original.Events) entry.RngState = null;
        if (legacyHeaders) { original.Names = null; original.ContentHash = null; }
        var branch = ReplayBranch.Start(source.Db, original, 2, 0, AiLevel.Normal);
        ContinueChoices(branch, original, 2);
        string originalJson = original.ToJson(), branchJson = branch.Record.ToJson();
        var result = BranchCompare.Compare(source.Db, original, branch.Record);
        Assert.Equal("结果相同", result.Conclusion);
        Assert.Equal(0, result.Original.Winner);
        Assert.Equal(0, result.Branch.Winner);
        Assert.Equal(originalJson, original.ToJson());
        Assert.Equal(branchJson, branch.Record.ToJson());
    }

    [Theory]
    [InlineData("source-unfinished")]
    [InlineData("branch-unfinished")]
    [InlineData("source-future")]
    [InlineData("branch-future")]
    [InlineData("source-fingerprint")]
    [InlineData("branch-fingerprint")]
    [InlineData("missing-pack")]
    [InlineData("missing-unit")]
    [InlineData("no-branch")]
    public void InvalidOrIncompleteInputsAreRejectedInChineseWithoutMutation(string defect)
    {
        var source = Source();
        var original = Copy(source.Record);
        BattleRecord branch;
        if (defect == "source-unfinished")
        {
            original.Events.RemoveAt(original.Events.Count - 1);
            var game = ReplayBranch.Start(source.Db, original, 2, 0, AiLevel.Normal);
            game.Surrender(1);
            branch = game.Record;
        }
        else branch = Copy(SameChoices(source).Record);
        switch (defect)
        {
            case "branch-unfinished": branch.Events.RemoveAt(branch.Events.Count - 1); break;
            case "source-future": original.Version = BattleRecord.CurrentVersion + 1; break;
            case "branch-future": branch.Version = BattleRecord.CurrentVersion + 1; break;
            case "source-fingerprint": original.ContentHash = new string('0', 64); break;
            case "branch-fingerprint": branch.ContentHash = new string('0', 64); break;
            case "missing-pack": original.PackId = "missing_pack"; break;
            case "missing-unit": original.Teams[0][5] = "missing_unit"; break;
            case "no-branch": branch = Copy(source.Record); break;
        }
        string originalJson = original.ToJson(), branchJson = branch.ToJson(), content = Json(source.Db);
        var error = ChineseFailure(() => BranchCompare.Compare(source.Db, original, branch));
        if (defect == "source-future" || defect == "branch-future")
            Assert.Contains("更新的版本", error.Message);
        Assert.Equal(originalJson, original.ToJson());
        Assert.Equal(branchJson, branch.ToJson());
        Assert.Equal(content, Json(source.Db));
    }

    [Theory]
    [InlineData("db")]
    [InlineData("source")]
    [InlineData("branch")]
    public void NullInputsAreRejectedInChinese(string missing)
    {
        var source = Source();
        var branch = SameChoices(source);
        Assert.IsType<ArgumentNullException>(ChineseFailure(() => BranchCompare.Compare(
            missing == "db" ? null : source.Db, missing == "source" ? null : source.Record,
            missing == "branch" ? null : branch.Record)));
    }

    [Fact]
    public void RemovedContentCannotBeComparedEvenWhenTheRecordsOtherwiseMatch()
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db);
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        source.Surrender(1);
        var branch = ReplayBranch.Start(db, source.Record, 1, 0, AiLevel.Normal);
        branch.Surrender(1);
        string removed = source.Record.Teams[0][5];
        var policy = PackPolicy.FromJson(Json(new
        {
            disabledPacks = Array.Empty<string>(), disabledStreamers = new[] { removed },
            reason = new Dictionary<string, string> { { removed, "测试申请撤下" } }
        }));
        string directory = Path.Combine(AppContext.BaseDirectory, "Content");
        var filtered = ContentDb.FromJson(File.ReadAllText(Path.Combine(directory, "StreamDuelRules.json")),
            File.ReadAllText(Path.Combine(directory, "Packs", "original.json")), policy);
        string originalJson = source.Record.ToJson(), branchJson = branch.Record.ToJson();

        var error = ChineseFailure(() => BranchCompare.Compare(filtered, source.Record, branch.Record));

        Assert.Contains("已下架", error.Message);
        Assert.Equal(originalJson, source.Record.ToJson());
        Assert.Equal(branchJson, branch.Record.ToJson());
    }
}
