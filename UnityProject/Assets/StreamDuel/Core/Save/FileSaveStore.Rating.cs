using System;
using System.Text;

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
            string json;
            try
            {
                // 手动切季不能把损坏字节替换后覆盖来源，普通读档继续保留旧容错行为。
                json = ReadOptional("profile.json", true);
            }
            catch (DecoderFallbackException ex)
            {
                throw new FormatException("档案文件不是有效的 UTF-8 文本，原文件已保留。", ex);
            }
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
