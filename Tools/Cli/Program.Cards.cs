using System;
using System.IO;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static int PrintCards(CliOptions options)
        {
            string content = Path.Combine(AppContext.BaseDirectory, "Content");
            var policy = PackPolicy.Load(Path.Combine(content, "PackPolicy.json"));
            if (options.CardId != null)
            {
                // 原名单先过政策，避免过滤后不足六人的包把下架主播误报成未安装。
                string disabled = policy.CheckTeam(RequestedPackId(options), new[] { options.CardId });
                if (disabled != null) throw new ArgumentException(disabled);
            }
            var db = LoadPack(content, options, policy);
            var ids = options.CardId != null ? new[] { options.CardId } : db.Streamers.Select(s => s.Id).ToArray();
            var cards = ids.Select(id => StreamerCard.Build(db, id).ToText()).ToArray();
            Console.Write(string.Join("\n", cards));
            return 0;
        }
    }
}
