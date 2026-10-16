using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        private static int PlayHotseatRuleset(ContentDb db, CliOptions options, TeamPreset imported)
        {
            // Windows 管道默认代码页不一定是 UTF-8，中文恢复词必须按统一编码读取。
            if (Console.IsInputRedirected) Console.InputEncoding = new UTF8Encoding(false, true);
            var ruleset = Rulesets.All.FirstOrDefault(item => string.Equals(item.Id, options.RulesetId, StringComparison.Ordinal));
            if (ruleset == null)
                throw new ArgumentException("未知赛制：" + options.RulesetId + "；可用赛制：" + string.Join("、", Rulesets.All.Select(item => item.Id)) + "。");
            ruleset.Validate(db);
            var preset = PrepareTeam(db, options, imported, ruleset.Season);
            var teamA = preset.Ids.ToArray();
            var teamB = options.TeamB ?? db.Streamers.Select(streamer => streamer.Id)
                .Where(id => !teamA.Contains(id)).Concat(teamA).Take(6).ToArray();
            var errors = ruleset.CheckTeam(db, teamB);
            if (errors.Count > 0)
            {
                if (options.TeamB != null) throw new ArgumentException("B 方队伍不符合赛制：" + string.Join("；", errors));
                teamB = TeamBuilder.Suggest(db, ruleset.Season, Array.Empty<string>(), unchecked(options.Seed + 1)).ToArray();
            }
            var session = new HotseatSession(db, teamA, teamB, options.NameA, options.NameB, ruleset, options.Seed);
            var nameA = TextInput.Clean(options.NameA, TextInputKind.PlayerName);
            var nameB = TextInput.Clean(options.NameB, TextInputKind.PlayerName);
            var input = new HotseatRulesetInput();
            var completed = new List<Battle>();
            int exitCode = 0;
            if (options.ExportTeam) Console.WriteLine("本方队伍码：" + TeamCode.Encode(preset));
            Console.WriteLine("开播对决 · 同屏双人赛制对战");
            Console.WriteLine("内容包：" + db.PackName);
            Console.WriteLine(db.PackNotice);
            PrintMixedNotices(db, teamA.Concat(teamB));
            Console.WriteLine("赛制：" + ruleset.Name + "（" + ruleset.Id + "）；最多 " + ruleset.BestOf + " 局，先赢 " + (ruleset.BestOf / 2 + 1) + " 局结束");
            Console.WriteLine("种子：" + options.Seed + "；A 方：" + nameA.Text + "；B 方：" + nameB.Text);
            if (nameA.Changed) Console.WriteLine("A 方昵称显示已调整：" + nameA.Message);
            if (nameB.Changed) Console.WriteLine("B 方昵称显示已调整：" + nameB.Message);
            Console.WriteLine("轮流私密选招，输入不回显；交接后按回车确认，q 或输入结束退出。");
            Console.WriteLine("每个完成局按 A 方累计普通战绩并保存录像，不计排位分。");
            Console.WriteLine("对战指令：/surrender 投降并结算；同屏模式暂不支持 /draw 求和。");
            if (session.Clock != null)
                Console.WriteLine("计时配置：每阶段基本 " + ruleset.TurnSeconds + " 秒，每方总时间 " + ruleset.BankSeconds
                    + " 秒；交接、禁用和首发不计时，每局重置预算。");

            while (true)
            {
                Console.WriteLine();
                Console.WriteLine("===== 同屏赛制第 " + session.GameNumber + " 局 · 种子 " + session.Record.Seed + " =====");
                if (!PlayHotseatRulesetGame(db, session, options, input))
                {
                    Quit();
                    Console.WriteLine("赛制未完成；已完成 " + completed.Count + " 局的回放与战绩保留，未完成局不计入比分。");
                    return exitCode;
                }
                var battle = session.CloneCompletedBattle();
                completed.Add(battle);
                try
                {
                    if (SaveCompletedBattle(battle, options, 0, exportReport: false) != 0) exitCode = 1;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                    || ex is FormatException || ex is OverflowException)
                {
                    Console.Error.WriteLine("错误：第 " + completed.Count + " 局档案保存失败：" + ex.Message);
                    exitCode = 1;
                }
                Console.WriteLine("赛制比分：" + nameA.Text + " " + session.Wins[0] + " : " + session.Wins[1]
                    + " " + nameB.Text + "；平局 " + session.Draws);
                if (session.MatchOver) break;
                if (!input.Recover()) return QuitHotseatRuleset(completed.Count, exitCode);
                session.NextGame();
            }
            Console.WriteLine("赛制结束：共 " + completed.Count + " 局；" + nameA.Text + " " + session.Wins[0] + " : "
                + session.Wins[1] + " " + nameB.Text + "；平局 " + session.Draws);
            SaveRulesetReport(ruleset, session.Wins, session.Draws, completed, options, nameA.Text, nameB.Text);
            return exitCode;
        }

        private static int QuitHotseatRuleset(int completed, int exitCode)
        {
            Quit();
            Console.WriteLine("赛制未完成；已完成 " + completed + " 局的回放与战绩保留，未完成局不计入比分。");
            return exitCode;
        }

        private static bool PlayHotseatRulesetGame(ContentDb db, HotseatSession session, CliOptions options,
            HotseatRulesetInput input)
        {
            int logIndex = 0;
            try
            {
                while (session.Phase != Phase.Over)
                {
                    if (!input.Recover()) return false;
                    if (session.NeedsHandover && !ConfirmHotseatHandover(session, options, () => input.ReadLine())) return false;
                    int side = session.WhoseTurn;
                    long revision = session.TimerRevision;
                    var elapsed = Stopwatch.StartNew();
                    string lastClock = null;
                    Func<bool> pump = () =>
                    {
                        if (session.TimerRevision != revision || session.WhoseTurn != side || session.NeedsHandover) return true;
                        double seconds = elapsed.Elapsed.TotalSeconds;
                        elapsed.Restart();
                        if (session.TickRuleset(seconds)) return true;
                        if (session.Clock != null && (session.Phase == Phase.Choose || session.Phase == Phase.Replace))
                        {
                            string clock = "计时：本方基本 " + Math.Ceiling(session.Clock.TurnLeft(side)).ToString("0", CultureInfo.InvariantCulture)
                                + " 秒 / 总时间 " + Math.Ceiling(session.Clock.BankLeft(side)).ToString("0", CultureInfo.InvariantCulture) + " 秒";
                            if (clock != lastClock) { Console.WriteLine(clock); lastClock = clock; }
                        }
                        return false;
                    };
                    Func<string> read = () => input.ReadLine(pump);
                    var view = session.ViewFor(side);
                    PrintHotseatLog(session.DisplayLog, ref logIndex);
                    Console.WriteLine();
                    Console.WriteLine("===== 第 " + view.Turn + " 回合 · " + view.Sides[side].Name + " 操作 =====");
                    PrintHotseatTeams(view);
                    try
                    {
                        if (view.Phase == Phase.Ban || view.Phase == Phase.Lead || view.Phase == Phase.Replace)
                        {
                            bool ban = view.Phase == Phase.Ban;
                            string purpose = ban ? "禁用对手" : view.Phase == Phase.Lead ? "选择首发" : "选择补位";
                            var choice = ReadHotseatMember(view.Sides[ban ? 1 - side : side], purpose,
                                view.Phase == Phase.Replace ? view.SwitchTargets : null, session, read);
                            if (!choice.HasValue) return false;
                            if (pump()) { input.Expire(); continue; }
                            if (ban) session.SubmitBan(choice.Value);
                            else if (view.Phase == Phase.Lead) session.SubmitLead(choice.Value);
                            else session.SubmitReplace(choice.Value);
                        }
                        else
                        {
                            PrintHotseatFighter(view, 1 - side, db); PrintHotseatFighter(view, side, db);
                            var choice = ReadHotseatAction(view, side, session, read);
                            if (!choice.HasValue) return false;
                            if (pump()) { input.Expire(); continue; }
                            session.Submit(choice.Value);
                        }
                    }
                    catch (ChoiceExpiredException) { }
                }
            }
            catch (BattleFinishedException) { }
            PrintHotseatLog(session.DisplayLog, ref logIndex);
            return true;
        }

        private sealed class HotseatRulesetInput
        {
            Task<string> pending;
            bool expired;

            public string ReadLine(Func<bool> pump = null)
            {
                if (pending == null) pending = Task.Run(() => ReadHotseatLine());
                while (true)
                {
                    if (pump != null && pump())
                    {
                        Expire();
                        throw new ChoiceExpiredException();
                    }
                    if (pending.IsCompleted)
                    {
                        string line = pending.GetAwaiter().GetResult();
                        pending = null;
                        return line;
                    }
                    Thread.Sleep(10);
                }
            }

            public void Expire()
            {
                if (expired) return;
                expired = true;
                Console.WriteLine("本次输入已超时，已提交默认选择；旧输入不会用于下一位玩家。");
            }

            public bool Recover()
            {
                if (!expired) return true;
                if (pending != null)
                {
                    Console.WriteLine("请按回车结束旧输入；这一整行将被丢弃。");
                    if (ReadLine() == null) return false;
                }
                while (true)
                {
                    Console.Write("输入“继续”恢复交接（q 退出；恢复前暂停计时）> ");
                    string line = ReadLine();
                    if (line == null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) return false;
                    if (line.Trim() != "继续") continue;
                    expired = false;
                    return true;
                }
            }
        }
    }
}
