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
                    return 0;
                }

                var db = LoadPack(options.Pack);
                if (options.CompareOpeners)
                {
                    OpenerComparison.Run(db, options);
                    return 0;
                }
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
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException || ex is KeyError)
            {
                Console.Error.WriteLine("错误：" + ex.Message);
                return 1;
            }
        }

        static ContentDb LoadPack(string id)
        {
            string content = Path.Combine(AppContext.BaseDirectory, "Content");
            string rules = File.ReadAllText(Path.Combine(content, "StreamDuelRules.json"));
            var packs = PackLoader.LoadDirectory(rules, Path.Combine(content, "Packs"));
            var selected = packs.FirstOrDefault(p => p.Db != null && p.Db.PackId == id)
                ?? packs.FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p.FileName), id, StringComparison.OrdinalIgnoreCase));
            if (selected == null) throw new ArgumentException("找不到内置内容包「" + id + "」。");
            if (selected.Db == null) throw new ArgumentException("内容包载入失败：" + selected.Error);
            return selected.Db;
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
