using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static class ProfileStore
    {
        public static Profile Record(Battle battle, string directory, int mySide = 0)
        {
            return new FileSaveStore(directory).RecordProfile(battle, mySide);
        }
    }
}
