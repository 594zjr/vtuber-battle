using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Cli
{
    internal sealed partial class CliOptions
    {
        public bool BugReportAtEnd;
        public string ReproduceCode;

        private static void ValidateBugReportOptions(CliOptions options, List<string> provided)
        {
            if (options.Help) return;
            if (options.ReproduceCode != null)
            {
                var allowed = new HashSet<string>(new[] { "--reproduce", "--pack", "--pack-dir" }, StringComparer.Ordinal);
                var unsupported = provided.Where(option => !allowed.Contains(option)).Distinct().ToArray();
                if (unsupported.Length > 0)
                    throw new ArgumentException("--reproduce 只可搭配 --pack、--pack-dir；不开始新对局或写入存档，不能与 "
                        + string.Join("、", unsupported) + " 同用。");
            }
            if (options.BugReportAtEnd && (options.ReplayMode || options.ListPacks || options.ListReplays
                || options.Glossary || options.AnalysisMode || options.TournamentFile != null || options.LanList))
                throw new ArgumentException("--bug-report 仅用于新对局结束导出，不能与回看、列表、图鉴、分析、赛事或搜索入口同用。");
        }
    }
}
