using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using StreamDuel.Core;
using Xunit;

public class T131_RulesetShareTests
{
    const string Prefix = "SDRULE1-";

    [Fact]
    public void CodeRoundTripPreservesEveryFieldIncludingNullAndOrderedModifiers()
    {
        var db = BattleTests.LoadPack("original");
        foreach (var modifiers in new[] { null, new List<string>(), new List<string> { "hype_start", "half_pp", "hype_start" } })
        {
            var source = Rule();
            source.Id = "custom/中文\"\\🦊";
            source.Name = "周末友谊赛 🦊";
            source.Modifiers = modifiers;
            source.TurnSeconds = int.MaxValue;
            source.BankSeconds = int.MaxValue;
            string json = source.ToJson();

            string code = RulesetCode.Encode(source);
            Assert.StartsWith(Prefix, code);
            Assert.DoesNotContain("=", code);
            Assert.Equal(json, RulesetCode.Decode(code, db).ToJson());
            Assert.Equal(code, RulesetCode.Encode(RulesetCode.Decode(code)));
            Assert.Equal(json, source.ToJson());
        }
        var defaults = new Ruleset();
        Assert.Equal(defaults.ToJson(), RulesetCode.Decode(RulesetCode.Encode(defaults)).ToJson());
    }

    [Fact]
    public void SavedImportedAndExportedCustomRulesetsKeepAllGameplayFields()
    {
        using var dir = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var source = Rule();
        string before = source.ToJson();
        var first = new FileSaveStore(Path.Combine(dir.PathName, "first"));
        var saved = first.SaveRuleset("朋友赛制", source, db);
        var second = new FileSaveStore(Path.Combine(dir.PathName, "second"));

        var imported = second.ImportRuleset(RulesetCode.Encode(saved), db);

        Assert.Equal("朋友赛制", imported.Name);
        Assert.Equal(saved.ToJson(), imported.ToJson());
        Assert.Equal(saved.ToJson(), Assert.Single(second.LoadProfile().MyRulesets).ToJson());
        Assert.Equal(saved.ToJson(), RulesetCode.Decode(RulesetCode.Encode(imported), db).ToJson());
        Assert.Equal(before, source.ToJson());
    }

    [Fact]
    public void ChangingOnePayloadCharacterFailsChecksumBeforeParsing()
    {
        string code = RulesetCode.Encode(Rule());
        var changed = code.ToCharArray();
        int index = Prefix.Length + 3;
        changed[index] = changed[index] == 'A' ? 'B' : 'A';
        Assert.Equal(1, changed.Zip(code, (a, b) => a != b ? 1 : 0).Sum());

        var error = Assert.Throws<FormatException>(() => RulesetCode.Decode(new string(changed)));

        Assert.Contains("校验", error.Message);
    }

