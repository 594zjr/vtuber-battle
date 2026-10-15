using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T27_JsonValidationTests
{
    [Fact]
    public void AcceptsStandardWhitespaceEscapesLiteralsAndNumberForms()
    {
        string json = " \t\r\n" + @"{""text"":""\""\\\/\b\f\n\r\t\u4e2d\uD83D\uDE00"",""values"":[0,-0,12,-12.5,0.125,1e2,-2E-1,3E+2,true,false,null]}" + " \t\r\n";
        var parsed = Assert.IsType<Dictionary<string, object>>(MiniJson.Parse(json));

        Assert.Equal("\"\\/\b\f\n\r\t中😀", parsed["text"]);
        var values = Assert.IsType<List<object>>(parsed["values"]);
        Assert.Equal(new[] { 0.0, 0.0, 12.0, -12.5, 0.125, 100.0, -0.2, 300.0 }, values.Take(8).Cast<double>());
        Assert.Equal(true, values[8]);
        Assert.Equal(false, values[9]);
        Assert.Null(values[10]);
    }

    [Fact]
    public void RejectsNonJsonEscapesControlsWhitespaceAndNumbers()
    {
        string[] invalid =
        {
            @"""\q""", @"""\x41""", @"""\u12G4""", "\"line\nfeed\"", "\"tab\tvalue\"", "\"nul\0value\"",
            "\v0", "0\f", "\u00a00", "0\u3000", "+1", "01", "-01", ".5", "1.", "1.e2", "1e", "1e+", "1e-",
            "--1", "0x10", "NaN", "Infinity", "1e9999", "[1,]", "{\"a\":1,}", "true false"
        };

        foreach (string json in invalid) Assert.Throws<FormatException>(() => MiniJson.Parse(json));
    }

    [Fact]
    public void TruncatedInputsAlwaysReportFormatException()
    {
        string[] truncated =
        {
            null, "", " \t\r\n", "{", "[", "{\"key\"", "{\"key\":", "{\"key\":0", "{\"key\":0,",
            "[0", "[0,", "\"", "\"abc\\", "\"\\u", "\"\\u1", "\"\\u12", "\"\\u123", "tru", "fals", "nul", "-"
        };

        foreach (string json in truncated) Assert.Throws<FormatException>(() => MiniJson.Parse(json));
    }

    [Fact]
    public void NestingLimitAcceptsItsBoundaryAndRejectsDeeperContainers()
    {
        Assert.NotNull(MiniJson.Parse(new string('[', 64) + "0" + new string(']', 64)));
        Assert.Throws<FormatException>(() => MiniJson.Parse(new string('[', 65) + "0" + new string(']', 65)));
        string objects = string.Concat(Enumerable.Repeat("{\"child\":", 65)) + "0" + new string('}', 65);
        Assert.Throws<FormatException>(() => MiniJson.Parse(objects));
    }

    [Fact]
    public void InvalidEscapeInEarlierPackDoesNotReserveItsId()
    {
        using var directory = new TestDirectory();
        string content = Path.Combine(AppContext.BaseDirectory, "Content");
        string valid = File.ReadAllText(Path.Combine(content, "Packs", "original.json"))
            .Replace("\"id\": \"original\"", "\"id\": \"shared_json_id\"");
        string invalid = valid.Replace("\"name\": \"原创主播\"", "\"name\": \"\\q\"");
        Assert.NotEqual(valid, invalid);
        directory.Write("a_bad.json", invalid);
        directory.Write("b_good.json", valid);

        var results = PackLoader.LoadDirectory(File.ReadAllText(Path.Combine(content, "StreamDuelRules.json")), directory.PathName);

        Assert.Equal(new[] { "a_bad.json", "b_good.json" }, results.Select(result => result.FileName));
        Assert.Null(results[0].Db);
        Assert.Contains("JSON", results[0].Error);
        Assert.Null(results[1].Error);
        Assert.NotNull(results[1].Db);
        Assert.Equal("shared_json_id", results[1].Db.PackId);
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string root;
        readonly string id = Guid.NewGuid().ToString("N");
        public string PathName { get; }

        public TestDirectory()
        {
            string tests = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
            if (!string.Equals(Path.GetFileName(tests), "Engine.Tests", StringComparison.Ordinal))
                throw new InvalidOperationException("测试夹具必须位于 Engine.Tests/obj 内");
            root = Path.GetFullPath(Path.Combine(tests, "obj", nameof(T27_JsonValidationTests)));
            PathName = Path.GetFullPath(Path.Combine(root, id));
            VerifyOwnDirectory();
            Directory.CreateDirectory(PathName);
        }

        public void Write(string name, string contents)
        {
            string file = Path.GetFullPath(Path.Combine(PathName, name));
            if (!string.Equals(Path.GetDirectoryName(file), PathName, StringComparison.Ordinal))
                throw new InvalidOperationException("测试文件必须位于当前 GUID 目录内");
            File.WriteAllText(file, contents);
        }

        void VerifyOwnDirectory()
        {
            string path = Path.GetFullPath(PathName);
            if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.Ordinal) ||
                !string.Equals(Path.GetFileName(path), id, StringComparison.Ordinal))
                throw new InvalidOperationException("只能清理当前测试的 GUID 目录");
        }

        public void Dispose()
        {
            VerifyOwnDirectory();
            if (Directory.Exists(PathName)) Directory.Delete(PathName, true);
        }
    }
}
