using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T27_StatsValidationTests
{
    static string Res(string relative)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", relative));
    }

    static JsonObject Pack()
    {
        return JsonNode.Parse(Res(Path.Combine("Packs", "original.json"))).AsObject();
    }

    static JsonObject Bell(JsonObject pack)
    {
        return pack["streamers"].AsArray().OfType<JsonObject>().Single(s => s["id"].GetValue<string>() == "bell");
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string root;
        public string PathName { get; }

        public TestDirectory()
        {
            string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
            if (Path.GetFileName(project) != "Engine.Tests" || !File.Exists(Path.Combine(project, "StreamDuel.Tests.csproj")))
                throw new InvalidOperationException("测试产物必须位于本仓库 Engine.Tests/obj 内");
            root = Path.GetFullPath(Path.Combine(project, "obj", "T27_StatsValidationTests"));
            PathName = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(PathName);
        }

        public void Dispose()
        {
            string path = Path.GetFullPath(PathName);
            if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.Ordinal))
                throw new InvalidOperationException("只能清理当前测试夹具目录");
            // 只清理由本夹具创建的两个文件，不递归删除其他产物。
            File.Delete(Path.Combine(path, "a_invalid.json"));
            File.Delete(Path.Combine(path, "b_valid.json"));
            Directory.Delete(path);
        }
    }

    static void AssertRejectedBeforeValidPack(JsonObject invalid, string stat)
    {
        string rules = Res("StreamDuelRules.json");
        string invalidJson = invalid.ToJsonString();
        var error = Assert.Throws<KeyError>(() => ContentDb.FromJson(rules, invalidJson));
        Assert.Contains("bell", error.Message);
        Assert.Contains(stat, error.Message);

        using var directory = new TestDirectory();
        File.WriteAllText(Path.Combine(directory.PathName, "a_invalid.json"), invalidJson);
        File.WriteAllText(Path.Combine(directory.PathName, "b_valid.json"), Pack().ToJsonString());
        var results = PackLoader.LoadDirectory(rules, directory.PathName);

        Assert.Equal(new[] { "a_invalid.json", "b_valid.json" }, results.Select(r => r.FileName));
        Assert.Null(results[0].Db);
        Assert.Contains("bell", results[0].Error);
        Assert.Contains(stat, results[0].Error);
        Assert.Null(results[1].Error);
        var db = results[1].Db;
        Assert.NotNull(db);
        Assert.Equal("original", db.PackId);
        var ids = db.Streamers.Select(s => s.Id).ToArray();
        var battle = new Battle(db, new IList<string>[] { ids.Take(6).ToList(), ids.Skip(6).Take(6).ToList() },
            new[] { "A", "B" }, 27);
        Assert.Equal(Phase.Choose, battle.Phase);
        Assert.Equal(db.Streamer(ids[0]).Stats["hp"], battle.Active(0).MaxHp);
    }

    [Theory]
    [InlineData("hp")]
    [InlineData("atk")]
    [InlineData("def")]
    [InlineData("spa")]
    [InlineData("sdef")]
    [InlineData("spd")]
    public void EveryRequiredStatIsRejectedWhenMissing(string stat)
    {
        var pack = Pack();
        Assert.True(Bell(pack)["stats"].AsObject().Remove(stat));
        AssertRejectedBeforeValidPack(pack, stat);

        var db = BattleTests.LoadPack("original");
        db.Streamer("bell").Stats.Remove(stat);
        var error = Assert.Throws<KeyError>(() => db.Validate());
        Assert.Contains("bell", error.Message);
        Assert.Contains(stat, error.Message);
    }

    [Theory]
    [InlineData("hp", "400.5")]
    [InlineData("atk", "2147483648")]
    [InlineData("def", "-2147483649")]
    [InlineData("spa", "\"300\"")]
    [InlineData("sdef", "null")]
    [InlineData("spd", "true")]
    [InlineData("hp", "{}")]
    [InlineData("atk", "[]")]
    public void InvalidStatValuesAreRejectedWithoutReservingThePackId(string stat, string jsonValue)
    {
        var pack = Pack();
        Bell(pack)["stats"][stat] = JsonNode.Parse(jsonValue);
        AssertRejectedBeforeValidPack(pack, stat);
    }

    [Fact]
    public void MissingStatsObjectIsRejectedBeforeThePackCanBeSelected()
    {
        var pack = Pack();
        Assert.True(Bell(pack).Remove("stats"));
        AssertRejectedBeforeValidPack(pack, "hp");
    }

    [Fact]
    public void IntegralValuesKeepTheirValueWithoutAddingBalanceLimits()
    {
        var pack = Pack();
        Bell(pack)["stats"] = JsonNode.Parse("{\"hp\":0,\"atk\":-1,\"def\":-2147483648,\"spa\":2147483647,\"sdef\":100.0,\"spd\":3e2}");

        var db = ContentDb.FromJson(Res("StreamDuelRules.json"), pack.ToJsonString());

        var stats = db.Streamer("bell").Stats;
        Assert.Equal(0, stats["hp"]);
        Assert.Equal(-1, stats["atk"]);
        Assert.Equal(int.MinValue, stats["def"]);
        Assert.Equal(int.MaxValue, stats["spa"]);
        Assert.Equal(100, stats["sdef"]);
        Assert.Equal(300, stats["spd"]);
    }
}
