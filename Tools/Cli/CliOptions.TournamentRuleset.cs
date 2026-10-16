using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal sealed partial class CliOptions
    {
        public string TournamentRulesetId;

        private bool ReadTournamentRulesetOption(string[] args, ref int index)
        {
            if (args[index] != "--tournament-ruleset") return false;
            if (TournamentRulesetId != null) throw new ArgumentException("--tournament-ruleset 不能重复指定。");
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])
                || args[index].StartsWith("--", StringComparison.Ordinal) || args[index] == "-h")
                throw new ArgumentException("--tournament-ruleset 缺少赛制编号。");
            string id = args[index];
            if (!Rulesets.All.Any(rule => rule.Id == id))
                throw new ArgumentException("未知赛事赛制：" + id + "；可用赛制："
                    + string.Join("、", Rulesets.All.Select(rule => rule.Id)) + "。");
            TournamentRulesetId = id;
            return true;
        }

        private static void ValidateTournamentRulesetOptions(CliOptions options, List<string> provided)
        {
            if (options.TournamentRulesetId == null || options.Help) return;
            if (options.TournamentFile == null)
                throw new ArgumentException("--tournament-ruleset 只能与 --tournament 同用。");
            if (options.NoAutoTiebreak)
                throw new ArgumentException("--tournament-ruleset 不能与 --no-auto-tiebreak 同用；逐局与加赛由赛事赛制决定。");
            var allowed = new[] { "--tournament-ruleset", "--tournament", "--format", "--pack", "--pack-dir", "--auto", "--tournament-seed" };
            var unsupported = provided.Where(option => !allowed.Contains(option)).Distinct().ToArray();
            if (unsupported.Length > 0)
                throw new ArgumentException("--tournament-ruleset 不能与 " + string.Join("、", unsupported)
                    + " 同用；请只提供赛事、内容包、自动代打与赛事种子参数。");
        }
    }
}
