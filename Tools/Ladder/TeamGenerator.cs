using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Ladder
{
    internal static class TeamGenerator
    {
        const int TeamSize = 6;

        public static List<string[]> Generate(ContentDb db, string seasonId, int count, uint seed)
        {
            if (db == null) throw new ArgumentNullException(nameof(db), "内容数据不能为空");
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "阵容数量不能为负数");
            SeasonDef season = null;
            if (seasonId != null && !db.Seasons.TryGetValue(seasonId, out season))
                throw new ArgumentException("赛季不存在：" + seasonId, nameof(seasonId));
            var teams = new List<string[]>(count);
            if (count == 0) return teams;

            var banned = new HashSet<string>(season == null ? Enumerable.Empty<string>() : season.Banned, StringComparer.Ordinal);
            var candidates = db.Streamers.Select(streamer => streamer.Id).Distinct(StringComparer.Ordinal)
                .Where(id => !banned.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var pools = season == null ? new LimitedPoolDef[0] : season.Limited.ToArray();
            var limits = pools.Select(pool => pool.Max).ToArray();
            if (candidates.Length < TeamSize || limits.Any(limit => limit < 0))
                throw new InvalidOperationException("当前名单和赛季限制无法组成合法的 6 人阵容");

            var poolMembers = pools.Select(pool => new HashSet<string>(pool.Ids, StringComparer.Ordinal)).ToArray();
            var memberships = candidates.Select(id => Enumerable.Range(0, pools.Length)
                .Where(pool => poolMembers[pool].Contains(id)).ToArray()).ToArray();
            // T-38 允许工具使用独立种子生成阵容，不消耗任何对战的随机数。
            var random = new Random(unchecked((int)seed));
            for (int teamIndex = 0; teamIndex < count; teamIndex++)
            {
                if (teamIndex < count / 2)
                {
                    teams.Add(TeamBuilder.Suggest(db, seasonId, null, unchecked(seed + (uint)teamIndex)).ToArray());
                    continue;
                }
                var order = Enumerable.Range(0, candidates.Length).ToArray();
                Shuffle(order, random);
                var selected = new int[TeamSize];
                var used = new int[pools.Length];
                if (!TryBuild(order, 0, 0, selected, memberships, used, limits))
                    throw new InvalidOperationException("当前名单和赛季限制无法组成合法的 6 人阵容");

                var team = selected.Select(index => candidates[index]).ToArray();
                // 组合是否合法与号位无关，搜索完成后单独随机号位，保留首发和第六位的差异。
                Shuffle(team, random);
                var errors = TeamCheck.Validate(db, seasonId, team);
                if (errors.Count != 0)
                    throw new InvalidOperationException("生成的阵容未通过规则检查：" + string.Join("；", errors));
                teams.Add(team);
            }
            return teams;
        }

        static bool TryBuild(int[] order, int start, int depth, int[] selected,
            int[][] memberships, int[] used, int[] limits)
        {
            if (depth == TeamSize) return true;
            int needed = TeamSize - depth;
            if (order.Length - start < needed) return false;

            int available = 0;
            var availableInPool = new int[limits.Length];
            for (int i = start; i < order.Length; i++)
            {
                var memberPools = memberships[order[i]];
                if (!CanAdd(memberPools, used, limits)) continue;
                available++;
                foreach (int pool in memberPools) availableInPool[pool]++;
            }
            if (available < needed) return false;
            for (int pool = 0; pool < limits.Length; pool++)
            {
                int capacity = available - availableInPool[pool]
                    + Math.Min(availableInPool[pool], limits[pool] - used[pool]);
                if (capacity < needed) return false;
            }

            // 只遍历递增下标的组合，避免无解时反复搜索同一组成员的不同排列。
            for (int i = start; i <= order.Length - needed; i++)
            {
                int candidate = order[i];
                var memberPools = memberships[candidate];
                if (!CanAdd(memberPools, used, limits)) continue;
                selected[depth] = candidate;
                foreach (int pool in memberPools) used[pool]++;
                if (TryBuild(order, i + 1, depth + 1, selected, memberships, used, limits)) return true;
                foreach (int pool in memberPools) used[pool]--;
            }
            return false;
        }

        static bool CanAdd(int[] memberPools, int[] used, int[] limits)
        {
            foreach (int pool in memberPools)
                if (used[pool] >= limits[pool]) return false;
            return true;
        }

        static void Shuffle<T>(T[] values, Random random)
        {
            for (int i = values.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                T value = values[i];
                values[i] = values[j];
                values[j] = value;
            }
        }
    }
}
