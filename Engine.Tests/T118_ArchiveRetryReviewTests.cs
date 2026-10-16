using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T118_ArchiveRetryReviewTests
{
    static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexFailureAfterArchiveCanRetryWithoutRewritingReplayOrAdvancingTournament(bool registerBeforeRetry)
    {
        using var files = new TestDirectory();
        var db = Content();
        var tournament = new Tournament(db, Tournament.SingleElimination, options: new TournamentOptions
        {
            AutoTiebreak = true, Seed = 118
        });
        for (int side = 0; side < 2; side++)
            tournament.AddPlayer("选手" + side, TeamCode.Encode(new TeamPreset
            {
                PackId = db.PackId, Name = "验收阵容",
                Ids = db.Streamers.Skip(side * 6).Take(6).Select(item => item.Id).ToList()
            }));
        var pairing = Assert.Single(tournament.NextRound());
        var battle = tournament.CreateGame(pairing);
        battle.ResolveSurrenders(true, true);
        Assert.Equal(Phase.Over, battle.Phase);
        Assert.Equal(-1, battle.Winner);
        string record = battle.Record.ToJson(), pending = tournament.ToJson();
        ulong state = battle.StateHash();
        string libraryPath = Path.Combine(files.Root, "library");
        var library = new ReplayLibrary(libraryPath, clock: () => Epoch);
        string indexPath = Path.Combine(libraryPath, "index.json");

        // 同名普通目录在各平台都拒绝写入文件，不依赖权限、共享锁或计时竞态。
        Directory.CreateDirectory(indexPath);
        var failure = Assert.Throws<ReplayLibraryMaintenanceException>(
            () => library.SaveTournamentGame(tournament, pairing, battle, 0));
        Assert.IsAssignableFrom<IOException>(failure.InnerException);
        Assert.Matches("[\u4e00-\u9fff]", failure.Message);
        string id = failure.ReplayId;
        Assert.False(string.IsNullOrEmpty(id));
        string replayPath = Path.Combine(libraryPath, id + ".json");
        Assert.Equal(replayPath, Assert.Single(ReplayFiles(libraryPath)));
        Assert.True(Directory.Exists(indexPath));
        Assert.False(File.Exists(indexPath));
        Assert.False(File.Exists(Path.Combine(libraryPath, ".library-write.tmp")));
        Assert.Equal(pending, tournament.ToJson());
        Assert.Empty(pairing.Games);
        Assert.Null(pairing.WinnerSide);

        byte[] replayBytes = File.ReadAllBytes(replayPath);
        // 读回平台实际时间精度，避免依赖两次操作之间的睡眠或文件系统时间分辨率。
        File.SetLastWriteTimeUtc(replayPath, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        DateTime replayMtime = File.GetLastWriteTimeUtc(replayPath);
        if (registerBeforeRetry) tournament.ReportGame(pairing, battle);
        string beforeRetry = tournament.ToJson();
        int registered = registerBeforeRetry ? 1 : 0;

        // 新实例证明恢复不依赖首次调用的内存缓存；改变时钟也不能另建第二份录像。
        var reopened = new ReplayLibrary(libraryPath, clock: () => Epoch.AddDays(1));
        var retryFailure = Assert.ThrowsAny<IOException>(
            () => reopened.SaveTournamentGame(tournament, pairing, battle, 0));
        Assert.Matches("[\u4e00-\u9fff]", retryFailure.Message);
        Assert.Equal(beforeRetry, tournament.ToJson());
        Assert.Equal(registered, pairing.Games.Count);
        AssertReplayUnchanged(libraryPath, replayPath, replayBytes, replayMtime);
        Assert.True(Directory.Exists(indexPath));

        files.RemoveEmptyDirectory(indexPath);
        string retryId = reopened.SaveTournamentGame(tournament, pairing, battle, 0);

        Assert.Equal(id, retryId);
        Assert.Equal(beforeRetry, tournament.ToJson());
        Assert.Equal(registered, pairing.Games.Count);
        Assert.Null(pairing.WinnerSide);
        Assert.False(tournament.IsFinished);
        AssertReplayUnchanged(libraryPath, replayPath, replayBytes, replayMtime);
        Assert.True(File.Exists(indexPath));
        using (var index = JsonDocument.Parse(File.ReadAllText(indexPath)))
        {
            Assert.Equal(1, index.RootElement.GetProperty("version").GetInt32());
            var entry = Assert.Single(index.RootElement.GetProperty("entries").EnumerateArray());
            Assert.Equal(id, entry.GetProperty("id").GetString());
            Assert.False(entry.GetProperty("isCorrupt").GetBoolean());
            Assert.False(entry.GetProperty("isUnavailable").GetBoolean());
            var tag = entry.GetProperty("tournament");
            Assert.Equal(2, tag.GetProperty("version").GetInt32());
            Assert.Equal(pairing.Id, tag.GetProperty("pairingId").GetInt32());
            Assert.Equal(0, tag.GetProperty("attempt").GetInt32());
        }

        var reader = ReplayLibrary.OpenReadOnly(libraryPath);
        Assert.Equal(record, reader.Load(id).ToJson());
        if (!registerBeforeRetry)
        {
            using var pendingReport = JsonDocument.Parse(TournamentReport.Build(tournament, reader).ToJson());
            var pendingCoverage = pendingReport.RootElement.GetProperty("coverage");
            Assert.Equal(0, pendingCoverage.GetProperty("completedGames").GetInt32());
            Assert.Equal(0, pendingCoverage.GetProperty("archivedGames").GetInt32());
            Assert.Equal(pending, tournament.ToJson());
            tournament.ReportGame(pairing, battle);
        }
        Assert.Equal(0, Assert.Single(pairing.Games).Attempt);
        string registeredJson = tournament.ToJson();
        using var report = JsonDocument.Parse(TournamentReport.Build(tournament, reader).ToJson());
        var coverage = report.RootElement.GetProperty("coverage");
        Assert.Equal(1, coverage.GetProperty("completedGames").GetInt32());
        Assert.Equal(1, coverage.GetProperty("archivedGames").GetInt32());
        Assert.Equal(0, coverage.GetProperty("missingGames").GetInt32());
        Assert.Equal(0, coverage.GetProperty("invalidGames").GetInt32());
        var match = Assert.Single(report.RootElement.GetProperty("rounds").EnumerateArray()
            .SelectMany(round => round.GetProperty("matches").EnumerateArray()));
        var shownGame = Assert.Single(match.GetProperty("games").EnumerateArray());
        Assert.Equal("saved", shownGame.GetProperty("replay").GetProperty("status").GetString());
        Assert.Equal(id, shownGame.GetProperty("replay").GetProperty("id").GetString());
        Assert.Equal(registeredJson, tournament.ToJson());
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(state, battle.StateHash());
        AssertReplayUnchanged(libraryPath, replayPath, replayBytes, replayMtime);
    }

    static string[] ReplayFiles(string directory)
    {
        return Directory.GetFiles(directory, "*.json").Where(path => Path.GetFileName(path) != "index.json").ToArray();
    }

    static void AssertReplayUnchanged(string directory, string path, byte[] bytes, DateTime mtime)
    {
        Assert.Equal(path, Assert.Single(ReplayFiles(directory)));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));
        Assert.False(File.Exists(Path.Combine(directory, ".library-write.tmp")));
    }

    static ContentDb Content()
    {
        var db = new ContentDb { PackId = "t118_retry_fixture", PackName = "归档重试验收", PackNotice = "独立测试数据" };
        db.Types["talk"] = new TypeDef { Id = "talk", Name = "聊天", Color = "#808080" };
        db.Chart["talk"] = new Dictionary<string, double> { { "talk", 1 } };
        db.Personas["neutral"] = new PersonaDef { Id = "neutral", Name = "测试人设", Code = "T_LOWHP_POWER" };
        var skills = Enumerable.Range(0, 4).Select(index => "wait" + index).ToList();
        foreach (string id in skills)
            db.Skills[id] = new SkillDef { Id = id, Name = "测试等待", Type = "talk", Cat = "status", Pp = 20 };
        db.Struggle = new SkillDef { Id = "struggle", Name = "测试硬撑", Type = "talk", Cat = "interact", Power = 20, Pp = 1 };
        for (int index = 0; index < 12; index++)
            db.Streamers.Add(new StreamerDef
            {
                Id = "u" + index.ToString("D2"), Name = "测试主播" + index, Type = "talk", Persona = "neutral",
                Stats = new Dictionary<string, int>
                {
                    { "hp", 100 }, { "atk", 100 }, { "def", 100 }, { "spa", 100 }, { "sdef", 100 }, { "spd", 100 }
                },
                Skills = new List<string>(skills), Fifth = skills[0]
            });
        db.Validate();
        return db;
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string workspace, parent, name = Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string source = "")
        {
            string tests = Path.GetDirectoryName(Path.GetFullPath(source));
            while (tests != null && Path.GetFileName(tests) != "Engine.Tests") tests = Path.GetDirectoryName(tests);
            if (tests == null) throw new InvalidOperationException("验收临时目录必须位于当前工程 Engine.Tests/obj。");
            workspace = Path.GetDirectoryName(tests);
            parent = Path.GetFullPath(Path.Combine(tests, "obj", "t118-review", "temp"));
            Root = Path.Combine(parent, name);
            ValidatePath(Root);
            Directory.CreateDirectory(Root);
        }

        void ValidatePath(string path)
        {
            string target = Path.GetFullPath(path), owned = Path.GetFullPath(Root);
            if (!target.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.GetDirectoryName(owned) != parent || Path.GetFileName(owned) != name
                || (target != owned && !target.StartsWith(owned + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw new InvalidOperationException("只能操作当前工作区内本测试创建的 GUID 目录。");
            for (string item = target; item != workspace; item = Path.GetDirectoryName(item))
                if (Directory.Exists(item) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("验收临时目录不能经过链接。");
        }

        public void RemoveEmptyDirectory(string path)
        {
            ValidatePath(path);
            Directory.Delete(path, false);
        }

        public void Dispose()
        {
            ValidatePath(Root);
            if (!Directory.Exists(Root)) return;
            foreach (string child in Directory.EnumerateDirectories(Root)) ValidatePath(child);
            Directory.Delete(Root, true);
        }
    }
}
