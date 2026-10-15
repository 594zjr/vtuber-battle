using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        static int PlayNetwork(ContentDb db, IList<string> team, CliOptions options)
        {
            TcpTransport connection;
            bool host = options.HostPort.HasValue;
            Console.WriteLine("开播对决 · 局域网对战（仅用于可信局域网，无加密、无账号）");
            Console.WriteLine("内容包：" + db.PackName);
            Console.WriteLine(db.PackNotice);
            if (host)
            {
                connection = WaitForNetworkGuest(db, options);
                if (connection == null) return Quit();
            }
            else
            {
                Console.WriteLine("正在连接房主……");
                connection = TcpTransport.Connect(options.JoinHost, options.JoinPort, 10000);
            }

            using (connection)
            {
                var session = new NetSession(db, connection, host, team, host ? "房主" : "客机", host ? options.Seed : (uint?)null,
                    turnSeconds: options.TurnSeconds, bankSeconds: options.BankSeconds, enableTermination: true);
                Console.WriteLine("等待对手握手……");
                var handshake = Stopwatch.StartNew();
                while (session.State == NetState.Handshake)
                {
                    session.Poll();
                    if (session.State != NetState.Handshake) break;
                    if (handshake.ElapsedMilliseconds >= 10000) throw new IOException("等待对手握手超时。");
                    Thread.Sleep(10);
                }
                CheckNetwork(session);
                var battle = session.Battle;
                ValidateRandomNetworkTeams(db, battle, options);
                Console.WriteLine("连接成功；本方：" + session.DisplayNames[session.MySide] + "；房主种子：" + battle.Seed);
                var nameInputs = session.NameInputs;
                for (int side = 0; side < nameInputs.Count; side++)
                    if (nameInputs[side].Changed)
                        Console.WriteLine((side == session.MySide ? "本方" : "对方") + "昵称提示：" + nameInputs[side].Message);
                if (session.Clock != null)
                    Console.WriteLine("已启用房主计时：每阶段 " + session.Clock.TurnLeft(session.MySide).ToString("R", CultureInfo.InvariantCulture)
                        + " 秒；总时间 " + session.Clock.BankLeft(session.MySide).ToString("R", CultureInfo.InvariantCulture) + " 秒。");
                else if (!host && options.TurnSeconds.HasValue)
                    Console.WriteLine("房主未启用计时，客机 --timer 不改变本局设置。");
                PrintTeams(TextInput.ForDisplay(battle));
                PrintMixedNotices(db, battle.Sides.SelectMany(side => side.Team).Select(fighter => fighter.Def.Id));
                PrintTerminationCommands(network: true);
                var input = new ConsoleInput();
                var pump = new NetworkPump(session);
                bool drawNotified = false;
                bool terminalCommand = false;
                int awaitingDrawResponse = -1;
                Action poll = () =>
                {
                    pump.Pump();
                    ShowNetworkDraw(session, options.Auto, ref drawNotified);
                };
                Func<string, bool> command = line => HandleNetworkCommand(session, line,
                    () => terminalCommand = true, () => awaitingDrawResponse = battle.Record.Events.Count);
                ChoiceStamp? inputStage = null;
                Action prepareInput = () =>
                {
                    if (options.Auto || terminalCommand || battle.Phase == Phase.Over) return;
                    if (!inputStage.HasValue || !inputStage.Value.Matches(battle))
                    {
                        input.DiscardCompleted(preserveCommands: true);
                        inputStage = new ChoiceStamp(battle);
                    }
                    input.Start();
                };
                int logIndex = 0;
                int hashEventCount = -1;
                bool waitingPrinted = false;
                while (true)
                {
                    poll();
                    if (awaitingDrawResponse >= 0 && battle.Record.Events.Skip(awaitingDrawResponse)
                        .Any(entry => entry.Type == BattleRecordEventType.DrawResponse && entry.Side == 1 - session.MySide))
                        awaitingDrawResponse = -1;
                    if (!options.Auto)
                    {
                        prepareInput();
                        // 已发出的终局操作必须等双方确认，输入管道随命令结束不能漏记结果。
                        if (input.EndRequested && (!input.EndOfInput
                            || !terminalCommand && awaitingDrawResponse < 0 && !session.DrawOfferPending && battle.Phase != Phase.Over)) return Quit();
                    }
                    PrintNewLog(battle, ref logIndex);
                    if (options.Hash && session.State != NetState.Waiting && battle.Record.Events.Count != hashEventCount)
                    {
                        Console.WriteLine("同步状态哈希（第 " + battle.Turn + " 回合）：" + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
                        hashEventCount = battle.Record.Events.Count;
                    }
                    if (session.State == NetState.Over) break;
                    if (session.State == NetState.Waiting)
                    {
                        if (!waitingPrinted) Console.WriteLine("等待对手……");
                        waitingPrinted = true;
                        if (!options.Auto && !terminalCommand && input.TryTake(out string line))
                        {
                            if (line != null && !line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) && !command(line))
                                Console.WriteLine("已提交选择，等待对手；仍可输入 /surrender 或 /draw 指令。");
                        }
                        Thread.Sleep(10);
                        continue;
                    }
                    if (!options.Auto && input.EndOfInput && (awaitingDrawResponse >= 0 || session.DrawOfferPending))
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    waitingPrinted = false;
                    Console.WriteLine();
                    Console.WriteLine("===== 第 " + battle.Turn + " 回合 =====");
                    var displayBattle = TextInput.ForDisplay(battle);
                    PrintFighter(displayBattle, 1 - session.MySide);
                    PrintFighter(displayBattle, session.MySide);
                    var stamp = new ChoiceStamp(battle);
                    NetState choosingState = session.State;
                    Func<bool> expired = () => !stamp.Matches(battle) || session.HasSubmitted || session.State != choosingState;
                    try
                    {
                        if (choosingState == NetState.Choose)
                        {
                            BattleAction? action = options.Auto ? Ai.Choose(battle, session.MySide, options.Level)
                                : ReadAction(battle, session.MySide, prompt => ReadPumpedNumber(input, prompt, poll, expired, command));
                            if (!action.HasValue) return Quit();
                            poll();
                            if (!expired()) session.Submit(action.Value);
                        }
                        else if (choosingState == NetState.Replace)
                        {
                            int? to = options.Auto ? Ai.ChooseReplacement(battle, session.MySide, options.Level)
                                : ReadReplacement(battle, session.MySide, prompt => ReadPumpedNumber(input, prompt, poll, expired, command));
                            if (!to.HasValue) return Quit();
                            poll();
                            if (!expired()) session.SubmitReplace(to.Value);
                        }
                    }
                    catch (ChoiceExpiredException) { }
                    finally
                    {
                        input.DiscardCompleted(preserveCommands: true);
                    }
                    CheckNetwork(session);
                    Console.WriteLine("等待对手……");
                    waitingPrinted = true;
                }
                FinishNetwork(session, connection);
                Console.WriteLine("最终状态哈希：" + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
                return SaveCompletedBattle(battle, options, session.MySide);
            }
        }

        static void CheckNetwork(NetSession session)
        {
            if (session.State == NetState.Error || session.State == NetState.Desync)
                throw new IOException(session.Error);
        }

        static void FinishNetwork(NetSession session, TcpTransport connection)
        {
            Console.WriteLine("等待对手完成终局校验……");
            // 关闭发送方向后仍接收数据，保证对端最终哈希报错先于 EOF 被处理。
            connection.CompleteSending();
            var completion = Stopwatch.StartNew();
            while (true)
            {
                session.Poll();
                CheckNetwork(session);
                if (connection.ReceiveDrained) return;
                if (completion.ElapsedMilliseconds >= 10000)
                    throw new IOException("等待对手完成终局校验超时，对局未保存。");
                Thread.Sleep(10);
            }
        }

        static int SaveCompletedBattle(Battle battle, CliOptions options, int mySide)
        {
            var display = TextInput.ForDisplay(battle);
            Console.WriteLine();
            Console.WriteLine("对局结束：" + (display.Winner < 0 ? "平局" : display.Sides[display.Winner].Name + " 获胜"));
            PrintTeams(display);
            var summary = BattleSummary.From(battle);
            Console.WriteLine(BattleSummary.From(display).ToText());
            PrintReview(display, mySide);
            bool shareFailed = false;
            try
            {
                PrintShareCode(battle.Record, options);
            }
            catch (FormatException ex)
            {
                // 分享码有大小上限，导出失败不能漏存已完成的对局。
                Console.Error.WriteLine("错误：分享码导出失败：" + ex.Message);
                shareFailed = true;
            }
            bool reportFailed = false;
            try
            {
                SaveReport(battle, options, mySide);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                // 战报和存档各自保存，任一目标不可写时仍保留其他已完成的结果。
                Console.Error.WriteLine("错误：" + ex.Message);
                reportFailed = true;
            }
            string replayWarning;
            try
            {
                replayWarning = ReplayStore.Save(battle, options.SaveDir);
                Console.WriteLine("回放已保存到：" + Path.Combine(options.SaveDir, "replays"));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 回放与档案独立保存，回放目录的故障不能漏记已完成的对局。
                replayWarning = "回放保存失败：" + ex.Message;
            }
            if (replayWarning != null) Console.Error.WriteLine("警告：" + replayWarning);
            bool libraryFailed = false;
            try
            {
                new ReplayLibrary(ReplayLibraryDirectory(options), policy: battle.Db.Policy).Save(battle.Record, summary);
                Console.WriteLine("录像库已更新：" + ReplayLibraryDirectory(options));
            }
            catch (ReplayLibraryMaintenanceException ex)
            {
                Console.Error.WriteLine("警告：录像已写入，但录像库维护失败：" + ex.Message);
                libraryFailed = true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                || ex is ArgumentException || ex is FormatException || ex is InvalidOperationException)
            {
                // 独立录像库的故障不能影响旧版回放和档案，已完成的对局仍须累计战绩。
                Console.Error.WriteLine("警告：录像库保存失败：" + ex.Message);
                libraryFailed = true;
            }
            var profile = ProfileStore.Record(battle, options.SaveDir, mySide);
            var stats = profile.Packs[battle.Db.PackId];
            Console.WriteLine("本包战绩：" + stats.Wins + " 胜 / " + stats.Losses + " 负 / " + stats.Draws + " 平");
            Console.WriteLine("档案已保存：" + Path.Combine(options.SaveDir, "profile.json"));
            return replayWarning == null && !reportFailed && !shareFailed && !libraryFailed ? 0 : 1;
        }
    }
}
