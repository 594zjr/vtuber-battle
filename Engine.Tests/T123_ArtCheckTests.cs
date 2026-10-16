using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamDuel.Core;
using StreamDuel.PackCheck;
using Xunit;

public class T123_ArtCheckTests
{
    const int MaximumBytes = 8 * 1024 * 1024;
    static readonly uint[] CrcTable = MakeCrcTable();

    [Fact]
    public void MissingPortraitsAreWarningsAndExplicitlyKeepThePlaceholderFallback()
    {
        using var directory = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        var report = ArtCheck.Check(db, directory.Root);

        Assert.True(report.Passed);
        Assert.Equal(0, report.ErrorCount);
        Assert.Equal(db.Streamers.Count, report.WarningCount);
        Assert.Equal(db.PackId, report.PackId);
        Assert.Equal(new[] { db.PackId }, report.PackIds);
        Assert.Equal(Path.GetFullPath(directory.Root), report.Root);
        foreach (var streamer in db.Streamers)
        {
            var issue = Assert.Single(report.Issues, i => i.Code == "missing" && i.Id == db.PackId + "/" + streamer.Id);
            Assert.Equal("warn", issue.Level);
            Assert.Equal(db.Art.PortraitPath(streamer.Id) + ".png", issue.Path);
            Assert.Contains("将使用占位", issue.Message, StringComparison.Ordinal);
            Assert.Null(issue.Width);
            Assert.Null(issue.Height);
            Assert.Null(issue.Bytes);
        }
        Assert.Empty(Directory.GetFileSystemEntries(directory.Root));
    }

    [Fact]
    public void CompleteValidArtPassesAndRetainsTheChineseRightsReminder()
    {
        using var directory = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        WritePortraits(directory.Root, db);

        var report = ArtCheck.Check(db, directory.Root);

        Assert.True(report.Passed);
        Assert.Equal(0, report.ErrorCount);
        Assert.Equal(0, report.WarningCount);
        Assert.Contains(report.Issues, i => i.Code == "procedural" && i.Level == "info");
        Assert.Contains("授权", report.Reminder, StringComparison.Ordinal);
        Assert.Contains("官方立绘", report.Reminder, StringComparison.Ordinal);
        Assert.Contains("截图", report.Reminder, StringComparison.Ordinal);
        Assert.All(report.Issues, i => Assert.Matches("[\u4e00-\u9fff]", i.Message));
    }

    [Theory]
    [InlineData(128, 128, true)]
    [InlineData(4096, 4096, true)]
    [InlineData(128, 4096, true)]
    [InlineData(4096, 128, true)]
    [InlineData(127, 128, false)]
    [InlineData(128, 127, false)]
    [InlineData(4097, 128, false)]
    [InlineData(128, 4097, false)]
    public void PortraitDimensionsHaveInclusiveAndIndependentBounds(int width, int height, bool valid)
    {
        using var directory = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        WritePortraits(directory.Root, db);
        string relative = db.Art.PortraitPath("bell") + ".png";
        byte[] png = Png(width, height);
        Write(directory.Root, relative, png);

        var report = ArtCheck.Check(db, directory.Root);

        Assert.Equal(valid, report.Passed);
        Assert.Equal(valid ? 0 : 1, report.ErrorCount);
        Assert.Equal(0, report.WarningCount);
        Assert.DoesNotContain(report.Issues, i => i.Code == "format");
        if (!valid)
        {
            var issue = Assert.Single(report.Issues, i => i.Code == "size");
            Assert.Equal("error", issue.Level);
            Assert.Equal(relative, issue.Path);
            Assert.Equal("original/bell", issue.Id);
            Assert.Equal(width, issue.Width);
            Assert.Equal(height, issue.Height);
            Assert.Equal((long)png.Length, issue.Bytes);
        }
    }

