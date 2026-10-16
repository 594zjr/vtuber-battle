using System;

namespace StreamDuel.Core
{
    public sealed partial class FileSaveStore
    {
        public static Profile LoadProfileSnapshot(string rootDirectory, bool rejectInvalid)
        {
            var store = new FileSaveStore(rootDirectory, false);
            return rejectInvalid ? store.LoadProfileStrict() : store.LoadProfile();
        }

        private Profile LoadProfileStrict()
        {
            string json = ReadOptional("profile.json");
            return json == null ? new Profile() : Profile.FromJsonStrict(json);
        }

        public Profile NewRatingSeason(string format = Profile.DefaultRatingFormat)
        {
            if (string.IsNullOrWhiteSpace(format)) throw new ArgumentException("排位赛制名称不能为空。", nameof(format));
            // 切季和完局共享档案锁，避免用开局时的快照覆盖另一进程已经保存的新赛季。
            using (OpenLock(".profile.lock"))
            {
                var profile = LoadProfileStrict();
                PlayerRating current;
                var rating = profile.Ratings.TryGetValue(format, out current) ? current.Clone() : new PlayerRating();
                RatingSystem.NewSeason(rating);
                profile.Ratings[format] = rating;
                WriteAtomic("profile.json", ".profile.json.tmp", profile.ToJson());
                return profile;
            }
        }
    }
}
