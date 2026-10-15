using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public static class MixedBattle
    {
        /// <summary>按种子随机分配两支六人队伍，双方之间也不重复，不改变内容库。</summary>
        public static string[][] RandomTeams(ContentDb merged, uint seed)
        {
            if (merged == null) throw new ArgumentNullException(nameof(merged));
            if (merged.Streamers == null) throw new KeyError("混合内容包缺少主播名单");
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var streamer in merged.Streamers)
            {
                if (streamer == null || string.IsNullOrWhiteSpace(streamer.Id))
                    throw new KeyError("混合内容包包含空主播 id");
                if (!unique.Add(streamer.Id))
                    throw new KeyError("混合内容包主播 id 重复：" + streamer.Id);
            }
            string unavailable = merged.Policy.CheckTeam(merged.PackId, unique);
            if (unavailable != null) throw new PackDisabledException(unavailable);
            if (unique.Count < 12) throw new KeyError("随机组队至少需要 12 位不同主播，当前只有 " + unique.Count + " 位");

            // 先固定顺序，使包的传入顺序和列表枚举顺序不影响同一种子的分配。
            var ids = unique.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var rng = new Rng(seed);
            for (int i = ids.Length - 1; i > 0; i--)
            {
                int j = (int)(rng.Next() * (i + 1));
                string swap = ids[i];
                ids[i] = ids[j];
                ids[j] = swap;
            }
            return new[] { ids.Take(6).ToArray(), ids.Skip(6).Take(6).ToArray() };
        }
    }
}
