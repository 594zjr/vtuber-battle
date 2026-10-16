using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Cli
{
    internal sealed partial class CliOptions
    {
        public bool NewSeason;
        public bool RatingHistory;
        public bool RatingMode { get { return NewSeason || RatingHistory; } }

        private bool ReadRatingOption(string option)
        {
            if (option != "--new-season" && option != "--rating-history") return false;
            if ((option == "--new-season" && NewSeason) || (option == "--rating-history" && RatingHistory))
                throw new ArgumentException(option + " 不能重复指定。");
            if (option == "--new-season") NewSeason = true;
            else RatingHistory = true;
            return true;
        }

        private static void ValidateRatingOptions(CliOptions options, List<string> provided)
        {
            if (!options.RatingMode || options.Help) return;
            if (options.NewSeason && options.RatingHistory)
                throw new ArgumentException("--new-season 与 --rating-history 不能同时使用，请选择切换赛季或只读查询。");
            string command = options.NewSeason ? "--new-season" : "--rating-history";
            var unsupported = provided.Where(option => option != command && option != "--save-dir").Distinct().ToArray();
            if (unsupported.Length > 0)
                throw new ArgumentException(command + " 是独立的排位档案操作，只可搭配 --save-dir；不能与 "
                    + string.Join("、", unsupported) + " 同用。");
        }
    }
}
