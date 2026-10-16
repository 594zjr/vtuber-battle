using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamDuel.Core;

namespace StreamDuel.Ladder
{
    internal static class ExpertComparison
    {
        const string BudgetRole = "budget";
        const string UnlimitedRole = "unlimited";
        const string HardRole = "hard";
        static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };

        internal sealed class Fixture
        {
            public int Id;
            public uint Seed;
            public string[][] Teams;
        }

        internal sealed class NodeSummary
        {
            public long SimulatedNodes;
            public int MaxDecisionNodes;
            public int ChooseDecisions, ReplacementDecisions;
            public int ExhaustedChooseDecisions, ExhaustedReplacementDecisions;
        }

        internal sealed class GameResult
        {
            public int Id, FixtureId, Leg;
            public string Mode;
            public uint Seed;
            public string[] Roles;
            public int Winner, Turns;
            public NodeSummary[] Nodes = { new NodeSummary(), new NodeSummary() };
            public string FinalStateHash, RecordSha256;
        }

        internal sealed class Score
        {
            public int Games, Side0Games, Side1Games, Wins, Draws, Losses;
            public double WinRate, ScoreRate, ScoreChangeFrom50PercentagePoints;
            public NodeSummary Nodes = new NodeSummary();
        }

        internal sealed class PairSummary
        {
            public Score Budget, Unlimited;
        }

        internal sealed class HardSummary
        {
            public Score Budget, Unlimited;
            public double WinRateDropPercentagePoints, ScoreRateDropPercentagePoints;
            public bool WinRateDropAtMostThreePoints;
        }

        internal sealed class GameTiming
        {
            public int GameId;
            public double ElapsedMilliseconds;
        }

