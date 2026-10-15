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
                    turnSeconds: options.TurnSeconds, bankSeconds: options.BankSeconds, aiTakeoverOnTimeout: options.AiTakeover);
                Console.WriteLine("等待对手握手……");
                var handshake = Stopwatch.StartNew();
                double handshakeTick = 0;
                while (session.State == NetState.Handshake)
                {
                    session.Poll();
                    if (session.State != NetState.Handshake) break;
                    double now = handshake.Elapsed.TotalSeconds;
                    session.Tick(now - handshakeTick);
                    handshakeTick = now;
                    CheckNetwork(session);
                    if (handshake.ElapsedMilliseconds >= 10000) throw new IOException("等待对手握手超时。");
                    Thread.Sleep(10);
                }
                CheckNetwork(session);
                var battle = session.Battle;
                Console.WriteLine("连接成功；本方：" + battle.Sides[session.MySide].Name + "；房主种子：" + battle.Seed);
                if (session.AiTakeoverOnTimeout)
                    Console.WriteLine("房主已启用电脑代打：总时间耗尽后自动使用普通 AI；断线超过宽限将进入离线续局。");
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
                bool offlinePrinted = false;
                Action pumpNetwork = () =>
                {
                    pump.Pump();
                    if (!ReferenceEquals(battle, session.Battle))
                    {
                        // 离线续局恢复最后确认快照，旧引用和未确认输入都不能再用于后续选择。
                        battle = session.Battle;
                        logIndex = battle.Log.Count;
                        hashEventCount = -1;
                        inputStage = null;
                        input.DiscardCompleted();
                    }
                    if (session.IsOfflineContinuation && !offlinePrinted)
                    {
                        Console.WriteLine("已进入离线续局：未确认阶段已撤回，对方由电脑代打；本地结果未经双方终局确认，不累计联网战绩。");
                        offlinePrinted = true;
                        waitingPrinted = false;
                    }
                };
                while (true)
                {
                    if (!options.Auto)
                    {
                        prepareInput();
                        if (input.EndRequested) return Quit();
                    }
                    pumpNetwork();
                    prepareInput();
                    PrintNewLog(battle, ref logIndex);
                    if (options.Hash && session.State != NetState.Waiting && battle.Record.Events.Count != hashEventCount)
                    {
                        Console.WriteLine((session.IsOfflineContinuation ? "本地状态哈希（第 " : "同步状态哈希（第 ")
                            + battle.Turn + " 回合）：" + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
                        hashEventCount = battle.Record.Events.Count;
                    }
                    if (session.State == NetState.Over) break;
                    if (session.State == NetState.Waiting)
                    {
                        if (!waitingPrinted) Console.WriteLine(session.WaitingForAiTakeover
                            ? "连接已断开，等待电脑代打宽限结束……"
                            : session.AiTakeoverOnTimeout ? "等待对手或电脑代打……" : "等待对手……");
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
                    var selectedBattle = battle;
                    NetState choosingState = session.State;
                    Func<bool> expired = () => !ReferenceEquals(selectedBattle, session.Battle)
                        || !stamp.Matches(battle) || session.HasSubmitted || session.State != choosingState;
                    try
                    {
                        if (choosingState == NetState.Choose)
                        {
                            BattleAction? action = options.Auto ? Ai.Choose(battle, session.MySide, options.Level)
                                : ReadAction(battle, session.MySide, prompt => ReadPumpedNumber(input, prompt, pumpNetwork, expired));
                            if (!action.HasValue) return Quit();
                            pumpNetwork();
                            if (!expired()) session.Submit(action.Value);
                        }
                        else if (choosingState == NetState.Replace)
                        {
                            int? to = options.Auto ? Ai.ChooseReplacement(battle, session.MySide, options.Level)
                                : ReadReplacement(battle, session.MySide, prompt => ReadPumpedNumber(input, prompt, pumpNetwork, expired));
                            if (!to.HasValue) return Quit();
                            pumpNetwork();
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
                if (!session.IsOfflineContinuation) FinishNetwork(session, connection);
                Console.WriteLine((session.IsOfflineContinuation ? "离线终局哈希：" : "最终状态哈希：")
                    + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
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
            Console.WriteLine();
            Console.WriteLine("对局结束：" + (battle.Winner < 0 ? "平局" : battle.Sides[battle.Winner].Name + " 获胜"));
            PrintTeams(battle);
            var summary = BattleSummary.From(battle);
            Console.WriteLine(summary.ToText());
            PrintReview(battle, mySide);
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
                new ReplayLibrary(ReplayLibraryDirectory(options)).Save(battle.Record, summary);
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
            if (summary.IsOfflineContinuation)
            {
                Console.WriteLine("离线续局未经双方终局确认，不更新联网战绩档案。");
                return replayWarning == null && !reportFailed && !shareFailed && !libraryFailed ? 0 : 1;
            }
            var profile = ProfileStore.Record(battle, options.SaveDir, mySide);
            var stats = profile.Packs[battle.Db.PackId];
            Console.WriteLine("本包战绩：" + stats.Wins + " 胜 / " + stats.Losses + " 负 / " + stats.Draws + " 平");
            Console.WriteLine("档案已保存：" + Path.Combine(options.SaveDir, "profile.json"));
            return replayWarning == null && !reportFailed && !shareFailed && !libraryFailed ? 0 : 1;
        }
    }
}
