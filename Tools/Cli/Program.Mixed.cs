using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static string RequestedPackId(CliOptions options)
        {
            return options.Packs == null ? options.Pack
                : "mix:" + string.Join(",", options.Packs.OrderBy(id => id, StringComparer.Ordinal).Select(Uri.EscapeDataString));
        }

        private static ContentDb LoadMixedPack(List<PackLoadResult> available, CliOptions options,
            string rules, string content, PackPolicy policy)
        {
            string[] ids = options.Packs ?? CliOptions.ParseMixedPackIds(options.Pack.Substring(4), encoded: true);
            var sources = ids.Select(id =>
            {
                string disabled = policy.PackReason(id);
                if (disabled != null) throw new ArgumentException(disabled);
                var source = available.FirstOrDefault(pack => pack.Db.PackId == id)?.Db;
                if (source != null) return source;
                // 下架与不足六人会从列表移除，补查政策原因，不能误报成缺少安装文件。
                disabled = FindDisabledReason(rules, Path.Combine(content, "Packs"), id, policy, false)
                    ?? FindDisabledReason(rules, options.PackDirectory, id, policy, false);
                throw new ArgumentException(disabled ?? "找不到混合内容包成员「" + id + "」；请检查 --pack-dir 或使用 --list-packs。");
            }).ToArray();
            var merged = ContentDb.Merge(sources);
            if (options.Packs == null && options.Pack != merged.PackId)
                throw new ArgumentException("混合包编号必须按原始 id 的 ordinal 顺序规范排列：" + merged.PackId);
            return merged;
        }

        private static void PrepareRandomTeams(ContentDb db, CliOptions options)
        {
            if (!options.RandomTeams) return;
            var teams = MixedBattle.RandomTeams(db, options.Seed);
            options.Team = teams[options.JoinHost != null ? 1 : 0];
            options.TeamB = teams[1];
        }

        private static string[] PrepareOpponent(ContentDb db, string[] player, CliOptions options)
        {
            var team = options.TeamB ?? db.Streamers.Select(streamer => streamer.Id)
                .Where(id => !player.Contains(id)).Concat(player).Take(6).ToArray();
            var errors = TeamCheck.Validate(db, null, team);
            if (errors.Count > 0) throw new ArgumentException("对方队伍无效：" + string.Join("；", errors));
            return team;
        }

        private static void ValidateRandomNetworkTeams(ContentDb db, Battle battle, CliOptions options)
        {
            if (!options.RandomTeams) return;
            var expected = MixedBattle.RandomTeams(db, options.Seed);
            if (Enumerable.Range(0, 2).Any(side =>
                !expected[side].SequenceEqual(battle.Sides[side].Team.Select(fighter => fighter.Def.Id))))
                throw new ArgumentException("随机联网阵容不一致：双方必须加载相同包组合、使用 --random-teams，并显式指定相同 --seed；本局未开始或保存。");
        }

        private static void PrintMixedNotices(ContentDb db, IEnumerable<string> ids)
        {
            if (!db.PackId.StartsWith("mix:", StringComparison.Ordinal)) return;
            Console.WriteLine("阵容来源声明：");
            foreach (string id in ids.Distinct(StringComparer.Ordinal))
            {
                var streamer = db.Streamers.First(item => item.Id == id);
                Console.WriteLine("  " + streamer.Name + "（" + id + "）：" + db.NoticeFor(id));
            }
        }
    }
}
