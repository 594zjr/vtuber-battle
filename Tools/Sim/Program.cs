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
            public int Games = 5000;
            public uint Seed = 1;
            public bool Help;
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
                    Console.WriteLine("用法：dotnet run --project Tools/Sim -- --pack original --games 5000 --seed 1");
                    Console.WriteLine("默认：--pack original，--games 5000，--seed 1；games 为正整数，seed 为 0..4294967295。");
                    Console.WriteLine("games 按入选队伍计数（包括未上场的后排）；wins 为所在队伍获胜次数，平局不计胜场。");
                    Console.WriteLine("winrate 为 wins/games（0..1），avg_turns 为这些对局的平均回合数；双方都有同一主播时分别计数。");
                    Console.WriteLine("avg_damage_dealt、avg_kos 为每次入选队伍的平均造成伤害、平均击倒数，未上场也计入分母；统计口径由 Core.BattleSummary 定义。");
                    Console.WriteLine("uses 只统计实际执行的技能（含未命中、免扣 PP、硬撑），未执行的选择不计入；未使用项为 0。");
                    Console.WriteLine("标准输出为两段 CSV；标准错误包含总局数、平局数和耗时，耗时不参与可复现比较。");
                    return 0;
                }

                string content = Path.Combine(AppContext.BaseDirectory, "Content");
                var db = ContentDb.FromJson(
                    File.ReadAllText(Path.Combine(content, "StreamDuelRules.json")),
                    File.ReadAllText(Path.Combine(content, "Packs", options.Pack + ".json")));
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
                    int turns = RunBattle(battle);
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
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is KeyError)
            {
                Console.Error.WriteLine("错误：" + ex.Message);
                return 1;
            }
        }

        private static Options ParseOptions(string[] args)
        {
            var options = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string option = args[i];
                if (option == "--help" || option == "-h")
                {
                    options.Help = true;
                    continue;
                }
                if (option != "--pack" && option != "--games" && option != "--seed")
                    throw new ArgumentException("未知选项：" + option + "；使用 --help 查看用法。");
                if (++i >= args.Length) throw new ArgumentException(option + " 缺少参数值。");
                string value = args[i];
                switch (option)
                {
                    case "--pack":
                        if (value.Length == 0 || !value.All(c =>
                            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                            (c >= '0' && c <= '9') || c == '_' || c == '-'))
                            throw new ArgumentException("--pack 应为内容包文件名（不含 .json），只允许英文字母、数字、下划线和连字符。");
                        options.Pack = value;
                        break;
                    case "--games":
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.Games) || options.Games <= 0)
                            throw new ArgumentException("--games 必须是 1..2147483647 的整数。");
                        break;
                    case "--seed":
                        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out options.Seed))
                            throw new ArgumentException("--seed 必须是 0..4294967295 的整数。");
                        break;
                }
            }
            return options;
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

        private static int RunBattle(Battle battle)
        {
            int turns = 0;
            while (battle.Phase != Phase.Over)
            {
                if (battle.Phase == Phase.Replace)
                {
                    for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                    {
                        if (battle.Need[side]) battle.Replace(side, Ai.ChooseReplacement(battle, side));
                    }
                    continue;
                }

                var actions = new[] { Ai.Choose(battle, 0), Ai.Choose(battle, 1) };
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
