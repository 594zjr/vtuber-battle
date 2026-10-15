using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Sim
{
    internal static class Tuning
    {
        private sealed class Result
        {
            public int Wins;
            public int Draws;
            public int Games;
            public double WinRate { get { return (double)Wins / Games; } }
        }

        public static void Run(ContentDb db, int rounds, int games, uint seed)
        {
            var roster = db.Streamers.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var best = AiWeights.Default;
            // 扰动与抽阵容各用独立的 Battle 随机源，避免对局长度改变后续候选。
            var random = RandomSource(db, roster, seed ^ 0xA511E9B3u);
            for (int round = 0; round < rounds; round++)
            {
                var candidate = best.Copy();
                string changes = Perturb(candidate, random);
                uint roundSeed = unchecked(seed + (uint)round * (uint)games);
                var result = Evaluate(db, roster, candidate, best, games, roundSeed);
                bool accepted = result.WinRate > 0.55;
                if (accepted) best = candidate;
                Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "第 {0}/{1} 轮：{2}；候选胜 {3}/{4}（{5:P2}），平 {6}；{7}",
                    round + 1, rounds, changes, result.Wins, games, result.WinRate, result.Draws,
                    accepted ? "采用候选" : "保留当前最好"));
            }

            // 最终比较不用选拔候选的种子，避免把训练成绩当作验证成绩。
            uint validationSeed = unchecked(seed + (uint)rounds * (uint)games);
            var validation = Evaluate(db, roster, best, AiWeights.Default, games, validationSeed);
            Console.WriteLine(best.ToJson());
            Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "最终权重对 Default：胜 {0}/{1}（{2:P2}），负 {3}，平 {4}；验证起始种子 {5}。未自动采用。",
                validation.Wins, games, validation.WinRate, games - validation.Wins - validation.Draws,
                validation.Draws, validationSeed));
        }

        private static string Perturb(AiWeights weights, Battle random)
        {
            var names = weights.Names().OrderBy(name => name, StringComparer.Ordinal).ToArray();
            int count = 1 + (int)(random.Random() * Math.Min(3, names.Length));
            var changes = new List<string>();
            for (int i = 0; i < count; i++)
            {
                int selected = i + (int)(random.Random() * (names.Length - i));
                string name = names[selected];
                names[selected] = names[i];
                names[i] = name;
                double factor = 0.7 + random.Random() * 0.6;
                weights.Set(name, weights.Get(name) * factor);
                changes.Add(name + "×" + factor.ToString("F6", CultureInfo.InvariantCulture));
            }
            return string.Join("，", changes);
        }

        private static Result Evaluate(ContentDb db, string[] roster, AiWeights candidate,
            AiWeights opponent, int games, uint seed)
        {
            var random = RandomSource(db, roster, seed ^ 0x63D83595u);
            var result = new Result { Games = games };
            IList<string>[] teams = null;
            for (int game = 0; game < games; game++)
            {
                // 成对重复阵容与种子，并交换权重所在方，消除固定侧和阵容偏差。
                if (game % 2 == 0) teams = DrawTeams(roster, random);
                int candidateSide = game % 2;
                var battle = new Battle(db, teams, new[] { "甲方", "乙方" }, unchecked(seed + (uint)(game / 2)));
                while (battle.Phase != Phase.Over)
                {
                    if (battle.Phase == Phase.Replace)
                    {
                        for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                            if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
                        continue;
                    }
                    var a0 = Ai.Choose(battle, 0, AiLevel.Normal, candidateSide == 0 ? candidate : opponent);
                    var a1 = Ai.Choose(battle, 1, AiLevel.Normal, candidateSide == 1 ? candidate : opponent);
                    battle.ResolveTurn(a0, a1);
                }
                if (battle.Winner == candidateSide) result.Wins++;
                else if (battle.Winner == -1) result.Draws++;
            }
            return result;
        }

        private static Battle RandomSource(ContentDb db, string[] roster, uint seed)
        {
            var team = roster.Take(6).ToArray();
            return new Battle(db, new IList<string>[] { team, team }, new[] { "抽样甲", "抽样乙" },
                new BattleOptions { Seed = seed, LeadPick = true });
        }

        private static IList<string>[] DrawTeams(string[] roster, Battle random)
        {
            if (roster.Length >= 12)
            {
                var selected = Sample(roster, 12, random);
                return new IList<string>[] { selected.Take(6).ToArray(), selected.Skip(6).ToArray() };
            }
            return new IList<string>[] { Sample(roster, 6, random), Sample(roster, 6, random) };
        }

        private static string[] Sample(string[] roster, int count, Battle random)
        {
            var shuffled = (string[])roster.Clone();
            for (int i = 0; i < count; i++)
            {
                int j = i + (int)(random.Random() * (shuffled.Length - i));
                string swap = shuffled[i];
                shuffled[i] = shuffled[j];
                shuffled[j] = swap;
            }
            return shuffled.Take(count).ToArray();
        }
    }
}
