using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
                using (var listener = TcpTransport.Listen(options.HostPort.Value, out int actualPort))
                {
                    Console.WriteLine("监听端口：" + actualPort);
                    Console.WriteLine("等待对手连接……（Ctrl+C 退出）");
                    Console.Out.Flush();
                    do { connection = listener.Accept(250); } while (connection == null);
                }
            }
            else
            {
                Console.WriteLine("正在连接房主……");
                connection = TcpTransport.Connect(options.JoinHost, options.JoinPort, 10000);
            }

            using (connection)
            {
                var session = new NetSession(db, connection, host, team, host ? "房主" : "客机", host ? options.Seed : (uint?)null,
                    turnSeconds: options.TurnSeconds, bankSeconds: options.BankSeconds);
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
                Console.WriteLine("连接成功；本方：" + battle.Sides[session.MySide].Name + "；房主种子：" + battle.Seed);
                if (session.Clock != null)
                    Console.WriteLine("已启用房主计时：每阶段 " + session.Clock.TurnLeft(session.MySide).ToString("R", CultureInfo.InvariantCulture)
                        + " 秒；总时间 " + session.Clock.BankLeft(session.MySide).ToString("R", CultureInfo.InvariantCulture) + " 秒。");
                else if (!host && options.TurnSeconds.HasValue)
                    Console.WriteLine("房主未启用计时，客机 --timer 不改变本局设置。");
                PrintTeams(battle);
                var input = new ConsoleInput();
                var pump = new NetworkPump(session);
                ChoiceStamp? inputStage = null;
                Action prepareInput = () =>
                {
                    if (options.Auto || session.State != NetState.Choose && session.State != NetState.Replace) return;
                    if (!inputStage.HasValue || !inputStage.Value.Matches(battle))
                    {
                        input.DiscardCompleted();
                        inputStage = new ChoiceStamp(battle);
                    }
                    input.Start();
                };
                int logIndex = 0;
                int hashEventCount = -1;
                bool waitingPrinted = false;
                while (true)
                {
                    if (!options.Auto)
                    {
                        prepareInput();
                        if (input.EndRequested) return Quit();
                    }
                    pump.Pump();
                    prepareInput();
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
                        Thread.Sleep(10);
                        continue;
                    }

                    waitingPrinted = false;
                    Console.WriteLine();
                    Console.WriteLine("===== 第 " + battle.Turn + " 回合 =====");
                    PrintFighter(battle, 1 - session.MySide);
                    PrintFighter(battle, session.MySide);
                    var stamp = new ChoiceStamp(battle);
                    NetState choosingState = session.State;
                    Func<bool> expired = () => !stamp.Matches(battle) || session.HasSubmitted || session.State != choosingState;
                    try
                    {
                        if (choosingState == NetState.Choose)
                        {
                            BattleAction? action = options.Auto ? Ai.Choose(battle, session.MySide, options.Level)
                                : ReadAction(battle, session.MySide, prompt => ReadPumpedNumber(input, prompt, pump.Pump, expired));
                            if (!action.HasValue) return Quit();
                            pump.Pump();
                            if (!expired()) session.Submit(action.Value);
                        }
                        else if (choosingState == NetState.Replace)
                        {
                            int? to = options.Auto ? Ai.ChooseReplacement(battle, session.MySide, options.Level)
                                : ReadReplacement(battle, session.MySide, prompt => ReadPumpedNumber(input, prompt, pump.Pump, expired));
                            if (!to.HasValue) return Quit();
                            pump.Pump();
                            if (!expired()) session.SubmitReplace(to.Value);
                        }
                    }
                    catch (ChoiceExpiredException) { }
                    finally
                    {
                        input.DiscardCompleted();
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
                if (connection.PeerClosed)
                {
                    // EOF 可能刚好在前一次 Poll 返回后到达，再排空关闭前的完整消息。
                    session.Poll();
                    CheckNetwork(session);
                    return;
                }
                if (completion.ElapsedMilliseconds >= 10000)
                    throw new IOException("等待对手完成终局校验超时，对局未保存。");
                Thread.Sleep(10);
            }
        }

        static int SaveCompletedBattle(Battle battle, CliOptions options, int mySide)
        {
            Console.WriteLine();
            Console.WriteLine("对局结束：" + (battle.Winner < 0 ? "平局" : battle.Sides[battle.Winner].Name + " 获胜"));
            PrintTeams(battle);
            Console.WriteLine(BattleSummary.From(battle).ToText());
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
                SaveReport(battle, options);
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
            var profile = ProfileStore.Record(battle, options.SaveDir, mySide);
            var stats = profile.Packs[battle.Db.PackId];
            Console.WriteLine("本包战绩：" + stats.Wins + " 胜 / " + stats.Losses + " 负 / " + stats.Draws + " 平");
            Console.WriteLine("档案已保存：" + Path.Combine(options.SaveDir, "profile.json"));
            return replayWarning == null && !reportFailed && !shareFailed ? 0 : 1;
        }
    }
}
