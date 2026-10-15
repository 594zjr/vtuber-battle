using System;
using System.IO;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static string ReplayLibraryDirectory(CliOptions options)
        {
            return Path.Combine(options.SaveDir, "replay-library");
        }

        private static bool IsReplayFile(string value)
        {
            // 原有文件路径继续可用；不含路径信息且不存在的名称交给录像库验证编号。
            return File.Exists(value) || Path.IsPathRooted(value)
                || value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0
                || value.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        }

        private static BattleRecord LoadReplay(CliOptions options)
        {
            if (IsReplayFile(options.ReplayFile))
                return BattleRecord.FromJson(File.ReadAllText(options.ReplayFile));
            return new ReplayLibrary(ReplayLibraryDirectory(options)).Load(options.ReplayFile);
        }

        private static int ListReplays(CliOptions options)
        {
            var entries = new ReplayLibrary(ReplayLibraryDirectory(options)).List();
            Console.WriteLine("本地录像库：" + ReplayLibraryDirectory(options));
            if (entries.Count == 0) Console.WriteLine("暂无录像。");
            foreach (var entry in entries)
            {
                Console.WriteLine("编号：" + OneLine(entry.Id));
                if (entry.IsCorrupt)
                {
                    Console.WriteLine("  已损坏：" + OneLine(entry.Error));
                    continue;
                }
                Console.WriteLine("  时间：" + entry.Date.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
                Console.WriteLine("  内容包：" + OneLine(entry.PackId));
                string first = OneLine(entry.Names[0]), second = OneLine(entry.Names[1]);
                Console.WriteLine("  双方：" + first + " / " + second);
                for (int side = 0; side < 2; side++)
                    Console.WriteLine("  " + (side == 0 ? first : second) + "阵容："
                        + OneLine(string.Join("、", entry.Teams[side])));
                Console.WriteLine("  胜方：" + (entry.Winner < 0 ? "平局" : entry.Winner == 0 ? first : second)
                    + "；回合：" + entry.Turns);
                Console.WriteLine("  备注：" + (string.IsNullOrEmpty(entry.Note) ? "无" : OneLine(entry.Note)));
            }
            return 0;
        }

        private static string OneLine(string value)
        {
            return (value ?? "").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