        public static int Run(string[] args, Func<string, ContentDb> loadPack)
        {
            var options = ExpertComparisonOptions.Parse(args);
            if (options.Help)
            {
                Console.WriteLine("用法：dotnet run --project Tools/Ladder -- --compare-expert [--compare-mode paired|hard|both] [--games 300] [--budget 128] [--workers 1] [--seed 1] [--out ./ladder_expert_out]");
                Console.WriteLine("固定原创包、随机合法六人队伍，无赛季、禁用、首发选择和修饰器。--games 是配对样本数量，不是总对局数。");
                Console.WriteLine("paired（默认）：每个样本保持队伍及种子，交换预算/无限专家所处位置，默认 600 场；两种专家各在每侧打 300 场。");
                Console.WriteLine("hard：同一样本、种子、专家位置分别让两种专家对困难电脑，默认各 300 场；both 同时执行两组，默认总计 1200 场。");
                Console.WriteLine("--budget 为 0..2147483647 的节点上限；--seed 为 0..4294967295；--games 至少 1。比较模式拒绝天梯专用参数。可选 --pack 只接受 original。");
                Console.WriteLine("--workers 为 1..16，默认 1；按样本并行，结果仍按样本顺序输出，工作线程数只保存至 timings.json。");
                Console.WriteLine("comparison.json 与 games.csv 保存可复现结果；timings.json 单独保存不可复现的运行耗时。完整口径见 Tools/Ladder/README.md。");
                return 0;
            }

            var db = loadPack("original");
            var timer = Stopwatch.StartNew();
            var fixtures = GenerateFixtures(db, options.Games, options.Seed);
            var games = new List<GameResult>();
            var timings = new List<GameTiming>();
            var fixtureGames = new List<GameResult>[fixtures.Count];
            var fixtureTimings = new List<GameTiming>[fixtures.Count];
            bool paired = options.Mode != "hard", hard = options.Mode != "paired";
            int gamesPerFixture = paired && hard ? 4 : 2;
            int plannedGames = options.Games * gamesPerFixture;
            int completedFixtures = 0;
            Console.WriteLine("专家比较：模式 " + options.Mode + "；样本 " + options.Games + "；计划对局 " + plannedGames
                + "；预算 " + options.MaxNodes + " 节点；种子 " + options.Seed + "。");
            try
            {
                Parallel.For(0, fixtures.Count, new ParallelOptions { MaxDegreeOfParallelism = options.Workers }, index =>
                {
                    var fixture = fixtures[index];
                    var budget = new ExpertBudget(options.MaxNodes);
                    var localGames = new List<GameResult>(gamesPerFixture);
                    var localTimings = new List<GameTiming>(gamesPerFixture);
                    if (paired)
                    {
                        Play(db, fixture, "paired", 1, new[] { BudgetRole, UnlimitedRole }, budget, localGames, localTimings);
                        Play(db, fixture, "paired", 2, new[] { UnlimitedRole, BudgetRole }, budget, localGames, localTimings);
                    }
                    if (hard)
                    {
                        int expertSide = (fixture.Id - 1) % 2;
                        var roles = new[] { HardRole, HardRole };
                        roles[expertSide] = BudgetRole;
                        Play(db, fixture, "hard", 1, roles, budget, localGames, localTimings);
                        roles = new[] { HardRole, HardRole };
                        roles[expertSide] = UnlimitedRole;
                        Play(db, fixture, "hard", 2, roles, budget, localGames, localTimings);
                    }
                    fixtureGames[index] = localGames;
                    fixtureTimings[index] = localTimings;
                    int completed = Interlocked.Increment(ref completedFixtures);
                    if (completed % 10 == 0 || completed == fixtures.Count)
                        Console.WriteLine("已完成样本 " + completed + "/" + fixtures.Count + "，对局 " + (completed * gamesPerFixture) + "/" + plannedGames + "。");
                });
            }
            catch (AggregateException ex)
            {
                throw new InvalidOperationException("专家比较失败：" + string.Join("；", ex.Flatten().InnerExceptions.Select(error => error.Message)), ex);
            }
            // 完成先后不能改变结果顺序和编号，方便不同线程数的报告逐字比较。
            for (int index = 0; index < fixtures.Count; index++)
            {
                for (int localIndex = 0; localIndex < fixtureGames[index].Count; localIndex++)
                {
                    var game = fixtureGames[index][localIndex];
                    var timing = fixtureTimings[index][localIndex];
                    game.Id = games.Count + 1;
                    timing.GameId = game.Id;
                    games.Add(game);
                    timings.Add(timing);
                }
            }
            timer.Stop();

            PairSummary pairedSummary = paired ? new PairSummary
            {
                Budget = Tally(games, "paired", BudgetRole), Unlimited = Tally(games, "paired", UnlimitedRole)
            } : null;
            HardSummary hardSummary = null;
            if (hard)
            {
                var budgetScore = Tally(games, "hard", BudgetRole);
                var unlimitedScore = Tally(games, "hard", UnlimitedRole);
                hardSummary = new HardSummary
                {
                    Budget = budgetScore, Unlimited = unlimitedScore,
                    WinRateDropPercentagePoints = 100 * (unlimitedScore.WinRate - budgetScore.WinRate),
                    ScoreRateDropPercentagePoints = 100 * (unlimitedScore.ScoreRate - budgetScore.ScoreRate),
                    WinRateDropAtMostThreePoints = (long)(unlimitedScore.Wins - budgetScore.Wins) * 100 <= (long)options.Games * 3
                };
            }
            var report = new
            {
                SchemaVersion = 1,
                Protocol = "T108-expert-comparison-v1",
                Mode = options.Mode,
                PackId = db.PackId,
                ContentHash = db.ContentHash(),
                EngineAssemblySha256 = FileHash(typeof(Battle).Assembly.Location),
                Seed = options.Seed,
                FixtureCount = options.Games,
                BudgetMaxNodes = options.MaxNodes,
                UnlimitedMaxNodes = ExpertBudget.Unlimited.MaxNodes,
                GameCount = games.Count,
                SelectionOrder = "side0-then-side1",
                FixtureSha256 = Hash(JsonSerializer.Serialize(fixtures, JsonOptions)),
                Paired = pairedSummary,
                CommonHardOpponent = hardSummary,
                Fixtures = fixtures,
                Games = games
            };
            Directory.CreateDirectory(options.OutputDirectory);
            WriteJson(Path.Combine(options.OutputDirectory, "comparison.json"), report);
            File.WriteAllText(Path.Combine(options.OutputDirectory, "games.csv"), Csv(games, fixtures), new UTF8Encoding(false));
            WriteJson(Path.Combine(options.OutputDirectory, "timings.json"), new
            {
                SchemaVersion = 1, Mode = options.Mode, FixtureCount = options.Games, Workers = options.Workers,
                TotalElapsedMilliseconds = timer.Elapsed.TotalMilliseconds, Games = timings
            });
            if (pairedSummary != null)
            {
                PrintScore("直接配对：预算专家", pairedSummary.Budget);
                PrintScore("直接配对：无限专家", pairedSummary.Unlimited);
            }
            if (hardSummary != null)
            {
                PrintScore("共同困难对手：预算专家", hardSummary.Budget);
                PrintScore("共同困难对手：无限专家", hardSummary.Unlimited);
                Console.WriteLine("对困难电脑的胜率下降 " + Number(hardSummary.WinRateDropPercentagePoints)
                    + " 个百分点；得分率下降 " + Number(hardSummary.ScoreRateDropPercentagePoints)
                    + " 个百分点；胜率下降不超过 3 个百分点：" + (hardSummary.WinRateDropAtMostThreePoints ? "是" : "否") + "。");
            }
            Console.WriteLine("运行耗时 " + Number(timer.Elapsed.TotalSeconds) + " 秒；已输出 comparison.json、games.csv、timings.json。");
            return 0;
        }

