using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T110_TournamentReportTests
{
    static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = "T110-" + Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string source = "")
        {
            parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "bin", "test-temp"));
            Root = Path.Combine(parent, name);
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            string target = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(target) != parent || Path.GetFileName(target) != name)
                throw new InvalidOperationException("只能清理当前测试创建的目录。");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    sealed class Fixture
    {
        public ContentDb Db;
        public Tournament Tournament;
        public ReplayLibrary Library;
        public string LibraryPath;
        public readonly Dictionary<int, string> ReplayIds = new Dictionary<int, string>();
    }

    sealed class IndependentTotals
    {
        public int Damage, Pairing = -1, LogIndex = -1, TargetSide = -1, TargetSlot = -1, Turn;
        public string TargetId;
        public readonly Dictionary<string, long> Kos = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> Appearances = new Dictionary<string, long>(StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("roundrobin", 4, 6)]
    [InlineData("swiss", 6, 9)]
    public void FixedSeedTournamentReportMatchesStandingsAndIndependentReplayStatistics(string format, int players, int matches)
    {
        using var files = new TestDirectory();
        var fixture = Make(files, format, players);
        Complete(fixture);
        var expected = Recompute(fixture);
        Assert.True(fixture.Tournament.IsFinished);
        Assert.Equal(matches, fixture.ReplayIds.Count);
        Assert.True(expected.Damage > 0);
        Assert.NotEmpty(expected.Kos);
        Assert.NotEmpty(expected.Appearances);
        string saved = fixture.Tournament.ToJson(), hash = fixture.Db.ContentHash();
        File.WriteAllText(Path.Combine(files.Root, "tournament.json"), saved, new UTF8Encoding(false));
        // 正常 List/Load 会维护索引或建立锁；报告不能依赖这些写入副作用。
        File.Delete(Path.Combine(fixture.LibraryPath, "index.json"));
        File.Delete(Path.Combine(fixture.LibraryPath, ".library.lock"));
        var before = Files(files.Root);
        var report = TournamentReport.Build(fixture.Tournament, fixture.Library);
        string json = report.ToJson(), text = report.ToText();
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(fixture.Db.PackId, root.GetProperty("packId").GetString());
        Assert.Equal(format, root.GetProperty("format").GetString());
        Assert.Equal(3, root.GetProperty("roundCount").GetInt32());
        Assert.Equal(3, root.GetProperty("playedRounds").GetInt32());
        Assert.True(root.GetProperty("isFinished").GetBoolean());
        AssertStandings(fixture.Tournament, root);
        AssertCoverage(root, matches, matches, 0, 0);
        AssertMetrics(fixture, root, expected);
        var matchRows = Matches(root);
        Assert.Equal(matches, matchRows.Length);
        foreach (var pairing in fixture.Tournament.Pairings)
        {
            var row = Assert.Single(matchRows, row => row.GetProperty("pairingId").GetInt32() == pairing.Id);
            Assert.Equal(pairing.Player0, row.GetProperty("player0").GetInt32());
            Assert.Equal(pairing.Player1, row.GetProperty("player1").GetInt32());
            Assert.Equal(pairing.WinnerSide.Value, row.GetProperty("winnerSide").GetInt32());
            Assert.Equal("saved", row.GetProperty("replay").GetProperty("status").GetString());
            Assert.Equal(fixture.ReplayIds[pairing.Id], row.GetProperty("replay").GetProperty("id").GetString());
            Assert.Equal(TextInput.Clean(fixture.Tournament.Players[pairing.Player0].Name, TextInputKind.PlayerName).Text,
                row.GetProperty("name0").GetString());
            Assert.Equal(TextInput.Clean(fixture.Tournament.Players[pairing.Player1].Name, TextInputKind.PlayerName).Text,
                row.GetProperty("name1").GetString());
        }
        Assert.DoesNotContain("\u202e", text, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Tournament.Players[0].Name, text, StringComparison.Ordinal);
        Assert.DoesNotContain(files.Root, text, StringComparison.Ordinal);
        Assert.DoesNotContain(files.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(files.Root), json, StringComparison.Ordinal);
        Assert.Contains("并列", text, StringComparison.Ordinal);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var restored = Tournament.FromJson(fixture.Db, saved);
            var again = TournamentReport.Build(restored, fixture.Library);
            Assert.Equal(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetBytes(again.ToText()));
            Assert.Equal(Encoding.UTF8.GetBytes(json), Encoding.UTF8.GetBytes(again.ToJson()));
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        Assert.Equal(saved, fixture.Tournament.ToJson());
        Assert.Equal(hash, fixture.Db.ContentHash());
        AssertFilesEqual(before, files.Root);
    }

    [Fact]
    public void DeletedCorruptAndMismatchedArchivesAreExcludedFromCoverageAndMetrics()
    {
        using var files = new TestDirectory();
        var fixture = Make(files);
        Complete(fixture);
        var good = fixture.ReplayIds.Keys.Where(id => id >= 3).ToArray();
        var expected = Recompute(fixture, good);
        File.Delete(ReplayPath(fixture, 0));
        EditReplay(fixture, 1, root => root["events"] = "事件列表已损坏");
        EditReplay(fixture, 2, root =>
        {
            var library = (Dictionary<string, object>)root["library"];
            library["winner"] = fixture.Tournament.Pairings[2].WinnerSide == -1 ? 0 : -1;
        });
        var before = Files(files.Root);
        var report = TournamentReport.Build(fixture.Tournament, fixture.Library);
        using var parsed = JsonDocument.Parse(report.ToJson());
        var root = parsed.RootElement;
        AssertCoverage(root, 6, 3, 1, 2);
        var rows = Matches(root);
        Assert.Equal("not_saved", ReplayStatus(rows, 0));
        Assert.Equal("corrupt", ReplayStatus(rows, 1));
        Assert.Equal("mismatch", ReplayStatus(rows, 2));
        Assert.Equal(JsonValueKind.Null, rows.Single(row => row.GetProperty("pairingId").GetInt32() == 0)
            .GetProperty("replay").GetProperty("id").ValueKind);
        foreach (int pairing in new[] { 1, 2 })
            Assert.Equal(fixture.ReplayIds[pairing], rows.Single(row => row.GetProperty("pairingId").GetInt32() == pairing)
                .GetProperty("replay").GetProperty("id").GetString());
        Assert.Contains("未保存", rows.Single(row => row.GetProperty("pairingId").GetInt32() == 0)
            .GetProperty("replay").GetProperty("message").GetString(), StringComparison.Ordinal);
        AssertMetrics(fixture, root, expected);
        AssertFilesEqual(before, files.Root);
    }

    [Fact]
    public void UnavailableAndDuplicateArchivesNeverCreatePartialOrDoubleCountedChampions()
    {
        using var files = new TestDirectory();
        var fixture = Make(files, players: 2);
        Complete(fixture);
        var policy = PackPolicy.FromJson("{\"disabledPacks\":[\"original\"]}");
        var unavailable = new ReplayLibrary(fixture.LibraryPath, policy: policy);
        var before = Files(files.Root);
        using (var parsed = JsonDocument.Parse(TournamentReport.Build(fixture.Tournament, unavailable).ToJson()))
        {
            AssertCoverage(parsed.RootElement, 1, 0, 0, 1);
            Assert.Equal("unavailable", ReplayStatus(Matches(parsed.RootElement), 0));
            Assert.Equal(fixture.ReplayIds[0], Assert.Single(Matches(parsed.RootElement))
                .GetProperty("replay").GetProperty("id").GetString());
            AssertNoMetrics(parsed.RootElement);
        }
        AssertFilesEqual(before, files.Root);

        string duplicate = fixture.ReplayIds[0] + "_copy";
        var copy = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(ReplayPath(fixture, 0)));
        ((Dictionary<string, object>)copy["library"])["id"] = duplicate;
        File.WriteAllText(Path.Combine(fixture.LibraryPath, duplicate + ".json"), JsonSerializer.Serialize(copy));
        before = Files(files.Root);
        using (var parsed = JsonDocument.Parse(TournamentReport.Build(fixture.Tournament, fixture.Library).ToJson()))
        {
            AssertCoverage(parsed.RootElement, 1, 0, 0, 1);
            Assert.Equal("ambiguous", ReplayStatus(Matches(parsed.RootElement), 0));
            Assert.Equal(JsonValueKind.Null, Assert.Single(Matches(parsed.RootElement))
                .GetProperty("replay").GetProperty("id").ValueKind);
            AssertNoMetrics(parsed.RootElement);
        }
        AssertFilesEqual(before, files.Root);
    }

    [Fact]
    public void MissingLibraryPendingGamesAndByesDoNotAdvanceOrInventStatistics()
    {
        using var files = new TestDirectory();
        var fixture = Make(files, players: 3);
        string initial = fixture.Tournament.ToJson();
        using (var parsed = JsonDocument.Parse(TournamentReport.Build(fixture.Tournament, null).ToJson()))
        {
            Assert.Empty(parsed.RootElement.GetProperty("rounds").EnumerateArray());
            AssertCoverage(parsed.RootElement, 0, 0, 0, 0);
            AssertNoMetrics(parsed.RootElement);
        }
        Assert.Equal(initial, fixture.Tournament.ToJson());
        var pairings = fixture.Tournament.NextRound();
        var match = Assert.Single(pairings, pairing => !pairing.IsBye);
        var bye = Assert.Single(pairings, pairing => pairing.IsBye);
        string pending = fixture.Tournament.ToJson();
        using (var parsed = JsonDocument.Parse(TournamentReport.Build(fixture.Tournament, null).ToJson()))
        {
            var rows = Matches(parsed.RootElement);
            Assert.Equal("pending", ReplayStatus(rows, match.Id));
            Assert.Equal("not_applicable", ReplayStatus(rows, bye.Id));
            Assert.Equal(JsonValueKind.Null, rows.Single(row => row.GetProperty("pairingId").GetInt32() == bye.Id)
                .GetProperty("player1").ValueKind);
            AssertCoverage(parsed.RootElement, 0, 0, 0, 0);
        }
        Assert.Equal(pending, fixture.Tournament.ToJson());
        fixture.Tournament.ReportResult(match, -1);
        string completedRound = fixture.Tournament.ToJson();
        using (var parsed = JsonDocument.Parse(TournamentReport.Build(fixture.Tournament, null).ToJson()))
        {
            Assert.Equal(1, parsed.RootElement.GetProperty("playedRounds").GetInt32());
            AssertCoverage(parsed.RootElement, 1, 0, 1, 0);
            Assert.Equal("not_saved", ReplayStatus(Matches(parsed.RootElement), match.Id));
            AssertNoMetrics(parsed.RootElement);
        }
        Assert.Equal(completedRound, fixture.Tournament.ToJson());
        Assert.Throws<ArgumentNullException>(() => TournamentReport.Build(null, fixture.Library));
    }

    [Fact]
    public void ReentryIsCountedOnceAndSelfDamageUsesActualLossAndExistingKoCredit()
    {
        using var files = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        db.Personas["report_neutral"] = new PersonaDef
        {
            Id = "report_neutral", Name = "统计测试人设", Code = "T_LOWHP_POWER",
            Params = new Dictionary<string, double> { ["below"] = 0.0, ["mul"] = 1.0 }
        };
        db.Skills["report_self"] = new SkillDef
        {
            Id = "report_self", Name = "统计测试自伤", Type = "talk", Cat = "status", Pp = 20,
            Effects = new List<EffectDef> { new EffectDef { Code = "FIXED_DAMAGE", At = "post", Target = "self", N = 999 } }
        };
        foreach (var fighter in db.Streamers)
        {
            fighter.Persona = "report_neutral";
            fighter.Stats["hp"] = 100;
            fighter.Skills[0] = "report_self";
        }
        var fixture = Make(files, players: 2, db: db);
        var pairing = Assert.Single(fixture.Tournament.NextRound());
        var battle = BattleFor(fixture, pairing);
        string first = battle.Active(0).Def.Id, opponent = battle.Active(1).Def.Id;
        battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.SwitchTo(0), BattleAction.Pass());
        battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.True(battle.Active(0).Fainted);
        battle.Surrender(0);
        fixture.ReplayIds[pairing.Id] = fixture.Library.SaveTournament(fixture.Tournament, pairing, battle);
        fixture.Tournament.ReportResult(pairing, battle.Winner);
        using var parsed = JsonDocument.Parse(TournamentReport.Build(fixture.Tournament, fixture.Library).ToJson());
        var root = parsed.RootElement;
        AssertMetrics(fixture, root, Recompute(fixture));
        Assert.Equal(100, root.GetProperty("maxDamage").GetProperty("amount").GetInt32());
        Assert.Equal(first, root.GetProperty("maxDamage").GetProperty("streamerId").GetString());
        Assert.Equal(opponent, root.GetProperty("mostKos").GetProperty("streamerId").GetString());
        Assert.Equal(1L, root.GetProperty("mostKos").GetProperty("count").GetInt64());
        Assert.Equal(1L, root.GetProperty("mostAppearances").GetProperty("count").GetInt64());
        var enteredIds = new[] { first, battle.Sides[0].Team[1].Def.Id, opponent };
        Assert.Equal(enteredIds.OrderBy(id => id, StringComparer.Ordinal).First(),
            root.GetProperty("mostAppearances").GetProperty("streamerId").GetString());
    }

    [Fact]
    public void ArchivedNoDamageGameDoesNotAwardZeroDamageOrZeroKoChampion()
    {
        using var files = new TestDirectory();
        var fixture = Make(files, players: 2, sameTeam: true);
        var pairing = Assert.Single(fixture.Tournament.NextRound());
        var battle = BattleFor(fixture, pairing);
        battle.ResolveSurrenders(true, true);
        fixture.ReplayIds[pairing.Id] = fixture.Library.SaveTournament(fixture.Tournament, pairing, battle);
        fixture.Tournament.ReportResult(pairing, -1);
        using var parsed = JsonDocument.Parse(TournamentReport.Build(fixture.Tournament, fixture.Library).ToJson());
        var root = parsed.RootElement;
        AssertCoverage(root, 1, 1, 0, 0);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("maxDamage").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("mostKos").ValueKind);
        Assert.Equal(2L, root.GetProperty("mostAppearances").GetProperty("count").GetInt64());
        Assert.Equal(fixture.Tournament.Players.Select(player => player.Team[0]).OrderBy(id => id, StringComparer.Ordinal).First(),
            root.GetProperty("mostAppearances").GetProperty("streamerId").GetString());
    }

    [Fact]
    public void IdenticalOrdinaryReplayWithoutTournamentAssociationIsStillNotSavedForThisMatch()
    {
        using var files = new TestDirectory();
        var fixture = Make(files, players: 2);
        var pairing = Assert.Single(fixture.Tournament.NextRound());
        var battle = BattleFor(fixture, pairing);
        battle.Surrender(0);
        fixture.Library.Save(battle.Record, BattleSummary.From(battle));
        fixture.Tournament.ReportResult(pairing, battle.Winner);
        var before = Files(files.Root);
        using var parsed = JsonDocument.Parse(TournamentReport.Build(fixture.Tournament, fixture.Library).ToJson());
        AssertCoverage(parsed.RootElement, 1, 0, 1, 0);
        Assert.Equal("not_saved", ReplayStatus(Matches(parsed.RootElement), pairing.Id));
        AssertNoMetrics(parsed.RootElement);
        AssertFilesEqual(before, files.Root);
    }

    [Fact]
    public void TournamentArchiveRejectsInvalidArgumentsForeignPairingsAndMismatchedBattlesAtomically()
    {
        using var files = new TestDirectory();
        var fixture = Make(files, players: 2);
        var pairing = Assert.Single(fixture.Tournament.NextRound());
        var battle = BattleFor(fixture, pairing);
        var foreign = Assert.Single(Tournament.FromJson(fixture.Db, fixture.Tournament.ToJson()).Pairings);
        var odd = Make(files, players: 3);
        var bye = Assert.Single(odd.Tournament.NextRound(), item => item.IsBye);
        string tournament = fixture.Tournament.ToJson(), record = battle.Record.ToJson();
        var before = Files(files.Root);

        Assert.Throws<ArgumentNullException>(() => fixture.Library.SaveTournament(null, pairing, battle));
        Assert.Throws<ArgumentNullException>(() => fixture.Library.SaveTournament(fixture.Tournament, null, battle));
        Assert.Throws<ArgumentNullException>(() => fixture.Library.SaveTournament(fixture.Tournament, pairing, null));
        Assert.Throws<ArgumentException>(() => fixture.Library.SaveTournament(fixture.Tournament, foreign, battle));
        Assert.Throws<InvalidOperationException>(() => fixture.Library.SaveTournament(odd.Tournament, bye, battle));
        Assert.Throws<InvalidOperationException>(() => fixture.Library.SaveTournament(fixture.Tournament, pairing, battle));
        Assert.Equal(tournament, fixture.Tournament.ToJson());
        Assert.Equal(record, battle.Record.ToJson());
        AssertFilesEqual(before, files.Root);

        battle.Surrender(0);
        var changes = new Action<Battle>[]
        {
            changed => changed.Record.PackId = "other_pack",
            changed => changed.Record.ContentHash = "wrong_fingerprint",
            changed => changed.Record.Names[0] = "另一位选手",
            changed =>
            {
                var team = changed.Record.Teams[0];
                string first = team[0]; team[0] = team[1]; team[1] = first;
            }
        };
        foreach (var change in changes)
        {
            var changed = battle.Clone();
            change(changed);
            Assert.Throws<InvalidOperationException>(() => fixture.Library.SaveTournament(fixture.Tournament, pairing, changed));
        }
        fixture.Tournament.ReportResult(pairing, 1 - battle.Winner);
        tournament = fixture.Tournament.ToJson();
        record = battle.Record.ToJson();
        Assert.Throws<InvalidOperationException>(() => fixture.Library.SaveTournament(fixture.Tournament, pairing, battle));
        Assert.Equal(tournament, fixture.Tournament.ToJson());
        Assert.Equal(record, battle.Record.ToJson());
        AssertFilesEqual(before, files.Root);
    }

    [Fact]
    public void ArchiveRetryReusesItsIdButDifferentOrCorruptExistingRecordsAreNotOverwritten()
    {
        using var files = new TestDirectory();
        var fixture = Make(files, players: 2);
        var pairing = Assert.Single(fixture.Tournament.NextRound());
        var battle = BattleFor(fixture, pairing);
        battle.Surrender(0);
        string id = fixture.Library.SaveTournament(fixture.Tournament, pairing, battle);
        fixture.ReplayIds[pairing.Id] = id;
        var before = Files(files.Root);
        string pending = fixture.Tournament.ToJson();
        using (var parsed = JsonDocument.Parse(TournamentReport.Build(fixture.Tournament, fixture.Library).ToJson()))
        {
            var root = parsed.RootElement;
            var row = Assert.Single(Matches(root));
            Assert.Equal("pending", row.GetProperty("replay").GetProperty("status").GetString());
            Assert.Equal(id, row.GetProperty("replay").GetProperty("id").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("winnerSide").ValueKind);
            AssertCoverage(root, 0, 0, 0, 0);
            AssertNoMetrics(root);
        }
        Assert.Equal(pending, fixture.Tournament.ToJson());
        Assert.Null(pairing.WinnerSide);
        AssertFilesEqual(before, files.Root);
        Assert.Equal(id, fixture.Library.SaveTournament(fixture.Tournament, pairing, battle));
        fixture.Tournament.ReportResult(pairing, battle.Winner);
        string saved = fixture.Tournament.ToJson();
        Assert.Equal(id, fixture.Library.SaveTournament(fixture.Tournament, pairing, battle));
        AssertFilesEqual(before, files.Root);

        var different = BattleFor(fixture, pairing);
        different.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        different.Surrender(0);
        Assert.Equal(battle.Winner, different.Winner);
        Assert.NotEqual(battle.Record.ToJson(), different.Record.ToJson());
        Assert.Throws<InvalidOperationException>(() => fixture.Library.SaveTournament(fixture.Tournament, pairing, different));
        Assert.Equal(saved, fixture.Tournament.ToJson());
        AssertFilesEqual(before, files.Root);

        EditReplay(fixture, pairing.Id, root => root["events"] = "保留关联但破坏记录");
        before = Files(files.Root);
        Assert.Throws<InvalidOperationException>(() => fixture.Library.SaveTournament(fixture.Tournament, pairing, battle));
        Assert.Equal(saved, fixture.Tournament.ToJson());
        AssertFilesEqual(before, files.Root);
    }

    [Fact]
    public void ReadOnlyLibraryRejectsAllMutationAndNeverCreatesMissingDirectoryOrLock()
    {
        using var files = new TestDirectory();
        var fixture = Make(files, players: 2);
        var pairing = Assert.Single(fixture.Tournament.NextRound());
        var battle = BattleFor(fixture, pairing);
        battle.Surrender(0);
        string id = fixture.Library.SaveTournament(fixture.Tournament, pairing, battle);
        fixture.Tournament.ReportResult(pairing, battle.Winner);
        File.Delete(Path.Combine(fixture.LibraryPath, "index.json"));
        File.Delete(Path.Combine(fixture.LibraryPath, ".library.lock"));
        var before = Files(files.Root);
        var readOnly = ReplayLibrary.OpenReadOnly(fixture.LibraryPath, PackPolicy.Empty);
        Assert.Equal(battle.Record.ToJson(), readOnly.Load(id).ToJson());
        var mutations = new Action[]
        {
            () => readOnly.SaveTournament(fixture.Tournament, pairing, battle),
            () => readOnly.Save(battle.Record, BattleSummary.From(battle)),
            () => readOnly.Delete(id),
            () => readOnly.SetNote(id, "不得改写"),
            () => readOnly.List()
        };
        foreach (var mutate in mutations)
            Assert.Contains("只读", Assert.Throws<InvalidOperationException>(mutate).Message, StringComparison.Ordinal);
        AssertFilesEqual(before, files.Root);

        string missingPath = Path.Combine(files.Root, "missing", "replays");
        var missing = ReplayLibrary.OpenReadOnly(missingPath, PackPolicy.Empty);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missingPath)));
        using (var parsed = JsonDocument.Parse(TournamentReport.Build(fixture.Tournament, missing).ToJson()))
        {
            AssertCoverage(parsed.RootElement, 1, 0, 1, 0);
            AssertNoMetrics(parsed.RootElement);
        }
        Assert.Throws<InvalidOperationException>(() => missing.SaveTournament(fixture.Tournament, pairing, battle));
        Assert.False(Directory.Exists(Path.GetDirectoryName(missingPath)));
        AssertFilesEqual(before, files.Root);
    }

    static Fixture Make(TestDirectory files, string format = "roundrobin", int players = 4, ContentDb db = null,
        bool sameTeam = false)
    {
        db = db ?? BattleTests.LoadPack("original");
        var fixture = new Fixture
        {
            Db = db, Tournament = new Tournament(db, format, format == "swiss" ? 3 : 0),
            LibraryPath = Path.Combine(files.Root, "replays")
        };
        fixture.Library = new ReplayLibrary(fixture.LibraryPath, clock: () => Epoch, policy: PackPolicy.Empty);
        var ids = db.Streamers.Select(fighter => fighter.Id).ToArray();
        for (int player = 0; player < players; player++)
        {
            var team = Enumerable.Range(0, 6).Select(slot => ids[((sameTeam ? 0 : player * 2) + slot) % ids.Length]).ToList();
            string name = player == 0 ? "甲\u202e  方直播间的超长显示名称" : "选手 " + player;
            fixture.Tournament.AddPlayer(name, TeamCode.Encode(new TeamPreset
            {
                Name = "测试阵容", PackId = db.PackId, Ids = team
            }));
        }
        return fixture;
    }

    static Battle BattleFor(Fixture fixture, Pairing pairing)
    {
        var first = fixture.Tournament.Players[pairing.Player0];
        var second = fixture.Tournament.Players[pairing.Player1];
        return new Battle(fixture.Db, new IList<string>[] { first.Team.ToArray(), second.Team.ToArray() },
            new[] { first.Name, second.Name }, unchecked(0x9E3779B9u + (uint)pairing.Id));
    }

    static Battle Play(Fixture fixture, Pairing pairing)
    {
        var battle = BattleFor(fixture, pairing);
        for (int guard = 0; battle.Phase != Phase.Over && guard < 500; guard++)
        {
            if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(Ai.Choose(battle, 0, AiLevel.Normal), Ai.Choose(battle, 1, AiLevel.Normal));
            else if (battle.Phase == Phase.Replace)
                for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, AiLevel.Normal));
        }
        Assert.Equal(Phase.Over, battle.Phase);
        return battle;
    }

    static void Complete(Fixture fixture)
    {
        while (!fixture.Tournament.IsFinished)
            foreach (var pairing in fixture.Tournament.NextRound())
            {
                if (pairing.IsBye) continue;
                var battle = Play(fixture, pairing);
                // 两种调用顺序均合法，续赛保存不能要求重新报告已确认的比分。
                if (pairing.Id % 2 == 0) fixture.Tournament.ReportResult(pairing, battle.Winner);
                fixture.ReplayIds[pairing.Id] = fixture.Library.SaveTournament(fixture.Tournament, pairing, battle);
                if (!pairing.WinnerSide.HasValue) fixture.Tournament.ReportResult(pairing, battle.Winner);
            }
    }

    static IndependentTotals Recompute(Fixture fixture, IEnumerable<int> pairings = null)
    {
        var result = new IndependentTotals();
        foreach (int pairing in (pairings ?? fixture.ReplayIds.Keys).OrderBy(value => value))
        {
            var battle = Replay.Run(fixture.Db, fixture.Library.Load(fixture.ReplayIds[pairing]));
            Assert.Equal(Phase.Over, battle.Phase);
            var entered = new HashSet<(int Side, string Id)>();
            for (int index = 0; index < battle.Log.Count; index++)
            {
                var log = battle.Log[index];
                if (log.Kind == "dmg")
                {
                    Assert.True(index > 0);
                    int slot = log.SnapActive[log.Side];
                    int lost = battle.Log[index - 1].SnapHp[log.Side][slot] - log.SnapHp[log.Side][slot];
                    if (lost > result.Damage)
                    {
                        result.Damage = lost;
                        result.Pairing = pairing;
                        result.LogIndex = index;
                        result.TargetSide = log.Side;
                        result.TargetSlot = slot;
                        result.Turn = log.Turn;
                        result.TargetId = battle.Sides[log.Side].Team[slot].Def.Id;
                    }
                }
                else if (log.Kind == "faint")
                {
                    int creditedSide = 1 - log.Side;
                    string id = battle.Sides[creditedSide].Team[log.SnapActive[creditedSide]].Def.Id;
                    Add(result.Kos, id);
                }
                else if (log.Kind == "enter")
                    entered.Add((log.Side, battle.Sides[log.Side].Team[log.SnapActive[log.Side]].Def.Id));
            }
            foreach (var fighter in entered) Add(result.Appearances, fighter.Id);
        }
        return result;
    }

    static void Add(Dictionary<string, long> values, string id)
    {
        values.TryGetValue(id, out long count);
        values[id] = count + 1;
    }

    static KeyValuePair<string, long> Most(Dictionary<string, long> values)
    {
        return values.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First();
    }

    static JsonElement[] Matches(JsonElement root)
    {
        return root.GetProperty("rounds").EnumerateArray().SelectMany(round => round.GetProperty("matches").EnumerateArray()).ToArray();
    }

    static string ReplayStatus(JsonElement[] rows, int pairing)
    {
        return Assert.Single(rows, row => row.GetProperty("pairingId").GetInt32() == pairing)
            .GetProperty("replay").GetProperty("status").GetString();
    }

    static void AssertCoverage(JsonElement root, int completed, int archived, int missing, int invalid)
    {
        var coverage = root.GetProperty("coverage");
        Assert.Equal(completed, coverage.GetProperty("completedGames").GetInt32());
        Assert.Equal(archived, coverage.GetProperty("archivedGames").GetInt32());
        Assert.Equal(missing, coverage.GetProperty("missingGames").GetInt32());
        Assert.Equal(invalid, coverage.GetProperty("invalidGames").GetInt32());
    }

    static void AssertNoMetrics(JsonElement root)
    {
        foreach (string metric in new[] { "maxDamage", "mostKos", "mostAppearances" })
            Assert.Equal(JsonValueKind.Null, root.GetProperty(metric).ValueKind);
    }

    static void AssertStandings(Tournament tournament, JsonElement root)
    {
        var rows = root.GetProperty("standings").EnumerateArray().ToArray();
        var standings = tournament.Standings();
        Assert.Equal(standings.Count, rows.Length);
        for (int index = 0; index < standings.Count; index++)
        {
            var expected = standings[index];
            var actual = rows[index];
            Assert.Equal(expected.Rank, actual.GetProperty("rank").GetInt32());
            Assert.Equal(expected.Player, actual.GetProperty("player").GetInt32());
            Assert.Equal(TextInput.Clean(expected.Name, TextInputKind.PlayerName).Text, actual.GetProperty("name").GetString());
            Assert.Equal(expected.Points, actual.GetProperty("points").GetInt32());
            Assert.Equal(expected.OpponentPoints, actual.GetProperty("opponentPoints").GetInt32());
            Assert.Equal(expected.HeadToHeadPoints, actual.GetProperty("headToHeadPoints").GetInt32());
            Assert.Equal(expected.Wins, actual.GetProperty("wins").GetInt32());
            Assert.Equal(expected.Draws, actual.GetProperty("draws").GetInt32());
            Assert.Equal(expected.Losses, actual.GetProperty("losses").GetInt32());
            Assert.Equal(expected.Byes, actual.GetProperty("byes").GetInt32());
        }
    }

    static void AssertMetrics(Fixture fixture, JsonElement root, IndependentTotals expected)
    {
        var max = root.GetProperty("maxDamage");
        if (expected.Damage == 0) Assert.Equal(JsonValueKind.Null, max.ValueKind);
        else
        {
            Assert.Equal(expected.Damage, max.GetProperty("amount").GetInt32());
            Assert.Equal(expected.Pairing, max.GetProperty("pairingId").GetInt32());
            Assert.Equal(fixture.Tournament.Pairings.Single(pairing => pairing.Id == expected.Pairing).Round,
                max.GetProperty("round").GetInt32());
            Assert.Equal(expected.Turn, max.GetProperty("turn").GetInt32());
            Assert.Equal(expected.LogIndex, max.GetProperty("logIndex").GetInt32());
            Assert.Equal(expected.TargetSide, max.GetProperty("side").GetInt32());
            Assert.Equal(expected.TargetSlot, max.GetProperty("slot").GetInt32());
            Assert.Equal(expected.TargetId, max.GetProperty("streamerId").GetString());
            Assert.Equal(fixture.Db.Streamer(expected.TargetId).Name, max.GetProperty("streamerName").GetString());
            Assert.Equal(fixture.ReplayIds[expected.Pairing], max.GetProperty("replayId").GetString());
        }
        AssertMost(fixture, root.GetProperty("mostKos"), expected.Kos);
        AssertMost(fixture, root.GetProperty("mostAppearances"), expected.Appearances);
    }

    static void AssertMost(Fixture fixture, JsonElement actual, Dictionary<string, long> counts)
    {
        if (counts.Count == 0) { Assert.Equal(JsonValueKind.Null, actual.ValueKind); return; }
        var expected = Most(counts);
        Assert.Equal(expected.Key, actual.GetProperty("streamerId").GetString());
        Assert.Equal(fixture.Db.Streamer(expected.Key).Name, actual.GetProperty("streamerName").GetString());
        Assert.Equal(expected.Value, actual.GetProperty("count").GetInt64());
    }

    static string ReplayPath(Fixture fixture, int pairing)
    {
        return Path.Combine(fixture.LibraryPath, fixture.ReplayIds[pairing] + ".json");
    }

    static void EditReplay(Fixture fixture, int pairing, Action<Dictionary<string, object>> edit)
    {
        string path = ReplayPath(fixture, pairing);
        var root = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(path));
        edit(root);
        File.WriteAllText(path, JsonSerializer.Serialize(root));
    }

    static Dictionary<string, byte[]> Files(string root)
    {
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes, StringComparer.Ordinal);
    }

    static void AssertFilesEqual(Dictionary<string, byte[]> before, string root)
    {
        var after = Files(root);
        Assert.Equal(before.Keys.OrderBy(key => key, StringComparer.Ordinal), after.Keys.OrderBy(key => key, StringComparer.Ordinal));
        foreach (var item in before) Assert.Equal(item.Value, after[item.Key]);
    }
}
