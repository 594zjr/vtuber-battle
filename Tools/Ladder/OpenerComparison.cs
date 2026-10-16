using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using StreamDuel.Core;

namespace StreamDuel.Ladder
{
    internal static class OpenerComparison
    {
        sealed class Counts
        {
            public int Wins, Draws, Losses;
            public int Games => Wins + Draws + Losses;
            public void Add(int winner, int newSide)
            {
                if (winner < 0) Draws++;
                else if (winner == newSide) Wins++;
                else Losses++;
            }
        }

        public static void Run(ContentDb db, Options options)
        {
            int blocks = options.ComparisonBlocks;
            var rosters = TeamGenerator.Generate(db, options.Season, blocks * 2, options.Seed);
            var sides = new[] { new Counts(), new Counts() };
            var total = new Counts();
            Directory.CreateDirectory(options.OutputDirectory);
            using var games = Writer(options.OutputDirectory, "opener-games.csv");
            using var records = Writer(options.OutputDirectory, "opener-records.jsonl");
            games.WriteLine("block,game,battle_seed,new_side,team0,team1,side0_slot1,side0_slot2,side0_slot3,side0_slot4,side0_slot5,side0_slot6,side1_slot1,side1_slot2,side1_slot3,side1_slot4,side1_slot5,side1_slot6,ban0,ban1,lead0,lead1,winner,new_result,turns,state_hash");
            try
            {
                for (int block = 0; block < blocks; block++)
                {
                    uint seed = unchecked(options.Seed + (uint)block);
                    var a = rosters[block];
                    var b = rosters[blocks + block];
                    // 两种策略都使用 A/B 阵容并处于两侧，避免把阵容来源或调用顺序当成策略优势。
                    for (int game = 0; game < 4; game++)
                    {
                        bool reversed = game == 1 || game == 3;
                        int newSide = game == 0 || game == 3 ? 0 : 1;
                        var teams = new IList<string>[] { reversed ? b : a, reversed ? a : b };
                        var battle = Play(db, teams, seed, newSide, out var bans, out var leads);
                        string record = battle.Record.ToJson();
                        VerifyReplay(db, battle, record);
                        string result = battle.Winner < 0 ? "draw" : battle.Winner == newSide ? "win" : "loss";
                        var cells = new List<string>
                        {
                            Number(block + 1), Number(game + 1), Number(seed), Number(newSide),
                            reversed ? "B" : "A", reversed ? "A" : "B"
                        };
                        cells.AddRange(teams[0]);
                        cells.AddRange(teams[1]);
                        cells.AddRange(new[] { bans[0], bans[1], leads[0], leads[1], Number(battle.Winner), result,
                            Number(battle.Record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn)), battle.StateHash().ToString("x16", CultureInfo.InvariantCulture) });
                        games.WriteLine(string.Join(",", cells.Select(Csv)));
                        records.WriteLine(record);
                        sides[newSide].Add(battle.Winner, newSide);
                        total.Add(battle.Winner, newSide);
                    }
                }
                games.Flush();
                records.Flush();
                WriteSummary(db, options, sides, total, null);
            }
            catch (Exception error)
            {
                WriteSummary(db, options, sides, total, error.Message);
                throw;
            }
            Console.WriteLine("开局策略配对比较：" + db.PackName + "；种子 " + options.Seed + "；配对组 " + blocks
                + "；对局 " + total.Games + "；赛季 " + (options.Season ?? "无"));
            Console.WriteLine("新策略禁用/首发 Hard，旧策略 Normal；双方行动与补位固定 Hard，禁用与首发均开启。每组同种子四局，双方/阵容/策略完整互换。");
            Print("新策略处于 0 方", sides[0]);
            Print("新策略处于 1 方", sides[1]);
            Print("合计", total);
            Console.WriteLine("严格胜率 ≥ 50%：" + (MeetsTarget(total) ? "达到" : "未达到，保留真实结果"));
            Console.WriteLine("全部终局已用 JSON 回放复核；输出 opener-games.csv、opener-records.jsonl、opener-summary.json。");
        }

