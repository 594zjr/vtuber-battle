using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class T30_ContentHashEdgeTests
{
    static Battle Make(ContentDb db)
    {
        return new Battle(db, new IList<string>[]
        {
            new[] { "bell", "enka", "shiori", "lumi", "hane", "momo" },
            new[] { "kei", "yuan", "nyako", "buck", "kasumi", "bee" }
        }, new[] { "A", "B" }, 31);
    }

    static Dictionary<string, T> Reverse<T>(Dictionary<string, T> source)
    {
        return source.Reverse().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    static string RewriteJson(string json, params string[] omitRootProperties)
    {
        using (var document = JsonDocument.Parse(json))
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream))
            {
                void Write(JsonElement value, bool root = false)
                {
                    if (value.ValueKind == JsonValueKind.Object)
                    {
                        writer.WriteStartObject();
                        foreach (var property in value.EnumerateObject().Reverse())
                        {
                            if (root && omitRootProperties.Contains(property.Name)) continue;
                            writer.WritePropertyName(property.Name);
                            Write(property.Value);
                        }
                        writer.WriteEndObject();
                    }
                    else if (value.ValueKind == JsonValueKind.Array)
                    {
                        writer.WriteStartArray();
                        foreach (var item in value.EnumerateArray()) Write(item);
                        writer.WriteEndArray();
                    }
                    else value.WriteTo(writer);
                }
                Write(document.RootElement, true);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    [Fact]
    public void DictionaryInsertionOrderAndStreamerListOrderDoNotChangeTheFingerprint()
    {
        var db = BattleTests.LoadPack("original");
        string expected = db.ContentHash();
        db.StatNames = Reverse(db.StatNames);
        db.CatNames = Reverse(db.CatNames);
        db.Types = Reverse(db.Types);
        db.Chart = Reverse(db.Chart);
        foreach (string id in db.Chart.Keys.ToArray()) db.Chart[id] = Reverse(db.Chart[id]);
        db.Statuses = Reverse(db.Statuses);
        db.Fields = Reverse(db.Fields);
        db.Personas = Reverse(db.Personas);
        foreach (var persona in db.Personas.Values) persona.Params = Reverse(persona.Params);
        db.Skills = Reverse(db.Skills);
        foreach (var skill in db.Skills.Values.Concat(new[] { db.Struggle }))
            foreach (var effect in skill.Effects) effect.Changes = Reverse(effect.Changes);
        db.Streamers.Reverse();
        foreach (var streamer in db.Streamers) streamer.Stats = Reverse(streamer.Stats);
        db.Seasons = Reverse(db.Seasons);
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        var skillKeys = db.Skills.Keys.ToArray();

        Assert.Equal(expected, db.ContentHash());
        Assert.Equal(ids, db.Streamers.Select(s => s.Id));
        Assert.Equal(skillKeys, db.Skills.Keys);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("fan_asoul_vr")]
    public void JsonObjectKeyOrderAndWhitespaceDoNotAffectLoadedContent(string pack)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Content");
        string rules = File.ReadAllText(Path.Combine(root, "StreamDuelRules.json"));
        string content = File.ReadAllText(Path.Combine(root, "Packs", pack + ".json"));

        var reordered = ContentDb.FromJson(RewriteJson(rules), RewriteJson(content));

        Assert.Equal(BattleTests.LoadPack(pack).ContentHash(), reordered.ContentHash());
    }

    [Fact]
    public void CultureDoesNotChangeNumericFormattingOrKeyOrdering()
    {
        var db = BattleTests.LoadPack("original");
        db.Personas["comeback"].Params["below"] = 0.1234567890123456;
        foreach (string key in new[] { "I", "ı", "ä", "z" }) db.StatNames[key] = "显示 " + key;
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            string expected = db.ContentHash();
            Assert.Matches("^[0-9a-f]{16}$", expected);
            foreach (string name in new[] { "tr-TR", "de-DE", "fr-FR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                Assert.Equal(expected, db.ContentHash());
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    static Dictionary<string, Action<ContentDb>> Changes()
    {
        return new Dictionary<string, Action<ContentDb>>
        {
            { "pack.id", db => db.PackId += "_changed" },
            { "pack.name", db => db.PackName += "新" },
            { "pack.notice", db => db.PackNotice += "新" },
            { "stat.label", db => db.StatNames["atk"] += "新" },
            { "category.label", db => db.CatNames["talent"] += "新" },
            { "track.name", db => db.Types["song"].Name += "新" },
            { "track.color", db => db.Types["song"].Color = "#010203" },
            { "chart.multiplier", db => db.Chart["song"]["talk"] += 0.125 },
            { "status.damage", db => db.Statuses["flame"].Dot += 0.000000000001 },
            { "status.skip", db => db.Statuses["stall"].Skip++ },
            { "status.cleanse", db => db.Statuses["flame"].Cleanse = false },
            { "status.description", db => db.Statuses["afk"].Desc += "新" },
            { "field.reflection", db => db.Fields["mirror"].ReflectDamage += 0.01 },
            { "field.immunity", db => db.Fields["firewall"].ImmuneWeak = false },
            { "field.switchLock", db => db.Fields["lockin"].PreventSwitch = false },
            { "field.maxStacks", db => db.Fields["hype"].MaxStacks++ },
            { "field.timing", db => db.Fields["shield"].When += "新" },
            { "field.description", db => db.Fields["shield"].Desc += "新" },
            { "persona.code", db => db.Personas["comeback"].Code = "T_EXECUTE" },
            { "persona.parameter", db => db.Personas["comeback"].Params["below"] += 0.000000000001 },
            { "persona.status", db => db.Personas["trollback"].Status = "stall" },
            { "persona.field", db => db.Personas["beauty"].Field = "shield" },
            { "persona.node", db => db.Personas["comeback"].Node += "新" },
            { "persona.description", db => db.Personas["comeback"].Desc += "新" },
            { "skill.power", db => db.Skills["warm_up"].Power++ },
            { "skill.pp", db => db.Skills["warm_up"].Pp++ },
            { "skill.accuracy", db => db.Skills["warm_up"].Acc-- },
            { "skill.priority", db => db.Skills["warm_up"].Prio++ },
            { "skill.name", db => db.Skills["warm_up"].Name += "新" },
            { "effect.timing", db => db.Skills["high_note"].Effects[0].At = "post" },
            { "effect.chance", db => db.Skills["high_note"].Effects[0].Chance++ },
            { "effect.target", db => db.Skills["high_note"].Effects[0].Target = "self" },
            { "effect.turns", db => db.Skills["mod_guard"].Effects[0].Turns++ },
            { "effect.count", db => db.Skills["meme_punch"].Effects[0].N++ },
            { "effect.fraction", db => db.Skills["deep_breath"].Effects[0].Frac += 0.01 },
            { "effect.multiplier", db => db.Skills["endgame"].Effects[0].Mul += 0.01 },
            { "effect.perStack", db => db.Skills["iconic_scene"].Effects[0].Per += 0.01 },
            { "effect.consume", db => db.Skills["iconic_scene"].Effects[0].Consume = false },
            { "effect.stages", db => db.Skills["pregame"].Effects[0].Changes["atk"]++ },
            { "effect.conditionKind", db => db.Skills["high_note"].Effects[0].Cond.Kind = "actedFirst" },
            { "effect.conditionValue", db => db.Skills["high_note"].Effects[0].Cond.Value += 0.01 },
            { "streamer.stats", db => db.Streamer("bell").Stats["spd"]++ },
            { "streamer.secondTrack", db => db.Streamer("bell").Type2 = "talk" },
            { "streamer.persona", db => db.Streamer("bell").Persona = "pure" },
            { "streamer.fifth", db => db.Streamer("bell").Fifth = "full_volume" },
            { "streamer.name", db => db.Streamer("bell").Name += "新" },
            { "streamer.blurb", db => db.Streamer("bell").Blurb += "新" },
            { "streamer.org", db => db.Streamer("bell").Org = "测试机构" },
            { "struggle.power", db => db.Struggle.Power++ },
            { "struggle.effect", db => db.Struggle.Effects[0].Frac += 0.01 },
            { "season.name", db => db.Seasons["s1"].Name += "新" },
            { "season.banned", db => db.Seasons["s1"].Banned.Add("bell") },
            { "season.limit", db => db.Seasons["s1"].Limited[0].Max++ },
            { "season.pool", db => db.Seasons["s1"].Limited[0].Ids.Add("enka") }
        };
    }

    public static IEnumerable<object[]> ContentChanges => Changes().Keys.Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(ContentChanges))]
    public void GameplayNestedParametersAndDisplayChangesAffectTheFingerprint(string path)
    {
        var db = BattleTests.LoadPack("original");
        string before = db.ContentHash();

        Changes()[path](db);

        Assert.NotEqual(before, db.ContentHash());
    }

    [Theory]
    [InlineData("effects")]
    [InlineData("skillSlots")]
    public void OrderedGameplayListsRemainOrderSensitive(string list)
    {
        var db = BattleTests.LoadPack("original");
        string before = db.ContentHash();
        if (list == "effects") db.Skills["redraw"].Effects.Reverse();
        else db.Streamer("bell").Skills.Reverse();

        Assert.NotEqual(before, db.ContentHash());
    }

    [Fact]
    public void AdjacentTextFieldsKeepTheirBoundariesIncludingUnicode()
    {
        var first = BattleTests.LoadPack("original");
        var second = BattleTests.LoadPack("original");
        first.PackName = "甲\0乙";
        first.PackNotice = "丙🎤";
        second.PackName = "甲\0";
        second.PackNotice = "乙丙🎤";

        Assert.NotEqual(first.ContentHash(), second.ContentHash());
    }

    [Fact]
    public void FingerprintingDoesNotChangeBattleStateAndRecordsKeepTheInitialFingerprint()
    {
        var db = BattleTests.LoadPack("original");
        string fingerprint = db.ContentHash();
        var b = Make(db);
        ulong state = b.StateHash();
        string record = b.Record.ToJson();
        db.ContentHash();
        db.ContentHash();
        Assert.Equal(state, b.StateHash());
        Assert.Equal(record, b.Record.ToJson());
        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        Assert.Equal(fingerprint, db.ContentHash());
        Assert.Equal(fingerprint, b.Record.ContentHash);

        db.Skills["warm_up"].Power++;

        Assert.Equal(fingerprint, b.Record.ContentHash);
        Assert.NotEqual(db.ContentHash(), b.Record.ContentHash);
        Assert.Throws<InvalidOperationException>(() => Replay.Run(db, b.Record));
    }

    [Fact]
    public void SameContentWithReorderedSourceReplaysTheVersionThreeRecordExactly()
    {
        var db = BattleTests.LoadPack("original");
        var b = Make(db);
        b.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
        string json = b.Record.ToJson();
        var replayDb = BattleTests.LoadPack("original");
        replayDb.Skills = Reverse(replayDb.Skills);
        var record = BattleRecord.FromJson(RewriteJson(json));

        var replayed = Replay.Run(replayDb, record);

        Assert.Equal(3, record.Version);
        Assert.Equal(db.ContentHash(), record.ContentHash);
        Assert.Equal(json, replayed.Record.ToJson());
        Assert.Equal(b.StateHash(), replayed.StateHash());
        Assert.Equal(b.Log.Select(l => l.Node + l.Text), replayed.Log.Select(l => l.Node + l.Text));
    }

    [Fact]
    public void VersionOneRecordWithoutFingerprintStillReplaysChangedContent()
    {
        var b = Make(BattleTests.LoadPack("original"));
        b.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
        var old = BattleRecord.FromJson(RewriteJson(b.Record.ToJson(), "version", "contentHash"));
        var changed = BattleTests.LoadPack("original");
        changed.Skills["warm_up"].Power++;

        Assert.Equal(1, old.Version);
        Assert.Null(old.ContentHash);
        var replayed = Replay.Run(changed, old);
        Assert.Equal(Phase.Choose, replayed.Phase);
        Assert.Equal(2, replayed.Turn);
        Assert.Equal(b.Log.Select(l => l.Node + l.Text), replayed.Log.Select(l => l.Node + l.Text));
    }

    [Theory]
    [InlineData("numeric")]
    [InlineData("display")]
    public void FingerprintMismatchIsDetectedBeforeConstructingOrExecutingTheReplay(string change)
    {
        var b = Make(BattleTests.LoadPack("original"));
        var record = BattleRecord.FromJson(b.Record.ToJson());
        var changed = BattleTests.LoadPack("original");
        if (change == "numeric") changed.Fields["shield"].DmgTakenMul += 0.01;
        else changed.Streamer("momo").Blurb += "新";
        record.Teams[0][0] = "missing_streamer";

        var error = Assert.Throws<InvalidOperationException>(() => Replay.Run(changed, record));

        Assert.Contains("内容不一致", error.Message);
    }

    [Fact]
    public void ChangedPackUsesFingerprintMismatchWhileLegacyRecordsKeepThePackIdCheck()
    {
        var db = BattleTests.LoadPack("original");
        var record = BattleRecord.FromJson(Make(db).Record.ToJson());
        db.PackId = "other_pack";

        var currentError = Assert.Throws<InvalidOperationException>(() => Replay.Run(db, record));
        Assert.Contains("内容不一致", currentError.Message);

        record.Version = 1;
        record.ContentHash = null;
        var legacyError = Assert.Throws<InvalidOperationException>(() => Replay.Run(db, record));
        Assert.Contains("内容包不匹配", legacyError.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void ExplicitPositiveVersionsAreReadWithoutAddingAnUnsupportedVersionRule(int version)
    {
        var db = BattleTests.LoadPack("original");
        string json = Make(db).Record.ToJson().Replace("\"version\":3", "\"version\":" + version.ToString(CultureInfo.InvariantCulture));

        var record = BattleRecord.FromJson(json);

        Assert.Equal(version, record.Version);
        Assert.Equal(Phase.Choose, Replay.Run(db, record).Phase);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2.5")]
    [InlineData("2147483648")]
    [InlineData("\"2\"")]
    [InlineData("null")]
    public void InvalidVersionValuesAreRejected(string value)
    {
        string json = Make(BattleTests.LoadPack("original")).Record.ToJson();
        json = json.Replace("\"version\":3", "\"version\":" + value);

        Assert.Throws<FormatException>(() => BattleRecord.FromJson(json));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    public void PresentNonStringFingerprintsAreRejected(string value)
    {
        var b = Make(BattleTests.LoadPack("original"));
        string json = b.Record.ToJson().Replace("\"contentHash\":\"" + b.Record.ContentHash + "\"", "\"contentHash\":" + value);

        Assert.Throws<FormatException>(() => BattleRecord.FromJson(json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PresentFingerprintsUseExactComparisonEvenForVersionOne(bool empty)
    {
        var db = BattleTests.LoadPack("original");
        var record = BattleRecord.FromJson(Make(db).Record.ToJson());
        record.Version = 1;
        record.ContentHash = empty ? "" : record.ContentHash.ToUpperInvariant();
        Assert.NotEqual(db.ContentHash(), record.ContentHash);

        var error = Assert.Throws<InvalidOperationException>(() => Replay.Run(db, record));

        Assert.Contains("内容不一致", error.Message);
    }
}
