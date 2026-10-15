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
            public int Rounds = 20;
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
                    Console.WriteLine("调参：dotnet run --project Tools/Sim -- --tune --rounds 20 --games 400 --seed 1；省略 games 时调参默认 400 局。");
                    Console.WriteLine("调参 games 必须为正偶数；每轮扰动 1～3 个权重（±30%），挑战当前最好权重；胜率严格超过 55% 才替换。双方交替换边，平局不计胜场。");
                    Console.WriteLine("调参标准输出只写最终权重 JSON；标准错误写每轮结果及独立种子集对 Default 的胜率。不会自动采用权重或改写文件。");
                    Console.WriteLine("--level 支持 easy（简单）/ normal（普通）/ hard（困难）/ expert（专家），默认 normal；双方行动和补位使用同一难度。");
                    Console.WriteLine("--tune 固定搜索普通难度的评分权重，不能与 --level 同时使用。");
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
                    Tuning.Run(db, options.Rounds, options.Games, options.Seed);
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
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is KeyError || ex is FormatException)
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
            bool levelSpecified = false;
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
                if (option != "--pack" && option != "--pack-dir" && option != "--games" && option != "--seed" && option != "--rounds" && option != "--level")
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
                        levelSpecified = true;
                        break;
                }
            }
            if (options.Tune && levelSpecified)
                throw new ArgumentException("--tune 固定搜索普通难度的评分权重，不能与 --level 同时使用；请移除 --level。");
            if (roundsSpecified && !options.Tune) throw new ArgumentException("--rounds 需要与 --tune 一起使用。");
            if (options.Tune && !gamesSpecified) options.Games = 400;
            if (options.Tune && options.Games % 2 != 0) throw new ArgumentException("调参的 --games 必须为正偶数，以便每组阵容交换双方各赛一局。");
            if (options.Tune && (ulong)options.Rounds * (uint)options.Games + (uint)(options.Games / 2) > (ulong)uint.MaxValue + 1)
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
