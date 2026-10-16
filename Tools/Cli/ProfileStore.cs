using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static class ProfileStore
    {
        public static PlayerRating LoadRating(string directory)
        {
            var profile = FileSaveStore.LoadProfileSnapshot(directory);
            PlayerRating rating;
            return profile.Ratings.TryGetValue(Profile.DefaultRatingFormat, out rating) ? CopyRating(rating) : new PlayerRating();
        }

        public static Profile Record(Battle battle, string directory, int mySide = 0, PlayerRating opponentRating = null)
        {
            return new FileSaveStore(directory).RecordProfile(battle, mySide, opponentRating);
        }

        private static PlayerRating CopyRating(PlayerRating rating)
        {
            return rating.Clone();
        }
    }
}
