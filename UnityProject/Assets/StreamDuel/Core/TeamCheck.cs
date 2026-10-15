using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public static class TeamCheck
    {
        public static List<string> Validate(ContentDb db, string seasonId, IList<string> team)
        {
            var errors = new List<string>();
            SeasonDef season = null;
            if (seasonId != null && !db.Seasons.TryGetValue(seasonId, out season))
            {
                errors.Add("赛季不存在：" + seasonId);
                return errors;
            }

            if (team == null || team.Count != 6)
            {
                errors.Add("队伍需要正好 6 位主播，当前为 " + (team == null ? 0 : team.Count) + " 位");
            }
            if (team == null) return errors;

            var seen = new HashSet<string>();
            var reportedDuplicates = new HashSet<string>();
            foreach (var id in team)
            {
                if (!seen.Add(id) && reportedDuplicates.Add(id))
                {
                    var streamer = db.Streamers.FirstOrDefault(s => s.Id == id);
                    errors.Add((streamer == null ? id ?? "空主播编号" : streamer.Name) + " 在队伍中重复");
                }
            }

            var members = new List<StreamerDef>();
            foreach (var id in team)
            {
                var streamer = db.Streamers.FirstOrDefault(s => s.Id == id);
                if (streamer == null) errors.Add("主播不存在：" + (id ?? "空主播编号"));
                else members.Add(streamer);
            }
            if (season == null) return errors;

            foreach (var streamer in members.Distinct())
            {
                if (season.Banned.Contains(streamer.Id)) errors.Add(streamer.Name + " 在" + season.Name + "禁用池中");
            }
            for (int i = 0; i < season.Limited.Count; i++)
            {
                var pool = season.Limited[i];
                var selected = members.Where(s => pool.Ids.Contains(s.Id)).ToList();
                if (selected.Count > pool.Max)
                {
                    var poolNames = pool.Ids.Select(id => db.Streamer(id).Name);
                    errors.Add(season.Name + "限制池 " + (i + 1) + "（" + string.Join("、", poolNames) + "）最多允许 "
                        + pool.Max + " 位，当前选择 " + selected.Count + " 位：" + string.Join("、", selected.Select(s => s.Name)));
                }
            }
            return errors;
        }
    }
}
