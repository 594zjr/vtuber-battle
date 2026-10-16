using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T93_ReplayGoldenTests
{
    const string BaselineCommit = "a5e3360147312ed7601f39e28dec51547e80f674";
    const string ManifestSha256 = "deccba7842f6387e43b9d3b8f85ee72ff4e5e892c85a06469fc059b6307fdca5";
    static readonly string[] Ids =
    {
        "ordinary-v2", "lead-v2", "ban-lead-v2", "modifiers-v2",
        "series-game-1-v2", "series-game-2-v2", "series-game-3-v2",
        "network-v2", "fan-v2", "wave2-v2", "legacy-v1"
    };

    // 从源码位置定位，避免工作目录不同或正式内容包调平衡时改变历史样例。
    static string DirectoryPath([CallerFilePath] string file = "")
    {
        return Path.Combine(Path.GetDirectoryName(file), "Golden", "replays");
    }

    static string PathFor(string file)
    {
        return Path.Combine(DirectoryPath(), file.Replace('/', Path.DirectorySeparatorChar));
    }

    static JsonDocument Manifest()
    {
        byte[] bytes = File.ReadAllBytes(PathFor("manifest.json"));
        Assert.Equal(ManifestSha256, Sha256(bytes));
        return JsonDocument.Parse(bytes);
    }

    static JsonElement Entry(JsonElement manifest, string id)
    {
        return manifest.GetProperty("replays").EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);
    }

    static ContentDb Load(string packId)
    {
        Assert.Contains(packId, new[] { "original", "fan_asoul_vr", "original_wave2" });
        return ContentDb.FromJson(File.ReadAllText(PathFor("content/StreamDuelRules.json")),
            File.ReadAllText(PathFor("content/" + packId + ".json")));
    }

    static string Sha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    static void Keys(JsonElement item, params string[] expected)
    {
        Assert.Equal(expected.OrderBy(key => key, StringComparer.Ordinal),
            item.EnumerateObject().Select(property => property.Name).OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void FrozenManifestAndContentSnapshotsAreCompleteAndUnchanged()
    {
        using var document = Manifest();
        var root = document.RootElement;
        Keys(root, "schemaVersion", "baselineCommit", "rules", "packs", "replays", "series");
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(BaselineCommit, root.GetProperty("baselineCommit").GetString());
        Assert.Equal(Ids, root.GetProperty("replays").EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(Ids.Select(id => id + ".json").Append("manifest.json").OrderBy(file => file),
            Directory.GetFiles(DirectoryPath(), "*.json").Select(Path.GetFileName).OrderBy(file => file));

        var snapshots = root.GetProperty("packs").EnumerateArray().Prepend(root.GetProperty("rules")).ToArray();
        Assert.Equal(new[] { "content/StreamDuelRules.json", "content/original.json", "content/fan_asoul_vr.json", "content/original_wave2.json" },
            snapshots.Select(item => item.GetProperty("file").GetString()));
        foreach (var snapshot in snapshots)
        {
            Keys(snapshot, "file", "sha256");
            string expected = snapshot.GetProperty("sha256").GetString();
            Assert.Matches("^[0-9a-f]{64}$", expected);
            Assert.Equal(expected, Sha256(File.ReadAllBytes(PathFor(snapshot.GetProperty("file").GetString()))));
        }
        Assert.Equal(snapshots.Select(item => Path.GetFileName(item.GetProperty("file").GetString())).OrderBy(file => file),
            Directory.GetFiles(PathFor("content"), "*.json").Select(Path.GetFileName).OrderBy(file => file));
        foreach (string packId in new[] { "original", "fan_asoul_vr", "original_wave2" }) Load(packId).Validate();
        Assert.Equal(new[] { "ban", "fan-pack", "lead", "legacy-v1", "modifiers", "network", "ordinary", "series-game", "wave2-pack" },
            root.GetProperty("replays").EnumerateArray().Select(item => item.GetProperty("category").GetString()).Distinct().OrderBy(value => value));
    }

    [Theory]
    [InlineData("ordinary-v2")]
    [InlineData("lead-v2")]
    [InlineData("ban-lead-v2")]
    [InlineData("modifiers-v2")]
    [InlineData("series-game-1-v2")]
    [InlineData("series-game-2-v2")]
    [InlineData("series-game-3-v2")]
    [InlineData("network-v2")]
    [InlineData("fan-v2")]
    [InlineData("wave2-v2")]
    [InlineData("legacy-v1")]
    public void EveryFrozenReplayFinishesAtItsOriginalHash(string id)
    {
        using var document = Manifest();
        var entry = Entry(document.RootElement, id);
        Keys(entry, "id", "file", "category", "policy", "seriesId", "game", "sha256", "recordVersion", "packId",
            "contentHash", "seed", "finalStateHash", "winner", "eventCount", "turnCount", "networkProtocolVersion", "peerHashesVerified");
        Assert.Equal(id + ".json", entry.GetProperty("file").GetString());
        byte[] bytes = File.ReadAllBytes(PathFor(id + ".json"));
        Assert.Equal(entry.GetProperty("sha256").GetString(), Sha256(bytes));
        var record = BattleRecord.FromJson(System.Text.Encoding.UTF8.GetString(bytes));
        var db = Load(entry.GetProperty("packId").GetString());
        string contentHash = entry.GetProperty("contentHash").GetString();
        Assert.Equal(contentHash, db.ContentHash());
        Assert.Equal(db.PackId, record.PackId);
        Assert.Equal(entry.GetProperty("recordVersion").GetInt32(), record.Version);
        Assert.Equal(entry.GetProperty("seed").GetUInt32(), record.Seed);
        Assert.Equal(entry.GetProperty("eventCount").GetInt32(), record.Events.Count);
        Assert.Equal(entry.GetProperty("turnCount").GetInt32(), record.Events.Count(item => item.Type == BattleRecordEventType.Turn));
        Assert.InRange(entry.GetProperty("turnCount").GetInt32(), 1, Battle.TurnCap);
        Assert.All(record.Teams, team => Assert.Equal(6, team.Count));
        if (record.Version >= 2) Assert.Equal(contentHash, record.ContentHash);
        else Assert.Null(record.ContentHash);

        string unchangedRecord = record.ToJson();
        var compatibility = Replay.Check(db, record);
        Assert.True(compatibility.CanReplay, compatibility.Reason);
        Assert.Equal(string.Empty, compatibility.Reason);
        var replay = Replay.Run(db, record);
        Assert.Equal(Phase.Over, replay.Phase);
        Assert.Equal(entry.GetProperty("winner").GetInt32(), replay.Winner);
        string finalHash = entry.GetProperty("finalStateHash").GetString();
        Assert.Matches("^[0-9a-f]{16}$", finalHash);
        Assert.Equal(finalHash, replay.StateHash().ToString("x16", CultureInfo.InvariantCulture));
        Assert.Equal(record.Seed, replay.Seed);
        Assert.Equal(unchangedRecord, record.ToJson());
        Assert.Equal(contentHash, db.ContentHash());

        var roundTrip = Replay.Run(db, BattleRecord.FromJson(unchangedRecord));
        var bestEffort = Replay.RunBestEffort(db, record);
        var shared = Replay.Run(db, ShareCode.Decode(ShareCode.Encode(record)));
        Assert.Equal(replay.StateHash(), roundTrip.StateHash());
        Assert.Equal(replay.StateHash(), bestEffort.StateHash());
        Assert.Equal(replay.StateHash(), shared.StateHash());
        Assert.Equal(replay.Log.Select(line => line.Node + "|" + line.Text), roundTrip.Log.Select(line => line.Node + "|" + line.Text));
        Assert.Equal(entry.GetProperty("sha256").GetString(), Sha256(File.ReadAllBytes(PathFor(id + ".json"))));
    }

    [Fact]
    public void HistoricalV1ReallyOmitsAllLaterOptionalFieldsAndRngCheckpoints()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(PathFor("legacy-v1.json")));
        Keys(document.RootElement, "seed", "packId", "teams", "events");
        foreach (var item in document.RootElement.GetProperty("events").EnumerateArray())
            Assert.False(item.TryGetProperty("rng", out _));
        var record = BattleRecord.FromJson(document.RootElement.GetRawText());
        Assert.Equal(1, record.Version);
        Assert.Null(record.ContentHash);
        Assert.Null(record.Names);
        Assert.Null(record.Modifiers);
        Assert.False(record.BanOne);
        Assert.False(record.LeadPick);
        Assert.All(record.Events, item => Assert.Null(item.RngState));
        var replay = Replay.Run(Load("original"), record);
        Assert.Equal(new[] { "你", "对手" }, replay.Sides.Select(side => side.Name));
    }

    [Fact]
    public void CategoriesContainTheirClaimedEventsOptionsAndContent()
    {
        var ordinary = BattleRecord.FromJson(File.ReadAllText(PathFor("ordinary-v2.json")));
        Assert.False(ordinary.BanOne);
        Assert.False(ordinary.LeadPick);
        Assert.Null(ordinary.Modifiers);

        var lead = BattleRecord.FromJson(File.ReadAllText(PathFor("lead-v2.json")));
        Assert.True(lead.LeadPick);
        Assert.False(lead.BanOne);
        Assert.Equal(new[] { BattleRecordEventType.Lead, BattleRecordEventType.Lead }, lead.Events.Take(2).Select(item => item.Type));
        Assert.Equal(new[] { 1, 0 }, lead.Events.Take(2).Select(item => item.Side));
        Assert.All(lead.Events.Take(2), item => Assert.True(item.To > 0));

        var ban = BattleRecord.FromJson(File.ReadAllText(PathFor("ban-lead-v2.json")));
        Assert.True(ban.BanOne);
        Assert.True(ban.LeadPick);
        Assert.Equal(new[] { BattleRecordEventType.Ban, BattleRecordEventType.Ban, BattleRecordEventType.Lead, BattleRecordEventType.Lead },
            ban.Events.Take(4).Select(item => item.Type));
        Assert.All(Replay.Run(Load("original"), ban).Sides, side => Assert.Equal(5, side.Team.Count));

        var modifiers = BattleRecord.FromJson(File.ReadAllText(PathFor("modifiers-v2.json")));
        Assert.Equal(new[] { "half_pp", "hype_start", "sudden_death" }, modifiers.Modifiers);
        Assert.True(modifiers.BanOne);
        Assert.True(modifiers.LeadPick);

        Assert.Equal("fan_asoul_vr", BattleRecord.FromJson(File.ReadAllText(PathFor("fan-v2.json"))).PackId);
        Assert.Equal("original_wave2", BattleRecord.FromJson(File.ReadAllText(PathFor("wave2-v2.json"))).PackId);
        using var manifest = Manifest();
        var network = Entry(manifest.RootElement, "network-v2");
        Assert.Equal("loopback-net-session", network.GetProperty("policy").GetString());
        Assert.Equal(3, network.GetProperty("networkProtocolVersion").GetInt32());
        Assert.True(network.GetProperty("peerHashesVerified").GetBoolean());
        foreach (var entry in manifest.RootElement.GetProperty("replays").EnumerateArray())
            if (entry.GetProperty("id").GetString() != "network-v2")
            {
                Assert.Equal(JsonValueKind.Null, entry.GetProperty("networkProtocolVersion").ValueKind);
                Assert.False(entry.GetProperty("peerHashesVerified").GetBoolean());
            }
    }

    [Fact]
    public void ThreeLinkedGamesRetainBothTeamsAndAlternateWinnersWithoutClaimingSeriesSupport()
    {
        using var document = Manifest();
        var series = Assert.Single(document.RootElement.GetProperty("series").EnumerateArray());
        Keys(series, "id", "coverage", "replayIds", "winners", "limitation");
        Assert.Equal("same-teams-three-games", series.GetProperty("id").GetString());
        Assert.Equal("individual-games-only", series.GetProperty("coverage").GetString());
        Assert.Contains("未验证 Series 容器", series.GetProperty("limitation").GetString());
        var ids = series.GetProperty("replayIds").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(new[] { "series-game-1-v2", "series-game-2-v2", "series-game-3-v2" }, ids);
        Assert.Equal(new[] { 0, 1, 0 }, series.GetProperty("winners").EnumerateArray().Select(item => item.GetInt32()));
        var first = BattleRecord.FromJson(File.ReadAllText(PathFor(ids[0] + ".json")));
        for (int game = 0; game < ids.Length; game++)
        {
            var entry = Entry(document.RootElement, ids[game]);
            Assert.Equal("same-teams-three-games", entry.GetProperty("seriesId").GetString());
            Assert.Equal(game + 1, entry.GetProperty("game").GetInt32());
            Assert.Equal(game == 1 ? 1 : 0, entry.GetProperty("winner").GetInt32());
            var record = BattleRecord.FromJson(File.ReadAllText(PathFor(ids[game] + ".json")));
            Assert.Equal(first.Teams[0], record.Teams[0]);
            Assert.Equal(first.Teams[1], record.Teams[1]);
            Assert.Equal(first.Names, record.Names);
            Assert.Equal((uint)(9311 + game), record.Seed);
        }
    }
}
