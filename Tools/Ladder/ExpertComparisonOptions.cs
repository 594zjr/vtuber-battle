using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Ladder
{
    internal sealed class ExpertComparisonOptions
    {
        public int Games = 300;
        public int MaxNodes = ExpertBudget.DefaultMaxNodes;
        public int Workers = 1;
        public uint Seed = 1;
        public string Mode = "paired";
        public string OutputDirectory = "./ladder_expert_out";
        public bool Help;

        public static ExpertComparisonOptions Parse(string[] args)
        {
            var options = new ExpertComparisonOptions();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                string name = args[i];
                if (name != "--compare-expert" && name != "--compare-mode" && name != "--games"
                    && name != "--budget" && name != "--workers" && name != "--seed" && name != "--out" && name != "--pack" && name != "--help")
                    throw new ArgumentException("专家比较模式不支持参数：" + (name ?? "空参数") + "；使用 --compare-expert --help 查看用法。");
                if (!seen.Add(name)) throw new ArgumentException("参数 " + name + " 不能重复指定。");
                if (name == "--compare-expert") continue;
                if (name == "--help") { options.Help = true; continue; }
                if (++i >= args.Length || args[i] == null || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException(name + " 缺少参数值。");
                string value = args[i];
                switch (name)
                {
                    case "--compare-mode":
                        if (value != "paired" && value != "hard" && value != "both")
                            throw new ArgumentException("--compare-mode 只能是 paired、hard 或 both。");
                        options.Mode = value;
                        break;
                    case "--games": options.Games = Integer(value, name, 1, int.MaxValue / 4); break;
                    case "--budget": options.MaxNodes = Integer(value, name, 0, int.MaxValue); break;
                    case "--workers": options.Workers = Integer(value, name, 1, 16); break;
                    case "--seed":
                        if (!Decimal(value) || !uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.Seed))
                            throw new ArgumentException("--seed 必须是 0..4294967295 的十进制整数。");
                        break;
                    case "--out":
                        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--out 需要非空的输出文件夹路径。");
                        options.OutputDirectory = value;
                        break;
                    case "--pack":
                        if (value != "original") throw new ArgumentException("专家比较固定使用原创包 original。");
                        break;
                }
            }
            return options;
        }

        static int Integer(string value, string name, int minimum, int maximum)
        {
            if (!Decimal(value) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result)
                || result < minimum || result > maximum)
                throw new ArgumentException(name + " 必须是 " + minimum + ".." + maximum + " 的十进制整数。");
            return result;
        }

        static bool Decimal(string value) { return value.Length > 0 && value.All(c => c >= '0' && c <= '9'); }
    }
}