    [Fact]
    public void CopyableWhitespaceDoesNotChangeTheCode()
    {
        string code = RulesetCode.Encode(Rule());
        string spaced = " \r\n" + string.Join("\t\u3000", code.Select(c => c.ToString())) + "\n ";
        Assert.Equal(Rule().ToJson(), RulesetCode.Decode(spaced).ToJson());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SDRULE1-")]
    [InlineData("sdrule1-AAAA")]
    [InlineData("SDRULE2-AAAA")]
    [InlineData("SDT1-AAAA")]
    [InlineData("SDRULE1-A")]
    [InlineData("SDRULE1-AAAA=")]
    [InlineData("SDRULE1-AAA+")]
    public void InvalidCodeEnvelopesFailWithChineseErrors(string code)
    {
        Chinese(Assert.Throws<FormatException>(() => RulesetCode.Decode(code)));
    }

    [Theory]
    [InlineData("bestOf")]
    [InlineData("modifier")]
    [InlineData("season")]
    [InlineData("clock")]
    public void InvalidRulesetImportLeavesExistingProfileBytesUnchanged(string invalid)
    {
        using var dir = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var store = new FileSaveStore(dir.PathName);
        store.SaveProfile(PopulatedProfile());
        store.SaveRuleset("已经保存", Rule(), db);
        byte[] before = File.ReadAllBytes(dir.ProfilePath);
        var source = Rule();
        if (invalid == "bestOf") source.BestOf = 2;
        if (invalid == "modifier") source.Modifiers = new List<string> { "not_a_modifier" };
        if (invalid == "season") source.Season = "missing_season";
        if (invalid == "clock") source.TurnSeconds = -1;
        string code = RulesetCode.Encode(source);
        Assert.Equal(source.ToJson(), RulesetCode.Decode(code).ToJson());

        Chinese(Assert.Throws<KeyError>(() => RulesetCode.FromJson(source.ToJson(), db)));
        Chinese(Assert.Throws<KeyError>(() => store.ImportRuleset(code, db)));
        Chinese(Assert.Throws<KeyError>(() => store.SaveRuleset("不应保存", source, db)));

        Assert.Equal(before, File.ReadAllBytes(dir.ProfilePath));
        Assert.False(File.Exists(Path.Combine(dir.PathName, ".profile.json.tmp")));
    }

    [Fact]
    public void TwentyCustomRulesetsAreAllowedAndDeletingOneFreesOneSlot()
    {
        using var dir = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var store = new FileSaveStore(dir.PathName);
        for (int i = 0; i < 20; i++) store.SaveRuleset("赛制 " + i, Rule(), db);
        byte[] before = File.ReadAllBytes(dir.ProfilePath);

        Chinese(Assert.Throws<KeyError>(() => store.SaveRuleset("第21个", Rule(), db)));
        var extra = Rule();
        extra.Name = "分享的第21个";
        Chinese(Assert.Throws<KeyError>(() => store.ImportRuleset(RulesetCode.Encode(extra), db)));
        Assert.Equal(before, File.ReadAllBytes(dir.ProfilePath));
        Assert.Equal(20, store.LoadProfile().MyRulesets.Count);

        store.RenameRuleset("赛制 0", "新名称");
        Assert.Equal(20, store.LoadProfile().MyRulesets.Count);
        store.DeleteRuleset("赛制 19");
        store.SaveRuleset("补上空位", Rule(), db);
        var loaded = new FileSaveStore(dir.PathName).LoadProfile();
        Assert.Equal(20, loaded.MyRulesets.Count);
        Assert.Contains(loaded.MyRulesets, rule => rule.Name == "新名称");
        Assert.Contains(loaded.MyRulesets, rule => rule.Name == "补上空位");
        Assert.DoesNotContain(loaded.MyRulesets, rule => rule.Name == "赛制 19");
    }

    [Theory]
    [InlineData("casual")]
    [InlineData("ranked")]
    [InlineData("chaos")]
    [InlineData("休闲")]
    [InlineData("排位")]
    [InlineData("乱斗")]
    public void BuiltInNamesCannotBeDeletedRenamedOrShadowed(string name)
    {
        using var dir = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var store = new FileSaveStore(dir.PathName);
        store.SaveRuleset("自己的赛制", Rule(), db);
        byte[] before = File.ReadAllBytes(dir.ProfilePath);

        Chinese(Assert.Throws<KeyError>(() => store.DeleteRuleset(name)));
        Chinese(Assert.Throws<KeyError>(() => store.RenameRuleset(name, "覆盖预设")));
        Chinese(Assert.Throws<KeyError>(() => store.SaveRuleset(name, Rule(), db)));
        Chinese(Assert.Throws<KeyError>(() => store.RenameRuleset("自己的赛制", name)));

        Assert.Equal(before, File.ReadAllBytes(dir.ProfilePath));
        Assert.NotNull(store.LoadProfile().GetRuleset(name));
    }

    [Fact]
    public void BuiltInCopiesCanBeEditedWithoutChangingTheOriginalOrLookupById()
    {
        var db = BattleTests.LoadPack("original");
        var profile = new Profile();
        var ranked = profile.GetRuleset("ranked");
        string original = ranked.ToJson();
        ranked.BestOf = 5;
        ranked.Modifiers.Add("half_pp");

        profile.AddRuleset("排位副本", ranked, db);

        Assert.Equal(5, profile.GetRuleset("排位副本").BestOf);
        Assert.Equal("ranked", profile.GetRuleset("排位副本").Id);
        Assert.Equal(original, profile.GetRuleset("ranked").ToJson());
        Assert.Equal(original, profile.GetRuleset("排位").ToJson());
        Assert.Equal(original, Rulesets.All.Single(rule => rule.Id == "ranked").ToJson());
    }

    [Fact]
    public void FilteredNamesAreUniqueForSavingImportingAndRenaming()
    {
        using var dir = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var store = new FileSaveStore(dir.PathName);
        var saved = store.SaveRuleset("  夜\u200b间\n赛  ", Rule(), db);
        Assert.Equal("夜间赛", saved.Name);
        store.SaveRuleset("另一个", Rule(), db);
        byte[] before = File.ReadAllBytes(dir.ProfilePath);
        var duplicate = Rule();
        duplicate.Name = "夜间\u200b赛";

        Assert.Throws<KeyError>(() => store.SaveRuleset("夜间\u200b赛", Rule(), db));
        Assert.Throws<KeyError>(() => store.ImportRuleset(RulesetCode.Encode(duplicate), db));
        Assert.Throws<KeyError>(() => store.RenameRuleset("另一个", " 夜间赛 "));

        Assert.Equal(before, File.ReadAllBytes(dir.ProfilePath));
        string longName = new string('甲', 15) + "🦊";
        Assert.Equal(longName, store.SaveRuleset(longName + "乙", Rule(), db).Name);
        before = File.ReadAllBytes(dir.ProfilePath);
        Assert.Throws<KeyError>(() => store.SaveRuleset(longName + "丙", Rule(), db));
        Assert.Equal(before, File.ReadAllBytes(dir.ProfilePath));
        Assert.Equal("我的赛制", store.SaveRuleset("\0\u200b", Rule(), db).Name);
    }

    [Fact]
    public void OldLookupNamesAreNotSilentlyFilteredIntoAnotherSavedRuleset()
    {
        var profile = new Profile();
        profile.AddRuleset("周末赛", Rule(), BattleTests.LoadPack("original"));
        string before = profile.ToJson();

        Assert.Throws<KeyError>(() => profile.DeleteRuleset("周\u200b末赛"));
        Assert.Throws<KeyError>(() => profile.RenameRuleset("周\u200b末赛", "改名"));
        Assert.Throws<KeyError>(() => profile.GetRuleset("周\u200b末赛"));

        Assert.Equal(before, profile.ToJson());
    }

    [Fact]
    public void InputReturnedRulesetsAndSnapshotsCannotMutateTheStoredDefinition()
    {
        var profile = new Profile();
        var source = Rule();
        var added = profile.AddRuleset("副本", source, BattleTests.LoadPack("original"));
        string before = profile.ToJson();
        source.BestOf = 1;
        source.Modifiers.Clear();
        added.Name = "改掉返回值";
        added.Modifiers.Add("half_pp");
        var list = profile.MyRulesets;
        list[0].Season = "changed";
        list[0].Modifiers.Clear();
        var found = profile.GetRuleset("副本");
        found.LeadPick = false;
        found.Modifiers.Clear();

        Assert.Equal(before, profile.ToJson());
        Assert.NotSame(list, profile.MyRulesets);
        Assert.Equal(Rule().Modifiers, Assert.Single(profile.MyRulesets).Modifiers);
        var renamed = profile.RenameRuleset("副本", "新副本");
        renamed.Modifiers.Clear();
        Assert.Equal(Rule().Modifiers, profile.GetRuleset("新副本").Modifiers);
    }

    [Fact]
    public void LegacyProfilesHaveAnEmptyListAndRetainTheirOriginalSerialization()
    {
        const string legacy = "{\"packs\":{},\"ratings\":{\"默认\":{\"games\":3,\"rating\":1460}}}";
        var profile = Profile.FromJson(legacy);
        Assert.Empty(profile.MyRulesets);
        Assert.Equal(legacy, profile.ToJson());
        Assert.Empty(new Profile().MyRulesets);
        Assert.Empty(Profile.FromJson("{\"packs\":{},\"myRulesets\":[]}").MyRulesets);
        Assert.Equal("{\"packs\":{}}", new Profile().ToJson());
    }

    [Fact]
    public void RulesetChangesAndLaterRatedResultsPreserveEachOthersData()
    {
        using var dir = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var store = new FileSaveStore(dir.PathName);
        var original = PopulatedProfile();
        store.SaveProfile(original);
        string originalRating = original.Ratings[Profile.DefaultRatingFormat].ToJson();
        var initialStats = original.Packs[db.PackId];

        store.SaveRuleset("长盘", Rule(), db);
        store.RenameRuleset("长盘", "联赛");
        var imported = Rule();
        imported.Name = "临时赛";
        store.ImportRuleset(RulesetCode.Encode(imported), db);
        store.DeleteRuleset("临时赛");
        var after = store.LoadProfile();
        Assert.Equal(originalRating, after.Ratings[Profile.DefaultRatingFormat].ToJson());
        Assert.Equal(initialStats.Wins, after.Packs[db.PackId].Wins);
        Assert.Equal(initialStats.Losses, after.Packs[db.PackId].Losses);
        Assert.Equal(initialStats.Draws, after.Packs[db.PackId].Draws);
        Assert.Equal(initialStats.Picks, after.Packs[db.PackId].Picks);
        Assert.Equal(initialStats.PickWins, after.Packs[db.PackId].PickWins);
        string custom = Assert.Single(after.MyRulesets).ToJson();

        var completed = CompletedWin(db);
        store.RecordProfile(completed, 0, new PlayerRating { Rating = 1500, Games = 50 });
        var rated = new FileSaveStore(dir.PathName).LoadProfile();
        Assert.Equal(custom, Assert.Single(rated.MyRulesets).ToJson());
        Assert.Equal(initialStats.Wins + 1, rated.Packs[db.PackId].Wins);
        Assert.Equal(after.Ratings[Profile.DefaultRatingFormat].Games + 1, rated.Ratings[Profile.DefaultRatingFormat].Games);
        Assert.Equal(original.Ratings["保留赛制"].ToJson(), rated.Ratings["保留赛制"].ToJson());
        Assert.Equal(original.Ratings[Profile.DefaultRatingFormat].History[0].FinalRating,
            rated.Ratings[Profile.DefaultRatingFormat].History[0].FinalRating);
        store.NewRatingSeason();
        Assert.Equal(custom, Assert.Single(store.LoadProfile().MyRulesets).ToJson());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"packs\":{},\"myRulesets\":null}")]
    [InlineData("{\"packs\":{},\"myRulesets\":{}}")]
    [InlineData("{\"packs\":{},\"myRulesets\":[null]}")]
    [InlineData("{\"packs\":{},\"myRulesets\":[{\"name\":\"已有\",\"bestOf\":2}]}")]
    [InlineData("{\"packs\":{},\"myRulesets\":[{\"name\":\"已有\"},{\"name\":\"已有\"}]}")]
    public void CorruptProfilesAreNeverReplacedByRulesetOperations(string json)
    {
        using var dir = new TestDirectory();
        var store = new FileSaveStore(dir.PathName);
        var db = BattleTests.LoadPack("original");
        File.WriteAllText(dir.ProfilePath, json, new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(dir.ProfilePath);

        Chinese(Assert.Throws<FormatException>(() => store.SaveRuleset("新赛制", Rule(), db)));
        Chinese(Assert.Throws<FormatException>(() => store.ImportRuleset(RulesetCode.Encode(Rule()), db)));
        Chinese(Assert.Throws<FormatException>(() => store.RenameRuleset("已有", "新名称")));
        Chinese(Assert.Throws<FormatException>(() => store.DeleteRuleset("已有")));

        Assert.Equal(before, File.ReadAllBytes(dir.ProfilePath));
        Assert.False(File.Exists(Path.Combine(dir.PathName, ".profile.json.tmp")));
    }

    [Fact]
    public void InvalidProfileUtf8DoesNotGetReplacedDuringAnOtherwiseValidSave()
    {
        using var dir = new TestDirectory();
        var store = new FileSaveStore(dir.PathName);
        byte[] bad = Encoding.UTF8.GetBytes("{\"packs\":{},\"bad\":\"")
            .Concat(new byte[] { 0xc3, 0x28 }).Concat(Encoding.UTF8.GetBytes("\"}")).ToArray();
        File.WriteAllBytes(dir.ProfilePath, bad);

        Chinese(Assert.Throws<FormatException>(() => store.SaveRuleset("合法", Rule(), BattleTests.LoadPack("original"))));

        Assert.Equal(bad, File.ReadAllBytes(dir.ProfilePath));
    }

    [Fact]
    public void RulesetWritesUseTheSameProfileLockAsBattleAndRatingWrites()
    {
        using var dir = new TestDirectory();
        var store = new FileSaveStore(dir.PathName);
        store.SaveProfile(PopulatedProfile());
        byte[] before = File.ReadAllBytes(dir.ProfilePath);
        using var held = new FileStream(Path.Combine(dir.PathName, ".profile.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.ThrowsAny<IOException>(() => store.SaveRuleset("等待写入", Rule(), BattleTests.LoadPack("original")));

        Assert.Equal(before, File.ReadAllBytes(dir.ProfilePath));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"name\":\"友谊赛\",\"bestOf\":\"3\"}")]
    [InlineData("{\"name\":\"友谊赛\",\"banOne\":1}")]
    [InlineData("{\"name\":\"友谊赛\",\"modifiers\":[null]}")]
    [InlineData("{\"name\":\"友谊赛\",\"name\":\"重名字段\"}")]
    [InlineData("{\"name\":\"友谊赛\",\"bestOff\":3}")]
    [InlineData("{\"name\":\"友谊赛\",}")]
    [InlineData("{\"name\":\"友谊赛\"}{}")]
    public void ValidChecksumsDoNotAllowMalformedRulesetJson(string json)
    {
        Chinese(Assert.Throws<FormatException>(() => RulesetCode.Decode(Envelope(Compress(Encoding.UTF8.GetBytes(json))))));
        Chinese(Assert.Throws<FormatException>(() => RulesetCode.FromJson(json, BattleTests.LoadPack("original"))));
    }

    [Fact]
    public void DirectJsonAndCodeImportPreserveTheSameValidRuleset()
    {
        var db = BattleTests.LoadPack("original");
        var source = Rule();
        Assert.Equal(source.ToJson(), RulesetCode.FromJson(source.ToJson(), db).ToJson());
        Assert.Equal(RulesetCode.Decode(RulesetCode.Encode(source), db).ToJson(),
            RulesetCode.FromJson(source.ToJson(), db).ToJson());
    }

    [Fact]
    public void DirectJsonRejectsNullTextAndBothValidatedEntryPointsRequireContent()
    {
        var db = BattleTests.LoadPack("original");
        Chinese(Assert.Throws<FormatException>(() => RulesetCode.FromJson(null, db)));
        Assert.Throws<ArgumentNullException>(() => RulesetCode.FromJson(Rule().ToJson(), null));
        Assert.Throws<ArgumentNullException>(() => RulesetCode.Decode(RulesetCode.Encode(Rule()), null));
    }

    [Fact]
    public void InvalidUtf8AfterSuccessfulDecompressionIsNotSilentlyReplaced()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{\"name\":\"")
            .Concat(new byte[] { 0xed, 0xa0, 0x80 }).Concat(Encoding.UTF8.GetBytes("\"}")).ToArray();
        Chinese(Assert.Throws<FormatException>(() => RulesetCode.Decode(Envelope(Compress(bytes)))));
    }

    [Fact]
    public void JsonByteLimitAppliesBothToEncodingAndToHighlyCompressedInput()
    {
        const int limit = 64 * 1024;
        var source = Rule();
        source.Id = "";
        int available = limit - Encoding.UTF8.GetByteCount(source.ToJson());
        source.Id = new string('中', available / 3) + new string('a', available % 3);
        Assert.Equal(limit, Encoding.UTF8.GetByteCount(source.ToJson()));
        Assert.Equal(source.ToJson(), RulesetCode.Decode(RulesetCode.Encode(source)).ToJson());
        source.Id += "a";
        Assert.True(source.ToJson().Length < limit);
        Assert.Equal(limit + 1, Encoding.UTF8.GetByteCount(source.ToJson()));

        Chinese(Assert.Throws<FormatException>(() => RulesetCode.Encode(source)));
        Chinese(Assert.Throws<FormatException>(() => RulesetCode.FromJson(source.ToJson(), BattleTests.LoadPack("original"))));
        byte[] compressed = Compress(Encoding.UTF8.GetBytes(source.ToJson()));
        Assert.True(compressed.Length < 1024);
        Chinese(Assert.Throws<FormatException>(() => RulesetCode.Decode(Envelope(compressed))));
    }

    [Fact]
    public void TruncatedTrailingAndOversizedCompressedStreamsFailEvenWithRecomputedChecksums()
    {
        byte[] compressed = Compress(Encoding.UTF8.GetBytes(Rule().ToJson()));
        foreach (byte[] bytes in new[]
        {
            compressed.Take(compressed.Length - 1).ToArray(),
            compressed.Concat(new byte[] { 0 }).ToArray(),
            compressed.Concat(compressed).ToArray(),
            new byte[65 * 1024 + 1]
        })
            Chinese(Assert.Throws<FormatException>(() => RulesetCode.Decode(Envelope(bytes))));
    }

    static Ruleset Rule()
    {
        return new Ruleset
        {
            Id = "custom", Name = "我的联赛", Season = "s1", BanOne = true, LeadPick = true,
            BestOf = 5, Modifiers = new List<string> { "hype_start", "half_pp" }, TurnSeconds = 17, BankSeconds = 125
        };
    }

    static Profile PopulatedProfile()
    {
        var profile = new Profile();
        profile.Packs.Add("original", new PackStats
        {
            Wins = 7, Losses = 3, Draws = 2,
            Picks = new Dictionary<string, int> { { "enka", 9 }, { "bell", 3 } },
            PickWins = new Dictionary<string, int> { { "enka", 4 }, { "bell", 1 } }
        });
        var rating = new PlayerRating { Rating = 1800, Games = 12 };
        RatingSystem.NewSeason(rating);
        RatingSystem.Apply(rating, new PlayerRating { Rating = 1650, Games = 20 }, 0.5);
        profile.Ratings.Add(Profile.DefaultRatingFormat, rating);
        profile.Ratings.Add("保留赛制", new PlayerRating { Rating = 1400, Games = 18 });
        return profile;
    }

    static Battle CompletedWin(ContentDb db)
    {
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() },
            new[] { "甲", "乙" }, 131);
        battle.Surrender(1);
        return battle;
    }

    static void Chinese(Exception error)
    {
        Assert.Matches("[\u4e00-\u9fff]", error.Message);
    }

    static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
            deflate.Write(bytes, 0, bytes.Length);
        return output.ToArray();
    }

    // 坏载荷独立生成有效校验，确保实际经过解压、UTF-8 和 JSON 校验层。
    static string Envelope(byte[] compressed)
    {
        ulong checksum = 14695981039346656037UL;
        foreach (byte value in compressed) checksum = unchecked((checksum ^ value) * 1099511628211UL);
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
        {
            writer.Write(compressed);
            writer.Write(checksum);
        }
        return Prefix + Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        public string PathName { get; }
        public string ProfilePath => Path.Combine(PathName, "profile.json");

        public TestDirectory([CallerFilePath] string sourcePath = "")
        {
            parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath), "..", "Build", "T131", "temp"));
            PathName = Path.Combine(parent, "T131-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(PathName);
        }

        public void Dispose()
        {
            string actual = Path.GetFullPath(PathName);
            if (!actual.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("测试目录超出指定工作树。");
            if (Directory.Exists(actual)) Directory.Delete(actual, true);
        }
    }
}
