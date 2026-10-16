using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

[Collection("Localization current")]
public class T101_PackPolicyTests
{
    static string Resource(string path)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", path));
    }

    static string Rules() { return Resource("StreamDuelRules.json"); }
    static string Original() { return Resource(Path.Combine("Packs", "original.json")); }

    static PackPolicy Policy(string[] packs = null, string[] streamers = null,
        Dictionary<string, string> reasons = null)
    {
        return PackPolicy.FromJson(JsonSerializer.Serialize(new
        {
            disabledPacks = packs ?? Array.Empty<string>(),
            disabledStreamers = streamers ?? Array.Empty<string>(),
            reason = reasons ?? new Dictionary<string, string>()
        }));
    }

    static string WithPackId(string json, string id)
    {
        var root = (Dictionary<string, object>)MiniJson.Parse(json);
        root["id"] = id;
        return JsonSerializer.Serialize(root);
    }

    static Battle BattleFor(ContentDb db, uint seed)
    {
        var ids = db.Streamers.Select(streamer => streamer.Id).ToArray();
        return new Battle(db, new IList<string>[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() },
            new[] { "甲", "乙" }, seed);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"disabledPacks\":[]}")]
    [InlineData("{\"disabledPacks\":[],\"disabledStreamers\":[],\"reason\":{}}")]
    public void EmptyPolicyPreservesContentAndAiChoicesIncludingTheRandomStream(string json)
    {
        string rules = Rules(), pack = Original();
        var original = ContentDb.FromJson(rules, pack);
        var policy = PackPolicy.FromJson(json);
        var filtered = ContentDb.FromJson(rules, pack, policy);
        Assert.Empty(policy.DisabledPacks);
        Assert.Empty(policy.DisabledStreamers);
        Assert.Empty(policy.Reasons);
        Assert.NotNull(filtered.Policy);
        Assert.Equal(original.ContentHash(), filtered.ContentHash());
        Assert.Equal(original.Streamers.Select(streamer => streamer.Id), filtered.Streamers.Select(streamer => streamer.Id));
        foreach (uint seed in new uint[] { 101, 2026 })
            foreach (var level in new[] { AiLevel.Easy, AiLevel.Normal, AiLevel.Hard })
            {
                var before = BattleFor(original, seed);
                var after = BattleFor(filtered, seed);
                var first = new BattleAction[2];
                var second = new BattleAction[2];
                for (int side = 0; side < 2; side++)
                {
                    first[side] = Ai.Choose(before, side, level);
                    second[side] = Ai.Choose(after, side, level);
                    Assert.Equal(first[side].ToString(), second[side].ToString());
                }
                before.ResolveTurn(first[0], first[1]);
                after.ResolveTurn(second[0], second[1]);
                Assert.Equal(before.StateHash(), after.StateHash());
                Assert.Equal(before.Record.ToJson(), after.Record.ToJson());
                Assert.Equal(before.Log.Select(entry => entry.Node + "|" + entry.Text),
                    after.Log.Select(entry => entry.Node + "|" + entry.Text));
                for (int draw = 0; draw < 4; draw++) Assert.Equal(before.Random(), after.Random());
            }
    }

    [Fact]
    public void IdentifiersAreOrdinalAndTeamReasonUsesPackThenRosterOrder()
    {
        var policy = Policy(new[] { "original" }, new[] { "bell", "BELL", "bee" },
            new Dictionary<string, string> { { "original", "包下架原因" }, { "bell", "主播下架原因" } });
        Assert.True(policy.IsPackDisabled("original"));
        Assert.False(policy.IsPackDisabled("ORIGINAL"));
        Assert.True(policy.IsStreamerDisabled("bell"));
        Assert.True(policy.IsStreamerDisabled("BELL"));
        Assert.False(policy.IsStreamerDisabled("Bell"));
        Assert.False(policy.IsStreamerDisabled("original"));
        Assert.False(policy.IsPackDisabled("bell"));
        Assert.Contains("下架", policy.PackReason("original"));
        Assert.Contains("包下架原因", policy.PackReason("original"));
        Assert.Contains("该主播已下架", policy.StreamerReason("bell"));
        Assert.Contains("主播下架原因", policy.StreamerReason("bell"));
        Assert.Contains("该主播已下架", policy.StreamerReason("bee"));
        Assert.Equal(policy.PackReason("original"), policy.CheckTeam("original", new[] { "bell" }));
        Assert.Equal(policy.StreamerReason("bee"), policy.CheckTeam("allowed", new[] { "enka", "bee", "bell" }));
        Assert.Equal(policy.StreamerReason("bell"), policy.CheckTeam("allowed", new[] { "bell", "bee" }));
        Assert.Null(policy.CheckTeam("ORIGINAL", new[] { "enka", "Bell" }));
        Assert.Null(PackPolicy.Empty.CheckTeam("original", new[] { "bell" }));
    }

    [Fact]
    public void PublishedCollectionsCannotMutateThePolicyOrOtherInstances()
    {
        const string json = "{\"disabledPacks\":[\"original\"],\"disabledStreamers\":[\"bell\"],\"reason\":{\"bell\":\"原因\"}}";
        var first = PackPolicy.FromJson(json);
        var second = PackPolicy.FromJson(json);
        foreach (var ids in new[] { first.DisabledPacks, first.DisabledStreamers })
        {
            if (ids is IList<string> list)
            {
                Assert.Throws<NotSupportedException>(() => list[0] = "other");
                Assert.Throws<NotSupportedException>(() => list.Add("other"));
            }
        }
        if (first.Reasons is IDictionary<string, string> reasons)
        {
            Assert.Throws<NotSupportedException>(() => reasons["bell"] = "被改写");
            Assert.Throws<NotSupportedException>(() => reasons.Clear());
        }
        Assert.Equal(new[] { "original" }, first.DisabledPacks);
        Assert.Equal(new[] { "bell" }, first.DisabledStreamers);
        Assert.Equal("原因", first.Reasons["bell"]);
        Assert.Equal(first.DisabledPacks, second.DisabledPacks);
        Assert.Equal(first.DisabledStreamers, second.DisabledStreamers);
        Assert.Equal("原因", second.Reasons["bell"]);
        Assert.Empty(PackPolicy.Empty.DisabledPacks);
        Assert.Empty(PackPolicy.Empty.DisabledStreamers);
        Assert.Empty(PackPolicy.Empty.Reasons);
    }

    [Fact]
    public void MissingFileIsEmptyButMalformedExistingFileFailsAndReloadDoesNotMutateOldPolicy()
    {
        using var files = new TestDirectory();
        string path = files.PathFor("policy.json");
        Assert.Empty(PackPolicy.Load(path).DisabledPacks);
        files.Write("policy.json", "{\"disabledStreamers\":[\"bell\"]}");
        var old = PackPolicy.Load(path);
        files.Write("policy.json", "{\"disabledStreamers\":[\"bee\"]}");
        var current = PackPolicy.Load(path);
        Assert.True(old.IsStreamerDisabled("bell"));
        Assert.False(old.IsStreamerDisabled("bee"));
        Assert.True(current.IsStreamerDisabled("bee"));
        Assert.False(current.IsStreamerDisabled("bell"));
        files.Write("policy.json", "{\"disabledStreamers\":[\"bell\"]");
        Assert.Throws<FormatException>(() => PackPolicy.Load(path));
        Assert.True(old.IsStreamerDisabled("bell"));
        Assert.True(current.IsStreamerDisabled("bee"));
    }

    [Fact]
    public void RegressionPolicyFixtureMatchesTheDefaultLoader()
    {
        var parsed = PackPolicy.FromJson(Resource("PackPolicy.json"));
        var loaded = PackPolicy.LoadDefault();
        Assert.Equal(parsed.DisabledPacks, loaded.DisabledPacks);
        Assert.Equal(parsed.DisabledStreamers, loaded.DisabledStreamers);
        Assert.Equal(parsed.Reasons, loaded.Reasons);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("{\"disabledPacks\":[\"original\"]")]
    [InlineData("{\"disabledPacks\":[\"original\"]} trailing")]
    [InlineData("{}{}")]
    [InlineData("{\"disabledPacks\":[\"original\"],}")]
    [InlineData("{\"disabledPacks\":[\"original\",]}")]
    [InlineData("{\"disabledPacks\":[\"original\"],\"disabledPacks\":[]}")]
    [InlineData("{\"reason\":{\"bell\":\"first\",\"bell\":\"second\"}}")]
    [InlineData("{\"disablePacks\":[\"original\"]}")]
    [InlineData("{\"disabledPacks\":null}")]
    [InlineData("{\"disabledPacks\":\"original\"}")]
    [InlineData("{\"disabledPacks\":[1]}")]
    [InlineData("{\"disabledPacks\":[\"\"]}")]
    [InlineData("{\"disabledPacks\":[\"  \n\"]}")]
    [InlineData("{\"disabledPacks\":[\"original\",\"original\"]}")]
    [InlineData("{\"disabledStreamers\":null}")]
    [InlineData("{\"disabledStreamers\":{}}")]
    [InlineData("{\"disabledStreamers\":[null]}")]
    [InlineData("{\"disabledStreamers\":[\"\"]}")]
    [InlineData("{\"disabledStreamers\":[\" \" ]}")]
    [InlineData("{\"disabledStreamers\":[\"bell\",\"bell\"]}")]
    [InlineData("{\"reason\":null}")]
    [InlineData("{\"reason\":[]}")]
    [InlineData("{\"reason\":{\"bell\":null}}")]
    [InlineData("{\"reason\":{\"bell\":1}}")]
    public void InvalidPolicyCannotSilentlyBecomeEmptyOrPartiallyApplied(string json)
    {
        var error = Assert.Throws<FormatException>(() => PackPolicy.FromJson(json));
        Assert.Contains(error.Message, c => c >= '\u4e00' && c <= '\u9fff');
    }

    [Fact]
    public void DirectoryHidesThePackByIdRatherThanItsFilenameAndKeepsOtherErrorsVisible()
    {
        using var files = new TestDirectory();
        string pack = Original();
        files.Write("renamed.json", pack);
        files.Write("original.json", WithPackId(pack, "allowed"));
        files.Write("upper.json", WithPackId(pack, "ORIGINAL"));
        files.Write("broken.json", "{");
        var policy = Policy(new[] { "original" }, reasons: new Dictionary<string, string> { { "original", "请求下架" } });

        var hidden = PackLoader.LoadJson(Rules(), pack, "renamed.json", policy);
        Assert.True(hidden.IsDisabled);
        Assert.Null(hidden.Db);
        Assert.Contains("下架", hidden.Error);
        Assert.Contains("请求下架", hidden.Error);
        Assert.Throws<PackDisabledException>(() => ContentDb.FromJson(Rules(), pack, policy));
        var results = PackLoader.LoadDirectory(Rules(), files.PathName, policy);
        Assert.DoesNotContain(results, result => result.FileName == "renamed.json" || result.IsDisabled);
        Assert.Equal(new[] { "allowed", "ORIGINAL" }, results.Where(result => result.Db != null).Select(result => result.Db.PackId));
        var broken = Assert.Single(results, result => result.FileName == "broken.json");
        Assert.False(broken.IsDisabled);
        Assert.Null(broken.Db);
        Assert.False(string.IsNullOrWhiteSpace(broken.Error));
    }

    [Theory]
    [InlineData(6, false)]
    [InlineData(7, true)]
    public void ExactlySixRemainingIsPlayableButFiveRemainingHidesTheWholePack(int removed, bool hidden)
    {
        using var files = new TestDirectory();
        string rules = Rules(), pack = Original();
        var raw = ContentDb.FromJson(rules, pack);
        var disabled = raw.Streamers.Take(removed).Select(streamer => streamer.Id).ToArray();
        var policy = Policy(streamers: disabled);
        files.Write("original.json", pack);
        var result = PackLoader.LoadJson(rules, pack, "original.json", policy);
        var list = PackLoader.LoadDirectory(rules, files.PathName, policy);
        Assert.Equal(hidden, result.IsDisabled);
        if (hidden)
        {
            Assert.Null(result.Db);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
            Assert.Empty(list);
            Assert.Throws<PackDisabledException>(() => ContentDb.FromJson(rules, pack, policy));
        }
        else
        {
            Assert.Null(result.Error);
            Assert.Equal(6, result.Db.Streamers.Count);
            Assert.Equal(raw.Streamers.Skip(removed).Select(streamer => streamer.Id), result.Db.Streamers.Select(streamer => streamer.Id));
            Assert.Single(list);
            Assert.Empty(TeamCheck.Validate(result.Db, null, result.Db.Streamers.Select(streamer => streamer.Id).ToList()));
        }
    }

    [Fact]
    public void RemovalPrunesSeasonReferencesWithoutChangingTheRawPackOrUnrelatedRules()
    {
        string rules = Rules();
        var data = (Dictionary<string, object>)MiniJson.Parse(Original());
        data["seasons"] = new
        {
            policy_season = new
            {
                name = "下架测试赛季",
                banned = new[] { "bell", "kei" },
                limited = new[]
                {
                    new { max = 1, ids = new[] { "bell", "bee" } },
                    new { max = 0, ids = new[] { "bell" } },
                    new { max = 2, ids = new[] { "enka", "lumi" } }
                }
            }
        };
        string json = JsonSerializer.Serialize(data);
        var raw = ContentDb.FromJson(rules, json);
        string rawHash = raw.ContentHash();
        var filtered = ContentDb.FromJson(rules, json, Policy(streamers: new[] { "bell" }));
        Assert.Equal(11, filtered.Streamers.Count);
        Assert.DoesNotContain(filtered.Streamers, streamer => streamer.Id == "bell");
        Assert.True(filtered.Policy.IsStreamerDisabled("bell"));
        Assert.Equal(new[] { "kei" }, filtered.Seasons["policy_season"].Banned);
        var pools = filtered.Seasons["policy_season"].Limited;
        Assert.All(pools, pool => Assert.DoesNotContain("bell", pool.Ids));
        Assert.Contains(pools, pool => pool.Max == 1 && pool.Ids.SequenceEqual(new[] { "bee" }));
        Assert.Contains(pools, pool => pool.Max == 2 && pool.Ids.SequenceEqual(new[] { "enka", "lumi" }));
        filtered.Validate();
        Assert.Equal(raw.Skills.Keys, filtered.Skills.Keys);
        Assert.Equal(rawHash, raw.ContentHash());
        Assert.Equal(12, raw.Streamers.Count);
        Assert.Contains("bell", raw.Seasons["policy_season"].Banned);
        Assert.Contains("bell", raw.Seasons["policy_season"].Limited[0].Ids);
        var parsedAgain = ContentDb.FromJson(rules, json);
        Assert.Equal(rawHash, parsedAgain.ContentHash());
        Assert.Empty(parsedAgain.Policy.DisabledStreamers);
    }

    [Fact]
    public void TeamGatePresetAndTeamCodeExplainWhyAPreviouslyValidMemberCannotBeUsed()
    {
        string rules = Rules(), pack = Original();
        var raw = ContentDb.FromJson(rules, pack);
        var preset = new TeamPreset
        {
            Name = "已保存的队伍", PackId = raw.PackId,
            Ids = new List<string> { "bell", "enka", "shiori", "lumi", "hane", "momo" }
        };
        string code = TeamCode.Encode(preset);
        var saved = new TeamPresetList { Items = new List<TeamPreset> { preset } }.ToJson();
        Assert.Empty(preset.Check(raw, null));
        var filtered = ContentDb.FromJson(rules, pack, Policy(streamers: new[] { "bell" },
            reasons: new Dictionary<string, string> { { "bell", "请求下架" } }));

        Assert.Contains("该主播已下架", Assert.Throws<KeyError>(() => filtered.Streamer("bell")).Message);
        Assert.Contains(TeamCheck.Validate(filtered, null, preset.Ids), error => error.Contains("该主播已下架"));
        Assert.Contains(preset.Check(filtered, null), error => error.Contains("该主播已下架"));
        Assert.Contains(TeamPresetList.FromJson(saved).Items[0].Check(filtered, null), error => error.Contains("该主播已下架"));
        var codeError = Assert.Throws<FormatException>(() => TeamCode.Decode(code, filtered));
        Assert.Contains("该主播已下架", codeError.Message);
        Assert.Contains("请求下架", codeError.Message);
        Assert.Equal(preset.Ids, TeamCode.Decode(code).Ids);
        Assert.Equal(code, TeamCode.Encode(preset));
        Assert.Empty(preset.Check(raw, null));

        var allowed = new TeamPreset { Name = "仍然可用", PackId = filtered.PackId, Ids = filtered.Streamers.Take(6).Select(s => s.Id).ToList() };
        Assert.Contains("该主播已下架", Assert.Throws<InvalidOperationException>(() =>
            TeamBuilder.Suggest(filtered, null, new[] { "bell" }, 101)).Message);
        Assert.Contains("该主播已下架", Assert.Throws<KeyError>(() => new Battle(filtered,
            new IList<string>[] { preset.Ids, allowed.Ids }, new[] { "甲", "乙" }, 101)).Message);
        Assert.Empty(allowed.Check(filtered, null));
        Assert.Equal(allowed.Ids, TeamCode.Decode(TeamCode.Encode(allowed), filtered).Ids);
    }

    [Fact]
    public void UnrelatedPolicyEntriesAndReasonTextDoNotChangePlayableContentHash()
    {
        var ordinary = ContentDb.FromJson(Rules(), Original());
        var unrelated = Policy(new[] { "some_other_pack" }, new[] { "some_other_streamer" });
        var filtered = ContentDb.FromJson(Rules(), Original(), unrelated);
        Assert.Equal(ordinary.ContentHash(), filtered.ContentHash());
        var first = ContentDb.FromJson(Rules(), Original(), Policy(streamers: new[] { "bell" },
            reasons: new Dictionary<string, string> { { "bell", "第一份原因" } }));
        var second = ContentDb.FromJson(Rules(), Original(), Policy(streamers: new[] { "bell" },
            reasons: new Dictionary<string, string> { { "bell", "补充后的原因" } }));
        Assert.Equal(first.ContentHash(), second.ContentHash());
        Assert.NotEqual(first.Policy.StreamerReason("bell"), second.Policy.StreamerReason("bell"));
        Assert.Equal(ordinary.ContentHash(), ContentDb.FromJson(Rules(), Original()).ContentHash());
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string root;
        readonly string id = Guid.NewGuid().ToString("N");
        public string PathName { get; }

        public TestDirectory([CallerFilePath] string source = "")
        {
            string tests = Path.GetDirectoryName(source);
            if (!string.Equals(Path.GetFileName(tests), "Engine.Tests", StringComparison.Ordinal))
                throw new InvalidOperationException("测试临时文件必须位于 Engine.Tests/obj");
            root = Path.GetFullPath(Path.Combine(tests, "obj", nameof(T101_PackPolicyTests)));
            PathName = Path.GetFullPath(Path.Combine(root, id));
            VerifyOwnDirectory();
            Directory.CreateDirectory(PathName);
        }

        public string PathFor(string name)
        {
            string path = Path.GetFullPath(Path.Combine(PathName, name));
            if (!string.Equals(Path.GetDirectoryName(path), PathName, StringComparison.Ordinal))
                throw new InvalidOperationException("测试文件必须位于当前夹具目录");
            return path;
        }

        public void Write(string name, string text) { File.WriteAllText(PathFor(name), text); }

        void VerifyOwnDirectory()
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(PathName)), root, StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(PathName), id, StringComparison.Ordinal))
                throw new InvalidOperationException("只能清理当前测试自己的目录");
        }

        public void Dispose()
        {
            VerifyOwnDirectory();
            if (Directory.Exists(PathName)) Directory.Delete(PathName, true);
        }
    }
}
