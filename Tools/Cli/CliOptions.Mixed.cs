using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Cli
{
    internal sealed partial class CliOptions
    {
        public string[] Packs;
        public bool RandomTeams;
        public bool SeedSpecified;
        public bool MixedPack { get { return Packs != null || (Pack != null && Pack.StartsWith("mix:", StringComparison.Ordinal)); } }

        internal static string[] ParseMixedPackIds(string value, bool encoded = false)
        {
            var parts = value.Split(',').Select(id => id.Trim()).ToArray();
            var ids = encoded ? parts.Select(Uri.UnescapeDataString).ToArray() : parts;
            if (encoded && parts.Where((part, index) => part != Uri.EscapeDataString(ids[index])).Any())
                throw new ArgumentException("mix: 中的包 id 必须使用规范 URI 转义；普通包 id 请通过 --packs 指定。");
            if (ids.Length < (encoded ? 1 : 2) || ids.Any(id => id.Length == 0 || id.StartsWith("mix:", StringComparison.Ordinal) || id.Any(char.IsControl)))
                throw new ArgumentException(encoded
                    ? "mix: 需要至少一个非空原始包 id，不能递归包含 mix: 或控制字符。"
                    : "混合内容包需要至少两个逗号分隔的非空原始包 id，不能递归包含 mix: 或控制字符。");
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                throw new ArgumentException("混合内容包 id 不能重复。");
            return ids;
        }

        private static void ValidateMixedOptions(CliOptions options, List<string> provided)
        {
            if (options.Help) return;
            if (options.Packs != null && provided.Contains("--pack"))
                throw new ArgumentException("--pack 与 --packs 不能同时使用。");
            if (options.Packs != null && (options.ReplayMode || options.ListPacks || options.ListReplays))
                throw new ArgumentException("--packs 不能与回看或列表入口同用；回看自动使用记录内的包组合。");
            if (options.RandomTeams)
            {
                if (options.Team != null || options.TeamB != null || options.TeamCode != null)
                    throw new ArgumentException("--random-teams 与 --team、--team-b、--team-code 不能同时使用。");
                if (options.ReplayMode || options.ListPacks || options.ListReplays || options.AnalysisMode || options.Glossary
                    || options.TournamentFile != null)
                    throw new ArgumentException("--random-teams 仅用于新对局，不能与回看、列表、分析、图鉴或赛事入口同用。");
                if (options.Network && !options.SeedSpecified)
                    throw new ArgumentException("随机阵容联网要求双方显式指定相同 --seed，并都使用 --random-teams。");
            }
            if (options.TeamB != null && !options.Hotseat && (options.Network || options.ReplayMode
                || options.ListPacks || options.ListReplays || options.AnalysisMode || options.Glossary))
                throw new ArgumentException("--team-b 仅用于同屏对战或混合包本地新对局；联网只指定自己的 --team。");
        }
    }
}
