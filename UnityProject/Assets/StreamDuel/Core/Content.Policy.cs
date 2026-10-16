using System.Collections.Generic;

namespace StreamDuel.Core
{
    public sealed partial class ContentDb
    {
        public PackPolicy Policy { get; private set; } = PackPolicy.Empty;

        public static ContentDb FromJson(string rulesJson, string packJson, PackPolicy policy)
        {
            policy = policy ?? PackPolicy.Empty;
            var pack = MiniJson.Parse(packJson) as Dictionary<string, object>;
            string id = pack != null && pack.TryGetValue("id", out object value) ? value as string : null;
            string reason = policy.PackReason(id);
            if (reason != null) throw new PackDisabledException(reason);
            var db = FromJson(rulesJson, packJson);
            db.Policy = policy;
            int removed = db.Streamers.RemoveAll(streamer => policy.IsStreamerDisabled(streamer.Id));
            if (removed == 0) return db;
            if (db.Streamers.Count < 6)
                throw new PackDisabledException("内容包「" + db.PackId + "」因主播下架后不足 6 位，已隐藏。");
            // 赛季引用随可用名单收缩，保留限制上限，不替玩家选择新成员。
            foreach (var season in db.Seasons.Values)
            {
                season.Banned.RemoveAll(policy.IsStreamerDisabled);
                foreach (var pool in season.Limited) pool.Ids.RemoveAll(policy.IsStreamerDisabled);
            }
            // 初次加载已经验证过全部引用，下架只清理被移除主播的显示覆盖。
            db.Art.RemoveStreamers(policy.IsStreamerDisabled);
            db.Validate();
            return db;
        }
    }
}
