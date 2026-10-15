using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using StreamDuel.Core;

namespace StreamDuel.Sim
{
    internal static class Tuning
    {
        const int SymmetryGames = 8;
        const double AcceptanceRate = 0.52;
        const double WilsonZ = 1.959963984540054;
        static readonly JsonSerializerOptions compactJson = JsonOptions(false);
        static readonly JsonSerializerOptions readableJson = JsonOptions(true);
        static readonly HashSet<string> hardUnused = new HashSet<string>(StringComparer.Ordinal)
        {
            "ControlledSwitchMinHpRatio", "ControlledSwitchProbability", "RandomScoreRange",
            "SwitchMinThreatRatio", "SwitchMaxActionScore", "SwitchMinMatchupGain", "SwitchProbability",
            "MatchupSlowerPenalty", "PpReduceScore"
        };

        private sealed class Result
        {
            public int Wins;
            public int Losses;
            public int Draws;
            public int Games;
            public double WinRate { get { return (double)Wins / Games; } }
            public double Wilson95Lower { get { return WilsonLower(Wins, Games); } }
        }

        private sealed class Change
        {
            public string Name;
            public double Before;
            public double After;
        }

        private sealed class CandidateResult
        {
            public int Candidate;
            public List<Change> Changes;
            public SortedDictionary<string, double> Weights;
            public Result Result;
            public bool Eligible;
            public bool Accepted;
        }

        private sealed class RoundResult
        {
            public int Round;
            public uint BattleSeedStart;
            public uint TeamSeed;
            public int SeedCount;
            public SortedDictionary<string, double> OpponentWeights;
            public List<CandidateResult> Candidates = new List<CandidateResult>();
        }

        private sealed class HoldoutResult
        {
            public uint BattleSeedStart;
            public uint TeamSeed;
            public int SeedCount;
            public SortedDictionary<string, double> CandidateWeights;
            public SortedDictionary<string, double> BaselineWeights;
            public Result Result;
        }

        private sealed class Report
        {
            public string Schema = "streamduel-ai-tuning-v2";
            public string Status = "running";
            public string PackId;
            public string ContentHash;
            public string Level;
            public uint Seed;
            public uint MutationSeed;
            public int RequestedRounds;
            public int CandidatesPerRound;
            public int GamesPerCandidate;
            public int HoldoutGames;
            public double Perturbation;
            public int MaximumChanges;
            public int Jobs;
            public string[] TunableWeights;
            public int SymmetryGames = Tuning.SymmetryGames;
            public string Symmetry = "team-order x candidate-side x choose-order; each factor 0 then 1";
            public string ReplacementPolicy = "ChooseReplacementWithWeights; same side order as choose";
            public string WinRateDefinition = "wins / all games; draws are non-wins";
            public string Acceptance = "winRate > 0.52 and two-sided 95% Wilson lower > 0.52; best eligible candidate per round";
            public string StatisticalScope = "per-candidate exploratory Wilson screen; correlated symmetry games and multiple candidates are not independent confirmatory evidence; holdout never feeds search";
            public string SeedSchedule = "uint32 wraparound; one seed per eight-game block; disjoint training/holdout battle-seed ranges";
            public SortedDictionary<string, double> InitialWeights;
            public SortedDictionary<string, double> BaselineWeights;
            public List<RoundResult> Rounds = new List<RoundResult>();
            public SortedDictionary<string, double> FinalWeights;
            public HoldoutResult Holdout;
        }

