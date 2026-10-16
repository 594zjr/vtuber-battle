using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Cli
{
    internal sealed partial class CliOptions
    {
        public int? SpectatePort;
        public string WatchHost;
        public int WatchPort;
        public string WatchName = "观众";
        public bool SpectatorChat = true;

        static void ValidateSpectatorOptions(CliOptions options, List<string> provided)
        {
            if (options.Help) return;
            if (options.WatchHost != null)
            {
                var allowed = new HashSet<string>(StringComparer.Ordinal)
                {
                    "--watch", "--watch-name", "--pack", "--packs", "--pack-dir", "--hash", "--list-packs", "--help", "-h"
                };
                var unsupported = provided.Where(option => !allowed.Contains(option)).Distinct().ToArray();
                if (unsupported.Length > 0)
                    throw new ArgumentException("--watch 只读观战，不支持 " + string.Join("、", unsupported) + "。");
            }
            if (provided.Contains("--watch-name") && options.WatchHost == null)
                throw new ArgumentException("--watch-name 只能与 --watch 一起使用。");
            if (provided.Contains("--spectator-chat") && !options.SpectatePort.HasValue)
                throw new ArgumentException("--spectator-chat 只能与 --spectate-port 一起使用。");
            if (options.SpectatePort.HasValue)
            {
                if (!options.HostPort.HasValue && options.LanHostName == null && options.RulesetId == null)
                    throw new ArgumentException("--spectate-port 只能与 --host、--lan-host 或 --ruleset 一起使用。");
                if (options.SpectatePort.Value != 0 && options.SpectatePort == options.HostPort)
                    throw new ArgumentException("--spectate-port 必须与 --host 使用不同端口；0 可分别自动分配。");
            }
        }
    }
}
