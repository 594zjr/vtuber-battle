using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Ladder
{
    internal sealed class StyleComparisonOptions
    {
        public int Games = 300;
        public uint Seed = 1;
        public AiLevel Level = AiLevel.Normal;
        public string OutputDirectory = "./ladder_styles_out";
        public bool Help;

        public static StyleComparisonOptions Parse(string[] args)
        {
            var options = new StyleComparisonOptions();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string argument in args ?? Array.Empty<string>())
                if (argument == null) throw new ArgumentException("风格比较参数不能为空。");
            if (args == null) throw new ArgumentException("参数列表不能为空。");
            for (int i = 0; i < args.Length; i++)
            {
                string name = args[i];
                if (name != "--compare-styles" && name != "--games" && name != "--seed"
                    && name != "--level" && name != "--out" && name != "--pack" && name != "--help")
                    throw new ArgumentException("风格比较模式不支持参数：" + name + "；使用 --compare-styles --help 查看用法。");
                if (!seen.Add(name)) throw new ArgumentException("参数 " + name + " 不能重复指定。");
                if (name == "--compare-styles") continue;
                if (name == "--help") { options.Help = true; continue; }
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException(name + " 缺少参数值。");
                string value = args[i];
                switch (name)
                {
                    case "--games":
                        if (!Decimal(value) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.Games)
                            || options.Games < 2 || options.Games > int.MaxValue / 2 || options.Games % 2 != 0)
                            throw new ArgumentException("--games 必须是 2..1073741823 内的偶数，表示每组场数；两组总场数为它的两倍。");
                        break;
                    case "--seed":
                        if (!Decimal(value) || !uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.Seed))
                            throw new ArgumentException("--seed 必须是 0..4294967295 的十进制整数。");
                        break;
                    case "--level":
                        if (value == "normal") options.Level = AiLevel.Normal;
                        else if (value == "hard") options.Level = AiLevel.Hard;
                        else throw new ArgumentException("--level 只能是 normal（普通）或 hard（困难）。");
                        break;
                    case "--pack":
                        if (value != "original_wave3") throw new ArgumentException("风格比较固定使用第三期原创包 original_wave3。");
                        break;
                    case "--out":
                        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--out 需要非空的输出文件夹路径。");
                        options.OutputDirectory = value;
                        break;
                }
            }
            return options;
        }

        static bool Decimal(string value) { return value.Length > 0 && value.All(c => c >= '0' && c <= '9'); }
    }
}
