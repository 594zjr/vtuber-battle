using System;
using System.IO;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static Ruleset TournamentRuleset(CliOptions options)
        {
            return options.TournamentRulesetId == null ? null
                : Rulesets.All.First(rule => rule.Id == options.TournamentRulesetId);
        }

        private static void ValidateSavedTournamentRuleset(CliOptions options, Tournament tournament)
        {
            if (options.TournamentRulesetId == null) return;
            var requested = TournamentRuleset(options);
            if (!tournament.HasRuleset || tournament.Ruleset.ToJson() != requested.ToJson())
                throw new ArgumentException("--tournament-ruleset 与已保存的规则快照不一致，不能更改续赛规则或升级旧赛事；原文件未修改。");
        }

        private static void PrintTournamentRuleset(Tournament tournament)
        {
            var ruleset = tournament.Ruleset;
            Console.WriteLine("逐局赛制：" + ruleset.Name + "（" + ruleset.Id + "）；常规最多 " + ruleset.BestOf
                + " 局，先赢 " + (ruleset.BestOf / 2 + 1) + " 局完成本场。");
            Console.WriteLine("每局重新使用原报名队伍；禁用：" + (ruleset.BanOne ? "开启" : "关闭")
                + "；首发选择：" + (ruleset.LeadPick ? "开启" : "关闭") + "。");
            Console.WriteLine("赛事自动代打即时使用普通 AI；保存计时配置，不按墙钟截断 AI 行动。");
            Console.WriteLine("常规局打满后的待决定情形会保留逐局记录并暂停；淘汰赛同分时最多加赛 3 次，仍平按种子顺位晋级。");
        }

        private static bool RunTournamentRuleset(Tournament tournament, Pairing pairing, TournamentFileStore file)
        {
            while (!pairing.WinnerSide.HasValue)
            {
                string pending = tournament.PendingDecision(pairing);
                if (!string.IsNullOrEmpty(pending))
                {
                    Console.Error.WriteLine(pending + " 已保存所有已完成局，本场保持待赛，不继续生成对局。");
                    return false;
                }
                int game = pairing.Games.Count;
                if (game >= tournament.BestOf + 3)
                    throw new InvalidOperationException("逐局记录超过赛事上限但仍未裁定，已停止继续对战。");
                Console.WriteLine("准备对局 " + (pairing.Id + 1) + " 的"
                    + (game < tournament.BestOf ? "常规第 " + (game + 1) + " 局" : "加赛 " + (game - tournament.BestOf + 1))
                    + "；种子：" + tournament.GameSeed(pairing, game));
                var battle = PlayAutomaticTournamentGame(tournament.CreateGame(pairing));
                try
                {
                    var library = new ReplayLibrary(TournamentLibraryPath(file.Path), policy: battle.Db.Policy);
                    string id = library.SaveTournamentGame(tournament, pairing, battle, game);
                    Console.WriteLine("赛事本局录像已保存：" + id);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                    || ex is ArgumentException || ex is FormatException || ex is InvalidOperationException || ex is KeyError)
                {
                    Console.Error.WriteLine("错误：赛事本局录像归档失败，本局结果未登记、局序未前进；修复保存问题后可使用 --auto 继续：" + ex.Message);
                    return false;
                }
                // 归档完成但尚未保存赛事时中断，续赛仍重跑同一局并复用原录像。
                tournament.ReportGame(pairing, battle);
                file.Save(tournament);
                Console.WriteLine("已保存本局结果：" + (battle.Winner < 0 ? "平局" : "第 " + battle.Winner + " 方获胜")
                    + "；已归档 " + pairing.Games.Count + " 局。");
            }
            if (pairing.AdvancedBySeed) Console.WriteLine("加赛 3 次仍平，按种子顺位晋级。");
            return true;
        }
    }
}
