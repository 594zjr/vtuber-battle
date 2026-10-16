using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StreamDuel.Cli
{
    internal sealed partial class CliOptions
    {
        public bool SeasonSummaryMode;
        public int? SummarySeason;

        private bool ReadSeasonSummaryOption(string[] args, ref int index)
        {
            if (args[index] != "--season-summary") return false;
            if (SeasonSummaryMode) throw new ArgumentException("--season-summary 不能重复指定。");
            SeasonSummaryMode = true;
            if (index + 1 < args.Length && args[index + 1] != null
                && !args[index + 1].StartsWith("--", StringComparison.Ordinal) && args[index + 1] != "-h")
            {
                if (!int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out int season) || season < 1)
                    throw new ArgumentException("--season-summary 的赛季号必须是从 1 开始的整数。");
                SummarySeason = season;
            }
            return true;
        }

        private static void ValidateSeasonSummaryOptions(CliOptions options, List<string> provided)
        {
            if (!options.SeasonSummaryMode || options.Help) return;
            var unsupported = provided.Where(option => option != "--season-summary" && option != "--save-dir")
                .Distinct().ToArray();
            if (unsupported.Length > 0)
                throw new ArgumentException("--season-summary 是独立只读查询，只可搭配 --save-dir；不能与 "
                    + string.Join("、", unsupported) + " 同用。");
        }
    }
}
