using System;
using System.Globalization;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static bool ChooseTournamentBans(ContentDb db, Tournament tournament, Pairing pairing,
            TournamentFileStore file, bool automatic)
        {
            if (tournament.BanCount == 0) return true;
            if (pairing.Bans.Count > 0) PrintTournamentBans(db, tournament, pairing, "已保存禁用：");
            int side;
            while ((side = tournament.NextBanSide(pairing)) >= 0)
            {
                if (automatic) tournament.AutoChooseBan(pairing);
                else
                {
                    var battle = tournament.CreateBattle(pairing);
                    var legal = battle.LegalBanTargets(side);
                    var player = tournament.Players[side == 0 ? pairing.Player0 : pairing.Player1];
                    Console.WriteLine(TournamentPlayerName(player) + " 可禁用的对方原阵容：");
                    foreach (int index in legal)
                        Console.WriteLine("  " + (index + 1) + " · " + battle.Sides[1 - side].Team[index].Name);
                    while (true)
                    {
                        string value = ReadTournamentLine(TournamentPlayerName(player)
                            + " 禁用对方主播（原阵容号位 1～6，q 保存退出）> ");
                        if (value == null) return false;
                        int number;
                        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number)
                            && number >= 1 && number <= 6 && legal.Contains(number - 1))
                        {
                            tournament.ChooseBan(pairing, side, number - 1);
                            break;
                        }
                        Console.WriteLine("请输入上方可用的原阵容号位；已禁用的主播不能重复选择。");
                    }
                }
                // 每一步立即保存，退出或下一位输入中断时无需重新选择。
                file.Save(tournament);
            }
            PrintTournamentBans(db, tournament, pairing, "本场禁用：");
            return true;
        }

        private static void PrintTournamentBans(ContentDb db, Tournament tournament, Pairing pairing, string prefix)
        {
            Console.WriteLine(prefix + string.Join("、", pairing.Bans.Select(ban =>
                TournamentPlayerName(tournament.Players[ban.Side == 0 ? pairing.Player0 : pairing.Player1])
                + " 禁用 " + db.Streamer(ban.StreamerId).Name + "（原 " + (ban.TargetIndex + 1) + " 号位）")));
        }
    }
}