        static List<Fixture> GenerateFixtures(ContentDb db, int count, uint seed)
        {
            var candidates = db.Streamers.Select(streamer => streamer.Id).Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (candidates.Length < 6) throw new InvalidOperationException("原创包当前可用主播不足六位。");
            // 赛程先生成完毕，AI 的不同计算预算不能改变后续抽样。
            var rng = new Rng(seed);
            var fixtures = new List<Fixture>(count);
            for (int index = 0; index < count; index++)
            {
                var teams = new[] { RandomTeam(candidates, rng), RandomTeam(candidates, rng) };
                foreach (var team in teams)
                {
                    var errors = TeamCheck.Validate(db, null, team);
                    if (errors.Count > 0) throw new InvalidOperationException("比较阵容不合法：" + string.Join("；", errors));
                }
                fixtures.Add(new Fixture { Id = index + 1, Seed = (uint)(rng.Next() * 4294967296.0), Teams = teams });
            }
            return fixtures;
        }

        static string[] RandomTeam(string[] candidates, Rng rng)
        {
            var shuffled = (string[])candidates.Clone();
            for (int i = shuffled.Length - 1; i > 0; i--)
            {
                int j = (int)(rng.Next() * (i + 1));
                string value = shuffled[i]; shuffled[i] = shuffled[j]; shuffled[j] = value;
            }
            return shuffled.Take(6).ToArray();
        }

        static void Play(ContentDb db, Fixture fixture, string mode, int leg, string[] roles,
            ExpertBudget budget, List<GameResult> games, List<GameTiming> timings)
        {
            var result = new GameResult
            {
                Id = games.Count + 1, FixtureId = fixture.Id, Mode = mode, Leg = leg, Seed = fixture.Seed, Roles = roles
            };
            var timer = Stopwatch.StartNew();
            var battle = new Battle(db, new IList<string>[] { fixture.Teams[0], fixture.Teams[1] }, new[] { "甲方", "乙方" }, fixture.Seed);
            int guard = 0;
            while (battle.Phase != Phase.Over)
            {
                if (++guard > 1000) throw new InvalidOperationException("比较对局未在预期操作次数内结束，样本 " + fixture.Id + "。");
                if (battle.Phase == Phase.Choose)
                {
                    var first = Choose(battle, 0, roles[0], false, budget, result.Nodes[0]);
                    var second = Choose(battle, 1, roles[1], false, budget, result.Nodes[1]);
                    battle.ResolveTurn(first, second);
                }
                else if (battle.Phase == Phase.Replace)
                {
                    for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                        if (battle.Need[side])
                            battle.Replace(side, Choose(battle, side, roles[side], true, budget, result.Nodes[side]).Index);
                }
                else throw new InvalidOperationException("专家比较不支持此对战阶段：" + battle.Phase);
            }
            timer.Stop();
            result.Winner = battle.Winner;
            result.Turns = battle.Record.Events.Count(entry => entry.Type == BattleRecordEventType.Turn);
            result.FinalStateHash = battle.StateHash().ToString("x16", CultureInfo.InvariantCulture);
            result.RecordSha256 = Hash(battle.Record.ToJson());
            games.Add(result);
            timings.Add(new GameTiming { GameId = result.Id, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds });
        }

        static BattleAction Choose(Battle battle, int side, string role, bool replacement, ExpertBudget budget, NodeSummary summary)
        {
            if (role == HardRole)
                return replacement ? BattleAction.SwitchTo(Ai.ChooseReplacement(battle, side, AiLevel.Hard))
                    : Ai.Choose(battle, side, AiLevel.Hard);
            var selectedBudget = role == BudgetRole ? budget : ExpertBudget.Unlimited;
            var decision = replacement ? Ai.ChooseExpertReplacement(battle, side, selectedBudget)
                : Ai.ChooseExpert(battle, side, selectedBudget);
            if (decision.SimulatedNodes < 0 || decision.SimulatedNodes > selectedBudget.MaxNodes)
                throw new InvalidOperationException("专家返回的节点数超过声明的预算。");
            if (replacement && decision.Action.Type != ActionType.Switch)
                throw new InvalidOperationException("专家补位没有返回换人行动。");
            summary.SimulatedNodes += decision.SimulatedNodes;
            summary.MaxDecisionNodes = Math.Max(summary.MaxDecisionNodes, decision.SimulatedNodes);
            if (replacement)
            {
                summary.ReplacementDecisions++;
                if (decision.BudgetExhausted) summary.ExhaustedReplacementDecisions++;
            }
            else
            {
                summary.ChooseDecisions++;
                if (decision.BudgetExhausted) summary.ExhaustedChooseDecisions++;
            }
            return decision.Action;
        }

