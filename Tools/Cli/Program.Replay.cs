using System;
using System.IO;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static int PlayReplay(CliOptions options)
        {
            BattleRecord record;
            try
            {
                record = BattleRecord.FromJson(File.ReadAllText(options.ReplayFile));
            }
            catch (IndexOutOfRangeException ex)
            {
                throw new FormatException("回放 JSON 不完整。", ex);
            }
            var db = LoadPack(Path.Combine(AppContext.BaseDirectory, "Content"),
                new CliOptions { Pack = record.PackId, PackDirectory = options.PackDirectory });
            foreach (var team in record.Teams)
            {
                var errors = TeamCheck.Validate(db, null, team);
                if (errors.Count > 0) throw new ArgumentException("回放原始队伍无效：" + string.Join("；", errors));
            }
            // 让 Core 恢复记录中的随机流，显示层只按日志回合翻页。
            var battle = Replay.Run(db, record);
            Console.WriteLine("开播对决 · 对战回看");
            Console.WriteLine("内容包：" + db.PackName);
            Console.WriteLine(db.PackNotice);
            Console.WriteLine("种子：" + record.Seed);

            bool first = true;
            foreach (var turn in battle.Log.GroupBy(entry => entry.Turn))
            {
                if (!first && !options.Auto && !ReadReplayNext())
                {
                    Console.WriteLine("已退出回看。");
                    return 0;
                }
                first = false;
                string node = null;
                foreach (var entry in turn)
                {
                    if (entry.Node != node)
                    {
                        Console.WriteLine("【第 " + entry.Turn + " 回合 · " + entry.Node + "】");
                        node = entry.Node;
                    }
                    Console.WriteLine("  " + entry.Text);
                }
            }

            if (battle.Phase == Phase.Over)
            {
                Console.WriteLine("回看结束：" + (battle.Winner < 0 ? "平局" : battle.Sides[battle.Winner].Name + " 获胜"));
                PrintTeams(battle);
                Console.WriteLine(BattleSummary.From(battle).ToText());
            }
            else Console.WriteLine("记录播放完毕：这是一局未完成的对战。");
            SaveReport(battle, options);
            return 0;
        }

        private static bool ReadReplayNext()
        {
            while (true)
            {
                Console.Write("回车播放下一回合，q 退出 > ");
                string line = Console.ReadLine();
                if (line == null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) return false;
                if (line.Trim().Length == 0) return true;
                Console.WriteLine("请直接按回车，或输入 q 退出。");
            }
        }
    }
}
