using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Ladder
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Out.NewLine = "\n";
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            try
            {
                if (args != null && args.Contains("--compare-styles"))
                    return StyleComparison.Run(args, LoadPack);
                if (args != null && args.Contains("--compare-expert"))
                    return ExpertComparison.Run(args, LoadPack);
                var options = Options.Parse(args);
                if (options.Help)
                {
                    Console.WriteLine("用法：dotnet run --project Tools/Ladder -- --pack original --teams 64 --rounds 10 --seed 1 [--level normal|hard] [--season s1] [--out ./ladder_out]");
                    Console.WriteLine("默认：原创包、64 支队伍、10 轮、种子 1、普通难度；teams 至少 2，rounds 至少 1，seed 为 0..4294967295。");
                    Console.WriteLine("前一半阵容自动组队，其余随机生成；奇数队伍时随机阵容多一队，均遵守赛季限制。");
                    Console.WriteLine("每队六人不重复，按号位保存；不同参赛队可抽到同一阵容。每轮按积分配对，优先减少重复对手，每对换边打两局，使用不同的确定性种子。");
                    Console.WriteLine("每局胜 1/平 0.5/负 0 分；Elo 初始 1500，每局 K=24，同对两局共用赛前期望。奇数队伍轮空得 2 分，不计对局、不改 Elo。");
                    Console.WriteLine("主播加权 Elo = Σ(所在阵容最终 Elo × 实际上场局数) / 总上场局数；从未上场的 Elo 留空，后排仅入队不算上场。");
                    Console.WriteLine("输出 UTF-8 teams.csv 和 streamers.csv，控制台打印两榜前十；同版本、内容和参数可复现。统计定义见 Tools/Ladder/README.md。");
                    Console.WriteLine("开局策略比较：--compare-openers [--blocks 250] [--pack original] [--seed 1] [--season s1] [--out ./ladder_out]；每组同种子、同两阵容换边/互换策略打四局。");
                    Console.WriteLine("默认共 1000 局，新 Hard 与旧 Normal 仅禁用/首发不同，双方行动和补位均 Hard；输出逐局 CSV、全部回放 JSONL 和胜平负/配置 JSON。");
                    Console.WriteLine();
                    Console.WriteLine("电脑强度回归：dotnet run --project Tools/Ladder -- --ai-regression [--pack original] [--games 100] [--seed 1] [--baseline 文件]");
                    Console.WriteLine("生成候选基准：dotnet run --project Tools/Ladder -- --ai-regression --write-baseline 输出文件 [--games 100] [--seed 1]");
                    Console.WriteLine("每对难度对战 games 局，games 必须为正偶数；默认读取随工具打包的 ai-baseline.json，比较时局数、种子与采样方案必须一致。");
                    Console.WriteLine("输出四档难度胜率表和六组胜负统计；高难度胜率须大于 50%，且不能比基准下降超过允许幅度，否则退出码为 1。");
                    Console.WriteLine("--write-baseline 显式覆盖指定输出文件；即使难度顺序未达标也保存实际结果，并返回失败，候选结果需审查后再启用。");
                    Console.WriteLine("回归模式不能使用 --teams、--rounds、--level、--season、--out；--baseline 与 --write-baseline 互斥。完整回归耗时较长，不接入 CI。");
                    Console.WriteLine("专家预算比较使用独立模式：--compare-expert --help。");
                    return 0;
                }

                var db = LoadPack(options.Pack);
                if (options.CompareOpeners)
                {
                    OpenerComparison.Run(db, options);
                    return 0;
                }
                if (options.AiRegression) return AiRegressionCommand.Run(db, options);
                var rosters = TeamGenerator.Generate(db, options.Season, options.Teams, options.Seed);
                var tournament = new Tournament(db, options, rosters);
                tournament.Run();
                var teams = tournament.Teams.OrderByDescending(t => t.Elo).ThenByDescending(t => t.Points).ThenBy(t => t.Id).ToList();
                var streamers = tournament.Streamers.Values.OrderByDescending(s => s.Appearances > 0)
                    .ThenByDescending(s => s.WeightedElo).ThenBy(s => s.Streamer.Id, StringComparer.Ordinal).ToList();
                var teamCsv = new StringBuilder("rank,team_id,slot1,slot2,slot3,slot4,slot5,slot6,elo,points,games,wins,draws,losses,byes\n");
                for (int i = 0; i < teams.Count; i++)
                {
                    var t = teams[i];
                    Row(teamCsv, Number(i + 1), Number(t.Id), t.Members[0], t.Members[1], t.Members[2], t.Members[3], t.Members[4], t.Members[5],
                        Number(t.Elo), Number(t.Points), Number(t.Games), Number(t.Wins), Number(t.Draws), Number(t.Losses), Number(t.Byes));
                }
                var streamerCsv = new StringBuilder("rank,id,name,weighted_elo,appearances,roster_games,wins,draws,losses,damage_dealt,kos,turns_active\n");
                for (int i = 0; i < streamers.Count; i++)
                {
                    var s = streamers[i];
                    Row(streamerCsv, Number(i + 1), s.Streamer.Id, s.Streamer.Name, s.Appearances == 0 ? "" : Number(s.WeightedElo),
                        Number(s.Appearances), Number(s.RosterGames), Number(s.Wins), Number(s.Draws), Number(s.Losses), Number(s.DamageDealt), Number(s.Kos), Number(s.TurnsActive));
                }

                Directory.CreateDirectory(options.OutputDirectory);
                File.WriteAllText(Path.Combine(options.OutputDirectory, "teams.csv"), teamCsv.ToString(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(options.OutputDirectory, "streamers.csv"), streamerCsv.ToString(), new UTF8Encoding(false));
                Console.WriteLine("天梯模拟：" + db.PackName + "；队伍 " + options.Teams + "；轮次 " + options.Rounds + "；对局 " + tournament.Games
                    + "；种子 " + options.Seed + "；难度 " + (options.Level == AiLevel.Hard ? "困难" : "普通") + "；赛季 " + (options.Season ?? "无"));
                Console.WriteLine("阵容 Elo 前十：");
                for (int i = 0; i < Math.Min(10, teams.Count); i++)
                {
                    var t = teams[i];
                    Console.WriteLine((i + 1) + ". 阵容 " + t.Id + "；Elo " + Number(t.Elo) + "；积分 " + Number(t.Points)
                        + "；" + string.Join("、", t.Members.Select(id => db.Streamer(id).Name)));
                }
                Console.WriteLine("主播出场加权 Elo 前十：");
                for (int i = 0; i < Math.Min(10, streamers.Count); i++)
                {
                    var s = streamers[i];
                    Console.WriteLine((i + 1) + ". " + s.Streamer.Name + "；Elo " + (s.Appearances == 0 ? "未上场" : Number(s.WeightedElo)) + "；上场 " + s.Appearances + " 局");
                }
                Console.WriteLine("已输出 teams.csv、streamers.csv。");
                return 0;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException || ex is KeyError || ex is FormatException)
            {
                Console.Error.WriteLine("错误：" + ex.Message);
                return 1;
            }
        }

        static ContentDb LoadPack(string id)
        {
            string content = Path.Combine(AppContext.BaseDirectory, "Content");
            var policy = PackPolicy.Load(Path.Combine(content, "PackPolicy.json"));
            string disabledReason = policy.PackReason(id);
            if (disabledReason != null) throw new ArgumentException(disabledReason);
            string rules = File.ReadAllText(Path.Combine(content, "StreamDuelRules.json"));
            var packs = PackLoader.LoadDirectory(rules, Path.Combine(content, "Packs"), policy);
            var selected = packs.FirstOrDefault(p => p.Db != null && p.Db.PackId == id)
                ?? packs.FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p.FileName), id, StringComparison.OrdinalIgnoreCase));
            if (selected == null)
            {
                disabledReason = FindDisabledReason(rules, Path.Combine(content, "Packs"), id, policy, true);
                throw new ArgumentException(disabledReason ?? "找不到内置内容包「" + id + "」。");
            }
            if (selected.Db == null) throw new ArgumentException("内容包载入失败：" + selected.Error);
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

        static string Number(long value) { return value.ToString(CultureInfo.InvariantCulture); }
        static string Number(double value) { return value.ToString("F6", CultureInfo.InvariantCulture); }

        static void Row(StringBuilder output, params string[] cells)
        {
            output.Append(string.Join(",", cells.Select(cell => cell.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
                ? cell : "\"" + cell.Replace("\"", "\"\"") + "\""))).Append('\n');
        }
    }
}
