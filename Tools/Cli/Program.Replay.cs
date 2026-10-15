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
            var policy = PackPolicy.LoadDefault();
            BattleRecord record;
            try
            {
                record = options.ReplayCode != null ? ShareCode.Decode(options.ReplayCode)
                    : LoadReplay(options, policy);
            }
            catch (IndexOutOfRangeException ex)
            {
                throw new FormatException("回放 JSON 不完整。", ex);
            }
            string disabled = policy.CheckTeam(record.PackId, record.Teams?.Where(team => team != null).SelectMany(team => team));
            if (disabled != null) throw new InvalidOperationException(disabled);
            var db = LoadPack(Path.Combine(AppContext.BaseDirectory, "Content"),
                new CliOptions { Pack = record.PackId, PackDirectory = options.PackDirectory }, policy);
            foreach (var team in record.Teams)
            {
                var errors = TeamCheck.Validate(db, null, team);
                if (errors.Count > 0) throw new ArgumentException("回放原始队伍无效：" + string.Join("；", errors));
            }
            // 让 Core 恢复记录中的随机流，显示层只按日志回合翻页。
            var compatibility = Replay.Check(db, record);
            if (!compatibility.CanReplay) throw new InvalidOperationException(compatibility.Reason);
            Battle battle;
            bool approximateReplay = false;
            if (options.ReplayBestEffort)
            {
                if (!string.IsNullOrEmpty(compatibility.Reason))
                {
                    approximateReplay = true;
                    Console.WriteLine("提示：" + compatibility.Reason);
                }
                battle = Replay.RunBestEffort(db, record);
            }
            else battle = Replay.Run(db, record);
            battle = TextInput.ForDisplay(battle);
            foreach (string name in record.Names ?? Array.Empty<string>())
            {
                var input = TextInput.Clean(name, TextInputKind.PlayerName);
                if (input.Changed) Console.WriteLine("昵称显示已调整：" + input.Message);
            }
            Console.WriteLine("开播对决 · 对战回看");
            Console.WriteLine("内容包：" + db.PackName);
            Console.WriteLine(db.PackNotice);
            PrintMixedNotices(db, record.Teams.SelectMany(team => team));
            Console.WriteLine("种子：" + record.Seed);

            bool first = true;
            foreach (var turn in battle.Log.Select((entry, index) => new { Entry = entry, Index = index }).GroupBy(item => item.Entry.Turn))
            {
                if (!first && !options.Auto && !ReadReplayNext())
                {
                    Console.WriteLine("已退出回看。");
                    return 0;
                }
                first = false;
                string node = null;
                foreach (var item in turn)
                {
                    var entry = item.Entry;
                    if (entry.Node != node)
                    {
                        Console.WriteLine("【第 " + entry.Turn + " 回合 · " + entry.Node + "】");
                        node = entry.Node;
                    }
                    Console.WriteLine("  " + entry.Text);
                    PrintDamageExplanation(battle, item.Index, options.ExplainDamage);
                }
            }

            if (battle.Phase == Phase.Over)
            {
                Console.WriteLine("回看结束：" + (battle.Winner < 0 ? "平局" : battle.Sides[battle.Winner].Name + " 获胜"));
                PrintTeams(battle);
                Console.WriteLine(BattleSummary.From(battle).ToText());
                PrintReview(battle, 0);
            }
            else Console.WriteLine("记录播放完毕：这是一局未完成的对战。");
            bool exportFailed = false;
            try
            {
                SaveReport(battle, options, approximateReplay: approximateReplay);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                // 两种导出各自完成，战报目标不可写时仍可分享已回看的记录。
                Console.Error.WriteLine("错误：" + ex.Message);
                exportFailed = true;
            }
            try
            {
                PrintShareCode(record, options);
            }
            catch (FormatException ex)
            {
                Console.Error.WriteLine("错误：分享码导出失败：" + ex.Message);
                exportFailed = true;
            }
            return exportFailed ? 1 : 0;
        }

        private static void PrintShareCode(BattleRecord record, CliOptions options)
        {
            if (options.Share) Console.WriteLine("分享码：" + ShareCode.Encode(record));
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
