using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using StreamDuel.Balance;
using Xunit;

public class T130_EncodingReviewTests
{
    const string Json = "{\"version\":1,\"gamesPerStreamer\":2,\"seed\":130,\"level\":\"easy\",\"aiVersion\":\"encoding-fixture\","
        + "\"packs\":[{\"packId\":\"fixture\",\"contentHash\":\"encoding-content\",\"battles\":100,\"streamers\":[{"
        + "\"id\":\"alpha\",\"name\":\"编码@测试🌟\",\"games\":100,\"wins\":50,\"draws\":0,\"winRate\":0.5,"
        + "\"low95\":0.4038315303659956,\"high95\":0.5961684696340044}]}]}";

    [Theory]
    [InlineData("invalid-utf8-string")]
    [InlineData("utf16-le-bom")]
    [InlineData("utf16-be-bom")]
    [InlineData("utf32-le-bom")]
    [InlineData("utf32-be-bom")]
    [InlineData("utf16-le-unpaired-surrogate")]
    [InlineData("utf16-be-unpaired-surrogate")]
    public void InvalidOrNonUtf8BytesAreRejectedWithoutReplacingNamesOrChangingFiles(string kind)
    {
        byte[] bytes = Bytes(kind);
        string path = Path.Combine(Scratch(), kind + ".json");
        File.WriteAllBytes(path, bytes);
        byte[] beforeHash = Hash(path);
        DateTime beforeTime = File.GetLastWriteTimeUtc(path);

        var error = Assert.Throws<FormatException>(() => BalanceSnapshot.Load(path));

        Assert.Matches("[\u4e00-\u9fff]", error.Message);
        Assert.Contains("UTF-8", error.Message);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(beforeHash, Hash(path));
        Assert.Equal(beforeTime, File.GetLastWriteTimeUtc(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void ValidUtf8WithAndWithoutBomLoadIdenticallyAndPreserveOriginalBytesAndTimes()
    {
        string directory = Scratch();
        string plain = Path.Combine(directory, "plain.json");
        string bom = Path.Combine(directory, "bom.json");
        byte[] plainBytes = new UTF8Encoding(false, true).GetBytes(Json);
        byte[] bomBytes = new UTF8Encoding(true, true).GetPreamble().Concat(plainBytes).ToArray();
        File.WriteAllBytes(plain, plainBytes);
        File.WriteAllBytes(bom, bomBytes);
        byte[] plainHash = Hash(plain), bomHash = Hash(bom);
        DateTime plainTime = File.GetLastWriteTimeUtc(plain), bomTime = File.GetLastWriteTimeUtc(bom);

        var first = BalanceSnapshot.Load(plain);
        var second = BalanceSnapshot.Load(bom);

        Assert.Equal("编码@测试🌟", Assert.Single(Assert.Single(first.Packs).Streamers).Name);
        Assert.Equal(first.ToJson(), second.ToJson());
        var comparison = BalanceCompare.Compare(first, second);
        Assert.False(comparison.ConditionsDiffer);
        Assert.False(comparison.HasSignificantChanges);
        Assert.False(comparison.HasOutOfRange);
        Assert.Equal(0.0, Assert.Single(comparison.Rows).Delta.Value);
        Assert.Equal(plainBytes, File.ReadAllBytes(plain));
        Assert.Equal(bomBytes, File.ReadAllBytes(bom));
        Assert.Equal(plainHash, Hash(plain));
        Assert.Equal(bomHash, Hash(bom));
        Assert.Equal(plainTime, File.GetLastWriteTimeUtc(plain));
        Assert.Equal(bomTime, File.GetLastWriteTimeUtc(bom));
        Assert.Equal(new[] { bom, plain }, Directory.GetFiles(directory).OrderBy(path => path, StringComparer.Ordinal));
        Assert.Empty(Directory.GetDirectories(directory));
    }

    static byte[] Bytes(string kind)
    {
        if (kind == "invalid-utf8-string")
        {
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(Json);
            int marker = Array.IndexOf(bytes, (byte)'@');
            Assert.True(marker >= 0);
            bytes[marker] = 0xff;
            return bytes;
        }
        bool bigEndian = kind.Contains("-be-");
        Encoding encoding = kind.StartsWith("utf32-", StringComparison.Ordinal)
            ? new UTF32Encoding(bigEndian, true, true)
            : new UnicodeEncoding(bigEndian, true, true);
        byte[] result = encoding.GetPreamble().Concat(encoding.GetBytes(Json)).ToArray();
        if (kind.EndsWith("unpaired-surrogate", StringComparison.Ordinal))
        {
            // 在已编码的 JSON 字符串内写入孤立高代理项，不能让测试夹具的编码器先替换坏字节。
            int marker = encoding.GetPreamble().Length + Json.IndexOf('@') * 2;
            result[marker] = bigEndian ? (byte)0xd8 : (byte)0;
            result[marker + 1] = bigEndian ? (byte)0 : (byte)0xd8;
        }
        return result;
    }

    static byte[] Hash(string path)
    {
        using var hash = SHA256.Create();
        return hash.ComputeHash(File.ReadAllBytes(path));
    }

    static string Scratch([CallerFilePath] string source = "")
    {
        string tests = Path.GetDirectoryName(Path.GetFullPath(source));
        string root = Path.GetFullPath(Path.Combine(tests, "obj", "t130-encoding-review"));
        string path = Path.GetFullPath(Path.Combine(root, Guid.NewGuid().ToString("N")));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("编码验收产物不能超出本测试工程的 obj 目录。");
        Directory.CreateDirectory(path);
        return path;
    }
}
