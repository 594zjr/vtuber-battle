// T-51 验收测试：自制内容包教程里的示例包必须能加载。实现后把本文件移到 Engine.Tests/ 下。
using System;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T51_ExamplePackTests
{
    [Fact]
    public void ExamplePackInDocsLoadsAndPlays()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var rules = File.ReadAllText(Path.Combine(root, "UnityProject", "Assets", "StreamDuel", "Resources", "StreamDuelRules.json"));
        var results = PackLoader.LoadDirectory(rules, Path.Combine(root, "docs", "examples"));
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Null(r.Error));
        var db = results[0].Db;
        Assert.True(db.Streamers.Count >= 6);
        Assert.Contains(db.Skills.Keys, k => k.StartsWith("ex_"));
        var ids = db.Streamers.Select(s => s.Id).Take(6).ToList();
        var b = new Battle(db, new System.Collections.Generic.IList<string>[] { ids, ids }, new[] { "A", "B" }, 1);
        int guard = 0;
        while (b.Phase != Phase.Over && guard++ < 500)
        {
            if (b.Phase == Phase.Choose) b.ResolveTurn(Ai.Choose(b, 0), Ai.Choose(b, 1));
            else for (int s = 0; s < 2; s++) if (b.Need[s]) b.Replace(s, Ai.ChooseReplacement(b, s));
        }
        Assert.Equal(Phase.Over, b.Phase);
        var manual = File.ReadAllText(Path.Combine(root, "docs", "modding.md"));
        Assert.Contains("docs/examples/", manual);
    }
}
