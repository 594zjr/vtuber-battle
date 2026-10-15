using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T99_VersionGoldenTests
{
    static readonly IList<string>[] Teams =
    {
        new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
        new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
    };

    [Theory]
    [InlineData("pass-draw-120", false, -1, 0, "382710c857b38789",
        "19d04ef0e344642009046c0875f195c75bd17fee1411b38a1ac8811db88a0d0f",
        "回合上限（120 回合）：双方剩余人数、剩余人气比例总和和累计伤害均相同，平局")]
    [InlineData("damage-healed-120", true, 0, 79, "a1122da58724ba3e",
        "2629e2883cee6ee64f241cc2aa23667c98e33ee4e448e0599e680ec6e3c180f4",
        "回合上限（120 回合）：双方剩余人数和剩余人气比例总和相同，按累计伤害判定，甲方 获胜")]
    public void FrozenVersionThreeTurnCapRecordsKeepTheirOutcomeAndReplayState(string scenario, bool damage,
        int winner, int dealt, string finalHash, string fileHash, string finalLog)
    {
        string path = Path.Combine(GoldenDirectory(), "t99-v3", scenario + ".json");
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(fileHash, Sha256(bytes));
        var record = BattleRecord.FromJson(Encoding.UTF8.GetString(bytes));
        var db = FrozenContent();
        Assert.Equal("63a758a4fa663be9", db.ContentHash());
        Assert.Equal(3, record.Version);
        Assert.Equal(99u, record.Seed);
        Assert.Equal(db.ContentHash(), record.ContentHash);
        Assert.Equal(120, record.Events.Count);
        Assert.All(record.Events, entry => Assert.Equal(BattleRecordEventType.Turn, entry.Type));
        Assert.True(Replay.Check(db, record).CanReplay);

        var replayed = Replay.Run(db, record);
        Assert.Equal(3, replayed.RulesVersion);
        Assert.Equal(120, replayed.EffectiveTurnCap);
        Assert.Equal(Phase.Over, replayed.Phase);
        Assert.Equal(120, replayed.Turn);
        Assert.Equal(winner, replayed.Winner);
        Assert.Equal("回合上限", replayed.EndReason);
        Assert.Equal(finalHash, replayed.StateHash().ToString("x16", CultureInfo.InvariantCulture));
        Assert.Equal(finalLog, replayed.Log.Last().Text);
        Assert.Equal(Node.Over, replayed.Log.Last().Node);
        Assert.Equal("turn", replayed.Log.Last().Kind);
        var end = Assert.Single(replayed.Events, entry => entry.Kind == BattleEventKind.End);
        Assert.Equal(Node.Over, end.Node);
        Assert.Equal(winner, end.Side);
        Assert.Equal(replayed.Log.Count - 1, end.LogIndex);
        Assert.Equal(120, end.Turn);

        // 所有人均存活且人气全满，第二份样例只能由历史实际伤害打破平局。
        Assert.All(replayed.Sides, side =>
        {
            Assert.Equal(6, side.Team.Count);
            Assert.All(side.Team, fighter =>
            {
                Assert.False(fighter.Fainted);
                Assert.Equal(fighter.MaxHp, fighter.Hp);
            });
        });
        var summary = BattleSummary.From(replayed);
        Assert.Equal("回合上限", summary.EndReason);
        Assert.Equal(120, summary.Turns);
        Assert.Equal(dealt, summary.Sides[0].Sum(stats => stats.DamageDealt));
        Assert.Equal(0, summary.Sides[1].Sum(stats => stats.DamageDealt));
        Assert.Equal(dealt, replayed.Events.Where(entry => entry.Kind == BattleEventKind.Damage).Sum(entry => entry.Amount));
        Assert.Equal(dealt, replayed.Events.Where(entry => entry.Kind == BattleEventKind.Heal).Sum(entry => entry.Amount));

        var fresh = Fresh(db, damage);
        Assert.Equal(record.ToJson(), fresh.Record.ToJson());
        AssertSameBattle(fresh, replayed);
        AssertSameBattle(replayed, replayed.Clone());
        var cursor = new ReplayCursor(db, record, 7);
        Assert.Equal(120, cursor.MaxTurn);
        cursor.ToEnd();
        AssertSameBattle(replayed, cursor.Current);
        cursor.Seek(119);
        Assert.True(cursor.Next());
        Assert.Single(cursor.EventsOfCurrentStep, entry => entry.Kind == BattleEventKind.End);
        AssertSameBattle(replayed, cursor.Current);
        Assert.Equal(fileHash, Sha256(File.ReadAllBytes(path)));
    }

    static Battle Fresh(ContentDb db, bool damage)
    {
        var battle = new Battle(db, Teams, new[] { "甲方", "乙方" }, new BattleOptions { Seed = 99 });
        Assert.Equal(3, battle.RulesVersion);
        Assert.Equal(3, battle.Record.Version);
        for (int turn = 1; turn <= 120; turn++)
        {
            Assert.Equal(Phase.Choose, battle.Phase);
            battle.ResolveTurn(damage && turn == 1 ? BattleAction.Skill(0) : BattleAction.Pass(),
                damage && turn == 2 ? BattleAction.Skill(3) : BattleAction.Pass());
        }
        return battle;
    }

    static ContentDb FrozenContent()
    {
        string directory = Path.Combine(GoldenDirectory(), "content");
        return ContentDb.FromJson(File.ReadAllText(Path.Combine(directory, "StreamDuelRules.json")),
            File.ReadAllText(Path.Combine(directory, "original.json")));
    }

    static string GoldenDirectory([CallerFilePath] string source = "")
    {
        return Path.Combine(Path.GetDirectoryName(source), "Golden", "replays");
    }

    static string Sha256(byte[] bytes)
    {
        return System.Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    static string Json<T>(T value)
    {
        return JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true });
    }

    static void AssertSameBattle(Battle expected, Battle actual)
    {
        Assert.Equal(expected.StateHash(), actual.StateHash());
        Assert.Equal(expected.RulesVersion, actual.RulesVersion);
        Assert.Equal(expected.EndReason, actual.EndReason);
        Assert.Equal(expected.Phase, actual.Phase);
        Assert.Equal(expected.Winner, actual.Winner);
        Assert.Equal(expected.Turn, actual.Turn);
        Assert.Equal(expected.Record.ToJson(), actual.Record.ToJson());
        Assert.Equal(Json(expected.Log), Json(actual.Log));
        Assert.Equal(Json(expected.Events), Json(actual.Events));
        Assert.Equal(BattleSummary.From(expected).ToText(), BattleSummary.From(actual).ToText());
        var left = expected.Clone();
        var right = actual.Clone();
        for (int i = 0; i < 4; i++) Assert.Equal(left.Random(), right.Random());
    }
}
