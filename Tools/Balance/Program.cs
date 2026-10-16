using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Balance
{
    internal static class Program
    {
        private static readonly string[] BuiltInPacks = { "original", "original_wave2", "fan_asoul_vr", "fan_holo_niji", "original_wave3" };

        private sealed class Options
        {
            public string Pack;
            public int GamesPerStreamer = 200;
            public uint Seed = 2026;
            public AiLevel Level = AiLevel.Normal;
            public bool Help;
        }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Out.NewLine = "\n";
            Console.Error.NewLine = "\n";
            try
            {
                var options = ParseOptions(args);
                if (options.Help)
                {
                    WriteHelp();
                    return 0;
                }

                string content = Path.Combine(AppContext.BaseDirectory, "Content");
                var policy = PackPolicy.Load(Path.Combine(content, "PackPolicy.json"));
                string rules = File.ReadAllText(Path.Combine(content, "StreamDuelRules.json"), Encoding.UTF8);
                var packs = options.Pack == null ? BuiltInPacks : new[] { options.Pack };
                var csv = new StringBuilder("pack_id,content_hash,streamer_id,name,games_per_streamer,seed,level,win_rate\n");
                foreach (string pack in packs)
                {
                    string json = File.ReadAllText(Path.Combine(content, "Packs", pack + ".json"), Encoding.UTF8);
                    var db = ContentDb.FromJson(rules, json, policy);
                    string contentHash = db.ContentHash();
                    var rates = BalanceCheck.WinRates(db, options.GamesPerStreamer, options.Seed, options.Level);
                    foreach (var streamer in db.Streamers.OrderBy(item => item.Id, StringComparer.Ordinal))
                    {
                        // API 返回真实入队次数口径的比例；最低次数仅作为本轮请求参数输出。
                        string[] row =
                        {
                            db.PackId, contentHash, streamer.Id, streamer.Name,
                            options.GamesPerStreamer.ToString(CultureInfo.InvariantCulture),
                            options.Seed.ToString(CultureInfo.InvariantCulture),
                            options.Level.ToString().ToLowerInvariant(),
                            rates[streamer.Id].ToString("R", CultureInfo.InvariantCulture)
                        };
                        csv.Append(string.Join(",", row.Select(Csv))).Append('\n');
                    }
                }
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
                if (option != "--pack" && option != "--games-per-streamer" && option != "--seed" && option != "--level")
                    throw new ArgumentException("未知选项：" + option + "；使用 --help 查看用法。");
                if (!seen.Add(option)) throw new ArgumentException("选项不能重复：" + option + "。");
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])
                    || args[index].StartsWith("--", StringComparison.Ordinal) || args[index] == "-h")
                    throw new ArgumentException(option + " 缺少参数值。");
                string value = args[index];
                switch (option)
                {
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
            Console.WriteLine("输出 UTF-8 CSV：包 id、内容指纹、主播 id、姓名、请求最低入队次数、种子、难度、胜率。");
            Console.WriteLine("win_rate 为 Core.BalanceCheck 返回的 0..1 原始比例，平局算半胜；含后排入队，按真实入队次数统计。输出不包含耗时，主播按 id 序排序。");
        }
    }
}
