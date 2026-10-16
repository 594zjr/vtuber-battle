using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using StreamDuel.Balance;
using StreamDuel.Core;
using Xunit;

public class T130_BalanceCompareTests
{
    [Fact]
    public void MeasureCountsRealMirrorAppearancesAndPreservesLegacyRatesAndContent()
    {
        var db = BattleTests.LoadPack("original");
        db.Streamers.RemoveRange(6, db.Streamers.Count - 6);
        string hash = db.ContentHash();
        var measured = BalanceCheck.Measure(db, 5, 130, AiLevel.Easy);
        Assert.Equal(3, measured.Battles);
        Assert.Equal(12 * measured.Battles, measured.Streamers.Sum(item => item.Games));
        Assert.Equal(db.Streamers.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal), measured.Streamers.Select(item => item.Id));
        Assert.All(measured.Streamers, item =>
        {
            Assert.Equal(6, item.Games);
            Assert.Equal(item.Games, 2 * item.Wins + item.Draws);
            Assert.Equal(0.5, item.WinRate);
            var saved = BalanceStreamerSnapshot.FromStats(item, db.Streamer(item.Id).Name);
            Assert.Equal(item.Id, saved.Id);
            Assert.Equal(item.Games, saved.Games);
            Assert.Equal(item.Wins, saved.Wins);
            Assert.Equal(item.Draws, saved.Draws);
            var expected = Interval(item.Games, item.Wins, item.Draws);
            Assert.Equal(expected.Low, saved.Low95, 12);
            Assert.Equal(expected.High, saved.High95, 12);
        });
        var legacy = BalanceCheck.WinRates(db, 5, 130, AiLevel.Easy);
        Assert.All(measured.Streamers, item => Assert.Equal(item.WinRate, legacy[item.Id]));
        Assert.Equal(hash, db.ContentHash());
        var list = Assert.IsAssignableFrom<IList<BalanceStreamerStats>>(measured.Streamers);
        Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
    }

    [Fact]
    public void WilsonIntervalsHaveKnownValuesAndHalfWinDrawsRoundTrip()
    {
        var snapshot = Snapshot(100, 0, 100);
        var row = snapshot.Packs[0].Streamers[0];
        Assert.Equal(0.5, row.WinRate);
        Assert.Equal(0.4038315303659956, row.Low95, 12);
        Assert.Equal(0.5961684696340044, row.High95, 12);
        var loaded = BalanceSnapshot.FromJson(snapshot.ToJson()).Packs[0].Streamers[0];
        Assert.Equal(100, loaded.Draws);
        Assert.Equal(0, loaded.Wins);
        Assert.Equal(row.Low95, loaded.Low95);
        Assert.Equal(row.High95, loaded.High95);
    }

    [Fact]
    public void SmallSampleZeroAndFullWinsProduceValidEndpointIntervalsFromRealStats()
    {
        var db = BattleTests.LoadPack("original");
        foreach (var streamer in db.Streamers)
        {
            streamer.Type = "song";
            streamer.Type2 = null;
            foreach (string stat in ContentDb.StatKeys) streamer.Stats[stat] = 10;
        }
        foreach (var persona in db.Personas.Values)
        {
            persona.Code = "T_LOWHP_POWER";
            persona.Params.Clear();
            persona.Params["below"] = 0;
            persona.Params["mul"] = 1;
        }
        foreach (var skill in db.Skills.Values)
        {
            skill.Cat = "interact";
            skill.Type = "song";
            skill.Power = 10;
            skill.Pp = 1000;
            skill.Acc = 100;
            skill.Prio = 0;
            skill.Effects.Clear();
        }
        foreach (string stat in ContentDb.StatKeys) db.Streamers[0].Stats[stat] = 1000000;
        var run = BalanceCheck.Measure(db, 1, 13001, AiLevel.Easy);
        Assert.Equal(1, run.Battles);
        var rows = run.Streamers.Select(item => BalanceStreamerSnapshot.FromStats(item, item.Id)).ToList();
        Assert.Equal(6, rows.Count(item => item.WinRate == 0));
        Assert.Equal(6, rows.Count(item => item.WinRate == 1));
        Assert.All(rows, item =>
        {
            if (item.WinRate == 0) Assert.Equal(0, item.Low95);
            else Assert.Equal(1, item.High95);
        });
        var snapshot = Snapshot();
        snapshot.GamesPerStreamer = 1;
        snapshot.Packs[0].Battles = run.Battles;
        snapshot.Packs[0].Streamers = rows;
        Assert.Equal(snapshot.ToJson(), BalanceSnapshot.FromJson(snapshot.ToJson()).ToJson());
    }

    [Fact]
    public void SignificantChangesAndOutOfRangeAreIndependentAndUseNewRates()
    {
        var old = Snapshot(10000, 5000);
        var newer = Snapshot(10000, 7000);
        var result = BalanceCompare.Compare(old, newer);
        var row = Assert.Single(result.Rows);
        Assert.Equal(0.2, row.Delta.Value, 12);
        Assert.True(row.IsSignificant);
        Assert.True(row.IsOutOfRange);
        Assert.True(result.HasSignificantChanges);
        Assert.True(result.HasOutOfRange);
        Assert.Contains("明显变化", result.ToText());
        Assert.Contains("超出平衡范围", result.ToText());

        var recovered = BalanceCompare.Compare(newer, old);
        Assert.True(Assert.Single(recovered.Rows).IsSignificant);
        Assert.False(recovered.HasOutOfRange);
    }

    [Fact]
    public void OverlappingIntervalsSuppressLargeSmallSampleChange()
    {
        var result = BalanceCompare.Compare(Snapshot(20, 10), Snapshot(20, 12));
        var row = Assert.Single(result.Rows);
        Assert.Equal(0.1, row.Delta.Value, 12);
        Assert.True(row.Old.High95 > row.New.Low95);
        Assert.False(row.IsSignificant);
        Assert.False(result.HasSignificantChanges);
    }

    [Fact]
    public void TouchingValidatedIntervalsAreNotStrictlySeparated()
    {
        const long count = 1000000000000L;
        var old = Snapshot(count, count / 2);
        var newer = Snapshot(count, 500001959964L);
        var before = old.Packs[0].Streamers[0];
        var after = newer.Packs[0].Streamers[0];
        // 极大样本的两个合法端点相差不足持久化数值容差，保留恰好相触的存储端点。
        Assert.True(Math.Abs(after.Low95 - before.High95) < 1e-12);
        after.Low95 = before.High95;
        newer.Validate();
        var row = Assert.Single(BalanceCompare.Compare(old, newer, 0).Rows);
        Assert.Equal(row.Old.High95, row.New.Low95);
        Assert.True(row.Delta > 0);
        Assert.False(row.IsSignificant);
    }

    [Theory]
    [InlineData(5500, false)]
    [InlineData(5501, true)]
    [InlineData(4500, false)]
    [InlineData(4499, true)]
    public void FivePercentagePointThresholdIsStrictInBothDirections(long wins, bool significant)
    {
        var row = Assert.Single(BalanceCompare.Compare(Snapshot(10000, 5000), Snapshot(10000, wins)).Rows);
        Assert.True(row.Old.High95 < row.New.Low95 || row.New.High95 < row.Old.Low95);
        Assert.Equal(significant, row.IsSignificant);
    }

    [Theory]
    [InlineData(3500, false)]
    [InlineData(6500, false)]
    [InlineData(3499, true)]
    [InlineData(6501, true)]
    public void BalanceRangeIncludesBothLimits(long wins, bool outside)
    {
        Assert.Equal(outside, Assert.Single(BalanceCompare.Compare(Snapshot(10000, 5000), Snapshot(10000, wins)).Rows).IsOutOfRange);
    }

    [Theory]
    [InlineData(3500, 0.3499999999999999)]
    [InlineData(6500, 0.6500000000000001)]
    public void ValidSerializationRoundingDoesNotPushBoundaryCountsOutside(long wins, double storedRate)
    {
        var newer = Snapshot(10000, wins);
        newer.Packs[0].Streamers[0].WinRate = storedRate;
        newer.Validate();
        Assert.False(Assert.Single(BalanceCompare.Compare(Snapshot(), newer).Rows).IsOutOfRange);
    }

    [Fact]
    public void SelfComparisonHasNoChangeAndDoesNotMutateSnapshotOrderOrData()
    {
        var snapshot = Snapshot();
        snapshot.Packs[0].Streamers.Add(Entry("before", 10000, 5000));
        snapshot.Packs.Add(Pack("a-pack", Entry("another", 10000, 5000)));
        var order = snapshot.Packs.Select(pack => pack.PackId).ToArray();
        var ids = snapshot.Packs[0].Streamers.Select(item => item.Id).ToArray();
        string before = snapshot.ToJson();
        var compared = BalanceCompare.Compare(snapshot, snapshot);
        Assert.False(compared.ConditionsDiffer);
        Assert.False(compared.HasSignificantChanges);
        Assert.False(compared.HasOutOfRange);
        Assert.All(compared.Rows, row => Assert.Equal(0.0, row.Delta.Value));
        Assert.Equal(before, snapshot.ToJson());
        Assert.Equal(order, snapshot.Packs.Select(pack => pack.PackId));
        Assert.Equal(ids, snapshot.Packs[0].Streamers.Select(item => item.Id));
        Assert.Equal(new[] { "a-pack/another", "pack/alpha", "pack/before" }, compared.Rows.Select(row => row.PackId + "/" + row.Id));
    }

    [Theory]
    [InlineData("requested")]
    [InlineData("seed")]
    [InlineData("level")]
    [InlineData("aiVersion")]
    [InlineData("battles")]
    [InlineData("appearances")]
    public void EverySamplingConditionDifferenceIsDisclosedBeforeTheTable(string change)
    {
        var old = Snapshot();
        var newer = Snapshot();
        switch (change)
        {
            case "requested": newer.GamesPerStreamer++; break;
            case "seed": newer.Seed++; break;
            case "level": newer.Level = "hard"; break;
            case "aiVersion": newer.AiVersion = "different-ai"; break;
            case "battles": newer.Packs[0].Battles++; break;
            case "appearances": newer.Packs[0].Streamers[0] = Entry("alpha", 12000, 6000); break;
        }
        var result = BalanceCompare.Compare(old, newer);
        Assert.True(result.ConditionsDiffer);
        Assert.StartsWith("条件不同，对比仅供参考", result.ToText());
    }

    [Fact]
    public void ContentVersionAndDisplayNameChangesAreComparableWithoutClaimingSamplingChanged()
    {
        var old = Snapshot();
        var newer = Snapshot();
        newer.Packs[0].ContentHash = "changed-content";
        newer.Packs[0].Streamers[0].Name = "改名后的主播";
        var result = BalanceCompare.Compare(old, newer);
        Assert.False(result.ConditionsDiffer);
        Assert.Equal(0.0, Assert.Single(result.Rows).Delta.Value);
    }

    [Fact]
    public void UnionRetainsAddedAndRemovedEntriesWithoutInventingDeltas()
    {
        var old = Snapshot();
        old.Packs[0].Streamers.Add(Entry("removed", 10000, 5000));
        old.Packs.Add(Pack("old-pack", Entry("same-id", 10000, 5000)));
        var newer = Snapshot();
        newer.Packs[0].Streamers.Add(Entry("added", 10000, 7000));
        newer.Packs.Add(Pack("new-pack", Entry("same-id", 10000, 5000)));
        var result = BalanceCompare.Compare(old, newer);
        Assert.Equal(new[] { "new-pack/same-id", "old-pack/same-id", "pack/added", "pack/alpha", "pack/removed" }, result.Rows.Select(row => row.PackId + "/" + row.Id));
        foreach (var row in result.Rows.Where(row => row.Old == null || row.New == null))
        {
            Assert.Null(row.Delta);
            Assert.False(row.IsSignificant);
        }
        Assert.True(Assert.Single(result.Rows, row => row.Id == "added").IsOutOfRange);
        Assert.Contains("新增", result.ToText());
        Assert.Contains("移除", result.ToText());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void InvalidThresholdIsRejected(double threshold)
    {
        var error = Assert.ThrowsAny<ArgumentException>(() => BalanceCompare.Compare(Snapshot(), Snapshot(), threshold));
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
    }

    [Fact]
    public void SerializationIsCanonicalAndLoadingIsReadOnly()
    {
        var snapshot = Snapshot();
        snapshot.Packs[0].Streamers.Add(Entry("aardvark", 10000, 5000));
        string json = snapshot.ToJson();
        var shuffled = Snapshot();
        shuffled.Packs[0].Streamers.Insert(0, Entry("aardvark", 10000, 5000));
        Assert.Equal(json, shuffled.ToJson());
        string path = Path.Combine(Scratch(), "result.json");
        File.WriteAllText(path, json);
        var timestamp = File.GetLastWriteTimeUtc(path);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var loaded = BalanceSnapshot.Load(path);
            Assert.Equal(json, loaded.ToJson());
            Assert.False(BalanceCompare.Compare(snapshot, loaded).HasSignificantChanges);
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Assert.Equal(json, File.ReadAllText(path));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)));
    }

    [Theory]
    [InlineData("syntax")]
    [InlineData("duplicate-root")]
    [InlineData("duplicate-nested")]
    [InlineData("nan")]
    [InlineData("version")]
    [InlineData("missing-seed")]
    [InlineData("wrong-rate")]
    [InlineData("wrong-interval")]
    [InlineData("negative-wins")]
    [InlineData("too-many-results")]
    [InlineData("below-request")]
    [InlineData("above-mirror-limit")]
    [InlineData("duplicate-streamer")]
    [InlineData("duplicate-pack")]
    public void CorruptSnapshotsAreRejectedWithChineseErrorsWithoutRewriting(string kind)
    {
        string json = Corrupt(kind, Snapshot().ToJson());
        string path = Path.Combine(Scratch(), "invalid.json");
        File.WriteAllText(path, json);
        var timestamp = File.GetLastWriteTimeUtc(path);
        var error = Assert.Throws<FormatException>(() => BalanceSnapshot.Load(path));
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
        Assert.Equal(json, File.ReadAllText(path));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
    }

    static string Corrupt(string kind, string json)
    {
        if (kind == "syntax") return "{不是有效 JSON";
        if (kind == "duplicate-root") return "{\"version\":1," + json.TrimStart().Substring(1);
        if (kind == "nan") return json.Replace("\"winRate\": 0.5", "\"winRate\": NaN").Replace("\"winRate\":0.5", "\"winRate\":NaN");
        if (kind == "duplicate-nested") return json.Replace("\"id\":", "\"id\":\"duplicate\",\"id\":");
        var node = JsonNode.Parse(json);
        var pack = node["packs"][0];
        var row = pack["streamers"][0];
        switch (kind)
        {
            case "version": node["version"] = 2; break;
            case "missing-seed": node.AsObject().Remove("seed"); break;
            case "wrong-rate": row["winRate"] = 0.8; break;
            case "wrong-interval": row["low95"] = 0.1; break;
            case "negative-wins": row["wins"] = -1; break;
            case "too-many-results": row["draws"] = 10000; break;
            case "below-request": node["gamesPerStreamer"] = 10001; break;
            case "above-mirror-limit": pack["battles"] = 1; break;
            case "duplicate-streamer": pack["streamers"].AsArray().Add(row.DeepClone()); break;
            case "duplicate-pack": node["packs"].AsArray().Add(pack.DeepClone()); break;
        }
        return node.ToJsonString();
    }

    static BalanceSnapshot Snapshot(long games = 10000, long wins = 5000, long draws = 0)
    {
        return new BalanceSnapshot
        {
            Version = 1, GamesPerStreamer = 2, Seed = 130, Level = "easy", AiVersion = "test-ai",
            Packs = new List<BalancePackSnapshot> { Pack("pack", Entry("alpha", games, wins, draws)) }
        };
    }

    static BalancePackSnapshot Pack(string id, params BalanceStreamerSnapshot[] streamers)
    {
        return new BalancePackSnapshot { PackId = id, ContentHash = "test-content", Battles = streamers.Max(item => item.Games), Streamers = streamers.ToList() };
    }

    static BalanceStreamerSnapshot Entry(string id, long games, long wins, long draws = 0)
    {
        var interval = Interval(games, wins, draws);
        return new BalanceStreamerSnapshot { Id = id, Name = "测试主播" + id, Games = games, Wins = wins, Draws = draws,
            WinRate = (wins + draws / 2.0) / games, Low95 = interval.Low, High95 = interval.High };
    }

    static (double Low, double High) Interval(long games, long wins, long draws)
    {
        const double z = 1.959963984540054;
        double p = (wins + draws / 2.0) / games;
        double denominator = 1 + z * z / games;
        double middle = (p + z * z / (2 * games)) / denominator;
        double margin = z * Math.Sqrt(p * (1 - p) / games + z * z / (4 * (double)games * games)) / denominator;
        return (Math.Max(0, middle - margin), Math.Min(1, middle + margin));
    }

    static string Scratch()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Engine", "StreamDuel.Core.csproj"))) directory = directory.Parent;
        if (directory == null) throw new InvalidOperationException("找不到当前工作树，拒绝把测试产物写到外部目录。");
        string path = Path.Combine(directory.FullName, "Engine.Tests", "obj", "t130-compare-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
