using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T102_ProfileStatsTests
{
    static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    static readonly string[] TeamA = { "bell", "enka", "shiori", "lumi", "hane", "momo" };
    static readonly string[] TeamB = { "bell", "kei", "yuan", "nyako", "kasumi", "bee" };

    sealed class Fixture
    {
        public ContentDb Db;
        public Profile Profile;
        public List<ReplayEntry> Entries;
    }

    static Fixture TwentyGames()
    {
        // 前十二局使用第一队、后八局使用第二队，结果由此固定表给出，不依赖统计实现生成答案。
        const string results = "WWLDLWWWLDLWDWLWWWWW";
        var fixture = new Fixture
        {
            Db = BattleTests.LoadPack("original"), Profile = new Profile(), Entries = new List<ReplayEntry>()
        };
        for (int game = 1; game <= 20; game++)
        {
            var ids = game <= 12 ? TeamA : TeamB;
            var outcome = results[game - 1] == 'W' ? ProfileBattleOutcome.Win
                : results[game - 1] == 'L' ? ProfileBattleOutcome.Loss : ProfileBattleOutcome.Draw;
            var entry = Entry("game_" + game.ToString("D2"), game, "original",
                game % 2 == 0 ? ids.Reverse().ToArray() : ids, outcome, game % 2, (AiLevel)((game - 1) / 5));
            entry.Turns = game;
            foreach (var fighter in entry.Stats.Fighters)
            {
                if (fighter.StreamerId == "bell") { fighter.DamageDealt = game * 10; fighter.Kos = game % 3; }
                else if (fighter.StreamerId == "enka") { fighter.DamageDealt = 20; fighter.Kos = 1; }
                else if (fighter.StreamerId == "kei") fighter.DamageDealt = 40;
            }
            fixture.Entries.Add(entry);
        }
        var pack = new PackStats { Wins = 12, Losses = 5, Draws = 3 };
        foreach (string id in TeamA) { pack.Picks[id] = 12; pack.PickWins[id] = 6; }
        foreach (string id in TeamB) { pack.Picks[id] = 8; pack.PickWins[id] = 6; }
        pack.Picks["bell"] = 20;
        pack.PickWins["bell"] = 12;
        fixture.Profile.Packs.Add("original", pack);
        fixture.Entries = fixture.Entries.OrderBy(entry => entry.Turns % 3).ThenByDescending(entry => entry.Turns).ToList();
        return fixture;
    }

    [Fact]
    public void TwentyKnownGamesProduceExactStreamerTeamAiRecentAndStreakStatistics()
    {
        var fixture = TwentyGames();
        var stats = new ProfileStats(fixture.Profile, fixture.Entries, new[] { fixture.Db });
        var bell = Assert.Single(stats.ByStreamer(), row => row.StreamerId == "bell");
        Assert.Equal(20L, bell.Games);
        Assert.Equal(12L, bell.Wins);
        Assert.Equal(0.6, bell.WinRate, 10);
        Assert.Equal(20L, bell.SampleGames);
        Assert.Equal(2100L, bell.DamageDealt);
        Assert.Equal(105.0, bell.AverageDamage);
        Assert.Equal(21L, bell.Kos);
        Assert.True(bell.CountsFromProfile);
        Assert.Equal(fixture.Db.Streamer("bell").Name, bell.Name);
        Assert.Equal("bell", stats.ByStreamer()[0].StreamerId);
        var enka = Assert.Single(stats.ByStreamer(), row => row.StreamerId == "enka");
        Assert.Equal(12L, enka.Games);
        Assert.Equal(6L, enka.Wins);
        Assert.Equal(20.0, enka.AverageDamage);
        Assert.Equal(12L, enka.Kos);
        var kei = Assert.Single(stats.ByStreamer(), row => row.StreamerId == "kei");
        Assert.Equal(8L, kei.Games);
        Assert.Equal(6L, kei.Wins);
        Assert.Equal(40.0, kei.AverageDamage);

        var teams = stats.TopTeams(10);
        Assert.Equal(2, teams.Count);
        Assert.Equal(TeamA.OrderBy(id => id, StringComparer.Ordinal), teams[0].StreamerIds);
        Assert.Equal(12L, teams[0].Games);
        Assert.Equal(6L, teams[0].Wins);
        Assert.Equal(4L, teams[0].Losses);
        Assert.Equal(2L, teams[0].Draws);
        Assert.Equal(0.5, teams[0].WinRate);
        Assert.Equal(TeamB.OrderBy(id => id, StringComparer.Ordinal), teams[1].StreamerIds);
        Assert.Equal(8L, teams[1].Games);
        Assert.Equal(6L, teams[1].Wins);
        Assert.Equal(1L, teams[1].Losses);
        Assert.Equal(1L, teams[1].Draws);
        Assert.Equal(0.75, teams[1].WinRate);
        Assert.Single(stats.TopTeams(1));

        var ai = stats.VsAi();
        Assert.Equal(new[] { AiLevel.Easy, AiLevel.Normal, AiLevel.Hard, AiLevel.Expert }, ai.Select(row => row.Level));
        Assert.Equal(new long[] { 2, 3, 2, 5 }, ai.Select(row => row.Wins));
        Assert.Equal(new long[] { 2, 1, 2, 0 }, ai.Select(row => row.Losses));
        Assert.Equal(new long[] { 1, 1, 1, 0 }, ai.Select(row => row.Draws));
        Assert.All(ai, row => Assert.Equal(5L, row.Games));
        Assert.Equal(0.4, ai[0].WinRate);
        Assert.Equal(new[] { "game_20", "game_19", "game_18" }, stats.Recent(3).Select(row => row.Id));
        Assert.Equal(0, stats.Recent(1)[0].PlayerSide);
        Assert.Equal(ProfileBattleOutcome.Win, stats.Recent(1)[0].Outcome);
        Assert.Equal(20, stats.Recent(1)[0].Turns);
        Assert.Equal(5L, stats.Streaks().CurrentWins);
        Assert.Equal(0L, stats.Streaks().CurrentLosses);
        Assert.Equal(5L, stats.Streaks().LongestWins);
    }

    [Fact]
    public void LifetimeProfileCountsAreAuthoritativeAndDamageUsesOnlyDetailedSamples()
    {
        var profile = new Profile();
        profile.Packs["original"] = new PackStats
        {
            Picks = new Dictionary<string, int> { ["bell"] = 100, ["shiori"] = 0 },
            PickWins = new Dictionary<string, int> { ["bell"] = 60, ["shiori"] = 0 }
        };
        var win = Entry("one", 1, "original", new[] { "bell", "enka", "shiori" }, ProfileBattleOutcome.Win);
        win.Stats.Fighters[0].DamageDealt = 10;
        win.Stats.Fighters[0].Kos = 1;
        var loss = Entry("two", 2, "original", new[] { "bell" }, ProfileBattleOutcome.Loss);
        loss.Stats.Fighters[0].DamageDealt = 40;
        loss.Stats.Fighters[0].Kos = 2;
        var rows = new ProfileStats(profile, new[] { win, loss }).ByStreamer();
        var bell = Assert.Single(rows, row => row.StreamerId == "bell");
        Assert.Equal(100L, bell.Games);
        Assert.Equal(60L, bell.Wins);
        Assert.Equal(2L, bell.SampleGames);
        Assert.Equal(25.0, bell.AverageDamage);
        Assert.Equal(3L, bell.Kos);
        Assert.True(bell.CountsFromProfile);
        var fallback = Assert.Single(rows, row => row.StreamerId == "enka");
        Assert.Equal(1L, fallback.Games);
        Assert.Equal(1L, fallback.Wins);
        Assert.False(fallback.CountsFromProfile);
        var explicitZero = Assert.Single(rows, row => row.StreamerId == "shiori");
        Assert.True(explicitZero.CountsFromProfile);
        Assert.Equal(0L, explicitZero.Games);
        Assert.Equal(1L, explicitZero.SampleGames);
    }

    [Fact]
    public void ProfileOnlyRowsHaveNoInventedDamageSamplesAndEmptyInputsReturnEmptyResults()
    {
        var empty = new ProfileStats(null, null);
        Assert.Empty(empty.ByStreamer());
        Assert.Empty(empty.TopTeams(10));
        Assert.Empty(empty.VsAi());
        Assert.Empty(empty.Recent(10));
        Assert.Equal(0L, empty.Streaks().CurrentWins);
        Assert.Equal(0L, empty.Streaks().CurrentLosses);
        Assert.Equal(0L, empty.Streaks().LongestWins);
        var profile = new Profile();
        profile.Packs["original"] = new PackStats
        {
            Picks = new Dictionary<string, int> { ["bell"] = 9 },
            PickWins = new Dictionary<string, int> { ["bell"] = 3 }
        };
        var row = Assert.Single(new ProfileStats(profile, Array.Empty<ReplayEntry>()).ByStreamer());
        Assert.Equal(9L, row.Games);
        Assert.Equal(1.0 / 3, row.WinRate, 10);
        Assert.Equal(0L, row.SampleGames);
        Assert.Equal(0L, row.DamageDealt);
        Assert.Equal(0L, row.Kos);
    }

    [Fact]
    public void EqualIdsInDifferentPacksRemainSeparateAndMissingNamesAreNotGuessedAcrossPacks()
    {
        var db = BattleTests.LoadPack("original");
        var profile = new Profile();
        profile.Packs["original"] = new PackStats
        {
            Picks = new Dictionary<string, int> { ["bell"] = 9, ["removed"] = 2 },
            PickWins = new Dictionary<string, int> { ["bell"] = 7 }
        };
        profile.Packs["unloaded"] = new PackStats
        {
            Picks = new Dictionary<string, int> { ["bell"] = 4 },
            PickWins = new Dictionary<string, int> { ["bell"] = 1 }
        };
        var entries = new[]
        {
            Entry("loaded", 1, "original", new[] { "bell" }, ProfileBattleOutcome.Win),
            Entry("unknown", 2, "unloaded", new[] { "bell" }, ProfileBattleOutcome.Loss)
        };
        entries[0].Stats.Fighters[0].DamageDealt = 1;
        entries[1].Stats.Fighters[0].DamageDealt = 100;
        var stats = new ProfileStats(profile, entries, new[] { db });
        var rows = stats.ByStreamer();
        Assert.Equal(3, rows.Count);
        var loaded = Assert.Single(rows, row => row.PackId == "original" && row.StreamerId == "bell");
        var unknown = Assert.Single(rows, row => row.PackId == "unloaded");
        Assert.Equal(9L, loaded.Games);
        Assert.Equal(4L, unknown.Games);
        Assert.Equal(1.0, loaded.AverageDamage);
        Assert.Equal(100.0, unknown.AverageDamage);
        Assert.Equal(db.Streamer("bell").Name, loaded.Name);
        Assert.Contains("bell", unknown.Name);
        Assert.Contains("未知", unknown.Name);
        Assert.DoesNotContain("已下架", unknown.Name);
        Assert.Equal("已下架", Assert.Single(rows, row => row.StreamerId == "removed").Name);
        Assert.Equal(2, stats.TopTeams(10).Count);
    }

    [Fact]
    public void TeamGroupingIgnoresOrderButDoesNotCollideWhenIdsContainSeparators()
    {
        string[] first = { "a|b", "c", "d", "e", "f", "g" };
        string[] second = { "a", "b|c", "d", "e", "f", "g" };
        var entries = new[]
        {
            Entry("one", 1, "pack", first, ProfileBattleOutcome.Win),
            Entry("two", 2, "pack", first.Reverse(), ProfileBattleOutcome.Draw),
            Entry("three", 3, "pack", second, ProfileBattleOutcome.Loss)
        };
        var teams = new ProfileStats(null, entries).TopTeams(10);
        Assert.Equal(2, teams.Count);
        Assert.Equal(first.OrderBy(id => id, StringComparer.Ordinal), teams[0].StreamerIds);
        Assert.Equal(2L, teams[0].Games);
        Assert.Equal(0.5, teams[0].WinRate);
        Assert.Equal(second.OrderBy(id => id, StringComparer.Ordinal), teams[1].StreamerIds);
        Assert.Equal(1L, teams[1].Games);
    }

    [Fact]
    public void UnknownPerspectiveAndDrawsBreakStreaksWithoutInventingAiResults()
    {
        var entries = new List<ReplayEntry>();
        for (int game = 0; game < 5; game++)
            entries.Add(Entry("game_" + game, game, "pack", new[] { "a" }, ProfileBattleOutcome.Win, ai: AiLevel.Easy));
        entries[2].Stats = null;
        var stats = new ProfileStats(null, entries);
        Assert.Equal(5, stats.Recent(10).Count);
        var unknown = Assert.Single(stats.Recent(10), row => row.Id == "game_2");
        Assert.Null(unknown.PlayerSide);
        Assert.Null(unknown.Outcome);
        Assert.Null(unknown.OpponentAiLevel);
        Assert.Equal(4L, Assert.Single(stats.VsAi()).Wins);
        Assert.Equal(4L, Assert.Single(stats.TopTeams(10)).Games);
        Assert.Equal(2L, stats.Streaks().CurrentWins);
        Assert.Equal(2L, stats.Streaks().LongestWins);
        var newestUnknown = Entry("unknown_new", 5, "pack", new[] { "a" }, ProfileBattleOutcome.Win);
        newestUnknown.Stats = null;
        entries.Add(newestUnknown);
        Assert.Equal(0L, new ProfileStats(null, entries).Streaks().CurrentWins);
        entries.RemoveAt(entries.Count - 1);
        entries.Add(Entry("draw", 5, "pack", new[] { "a" }, ProfileBattleOutcome.Draw));
        var drawn = new ProfileStats(null, entries);
        Assert.Equal(0L, drawn.Streaks().CurrentWins);
        Assert.Equal(0L, drawn.Streaks().CurrentLosses);
        Assert.Equal(0.8, Assert.Single(drawn.TopTeams(10)).WinRate);
        entries.Add(Entry("loss_a", 6, "pack", new[] { "a" }, ProfileBattleOutcome.Loss));
        entries.Add(Entry("loss_b", 7, "pack", new[] { "a" }, ProfileBattleOutcome.Loss));
        var lost = new ProfileStats(null, entries);
        Assert.Equal(2L, lost.Streaks().CurrentLosses);
        Assert.Equal(0L, lost.Streaks().CurrentWins);
        Assert.Equal(2L, lost.Streaks().LongestWins);
        Assert.Equal(4L, Assert.Single(lost.VsAi()).Games);
    }

    [Fact]
    public void HumanAndLegacyGamesDoNotBecomeAiGamesAndCorruptEntriesAreExcluded()
    {
        var human = Entry("human", 1, "pack", new[] { "a" }, ProfileBattleOutcome.Win);
        var legacy = Entry("legacy", 2, "pack", new[] { "a" }, ProfileBattleOutcome.Win);
        legacy.Stats = null;
        var corrupt = Entry("corrupt", 3, "pack", new[] { "a" }, ProfileBattleOutcome.Loss, ai: AiLevel.Expert);
        corrupt.IsCorrupt = true;
        var stats = new ProfileStats(null, new[] { human, legacy, corrupt, null });
        Assert.Empty(stats.VsAi());
        Assert.Equal(2, stats.Recent(10).Count);
        Assert.Equal(1L, Assert.Single(stats.ByStreamer()).Games);
        Assert.Equal(0L, stats.Streaks().CurrentWins);
    }

    [Fact]
    public void ConstructionAndAllQueriesPreserveSourcesAndOutputsStayDetachedAfterSourceMutation()
    {
        var fixture = TwentyGames();
        string profile = fixture.Profile.ToJson(), entries = Json(fixture.Entries), content = fixture.Db.ContentHash();
        var stats = new ProfileStats(fixture.Profile, fixture.Entries, new[] { fixture.Db });
        string snapshot = Dump(stats);
        Assert.Equal(profile, fixture.Profile.ToJson());
        Assert.Equal(entries, Json(fixture.Entries));
        Assert.Equal(content, fixture.Db.ContentHash());
        fixture.Profile.Packs.Clear();
        fixture.Entries[0].Names[0] = "修改名称";
        fixture.Entries[0].Teams[0].Clear();
        fixture.Entries[0].Stats.PlayerSide = 1 - fixture.Entries[0].Stats.PlayerSide;
        fixture.Entries[0].Stats.Fighters[0].DamageDealt = int.MaxValue;
        fixture.Entries[0].Stats.Fighters.Clear();
        fixture.Entries.Clear();
        fixture.Db.Streamer("bell").Name = "后来改名";
        fixture.Db.Streamers.Clear();
        Assert.Equal(snapshot, Dump(stats));
        AssertReadOnly(stats.ByStreamer());
        AssertReadOnly(stats.TopTeams(2));
        AssertReadOnly(stats.TopTeams(2)[0].StreamerIds);
        AssertReadOnly(stats.TopTeams(2)[0].Names);
        AssertReadOnly(stats.VsAi());
        AssertReadOnly(stats.Recent(2));
        AssertReadOnly(stats.Recent(2)[0].Names);
    }

    [Fact]
    public void DateAndIdOrderIsStableAndNonpositiveLimitsReturnEmptyLists()
    {
        var first = Entry("a", 1, "pack", new[] { "a" }, ProfileBattleOutcome.Win);
        var second = Entry("b", 1, "pack", new[] { "a" }, ProfileBattleOutcome.Loss);
        second.Date = first.Date.ToOffset(TimeSpan.FromHours(8));
        var stats = new ProfileStats(null, new[] { first, second });
        Assert.Equal(new[] { "b", "a" }, stats.Recent(10).Select(row => row.Id));
        Assert.Equal(1L, stats.Streaks().CurrentLosses);
        foreach (int n in new[] { int.MinValue, -1, 0 })
        {
            Assert.Empty(stats.Recent(n));
            Assert.Empty(stats.TopTeams(n));
        }
        Assert.Equal(Dump(stats), Dump(new ProfileStats(null, new[] { second, first })));
    }

    [Fact]
    public void DamageAndKnockoutTotalsUseLongWithoutIntegerOverflow()
    {
        var entries = new[]
        {
            Entry("one", 1, "pack", new[] { "a" }, ProfileBattleOutcome.Win),
            Entry("two", 2, "pack", new[] { "a" }, ProfileBattleOutcome.Draw)
        };
        foreach (var entry in entries)
        {
            entry.Stats.Fighters[0].DamageDealt = int.MaxValue;
            entry.Stats.Fighters[0].Kos = int.MaxValue;
        }
        var row = Assert.Single(new ProfileStats(null, entries).ByStreamer());
        Assert.Equal(2L * int.MaxValue, row.DamageDealt);
        Assert.Equal(2L * int.MaxValue, row.Kos);
        Assert.Equal((double)int.MaxValue, row.AverageDamage);
        Assert.Equal(0.5, row.WinRate);
    }

    static ReplayEntry Entry(string id, int minute, string pack, IEnumerable<string> ids,
        ProfileBattleOutcome outcome, int side = 0, AiLevel? ai = null)
    {
        var team = ids.ToList();
        return new ReplayEntry
        {
            Id = id, Date = Epoch.AddMinutes(minute), PackId = pack, Names = new[] { "甲方", "乙方" },
            Teams = new IList<string>[] { team.ToList(), team.ToList() },
            Winner = outcome == ProfileBattleOutcome.Draw ? -1 : outcome == ProfileBattleOutcome.Win ? side : 1 - side,
            Turns = 10,
            Stats = new ReplayStats
            {
                PlayerSide = side, OpponentAiLevel = ai,
                Fighters = team.Select(streamer => new ReplayFighterStats { StreamerId = streamer }).ToList()
            }
        };
    }

    static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true });

    static string Dump(ProfileStats stats) => Json(new
    {
        Streamers = stats.ByStreamer(), Teams = stats.TopTeams(int.MaxValue), Ai = stats.VsAi(),
        Recent = stats.Recent(int.MaxValue), Streaks = stats.Streaks()
    });

    static void AssertReadOnly<T>(IReadOnlyList<T> values)
    {
        if (values is IList<T> mutable)
        {
            Assert.True(mutable.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => mutable.Clear());
        }
    }
}
