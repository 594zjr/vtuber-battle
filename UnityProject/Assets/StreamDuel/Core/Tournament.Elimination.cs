using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class TournamentOptions
    {
        public uint? Seed;
        public IList<int> SeedOrder;
        public bool ResetFinal = true;
        public bool AutoTiebreak;
    }

    public sealed partial class Tournament
    {
        public const string SingleElimination = "single";
        public const string DoubleElimination = "double";

        public bool IsElimination { get { return Format == SingleElimination || Format == DoubleElimination; } }
        public uint? Seed { get; private set; }
        public IReadOnlyList<int> SeedOrder { get; private set; }
        public bool ResetFinal { get; private set; } = true;
        public bool AutoTiebreak { get; private set; }
        public int BestOf { get { return 1; } }
        public int? Champion
        {
            get
            {
                if (!IsElimination || Round == 0) return null;
                return EliminationChampion(RebuildElimination());
            }
        }
        public string FormatName
        {
            get
            {
                switch (Format)
                {
                    case "roundrobin": return "循环赛";
                    case "swiss": return "瑞士轮";
                    case SingleElimination: return "单败淘汰赛";
                    case DoubleElimination: return "双败淘汰赛";
                    default: throw new InvalidOperationException("无法识别赛事赛制。");
                }
            }
        }

        sealed class EliminationChoice
        {
            public int Player0 { get; }
            public int Player1 { get; }
            public string Bracket { get; }
            public int BracketRound { get; }

            public EliminationChoice(int first, int second, string bracket, int bracketRound)
            {
                Player0 = first < 0 ? second : first;
                Player1 = first < 0 ? first : second;
                Bracket = bracket;
                BracketRound = bracketRound;
            }
        }

        sealed class EliminationInput
        {
            readonly int player;
            readonly EliminationNode source;
            readonly bool loser;

            public EliminationInput(int player) { this.player = player; }
            public EliminationInput(EliminationNode source, bool loser = false)
            {
                this.source = source;
                this.loser = loser;
            }

            public bool TryRead(out int value)
            {
                value = -1;
                if (source == null) { value = player; return true; }
                if (!source.Resolved) return false;
                if (source.Empty) return true;
                var result = source.Result;
                // 轮空只能产生晋级者；不能把空位变成败者组的一次失败。
                if (loser && result.IsBye) return true;
                int side = result.WinnerSide.Value;
                if (side != 0 && side != 1) throw new InvalidOperationException("淘汰赛不能用平局推进对阵。");
                if (loser) side = 1 - side;
                value = side == 0 ? result.Player0 : result.Player1;
                return true;
            }
        }

        sealed class EliminationNode
        {
            public readonly EliminationInput First, Second;
            public readonly string Bracket;
            public readonly int BracketRound;
            public EliminationNode ResetAfter;
            public Pairing Result;
            public bool Empty;
            public bool Resolved { get { return Empty || Result != null && Result.WinnerSide.HasValue; } }

            public EliminationNode(EliminationInput first, EliminationInput second, string bracket, int round)
            {
                First = first;
                Second = second;
                Bracket = bracket;
                BracketRound = round;
            }

            public bool TryChoice(out EliminationChoice choice)
            {
                choice = null;
                if (Result != null || Empty) return false;
                // 重置赛依赖已完成的第一场总决赛，不能只因两名决赛选手已知而提前排出。
                if (ResetAfter != null && (!ResetAfter.Resolved || ResetAfter.Result == null
                    || ResetAfter.Result.WinnerSide != 1)) return false;
                if (!First.TryRead(out int first) || !Second.TryRead(out int second)) return false;
                if (first < 0 && second < 0) { Empty = true; return false; }
                choice = new EliminationChoice(first, second, Bracket, BracketRound);
                return true;
            }
        }

        sealed class EliminationGraph
        {
            public readonly List<EliminationNode> Nodes = new List<EliminationNode>();
            public EliminationNode WinnerFinal, GrandFinal, Reset;
        }

        void InitializeElimination(TournamentOptions options)
        {
            if (!IsElimination)
            {
                if (options != null) throw new ArgumentException("淘汰赛配置只能用于单败或双败淘汰赛。", nameof(options));
                return;
            }
            if (options == null) return;
            if (options.Seed.HasValue && options.SeedOrder != null)
                throw new ArgumentException("赛事种子和指定种子顺序不能同时使用。", nameof(options));
            Seed = options.Seed;
            ResetFinal = options.ResetFinal;
            AutoTiebreak = options.AutoTiebreak;
            if (options.SeedOrder != null)
            {
                var copy = options.SeedOrder.ToArray();
                if (copy.Length < 2 || copy.Any(id => id < 0 || id >= copy.Length)
                    || copy.Distinct().Count() != copy.Length)
                    throw new ArgumentException("种子顺序必须是至少两名选手的完整报名编号排列。", nameof(options));
                SeedOrder = Array.AsReadOnly(copy);
            }
        }

        int[] EffectiveEliminationSeeds(bool requireComplete = true)
        {
            if (SeedOrder != null)
            {
                if (requireComplete && SeedOrder.Count != players.Count)
                    throw new InvalidOperationException("种子顺序必须包含全部已报名选手，且不能包含未报名编号。");
                // 报名尚未封盘时仍列出所有已报名者；人数不符只在首次正式排轮时拒绝。
                return SeedOrder.Where(id => id < players.Count)
                    .Concat(Enumerable.Range(SeedOrder.Count, Math.Max(0, players.Count - SeedOrder.Count))).ToArray();
            }
            var order = Enumerable.Range(0, players.Count).ToArray();
            if (Seed.HasValue)
            {
                // 每次由相同配置重建，查询、存档恢复和报分顺序都不会消耗额外随机状态。
                var rng = new Rng(Seed.Value);
                for (int i = order.Length - 1; i > 0; i--)
                {
                    int j = (int)(rng.Next() * (i + 1));
                    int swap = order[i];
                    order[i] = order[j];
                    order[j] = swap;
                }
            }
            return order;
        }

        List<EliminationChoice> EliminationRound()
        {
            var graph = RebuildElimination();
            if (EliminationChampion(graph).HasValue) return new List<EliminationChoice>();
            return ReadyEliminationNodes(graph).Select(node =>
            {
                node.TryChoice(out var choice);
                return choice;
            }).ToList();
        }

        bool EliminationFinished()
        {
            return IsElimination && Round > 0 && EliminationChampion(RebuildElimination()).HasValue;
        }

        int EliminationRounds()
        {
            if (players.Count < 2) return 0;
            if (Round > 0 && EliminationFinished()) return Round;
            int depth = 0;
            for (int remaining = players.Count - 1; remaining > 0; remaining >>= 1) depth++;
            return Format == SingleElimination ? depth : depth * 2 + (ResetFinal ? 1 : 0);
        }

        int[] EliminationRankOrder()
        {
            var seeds = EffectiveEliminationSeeds(Round > 0);
            var rank = new int[players.Count];
            for (int i = 0; i < seeds.Length; i++) rank[seeds[i]] = i;
            if (Round == 0) return seeds;
            int? champion = Champion;
            var losses = new int[players.Count];
            var eliminated = new int[players.Count];
            foreach (var pairing in pairings.Where(item => !item.IsBye && item.WinnerSide.HasValue))
            {
                int loser = pairing.WinnerSide.Value == 0 ? pairing.Player1 : pairing.Player0;
                losses[loser]++;
                if (losses[loser] >= (Format == SingleElimination ? 1 : 2)
                    || Format == DoubleElimination && !ResetFinal && pairing.Bracket == "final")
                    eliminated[loser] = pairing.Round;
            }
            return Enumerable.Range(0, players.Count).OrderBy(id => champion == id ? 0 : 1)
                .ThenBy(id => eliminated[id] == 0 ? 0 : 1).ThenByDescending(id => eliminated[id])
                .ThenBy(id => rank[id]).ToArray();
        }

        EliminationGraph RebuildElimination()
        {
            var graph = CreateEliminationGraph();
            // 唯一事实源仍是已排对阵及其结果，不把隐式排程游标写进赛事存档。
            foreach (var round in roundsPlayed)
            {
                var ready = ReadyEliminationNodes(graph);
                if (ready.Count != round.Count)
                    throw new InvalidOperationException("淘汰赛历史对阵数量与种子排程不一致。");
                for (int i = 0; i < ready.Count; i++)
                {
                    ready[i].TryChoice(out var expected);
                    if (expected.Player0 != round[i].Player0 || expected.Player1 != round[i].Player1)
                        throw new InvalidOperationException("淘汰赛历史对阵与晋级路线不一致。");
                    ready[i].Result = round[i];
                }
            }
            return graph;
        }

        static List<EliminationNode> ReadyEliminationNodes(EliminationGraph graph)
        {
            var ready = new List<EliminationNode>();
            // Nodes 按依赖拓扑排列；双空节点可在本次扫描内向后传播，真实轮空仍占一场历史对阵。
            foreach (var node in graph.Nodes)
                if (node.TryChoice(out var ignored)) ready.Add(node);
            return ready;
        }

        int? EliminationChampion(EliminationGraph graph)
        {
            var final = Format == SingleElimination ? graph.WinnerFinal : graph.GrandFinal;
            if (!final.Resolved || final.Result == null) return null;
            if (Format == DoubleElimination && ResetFinal && final.Result.WinnerSide == 1)
                final = graph.Reset;
            if (final == null || !final.Resolved || final.Result == null) return null;
            return final.Result.WinnerSide == 0 ? final.Result.Player0 : final.Result.Player1;
        }

        EliminationGraph CreateEliminationGraph()
        {
            var seeds = EffectiveEliminationSeeds();
            if (seeds.Length < 2) throw new InvalidOperationException("至少需要两名选手才能排淘汰赛。");
            int size = 2;
            while (size < seeds.Length)
            {
                if (size > int.MaxValue / 2) throw new InvalidOperationException("参赛人数超过淘汰赛排程范围。");
                size *= 2;
            }
            var positions = new List<int> { 1, 2 };
            while (positions.Count < size)
            {
                int sum = positions.Count * 2 + 1;
                positions = positions.SelectMany(seed => new[] { seed, sum - seed }).ToList();
            }
            var graph = new EliminationGraph();
            EliminationNode Add(EliminationInput first, EliminationInput second, string bracket, int round)
            {
                var node = new EliminationNode(first, second, bracket, round);
                graph.Nodes.Add(node);
                return node;
            }
            var winners = new List<List<EliminationNode>>();
            var opening = new List<EliminationNode>();
            for (int i = 0; i < size; i += 2)
            {
                int first = positions[i] <= seeds.Length ? seeds[positions[i] - 1] : -1;
                int second = positions[i + 1] <= seeds.Length ? seeds[positions[i + 1] - 1] : -1;
                opening.Add(Add(new EliminationInput(first), new EliminationInput(second),
                    Format == SingleElimination ? "single" : "winners", 1));
            }
            winners.Add(opening);
            while (winners[winners.Count - 1].Count > 1)
            {
                var previous = winners[winners.Count - 1];
                var next = new List<EliminationNode>();
                for (int i = 0; i < previous.Count; i += 2)
                    next.Add(Add(new EliminationInput(previous[i]), new EliminationInput(previous[i + 1]),
                        Format == SingleElimination ? "single" : "winners", winners.Count + 1));
                winners.Add(next);
            }
            graph.WinnerFinal = winners[winners.Count - 1][0];
            if (Format == SingleElimination) return graph;

            EliminationInput loserFinal;
            if (size == 2) loserFinal = new EliminationInput(graph.WinnerFinal, true);
            else
            {
                var lower = new List<EliminationNode>();
                for (int i = 0; i < opening.Count; i += 2)
                    lower.Add(Add(new EliminationInput(opening[i], true), new EliminationInput(opening[i + 1], true), "losers", 1));
                for (int upperRound = 1; upperRound < winners.Count; upperRound++)
                {
                    var upper = winners[upperRound];
                    var next = new List<EliminationNode>();
                    for (int i = 0; i < upper.Count; i++)
                    {
                        // 相邻胜者组分区交换落点，避免刚从同一分区落下就立即重赛。
                        int crossed = upper.Count > 1 ? i ^ 1 : i;
                        next.Add(Add(new EliminationInput(lower[i]), new EliminationInput(upper[crossed], true),
                            "losers", upperRound * 2));
                    }
                    lower = next;
                    if (upperRound + 1 < winners.Count)
                    {
                        next = new List<EliminationNode>();
                        for (int i = 0; i < lower.Count; i += 2)
                            next.Add(Add(new EliminationInput(lower[i]), new EliminationInput(lower[i + 1]),
                                "losers", upperRound * 2 + 1));
                        lower = next;
                    }
                }
                loserFinal = new EliminationInput(lower[0]);
            }
            var winnerFinal = new EliminationInput(graph.WinnerFinal);
            graph.GrandFinal = Add(winnerFinal, loserFinal, "final", 1);
            if (ResetFinal)
            {
                graph.Reset = Add(winnerFinal, loserFinal, "reset", 1);
                graph.Reset.ResetAfter = graph.GrandFinal;
            }
            return graph;
        }
    }
}
