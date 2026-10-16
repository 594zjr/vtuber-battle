using System;

namespace StreamDuel.Cli
{
    internal sealed partial class CliOptions
    {
        public int? TournamentBans;

        private bool ReadTournamentBanOption(string[] args, ref int index)
        {
            if (args[index] != "--bans") return false;
            if (TournamentBans.HasValue) throw new ArgumentException("--bans 不能重复指定。");
            if (++index >= args.Length || args[index] == null
                || args[index].StartsWith("--", StringComparison.Ordinal) || args[index] == "-h")
                throw new ArgumentException("--bans 缺少参数值，只能指定 0、1 或 2。");
            string value = args[index];
            if (value != "0" && value != "1" && value != "2")
                throw new ArgumentException("--bans 只能是 0、1 或 2，表示每场每方禁用对手的人数。");
            TournamentBans = value[0] - '0';
            return true;
        }

        private static void ValidateTournamentBanOptions(CliOptions options)
        {
            if (options.TournamentBans.HasValue && options.TournamentFile == null && !options.Help)
                throw new ArgumentException("--bans 只能与 --tournament 同用；普通对局的一人禁用请使用 --ban。");
        }
    }
}
