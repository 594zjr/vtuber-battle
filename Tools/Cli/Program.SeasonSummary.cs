using System;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static int PrintSeasonSummary(CliOptions options)
        {
            var profile = FileSaveStore.LoadProfileSnapshot(options.SaveDir, true);
            var library = ReplayLibrary.OpenReadOnly(ReplayLibraryDirectory(options));
            Console.WriteLine(SeasonSummary.Create(profile, library, options.SummarySeason).ToText());
            return 0;
        }
    }
}
