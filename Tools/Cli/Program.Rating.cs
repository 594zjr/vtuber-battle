using System;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static int RunRatingCommand(CliOptions options)
        {
            if (options.NewSeason)
            {
                var profile = new FileSaveStore(options.SaveDir).NewRatingSeason();
                var rating = profile.Ratings[Profile.DefaultRatingFormat];
                Console.WriteLine("已开始第 " + rating.Season + " 赛季；旧赛季已归档。");
                PrintRatingHistory(Profile.DefaultRatingFormat, rating);
            }
            else
            {
                var profile = FileSaveStore.LoadProfileSnapshot(options.SaveDir, true);
                if (profile.Ratings.Count == 0)
                    PrintRatingHistory(Profile.DefaultRatingFormat, new PlayerRating());
                else
                    foreach (string format in profile.Ratings.Keys.OrderBy(key => key, StringComparer.Ordinal))
                        PrintRatingHistory(format, profile.Ratings[format]);
            }
            return 0;
        }

        private static void PrintRatingHistory(string format, PlayerRating rating)
        {
            Console.WriteLine("赛制：" + TextInput.Clean(format, TextInputKind.Note).Text);
            Console.WriteLine("当前第 " + rating.Season + " 赛季：" + rating.Rating + " 分 · "
                + RatingSystem.TierOf(rating.Rating).Name + " · 最高 " + rating.PeakRating + " 分 · "
                + rating.Games + " 局 · " + (rating.IsPlaced ? "已定级" : "定级中"));
            if (rating.History.Count == 0)
            {
                Console.WriteLine("暂无已结束赛季。");
                return;
            }
            Console.WriteLine("已结束赛季：");
            foreach (var season in rating.History)
                Console.WriteLine("第 " + season.Season + " 赛季：" + season.FinalRating + " 分 · "
                    + season.Tier + " · 最高 " + season.PeakRating + " 分 · " + season.Games + " 局");
        }
    }
}
