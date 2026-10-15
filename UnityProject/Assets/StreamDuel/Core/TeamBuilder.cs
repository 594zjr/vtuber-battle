using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public static class TeamBuilder
    {
        sealed class Candidate
        {
            public StreamerDef Def;
            public int[] Pools;
            public double[] Matchups;
            public double Speed, Bulk;
        }

        sealed class Ranked
        {
            public Candidate Candidate;
            public double Score;
        }

        /// <summary>保留固定顺序，按赛道覆盖、对位与快慢耐久搭配补齐合法六人队伍。</summary>
        public static List<string> Suggest(ContentDb db, string seasonId, IList<string> fixedIds, uint seed)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            var ids = fixedIds == null ? new List<string>() : new List<string>(fixedIds);
            var errors = TeamCheck.Validate(db, seasonId, ids);
            // 未满队正是补人的入口，只忽略 TeamCheck 在已知赛季下的首条人数错误。
            if (ids.Count < 6 && (seasonId == null || db.Seasons.ContainsKey(seasonId))) errors.RemoveAt(0);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
            if (ids.Count == 6) return ids;

            var season = seasonId == null ? null : db.Seasons[seasonId];
            var pools = season == null ? new List<LimitedPoolDef>() : season.Limited;
            var limits = pools.Select(pool => pool.Max).ToArray();
            var used = new int[limits.Length];
            // 固定顺序使相同内容和种子不受字典枚举顺序影响；对位基准包含全包，包括禁用者。
            var roster = db.Streamers.OrderBy(streamer => streamer.Id, StringComparer.Ordinal).ToList();
            var all = roster.Select(streamer => new Candidate
            {
                Def = streamer,
                Pools = Enumerable.Range(0, pools.Count).Where(i => pools[i].Ids.Contains(streamer.Id)).ToArray(),
                Matchups = roster.Select(foe => Matchup(db, streamer, foe)).ToArray(),
                Speed = streamer.Stats["spd"],
                Bulk = (double)streamer.Stats["hp"] * (streamer.Stats["def"] + (double)streamer.Stats["sdef"]) / 2
            }).ToList();
            if (all.Count < 6) throw CannotComplete();
            double minSpeed = all.Min(c => c.Speed), speedRange = all.Max(c => c.Speed) - minSpeed;
            double minBulk = all.Min(c => c.Bulk), bulkRange = all.Max(c => c.Bulk) - minBulk;
            foreach (var candidate in all)
            {
                candidate.Speed = speedRange == 0 ? 0 : (candidate.Speed - minSpeed) / speedRange;
                candidate.Bulk = bulkRange == 0 ? 0 : (candidate.Bulk - minBulk) / bulkRange;
            }

            var picked = ids.Select(id => all.First(c => c.Def.Id == id)).ToList();
            foreach (var candidate in picked) CountPools(candidate, used, 1);
            var remaining = all.Where(c => !ids.Contains(c.Def.Id)
                && (season == null || !season.Banned.Contains(c.Def.Id))).ToList();
            var tracks = new HashSet<string>(picked.Select(c => c.Def.Type), StringComparer.Ordinal);
            int targetTracks = Math.Min(tracks.Count + 6 - picked.Count,
                picked.Concat(remaining).Select(c => c.Def.Type).Distinct().Count());
            // 覆盖优先于评分；固定前缀和赛季限制使五赛道不可行时，取真正可达的上限。
            while (targetTracks >= 0 && !CanComplete(remaining, 0, 6 - picked.Count, tracks, targetTracks, used, limits))
                targetTracks--;
            if (targetTracks < 0) throw CannotComplete();

            var rng = new Rng(seed);
            while (picked.Count < 6)
            {
                var ranked = remaining.Where(c => CanAdd(c, used, limits))
                    .Select(c => new Ranked { Candidate = c, Score = Score(picked.Concat(new[] { c }).ToList()) })
                    .OrderByDescending(c => c.Score).ThenBy(c => c.Candidate.Def.Id, StringComparer.Ordinal);
                var best = new List<Candidate>();
                double bestScore = double.NegativeInfinity;
                foreach (var option in ranked)
                {
                    if (option.Score < bestScore - 0.08) break;
                    var candidate = option.Candidate;
                    CountPools(candidate, used, 1);
                    bool addedTrack = tracks.Add(candidate.Def.Type);
                    bool feasible = CanComplete(remaining.Where(c => c != candidate).ToList(), 0,
                        5 - picked.Count, tracks, targetTracks, used, limits);
                    if (addedTrack) tracks.Remove(candidate.Def.Type);
                    CountPools(candidate, used, -1);
                    if (!feasible) continue;
                    if (best.Count == 0) bestScore = option.Score;
                    best.Add(candidate);
                }

                if (best.Count == 0) throw CannotComplete();
                var next = best[(int)(rng.Next() * best.Count)];
                picked.Add(next);
                remaining.Remove(next);
                tracks.Add(next.Def.Type);
                CountPools(next, used, 1);
            }

            var team = picked.Select(c => c.Def.Id).ToList();
            errors = TeamCheck.Validate(db, seasonId, team);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
            return team;
        }

        static InvalidOperationException CannotComplete()
        {
            return new InvalidOperationException("当前内容包、赛季规则和固定选择无法组成合法的 6 人队伍");
        }

        static bool CanAdd(Candidate candidate, int[] used, int[] limits)
        {
            return candidate.Pools.All(pool => used[pool] < limits[pool]);
        }

        static void CountPools(Candidate candidate, int[] used, int change)
        {
            foreach (int pool in candidate.Pools) used[pool] += change;
        }

        static bool CanComplete(List<Candidate> remaining, int start, int slots, HashSet<string> tracks,
            int targetTracks, int[] used, int[] limits)
        {
            if (slots == 0) return tracks.Count >= targetTracks;
            if (remaining.Count - start < slots || tracks.Count + slots < targetTracks) return false;
            int available = 0;
            var poolAvailable = new int[limits.Length];
            var possibleTracks = new HashSet<string>(tracks, StringComparer.Ordinal);
            for (int i = start; i < remaining.Count; i++)
            {
                if (!CanAdd(remaining[i], used, limits)) continue;
                available++;
                CountPools(remaining[i], poolAvailable, 1);
                possibleTracks.Add(remaining[i].Def.Type);
            }
            if (available < slots || possibleTracks.Count < targetTracks) return false;
            // 即使池外全选，单个池的剩余额也可能不足；这是重叠池下仍安全的乐观上界。
            for (int pool = 0; pool < limits.Length; pool++)
                if (available - poolAvailable[pool] + Math.Min(poolAvailable[pool], limits[pool] - used[pool]) < slots)
                    return false;

            // 重叠限制池会让贪心选中的主播堵死余下名额，回溯确认有完整阵容再接受。
            for (int i = start; i <= remaining.Count - slots; i++)
            {
                var candidate = remaining[i];
                if (!CanAdd(candidate, used, limits)) continue;
                CountPools(candidate, used, 1);
                bool addedTrack = tracks.Add(candidate.Def.Type);
                bool complete = CanComplete(remaining, i + 1, slots - 1, tracks, targetTracks, used, limits);
                if (addedTrack) tracks.Remove(candidate.Def.Type);
                CountPools(candidate, used, -1);
                if (complete) return true;
            }
            return false;
        }

        static double Matchup(ContentDb db, StreamerDef candidate, StreamerDef foe)
        {
            // 与电脑首发的满血对位估值一致，包含第五技能和双赛道；不创建对战或触发人设。
            return AttackCoverage(db, candidate, foe) - AttackCoverage(db, foe, candidate) + 1
                - (candidate.Stats["spd"] < foe.Stats["spd"] ? 0.2 : 0);
        }

        static double AttackCoverage(ContentDb db, StreamerDef attacker, StreamerDef defender)
        {
            return attacker.Skills.Concat(new[] { attacker.Fifth }).Select(id => db.Skills[id])
                .Where(skill => skill.Power > 0)
                .Select(skill => db.TypeMul(skill.Type, defender.Type, defender.Type2)
                    * (skill.Type == attacker.Type || skill.Type == attacker.Type2 ? 1.5 : 1))
                .DefaultIfEmpty(0).Max();
        }

        static double Score(List<Candidate> team)
        {
            double average = 0, coverage = 0;
            for (int foe = 0; foe < team[0].Matchups.Length; foe++)
            {
                average += team.Average(c => c.Matchups[foe]);
                coverage += team.Max(c => c.Matchups[foe]);
            }
            // 全队平均对位为主，最佳应对者体现互补；归一化范围鼓励快慢与耐久层次。
            return (0.75 * average + 0.25 * coverage) / team[0].Matchups.Length
                + 0.2 * (team.Max(c => c.Speed) - team.Min(c => c.Speed))
                + 0.2 * (team.Max(c => c.Bulk) - team.Min(c => c.Bulk));
        }
    }
}