    [Theory]
    [InlineData(MaximumBytes, true)]
    [InlineData(MaximumBytes + 1, false)]
    public void EightMebibytesIsAllowedButOneMoreByteIsAnError(int bytes, bool valid)
    {
        using var directory = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        WritePortraits(directory.Root, db);
        string relative = db.Art.PortraitPath("bell") + ".png";
        byte[] png = Png(128, 128, bytes);
        Assert.Equal(bytes, png.Length);
        Write(directory.Root, relative, png);

        var report = ArtCheck.Check(db, directory.Root);

        Assert.Equal(valid, report.Passed);
        Assert.Equal(valid ? 0 : 1, report.ErrorCount);
        Assert.Equal(0, report.WarningCount);
        Assert.DoesNotContain(report.Issues, i => i.Code == "format" || i.Code == "size");
        if (!valid)
        {
            var issue = Assert.Single(report.Issues, i => i.Code == "large");
            Assert.Equal("error", issue.Level);
            Assert.Equal(relative, issue.Path);
            Assert.Equal((long)bytes, issue.Bytes);
        }
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("gif")]
    [InlineData("text")]
    public void NonPngBytesDisguisedWithThePngExtensionAreFormatErrors(string kind)
    {
        using var directory = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        WritePortraits(directory.Root, db);
        byte[] fake = new byte[64];
        byte[] signature = kind == "jpeg" ? new byte[] { 255, 216, 255, 224 }
            : Encoding.ASCII.GetBytes(kind == "gif" ? "GIF89a" : "this is not an image");
        Array.Copy(signature, fake, signature.Length);
        string relative = db.Art.PortraitPath("bell") + ".png";
        Write(directory.Root, relative, fake);

        AssertFormatError(ArtCheck.Check(db, directory.Root), relative);
    }

    [Fact]
    public void AReferencedJpegExtensionIsReportedAsAFormatError()
    {
        using var directory = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        WritePortraits(directory.Root, db, "bell");
        string relative = db.Art.PortraitPath("bell") + ".jpg";
        Write(directory.Root, relative, Png(128, 128));

        var report = ArtCheck.Check(db, directory.Root);

        AssertFormatError(report, relative);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("truncated")]
    [InlineData("length")]
    [InlineData("type")]
    [InlineData("crc")]
    [InlineData("zero-width")]
    [InlineData("zero-height")]
    [InlineData("huge-width")]
    [InlineData("depth")]
    [InlineData("color")]
    [InlineData("compression")]
    [InlineData("filter")]
    [InlineData("interlace")]
    public void CorruptSignatureAndIhdrFieldsAreRejectedWithoutImageDecoding(string corruption)
    {
        using var directory = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        WritePortraits(directory.Root, db);
        string relative = db.Art.PortraitPath("bell") + ".png";
        Write(directory.Root, relative, DamageHeader(Png(128, 128), corruption));

        AssertFormatError(ArtCheck.Check(db, directory.Root), relative);
    }

