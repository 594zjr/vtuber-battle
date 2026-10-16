using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

public class GoldenTests
{
    static readonly string ProjectDirectory = SourceDirectory();
    static readonly string ContentDirectory = Path.GetFullPath(Path.Combine(ProjectDirectory,
        "..", "UnityProject", "Assets", "StreamDuel", "Resources"));
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        IncludeFields = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IEnumerable<object[]> Cases()
    {
        var files = Directory.GetFiles(Path.Combine(ContentDirectory, "Packs"))
            .Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(files);
        foreach (string file in files)
            for (uint seed = 0; seed < 10; seed++)
                yield return new object[] { Path.GetFileName(file), seed };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void NormalVersusNormalMatchesCompleteLog(string packFile, uint seed)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            // 日志包含数值文本，固定文化后才能在 Windows 与 Linux 上使用同一份基线。
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            var db = ContentDb.FromJson(
                File.ReadAllText(Path.Combine(ContentDirectory, "StreamDuelRules.json")),
                File.ReadAllText(Path.Combine(ContentDirectory, "Packs", packFile)));
            var teams = Teams(db, seed);
            var battle = new Battle(db, teams, new[] { "甲方", "乙方" }, seed);
            PlayToEnd(battle);
            string actual = CompleteLog(battle, teams);
            string goldenPath = Path.Combine(ProjectDirectory, "Golden", Path.GetFileNameWithoutExtension(packFile),
                "seed-" + seed.ToString("D10", CultureInfo.InvariantCulture) + ".txt");
            if (string.Equals(Environment.GetEnvironmentVariable("SD_UPDATE_GOLDEN"), "1", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(goldenPath));
                File.WriteAllText(goldenPath, actual, new UTF8Encoding(false));
            }
            Assert.True(File.Exists(goldenPath), "缺少 golden 基线：" + goldenPath +
                "。仅在有意修改规则或加入新内容包时设置 SD_UPDATE_GOLDEN=1 生成，并在 PR 中说明原因。");
            Assert.Equal(File.ReadAllText(goldenPath, Encoding.UTF8), actual);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    static IList<string>[] Teams(ContentDb db, uint seed)
    {
        var ids = db.Streamers.Select(streamer => streamer.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.True(ids.Length >= 6, "内容包至少需要 6 位主播：" + db.PackId);
        int offset = (int)(seed % (uint)ids.Length);
        var teams = new IList<string>[2];
        for (int side = 0; side < 2; side++)
        {
            // 轮换固定阵容不额外消耗战斗随机数；不足 12 人时允许两队重叠，但队内不重复。
            teams[side] = Enumerable.Range(0, 6).Select(index => ids[(offset + side * 6 + index) % ids.Length]).ToArray();
            Assert.Empty(TeamCheck.Validate(db, null, teams[side]));
        }
        return teams;
    }

    static void PlayToEnd(Battle battle)
    {
        int steps = 0;
        while (battle.Phase != Phase.Over && steps++ < Battle.TurnCap * 3)
        {
            if (battle.Phase == Phase.Choose)
            {
                // 固定历史策略的输入，让规则日志回归不随调参默认值一起漂移。
                var first = Ai.ChooseWithWeights(battle, 0, AiLevel.Normal, AiWeights.Legacy);
                var second = Ai.ChooseWithWeights(battle, 1, AiLevel.Normal, AiWeights.Legacy);
                battle.ResolveTurn(first, second);
            }
            else
            {
                Assert.Equal(Phase.Replace, battle.Phase);
                Assert.Contains(true, battle.Need);
                for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacementWithWeights(battle, side, AiLevel.Normal, AiWeights.Legacy));
            }
        }
        Assert.True(battle.Phase == Phase.Over, "普通电脑对局未结束：包=" + battle.Db.PackId + "，种子=" + battle.Seed);
        Assert.InRange(battle.Turn, 1, Battle.TurnCap);
        Assert.NotEmpty(battle.Log);
    }

    static string CompleteLog(Battle battle, IList<string>[] teams)
    {
        var text = new StringBuilder();
        text.Append(JsonSerializer.Serialize(new
        {
            Format = "StreamDuel.Golden.v1",
            PackId = battle.Db.PackId,
            Seed = battle.Seed,
            Level = "Normal",
            Teams = teams
        }, JsonOptions)).Append('\n');
        foreach (var entry in battle.Log)
            text.Append(JsonSerializer.Serialize(entry, JsonOptions)).Append('\n');
        return text.ToString();
    }

    static string SourceDirectory([CallerFilePath] string sourceFile = "")
    {
        // 直接定位编译本测试的工作树，更新时不会误写测试输出目录或另一份 checkout。
        return Path.GetDirectoryName(sourceFile);
    }
}
