using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Cli
{
    internal sealed partial class CliOptions
    {
        private static void ValidateOpponentOptions(CliOptions options, List<string> provided)
        {
            if (options.OpponentId == null || options.Help) return;
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "--vs", "--pack", "--pack-dir"
            };
            if (!options.ListOpponents)
                allowed.UnionWith(new[]
                {
                    "--level", "--seed", "--team", "--team-code", "--export-team", "--auto",
                    "--ban", "--lead", "--advice", "--timer", "--save-dir", "--hash", "--share", "--report"
                });
            // 独立入口会提前返回，必须先拒绝混用，避免悄悄忽略指定的陪练。
            var unsupported = provided.Where(option => !allowed.Contains(option)).Distinct().ToArray();
            if (unsupported.Length > 0)
                throw new ArgumentException((options.ListOpponents ? "--vs list 仅列出当前包的电脑对手"
                    : "--vs 仅用于当前包的本地单局电脑对战")
                    + "，不能与 " + string.Join("、", unsupported) + " 同用。");
        }
    }
}