        static Score Tally(IEnumerable<GameResult> games, string mode, string role)
        {
            var score = new Score();
            foreach (var game in games.Where(game => game.Mode == mode))
            {
                int side = Array.IndexOf(game.Roles, role);
                if (side < 0) continue;
                score.Games++;
                if (side == 0) score.Side0Games++; else score.Side1Games++;
                if (game.Winner == -1) score.Draws++;
                else if (game.Winner == side) score.Wins++;
                else score.Losses++;
                var nodes = game.Nodes[side];
                score.Nodes.SimulatedNodes += nodes.SimulatedNodes;
                score.Nodes.MaxDecisionNodes = Math.Max(score.Nodes.MaxDecisionNodes, nodes.MaxDecisionNodes);
                score.Nodes.ChooseDecisions += nodes.ChooseDecisions;
                score.Nodes.ReplacementDecisions += nodes.ReplacementDecisions;
                score.Nodes.ExhaustedChooseDecisions += nodes.ExhaustedChooseDecisions;
                score.Nodes.ExhaustedReplacementDecisions += nodes.ExhaustedReplacementDecisions;
            }
            score.WinRate = (double)score.Wins / score.Games;
            score.ScoreRate = (score.Wins + 0.5 * score.Draws) / score.Games;
            score.ScoreChangeFrom50PercentagePoints = (score.ScoreRate - 0.5) * 100;
            return score;
        }

        static void PrintScore(string title, Score score)
        {
            Console.WriteLine(title + "：" + score.Games + " 场（side0 " + score.Side0Games + "/side1 " + score.Side1Games
                + "），胜 " + score.Wins + "、平 " + score.Draws + "、负 " + score.Losses + "；胜率 " + Number(100 * score.WinRate)
                + "%；得分率 " + Number(100 * score.ScoreRate) + "%；相对 50% " + Number(score.ScoreChangeFrom50PercentagePoints) + " 个百分点。");
            Console.WriteLine("  模拟节点 " + score.Nodes.SimulatedNodes + "，单次最多 " + score.Nodes.MaxDecisionNodes
                + "；行动耗尽 " + score.Nodes.ExhaustedChooseDecisions + "/" + score.Nodes.ChooseDecisions
                + "；补位耗尽 " + score.Nodes.ExhaustedReplacementDecisions + "/" + score.Nodes.ReplacementDecisions + "。");
        }

        static string Csv(List<GameResult> games, List<Fixture> fixtures)
        {
            var output = new StringBuilder("game_id,fixture_id,mode,leg,seed,team0_json,team1_json,role0,role1,winner,turns,side0_nodes,side0_max_decision_nodes,side0_choose_decisions,side0_replacement_decisions,side0_exhausted_choose,side0_exhausted_replacement,side1_nodes,side1_max_decision_nodes,side1_choose_decisions,side1_replacement_decisions,side1_exhausted_choose,side1_exhausted_replacement,final_state_hash,record_sha256\n");
            foreach (var game in games)
            {
                var fixture = fixtures[game.FixtureId - 1];
                var values = new List<string>
                {
                    Number(game.Id), Number(game.FixtureId), game.Mode, Number(game.Leg), Number(game.Seed),
                    JsonSerializer.Serialize(fixture.Teams[0]), JsonSerializer.Serialize(fixture.Teams[1]),
                    game.Roles[0], game.Roles[1], Number(game.Winner), Number(game.Turns)
                };
                foreach (var nodes in game.Nodes)
                {
                    values.AddRange(new[] { Number(nodes.SimulatedNodes), Number(nodes.MaxDecisionNodes), Number(nodes.ChooseDecisions),
                        Number(nodes.ReplacementDecisions), Number(nodes.ExhaustedChooseDecisions), Number(nodes.ExhaustedReplacementDecisions) });
                }
                values.Add(game.FinalStateHash);
                values.Add(game.RecordSha256);
                output.Append(string.Join(",", values.Select(value => value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
                    ? value : "\"" + value.Replace("\"", "\"\"") + "\""))).Append('\n');
            }
            return output.ToString();
        }

        static void WriteJson(string path, object value)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + "\n", new UTF8Encoding(false));
        }

        static string Hash(string text)
        {
            using (var sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }

        static string FileHash(string path)
        {
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(input)).ToLowerInvariant();
        }

        static string Number(long value) { return value.ToString(CultureInfo.InvariantCulture); }
        static string Number(double value) { return value.ToString("F6", CultureInfo.InvariantCulture); }
    }
}
