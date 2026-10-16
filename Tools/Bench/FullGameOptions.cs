using System;
using System.Collections.Generic;
using System.Globalization;

namespace StreamDuel.Bench
{
    internal sealed class FullGameOptions
    {
        public int Games = FullGameWorkload.GamesPerGroup;
        public string Output = "bench_out/full-games";
        public string Baseline;
        public bool Help;
        public bool VerifyOnly;

        public static FullGameOptions Parse(string[] args)
        {
            var result = new FullGameOptions();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < args.Length; index++)
            {
                string name = args[index];
                if (!seen.Add(name)) throw new ArgumentException("参数不能重复：" + name);
                if (name == "--full-games") continue;
                if (name == "--verify-only") { result.VerifyOnly = true; continue; }
                if (name == "--help" || name == "-h") { result.Help = true; continue; }
                if (name != "--out" && name != "--compare" && name != "--games")
                    throw new ArgumentException("整局基准不支持参数：" + name);
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("参数缺少值：" + name);
                if (name == "--out") result.Output = args[index];
                else if (name == "--compare") result.Baseline = args[index];
                else if (!int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out result.Games)
                    || result.Games < 1 || result.Games > FullGameWorkload.GamesPerGroup)
                    throw new ArgumentException("--games 必须是 1..200；正式验收必须使用默认 200。");
            }
            if (!result.Help && result.VerifyOnly && (result.Baseline == null || !seen.Contains("--out")))
                throw new ArgumentException("--verify-only 必须显式提供 --out 已完成目录和 --compare 已完成基线目录。");
            if (result.VerifyOnly && result.Games != FullGameWorkload.GamesPerGroup)
                throw new ArgumentException("--verify-only 只核验正式 2400 局，不能缩小 --games。");
            return result;
        }
    }
}
