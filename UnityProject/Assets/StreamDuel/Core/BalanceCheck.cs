using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    /// <summary>统计随机六人阵容的入队胜率；平局计半胜，后排也计入。</summary>
    public static class BalanceCheck
    {
        /// <summary>持续抽队，直到每位至少入队 gamesPerStreamer 次；镜像双方分别计数。</summary>
        public static Dictionary<string, double> WinRates(ContentDb db, int gamesPerStreamer, uint seed, AiLevel level)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (gamesPerStreamer <= 0)
                throw new ArgumentOutOfRangeException(nameof(gamesPerStreamer), "每位主播的最低入队次数必须大于 0。");
            if (!Enum.IsDefined(typeof(AiLevel), level))
                throw new ArgumentOutOfRangeException(nameof(level), "未知电脑难度。");

            var ids = db.Streamers.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (ids.Length < 6 || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                throw new ArgumentException("内容包至少需要六位 id 非空且不重复的主播。", nameof(db));

            var games = ids.ToDictionary(id => id, id => 0L, StringComparer.Ordinal);
            var points = ids.ToDictionary(id => id, id => 0.0, StringComparer.Ordinal);
            // 抽队和每局种子共用独立的 Rng，避免对局长短改变后续抽样。
            var random = new Rng(seed);
            while (games.Values.Any(count => count < gamesPerStreamer))
            {
                IList<string>[] teams;
                if (ids.Length >= 12)
                {
                    var selected = Sample(ids, 12, random);
                    teams = new IList<string>[] { selected.Take(6).ToArray(), selected.Skip(6).ToArray() };
                }
                else
                {
                    teams = new IList<string>[] { Sample(ids, 6, random), Sample(ids, 6, random) };
                }

                uint battleSeed = (uint)(random.Next() * 4294967296.0);
                var battle = new Battle(db, teams, new[] { "甲方", "乙方" }, battleSeed);
                while (battle.Phase != Phase.Over)
                {
                    if (battle.Phase == Phase.Replace)
                    {
                        for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                        {
                            if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, level));
                        }
                    }
                    else
                    {
                        battle.ResolveTurn(Ai.Choose(battle, 0, level), Ai.Choose(battle, 1, level));
                    }
                }

                for (int side = 0; side < 2; side++)
                {
                    double score = battle.Winner == -1 ? 0.5 : battle.Winner == side ? 1.0 : 0.0;
                    foreach (string id in teams[side])
                    {
                        games[id]++;
                        points[id] += score;
                    }
                }
            }

            return ids.ToDictionary(id => id, id => points[id] / games[id], StringComparer.Ordinal);
        }

        static string[] Sample(string[] ids, int count, Rng random)
        {
            var shuffled = (string[])ids.Clone();
            for (int i = 0; i < count; i++)
            {
                int other = i + (int)(random.Next() * (shuffled.Length - i));
                string swap = shuffled[i];
                shuffled[i] = shuffled[other];
                shuffled[other] = swap;
            }
            return shuffled.Take(count).ToArray();
        }
    }
}