        public static void Run(ContentDb db, int rounds, int candidates, int games, int holdoutGames,
            uint seed, AiLevel level, double perturbation, int changes, int jobs, string weightsPath, string baselinePath, string reportPath)
        {
            var roster = db.Streamers.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var best = LoadWeights(weightsPath, level == AiLevel.Hard ? AiWeights.HardDefault : AiWeights.Default);
            var baseline = LoadWeights(baselinePath, AiWeights.Legacy);
            if (best.DamageScoreMultiplier != 1 || best.PpReduceScore != 0)
                throw new ArgumentException("起始权重的 DamageScoreMultiplier 必须为 1，PpReduceScore 必须为 0；这两项为冻结的默认契约。");
            CheckReportPath(reportPath, weightsPath, baselinePath);
            string[] tunable = best.Names().Where(name => name != "DamageScoreMultiplier" && name != "PpReduceScore" &&
                (level != AiLevel.Hard || !hardUnused.Contains(name))).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            // 扰动和抽阵容各有独立 Battle 随机源，不让对局长度改变后续候选与样本。
            uint mutationSeed = seed ^ 0xA511E9B3u;
            var random = RandomSource(db, roster, mutationSeed);
            var report = new Report
            {
                PackId = db.PackId, ContentHash = db.ContentHash(), Level = level.ToString().ToLowerInvariant(),
                Seed = seed, MutationSeed = mutationSeed, RequestedRounds = rounds,
                CandidatesPerRound = candidates, GamesPerCandidate = games, HoldoutGames = holdoutGames,
                Perturbation = perturbation, MaximumChanges = Math.Min(changes, tunable.Length), Jobs = jobs, TunableWeights = tunable,
                InitialWeights = Values(best), BaselineWeights = Values(baseline), FinalWeights = Values(best)
            };
            Console.Error.WriteLine(JsonSerializer.Serialize(new { type = "configuration", data = report }, compactJson));
            SaveReport(reportPath, report);

            for (int round = 0; round < rounds; round++)
            {
                uint roundSeed = unchecked(seed + (uint)((ulong)round * (uint)(games / SymmetryGames)));
                var roundResult = new RoundResult
                {
                    Round = round + 1, BattleSeedStart = roundSeed, TeamSeed = TeamSeed(roundSeed),
                    SeedCount = games / SymmetryGames, OpponentWeights = Values(best)
                };
                AiWeights nextBest = null;
                CandidateResult selected = null;
                for (int index = 0; index < candidates; index++)
                {
                    // 轮内所有候选共享冻结对手和样本，候选生成顺序不能改变对照难度。
                    var candidate = best.Copy();
                    var modified = Perturb(candidate, random, tunable, perturbation, changes);
                    var result = Evaluate(db, roster, candidate, best, games, roundSeed, level, jobs);
                    var entry = new CandidateResult
                    {
                        Candidate = index + 1, Changes = modified, Weights = Values(candidate), Result = result,
                        Eligible = result.WinRate > AcceptanceRate && result.Wilson95Lower > AcceptanceRate
                    };
                    roundResult.Candidates.Add(entry);
                    if (entry.Eligible && (selected == null || result.Wins > selected.Result.Wins))
                    {
                        selected = entry;
                        nextBest = candidate;
                    }
                    Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "第 {0}/{1} 轮，候选 {2}/{3}：胜 {4}，负 {5}，平 {6}；胜率 {7:P2}，Wilson 95% 下界 {8:P2}；{9}。",
                        round + 1, rounds, index + 1, candidates, result.Wins, result.Losses, result.Draws,
                        result.WinRate, result.Wilson95Lower, entry.Eligible ? "达到采用门槛" : "保留当前最佳"));
                }
                if (selected != null)
                {
                    selected.Accepted = true;
                    best = nextBest;
                }
                report.Rounds.Add(roundResult);
                report.FinalWeights = Values(best);
                Console.Error.WriteLine(JsonSerializer.Serialize(new { type = "round", data = roundResult }, compactJson));
                SaveReport(reportPath, report);
            }

