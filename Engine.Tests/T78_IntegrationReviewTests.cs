using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public sealed class T78_IntegrationReviewTests
{
    static string Json(List<Dictionary<string, object>> rows, string pack = "original")
    {
        return JsonSerializer.Serialize(new Dictionary<string, object> { [pack] = rows });
    }

    static List<Dictionary<string, object>> Rows(ContentDb db)
    {
        string[] levels = { "easy", "normal", "hard", "expert", "normal", "hard" };
        return Enumerable.Range(0, 6).Select(index => new Dictionary<string, object>
        {
            ["id"] = "review_" + index, ["name"] = "测试陪练" + index, ["blurb"] = "固定阵容测试。",
            ["level"] = levels[index], ["style"] = "defensive",
            ["team"] = db.Streamers.Skip(6).Take(6).Select(streamer => streamer.Id).ToArray()
        }).ToList();
    }

    static ContentDb PolicyDb(string json)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Content");
        return ContentDb.FromJson(File.ReadAllText(Path.Combine(root, "StreamDuelRules.json")),
            File.ReadAllText(Path.Combine(root, "Packs", "original.json")), PackPolicy.FromJson(json));
    }

    static AiOpponent Opponent(ContentDb db, IEnumerable<string> team, string style = "defensive")
    {
        return new AiOpponent
        {
            Id = "test", Name = "测试陪练", Blurb = "测试", PackId = db.PackId,
            Level = AiLevel.Normal, Style = style, Team = team.ToList()
        };
    }

    static Battle BattleFor(ContentDb db, AiOpponent opponent, bool mirror = false, bool opening = false)
    {
        var mine = mirror ? opponent.Team.ToArray() : db.Streamers.Take(6).Select(streamer => streamer.Id).ToArray();
        return new Battle(db, new IList<string>[] { mine, opponent.Team.ToArray() }, new[] { "玩家", "陪练" },
            new BattleOptions { Seed = 78, BanOne = opening, LeadPick = opening });
    }

    [Fact]
    public void MirrorTeamsAndSharedContentStayIsolatedAndApplyingAStyleDoesNotConsumeRandomness()
    {
        var db = BattleTests.LoadPack("original");
        var opponent = Opponent(db, db.Streamers.Take(6).Select(streamer => streamer.Id));
        var battle = BattleFor(db, opponent, mirror: true);
        var untouched = BattleFor(db, opponent, mirror: true);
        string contentHash = db.ContentHash(), record = battle.Record.ToJson();
        ulong stateHash = battle.StateHash();
        double next = battle.Clone().Random();
        AiOpponents.ApplyStyle(battle, 1, opponent);
        AiOpponents.ApplyStyle(battle, 1, opponent);

        Assert.Equal(stateHash, battle.StateHash());
        Assert.Equal(next, battle.Clone().Random());
        Assert.Equal(contentHash, db.ContentHash());
        Assert.Equal(record, battle.Record.ToJson());
        foreach (var fighter in battle.Sides[1].Team)
        {
            Assert.Equal("defensive", fighter.Def.AiStyle);
            Assert.NotSame(db.Streamer(fighter.Def.Id), fighter.Def);
            Assert.NotSame(db.Streamer(fighter.Def.Id).Stats, fighter.Def.Stats);
            Assert.NotSame(db.Streamer(fighter.Def.Id).Skills, fighter.Def.Skills);
        }
        Assert.All(battle.Sides[0].Team, fighter => Assert.Null(fighter.Def.AiStyle));
        Assert.All(untouched.Sides[1].Team, fighter => Assert.Null(fighter.Def.AiStyle));
        opponent.Style = "aggressive";
        opponent.Team.Clear();
        Assert.All(battle.Sides[1].Team, fighter => Assert.Equal("defensive", fighter.Def.AiStyle));
        battle.Active(1).Def.Stats["hp"]++;
        battle.Active(1).Def.Skills.Clear();
        Assert.Equal(contentHash, db.ContentHash());
        Assert.Equal(stateHash, untouched.StateHash());
    }

    [Theory]
    [InlineData(AiLevel.Easy, "aggressive")]
    [InlineData(AiLevel.Normal, "aggressive")]
    [InlineData(AiLevel.Hard, "aggressive")]
    [InlineData(AiLevel.Expert, "aggressive")]
    [InlineData(AiLevel.Easy, "defensive")]
    [InlineData(AiLevel.Normal, "defensive")]
    [InlineData(AiLevel.Hard, "defensive")]
    [InlineData(AiLevel.Expert, "defensive")]
    [InlineData(AiLevel.Easy, "tricky")]
    [InlineData(AiLevel.Normal, "tricky")]
    [InlineData(AiLevel.Hard, "tricky")]
    [InlineData(AiLevel.Expert, "tricky")]
    public void AppliedStylesFollowTheExistingAiStrategyAtEveryDifficulty(AiLevel level, string style)
    {
        var db = BattleTests.LoadPack("original");
        var referenceDb = BattleTests.LoadPack("original");
        var opponent = Opponent(db, db.Streamers.Skip(6).Take(6).Select(streamer => streamer.Id), style);
        foreach (string id in opponent.Team) referenceDb.Streamer(id).AiStyle = style;
        var battle = BattleFor(db, opponent);
        var reference = BattleFor(referenceDb, opponent);
        string contentHash = db.ContentHash();
        AiOpponents.ApplyStyle(battle, 1, opponent);

        var action = Ai.Choose(battle, 1, level);
        var expected = Ai.Choose(reference, 1, level);

        Assert.Equal(expected.ToString(), action.ToString());
        Assert.Equal(reference.Clone().Random(), battle.Clone().Random());
        battle.ResolveTurn(BattleAction.Pass(), action);
        reference.ResolveTurn(BattleAction.Pass(), expected);
        Assert.Equal(reference.StateHash(), battle.StateHash());
        var replay = Replay.Run(db, BattleRecord.FromJson(battle.Record.ToJson()));
        Assert.Equal(battle.StateHash(), replay.StateHash());
        Assert.Equal(contentHash, db.ContentHash());
    }

    [Theory]
    [InlineData("aggressive", 0)]
    [InlineData("defensive", 1)]
    [InlineData("tricky", 2)]
    public void OpponentStylesActuallyChooseAttackRecoveryAndDisruption(string style, int expected)
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["review_neutral"] = new PersonaDef { Id = "review_neutral", Name = "测试人设", Code = "T_LOWHP_POWER" };
        AddSkill(db, "review_attack", 500);
        AddSkill(db, "review_heal", 0, new EffectDef { At = "post", Code = "HEAL", Target = "self", Frac = 0.5 });
        AddSkill(db, "review_control", 0, new EffectDef { At = "post", Code = "STATUS", Target = "foe", Status = "disconnect" });
        AddSkill(db, "review_idle", 0);
        foreach (var streamer in db.Streamers)
        {
            streamer.Type = "talk";
            streamer.Type2 = null;
            streamer.Persona = "review_neutral";
            streamer.Stats["hp"] = 1000;
            foreach (string stat in ContentDb.StatKeys) streamer.Stats[stat] = 100;
            streamer.Skills = new List<string> { "review_attack", "review_heal", "review_control", "review_idle" };
            streamer.Fifth = "review_idle";
        }
        var opponent = Opponent(db, db.Streamers.Skip(6).Take(6).Select(streamer => streamer.Id), style);
        db.Streamer(opponent.Team[0]).Stats["spd"] = 120;
        var battle = BattleFor(db, opponent);
        foreach (var fighter in battle.Sides[1].Team)
        {
            fighter.Hp = 400;
            fighter.Skills[3].Pp = fighter.Skills[4].Pp = 0;
        }
        foreach (var fighter in battle.Sides[0].Team)
            foreach (var slot in fighter.Skills) slot.Id = "review_idle";
        foreach (var side in battle.Sides) side.Team[side.Active].Fields.Add(new FieldInst { Id = "lockin", Turns = -1 });
        string contentHash = db.ContentHash();

        AiOpponents.ApplyStyle(battle, 1, opponent);

        Assert.Equal(BattleAction.Skill(expected).ToString(), Ai.Choose(battle, 1, AiLevel.Normal).ToString());
        Assert.Equal(contentHash, db.ContentHash());
        Assert.All(db.Streamers, streamer => Assert.Null(streamer.AiStyle));
    }

    static void AddSkill(ContentDb db, string id, int power, params EffectDef[] effects)
    {
        db.Skills[id] = new SkillDef
        {
            Id = id, Name = "测试技能", Type = "talk", Cat = power > 0 ? "interact" : "status",
            Power = power, Pp = 10, Effects = effects.ToList()
        };
    }

    [Theory]
    [InlineData("pack")]
    [InlineData("null_team")]
    [InlineData("order")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("style")]
    [InlineData("level")]
    public void InvalidMutableOpponentCannotPartiallyReplaceDefinitions(string fault)
    {
        var db = BattleTests.LoadPack("original");
        var opponent = Opponent(db, db.Streamers.Take(6).Select(streamer => streamer.Id));
        var battle = BattleFor(db, opponent, mirror: true);
        var original = battle.Sides[1].Team.Select(fighter => fighter.Def).ToArray();
        ulong hash = battle.StateHash();
        double next = battle.Clone().Random();
        if (fault == "pack") opponent.PackId = "other";
        if (fault == "null_team") opponent.Team = null;
        if (fault == "order") opponent.Team.Reverse();
        if (fault == "duplicate") opponent.Team[5] = opponent.Team[0];
        if (fault == "unknown") opponent.Team[5] = "missing";
        if (fault == "style") opponent.Style = "balanced";
        if (fault == "level") opponent.Level = (AiLevel)99;

        Assert.Throws<FormatException>(() => AiOpponents.ApplyStyle(battle, 1, opponent));

        for (int i = 0; i < original.Length; i++) Assert.Same(original[i], battle.Sides[1].Team[i].Def);
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(next, battle.Clone().Random());
    }

    [Fact]
    public void BanAndLeadReorderingKeepTheOriginalRosterIdentityForStyleApplication()
    {
        var db = BattleTests.LoadPack("original");
        var opponent = Opponent(db, db.Streamers.Skip(6).Take(6).Select(streamer => streamer.Id));
        var battle = BattleFor(db, opponent, opening: true);
        battle.ChooseBan(0, 0);
        battle.ChooseBan(1, 0);
        battle.ChooseLead(0, 2);
        battle.ChooseLead(1, 2);
        ulong hash = battle.StateHash();
        string record = battle.Record.ToJson();
        double next = battle.Clone().Random();

        AiOpponents.ApplyStyle(battle, 1, opponent);

        Assert.Equal(5, battle.Sides[1].Team.Count);
        Assert.All(battle.Sides[1].Team, fighter => Assert.Equal("defensive", fighter.Def.AiStyle));
        Assert.Equal(hash, battle.StateHash());
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(next, battle.Clone().Random());
    }

    [Fact]
    public void NullStylePreservesAnExistingStreamerStyleWithoutCopyingOrChangingDefaults()
    {
        var db = BattleTests.LoadPack("original");
        db.Streamers[0].AiStyle = "tricky";
        var opponent = Opponent(db, db.Streamers.Take(6).Select(streamer => streamer.Id), null);
        var battle = BattleFor(db, opponent);
        var reference = battle.Clone();
        var original = battle.Active(1).Def;

        AiOpponents.ApplyStyle(battle, 1, opponent);

        Assert.Same(original, battle.Active(1).Def);
        Assert.Equal("tricky", battle.Active(1).Def.AiStyle);
        Assert.Equal(Ai.Choose(reference, 1).ToString(), Ai.Choose(battle, 1).ToString());
        Assert.Equal(reference.Clone().Random(), battle.Clone().Random());
    }

    [Fact]
    public void NullOpponentStylePreservesAllRealWave3NativeDefinitionsAndChoices()
    {
        var db = BattleTests.LoadPack("original_wave3");
        var opponent = Opponent(db, db.Streamers.Skip(6).Take(6).Select(streamer => streamer.Id), null);
        var battle = BattleFor(db, opponent);
        var reference = battle.Clone();
        string contentHash = db.ContentHash(), record = battle.Record.ToJson();
        ulong stateHash = battle.StateHash();
        double next = reference.Clone().Random();
        Assert.All(db.Streamers, streamer => Assert.Equal(streamer.Id, streamer.AiStyle));

        AiOpponents.ApplyStyle(battle, 1, opponent);

        foreach (var side in battle.Sides)
        foreach (var fighter in side.Team)
        {
            Assert.Same(db.Streamer(fighter.Def.Id), fighter.Def);
            Assert.Equal(fighter.Def.Id, fighter.Def.AiStyle);
        }
        Assert.Equal(contentHash, db.ContentHash());
        Assert.Equal(record, battle.Record.ToJson());
        Assert.Equal(stateHash, battle.StateHash());
        Assert.Equal(next, battle.Clone().Random());
        Assert.Equal(Ai.Choose(reference, 1, AiLevel.Normal).ToString(), Ai.Choose(battle, 1, AiLevel.Normal).ToString());
        Assert.Equal(reference.Clone().Random(), battle.Clone().Random());
    }

    [Fact]
    public void GenericOpponentStylesOverrideOnlyTheComputerSideOfRealWave3MirrorTeams()
    {
        var db = BattleTests.LoadPack("original_wave3");
        string contentHash = db.ContentHash();
        foreach (string style in new[] { "aggressive", "defensive", "tricky" })
        {
            var opponent = Opponent(db, db.Streamers.Take(6).Select(streamer => streamer.Id), style);
            var battle = BattleFor(db, opponent, mirror: true);
            var untouched = battle.Clone();
            string record = battle.Record.ToJson();
            ulong stateHash = battle.StateHash();

            AiOpponents.ApplyStyle(battle, 1, opponent);

            for (int index = 0; index < 6; index++)
            {
                var player = battle.Sides[0].Team[index].Def;
                var computer = battle.Sides[1].Team[index].Def;
                Assert.Same(db.Streamer(player.Id), player);
                Assert.Equal(player.Id, player.AiStyle);
                Assert.Equal(style, computer.AiStyle);
                Assert.NotSame(player, computer);
                Assert.NotSame(player.Stats, computer.Stats);
                Assert.NotSame(player.Skills, computer.Skills);
                Assert.Equal(untouched.Sides[1].Team[index].Def.Id, untouched.Sides[1].Team[index].Def.AiStyle);
            }
            Assert.Equal(contentHash, db.ContentHash());
            Assert.Equal(record, battle.Record.ToJson());
            Assert.Equal(stateHash, battle.StateHash());
            Assert.Equal(untouched.Clone().Random(), battle.Clone().Random());
            Assert.Equal(Ai.Choose(untouched, 0, AiLevel.Normal).ToString(), Ai.Choose(battle, 0, AiLevel.Normal).ToString());
            Assert.Equal(untouched.Clone().Random(), battle.Clone().Random());
        }
    }

    [Fact]
    public void EveryRegisteredWave3StyleCanBeParsedAndAppliedWithoutChangingNativePlayerStyles()
    {
        var db = BattleTests.LoadPack("original_wave3");
        string contentHash = db.ContentHash();
        foreach (string style in db.Streamers.Select(streamer => streamer.AiStyle))
        {
            Assert.NotNull(AiStyles.Get(style));
            var rows = Rows(db);
            rows[0]["style"] = style;
            var opponent = AiOpponents.FromJson(db, Json(rows, db.PackId))[0];
            var battle = BattleFor(db, opponent, mirror: true);
            var reference = battle.Clone();
            string record = battle.Record.ToJson();
            ulong stateHash = battle.StateHash();

            AiOpponents.ApplyStyle(battle, 1, opponent);

            Assert.Equal(style, opponent.Style);
            Assert.All(battle.Sides[1].Team, fighter => Assert.Equal(style, fighter.Def.AiStyle));
            Assert.All(battle.Sides[0].Team, fighter =>
            {
                Assert.Same(db.Streamer(fighter.Def.Id), fighter.Def);
                Assert.Equal(fighter.Def.Id, fighter.Def.AiStyle);
            });
            Assert.Equal(contentHash, db.ContentHash());
            Assert.Equal(record, battle.Record.ToJson());
            Assert.Equal(stateHash, battle.StateHash());
            Assert.Equal(reference.Clone().Random(), battle.Clone().Random());
        }
    }

    [Fact]
    public void UnknownOrNonExactWave3StyleNamesAreRejectedWithoutReplacingNativeDefinitions()
    {
        var db = BattleTests.LoadPack("original_wave3");
        foreach (string style in new[] { "og3_unknown", "OG3_LILING", "og3_liling " })
        {
            var rows = Rows(db);
            rows[0]["style"] = style;
            Assert.Throws<FormatException>(() => AiOpponents.FromJson(db, Json(rows, db.PackId)));
            var opponent = Opponent(db, db.Streamers.Take(6).Select(streamer => streamer.Id), style);
            var battle = BattleFor(db, opponent, mirror: true);
            string contentHash = db.ContentHash(), record = battle.Record.ToJson();
            ulong stateHash = battle.StateHash();
            double next = battle.Clone().Random();

            Assert.Throws<FormatException>(() => AiOpponents.ApplyStyle(battle, 1, opponent));

            foreach (var side in battle.Sides)
            foreach (var fighter in side.Team)
            {
                Assert.Same(db.Streamer(fighter.Def.Id), fighter.Def);
                Assert.Equal(fighter.Def.Id, fighter.Def.AiStyle);
            }
            Assert.Equal(contentHash, db.ContentHash());
            Assert.Equal(record, battle.Record.ToJson());
            Assert.Equal(stateHash, battle.StateHash());
            Assert.Equal(next, battle.Clone().Random());
        }
    }

    [Fact]
    public void PolicyFiltersOnlyUnavailableOpponentsAndCannotHideUnknownMembers()
    {
        var plain = BattleTests.LoadPack("original");
        var rows = Rows(plain);
        string disabled = plain.Streamers[0].Id;
        rows[0]["team"] = plain.Streamers.Take(6).Select(streamer => streamer.Id).ToArray();
        var db = PolicyDb(JsonSerializer.Serialize(new { disabledStreamers = new[] { disabled } }));
        string hash = db.ContentHash();

        var opponents = AiOpponents.FromJson(db, Json(rows));

        Assert.Equal(5, opponents.Count);
        Assert.DoesNotContain(opponents, opponent => opponent.Id == "review_0");
        Assert.All(opponents, opponent => Assert.Empty(TeamCheck.Validate(db, null, opponent.Team)));
        Assert.Equal(hash, db.ContentHash());
        ((string[])rows[0]["team"])[1] = "unknown_not_disabled";
        var error = Assert.Throws<FormatException>(() => AiOpponents.FromJson(db, Json(rows)));
        Assert.Contains("unknown_not_disabled", error.Message);
    }

    [Fact]
    public void FindingACachedDisabledOpponentReportsPolicyAndApplyingCannotBypassIt()
    {
        var plain = BattleTests.LoadPack("original");
        var cached = AiOpponents.For(plain)[0];
        string disabled = cached.Team[0];
        var db = PolicyDb(JsonSerializer.Serialize(new { disabledStreamers = new[] { disabled } }));
        var error = Assert.Throws<PackDisabledException>(() => AiOpponents.Find(db, cached.Id));
        Assert.Contains(disabled, error.Message);
        var safe = Opponent(db, db.Streamers.Take(6).Select(streamer => streamer.Id));
        var battle = BattleFor(db, safe);
        var original = battle.Active(1).Def;

        error = Assert.Throws<PackDisabledException>(() => AiOpponents.ApplyStyle(battle, 1, cached));

        Assert.Contains(disabled, error.Message);
        Assert.Same(original, battle.Active(1).Def);
        Assert.Null(AiOpponents.Find(db, "no_such_opponent"));
        var first = AiOpponents.Find(plain, cached.Id);
        first.Team.Clear();
        Assert.Equal(cached.Team, AiOpponents.Find(plain, cached.Id).Team);
    }

    [Fact]
    public void DisabledPacksAreRejectedEvenWhenTheyHaveNoCatalogEntry()
    {
        var db = PolicyDb("{\"disabledPacks\":[\"hidden\"]}");
        db.PackId = "hidden";
        Assert.Throws<PackDisabledException>(() => AiOpponents.FromJson(db, "{}"));
        Assert.Throws<PackDisabledException>(() => AiOpponents.Find(db, "unknown"));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("escaped_field")]
    [InlineData("unknown_field")]
    [InlineData("id_whitespace")]
    [InlineData("team_control")]
    public void AmbiguousOrMisspelledCatalogDataIsRejected(string fault)
    {
        var db = BattleTests.LoadPack("original");
        var rows = Rows(db);
        if (fault == "unknown_field") rows[0]["stlye"] = "aggressive";
        if (fault == "id_whitespace") rows[0]["id"] = " review_0";
        if (fault == "team_control") ((string[])rows[0]["team"])[0] = "line\nbreak";
        string json = Json(rows);
        if (fault == "root") json = "{\"original\":" + JsonSerializer.Serialize(rows)
            + ",\"or\\u0069ginal\":" + JsonSerializer.Serialize(rows) + "}";
        if (fault == "escaped_field") json = json.Replace("\"name\":", "\"name\":\"first\",\"na\\u006de\":");

        Assert.Throws<FormatException>(() => AiOpponents.FromJson(db, json));
    }

    [Fact]
    public void MixedContentCanUseAnExplicitRosterWithoutChangingItsSourceIdentity()
    {
        var db = ContentDb.Merge(BattleTests.LoadPack("original"), BattleTests.LoadPack("original_wave2"));
        string hash = db.ContentHash();
        var opponents = AiOpponents.FromJson(db, Json(Rows(db), db.PackId));
        var battle = BattleFor(db, opponents[1]);

        AiOpponents.ApplyStyle(battle, 1, opponents[1]);

        Assert.Equal(db.PackId, opponents[1].PackId);
        Assert.Equal(hash, db.ContentHash());
        Assert.Equal(hash, battle.Record.ContentHash);
        Assert.Empty(AiOpponents.For(db));
    }
}
