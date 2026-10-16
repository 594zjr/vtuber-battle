using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T116_ReplayBranchTests
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true };

    sealed class TestDirectory : IDisposable
    {
        readonly string parent, name = Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string sourceFile = "")
        {
            parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile), "obj", "T116", "temp"));
            Root = Path.Combine(parent, name);
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(resolved) != parent || Path.GetFileName(resolved) != name
                || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("只能清理当前测试创建的普通目录。");
            Directory.Delete(resolved, true);
        }
    }

    static ContentDb Content()
    {
        // 固定内容隔离正式包的数值调整；全部状态由真实行动产生，不直接注入战斗快照。
        var db = new ContentDb { PackId = "t116_fixture", PackName = "分支验收", PackNotice = "独立测试数据" };
        db.Types["talk"] = new TypeDef { Id = "talk", Name = "聊天", Color = "#808080" };
        db.Chart["talk"] = new Dictionary<string, double> { { "talk", 1 } };
        db.Personas["neutral"] = new PersonaDef { Id = "neutral", Name = "测试人设", Code = "T_LOWHP_POWER" };
        db.Statuses["branch_weak"] = new StatusDef { Id = "branch_weak", Name = "测试弱化", Cat = "weak", Turns = 7, Cleanse = true };
        db.Fields["branch_room"] = new FieldDef { Id = "branch_room", Name = "测试直播间", Kind = "turn", Dispel = true, DmgTakenMul = 0.9 };
        db.Skills["enrich"] = new SkillDef
        {
            Id = "enrich", Name = "测试状态", Type = "talk", Cat = "status", Pp = 20,
            Effects = new List<EffectDef>
            {
                new EffectDef { At = "post", Code = "STAGE", Target = "foe", Changes = new Dictionary<string, int> { { "atk", -1 }, { "def", 1 } } },
                new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "branch_weak" },
                new EffectDef { At = "post", Code = "FIELD", Target = "foe", Field = "branch_room", Turns = 7 },
                new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 7 }
            }
        };
        db.Skills["attack"] = new SkillDef { Id = "attack", Name = "测试攻击", Type = "talk", Cat = "interact", Power = 55, Pp = 20 };
        db.Skills["heal"] = new SkillDef
        {
            Id = "heal", Name = "测试治疗", Type = "talk", Cat = "status", Pp = 20,
            Effects = new List<EffectDef> { new EffectDef { At = "post", Code = "HEAL", Target = "self", Frac = 0.1 } }
        };
        db.Skills["wait"] = new SkillDef { Id = "wait", Name = "测试等待", Type = "talk", Cat = "status", Pp = 20 };
        db.Skills["finish"] = new SkillDef
        {
            Id = "finish", Name = "测试下播", Type = "talk", Cat = "status", Pp = 20,
            Effects = new List<EffectDef> { new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 99999 } }
        };
        db.Struggle = new SkillDef { Id = "struggle", Name = "测试硬撑", Type = "talk", Cat = "interact", Power = 20, Pp = 1 };
        for (int index = 0; index < 12; index++)
            db.Streamers.Add(new StreamerDef
            {
                Id = "u" + index.ToString("D2"), Name = "测试主播" + index, Type = "talk", Persona = "neutral",
                Stats = new Dictionary<string, int>
                {
                    { "hp", 300 }, { "atk", 100 }, { "def", 100 }, { "spa", 100 }, { "sdef", 100 }, { "spd", 200 - index }
                },
                Skills = new List<string> { "enrich", "attack", "heal", "wait" }, Fifth = "finish"
            });
        db.Validate();
        return db;
    }

    static Battle Make(ContentDb db, bool openingChoices = false)
    {
        return new Battle(db, new IList<string>[]
        {
            db.Streamers.Take(6).Select(unit => unit.Id).ToArray(), db.Streamers.Skip(6).Take(6).Select(unit => unit.Id).ToArray()
        }, new[] { "原始甲\n\"名字\"", "原始乙🛰" }, new BattleOptions { Seed = 116, BanOne = openingChoices, LeadPick = openingChoices });
    }

    static Battle Recording()
    {
        var battle = Make(Content(), true);
        battle.ChooseBan(1, 5);
        battle.ChooseBan(0, 4);
        battle.ChooseLead(1, 3);
        battle.ChooseLead(0, 2);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        battle.OfferDraw(0);
        battle.RespondDraw(1, false);
        battle.ResolveTurn(BattleAction.Skill(4), BattleAction.Pass());
        Assert.Equal(Phase.Replace, battle.Phase);
        battle.Replace(1, 1);
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Skill(0));
        battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        battle.Surrender(1);
        Assert.Equal(4, battle.Record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn));
        return battle;
    }

    static string Json<T>(T value) { return JsonSerializer.Serialize(value, JsonOptions); }
    static BattleRecord Copy(BattleRecord record) { return BattleRecord.FromJson(record.ToJson()); }

    static Battle BeforeTurn(ContentDb db, BattleRecord source, int turn)
    {
        var prefix = Copy(source);
        int seen = 0, count = 0;
        foreach (var entry in prefix.Events)
        {
            if (entry.Type == BattleRecordEventType.Turn && ++seen == turn) break;
            count++;
        }
        prefix.Events.RemoveRange(count, prefix.Events.Count - count);
        return Replay.Run(db, prefix);
    }

    static void SamePhysicalState(Battle expected, Battle actual)
    {
        Assert.Equal(expected.Turn, actual.Turn);
        Assert.Equal(expected.Phase, actual.Phase);
        Assert.Equal(expected.Winner, actual.Winner);
        Assert.Equal(Json(expected.Sides), Json(actual.Sides));
        Assert.Equal(expected.Need, actual.Need);
        Assert.Equal(expected.ActedFirst, actual.ActedFirst);
        Assert.Equal(expected.Modifiers, actual.Modifiers);
        for (int side = 0; side < 2; side++)
        {
            Assert.Equal(expected.HasDrawOffer(side), actual.HasDrawOffer(side));
            Assert.Equal(expected.CanOfferDraw(side), actual.CanOfferDraw(side));
        }
    }

    static void SameReplay(Battle expected, Battle actual)
    {
        SamePhysicalState(expected, actual);
        Assert.Equal(expected.StateHash(), actual.StateHash());
        Assert.Equal(Json(expected.Log), Json(actual.Log));
        Assert.Equal(Json(expected.Events), Json(actual.Events));
        Assert.Equal(BattleSummary.From(expected).ToText(), BattleSummary.From(actual).ToText());
        Assert.Equal(expected.IsBranch, actual.IsBranch);
    }

    static string SourceHash(BattleRecord source)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(source.ToJson()))).Replace("-", "").ToLowerInvariant();
    }

    static string GoldenDirectory([CallerFilePath] string sourceFile = "")
    {
        return Path.Combine(Path.GetDirectoryName(sourceFile), "Golden");
    }

    static void AssertSource(BattleRecord source, Battle branch, int turn, int humanSide, AiLevel level)
    {
        Assert.True(branch.IsBranch);
        Assert.True(branch.Record.IsBranch);
        var info = branch.Record.Branch;
        Assert.NotNull(info);
        Assert.Equal(SourceHash(source), info.SourceHash);
        Assert.Equal(turn, info.Turn);
        Assert.Equal(humanSide, info.HumanSide);
        Assert.Equal(level, info.Level);
        Assert.Equal(ReplayBranch.DeriveSeed(source.Seed, turn), info.Seed);
    }

    static void ContinueAgainstAi(Battle battle, int humanSide, AiLevel level)
    {
        int guard = 0;
        while (battle.Phase != Phase.Over && guard++ < 300)
        {
            if (battle.Phase == Phase.Choose)
            {
                var human = BattleAction.Skill(4);
                var computer = Ai.Choose(battle, 1 - humanSide, level);
                battle.ResolveTurn(humanSide == 0 ? human : computer, humanSide == 1 ? human : computer);
            }
            else
                for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    if (battle.Need[side]) battle.Replace(side, side == humanSide
                        ? battle.LegalFor(side).Switches.First() : Ai.ChooseReplacement(battle, side, level));
        }
        Assert.Equal(Phase.Over, battle.Phase);
    }

    static Exception ChineseFailure(Action action)
    {
        var error = Record.Exception(action);
        Assert.NotNull(error);
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
        return error;
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    public void StartPreservesTheExactTurnOpeningIncludingChoicesStatusesFieldsAndReplacements(int turn, int humanSide)
    {
        var source = Recording();
        var original = Copy(source.Record);
        string before = original.ToJson(), content = Json(source.Db);
        var cursor = new ReplayCursor(source.Db, original, 2);
        cursor.Seek(turn - 1);
        string cursorBefore = Json(cursor.Current.Sides), cursorLog = Json(cursor.Current.Log);
        ulong cursorHash = cursor.Current.StateHash();
        var independent = BeforeTurn(source.Db, original, turn);
        SameReplay(independent, cursor.Current);

        var branch = ReplayBranch.Start(source.Db, original, turn, humanSide, AiLevel.Normal);

        AssertSource(original, branch, turn, humanSide, AiLevel.Normal);
        SamePhysicalState(independent, branch);
        Assert.Equal(Phase.Choose, branch.Phase);
        Assert.Equal(turn, branch.Turn);
        Assert.All(branch.Sides, side => Assert.Equal(5, side.Team.Count));
        if (turn == 2)
            foreach (var side in new[] { 0, 1 })
            {
                var active = branch.Active(side);
                Assert.Equal(293, active.Hp);
                Assert.Equal(-1, active.Stages["atk"]);
                Assert.Equal(6, Assert.Single(active.Statuses).Left);
                Assert.Equal(6, Assert.Single(active.Fields).Turns);
            }
        if (turn == 3)
        {
            Assert.True(branch.Sides[1].Team[0].Fainted);
            Assert.Equal(1, branch.Sides[1].Active);
            Assert.False(branch.Need[1]);
        }
        Assert.Equal(before, original.ToJson());
        Assert.Equal(content, Json(source.Db));
        Assert.Equal(cursorHash, cursor.Current.StateHash());
        Assert.Equal(cursorBefore, Json(cursor.Current.Sides));
        Assert.Equal(cursorLog, Json(cursor.Current.Log));
        Assert.NotSame(cursor.Current.Sides[0].Team[0], branch.Sides[0].Team[0]);
    }

    [Fact]
    public void ZeroNewActionsAlreadyHaveIndependentReplayAndTheDerivedRandomStream()
    {
        var source = Recording();
        var branch = ReplayBranch.Start(source.Db, source.Record, 2, 1, AiLevel.Hard);
        var roundTrip = Copy(branch.Record);

        AssertSource(source.Record, branch, 2, 1, AiLevel.Hard);
        Assert.Equal(1, roundTrip.Events.Count(entry => entry.Type == BattleRecordEventType.Turn));
        SameReplay(branch, Replay.Run(source.Db, roundTrip));
        Assert.Equal(branch.Record.ToJson(), roundTrip.ToJson());
        var cursor = new ReplayCursor(source.Db, roundTrip);
        cursor.ToEnd();
        SameReplay(branch, cursor.Current);
        var cloned = branch.Clone();
        var random = new Rng(branch.Record.Branch.Seed);
        for (int i = 0; i < 4; i++) Assert.Equal(random.Next(), cloned.Random());
        SameReplay(branch, Replay.Run(source.Db, roundTrip));
    }

    [Theory]
    [InlineData(0u, 1, 3551071350u)]
    [InlineData(116u, 2, 2038360061u)]
    [InlineData(9301u, 3, 2239942662u)]
    [InlineData(uint.MaxValue, int.MaxValue, 2919006377u)]
    public void DerivedSeedMatchesTheDocumentedUncheckedUInt32Vectors(uint seed, int turn, uint expected)
    {
        // 预期向量独立按文档的模 2^32 运算计算，不调用生产派生函数生成。
        Assert.Equal(expected, ReplayBranch.DeriveSeed(seed, turn));
    }

    [Fact]
    public void FrozenVersionFourBranchReplaysWithItsOriginalSourceSeedAndFinalHash()
    {
        string golden = GoldenDirectory();
        byte[] bytes = File.ReadAllBytes(Path.Combine(golden, "replay-branch-v4", "record.json"));
        using (var sha = SHA256.Create())
            Assert.Equal("9ab87f6cfb9fa77b5999dcd0491aa60b4c2dde29b3fc6836f8e514015122e534",
                BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant());
        string content = Path.Combine(golden, "replays", "content");
        var db = ContentDb.FromJson(File.ReadAllText(Path.Combine(content, "StreamDuelRules.json")),
            File.ReadAllText(Path.Combine(content, "original.json")), PackPolicy.Empty);
        var record = BattleRecord.FromJson(Encoding.UTF8.GetString(bytes));
        string before = record.ToJson();

        var compatibility = Replay.Check(db, record);
        Assert.True(compatibility.CanReplay, compatibility.Reason);
        var battle = Replay.Run(db, record);

        Assert.Equal(4, record.Version);
        Assert.True(record.IsBranch);
        Assert.True(battle.IsBranch);
        Assert.Equal(7, record.Events.Count);
        Assert.Equal("c1a6efb7d743286cb126e9d09f133938290a7bdff416973eb70b8dfa9db38990", record.Branch.SourceHash);
        Assert.Equal(3, record.Branch.Turn);
        Assert.Equal(1, record.Branch.HumanSide);
        Assert.Equal(AiLevel.Hard, record.Branch.Level);
        Assert.Equal(2239942662u, record.Branch.Seed);
        Assert.Equal(0x2032a691dd9d2805UL, battle.StateHash());
        Assert.Equal(0, battle.Winner);
        Assert.Equal(5, battle.Turn);
        Assert.Equal(Phase.Over, battle.Phase);
        var cursor = new ReplayCursor(db, record, 1);
        cursor.ToEnd();
        SameReplay(battle, cursor.Current);
        Assert.Equal(before, record.ToJson());
    }

    [Fact]
    public void LegacySourceWithoutNamesFingerprintOrRandomCheckpointsRemainsIndependentAfterTakeover()
    {
        var source = Recording();
        var legacy = Copy(source.Record);
        legacy.Events.RemoveAll(entry => entry.Type == BattleRecordEventType.DrawOffer
            || entry.Type == BattleRecordEventType.DrawResponse || entry.Type == BattleRecordEventType.Surrender);
        legacy.Version = 1;
        legacy.Names = null;
        legacy.ContentHash = null;
        foreach (var entry in legacy.Events) entry.RngState = null;
        string before = legacy.ToJson();

        var branch = ReplayBranch.Start(source.Db, legacy, 2, 0, AiLevel.Easy);

        AssertSource(legacy, branch, 2, 0, AiLevel.Easy);
        SamePhysicalState(BeforeTurn(source.Db, legacy, 2), branch);
        Assert.All(branch.Record.Events.Where(entry => entry.Type != BattleRecordEventType.Branch),
            entry => Assert.Null(entry.RngState));
        SameReplay(branch, Replay.Run(source.Db, Copy(branch.Record)));
        branch.Surrender(1);
        SameReplay(branch, Replay.Run(source.Db, Copy(branch.Record)));
        Assert.Equal(before, legacy.ToJson());
    }

    [Theory]
    [InlineData(AiLevel.Normal, 0)]
    [InlineData(AiLevel.Hard, 1)]
    [InlineData(AiLevel.Expert, 0)]
    public void IdenticalTakeoversAndHumanChoicesAgainstTheSameAiProduceIdenticalCompletedRecords(AiLevel level, int humanSide)
    {
        var source = Recording();
        string before = source.Record.ToJson();
        var first = ReplayBranch.Start(source.Db, source.Record, 2, humanSide, level);
        var second = ReplayBranch.Start(source.Db, source.Record, 2, humanSide, level);

        ContinueAgainstAi(first, humanSide, level);
        ContinueAgainstAi(second, humanSide, level);

        SameReplay(first, second);
        Assert.Equal(first.Record.ToJson(), second.Record.ToJson());
        SameReplay(first, Replay.Run(source.Db, Copy(first.Record)));
        Assert.Equal(before, source.Record.ToJson());
    }

    [Fact]
    public void SavingAndLoadingTheBranchPreservesItsSourceWithoutChangingTheOriginalFileOrObject()
    {
        using var dir = new TestDirectory();
        var source = Recording();
        string sourceJson = source.Record.ToJson(), contentBefore = Json(source.Db);
        ulong liveHash = source.StateHash();
        string sourcePath = Path.Combine(dir.Root, "source.json");
        File.WriteAllText(sourcePath, sourceJson, new UTF8Encoding(false));
        byte[] fileBefore = File.ReadAllBytes(sourcePath);
        var loadedSource = BattleRecord.FromJson(File.ReadAllText(sourcePath));
        var branch = ReplayBranch.Start(source.Db, loadedSource, 3, 0, AiLevel.Normal);
        ContinueAgainstAi(branch, 0, AiLevel.Normal);
        var library = new ReplayLibrary(Path.Combine(dir.Root, "library"), clock: () => new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero));

        string id = library.Save(branch.Record, BattleSummary.From(branch), "分支练习");
        var loaded = library.Load(id);

        Assert.Equal(branch.Record.ToJson(), loaded.ToJson());
        SameReplay(branch, Replay.Run(source.Db, loaded));
        AssertSource(source.Record, Replay.Run(source.Db, loaded), 3, 0, AiLevel.Normal);
        Assert.Equal(fileBefore, File.ReadAllBytes(sourcePath));
        Assert.Equal(sourceJson, source.Record.ToJson());
        Assert.Equal(sourceJson, loadedSource.ToJson());
        Assert.Equal(liveHash, source.StateHash());
        Assert.Equal(contentBefore, Json(source.Db));
        Assert.DoesNotContain(dir.Root, loaded.ToJson());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ABranchCanBeTakenOverAgainBeforeOrAfterItsEarlierBoundary(int nextTurn)
    {
        var source = Recording();
        var parent = ReplayBranch.Start(source.Db, source.Record, 2, 0, AiLevel.Normal);
        parent.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());
        parent.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        parent.Surrender(1);
        string parentBefore = parent.Record.ToJson();
        var expected = BeforeTurn(source.Db, parent.Record, nextTurn);

        var child = ReplayBranch.Start(source.Db, parent.Record, nextTurn, 1, AiLevel.Hard);

        AssertSource(parent.Record, child, nextTurn, 1, AiLevel.Hard);
        SamePhysicalState(expected, child);
        Assert.Equal(nextTurn == 1 ? 1 : 2, child.Record.Events.Count(entry => entry.Type == BattleRecordEventType.Branch));
        SameReplay(child, Replay.Run(source.Db, Copy(child.Record)));
        child.Surrender(0);
        SameReplay(child, Replay.Run(source.Db, Copy(child.Record)));
        Assert.Equal(parentBefore, parent.Record.ToJson());
    }

    [Fact]
    public void CloningPreservesBranchMetadataAndDoesNotShareMutableBattleOrRecordCollections()
    {
        var source = Recording();
        var branch = ReplayBranch.Start(source.Db, source.Record, 2, 0, AiLevel.Normal);
        var clone = branch.Clone();
        SameReplay(branch, clone);
        Assert.Equal(branch.Record.ToJson(), clone.Record.ToJson());
        AssertSource(source.Record, clone, 2, 0, AiLevel.Normal);
        string original = branch.Record.ToJson(), sides = Json(branch.Sides);
        ulong hash = branch.StateHash();

        clone.ResolveTurn(BattleAction.Skill(1), BattleAction.Pass());

        Assert.Equal(original, branch.Record.ToJson());
        Assert.Equal(sides, Json(branch.Sides));
        Assert.Equal(hash, branch.StateHash());
        Assert.NotEqual(branch.Record.Events.Count, clone.Record.Events.Count);
        SameReplay(clone, Replay.Run(source.Db, Copy(clone.Record)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void TurnsOutsideTheRecordedActionRangeAreRejectedWithChineseErrors(int turn)
    {
        var source = Recording();
        string before = source.Record.ToJson();
        ChineseFailure(() => ReplayBranch.Start(source.Db, source.Record, turn, 0, AiLevel.Normal));
        Assert.Equal(before, source.Record.ToJson());
    }

    [Fact]
    public void MissingInputsInvalidControlsAndEmptyRecordAreRejectedWithoutInventingABranch()
    {
        var source = Recording();
        ChineseFailure(() => ReplayBranch.Start(null, source.Record, 1, 0, AiLevel.Normal));
        ChineseFailure(() => ReplayBranch.Start(source.Db, null, 1, 0, AiLevel.Normal));
        foreach (int side in new[] { -1, 2 })
            ChineseFailure(() => ReplayBranch.Start(source.Db, source.Record, 1, side, AiLevel.Normal));
        ChineseFailure(() => ReplayBranch.Start(source.Db, source.Record, 1, 0, (AiLevel)99));
        ChineseFailure(() => ReplayBranch.DeriveSeed(116, 0));
        var empty = Make(source.Db).Record;
        string before = empty.ToJson();
        ChineseFailure(() => ReplayBranch.Start(source.Db, empty, 1, 0, AiLevel.Normal));
        Assert.False(empty.IsBranch);
        Assert.Equal(before, empty.ToJson());
    }

    [Theory]
    [InlineData("future")]
    [InlineData("fingerprint")]
    [InlineData("pack")]
    [InlineData("missing-unit")]
    public void CompatibilityFailuresCannotBeBypassedByTakingOverAnEarlyTurn(string defect)
    {
        var source = Recording();
        var record = Copy(source.Record);
        switch (defect)
        {
            case "future": record.Version = BattleRecord.CurrentVersion + 1; break;
            case "fingerprint": record.ContentHash = new string('0', 64); break;
            case "pack": record.PackId = "missing_pack"; break;
            case "missing-unit": record.Teams[0][5] = "missing_unit"; break;
        }
        string before = record.ToJson(), content = Json(source.Db);

        ChineseFailure(() => ReplayBranch.Start(source.Db, record, 1, 0, AiLevel.Normal));

        Assert.Equal(before, record.ToJson());
        Assert.Equal(content, Json(source.Db));
    }

    [Fact]
    public void ADisabledOriginalTeamMemberIsRejectedEvenIfItsSlotWouldHaveBeenBanned()
    {
        var db = BattleTests.LoadPack("original");
        var source = Make(db, true);
        source.ChooseBan(1, 5);
        source.ChooseBan(0, 5);
        source.ChooseLead(0, 0);
        source.ChooseLead(1, 0);
        source.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        string disabled = source.Record.Teams[0][5];
        var policy = PackPolicy.FromJson(JsonSerializer.Serialize(new
        {
            disabledPacks = Array.Empty<string>(), disabledStreamers = new[] { disabled },
            reason = new Dictionary<string, string> { { disabled, "测试申请撤下" } }
        }));
        string content = Path.Combine(AppContext.BaseDirectory, "Content");
        var filtered = ContentDb.FromJson(File.ReadAllText(Path.Combine(content, "StreamDuelRules.json")),
            File.ReadAllText(Path.Combine(content, "Packs", "original.json")), policy);
        string before = source.Record.ToJson();

        var error = ChineseFailure(() => ReplayBranch.Start(filtered, source.Record, 1, 0, AiLevel.Normal));

        Assert.Contains("已下架", error.Message);
        Assert.Equal(before, source.Record.ToJson());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void CompletedBranchesClonesAndLoadedReplaysNeverChangeFormalProfileCounts(int winner)
    {
        var source = Recording();
        var branch = ReplayBranch.Start(source.Db, source.Record, 2, 0, AiLevel.Normal);
        if (winner == -1) branch.ResolveTurn(BattleAction.Surrender(), BattleAction.Surrender());
        else branch.Surrender(1 - winner);
        var profile = new Profile();
        profile.Record(source, 0);
        string before = profile.ToJson();

        foreach (var candidate in new[] { branch, branch.Clone(), Replay.Run(source.Db, Copy(branch.Record)) })
        {
            Assert.True(candidate.IsBranch);
            profile.Record(candidate, 0);
            profile.Record(candidate, 1);
            Assert.Equal(before, profile.ToJson());
        }
        var empty = new Profile();
        empty.Record(branch, 0);
        Assert.Empty(empty.Packs);
        profile.Record(source, 0);
        Assert.Equal(2, profile.Packs[source.Db.PackId].Wins);
    }

    [Theory]
    [InlineData("version-three")]
    [InlineData("source-hash")]
    [InlineData("level")]
    [InlineData("missing-field")]
    [InlineData("derived-seed")]
    public void InvalidBranchProtocolMetadataIsRejectedWithoutChangingItsOriginalRecordOrFile(string defect)
    {
        using var dir = new TestDirectory();
        var source = Recording();
        string sourceBefore = source.Record.ToJson();
        var branch = ReplayBranch.Start(source.Db, source.Record, 2, 0, AiLevel.Normal);
        string original = branch.Record.ToJson();
        string path = Path.Combine(dir.Root, "branch-source.json");
        File.WriteAllText(path, original, new UTF8Encoding(false));
        byte[] fileBefore = File.ReadAllBytes(path);
        var edited = JsonNode.Parse(File.ReadAllText(path));
        var marker = Assert.Single(edited["events"].AsArray(), entry => entry["type"].GetValue<string>() == "branch").AsObject();
        switch (defect)
        {
            case "version-three": edited["version"] = 3; break;
            case "source-hash": marker["sourceHash"] = new string('A', 64); break;
            case "level": marker["level"] = "UnknownDifficulty"; break;
            case "missing-field": marker.Remove("humanSide"); break;
            case "derived-seed": marker["seed"] = unchecked(marker["seed"].GetValue<uint>() + 1); break;
        }
        string invalidJson = edited.ToJsonString();
        if (defect == "derived-seed")
        {
            // 此值类型与范围合法，须在兼容预检和实际回放校验派生关系。
            var invalid = BattleRecord.FromJson(invalidJson);
            string before = invalid.ToJson();
            var compatibility = Replay.Check(source.Db, invalid);
            Assert.False(compatibility.CanReplay);
            Assert.Contains("派生种子", compatibility.Reason);
            Assert.Contains("派生种子", ChineseFailure(() => Replay.Run(source.Db, invalid)).Message);
            Assert.Equal(before, invalid.ToJson());
        }
        else
        {
            ChineseFailure(() => BattleRecord.FromJson(invalidJson));
            if (defect == "version-three")
            {
                var downgraded = Copy(branch.Record);
                downgraded.Version = 3;
                string before = Json(downgraded);
                Assert.Contains("版本 4", ChineseFailure(() => downgraded.ToJson()).Message);
                var compatibility = Replay.Check(source.Db, downgraded);
                Assert.False(compatibility.CanReplay);
                Assert.Contains("版本 4", compatibility.Reason);
                Assert.Equal(before, Json(downgraded));
            }
        }
        Assert.Equal(original, branch.Record.ToJson());
        Assert.Equal(sourceBefore, source.Record.ToJson());
        Assert.Equal(fileBefore, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("wrong-turn")]
    [InlineData("after-end")]
    public void StructurallyValidBranchAtTheWrongTurnOrOutsideChooseIsRejectedByRealReplay(string defect)
    {
        using var dir = new TestDirectory();
        var source = Recording();
        string sourceBefore = source.Record.ToJson();
        var branch = ReplayBranch.Start(source.Db, source.Record, 2, 0, AiLevel.Normal);
        string original = branch.Record.ToJson();
        string path = Path.Combine(dir.Root, "branch-source.json");
        File.WriteAllText(path, original, new UTF8Encoding(false));
        byte[] fileBefore = File.ReadAllBytes(path);
        var edited = JsonNode.Parse(File.ReadAllText(path));
        var entries = edited["events"].AsArray();
        var marker = Assert.Single(entries, entry => entry["type"].GetValue<string>() == "branch");
        if (defect == "wrong-turn")
        {
            marker["turn"] = 3;
            marker["seed"] = ReplayBranch.DeriveSeed(source.Record.Seed, 3);
        }
        else
        {
            // 仍是第 2 回合，但接手前已通过真实终止事件进入 Over。
            entries.Insert(entries.IndexOf(marker), new JsonObject { ["type"] = "surrender", ["side"] = 1 });
        }
        var invalid = BattleRecord.FromJson(edited.ToJsonString());
        var compatibility = Replay.Check(source.Db, invalid);
        Assert.True(compatibility.CanReplay, compatibility.Reason);
        string invalidBefore = invalid.ToJson();

        Assert.Contains("选择阶段", ChineseFailure(() => Replay.Run(source.Db, invalid)).Message);

        Assert.Equal(invalidBefore, invalid.ToJson());
        Assert.Equal(original, branch.Record.ToJson());
        Assert.Equal(sourceBefore, source.Record.ToJson());
        Assert.Equal(fileBefore, File.ReadAllBytes(path));
    }

    static void SeedRatedProfile(FileSaveStore store, string path, Battle completed)
    {
        var profile = new Profile();
        profile.Record(completed, 0);
        profile.Ratings[Profile.DefaultRatingFormat] = new PlayerRating { Rating = 1473, Games = 8 };
        profile.Ratings["保留赛制"] = new PlayerRating { Rating = 1944, Games = 33 };
        store.SaveProfile(profile);
        // 合法的非规范空白让逐字节断言同时发现无意义的重写。
        File.WriteAllText(path, "\r\n  " + profile.ToJson() + "\r\n", new UTF8Encoding(false));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void RatedStoreSettlementOfFinishedBranchesClonesAndReplaysLeavesAllProfileBytesAndOpponentUnchanged(int winner)
    {
        using var dir = new TestDirectory();
        var source = Recording();
        var branch = ReplayBranch.Start(source.Db, source.Record, 2, 0, AiLevel.Normal);
        if (winner == -1) branch.ResolveTurn(BattleAction.Surrender(), BattleAction.Surrender());
        else branch.Surrender(1 - winner);
        var store = new FileSaveStore(dir.Root);
        string path = Path.Combine(dir.Root, "profile.json");
        SeedRatedProfile(store, path, source);
        byte[] fileBefore = File.ReadAllBytes(path);
        var before = store.LoadProfile();
        string packsBefore = Json(before.Packs), ratingsBefore = Json(before.Ratings);
        var opponent = new PlayerRating { Rating = 1620, Games = 24 };
        string opponentBefore = opponent.ToJson();

        foreach (var candidate in new[] { branch, branch.Clone(), Replay.Run(source.Db, Copy(branch.Record)) })
            for (int side = 0; side < 2; side++)
            {
                Assert.True(candidate.IsBranch);
                Assert.Equal(Phase.Over, candidate.Phase);
                var result = store.RecordProfile(candidate, side, opponent);

                Assert.Equal(packsBefore, Json(result.Packs));
                Assert.Equal(ratingsBefore, Json(result.Ratings));
                Assert.Equal(before.ToJson(), new FileSaveStore(dir.Root).LoadProfile().ToJson());
                Assert.Equal(fileBefore, File.ReadAllBytes(path));
                Assert.Equal(opponentBefore, opponent.ToJson());
                Assert.False(File.Exists(Path.Combine(dir.Root, ".profile.json.tmp")));
            }
    }

    [Fact]
    public void RatedStoreStillRejectsAnUnfinishedBranchWithoutChangingTheProfileOrOpponent()
    {
        using var dir = new TestDirectory();
        var source = Recording();
        var branch = ReplayBranch.Start(source.Db, source.Record, 2, 0, AiLevel.Normal);
        var store = new FileSaveStore(dir.Root);
        string path = Path.Combine(dir.Root, "profile.json");
        SeedRatedProfile(store, path, source);
        byte[] fileBefore = File.ReadAllBytes(path);
        string profileBefore = store.LoadProfile().ToJson(), branchBefore = branch.Record.ToJson();
        var opponent = new PlayerRating { Rating = 1620, Games = 24 };
        string opponentBefore = opponent.ToJson();

        var error = Assert.Throws<InvalidOperationException>(() => store.RecordProfile(branch, 0, opponent));

        Assert.Contains("对战尚未结束", error.Message);
        Assert.Equal(fileBefore, File.ReadAllBytes(path));
        Assert.Equal(profileBefore, store.LoadProfile().ToJson());
        Assert.Equal(opponentBefore, opponent.ToJson());
        Assert.Equal(branchBefore, branch.Record.ToJson());
        Assert.False(File.Exists(Path.Combine(dir.Root, ".profile.json.tmp")));
    }
}
