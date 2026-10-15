using System.IO;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static class ProfileStore
    {
        public static PlayerRating LoadRating(string directory)
        {
            string path = Path.Combine(directory, "profile.json");
            var profile = new Profile();
            if (File.Exists(path))
            {
                // 开场只读旧或新完整快照，不能阻止另一局持锁后原子替换档案。
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var reader = new StreamReader(stream)) profile = Profile.FromJson(reader.ReadToEnd());
            }
            PlayerRating rating;
            return profile.Ratings.TryGetValue(Profile.DefaultRatingFormat, out rating) ? CopyRating(rating) : new PlayerRating();
        }

        public static Profile Record(Battle battle, string directory, int mySide = 0, PlayerRating opponentRating = null)
        {
            string path = Path.Combine(directory, "profile.json");
            Profile profile;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".profile.json.tmp");
            // 锁独立于临时文件，进程退出会释放锁，遗留临时文件不会阻止下一次保存。
            using (var profileLock = new FileStream(Path.Combine(directory, ".profile.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                bool created = false;
                try
                {
                    profile = File.Exists(path) ? Profile.FromJson(File.ReadAllText(path)) : new Profile();
                    profile.Record(battle, mySide);
                    if (opponentRating != null)
                    {
                        PlayerRating current;
                        var own = profile.Ratings.TryGetValue(Profile.DefaultRatingFormat, out current)
                            ? CopyRating(current) : new PlayerRating();
                        // 双方自报分只用于本机显示，不防作弊；对方快照不能作为本方档案保存。
                        var opponent = CopyRating(opponentRating);
                        double score = battle.Winner < 0 ? 0.5 : battle.Winner == mySide ? 1.0 : 0.0;
                        RatingSystem.Apply(own, opponent, score);
                        profile.Ratings[Profile.DefaultRatingFormat] = own;
                    }
                    using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        created = true;
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(profile.ToJson());
                    }
                    // 持锁到替换及清理完成，其他进程不能在读改写之间插入保存。
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    created = false;
                }
                finally
                {
                    if (created && File.Exists(temporary)) File.Delete(temporary);
                }
            }
            return profile;
        }

        private static PlayerRating CopyRating(PlayerRating rating)
        {
            return new PlayerRating { Rating = rating.Rating, Games = rating.Games };
        }
    }
}
