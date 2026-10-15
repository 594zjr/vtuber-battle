using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public static class TeamCheck
    {
        private static StreamDuel.Core.Loc Loc { get { return StreamDuel.Core.Loc.Current; } }

        public static List<string> Validate(ContentDb db, string seasonId, IList<string> team)
        {
            var errors = new List<string>();
            SeasonDef season = null;
            if (seasonId != null && !db.Seasons.TryGetValue(seasonId, out season))
            {
                errors.Add(Loc.T("team.err.season", seasonId));
                return errors;
            }

            if (team == null || team.Count != 6)
            {
                errors.Add(Loc.T("team.err.size", team == null ? 0 : team.Count));
            }
            if (team == null) return errors;

            var seen = new HashSet<string>();
            var reportedDuplicates = new HashSet<string>();
            foreach (var id in team)
            {
                if (!seen.Add(id) && reportedDuplicates.Add(id))
                {
                    var streamer = db.Streamers.FirstOrDefault(s => s.Id == id);
                    errors.Add(Loc.T("team.err.duplicate", streamer == null ? id ?? Loc.T("team.id.empty") : streamer.Name));
                }
            }

            var members = new List<StreamerDef>();
            foreach (var id in team)
            {
                var streamer = db.Streamers.FirstOrDefault(s => s.Id == id);
                if (streamer == null) errors.Add(Loc.T("team.err.missing", id ?? Loc.T("team.id.empty")));
                else members.Add(streamer);
            }
            if (season == null) return errors;

            foreach (var streamer in members.Distinct())
            {
                if (season.Banned.Contains(streamer.Id)) errors.Add(Loc.T("team.err.banned", streamer.Name, season.Name));
            }
            for (int i = 0; i < season.Limited.Count; i++)
            {
                var pool = season.Limited[i];
                var selected = members.Where(s => pool.Ids.Contains(s.Id)).ToList();
                if (selected.Count > pool.Max)
                {
                    var poolNames = pool.Ids.Select(id => db.Streamer(id).Name);
                    errors.Add(Loc.T("team.err.limited", season.Name, i + 1, string.Join(Loc.T("team.list.separator"), poolNames),
                        pool.Max, selected.Count, string.Join(Loc.T("team.list.separator"), selected.Select(s => s.Name))));
                }
            }
            return errors;
        }
    }
}
