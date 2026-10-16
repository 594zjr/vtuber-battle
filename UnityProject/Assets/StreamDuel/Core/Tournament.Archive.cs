using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace StreamDuel.Core
{
    public sealed partial class Tournament
    {
        public IReadOnlyList<Pairing> Pairings { get { return pairings.AsReadOnly(); } }

        internal ContentDb ReportDb { get { return db; } }
        internal string ReportPackId { get { return packId; } }
        internal string ReportContentHash { get { return contentHash; } }

        internal string ReportKey
        {
            get
            {
                // 结果和已排轮次会变化，关联键只取赛事配置与按报名顺序保存的原始输入。
                var text = new StringBuilder(IsElimination ? "{\"version\":2,\"format\":" : "{\"version\":1,\"format\":");
                TeamPresetJson.WriteString(text, Format);
                if (IsElimination) WriteEliminationConfiguration(text);
                text.Append(",\"rounds\":").Append(requestedRounds.ToString(CultureInfo.InvariantCulture));
                if (BanCount > 0) text.Append(",\"banCount\":").Append(BanCount.ToString(CultureInfo.InvariantCulture));
                text.Append(",\"packId\":");
                TeamPresetJson.WriteString(text, packId);
                text.Append(",\"contentHash\":");
                TeamPresetJson.WriteString(text, contentHash);
                text.Append(",\"players\":[");
                for (int i = 0; i < players.Count; i++)
                {
                    if (i > 0) text.Append(',');
                    var player = players[i];
                    text.Append("{\"id\":").Append(player.Id.ToString(CultureInfo.InvariantCulture)).Append(",\"name\":");
                    TeamPresetJson.WriteString(text, player.Name);
                    text.Append(",\"teamCode\":");
                    TeamPresetJson.WriteString(text, player.TeamCode);
                    text.Append('}');
                }
                text.Append("]}");
                using (var hash = SHA256.Create())
                {
                    string domain = IsElimination ? "StreamDuel.TournamentArchive.v2\n" : "StreamDuel.TournamentArchive.v1\n";
                    byte[] digest = hash.ComputeHash(Encoding.UTF8.GetBytes(domain + text));
                    var result = new StringBuilder(64);
                    foreach (byte value in digest) result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                    return result.ToString();
                }
            }
        }
    }
}
