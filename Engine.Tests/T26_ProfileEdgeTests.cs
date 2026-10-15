using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T26_ProfileEdgeTests
{
    static Battle Make(string pack = "original", bool leadPick = false)
    {
        var db = BattleTests.LoadPack(pack);
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        return new Battle(db, new IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() },
            new[] { "A", "B" }, 7, leadPick);
    }

    static void Finish(Battle battle, int winner)
    {
        // 统计接口只读取结局；真实整局已由任务附带的验收测试覆盖。
        battle.Phase = Phase.Over;
        battle.Winner = winner;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, -1)]
    public void RecordsResultFromEitherSideAndIncludesTheWholeStartingRoster(int playerSide, int winner)
    {
        var battle = Make(leadPick: true);
        var roster = battle.Record.Teams[playerSide].ToArray();
        battle.ChooseLead(0, 5);
        battle.ChooseLead(1, 4);
        battle.Sides[playerSide].Active = 3;
        battle.Sides[playerSide].Team[0].Fainted = true;
        battle.Sides[playerSide].Team[0].Hp = 0;
        Finish(battle, winner);

        var profile = new Profile();
        profile.Record(battle, playerSide);

        var stats = Assert.Single(profile.Packs).Value;
        Assert.Equal(winner == playerSide ? 1 : 0, stats.Wins);
        Assert.Equal(winner >= 0 && winner != playerSide ? 1 : 0, stats.Losses);
        Assert.Equal(winner == -1 ? 1 : 0, stats.Draws);
        Assert.Equal(roster.OrderBy(id => id, StringComparer.Ordinal), stats.Picks.Keys.OrderBy(id => id, StringComparer.Ordinal));
        foreach (string id in roster)
        {
            Assert.Equal(1, stats.Picks[id]);
            Assert.Equal(winner == playerSide ? 1 : 0, stats.PickWins.TryGetValue(id, out int wins) ? wins : 0);
        }
        Assert.All(stats.PickWins.Keys, id => Assert.Contains(id, roster));
    }

    [Theory]
    [InlineData(Phase.Lead)]
    [InlineData(Phase.Choose)]
    [InlineData(Phase.Replace)]
    public void RejectingAnUnfinishedBattleDoesNotChangeOrCreatePackStats(Phase phase)
    {
        var profile = new Profile();
        var completed = Make();
        Finish(completed, 0);
        profile.Record(completed, 0);
        string before = profile.ToJson();
        var unfinished = Make("fan_holo_niji", phase == Phase.Lead);
        unfinished.Phase = phase;

        Assert.Throws<InvalidOperationException>(() => profile.Record(unfinished, 1));

        Assert.Equal(before, profile.ToJson());
        Assert.False(profile.Packs.ContainsKey(unfinished.Db.PackId));
    }

    [Fact]
    public void CountsAccumulateWithinTheirContentPackOnly()
    {
        var profile = new Profile();
        var original = Make();
        var fan = Make("fan_holo_niji");
        Finish(original, 0);
        profile.Record(original, 0);
        Finish(fan, 1);
        profile.Record(fan, 0);
        Finish(original, -1);
        profile.Record(original, 1);

        Assert.Equal(2, profile.Packs.Count);
        var own = profile.Packs["original"];
        Assert.Equal(1, own.Wins);
        Assert.Equal(0, own.Losses);
        Assert.Equal(1, own.Draws);
        Assert.Equal(12, own.Picks.Count);
        Assert.All(own.Picks.Values, count => Assert.Equal(1, count));
        foreach (string id in original.Record.Teams[0]) Assert.Equal(1, own.PickWins[id]);
        foreach (string id in original.Record.Teams[1])
            Assert.Equal(0, own.PickWins.TryGetValue(id, out int wins) ? wins : 0);

        var other = profile.Packs[fan.Db.PackId];
        Assert.Equal(0, other.Wins);
        Assert.Equal(1, other.Losses);
        Assert.Equal(0, other.Draws);
        Assert.Equal(fan.Record.Teams[0].OrderBy(id => id, StringComparer.Ordinal),
            other.Picks.Keys.OrderBy(id => id, StringComparer.Ordinal));
        Assert.All(other.Picks.Values, count => Assert.Equal(1, count));
        Assert.All(other.PickWins.Values, count => Assert.Equal(0, count));
    }

    static Profile WithKeys(IEnumerable<string> packIds, IEnumerable<string> streamerIds)
    {
        var profile = new Profile();
        foreach (string pack in packIds)
        {
            var stats = new PackStats { Wins = 2, Losses = 1, Draws = 1 };
            foreach (string id in streamerIds)
            {
                stats.Picks.Add(id, 4);
                stats.PickWins.Add(id, 2);
            }
            profile.Packs.Add(pack, stats);
        }
        return profile;
    }

    [Fact]
    public void SerializationSortsKeysAndEscapesThemWithoutChangingTheIds()
    {
        var keys = new[] { "z", "中", "a\"\\\n\t\u0001", "A", "a" };
        var profile = WithKeys(keys, keys.Reverse());
        string json = profile.ToJson();
        Assert.Equal(json, WithKeys(keys.Reverse(), keys).ToJson());

        // 独立 JSON 解析器验证转义，避免读写双方共享同一个错误仍能回环通过。
        using var document = JsonDocument.Parse(json);
        var packs = document.RootElement.GetProperty("packs");
        var sorted = keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, packs.EnumerateObject().Select(property => property.Name).ToArray());
        foreach (var pack in packs.EnumerateObject())
        {
            Assert.Equal(new[] { "draws", "losses", "pickWins", "picks", "wins" },
                pack.Value.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.Equal(sorted, pack.Value.GetProperty("picks").EnumerateObject().Select(property => property.Name).ToArray());
            Assert.Equal(sorted, pack.Value.GetProperty("pickWins").EnumerateObject().Select(property => property.Name).ToArray());
            Assert.Equal(2, pack.Value.GetProperty("wins").GetInt32());
            Assert.Equal(4, pack.Value.GetProperty("picks").GetProperty(keys[2]).GetInt32());
        }
        Assert.Equal(json, Profile.FromJson(json).ToJson());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" \r\n\t ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"packs\":[]}")]
    [InlineData("{\"packs\":{\"ok\":{\"draws\":0,\"losses\":0,\"pickWins\":{},\"picks\":{},\"wins\":1}},\"unfinished\":")]
    [InlineData("{\"packs\":{}} trailing")]
    [InlineData("{\"packs\":{\"bad\\q\":{\"draws\":0,\"losses\":0,\"pickWins\":{},\"picks\":{},\"wins\":1}}}")]
    [InlineData("{\"packs\":{\"bad\":{\"draws\":0,\"losses\":0,\"pickWins\":{},\"picks\":{},\"wins\":-1}}}")]
    [InlineData("{\"packs\":{\"bad\":{\"draws\":0,\"losses\":0,\"pickWins\":{},\"picks\":{},\"wins\":2147483648}}}")]
    public void InvalidInputReturnsAnEmptyProfile(string json)
    {
        Assert.Empty(Profile.FromJson(json).Packs);
    }
}
