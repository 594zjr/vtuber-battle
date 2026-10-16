using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T78_AiOpponentsTests
{
    public static IEnumerable<object[]> BuiltInPacks()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Content", "Packs");
        foreach (string path in Directory.GetFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            yield return new object[] { Path.GetFileNameWithoutExtension(path) };
    }

    [Theory]
    [MemberData(nameof(BuiltInPacks))]
    public void EveryBuiltInPackHasLegalOpponentsAtEveryDifficulty(string pack)
    {
        var db = BattleTests.LoadPack(pack);
        string contentHash = db.ContentHash();
        var opponents = AiOpponents.For(db);

        Assert.True(opponents.Count >= 6, pack + " 的电脑对手不足六位");
        Assert.Equal(new[] { AiLevel.Easy, AiLevel.Normal, AiLevel.Hard, AiLevel.Expert },
            opponents.Select(opponent => opponent.Level).Distinct().OrderBy(level => level));
        Assert.Equal(opponents.Count, opponents.Select(opponent => opponent.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var opponent in opponents)
        {
            Assert.Equal(db.PackId, opponent.PackId);
            Assert.False(string.IsNullOrWhiteSpace(opponent.Id));
            Assert.False(string.IsNullOrWhiteSpace(opponent.Name));
            Assert.False(string.IsNullOrWhiteSpace(opponent.Blurb));
            // T-53 已合并，T-78 要求内置陪练声明真实出招风格。
            Assert.Contains(opponent.Style, new[] { "aggressive", "defensive", "tricky" });
            Assert.NotNull(AiStyles.Get(opponent.Style));
            Assert.Equal(6, opponent.Team.Count);
            Assert.Empty(TeamCheck.Validate(db, null, opponent.Team));
        }
        Assert.Equal(contentHash, db.ContentHash());
    }

    [Fact]
    public void OpponentIdsAreUniqueAcrossAllBuiltInPacks()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pack in BuiltInPacks())
            foreach (var opponent in AiOpponents.For(BattleTests.LoadPack((string)pack[0])))
                Assert.True(ids.Add(opponent.Id), "重复电脑对手 id：" + opponent.Id);
    }

    [Theory]
    [MemberData(nameof(BuiltInPacks))]
    public void EveryOpponentCompletesAWholeBattleAtItsOwnDifficulty(string pack)
    {
        var db = BattleTests.LoadPack(pack);
        string contentHash = db.ContentHash();
        foreach (var opponent in AiOpponents.For(db))
        {
            string[] originalTeam = opponent.Team.ToArray();
            var battle = Play(db, opponent, 7800, false);
            Assert.Equal(Phase.Over, battle.Phase);
            Assert.InRange(battle.Winner, -1, 1);
            Assert.InRange(battle.Turn, 1, Battle.TurnCap + 1);
            Assert.Contains(battle.Log, entry => entry.Node == Node.Over);
            Assert.Equal(originalTeam, opponent.Team);
        }
        Assert.Equal(contentHash, db.ContentHash());
    }

    [Theory]
    [InlineData("original", AiLevel.Easy)]
    [InlineData("original_wave2", AiLevel.Normal)]
    [InlineData("fan_holo_niji", AiLevel.Hard)]
    [InlineData("fan_asoul_vr", AiLevel.Expert)]
    [InlineData("original_wave3", AiLevel.Hard)]
    public void FixedSeedReproducesSelectionsAndBattleAfterBanAndLead(string pack, AiLevel level)
    {
        var db = BattleTests.LoadPack(pack);
        var firstOpponent = AiOpponents.For(db).First(opponent => opponent.Level == level);
        var secondOpponent = AiOpponents.For(db).First(opponent => opponent.Id == firstOpponent.Id);

        var first = Play(db, firstOpponent, 7817, true);
        var second = Play(db, secondOpponent, 7817, true);

        Assert.Equal(first.StateHash(), second.StateHash());
        Assert.Equal(first.Winner, second.Winner);
        Assert.Equal(first.Record.ToJson(), second.Record.ToJson());
        Assert.Equal(first.Log.Select(entry => entry.Node + ":" + entry.Text),
            second.Log.Select(entry => entry.Node + ":" + entry.Text));
    }

    [Fact]
    public void ForReturnsIndependentListsOpponentsAndTeams()
    {
        var db = BattleTests.LoadPack("original");
        var first = AiOpponents.For(db);
        var second = AiOpponents.For(db);
        string expected = Snapshot(second);
        string contentHash = db.ContentHash();

        Assert.NotSame(first, second);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.NotSame(first[i], second[i]);
            Assert.NotSame(first[i].Team, second[i].Team);
        }
        Assert.NotSame(first[0].Team, first[1].Team);
        first[0].Id = "changed";
        first[0].Name = "已修改";
        first[0].Blurb = "已修改";
        first[0].PackId = "changed";
        first[0].Level = AiLevel.Expert;
        first[0].Style = "changed";
        first[0].Team.Clear();
        first.RemoveAt(first.Count - 1);

        Assert.Equal(expected, Snapshot(second));
        Assert.Equal(expected, Snapshot(AiOpponents.For(db)));
        Assert.Equal(contentHash, db.ContentHash());
    }

    [Fact]
    public void EmbeddedDefaultsMatchResourceJson()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", "AiOpponents.json"));
        foreach (var pack in BuiltInPacks())
        {
            var db = BattleTests.LoadPack((string)pack[0]);
            Assert.Equal(Snapshot(AiOpponents.FromJson(db, json)), Snapshot(AiOpponents.For(db)));
        }
    }

    [Fact]
    public void LoadingDoesNotCacheValidationAgainstMutableContent()
    {
        var db = BattleTests.LoadPack("original");
        string usedId = AiOpponents.For(db)[0].Team[0];
        db.Streamers.RemoveAll(streamer => streamer.Id == usedId);

        Assert.Throws<FormatException>(() => AiOpponents.For(db));
    }

    [Fact]
    public void UnknownPackHasNoImplicitFallbackOpponent()
    {
        var db = BattleTests.LoadPack("original");
        db.PackId = "custom_pack_without_opponents";
        Assert.Empty(AiOpponents.For(db));
    }

    [Fact]
    public void NullContentIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => AiOpponents.For(null));
        Assert.Throws<ArgumentNullException>(() => AiOpponents.FromJson(null, "{}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{} trailing")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"original\":null}")]
    [InlineData("{\"original\":{}}")]
    [InlineData("{\"original\":[null]}")]
    [InlineData("{\"original\":[0]}")]
    public void InvalidJsonAndContainerShapesAreRejected(string json)
    {
        Assert.Throws<FormatException>(() => AiOpponents.FromJson(BattleTests.LoadPack("original"), json));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("blurb")]
    [InlineData("team")]
    [InlineData("level")]
    public void MissingRequiredFieldsAreRejected(string field)
    {
        var db = BattleTests.LoadPack("original");
        var rows = ValidRows(db);
        rows[0].Remove(field);
        AssertInvalid(db, rows);
    }

    [Theory]
    [InlineData("id", "")]
    [InlineData("id", "   ")]
    [InlineData("id", null)]
    [InlineData("id", 1)]
    [InlineData("name", "")]
    [InlineData("name", "   ")]
    [InlineData("name", null)]
    [InlineData("name", 1)]
    [InlineData("blurb", "")]
    [InlineData("blurb", "   ")]
    [InlineData("blurb", null)]
    [InlineData("blurb", 1)]
    [InlineData("level", "legendary")]
    [InlineData("level", "")]
    [InlineData("level", null)]
    [InlineData("level", 0)]
    [InlineData("style", 1)]
    [InlineData("style", "balanced")]
    public void InvalidFieldValuesAreRejected(string field, object value)
    {
        var db = BattleTests.LoadPack("original");
        var rows = ValidRows(db);
        rows[0][field] = value;
        AssertInvalid(db, rows);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("string")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("empty")]
    [InlineData("non_string")]
    public void InvalidTeamsAreRejected(string fault)
    {
        var db = BattleTests.LoadPack("original");
        var rows = ValidRows(db);
        var team = db.Streamers.Take(6).Select(streamer => (object)streamer.Id).ToList();
        if (fault == "null") rows[0]["team"] = null;
        else if (fault == "string") rows[0]["team"] = "bell";
        else
        {
            if (fault == "short") team.RemoveAt(5);
            if (fault == "long") team.Add(db.Streamers[6].Id);
            if (fault == "duplicate") team[1] = team[0];
            if (fault == "unknown") team[0] = "missing_streamer";
            if (fault == "empty") team[0] = "";
            if (fault == "non_string") team[0] = 4;
            rows[0]["team"] = team;
        }
        AssertInvalid(db, rows);
    }

    [Fact]
    public void DuplicateOpponentIdsInOnePackAreRejected()
    {
        var db = BattleTests.LoadPack("original");
        var rows = ValidRows(db);
        rows[1]["id"] = rows[0]["id"];
        AssertInvalid(db, rows);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void FewerThanSixOpponentsAreRejected(int count)
    {
        var db = BattleTests.LoadPack("original");
        AssertInvalid(db, ValidRows(db).Take(count).ToList());
    }

    [Theory]
    [InlineData("easy")]
    [InlineData("normal")]
    [InlineData("hard")]
    [InlineData("expert")]
    public void MissingAnyDifficultyIsRejected(string missing)
    {
        var db = BattleTests.LoadPack("original");
        var rows = ValidRows(db);
        foreach (var row in rows)
            if ((string)row["level"] == missing)
                row["level"] = missing == "easy" ? "normal" : "easy";
        AssertInvalid(db, rows);
    }

    [Theory]
    [InlineData("bad_shape")]
    [InlineData("too_few")]
    [InlineData("missing_level")]
    public void NonSelectedPackStillRequiresValidRosterStructure(string fault)
    {
        var db = BattleTests.LoadPack("original");
        var otherRows = ValidRows(BattleTests.LoadPack("original_wave2"));
        if (fault == "too_few") otherRows.RemoveAt(5);
        if (fault == "missing_level") otherRows[3]["level"] = "normal";
        string json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [db.PackId] = ValidRows(db),
            ["original_wave2"] = fault == "bad_shape" ? (object)"invalid" : otherRows
        });

        Assert.Throws<FormatException>(() => AiOpponents.FromJson(db, json));
    }

    [Fact]
    public void DuplicateOpponentIdsAcrossPacksAreRejected()
    {
        var db = BattleTests.LoadPack("original");
        var otherDb = BattleTests.LoadPack("original_wave2");
        var rows = ValidRows(db);
        var otherRows = ValidRows(otherDb);
        otherRows[0]["id"] = rows[0]["id"];
        string json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [db.PackId] = rows,
            [otherDb.PackId] = otherRows
        });

        Assert.Throws<FormatException>(() => AiOpponents.FromJson(db, json));
        Assert.Throws<FormatException>(() => AiOpponents.FromJson(otherDb, json));
    }

    [Fact]
    public void CustomJsonPreservesDeclaredOrderTeamsAndDescriptions()
    {
        var db = BattleTests.LoadPack("original");
        var rows = ValidRows(db);
        rows[0]["name"] = "练习生「星灯」";
        rows[0]["blurb"] = "按固定阵容练习。\n第二行说明。";
        var team = db.Streamers.Skip(2).Take(6).Select(streamer => streamer.Id).Reverse().ToArray();
        rows[0]["team"] = team;
        string json = JsonSerializer.Serialize(new Dictionary<string, object> { [db.PackId] = rows });
        string contentHash = db.ContentHash();

        var opponents = AiOpponents.FromJson(db, json);

        Assert.Equal(rows.Select(row => (string)row["id"]), opponents.Select(opponent => opponent.Id));
        Assert.Equal("练习生「星灯」", opponents[0].Name);
        Assert.Equal("按固定阵容练习。\n第二行说明。", opponents[0].Blurb);
        Assert.Equal(team, opponents[0].Team);
        Assert.Equal(contentHash, db.ContentHash());
        opponents[0].Team.Clear();
        Assert.Equal(team, AiOpponents.FromJson(db, json)[0].Team);
    }

    static Battle Play(ContentDb db, AiOpponent opponent, uint seed, bool openingChoices)
    {
        var myTeam = db.Streamers.Take(6).Select(streamer => streamer.Id).ToList();
        var battle = new Battle(db, new IList<string>[] { myTeam, opponent.Team },
            new[] { "练习方", opponent.Name },
            new BattleOptions { Seed = seed, BanOne = openingChoices, LeadPick = openingChoices });
        AiOpponents.ApplyStyle(battle, 1, opponent);
        for (int step = 0; battle.Phase != Phase.Over && step < 500; step++)
        {
            if (battle.Phase == Phase.Ban)
            {
                battle.ChooseBan(0, Ai.ChooseBan(battle, 0));
                battle.ChooseBan(1, Ai.ChooseBan(battle, 1));
            }
            else if (battle.Phase == Phase.Lead)
            {
                battle.ChooseLead(0, Ai.ChooseLead(battle, 0));
                battle.ChooseLead(1, Ai.ChooseLead(battle, 1));
            }
            else if (battle.Phase == Phase.Choose)
                battle.ResolveTurn(Ai.Choose(battle, 0, AiLevel.Normal), Ai.Choose(battle, 1, opponent.Level));
            else if (battle.Phase == Phase.Replace)
            {
                for (int side = 0; side < 2; side++)
                    if (battle.Need[side])
                        battle.Replace(side, Ai.ChooseReplacement(battle, side, side == 0 ? AiLevel.Normal : opponent.Level));
            }
            else Assert.Fail("未处理的对战阶段：" + battle.Phase);
        }
        Assert.True(battle.Phase == Phase.Over, opponent.Id + " 未能正常结束对战");
        return battle;
    }

    static List<Dictionary<string, object>> ValidRows(ContentDb db)
    {
        string[] levels = { "easy", "normal", "hard", "expert", "normal", "hard" };
        return Enumerable.Range(0, 6).Select(index => new Dictionary<string, object>
        {
            ["id"] = db.PackId + "_test_" + index,
            ["name"] = "练习对手" + index,
            ["blurb"] = "固定阵容的练习对手。",
            ["team"] = db.Streamers.Take(6).Select(streamer => streamer.Id).ToArray(),
            ["level"] = levels[index],
            ["style"] = null
        }).ToList();
    }

    static void AssertInvalid(ContentDb db, List<Dictionary<string, object>> rows)
    {
        string json = JsonSerializer.Serialize(new Dictionary<string, object> { [db.PackId] = rows });
        Assert.Throws<FormatException>(() => AiOpponents.FromJson(db, json));
    }

    static string Snapshot(IEnumerable<AiOpponent> opponents)
    {
        return JsonSerializer.Serialize(opponents.Select(opponent => new
        {
            opponent.Id, opponent.Name, opponent.Blurb, opponent.PackId,
            opponent.Level, opponent.Style, Team = opponent.Team.ToArray()
        }));
    }
}
