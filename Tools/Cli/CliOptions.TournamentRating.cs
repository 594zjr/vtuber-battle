using System;

namespace StreamDuel.Cli
{
    internal sealed partial class CliOptions
    {
        public bool TournamentSeedByRating;

        private bool ReadTournamentSeedOption(string[] args, ref int index)
        {
            if (args[index] != "--seed-by") return false;
            if (TournamentSeedByRating) throw new ArgumentException("--seed-by 不能重复指定。");
            string value = ReadAnalysisValue(args, ref index, "--seed-by", "排种子方式 rating");
            if (value != "rating") throw new ArgumentException("--seed-by 只支持 rating（按排位分排种子）。");
            TournamentSeedByRating = true;
            return true;
        }

        private static void ValidateTournamentSeedOptions(CliOptions options)
        {
            if (!options.TournamentSeedByRating || options.Help) return;
            if (options.TournamentSeed.HasValue)
                throw new ArgumentException("--seed-by rating 不能与 --tournament-seed 同用，请选择按排位快照或随机排种子。");
            if (options.TournamentFile == null)
                throw new ArgumentException("--seed-by 只能与 --tournament 同用，不能用于普通对战或赛事战报查询。");
        }
    }
}
