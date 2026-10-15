using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T39_TeamBuilderEdgeTests
{
    static ContentDb SmallPack(int count)
    {
        var db = BattleTests.LoadPack("original");
        db.Streamers.RemoveRange(count, db.Streamers.Count - count);
        db.Seasons.Clear();
        return db;
    }

    static void AssertLegal(ContentDb db, string seasonId, IList<string> team)
    {
        Assert.Equal(6, team.Count);
        Assert.Equal(6, team.Distinct().Count());
        Assert.Empty(TeamCheck.Validate(db, seasonId, team));
    }

    static ContentDb OverlappingPools()
    {
        var db = SmallPack(7);
        var trap = db.Streamers[0];
        foreach (string stat in trap.Stats.Keys.ToArray()) trap.Stats[stat] = 2000;
        trap.Type = "meme";
        var season = new SeasonDef { Name = "交叠限制赛季" };
        foreach (var other in db.Streamers.Skip(1))
            season.Limited.Add(new LimitedPoolDef { Max = 1, Ids = { trap.Id, other.Id } });
        db.Seasons["overlap"] = season;
        return db;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void EveryPartialPrefixKeepsItsOrderAndCanBeCompletedLegally(int count)
    {
        var db = BattleTests.LoadPack("original");
        var prefix = new[] { "momo", "lumi", "hane", "bell", "shiori" }.Take(count).ToArray();
        var fixedIds = Array.AsReadOnly(prefix);
        foreach (uint seed in new[] { 0u, 39u, uint.MaxValue })
        {
            var team = TeamBuilder.Suggest(db, "s1", fixedIds, seed);

            AssertLegal(db, "s1", team);
            Assert.Equal(prefix, team.Take(count));
            Assert.True(team.Select(id => db.Streamer(id).Type).Distinct().Count() >= 5);
            Assert.Equal(team, TeamBuilder.Suggest(db, "s1", fixedIds, seed));
            Assert.Equal(prefix, fixedIds);
        }
    }

    [Fact]
    public void AFullLegalTeamIsPreservedEvenWhenItsTracksAreRepeated()
    {
        var db = BattleTests.LoadPack("original");
        var fixedIds = new List<string> { "enka", "buck", "shiori", "kasumi", "momo", "yuan" };
        string[] before = fixedIds.ToArray();

        var team = TeamBuilder.Suggest(db, null, fixedIds, 39);

        AssertLegal(db, null, team);
        Assert.Equal(before, team);
        Assert.Equal(before, fixedIds);
        team.Clear();
        Assert.Equal(before, fixedIds);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("null_id")]
    [InlineData("banned")]
    [InlineData("limited")]
    [InlineData("too_many")]
    public void InvalidFixedSelectionsReportTheExistingTeamCheckErrors(string kind)
    {
        var db = BattleTests.LoadPack("original");
        string[] fixedIds;
        switch (kind)
        {
            case "duplicate": fixedIds = new[] { "enka", "enka" }; break;
            case "missing": fixedIds = new[] { "missing_streamer" }; break;
            case "null_id": fixedIds = new string[] { null }; break;
            case "banned": fixedIds = new[] { "kei" }; break;
            case "limited": fixedIds = new[] { "bell", "bee" }; break;
            default: fixedIds = new[] { "enka", "shiori", "lumi", "bell", "hane", "momo", "nyako" }; break;
        }
        string[] before = fixedIds.ToArray();
        var expectedErrors = TeamCheck.Validate(db, "s1", fixedIds)
            .Where(error => fixedIds.Length > 6 || !error.StartsWith("队伍需要正好 6 位主播")).ToArray();
        Assert.NotEmpty(expectedErrors);

        var error = Assert.Throws<InvalidOperationException>(() => TeamBuilder.Suggest(db, "s1", fixedIds, 39));

        Assert.All(expectedErrors, expected => Assert.Contains(expected, error.Message));
        Assert.Equal(before, fixedIds);
    }

    [Fact]
    public void UnknownSeasonUsesTheTeamCheckErrorEvenWithAnInvalidPrefix()
    {
        var db = BattleTests.LoadPack("original");
        var fixedIds = new[] { "missing_streamer" };
        string expected = Assert.Single(TeamCheck.Validate(db, "missing_season", fixedIds));

        var error = Assert.Throws<InvalidOperationException>(() => TeamBuilder.Suggest(db, "missing_season", fixedIds, 39));

        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public void SmallPacksReturnSixDistinctAvailableStreamers(int count)
    {
        var db = SmallPack(count);
        var prefix = new[] { db.Streamers[2].Id, db.Streamers[0].Id };
        for (uint seed = 1; seed <= 8; seed++)
        {
            var team = TeamBuilder.Suggest(db, null, prefix, seed);

            AssertLegal(db, null, team);
            Assert.Equal(prefix, team.Take(prefix.Length));
            Assert.True(team.Select(id => db.Streamer(id).Type).Distinct().Count() >= 5);
            if (count == 6) Assert.Equal(db.Streamers.Select(streamer => streamer.Id).OrderBy(id => id), team.OrderBy(id => id));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void OverlappingPoolsDoNotTrapTheBuilderInAnUnfinishableHighStatChoice(int fixedCount)
    {
        var db = OverlappingPools();
        string[] onlyLegalTeam = db.Streamers.Skip(1).Select(streamer => streamer.Id).ToArray();
        string[] prefix = onlyLegalTeam.Reverse().Take(fixedCount).ToArray();
        Assert.Empty(TeamCheck.Validate(db, "overlap", onlyLegalTeam));
        foreach (uint seed in new[] { 0u, 1u, 8u, 39u, uint.MaxValue })
        {
            var team = TeamBuilder.Suggest(db, "overlap", prefix, seed);

            AssertLegal(db, "overlap", team);
            Assert.Equal(prefix, team.Take(prefix.Length));
            Assert.Equal(onlyLegalTeam.OrderBy(id => id), team.OrderBy(id => id));
        }
    }

    [Theory]
    [InlineData("not_enough_streamers")]
    [InlineData("banned")]
    [InlineData("overlapping_fixed")]
    [InlineData("global_limit")]
    public void ImpossibleCompletionFailsClearlyInsteadOfReturningAnInvalidOrShortTeam(string cause)
    {
        var db = cause == "overlapping_fixed" ? OverlappingPools() : SmallPack(cause == "not_enough_streamers" ? 5 : 7);
        string seasonId = null;
        string[] fixedIds = new string[0];
        if (cause == "overlapping_fixed")
        {
            seasonId = "overlap";
            fixedIds = new[] { db.Streamers[0].Id };
        }
        else if (cause == "banned" || cause == "global_limit")
        {
            seasonId = "impossible";
            var season = new SeasonDef { Name = "无法补满赛季" };
            if (cause == "banned") season.Banned.AddRange(db.Streamers.Take(2).Select(streamer => streamer.Id));
            else season.Limited.Add(new LimitedPoolDef { Max = 5, Ids = db.Streamers.Select(streamer => streamer.Id).ToList() });
            db.Seasons[seasonId] = season;
        }

        var error = Assert.Throws<InvalidOperationException>(() => TeamBuilder.Suggest(db, seasonId, fixedIds, 39));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
    }

    [Fact]
    public void ExistingSameTrackPicksDoNotMakeAnOtherwiseLegalTeamImpossible()
    {
        var db = BattleTests.LoadPack("original");
        db.Seasons.Clear();
        var fixedIds = db.Streamers.Take(5).Select(streamer => streamer.Id).ToArray();
        foreach (string id in fixedIds) db.Streamer(id).Type = "game";

        var team = TeamBuilder.Suggest(db, null, fixedIds, 39);

        AssertLegal(db, null, team);
        Assert.Equal(fixedIds, team.Take(5));
        Assert.Equal(2, team.Select(id => db.Streamer(id).Type).Distinct().Count());
    }

    [Fact]
    public void RepeatedSeededSuggestionsDoNotChangeInputOrderDefinitionsOrAnExistingBattle()
    {
        var db = BattleTests.LoadPack("original");
        var fixedIds = new List<string> { "hane", "enka" };
        string[] beforeFixed = fixedIds.ToArray();
        string[] beforeRoster = db.Streamers.Select(streamer => streamer.Id).ToArray();
        string beforeDefinitions = DefinitionSnapshot(db);
        var battle = new Battle(db, new IList<string>[] { beforeRoster.Take(6).ToArray(), beforeRoster.Skip(6).ToArray() },
            new[] { "甲方", "乙方" }, 39);
        ulong beforeBattle = battle.StateHash();
        string beforeRecord = battle.Record.ToJson();
        for (uint seed = 1; seed <= 16; seed++)
        {
            var team = TeamBuilder.Suggest(db, "s1", fixedIds, seed);
            AssertLegal(db, "s1", team);
            Assert.True(team.Select(id => db.Streamer(id).Type).Distinct().Count() >= 5);
            Assert.Equal(team, TeamBuilder.Suggest(db, "s1", fixedIds, seed));
        }

        Assert.Equal(beforeFixed, fixedIds);
        Assert.Equal(beforeRoster, db.Streamers.Select(streamer => streamer.Id));
        Assert.Equal(beforeDefinitions, DefinitionSnapshot(db));
        Assert.Equal(beforeBattle, battle.StateHash());
        Assert.Equal(beforeRecord, battle.Record.ToJson());
    }

    [Fact]
    public void AComplementaryFifthSkillWinsWhenTheOrdinarySkillsAndOtherQualitiesAreEqual()
    {
        var db = UniformScoringPack(7);
        var fixedIds = db.Streamers.Take(5).Select(streamer => streamer.Id).ToArray();
        var stronger = db.Streamers[6];
        db.Skills["builder_coverage_fifth"] = new SkillDef
        {
            Id = "builder_coverage_fifth", Name = "补足赛道第五技能", Type = "art", Cat = "interact", Power = 40, Pp = 20
        };
        stronger.Fifth = "builder_coverage_fifth";

        foreach (uint seed in new[] { 0u, 1u, 8u, 39u, uint.MaxValue })
        {
            var team = TeamBuilder.Suggest(db, null, fixedIds, seed);

            AssertLegal(db, null, team);
            Assert.Equal(fixedIds, team.Take(5));
            Assert.Equal(stronger.Id, team[5]);
        }
    }

    [Theory]
    [InlineData("speed")]
    [InlineData("bulk")]
    public void TheFinalPickAddsSpeedOrBulkVarietyWhenTracksAndSkillCoverageAreEqual(string quality)
    {
        var db = UniformScoringPack(7);
        var fixedIds = db.Streamers.Take(5).Select(streamer => streamer.Id).ToArray();
        var complementary = db.Streamers[6];
        if (quality == "speed")
        {
            // 更慢本身会吃对位罚分，仍被选中才能证明快慢搭配参与了评分。
            complementary.Stats["spd"] = 10;
        }
        else complementary.Stats["hp"] = 1200;

        foreach (uint seed in new[] { 0u, 1u, 8u, 39u, uint.MaxValue })
        {
            var team = TeamBuilder.Suggest(db, null, fixedIds, seed);

            AssertLegal(db, null, team);
            Assert.Equal(fixedIds, team.Take(5));
            Assert.Equal(complementary.Id, team[5]);
        }
    }

    [Fact]
    public void ASharedFiveMemberLimitRejectsAnImpossibleFiftyMemberRoster()
    {
        var db = UniformScoringPack(50);
        db.Seasons["large_limit"] = new SeasonDef
        {
            Name = "全员限选赛季",
            Limited = { new LimitedPoolDef { Max = 5, Ids = db.Streamers.Select(streamer => streamer.Id).ToList() } }
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            TeamBuilder.Suggest(db, "large_limit", new string[0], 39));

        Assert.Contains("合法", error.Message);
        Assert.Contains("6", error.Message);
    }

    static ContentDb UniformScoringPack(int count)
    {
        var db = BattleTests.LoadPack("original");
        string persona = db.Streamers[0].Persona;
        db.Seasons.Clear();
        db.Streamers.Clear();
        var skills = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            string id = "builder_same_skill_" + i;
            db.Skills[id] = new SkillDef
            {
                Id = id, Name = "相同赛道技能", Type = "song", Cat = "interact", Power = 40, Pp = 20
            };
            skills.Add(id);
        }
        for (int i = 0; i < count; i++)
        {
            db.Streamers.Add(new StreamerDef
            {
                Id = "builder_member_" + i.ToString("D2"), Name = "组队测试主播" + i,
                Type = "song", Persona = persona, Skills = skills.Take(4).ToList(), Fifth = skills[4],
                Stats = new Dictionary<string, int>
                {
                    { "hp", 400 }, { "atk", 250 }, { "def", 250 }, { "spa", 250 }, { "sdef", 250 }, { "spd", 250 }
                }
            });
        }
        return db;
    }

    static string DefinitionSnapshot(ContentDb db)
    {
        var roster = db.Streamers.Select(streamer => string.Join("|", streamer.Id, streamer.Type, streamer.Type2,
            string.Join(",", streamer.Stats.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)),
            string.Join(",", streamer.Skills), streamer.Fifth, streamer.Persona));
        var seasons = db.Seasons.OrderBy(pair => pair.Key).Select(pair => pair.Key + "|" + string.Join(",", pair.Value.Banned)
            + "|" + string.Join(";", pair.Value.Limited.Select(pool => pool.Max + ":" + string.Join(",", pool.Ids))));
        return string.Join("\n", roster.Concat(seasons));
    }
}