    [Fact]
    public void SharedChinesePortraitOverridesAreResolvedOnceAndDoNotRequireOldDefaults()
    {
        using var directory = new TestDirectory();
        var db = WithPortraits("original", "自制立绘/演出季", ("bell", "双人/共用"), ("enka", "双人/共用"));
        WritePortraits(directory.Root, db);
        string shared = "自制立绘/演出季/双人/共用.png";
        Write(directory.Root, shared, Png(127, 128));

        var report = ArtCheck.Check(db, directory.Root);

        Assert.False(report.Passed);
        Assert.Equal(1, report.ErrorCount);
        Assert.Equal(0, report.WarningCount);
        var issue = Assert.Single(report.Issues, i => i.Code == "size");
        Assert.Equal(shared, issue.Path);
        Assert.Equal("original/bell,original/enka", issue.Id);
        Assert.DoesNotContain('\\', issue.Path);
        Assert.Equal(db.Streamers.Count - 1, Directory.GetFiles(directory.Root, "*.png", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void MultiPackReferencesAreCombinedBeforeCheckingForUnusedImages()
    {
        using var directory = new TestDirectory();
        var original = WithPortraits("original", "共享素材", ("bell", "共用"));
        var wave = WithPortraits("original_wave2", "共享素材", ("og2_qideng", "共用"));
        WritePortraits(directory.Root, original);
        WritePortraits(directory.Root, wave);

        var complete = ArtCheck.Check(new[] { original, wave }, directory.Root);

        Assert.True(complete.Passed);
        Assert.Equal(0, complete.ErrorCount);
        Assert.Equal(0, complete.WarningCount);
        Assert.Equal("", complete.PackId);
        Assert.Equal(new[] { "original", "original_wave2" }, complete.PackIds);

        Write(directory.Root, "共享素材/共用.png", Png(127, 128));
        var invalid = ArtCheck.Check(new[] { original, wave }, directory.Root);
        var shared = Assert.Single(invalid.Issues, i => i.Code == "size");
        Assert.Equal("original/bell,original_wave2/og2_qideng", shared.Id);
        Assert.Equal(1, invalid.ErrorCount);
        Assert.Equal(0, invalid.WarningCount);
        var reversed = ArtCheck.Check(new[] { wave, original }, directory.Root);
        Assert.Equal(JsonSerializer.Serialize(invalid), JsonSerializer.Serialize(reversed));
    }

    [Fact]
    public void UnreferencedImagesAreWarningsWithoutBlockingOtherwiseValidArt()
    {
        using var directory = new TestDirectory();
        var db = BattleTests.LoadPack("original");
        WritePortraits(directory.Root, db);
        const string extra = "额外草稿/未采用.png";
        Write(directory.Root, extra, Png(128, 128));

        var report = ArtCheck.Check(db, directory.Root);

        Assert.True(report.Passed);
        Assert.Equal(0, report.ErrorCount);
        Assert.Equal(1, report.WarningCount);
        var issue = Assert.Single(report.Issues, i => i.Code == "unused");
        Assert.Equal("warn", issue.Level);
        Assert.Equal(extra, issue.Path);
        Assert.Matches("[\u4e00-\u9fff]", issue.Message);
    }

    [Fact]
    public void ExplicitProceduralEffectsAndIconsDoNotCreateFictitiousImageRequirements()
    {
        using var directory = new TestDirectory();
        var pack = JsonNode.Parse(ReadContent(Path.Combine("Packs", "original.json")));
        pack["art"] = JsonNode.Parse("{\"skillFx\":{\"full_volume\":\"wave\",\"struggle\":\"ring\"},\"statusIcons\":{\"stall\":\"star\",\"reconnect\":\"heart\"}}");
        var db = ContentDb.FromJson(ReadContent("StreamDuelRules.json"), pack.ToJsonString());
        WritePortraits(directory.Root, db);

        var report = ArtCheck.Check(db, directory.Root);

        Assert.True(report.Passed);
        Assert.Equal(0, report.WarningCount);
        Assert.Equal(0, report.ErrorCount);
        Assert.Contains(report.Issues, i => i.Code == "procedural" && i.Level == "info");
        Assert.Equal(db.Streamers.Count, Directory.GetFiles(directory.Root, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void RepeatedChecksDoNotChangeFilesDirectoriesContentOrAnEarlierReport()
    {
        using var directory = new TestDirectory();
        var db = WithPortraits("original", "作品/中文路径", ("bell", "小铃新装"));
        WritePortraits(directory.Root, db, "enka");
        Write(directory.Root, "作品/中文路径/小铃新装.png", Encoding.UTF8.GetBytes("损坏的自制图片"));
        Write(directory.Root, "草稿/备用.png", Png(128, 128));
        Write(directory.Root, "说明.txt", Encoding.UTF8.GetBytes("仅供本地检查，不应被修改。"));
        string contentHash = db.ContentHash();
        string[] paths = db.Streamers.Select(s => db.Art.PortraitPath(s.Id)).ToArray();
        var before = TreeSnapshot.Capture(directory.Root);

        var first = ArtCheck.Check(db, directory.Root);
        string firstJson = JsonSerializer.Serialize(first);
        var second = ArtCheck.Check(db, directory.Root);

        Assert.Equal(firstJson, JsonSerializer.Serialize(second));
        before.AssertUnchanged(directory.Root);
        Assert.Equal(contentHash, db.ContentHash());
        Assert.Equal(paths, db.Streamers.Select(s => db.Art.PortraitPath(s.Id)).ToArray());
        Write(directory.Root, "后来新增.png", Png(128, 128));
        ArtCheck.Check(db, directory.Root);
        Assert.Equal(firstJson, JsonSerializer.Serialize(first));
    }

    static void AssertFormatError(ArtCheckReport report, string relative)
    {
        Assert.False(report.Passed);
        Assert.True(report.ErrorCount > 0);
        var issue = Assert.Single(report.Issues, i => i.Code == "format" && i.Path == relative);
        Assert.Equal("error", issue.Level);
        Assert.Equal("original/bell", issue.Id);
        Assert.Matches("[\u4e00-\u9fff]", issue.Message);
    }

    static string ReadContent(string path) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", path));

    static ContentDb WithPortraits(string packId, string portraitDirectory, params (string Id, string Portrait)[] overrides)
    {
        var streamers = new JsonObject();
        foreach (var entry in overrides) streamers[entry.Id] = new JsonObject { ["portrait"] = entry.Portrait };
        var pack = JsonNode.Parse(ReadContent(Path.Combine("Packs", packId + ".json")));
        pack["art"] = new JsonObject { ["portraitDir"] = portraitDirectory, ["streamers"] = streamers };
        return ContentDb.FromJson(ReadContent("StreamDuelRules.json"), pack.ToJsonString());
    }

    static void WritePortraits(string root, ContentDb db, string skip = null)
    {
        byte[] png = Png(128, 128);
        foreach (var streamer in db.Streamers)
            if (streamer.Id != skip) Write(root, db.Art.PortraitPath(streamer.Id) + ".png", png);
    }

    static void Write(string root, string relative, byte[] bytes)
    {
        string fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("测试图片必须位于本次临时目录内。");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, bytes);
    }

    static byte[] Png(int width, int height, int paddedLength = 0)
    {
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = 8;
        WriteChunk(png, "IHDR", ihdr);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            var row = new byte[width + 1];
            for (int y = 0; y < height; y++) zlib.Write(row, 0, row.Length);
        }
        WriteChunk(png, "IDAT", compressed.ToArray());
        if (paddedLength > 0)
        {
            // 合法附属块精确补足大小，避免用截断文件或尾随垃圾制造边界。
            int padding = checked(paddedLength - (int)png.Length - 24);
            if (padding < 0) throw new ArgumentException("目标大小不足以容纳测试图片。");
            WriteChunk(png, "paDd", new byte[padding]);
        }
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var word = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        stream.Write(word, 0, word.Length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes, 0, typeBytes.Length);
        stream.Write(data, 0, data.Length);
        uint crc = Crc(typeBytes, uint.MaxValue);
        crc = Crc(data, crc) ^ uint.MaxValue;
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        stream.Write(word, 0, word.Length);
    }

    static byte[] DamageHeader(byte[] png, string corruption)
    {
        switch (corruption)
        {
            case "signature": png[0] = 0; break;
            case "truncated": return png.Take(32).ToArray();
            case "length": BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8, 4), 12); break;
            case "type": png[12] = (byte)'J'; break;
            case "crc": png[32] ^= 1; return png;
            case "zero-width": BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 0); break;
            case "zero-height": BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 0); break;
            case "huge-width": BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), uint.MaxValue); break;
            case "depth": png[24] = 3; break;
            case "color": png[25] = 1; break;
            case "compression": png[26] = 1; break;
            case "filter": png[27] = 1; break;
            case "interlace": png[28] = 2; break;
            default: throw new ArgumentException("未知的测试损坏方式。");
        }
        // 字段损坏时重算校验码，让断言确实覆盖字段检查而非被 CRC 提前拦住。
        uint crc = Crc(png.AsSpan(12, 17), uint.MaxValue) ^ uint.MaxValue;
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29, 4), crc);
        return png;
    }

    static uint Crc(ReadOnlySpan<byte> bytes, uint crc)
    {
        foreach (byte value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return crc;
    }

    static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            table[i] = crc;
        }
        return table;
    }

    sealed class TreeSnapshot
    {
        Dictionary<string, (byte[] Bytes, DateTime Modified)> files;
        Dictionary<string, DateTime> directories;

        public static TreeSnapshot Capture(string root)
        {
            return new TreeSnapshot
            {
                files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
                    path => path, path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)), StringComparer.Ordinal),
                directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Append(root).ToDictionary(
                    path => path, Directory.GetLastWriteTimeUtc, StringComparer.Ordinal)
            };
        }

        public void AssertUnchanged(string root)
        {
            var after = Capture(root);
            Assert.Equal(files.Keys.OrderBy(p => p, StringComparer.Ordinal), after.files.Keys.OrderBy(p => p, StringComparer.Ordinal));
            Assert.Equal(directories.Keys.OrderBy(p => p, StringComparer.Ordinal), after.directories.Keys.OrderBy(p => p, StringComparer.Ordinal));
            foreach (var pair in files)
            {
                Assert.Equal(pair.Value.Bytes, after.files[pair.Key].Bytes);
                Assert.Equal(pair.Value.Modified, after.files[pair.Key].Modified);
            }
            foreach (var pair in directories) Assert.Equal(pair.Value, after.directories[pair.Key]);
        }
    }

    sealed class TestDirectory : IDisposable
    {
        readonly string parent;
        readonly string name = Guid.NewGuid().ToString("N");
        public string Root { get; }

        public TestDirectory([CallerFilePath] string source = "")
        {
            parent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "obj", "t123-art-check"));
            Root = Path.GetFullPath(Path.Combine(parent, name));
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            string target = Path.GetFullPath(Root);
            if (!target.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetDirectoryName(target), parent, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(target), name, StringComparison.Ordinal))
                throw new InvalidOperationException("只能清理本次资源检查测试创建的临时目录。");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }
}
