using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public static partial class Ai
    {
        public static int ChooseBan(Battle b, int s, AiLevel level)
        {
            if (level == AiLevel.Normal) return ChooseBan(b, s);
            ValidateOpening(b, s, level, Phase.Ban);
            if (b.HasChosenBan(s)) throw new InvalidOperationException("这一方已经选择禁用");
            var candidates = OpeningCandidates(b, 1 - s);
            if (level == AiLevel.Easy) return RandomOpening(b, candidates);

            var allies = OpeningCandidates(b, s);
            var mine = OpeningIds(b, s);
            var foes = OpeningIds(b, 1 - s);
            var threat = OpeningMatrix(b, foes, mine);
            return BestOpening(b, candidates, i => allies.Sum(j => (double)threat[i, j]));
        }

        public static int ChooseLead(Battle b, int s, AiLevel level)
        {
            if (level == AiLevel.Normal) return ChooseLead(b, s);
            ValidateOpening(b, s, level, Phase.Lead);
            if (b.HasChosenLead(s)) throw new InvalidOperationException("这一方已经选择首发");
            var candidates = OpeningCandidates(b, s);
            if (level == AiLevel.Easy) return RandomOpening(b, candidates);

            var opponents = OpeningCandidates(b, 1 - s);
            // 困难估值沿用公开原阵容；专家重算禁用后的后排支援，不能补入假队友。
            var mine = level == AiLevel.Expert ? OpeningIds(b, s) : b.Record.Teams[s];
            var foes = level == AiLevel.Expert ? OpeningIds(b, 1 - s) : b.Record.Teams[1 - s];
            var attack = OpeningMatrix(b, mine, foes);
            var defense = OpeningMatrix(b, foes, mine);
            var myRows = candidates.ToDictionary(i => i, i => mine.IndexOf(b.Sides[s].Team[i].Def.Id));
            var foeRows = opponents.Select(i => foes.IndexOf(b.Sides[1 - s].Team[i].Def.Id)).ToArray();
            // 所有未被禁用的对手都可能首发；不查看 Active、待确认选择或记录中的私密选择。
            return BestOpening(b, candidates, i => foeRows.Average(j =>
                (double)attack[myRows[i], j] - defense[j, myRows[i]]));
        }

        static void ValidateOpening(Battle b, int s, AiLevel level, Phase phase)
        {
            if (b == null) throw new ArgumentNullException(nameof(b));
            if (s < 0 || s >= b.Sides.Length) throw new ArgumentOutOfRangeException(nameof(s), "对战方必须是 0 或 1");
            if (level < AiLevel.Easy || level > AiLevel.Expert) throw new ArgumentOutOfRangeException(nameof(level), "电脑难度无效");
            if (b.Phase != phase) throw new InvalidOperationException(phase == Phase.Ban
                ? "现在不是选择禁用的阶段" : "现在不是选择首发的阶段");
        }

        static List<int> OpeningCandidates(Battle b, int side)
        {
            var team = b.Sides[side].Team;
            var candidates = Enumerable.Range(0, team.Count).Where(i => !team[i].Fainted).ToList();
            if (candidates.Count == 0) throw new InvalidOperationException("队伍没有可选择的主播");
            return candidates;
        }

        static IList<string> OpeningIds(Battle b, int side)
        {
            return b.Sides[side].Team.Select(f => f.Def.Id).ToArray();
        }

        static int[,] OpeningMatrix(Battle b, IList<string> attackers, IList<string> defenders)
        {
            if (attackers.Count == 6 && defenders.Count == 6 && b.Modifiers.Count == 0)
                return DamageCalc.Matchup(b.Db, attackers, defenders);
            return DamageCalc.MatchupRemaining(b.Db, attackers, defenders, b.Modifiers.ToList());
        }

        static int RandomOpening(Battle b, List<int> candidates)
        {
            return candidates[(int)(b.Random() * candidates.Count)];
        }

        static int BestOpening(Battle b, List<int> candidates, Func<int, double> score)
        {
            var best = new List<int>();
            double bestScore = double.NegativeInfinity;
            foreach (int candidate in candidates)
            {
                double value = score(candidate);
                if (value > bestScore + 1e-9)
                {
                    bestScore = value;
                    best.Clear();
                    best.Add(candidate);
                }
                else if (Math.Abs(value - bestScore) <= 1e-9) best.Add(candidate);
            }
            return best.Count == 1 ? best[0] : RandomOpening(b, best);
        }
    }
}
