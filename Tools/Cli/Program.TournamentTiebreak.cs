using System;
using System.IO;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static void ValidateTournamentTiebreakOptions(CliOptions options, string format)
        {
            if ((options.TournamentSeed.HasValue || options.NoAutoTiebreak)
                && format != Tournament.SingleElimination && format != Tournament.DoubleElimination)
                throw new ArgumentException("--tournament-seed 和 --no-auto-tiebreak 仅适用于单败或双败淘汰赛；原文件未修改。");
        }

        private static bool RunTournamentTiebreak(Tournament tournament, Pairing pairing, TournamentFileStore file)
        {
            // 每次最多原场加三次加赛；归档失败立即退出，避免相同种子无限重跑。
            for (int attempt = pairing.Games.Count; attempt <= 3 && !pairing.WinnerSide.HasValue; attempt++)
            {
                Console.WriteLine("准备对局 " + (pairing.Id + 1) + " 的" + (attempt == 0 ? "原场" : "加赛 " + attempt)
                    + "；种子：" + tournament.GameSeed(pairing, attempt));
                var battle = PlayAutomaticTournamentGame(tournament.CreateGame(pairing));
                try
                {
                    var library = new ReplayLibrary(TournamentLibraryPath(file.Path), policy: battle.Db.Policy);
                    string id = library.SaveTournamentGame(tournament, pairing, battle, attempt);
                    Console.WriteLine("赛事本局录像已保存：" + id);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                    || ex is ArgumentException || ex is FormatException || ex is InvalidOperationException || ex is KeyError)
                {
                    Console.Error.WriteLine("错误：赛事本局录像归档失败，本局结果未登记、加赛序号未前进；修复保存问题后可使用 --auto 继续：" + ex.Message);
                    return false;
                }
                // 存档写入前中断时，重跑同局会得到同一录像；库归档按对阵和加赛序号幂等。
                tournament.ReportGame(pairing, battle);
                file.Save(tournament);
                Console.WriteLine("已保存本局结果：" + (battle.Winner < 0 ? "平局" : "第 " + battle.Winner + " 方获胜")
                    + "；已归档 " + pairing.Games.Count + " 局。");
            }
            if (!pairing.WinnerSide.HasValue)
                throw new InvalidOperationException("加赛记录未产生晋级结果，已停止继续对战。");
            if (pairing.AdvancedBySeed) Console.WriteLine("加赛 3 次仍平，按种子顺位晋级。");
            return true;
        }
    }
}
