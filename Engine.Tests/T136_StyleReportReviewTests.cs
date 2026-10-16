using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamDuel.Ladder;
using Xunit;

public class T136_StyleReportReviewTests
{
    static readonly string[] FileNames = { "comparison.json", "fixtures.json", "games.csv" };

    [Theory]
    [InlineData("normal")]
    [InlineData("hard")]
    public void SmallReportsUseUtf8LfAndRepeatExactlyWithTheActualFixtureHash(string level)
    {
        string root = Scratch();
        string first = Path.Combine(root, "first");
        string second = Path.Combine(root, "second");

        foreach (string directory in new[] { first, second })
        {
            Assert.False(Directory.Exists(directory));
            int exit = StyleComparison.Run(new[]
            {
                "--compare-styles", "--pack", "original_wave3", "--games", "2",
                "--seed", "20261016", "--level", level, "--out", directory
            }, BattleTests.LoadPack);

            Assert.Equal(0, exit);
            Assert.Equal(FileNames, Directory.GetFiles(directory).Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal));
            Assert.Empty(Directory.GetDirectories(directory));
            foreach (string name in FileNames)
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(directory, name));
                Assert.NotEmpty(bytes);
                Assert.False(bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf);
                Assert.DoesNotContain((byte)'\r', bytes);
                Assert.Equal((byte)'\n', bytes[bytes.Length - 1]);
                string text = new UTF8Encoding(false, true).GetString(bytes);
                Assert.Contains("\n", text);
                Assert.DoesNotContain("\r", text);
                Assert.EndsWith("\n", text);
            }

            using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "comparison.json")));
            using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "fixtures.json")));
            JsonElement result = report.RootElement;
            Assert.Equal("T136-style-comparison-v1", result.GetProperty("Protocol").GetString());
            Assert.Equal("original_wave3", result.GetProperty("PackId").GetString());
            Assert.Equal(level, result.GetProperty("Level").GetString());
            Assert.Equal(2, result.GetProperty("GamesPerArm").GetInt32());
            Assert.Equal(1, result.GetProperty("FixtureCount").GetInt32());
            Assert.Equal(4, result.GetProperty("GameCount").GetInt32());
            Assert.Equal(4, result.GetProperty("Games").GetArrayLength());
            Assert.Equal(2, result.GetProperty("Styled").GetProperty("Games").GetInt32());
            Assert.Equal(2, result.GetProperty("Unstyled").GetProperty("Games").GetInt32());
            Assert.False(result.GetProperty("MeetsRequiredSampleSize").GetBoolean());
            Assert.False(result.GetProperty("AllStreamersStyled").GetBoolean());
            Assert.False(result.GetProperty("AcceptancePassed").GetBoolean());
            Assert.Equal(1, fixture.RootElement.GetProperty("Fixtures").GetArrayLength());
            Assert.Equal(Hash(Path.Combine(directory, "fixtures.json")), result.GetProperty("FixtureSha256").GetString());
        }

        foreach (string name in FileNames)
        {
            string firstPath = Path.Combine(first, name), secondPath = Path.Combine(second, name);
            Assert.Equal(Hash(firstPath), Hash(secondPath));
            Assert.Equal(File.ReadAllBytes(firstPath), File.ReadAllBytes(secondPath));
        }
    }

    static string Hash(string path)
    {
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    static string Scratch([CallerFilePath] string source = "")
    {
        string tests = Path.GetDirectoryName(Path.GetFullPath(source));
        string root = Path.GetFullPath(Path.Combine(tests, "obj", "t136-style-report-review"));
        string directory = Path.GetFullPath(Path.Combine(root, Guid.NewGuid().ToString("N")));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("风格报告验收产物不能超出本测试工程的 obj 目录。");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
