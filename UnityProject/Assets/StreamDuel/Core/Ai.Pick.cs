using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public static partial class Ai
    {
        public static List<int> ChoosePick(Battle b, int side, AiLevel level)
        {
            if (b == null) throw new ArgumentNullException(nameof(b), "选人需要对战。");
            if (side < 0 || side > 1) throw new ArgumentOutOfRangeException(nameof(side), "对战方必须是 0 或 1。");
            if (b.Phase != Phase.Pick) throw new InvalidOperationException("现在不是选人阶段。");
            if (b.HasChosenPick(side)) throw new InvalidOperationException("这一方已经确认出战名单。");
            if (!Enum.IsDefined(typeof(AiLevel), level)) throw new ArgumentOutOfRangeException(nameof(level), "未知的电脑难度。");
            var available = b.AvailablePicks(side).OrderBy(index => index).ToList();
            if (level == AiLevel.Easy)
            {
                var chosen = new List<int>();
                while (chosen.Count < b.PickCount)
                {
                    int index = (int)(b.Random() * available.Count);
                    chosen.Add(available[index]);
                    available.RemoveAt(index);
                }
                chosen.Sort();
                return chosen;
            }

            // 对位表要求原始六人；被禁成员只从公开候选集合排除，不读取任何秘密选序。
            var foes = b.AvailablePicks(1 - side).ToArray();
            var outgoing = DamageCalc.Matchup(b.Db, b.Record.Teams[side], b.Record.Teams[1 - side]);
            var incoming = DamageCalc.Matchup(b.Db, b.Record.Teams[1 - side], b.Record.Teams[side]);
            var net = new long[6, 6];
            var totals = new long[6];
            foreach (int own in available)
                foreach (int foe in foes)
                {
                    net[own, foe] = (long)outgoing[own, foe] - incoming[foe, own];
                    totals[own] += net[own, foe];
                }

            List<int> best = null;
            long bestCoverage = long.MinValue, bestTotal = long.MinValue;
            foreach (var combination in PickCombinations(available, b.PickCount, 0, new List<int>()))
            {
                long coverage = 0, total = 0;
                foreach (int foe in foes) coverage += combination.Max(own => net[own, foe]);
                foreach (int own in combination) total += totals[own];
                if (best == null || coverage > bestCoverage || coverage == bestCoverage && total > bestTotal)
                {
                    best = combination;
                    bestCoverage = coverage;
                    bestTotal = total;
                }
            }
            // 先保证组合能应对公开阵容，再用总体对位决定首发；完全同分保留原始号位顺序。
            return best.OrderByDescending(index => totals[index]).ThenBy(index => index).ToList();
        }

        static IEnumerable<List<int>> PickCombinations(List<int> available, int count, int start, List<int> selected)
        {
            if (selected.Count == count)
            {
                yield return new List<int>(selected);
                yield break;
            }
            for (int i = start; i <= available.Count - (count - selected.Count); i++)
            {
                selected.Add(available[i]);
                foreach (var combination in PickCombinations(available, count, i + 1, selected)) yield return combination;
                selected.RemoveAt(selected.Count - 1);
            }
        }
    }
}
