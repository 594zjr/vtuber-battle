using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Balance
{
    internal static partial class Program
    {
        private static readonly string[] BuiltInPacks = { "original", "original_wave2", "fan_asoul_vr", "fan_holo_niji", "original_wave3" };

        private sealed class Options
        {
            public string Pack;
            public int GamesPerStreamer = 200;
            public uint Seed = 2026;
            public AiLevel Level = AiLevel.Normal;
            public bool Help;
            public string SaveBalance;
        }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Out.NewLine = "\n";
            Console.Error.NewLine = "\n";
            try
            {
                if (args.Contains("--compare-balance")) return RunBalanceComparison(args);
                var options = ParseOptions(args);
                if (options.Help)
                {
                    WriteHelp();
                    return 0;
                }

                string outputPath = options.SaveBalance == null ? null : BalanceOutputPath(options.SaveBalance);
                string content = Path.Combine(AppContext.BaseDirectory, "Content");
                var policy = PackPolicy.Load(Path.Combine(content, "PackPolicy.json"));
                string rules = File.ReadAllText(Path.Combine(content, "StreamDuelRules.json"), Encoding.UTF8);
                var packs = options.Pack == null ? BuiltInPacks : new[] { options.Pack };
                var snapshot = new BalanceSnapshot
                {
                    GamesPerStreamer = options.GamesPerStreamer,
                    Seed = options.Seed,
                    Level = options.Level.ToString().ToLowerInvariant(),
                    AiVersion = "core-mvid:" + typeof(Ai).Module.ModuleVersionId.ToString("N"),
                    Packs = new List<BalancePackSnapshot>()
                };
                var csv = new StringBuilder("pack_id,content_hash,streamer_id,name,games_per_streamer,seed,level,win_rate\n");
                foreach (string pack in packs)
                {
                    string json = File.ReadAllText(Path.Combine(content, "Packs", pack + ".json"), Encoding.UTF8);
                    var db = ContentDb.FromJson(rules, json, policy);
                    string contentHash = db.ContentHash();
                    var run = BalanceCheck.Measure(db, options.GamesPerStreamer, options.Seed, options.Level);
                    var stats = run.Streamers.ToDictionary(item => item.Id, StringComparer.Ordinal);
                    var packSnapshot = new BalancePackSnapshot
                    {
                        PackId = db.PackId, ContentHash = contentHash, Battles = run.Battles,
                        Streamers = new List<BalanceStreamerSnapshot>()
                    };
                    foreach (var streamer in db.Streamers.OrderBy(item => item.Id, StringComparer.Ordinal))
                    {
                        packSnapshot.Streamers.Add(BalanceStreamerSnapshot.FromStats(stats[streamer.Id], streamer.Name));
                        // API 返回真实入队次数口径的比例；最低次数仅作为本轮请求参数输出。
                        string[] row =
                        {
                            db.PackId, contentHash, streamer.Id, streamer.Name,
                            options.GamesPerStreamer.ToString(CultureInfo.InvariantCulture),
                            options.Seed.ToString(CultureInfo.InvariantCulture),
                            options.Level.ToString().ToLowerInvariant(),
                            stats[streamer.Id].WinRate.ToString("R", CultureInfo.InvariantCulture)
                        };
                        csv.Append(string.Join(",", row.Select(Csv))).Append('\n');
                    }
                    snapshot.Packs.Add(packSnapshot);
                }
                if (outputPath != null) SaveBalanceSnapshot(snapshot, outputPath);
                Console.Write(csv.ToString());
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("错误：" + ex.Message);
                return 1;
            }
        }

        private static Options ParseOptions(string[] args)
        {
            var options = new Options();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < args.Length; index++)
            {
                string option = args[index];
                if (option == "--help" || option == "-h")
                {
                    options.Help = true;
                    continue;
                }
                if (option != "--pack" && option != "--games-per-streamer" && option != "--seed" && option != "--level" && option != "--save-balance")
                    throw new ArgumentException("未知选项：" + option + "；使用 --help 查看用法。");
                if (!seen.Add(option)) throw new ArgumentException("选项不能重复：" + option + "。");
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])
                    || args[index].StartsWith("--", StringComparison.Ordinal) || args[index] == "-h")
                    throw new ArgumentException(option + " 缺少参数值。");
                string value = args[index];
                switch (option)
                {
                    case "--save-balance":
                        options.SaveBalance = value;
                        break;
                    case "--pack":
                        if (!BuiltInPacks.Contains(value, StringComparer.Ordinal))
                            throw new ArgumentException("未知内置包：" + value + "；可选 " + string.Join("、", BuiltInPacks) + "。");
                        options.Pack = value;
                        break;
                    case "--games-per-streamer":
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int games) || games <= 0)
                            throw new ArgumentException("--games-per-streamer 必须是 1..2147483647 的正整数。");
                        options.GamesPerStreamer = games;
                        break;
                    case "--seed":
                        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint seed))
                            throw new ArgumentException("--seed 必须是 0..4294967295 的整数。");
                        options.Seed = seed;
                        break;
                    case "--level":
                        switch (value)
                        {
                            case "easy": options.Level = AiLevel.Easy; break;
                            case "normal": options.Level = AiLevel.Normal; break;
                            case "hard": options.Level = AiLevel.Hard; break;
                            case "expert": options.Level = AiLevel.Expert; break;
                            default: throw new ArgumentException("--level 必须是 easy、normal、hard 或 expert。");
                        }
                        break;
                }
            }
            return options;
        }

        private static string Csv(string value)
        {
            if (value == null) return "";
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        private static void WriteHelp()
        {
            Console.WriteLine("用法：dotnet run --project Tools/Balance -- [--pack original] [--games-per-streamer 200] [--seed 2026] [--level normal]");
            Console.WriteLine("--pack：选择 " + string.Join("、", BuiltInPacks) + "；省略时依次检查全部内置包。");
            Console.WriteLine("--games-per-streamer：每位主播最低入队次数，正整数，默认 200；实际统计次数可能更高，此参数不是胜率分母。");
            Console.WriteLine("--seed：0..4294967295 的整数，默认 2026；每个包使用同一个给定种子。");
            Console.WriteLine("--level：easy（简单）、normal（普通）、hard（困难）、expert（专家），默认 normal；双方使用相同难度。");
            Console.WriteLine("--help / -h：显示此说明。");
            Console.WriteLine("--save-balance <新文件.json>：另存本次结果、实际入队数/局数、近似 95% 区间、内容和电脑构建版本；父目录须存在，不覆盖已有文件、不写入内容资源目录。");
            Console.WriteLine("--compare-balance <旧文件> <新文件> [--change-threshold 0.05]：独立只读对比，不与模拟或保存选项合用；阈值按比例填写，0.05 即 5 个百分点。");
            Console.WriteLine("胜率变化严格超过阈值且区间不重叠时标记明显变化；新结果超出 35%～65% 时返回 1，边界包含。条件不同只提示，不阻止比较。");
            Console.WriteLine("输出 UTF-8 CSV：包 id、内容指纹、主播 id、姓名、请求最低入队次数、种子、难度、胜率。");
            Console.WriteLine("win_rate 为 Core.BalanceCheck 返回的 0..1 原始比例，平局算半胜；含后排入队，按真实入队次数统计。输出不包含耗时，主播按 id 序排序。");
        }
    }
}
