using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Ladder
{
    internal sealed class Options
    {
        public string Pack = "original";
        public int Teams = 64;
        public int Rounds = 10;
        public uint Seed = 1;
        public AiLevel Level = AiLevel.Normal;
        public string Season = null;
        public string OutputDirectory = "./ladder_out";
        public bool AiRegression;
        public int Games = 100;
        public string Baseline;
        public string WriteBaseline;
        public bool Help;
        public bool CompareOpeners;
        public int ComparisonBlocks = 250;

        public static Options Parse(string[] args)
        {
            if (args == null) throw new ArgumentException("参数列表不能为空。");

            var options = new Options();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                string option = args[i];
                if (option != "--pack" && option != "--teams" && option != "--rounds" && option != "--seed"
                    && option != "--level" && option != "--season" && option != "--out" && option != "--help"
                    && option != "--compare-openers" && option != "--blocks"
                    && option != "--ai-regression" && option != "--games" && option != "--baseline" && option != "--write-baseline")
                    throw new ArgumentException("未知参数：" + (string.IsNullOrEmpty(option) ? "空参数" : option) + "；使用 --help 查看用法。");
                if (!seen.Add(option)) throw new ArgumentException("参数 " + option + " 不能重复指定。");
                if (option == "--help")
                {
                    options.Help = true;
                    continue;
                }
                if (option == "--compare-openers")
                {
                    options.CompareOpeners = true;
                    continue;
                }
                if (option == "--ai-regression")
                {
                    options.AiRegression = true;
                    continue;
                }

                if (++i >= args.Length || args[i] == null || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException(option + " 缺少参数值。");
                string value = args[i];
                switch (option)
                {
                    case "--pack":
                        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--pack 需要非空的内容包编号。");
                        options.Pack = value;
                        break;
                    case "--teams":
                        if (!IsDecimal(value) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.Teams)
                            || options.Teams < 2)
                            throw new ArgumentException("--teams 必须是 2..2147483647 的十进制整数，允许奇数。");
                        break;
                    case "--rounds":
                        if (!IsDecimal(value) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.Rounds)
                            || options.Rounds < 1)
                            throw new ArgumentException("--rounds 必须是 1..2147483647 的十进制整数。");
                        break;
                    case "--seed":
                        if (!IsDecimal(value) || !uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.Seed))
                            throw new ArgumentException("--seed 必须是 0..4294967295 的十进制整数。");
                        break;
                    case "--level":
                        if (value == "normal") options.Level = AiLevel.Normal;
                        else if (value == "hard") options.Level = AiLevel.Hard;
                        else throw new ArgumentException("--level 只能是 normal（普通）或 hard（困难）。");
                        break;
                    case "--season":
                        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--season 需要非空的赛季编号。");
                        options.Season = value;
                        break;
                    case "--out":
                        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--out 需要非空的输出文件夹路径。");
                        options.OutputDirectory = value;
                        break;
                    case "--blocks":
                        if (!IsDecimal(value) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.ComparisonBlocks)
                            || options.ComparisonBlocks < 1 || options.ComparisonBlocks > int.MaxValue / 4)
                            throw new ArgumentException("--blocks 必须是 1..536870911 的十进制整数，每组四局。");
                        break;
                    case "--games":
                        if (!IsDecimal(value) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.Games)
                            || options.Games < 2 || options.Games % 2 != 0)
                            throw new ArgumentException("--games 必须是 2..2147483646 的偶数，每组阵容必须交换难度打两局。");
                        break;
                    case "--baseline":
                        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--baseline 需要非空的基准文件路径。");
                        options.Baseline = value;
                        break;
                    case "--write-baseline":
                        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--write-baseline 需要非空的输出文件路径。");
                        options.WriteBaseline = value;
                        break;
                }
            }

            if (options.CompareOpeners && options.AiRegression)
                throw new ArgumentException("--compare-openers 与 --ai-regression 不能同时使用。");
            if (options.CompareOpeners && (seen.Contains("--teams") || seen.Contains("--rounds") || seen.Contains("--level")))
                throw new ArgumentException("--compare-openers 固定双方行动/补位为 hard，请用 --blocks 指定规模，不能混用 --teams、--rounds 或 --level。");
            if (!options.CompareOpeners && seen.Contains("--blocks"))
                throw new ArgumentException("--blocks 只能与 --compare-openers 一起使用。");

            string[] ladderOnly = { "--teams", "--rounds", "--level", "--season", "--out" };
            string[] regressionOnly = { "--games", "--baseline", "--write-baseline" };
            foreach (string option in options.AiRegression ? ladderOnly : regressionOnly)
                if (seen.Contains(option))
                    throw new ArgumentException(option + (options.AiRegression ? " 不能与 --ai-regression 同时使用。" : " 只能与 --ai-regression 同时使用。"));
            if (options.Baseline != null && options.WriteBaseline != null)
                throw new ArgumentException("--baseline 和 --write-baseline 不能同时使用：一次只比较已有基准或生成候选基准。");
            return options;
        }

        private static bool IsDecimal(string value)
        {
            return value.Length > 0 && value.All(c => c >= '0' && c <= '9');
        }
    }
}
