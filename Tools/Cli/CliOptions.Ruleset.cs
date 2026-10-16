using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Cli
{
    internal sealed partial class CliOptions
    {
        public string RulesetId;

        private static void ValidateRulesetOptions(CliOptions options, List<string> provided)
        {
            if (options.RulesetId == null || options.Help || options.ListPacks) return;
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "--ruleset", "--pack", "--packs", "--pack-dir", "--level", "--seed", "--team", "--team-b",
                "--team-code", "--export-team", "--random-teams", "--save-dir", "--auto", "--hash",
                "--share", "--report", "--advice", "--spectate-port", "--spectator-chat"
            };
            var unsupported = provided.Where(option => !allowed.Contains(option)).Distinct().ToArray();
            if (unsupported.Length > 0)
                throw new ArgumentException("--ruleset 用于本地赛制对战，不能与 " + string.Join("、", unsupported)
                    + " 同用；禁用、首发和计时由赛制决定，联网、赛事及查询入口请单独使用。");
        }
    }
}