        static Battle Play(ContentDb db, IList<string>[] teams, uint seed, int newSide, out string[] bans, out string[] leads)
        {
            var levels = new[] { newSide == 0 ? AiLevel.Hard : AiLevel.Normal, newSide == 1 ? AiLevel.Hard : AiLevel.Normal };
            var names = new[] { newSide == 0 ? "新开局策略" : "旧开局策略", newSide == 1 ? "新开局策略" : "旧开局策略" };
            var battle = new Battle(db, teams, names, new BattleOptions { Seed = seed, BanOne = true, LeadPick = true });
            var banIndices = new[] { Ai.ChooseBan(battle, 0, levels[0]), Ai.ChooseBan(battle, 1, levels[1]) };
            bans = new[] { battle.Sides[1].Team[banIndices[0]].Def.Id, battle.Sides[0].Team[banIndices[1]].Def.Id };
            battle.ChooseBan(0, banIndices[0]);
            battle.ChooseBan(1, banIndices[1]);
            if (battle.Phase != Phase.Lead) throw new InvalidOperationException("禁用后未进入首发阶段。");
            // 同时收集两边的决定，再提交；第二方也不能看到第一方已确认的隐藏首发。
            var leadIndices = new[] { Ai.ChooseLead(battle, 0, levels[0]), Ai.ChooseLead(battle, 1, levels[1]) };
            leads = new[] { battle.Sides[0].Team[leadIndices[0]].Def.Id, battle.Sides[1].Team[leadIndices[1]].Def.Id };
            battle.ChooseLead(0, leadIndices[0]);
            battle.ChooseLead(1, leadIndices[1]);
            int operations = 0, turns = 0;
            while (battle.Phase != Phase.Over)
            {
                if (battle.Turn > Battle.TurnCap || operations++ >= Battle.TurnCap * 4 + 32)
                    throw new InvalidOperationException("unfinished：对局超过安全上限但 Core 未终局，不计入平局。seed=" + seed + "，new_side=" + newSide);
                if (battle.Phase == Phase.Replace)
                {
                    bool replaced = false;
                    for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    {
                        if (!battle.Need[side]) continue;
                        battle.Replace(side, Ai.ChooseReplacement(battle, side, AiLevel.Hard));
                        replaced = true;
                    }
                    if (!replaced) throw new InvalidOperationException("unfinished：补位阶段没有可推进的补位需求。");
                }
                else if (battle.Phase == Phase.Choose)
                {
                    if (++turns > Battle.TurnCap) throw new InvalidOperationException("unfinished：超出 Core 回合上限，不计入平局。");
                    var first = Ai.Choose(battle, 0, AiLevel.Hard);
                    var second = Ai.Choose(battle, 1, AiLevel.Hard);
                    battle.ResolveTurn(first, second);
                }
                else throw new InvalidOperationException("unfinished：出现无法推进的对战阶段 " + battle.Phase + "。");
            }
            if (battle.Winner < -1 || battle.Winner > 1 || battle.Turn > Battle.TurnCap)
                throw new InvalidOperationException("unfinished：终局结果或回合数无效。");
            return battle;
        }

        static void VerifyReplay(ContentDb db, Battle battle, string json)
        {
            var replay = Replay.Run(db, BattleRecord.FromJson(json));
            if (replay.Phase != Phase.Over || replay.Winner != battle.Winner || replay.Turn != battle.Turn
                || replay.StateHash() != battle.StateHash() || replay.Log.Count != battle.Log.Count)
                throw new InvalidOperationException("比较对局的 JSON 回放不能复现终局。");
            for (int index = 0; index < battle.Log.Count; index++)
            {
                var expected = battle.Log[index];
                var actual = replay.Log[index];
                if (expected.Turn != actual.Turn || expected.Node != actual.Node || expected.Text != actual.Text)
                    throw new InvalidOperationException("比较对局的 JSON 回放日志不同，下标 " + index + "。");
            }
        }

        static void WriteSummary(ContentDb db, Options options, Counts[] sides, Counts total, string failure)
        {
            object Summary(string side, Counts counts) => new
            {
                new_side = side, games = counts.Games, wins = counts.Wins, draws = counts.Draws, losses = counts.Losses,
                win_rate = counts.Games == 0 ? 0 : (double)counts.Wins / counts.Games,
                score_rate = counts.Games == 0 ? 0 : (counts.Wins + 0.5 * counts.Draws) / counts.Games,
                meets_50_percent = counts.Games > 0 && MeetsTarget(counts)
            };
            var report = new
            {
                mode = "compare-openers", status = failure == null ? "completed" : "failed", failure,
                pack_id = db.PackId, content_hash = db.ContentHash(), seed = options.Seed, season = options.Season,
                blocks = options.ComparisonBlocks, expected_games = options.ComparisonBlocks * 4,
                completed_games = total.Games, not_completed_games = options.ComparisonBlocks * 4 - total.Games,
                new_opening_level = "hard", old_opening_level = "normal", action_level = "hard", replacement_level = "hard",
                ban_one = true, lead_pick = true, battle_turn_cap = Battle.TurnCap,
                roster_generation = "TeamGenerator.Generate(db, season, 2 * blocks, seed); A[i]=rosters[i], B[i]=rosters[blocks+i]",
                battle_seeds = "unchecked(seed + block), block starts at 0; all four games in a block share the seed",
                orientations = new[] { "A:new / B:old", "B:old / A:new", "A:old / B:new", "B:new / A:old" },
                random_policy = "Only existing TeamGenerator and Battle.Rng; no resets; side 0 chooses before side 1",
                replay_verified_games = total.Games,
                turns_definition = "Number of recorded Turn events; replacement-only phases do not add a completed turn",
                win_rate_definition = "wins / games; draws stay in denominator; score_rate=(wins+0.5*draws)/games",
                results = new[] { Summary("0", sides[0]), Summary("1", sides[1]), Summary("all", total) }
            };
            File.WriteAllText(Path.Combine(options.OutputDirectory, "opener-summary.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        }

        static bool MeetsTarget(Counts counts) => (long)counts.Wins * 2 >= counts.Games;
        static void Print(string label, Counts counts)
        {
            Console.WriteLine(label + "：" + counts.Games + " 局，胜 " + counts.Wins + " / 平 " + counts.Draws + " / 负 " + counts.Losses
                + "；胜率 " + ((double)counts.Wins / counts.Games * 100).ToString("F2", CultureInfo.InvariantCulture)
                + "%；含平局半分得分率 " + ((counts.Wins + 0.5 * counts.Draws) / counts.Games * 100).ToString("F2", CultureInfo.InvariantCulture) + "%");
        }
        static StreamWriter Writer(string directory, string file)
        {
            return new StreamWriter(Path.Combine(directory, file), false, new UTF8Encoding(false)) { NewLine = "\n" };
        }
        static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        static string Csv(string value) => value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
            ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
