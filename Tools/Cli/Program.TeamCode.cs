using System;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static TeamPreset ReadTeamCode(CliOptions options)
        {
            if (options.TeamCode == null) return null;
            TeamPreset preset;
            try
            {
                preset = TeamCode.Decode(options.TeamCode);
            }
            catch (FormatException ex)
            {
                throw new FormatException("队伍码无效：" + ex.Message, ex);
            }
            if (!options.PackSpecified) options.Pack = preset.PackId;
            return preset;
        }

        private static TeamPreset PrepareTeam(ContentDb db, CliOptions options, TeamPreset imported)
        {
            var preset = imported ?? new TeamPreset
            {
                Name = "我的队伍",
                PackId = db.PackId,
                Ids = (options.Team ?? TeamBuilder.Suggest(db, null, new string[0], options.Seed).ToArray()).ToList()
            };
            var errors = preset.Check(db, null);
            if (errors.Count > 0)
                throw new ArgumentException((imported == null ? "" : "队伍码无效：") + string.Join("；", errors));
            return preset;
        }
    }
}
