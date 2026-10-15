using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Sim
{
    internal static class Program
    {
        private sealed class Options
        {
            public string Pack = "original";
            public string PackDirectory;
            public int Games = 5000;
            public uint Seed = 1;
            public AiLevel Level = AiLevel.Normal;
            public bool Help;
            public bool ListPacks;
            public bool Tune;
            public bool Compare;
            public AiLevel OpponentLevel = AiLevel.Normal;
            public int Rounds = 20;
            public int Candidates = 4;
            public int HoldoutGames = 800;
            public double Perturbation = 0.3;
            public int Changes = 3;
            public int Jobs = 1;
            public string Weights;
            public string Baseline;
            public string Report;
        }

        private sealed class StreamerStats
        {
            public StreamerDef Streamer;
            public long Games;
            public long Wins;
            public long Turns;
            public long DamageDealt;
            public long Kos;
            public double WinRate { get { return Games == 0 ? 0 : (double)Wins / Games; } }
            public double AverageTurns { get { return Games == 0 ? 0 : (double)Turns / Games; } }
            public double AverageDamageDealt { get { return Games == 0 ? 0 : (double)DamageDealt / Games; } }
            public double AverageKos { get { return Games == 0 ? 0 : (double)Kos / Games; } }
        }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Out.NewLine = "\n";
            try
            {
                var options = ParseOptions(args);
                if (options.Help)
                {
                    Console.WriteLine("用法：dotnet run --project Tools/Sim -- --pack original --games 5000 --seed 1 [--level normal] [--pack-dir <文件夹>] [--list-packs]");
                    Console.WriteLine("调参：dotnet run --project Tools/Sim -- --tune --level normal --rounds 20 --candidates 4 --games 400 --seed 1。");
                    Console.WriteLine("调参支持 normal / hard（默认 normal）；每轮生成若干候选，对照轮初最佳。--changes 默认 3：每个候选扰动 1～3 个有效权重。");
                    Console.WriteLine("--perturbation 默认 0.3：乘法扰动 ±30%，支持 0 < 幅度 <= 1。DamageScoreMultiplier / PpReduceScore 冻结，Hard 只扰动实际使用字段。");
                    Console.WriteLine("--jobs 默认 1，范围 1.." + Math.Min(16, Environment.ProcessorCount) + "；只并行独立的八局组，固定种子的结果不随并行数变化。");
                    Console.WriteLine("调参 games 默认 400、--holdout-games 默认 800，均须为正的 8 倍数；每组固定阵容/种子，交换阵容、权重所在方、电脑调用顺序共 8 局。平局非胜。");
                    Console.WriteLine("候选胜率 >52% 且 Wilson 双侧 95% 下界 >52% 才可采用，每轮取合格候选中胜率最高者，同分取先生成者。");
                    Console.WriteLine("--weights <JSON文件> 指定起始完整权重；--baseline <JSON文件> 指定最终对照（默认旧版 Legacy）。最终独立种子留出验证不反馈搜索。");
                    Console.WriteLine("调参 stdout 仅输出最终权重 JSON；stderr 含每轮全部候选权重、种子、胜负平与采用记录。--report <JSON文件> 另保存完整记录；不会自动采用权重。");
                    Console.WriteLine("对比：--compare --level hard --opponent-level normal --weights <本方完整权重.json> --baseline <对手完整权重.json> --games 400 --seed 1 --jobs 1。");
                    Console.WriteLine("对比双方难度只支持 normal / hard（均默认 normal），必须显式提供两份权重；八局对称规则不变，不扰动或筛选。stdout 输出完整结果 JSON，--report 可另存同一报告。");
                    Console.WriteLine("--level 支持 easy（简单）/ normal（普通）/ hard（困难）/ expert（专家），默认 normal；双方行动和补位使用同一难度。");
                    Console.WriteLine("默认：--pack original，--games 5000，--seed 1；games 为正整数，seed 为 0..4294967295。");
                    Console.WriteLine("--pack-dir 加载指定文件夹中的 JSON（不递归）；--list-packs 列出内置和外部包后退出。--pack 使用包 id，兼容内置文件名；id 冲突时内置优先并提示。");
                    Console.WriteLine("games 按入选队伍计数（包括未上场的后排）；wins 为所在队伍获胜次数，平局不计胜场。");
                    Console.WriteLine("winrate 为 wins/games（0..1），avg_turns 为这些对局的平均回合数；双方都有同一主播时分别计数。");
                    Console.WriteLine("avg_damage_dealt、avg_kos 为每次入选队伍的平均造成伤害、平均击倒数，未上场也计入分母；统计口径由 Core.BattleSummary 定义。");
                    Console.WriteLine("uses 只统计实际执行的技能（含未命中、免扣 PP、硬撑），未执行的选择不计入；未使用项为 0。");
                    Console.WriteLine("标准输出为两段 CSV；标准错误包含总局数、平局数和耗时，耗时不参与可复现比较。");
                    return 0;
                }

                string content = Path.Combine(AppContext.BaseDirectory, "Content");
                var db = LoadPack(content, options);
                if (options.ListPacks) return 0;
                if (options.Tune)
                {
                    Tuning.Run(db, options.Rounds, options.Candidates, options.Games, options.HoldoutGames,
                        options.Seed, options.Level, options.Perturbation, options.Changes, options.Jobs, options.Weights, options.Baseline, options.Report);
                    return 0;
                }
                if (options.Compare)
                {
                    Tuning.Compare(db, options.Games, options.Seed, options.Level, options.OpponentLevel,
                        options.Jobs, options.Weights, options.Baseline, options.Report);
                    return 0;
                }
                var streamers = db.Streamers.ToDictionary(s => s.Id, s => new StreamerStats { Streamer = s });
                var skills = db.Skills.Values.Concat(new[] { db.Struggle }).ToDictionary(s => s.Id);
                var uses = skills.Keys.ToDictionary(id => id, id => 0L);
                var roster = db.Streamers.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                // 抽阵容独立于对战随机数，避免对局长短影响后续阵容。
                var teamRandom = new Random(unchecked((int)options.Seed));
                int draws = 0;
                var timer = Stopwatch.StartNew();

                for (int game = 0; game < options.Games; game++)
                {
                    var teams = DrawTeams(roster, teamRandom);
                    var battle = new Battle(db, teams, new[] { "甲方", "乙方" }, unchecked(options.Seed + (uint)game));
                    int turns = RunBattle(battle, options.Level);
                    var summary = BattleSummary.From(battle);
                    if (battle.Winner == -1) draws++;
                    for (int side = 0; side < 2; side++)
                    {
                        foreach (var fighter in summary.Sides[side])
                        {
                            var stats = streamers[fighter.StreamerId];
                            stats.Games++;
                            if (battle.Winner == side) stats.Wins++;
                            stats.Turns += turns;
                            stats.DamageDealt += fighter.DamageDealt;
                            stats.Kos += fighter.Kos;
                            foreach (var skill in fighter.SkillUses) uses[skill.Key] += skill.Value;
                        }
                    }
                }

                timer.Stop();
                WriteCsv(streamers.Values, skills, uses);
                Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "总局数：{0}，平局数：{1}，耗时：{2:F3} 秒", options.Games, draws, timer.Elapsed.TotalSeconds));
                return 0;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException ||
                ex is KeyError || ex is FormatException || ex is KeyNotFoundException || ex is System.Text.Json.JsonException)
            {
                Console.Error.WriteLine("错误：" + ex.Message);
                return 1;
            }
        }

        private static Options ParseOptions(string[] args)
        {
            var options = new Options();
            bool gamesSpecified = false;
            bool roundsSpecified = false;
            bool tuningOptionSpecified = false;
            bool evaluationOptionSpecified = false;
            bool opponentLevelSpecified = false;
            for (int i = 0; i < args.Length; i++)
            {
                string option = args[i];
                if (option == "--help" || option == "-h")
                {
                    options.Help = true;
                    continue;
                }
                if (option == "--list-packs")
                {
                    options.ListPacks = true;
                    continue;
                }
                if (option == "--tune")
                {
                    options.Tune = true;
                    continue;
                }
                if (option == "--compare")
                {
                    options.Compare = true;
                    continue;
                }
                if (option != "--pack" && option != "--pack-dir" && option != "--games" && option != "--seed" && option != "--rounds" && option != "--level" &&
                    option != "--candidates" && option != "--holdout-games" && option != "--weights" && option != "--baseline" && option != "--report" &&
                    option != "--perturbation" && option != "--changes" && option != "--jobs" && option != "--opponent-level")
                    throw new ArgumentException("未知选项：" + option + "；使用 --help 查看用法。");
                if (++i >= args.Length || args[i] == null || args[i].StartsWith("--", StringComparison.Ordinal) || args[i] == "-h")
                    throw new ArgumentException(option + " 缺少参数值。");
                string value = args[i];
                switch (option)
                {
                    case "--pack":
                        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--pack 需要非空内容包 id。");
                        options.Pack = value;
                        break;
                    case "--pack-dir":
                        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--pack-dir 需要非空文件夹路径。");
                        options.PackDirectory = value;
                        break;
                    case "--games":
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.Games) || options.Games <= 0)
                            throw new ArgumentException("--games 必须是 1..2147483647 的整数。");
                        gamesSpecified = true;
                        break;
                    case "--rounds":
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.Rounds) || options.Rounds <= 0)
                            throw new ArgumentException("--rounds 必须是 1..2147483647 的整数。");
                        roundsSpecified = true;
                        break;
                    case "--candidates":
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.Candidates) || options.Candidates <= 0)
                            throw new ArgumentException("--candidates 必须是 1..2147483647 的整数。");
                        tuningOptionSpecified = true;
                        break;
                    case "--changes":
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.Changes) || options.Changes <= 0 || options.Changes > 35)
                            throw new ArgumentException("--changes 必须是 1..35 的整数；实际最多使用该难度的可调字段数。");
                        tuningOptionSpecified = true;
                        break;
                    case "--jobs":
                        int maxJobs = Math.Min(16, Environment.ProcessorCount);
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.Jobs) || options.Jobs <= 0 || options.Jobs > maxJobs)
                            throw new ArgumentException("--jobs 必须是 1.." + maxJobs + " 的整数。");
                        evaluationOptionSpecified = true;
                        break;
                    case "--perturbation":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out options.Perturbation) ||
                            !double.IsFinite(options.Perturbation) || options.Perturbation <= 0 || options.Perturbation > 1)
                            throw new ArgumentException("--perturbation 必须是大于 0 且不超过 1 的有限小数。");
                        tuningOptionSpecified = true;
                        break;
                    case "--holdout-games":
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.HoldoutGames) || options.HoldoutGames <= 0)
                            throw new ArgumentException("--holdout-games 必须是 1..2147483647 的整数。");
                        tuningOptionSpecified = true;
                        break;
                    case "--weights":
                    case "--baseline":
                    case "--report":
                        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(option + " 需要非空 JSON 文件路径。");
                        if (option == "--weights") options.Weights = value;
                        else if (option == "--baseline") options.Baseline = value;
                        else options.Report = value;
                        evaluationOptionSpecified = true;
                        break;
                    case "--seed":
                        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.Seed))
                            throw new ArgumentException("--seed 必须是 0..4294967295 的整数。");
                        break;
                    case "--level":
                        if (value == "easy") options.Level = AiLevel.Easy;
                        else if (value == "normal") options.Level = AiLevel.Normal;
                        else if (value == "hard") options.Level = AiLevel.Hard;
                        else if (value == "expert") options.Level = AiLevel.Expert;
                        else throw new ArgumentException("--level 只能是 easy（简单）、normal（普通）、hard（困难）或 expert（专家）。");
                        break;
                    case "--opponent-level":
                        if (value == "normal") options.OpponentLevel = AiLevel.Normal;
                        else if (value == "hard") options.OpponentLevel = AiLevel.Hard;
                        else throw new ArgumentException("--opponent-level 只能是 normal（普通）或 hard（困难）。");
                        opponentLevelSpecified = true;
                        break;
                }
            }
            if (options.Tune && options.Compare) throw new ArgumentException("--tune 与 --compare 不能同时使用。");
            bool evaluating = options.Tune || options.Compare;
            if (evaluating && options.Level != AiLevel.Normal && options.Level != AiLevel.Hard)
                throw new ArgumentException("--tune / --compare 的 --level 只支持 normal 或 hard。");
            if (roundsSpecified && !options.Tune) throw new ArgumentException("--rounds 需要与 --tune 一起使用。");
            if (tuningOptionSpecified && !options.Tune) throw new ArgumentException("--candidates、--holdout-games、--perturbation、--changes 需要与 --tune 一起使用。");
            if (evaluationOptionSpecified && !evaluating) throw new ArgumentException("--weights、--baseline、--report、--jobs 需要与 --tune 或 --compare 一起使用。");
            if (opponentLevelSpecified && !options.Compare) throw new ArgumentException("--opponent-level 需要与 --compare 一起使用。");
            if (options.Compare && (options.Weights == null || options.Baseline == null))
                throw new ArgumentException("--compare 必须显式提供 --weights 和 --baseline 两份完整权重文件。");
            if (evaluating && options.ListPacks) throw new ArgumentException("--tune / --compare 不能与 --list-packs 同时使用。");
            if (evaluating && !gamesSpecified) options.Games = 400;
            if ((evaluating && options.Games % 8 != 0) || (options.Tune && options.HoldoutGames % 8 != 0))
                throw new ArgumentException("调参或对比的 --games 与调参 --holdout-games 必须为正的 8 倍数，以便完整交换阵容、双方与电脑调用顺序。");
            if (options.Tune && (ulong)options.Rounds * (uint)(options.Games / 8) + (uint)(options.HoldoutGames / 8) > (ulong)uint.MaxValue + 1)
                throw new ArgumentException("调参的轮数与局数组合超出种子范围，会重复训练或验证种子；请减少 --rounds 或 --games。");
            return options;
        }

        private static ContentDb LoadPack(string content, Options options)
        {
            var policy = PackPolicy.Load(Path.Combine(content, "PackPolicy.json"));
            string rules = File.ReadAllText(Path.Combine(content, "StreamDuelRules.json"));
            var builtIn = PackLoader.LoadDirectory(rules, Path.Combine(content, "Packs"), policy);
            var packs = new List<PackLoadResult>();
            foreach (var result in builtIn)
            {
                if (result.Db == null) Console.Error.WriteLine("[错误] 内置内容包 " + result.FileName + "：" + result.Error);
                else packs.Add(result);
            }
            var builtInIds = new HashSet<string>(packs.Select(result => result.Db.PackId), StringComparer.Ordinal);
            if (options.PackDirectory != null)
            {
                foreach (var result in PackLoader.LoadDirectory(rules, options.PackDirectory, policy))
                {
                    if (result.Db == null)
                        Console.Error.WriteLine("[错误] 外部内容包 " + result.FileName + "：" + result.Error);
                    else if (builtInIds.Contains(result.Db.PackId))
                        Console.Error.WriteLine("[提醒] 外部内容包 " + result.FileName + " 的 id「" + result.Db.PackId + "」与内置包重复，已使用内置包。");
                    else packs.Add(result);
                }
            }
            if (options.ListPacks)
            {
                foreach (var result in packs)
                    Console.WriteLine(result.Db.PackId + "：" + result.Db.PackName + "〔" + (builtIn.Contains(result) ? "内置" : "外部") + "〕（" + result.FileName + "）");
                return null;
            }

            string disabledReason = policy.PackReason(options.Pack);
            if (disabledReason != null) throw new ArgumentException(disabledReason);
            var selected = packs.FirstOrDefault(result => result.Db.PackId == options.Pack);
            if (selected == null)
                selected = builtIn.FirstOrDefault(result => result.Db != null &&
                    string.Equals(Path.GetFileNameWithoutExtension(result.FileName), options.Pack, StringComparison.OrdinalIgnoreCase));
            if (selected == null)
            {
                disabledReason = FindDisabledReason(rules, Path.Combine(content, "Packs"), options.Pack, policy, true)
                    ?? FindDisabledReason(rules, options.PackDirectory, options.Pack, policy, false);
                throw new ArgumentException(disabledReason ?? "找不到内容包「" + options.Pack + "」；使用 --list-packs 查看可用内容包。");
            }
            return selected.Db;
        }

        private static string FindDisabledReason(string rules, string directory, string id, PackPolicy policy, bool allowAlias)
        {
            if (directory == null || !Directory.Exists(directory)) return null;
            try
            {
                foreach (string path in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
                {
                    PackLoadResult result;
                    try { result = PackLoader.LoadJson(rules, File.ReadAllText(path), Path.GetFileName(path), policy); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    if (result.IsDisabled && (result.PackId == id || (allowAlias &&
                        string.Equals(Path.GetFileNameWithoutExtension(path), id, StringComparison.OrdinalIgnoreCase))))
                        return result.Error;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            // 目录加载时已报告坏包；选择失败时只补充下架原因，不改变原来的错误路径。
            return null;
        }

        private static IList<string>[] DrawTeams(string[] roster, Random random)
        {
            if (roster.Length >= 12)
            {
                var selected = Sample(roster, 12, random);
                return new IList<string>[] { selected.Take(6).ToArray(), selected.Skip(6).ToArray() };
            }
            return new IList<string>[] { Sample(roster, 6, random), Sample(roster, 6, random) };
        }

        private static string[] Sample(string[] roster, int count, Random random)
        {
            var shuffled = (string[])roster.Clone();
            for (int i = 0; i < count; i++)
            {
                int j = random.Next(i, shuffled.Length);
                string swap = shuffled[i];
                shuffled[i] = shuffled[j];
                shuffled[j] = swap;
            }
            return shuffled.Take(count).ToArray();
        }

        private static int RunBattle(Battle battle, AiLevel level)
        {
            int turns = 0;
            while (battle.Phase != Phase.Over)
            {
                if (battle.Phase == Phase.Replace)
                {
                    for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    {
                        if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side, level));
                    }
                    continue;
                }

                var actions = new[] { Ai.Choose(battle, 0, level), Ai.Choose(battle, 1, level) };
                battle.ResolveTurn(actions[0], actions[1]);
                turns++;
            }
            return turns;
        }

        private static void WriteCsv(IEnumerable<StreamerStats> streamers,
            Dictionary<string, SkillDef> skills, Dictionary<string, long> uses)
        {
            Console.WriteLine("id,name,games,wins,winrate,avg_turns,avg_damage_dealt,avg_kos");
            foreach (var stats in streamers.OrderByDescending(s => s.WinRate).ThenBy(s => s.Streamer.Id, StringComparer.Ordinal))
            {
                Console.WriteLine(string.Join(",", Csv(stats.Streamer.Id), Csv(stats.Streamer.Name),
                    stats.Games.ToString(CultureInfo.InvariantCulture), stats.Wins.ToString(CultureInfo.InvariantCulture),
                    stats.WinRate.ToString("F6", CultureInfo.InvariantCulture), stats.AverageTurns.ToString("F6", CultureInfo.InvariantCulture),
                    stats.AverageDamageDealt.ToString("F6", CultureInfo.InvariantCulture), stats.AverageKos.ToString("F6", CultureInfo.InvariantCulture)));
            }
            Console.WriteLine();
            Console.WriteLine("skill_id,skill_name,uses");
            foreach (string id in skills.Keys.OrderBy(id => id, StringComparer.Ordinal))
                Console.WriteLine(string.Join(",", Csv(id), Csv(skills[id].Name), uses[id].ToString(CultureInfo.InvariantCulture)));
        }

        private static string Csv(string value)
        {
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
                ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
