using System;
using System.IO;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static bool ReadTournamentRatingSource(out Profile profile, out AiLevel? computerLevel)
        {
            profile = null;
            computerLevel = null;
            while (true)
            {
                string source = ReadTournamentLine("选手来源（回车或 human = 真人；电脑输入 normal/hard/expert；q 保存退出）> ");
                if (source == null) return false;
                if (source.Length == 0 || source == "human") break;
                if (source == "normal") computerLevel = AiLevel.Normal;
                else if (source == "hard") computerLevel = AiLevel.Hard;
                else if (source == "expert") computerLevel = AiLevel.Expert;
                else
                {
                    Console.WriteLine("请输入 human（真人）、normal（普通电脑）、hard（困难电脑）或 expert（专家电脑）；不支持 easy。");
                    continue;
                }
                break;
            }
            Console.WriteLine("档案仅只读提取当前季“默认”赛制分数和场数，不保存来源路径。无档案真人采用 1500 分，电脑采用对应参考分；有档案但无排位记录采用 1500 分、0 场。");
            while (true)
            {
                string directory = ReadTournamentLine("可选档案目录（回车 = 无档案；q 保存退出）> ");
                if (directory == null) return false;
                if (directory.Length == 0) return true;
                try
                {
                    profile = FileSaveStore.LoadProfileSnapshot(directory, true);
                    // 严格读取先拒绝坏文件和权限错误，再区分缺档案与存在但未排位的档案。
                    try { File.GetAttributes(Path.Combine(Path.GetFullPath(directory), "profile.json")); }
                    catch (FileNotFoundException) { profile = null; }
                    catch (DirectoryNotFoundException) { profile = null; }
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                    || ex is ArgumentException || ex is FormatException || ex is InvalidOperationException
                    || ex is NotSupportedException)
                {
                    // 损坏档案不能冒充新选手；保留此前报名并允许改正来源或退出。
                    Console.WriteLine("档案读取失败，未添加本名选手，来源未修改：" + ex.Message);
                }
            }
        }

        private static void PrintTournamentRatingSnapshot(Tournament tournament, int player)
        {
            if (tournament.SeedMode != TournamentSeedMode.ByRating) return;
            var snapshot = tournament.RatingSeeds.Single(item => item.Player == player);
            Console.WriteLine("排位快照：" + TournamentPlayerName(tournament.Players[player])
                + "；" + snapshot.Rating + " 分；" + snapshot.Games + " 场；第 " + snapshot.Season + " 季；"
                + (snapshot.ComputerLevel.HasValue ? LevelName(snapshot.ComputerLevel.Value) + "电脑" : "真人"));
        }

        private static void PrintTournamentRatingOrder(Tournament tournament)
        {
            if (tournament.SeedMode != TournamentSeedMode.ByRating) return;
            Console.WriteLine("排种子方式：按报名时排位快照，分数降序、场数降序、报名顺序。");
            int rank = 0;
            foreach (int player in tournament.EffectiveSeedOrder)
            {
                Console.Write("种子 " + (++rank) + "：");
                PrintTournamentRatingSnapshot(tournament, player);
            }
        }
    }
}
