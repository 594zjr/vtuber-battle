using System;
using System.IO;
using System.Linq;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static int PlayBranch(CliOptions options)
        {
            var policy = PackPolicy.LoadDefault();
            BattleRecord record;
            try
            {
                string source = ValidateBranchSourcePath(options);
                record = source != null
                    ? BattleRecord.FromJson(File.ReadAllText(source, new UTF8Encoding(false, true)))
                    : ReplayLibrary.OpenReadOnly(ReplayLibraryDirectory(options), policy).Load(options.BranchReplay);
            }
            catch (IndexOutOfRangeException ex)
            {
                throw new FormatException("回放 JSON 不完整。", ex);
            }
            catch (DecoderFallbackException ex)
            {
                throw new FormatException("接手来源录像不是有效的 UTF-8 文本。", ex);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException("无法读取接手来源录像：" + ex.Message, ex);
            }
            if (record.Version > BattleRecord.CurrentVersion)
                throw new FormatException(Replay.Check(null, record, policy).Reason);
            string disabled = policy.CheckTeam(record.PackId,
                record.Teams?.Where(team => team != null).SelectMany(team => team));
            if (disabled != null) throw new InvalidOperationException(disabled);
            var db = LoadPack(Path.Combine(AppContext.BaseDirectory, "Content"),
                new CliOptions { Pack = record.PackId, PackDirectory = options.PackDirectory }, policy);
            var compatibility = Replay.Check(db, record, policy);
            if (!compatibility.CanReplay) throw new InvalidOperationException(compatibility.Reason);
            int humanSide = options.BranchSide.Value;
            var battle = ReplayBranch.Start(db, record, options.BranchTurn.Value, humanSide, options.BranchAi);

            Console.WriteLine("开播对决 · 从录像接手");
            Console.WriteLine("内容包：" + db.PackName);
            Console.WriteLine(db.PackNotice);
            Console.WriteLine("从第 " + options.BranchTurn.Value + " 回合开始；你操作第 " + humanSide + " 方（"
                + TextInput.Clean(battle.Sides[humanSide].Name, TextInputKind.PlayerName).Text
                + "）；电脑难度：" + LevelName(options.BranchAi));
            Console.WriteLine("此接手对局不计入正式战绩；来源录像保持不变。");
            PrintTeams(battle);
            PrintMixedNotices(db, record.Teams.SelectMany(team => team));
            PrintTerminationCommands();
            Console.WriteLine("继承的对方求和可用 /draw accept 同意，或 /draw reject 拒绝。");
            int logIndex = battle.Log.Count;
            Func<string, int?> readNumber = prompt => ReadNumber(prompt,
                line => HandleBranchCommand(battle, humanSide, line));
            try
            {
                while (battle.Phase != Phase.Over)
                {
                    if (battle.HasDrawOffer(humanSide))
                    {
                        bool accept = Ai.AcceptDraw(battle, 1 - humanSide);
                        battle.RespondDraw(1 - humanSide, accept);
                        Console.WriteLine(accept ? "电脑同意继承的和局提议。" : "电脑拒绝继承的和局提议，继续对战。");
                        PrintNewLog(battle, ref logIndex);
                        if (battle.Phase == Phase.Over) break;
                    }
                    if (battle.HasDrawOffer(1 - humanSide))
                        Console.WriteLine("对方有尚未回应的和局提议：/draw accept 同意，/draw reject 拒绝。");
                    Console.WriteLine();
                    Console.WriteLine("===== 第 " + battle.Turn + " 回合 =====");
                    PrintFighter(battle, 1 - humanSide);
                    PrintFighter(battle, humanSide);
                    if (battle.Phase == Phase.Replace)
                    {
                        for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                        {
                            if (!battle.Need[side]) continue;
                            int to;
                            if (side == humanSide)
                            {
                                var choice = ReadReplacement(battle, humanSide, readNumber);
                                if (!choice.HasValue) return Quit();
                                to = choice.Value;
                            }
                            else to = Ai.ChooseReplacement(battle, side, options.BranchAi);
                            battle.Replace(side, to);
                            PrintNewLog(battle, ref logIndex);
                        }
                        continue;
                    }
                    if (battle.Phase != Phase.Choose)
                        throw new InvalidOperationException("接手对局不在可操作的行动或补位阶段。");
                    var action = ReadAction(battle, humanSide, readNumber);
                    if (!action.HasValue) return Quit();
                    var opponent = Ai.Choose(battle, 1 - humanSide, options.BranchAi);
                    battle.ResolveTurn(humanSide == 0 ? action.Value : opponent,
                        humanSide == 1 ? action.Value : opponent);
                    PrintNewLog(battle, ref logIndex);
                }
            }
            catch (BattleFinishedException) { }
            PrintNewLog(battle, ref logIndex);
            return SaveBranch(battle, options);
        }

        private static string ValidateBranchSourcePath(CliOptions options)
        {
            if (!IsReplayFile(options.BranchReplay)) return null;
            string source, library;
            try
            {
                // 来源和目标库的扩展前缀都可能指向同一保留文件，比较与实际读取必须使用同一规范路径。
                source = ReportPath(options.BranchReplay, "接手");
                library = ReportPath(ReplayLibraryDirectory(options), "接手");
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw new ArgumentException("接手来源或录像库路径无效：" + ex.Message, ex);
            }
            for (string current = source; !string.IsNullOrEmpty(Path.GetDirectoryName(current)); current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new ArgumentException("接手来源路径不能包含符号链接、目录联接或其他重解析点；请先复制为普通录像文件。");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (string name in new[] { "index.json", ".library-write.tmp", ".library.lock" })
                if (string.Equals(source, Path.Combine(library, name), comparison))
                    throw new ArgumentException("接手来源不能使用目标录像库的索引、写入临时文件或锁文件路径；请先将录像复制到普通文件。");
            return source;
        }

        private static bool HandleBranchCommand(Battle battle, int side, string line)
        {
            string command = line.Trim();
            bool accept = command.Equals("/draw accept", StringComparison.OrdinalIgnoreCase);
            if (!accept && !command.Equals("/draw reject", StringComparison.OrdinalIgnoreCase))
                return HandleLocalCommand(battle, side, line);
            if (!battle.HasDrawOffer(1 - side))
                Console.WriteLine("当前没有对方的和局提议需要回应。");
            else
            {
                battle.RespondDraw(side, accept);
                Console.WriteLine(accept ? "已同意和局。" : "已拒绝和局，继续选择。");
                if (battle.Phase == Phase.Over) throw new BattleFinishedException();
            }
            return true;
        }

        private static int SaveBranch(Battle battle, CliOptions options)
        {
            var display = TextInput.ForDisplay(battle);
            Console.WriteLine();
            Console.WriteLine("接手对局结束：" + (display.Winner < 0 ? "平局" : display.Sides[display.Winner].Name + " 获胜"));
            PrintTeams(display);
            Console.WriteLine(BattleSummary.From(display).ToText());
            string directory = ReplayLibraryDirectory(options);
            var source = battle.Record.Branch;
            // 来源可能就在同一库中，接手保存不能触发容量清理而删除它。
            try
            {
                new ReplayLibrary(directory, maxCount: int.MaxValue, policy: battle.Db.Policy).Save(
                    battle.Record, BattleSummary.From(battle), "接手第 " + source.Turn + " 回合，来源 " + source.SourceHash);
            }
            catch (ReplayLibraryMaintenanceException ex)
            {
                throw new IOException("接手录像已写入，但录像库索引更新失败：" + ex.Message, ex);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException("无法保存接手录像：" + ex.Message, ex);
            }
            Console.WriteLine("接手录像已保存到：" + directory);
            Console.WriteLine("未累计正式战绩；本次保存保留所有旧录像。");
            return 0;
        }
    }
}
