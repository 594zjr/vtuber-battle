using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using Xunit;

public class T27_PackDirectoryEdgeTests
{
    static string Res(string relative)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", relative));
    }

    static JsonObject Pack(string id)
    {
        var pack = JsonNode.Parse(Res(Path.Combine("Packs", "original.json"))).AsObject();
        pack["id"] = id;
        return pack;
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string root;
        public string PathName { get; }

        public TestDirectory()
        {
            // 夹具固定留在仓库内，避免运行测试时写入用户的系统临时目录。
            root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".test-temp", "T27_PackDirectoryEdgeTests"));
            PathName = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(PathName);
        }

        public string Write(string relative, string contents)
        {
            var path = Path.GetFullPath(Path.Combine(PathName, relative));
            if (!path.StartsWith(PathName + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("测试文件必须位于夹具目录内");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            var path = Path.GetFullPath(PathName);
            if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.Ordinal))
                throw new InvalidOperationException("只能清理当前测试夹具目录");
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    static void AssertFailure(PackLoadResult result)
    {
        Assert.Null(result.Db);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Matches(@"[\u4e00-\u9fff]", result.Error);
    }

    [Fact]
    public void LoadsOnlyTopLevelJsonFilesInOrdinalFileNameOrder()
    {
        using var dir = new TestDirectory();
        dir.Write("z_last.json", Pack("last").ToJsonString());
        dir.Write("a_second.json", Pack("second").ToJsonString());
        dir.Write("A_first.JSON", Pack("first").ToJsonString());
        dir.Write("nested/hidden.json", Pack("hidden").ToJsonString());
        dir.Write("readme.txt", "不是内容包");
        dir.Write("backup.json.bak", "{ 坏掉的 JSON");

        var results = PackLoader.LoadDirectory(Res("StreamDuelRules.json"), dir.PathName);

        Assert.Equal(new[] { "A_first.JSON", "a_second.json", "z_last.json" }, results.Select(r => r.FileName));
        Assert.Equal(new[] { "first", "second", "last" }, results.Select(r => r.Db.PackId));
        Assert.All(results, result => Assert.Null(result.Error));
    }

    [Fact]
    public void FailedValidationDoesNotReservePackIdOrStopLaterFiles()
    {
        using var dir = new TestDirectory();
        var invalid = Pack("shared_id");
        invalid["streamers"][0]["skills"][0] = "missing_skill";
        dir.Write("a_invalid.json", invalid.ToJsonString());
        dir.Write("b_malformed.json", "{ 坏掉的 JSON");
        dir.Write("c_valid.json", Pack("shared_id").ToJsonString());

        var results = PackLoader.LoadDirectory(Res("StreamDuelRules.json"), dir.PathName);

        Assert.Equal(3, results.Count);
        AssertFailure(results[0]);
        Assert.Contains("missing_skill", results[0].Error);
        AssertFailure(results[1]);
        Assert.Null(results[2].Error);
        Assert.Equal("shared_id", results[2].Db.PackId);
    }

    [Fact]
    public void DuplicateIdKeepsFirstSuccessfulFileWithoutReplacingIt()
    {
        using var dir = new TestDirectory();
        var later = Pack("same_id");
        later["name"] = "后加载包";
        dir.Write("z_duplicate.json", later.ToJsonString());
        var first = Pack("same_id");
        first["name"] = "先加载包";
        dir.Write("a_first.json", first.ToJsonString());

        var results = PackLoader.LoadDirectory(Res("StreamDuelRules.json"), dir.PathName);

        Assert.Equal(2, results.Count);
        Assert.Null(results[0].Error);
        Assert.Equal("先加载包", results[0].Db.PackName);
        AssertFailure(results[1]);
        Assert.Contains("same_id", results[1].Error);
    }

    [Fact]
    public void SuccessfulIdsAreCaseSensitiveAndScopedToOneLoadCall()
    {
        using var first = new TestDirectory();
        using var second = new TestDirectory();
        first.Write("a_lower.json", Pack("custom").ToJsonString());
        first.Write("b_upper.json", Pack("CUSTOM").ToJsonString());
        second.Write("another.json", Pack("custom").ToJsonString());
        var rules = Res("StreamDuelRules.json");

        var initial = PackLoader.LoadDirectory(rules, first.PathName);
        var repeated = PackLoader.LoadDirectory(rules, first.PathName);
        var independent = PackLoader.LoadDirectory(rules, second.PathName);

        Assert.Equal(2, initial.Count);
        Assert.Equal(2, repeated.Count);
        Assert.All(initial.Concat(repeated).Concat(independent), result =>
        {
            Assert.Null(result.Error);
            Assert.NotNull(result.Db);
        });
        Assert.Equal("custom", Assert.Single(independent).Db.PackId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n ")]
    public void MissingOrBlankPackIdIsReportedAsAnError(string id)
    {
        using var dir = new TestDirectory();
        var pack = Pack(id);
        if (id == null) pack.Remove("id");
        dir.Write("invalid_id.json", pack.ToJsonString());
        dir.Write("valid.json", Pack("valid").ToJsonString());

        var results = PackLoader.LoadDirectory(Res("StreamDuelRules.json"), dir.PathName);

        Assert.Equal(2, results.Count);
        AssertFailure(results[0]);
        Assert.Contains("id", results[0].Error);
        Assert.Null(results[1].Error);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void InvalidPackRootProducesFileErrorInsteadOfThrowing(string json)
    {
        using var dir = new TestDirectory();
        dir.Write("invalid.json", json);

        var result = Assert.Single(PackLoader.LoadDirectory(Res("StreamDuelRules.json"), dir.PathName));

        Assert.Equal("invalid.json", result.FileName);
        AssertFailure(result);
    }

    [Fact]
    public void FileReadFailureDoesNotStopOtherPacks()
    {
        using var dir = new TestDirectory();
        var lockedPath = dir.Write("a_locked.json", Pack("locked").ToJsonString());
        dir.Write("b_good.json", Pack("good").ToJsonString());
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var results = PackLoader.LoadDirectory(Res("StreamDuelRules.json"), dir.PathName);

        Assert.Equal(2, results.Count);
        AssertFailure(results[0]);
        Assert.Equal("a_locked.json", results[0].FileName);
        Assert.Null(results[1].Error);
        Assert.Equal("good", results[1].Db.PackId);
    }

    [Fact]
    public void InvalidRulesReturnOneErrorPerFile()
    {
        using var dir = new TestDirectory();
        dir.Write("a.json", Pack("first").ToJsonString());
        dir.Write("b.json", Pack("second").ToJsonString());

        var results = PackLoader.LoadDirectory("{ 坏掉的规则", dir.PathName);

        Assert.Equal(new[] { "a.json", "b.json" }, results.Select(r => r.FileName));
        Assert.All(results, AssertFailure);
    }

    [Fact]
    public void LoadedDatabasesKeepRenamesAndLaterChangesIndependent()
    {
        using var dir = new TestDirectory();
        var first = Pack("first");
        first["skillNames"] = new JsonObject { ["warm_up"] = "甲包开嗓" };
        var second = Pack("second");
        second["skillNames"] = new JsonObject { ["warm_up"] = "乙包开嗓" };
        dir.Write("a_first.json", first.ToJsonString());
        dir.Write("b_second.json", second.ToJsonString());

        var results = PackLoader.LoadDirectory(Res("StreamDuelRules.json"), dir.PathName);

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Null(result.Error));
        Assert.Equal("甲包开嗓", results[0].Db.Skills["warm_up"].Name);
        Assert.Equal("乙包开嗓", results[1].Db.Skills["warm_up"].Name);
        results[0].Db.Skills["warm_up"].Name = "甲包修改后";
        results[0].Db.Personas["punctual"].Params["prio"] = 99;
        Assert.Equal("乙包开嗓", results[1].Db.Skills["warm_up"].Name);
        Assert.Equal(1, results[1].Db.Personas["punctual"].P("prio"));
    }
}
