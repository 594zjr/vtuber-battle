using System;
using System.IO;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static int PrintComparison(CliOptions options)
        {
            string content = Path.Combine(AppContext.BaseDirectory, "Content");
            var policy = PackPolicy.Load(Path.Combine(content, "PackPolicy.json"));
            // 先查两位原始编号，保留已下架及过滤后不足六人的明确原因。
            string disabled = policy.CheckTeam(RequestedPackId(options), new[] { options.CompareA, options.CompareB });
            if (disabled != null) throw new ArgumentException(disabled);
            var db = LoadPack(content, options, policy);
            Console.Write(StreamerCompare.Build(db, options.CompareA, options.CompareB).ToText());
            return 0;
        }
    }
}