            // 留出种子不参与选拔，即使结果不好也不回滚或再次筛选候选。
            uint validationSeed = unchecked(seed + (uint)((ulong)rounds * (uint)(games / SymmetryGames)));
            var validation = Evaluate(db, roster, best, baseline, holdoutGames, validationSeed, level, jobs);
            report.Holdout = new HoldoutResult
            {
                BattleSeedStart = validationSeed, TeamSeed = TeamSeed(validationSeed),
                SeedCount = holdoutGames / SymmetryGames, CandidateWeights = Values(best), BaselineWeights = Values(baseline),
                Result = validation
            };
            report.Status = "completed";
            SaveReport(reportPath, report);
            Console.Error.WriteLine(JsonSerializer.Serialize(new { type = "holdout", data = report.Holdout }, compactJson));
            Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "独立留出验证：胜 {0}/{1}（{2:P2}），负 {3}，平 {4}；Wilson 95% 下界 {5:P2}；起始种子 {6}。未自动采用。",
                validation.Wins, validation.Games, validation.WinRate, validation.Losses, validation.Draws,
                validation.Wilson95Lower, validationSeed));
            Console.WriteLine(JsonSerializer.Serialize(Values(best), readableJson));
        }

        public static void Compare(ContentDb db, int games, uint seed, AiLevel level, AiLevel opponentLevel,
            int jobs, string weightsPath, string baselinePath, string reportPath)
        {
            var weights = LoadWeights(weightsPath, AiWeights.Legacy);
            var baseline = LoadWeights(baselinePath, AiWeights.Legacy);
            CheckReportPath(reportPath, weightsPath, baselinePath);
            var roster = db.Streamers.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var result = Evaluate(db, roster, weights, baseline, games, seed, level, opponentLevel, jobs);
            var report = new
            {
                Schema = "streamduel-ai-comparison-v1", Status = "completed", db.PackId,
                ContentHash = db.ContentHash(), Level = level.ToString().ToLowerInvariant(),
                OpponentLevel = opponentLevel.ToString().ToLowerInvariant(), Jobs = jobs,
                BattleSeedStart = seed, TeamSeed = TeamSeed(seed), SeedCount = games / SymmetryGames,
                SymmetryGames, Symmetry = "team-order x candidate-side x choose-order; each factor 0 then 1",
                ReplacementPolicy = "ChooseReplacementWithWeights using each side's level; same side order as choose",
                WinRateDefinition = "wins / all games for --weights side; draws are non-wins",
                StatisticalScope = "fixed comparison only; no mutation or selection; symmetry games are correlated",
                Weights = Values(weights), BaselineWeights = Values(baseline), Result = result
            };
            string json = JsonSerializer.Serialize(report, readableJson);
            if (reportPath != null) File.WriteAllText(reportPath, json + "\n", new UTF8Encoding(false));
            Console.WriteLine(json);
        }

        private static AiWeights LoadWeights(string path, AiWeights defaults)
        {
            if (path == null) return defaults.Copy();
            string json = File.ReadAllText(path);
            var weights = AiWeights.FromJson(json);
            using var document = JsonDocument.Parse(json);
            var supplied = new HashSet<string>(document.RootElement.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
            if (!supplied.SetEquals(weights.Names()))
                throw new ArgumentException("权重文件必须包含全部权重，避免续跑时遗漏项随默认值漂移：" + path);
            return weights;
        }

        private static void CheckReportPath(string reportPath, params string[] inputs)
        {
            if (reportPath == null) return;
            string output = Path.GetFullPath(reportPath);
            foreach (string input in inputs)
                if (input != null && string.Equals(output, Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--report 不能覆盖 --weights 或 --baseline 输入文件。");
        }

        private static void SaveReport(string path, Report report)
        {
            if (path != null) File.WriteAllText(path, JsonSerializer.Serialize(report, readableJson) + "\n", new UTF8Encoding(false));
        }

        private static SortedDictionary<string, double> Values(AiWeights weights)
        {
            var result = new SortedDictionary<string, double>(StringComparer.Ordinal);
            foreach (string name in weights.Names()) result.Add(name, weights.Get(name));
            return result;
        }

        private static JsonSerializerOptions JsonOptions(bool indented)
        {
            return new JsonSerializerOptions
            {
                IncludeFields = true, WriteIndented = indented, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
        }

        private static List<Change> Perturb(AiWeights weights, Battle random, string[] tunable, double perturbation, int maxChanges)
        {
            var names = (string[])tunable.Clone();
            int count = 1 + (int)(random.Random() * Math.Min(maxChanges, names.Length));
            var changes = new List<Change>();
            for (int i = 0; i < count; i++)
            {
                int selected = i + (int)(random.Random() * (names.Length - i));
                string name = names[selected];
                names[selected] = names[i];
                names[i] = name;
                double before = weights.Get(name);
                double delta = (random.Random() * 2 - 1) * perturbation;
                double after = before * (1 + delta);
                weights.Set(name, after);
                changes.Add(new Change { Name = name, Before = before, After = after });
            }
            return changes;
        }

        private static double WilsonLower(int wins, int games)
        {
            double p = (double)wins / games;
            double z2 = WilsonZ * WilsonZ;
            return (p + z2 / (2 * games) - WilsonZ * Math.Sqrt(p * (1 - p) / games + z2 / (4 * games * (double)games)))
                / (1 + z2 / games);
        }

        private static Result Evaluate(ContentDb db, string[] roster, AiWeights candidate,
            AiWeights opponent, int games, uint seed, AiLevel level, int jobs)
        {
            return Evaluate(db, roster, candidate, opponent, games, seed, level, level, jobs);
        }

        private static Result Evaluate(ContentDb db, string[] roster, AiWeights candidate,
            AiWeights opponent, int games, uint seed, AiLevel level, AiLevel opponentLevel, int jobs)
        {
            var random = RandomSource(db, roster, TeamSeed(seed));
            int blockCount = games / SymmetryGames;
            var teams = new IList<string>[blockCount][];
            // 先串行抽完阵容，线程调度不会改变随机流的归属。
            for (int block = 0; block < blockCount; block++) teams[block] = DrawTeams(roster, random);
            var blocks = new Result[blockCount];
            if (jobs == 1)
            {
                for (int block = 0; block < blockCount; block++)
                    blocks[block] = EvaluateBlock(db, teams[block], candidate, opponent, unchecked(seed + (uint)block), level, opponentLevel);
            }
            else
            {
                Parallel.For(0, blockCount, new ParallelOptions { MaxDegreeOfParallelism = jobs }, block =>
                {
                    blocks[block] = EvaluateBlock(db, teams[block], candidate, opponent, unchecked(seed + (uint)block), level, opponentLevel);
                });
            }
            // 每组独立计数并按索引合计，只共享只读内容与权重，不共享 Battle 或累计器。
            var result = new Result { Games = games };
            for (int block = 0; block < blockCount; block++)
            {
                result.Wins += blocks[block].Wins;
                result.Losses += blocks[block].Losses;
                result.Draws += blocks[block].Draws;
            }
            return result;
        }

        private static Result EvaluateBlock(ContentDb db, IList<string>[] drawnTeams, AiWeights candidate,
            AiWeights opponent, uint battleSeed, AiLevel level, AiLevel opponentLevel)
        {
            var result = new Result { Games = SymmetryGames };
            for (int swapTeams = 0; swapTeams < 2; swapTeams++)
            for (int candidateSide = 0; candidateSide < 2; candidateSide++)
            for (int firstChoiceSide = 0; firstChoiceSide < 2; firstChoiceSide++)
            {
                // 三个独立的交换因子，分别抵消阵容、固定侧与共享 RNG 取值先后的偏差。
                var teams = new[] { drawnTeams[swapTeams], drawnTeams[1 - swapTeams] };
                var battle = new Battle(db, teams, new[] { "甲方", "乙方" }, battleSeed);
                while (battle.Phase != Phase.Over)
                {
                    if (battle.Phase == Phase.Replace)
                    {
                        for (int offset = 0; offset < 2 && battle.Phase == Phase.Replace; offset++)
                        {
                            int side = firstChoiceSide ^ offset;
                            if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacementWithWeights(battle, side,
                                side == candidateSide ? level : opponentLevel,
                                side == candidateSide ? candidate : opponent));
                        }
                        continue;
                    }
                    var actions = new BattleAction[2];
                    for (int offset = 0; offset < 2; offset++)
                    {
                        int side = firstChoiceSide ^ offset;
                        actions[side] = Ai.ChooseWithWeights(battle, side, side == candidateSide ? level : opponentLevel,
                            side == candidateSide ? candidate : opponent);
                    }
                    battle.ResolveTurn(actions[0], actions[1]);
                }
                if (battle.Winner == candidateSide) result.Wins++;
                else if (battle.Winner == -1) result.Draws++;
                else result.Losses++;
            }
            return result;
        }

        private static uint TeamSeed(uint seed) { return seed ^ 0x63D83595u; }

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
