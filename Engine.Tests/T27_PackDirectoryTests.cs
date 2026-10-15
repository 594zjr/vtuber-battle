// T-27 验收测试：从外部文件夹加载内容包。实现后把本文件移到 Engine.Tests/ 下。
using System;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T27_PackDirectoryTests
{
    static string Res(string rel) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Content", rel));

    [Fact]
    public void LoadsGoodPacksAndReportsBadOnes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sd_packs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var orig = Res(Path.Combine("Packs", "original.json"));
            File.WriteAllText(Path.Combine(dir, "a_good.json"), orig.Replace("\"id\": \"original\"", "\"id\": \"my_pack\""));
            File.WriteAllText(Path.Combine(dir, "b_broken.json"), "{ 坏掉的 json");
            File.WriteAllText(Path.Combine(dir, "c_dup.json"), orig.Replace("\"id\": \"original\"", "\"id\": \"my_pack\""));
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "不是内容包");

            var results = PackLoader.LoadDirectory(Res("StreamDuelRules.json"), dir);
            Assert.Equal(3, results.Count);
            var good = results.Single(r => r.FileName == "a_good.json");
            Assert.Null(good.Error);
            Assert.Equal("my_pack", good.Db.PackId);
            Assert.NotNull(results.Single(r => r.FileName == "b_broken.json").Error);
            var dup = results.Single(r => r.FileName == "c_dup.json");
            Assert.Null(dup.Db);
            Assert.Contains("my_pack", dup.Error);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MissingDirectoryGivesEmptyList()
    {
        Assert.Empty(PackLoader.LoadDirectory(Res("StreamDuelRules.json"), Path.Combine(Path.GetTempPath(), "no_such_" + Guid.NewGuid().ToString("N"))));
    }
}
