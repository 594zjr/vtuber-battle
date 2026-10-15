using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StreamDuel.Core;

namespace StreamDuel.Cli
{
    internal static partial class Program
    {
        static int PlayNetwork(ContentDb db, IList<string> team, CliOptions options)
        {
            Console.WriteLine("开播对决 · 局域网对战（仅用于可信局域网，无加密、无账号）");
            Console.WriteLine("内容包：" + db.PackName);
            Console.WriteLine(db.PackNotice);
            using (var network = new NetworkConnection(db, team, options))
            {
                var session = network.Session;
                Console.WriteLine("等待对手握手……");
                var handshake = Stopwatch.StartNew();
                while (session.State == NetState.Handshake)
                {
                    network.Poll();
                    if (session.State != NetState.Handshake) break;
                    if (handshake.ElapsedMilliseconds >= 10000) throw new IOException("等待对手握手超时。");
                    Thread.Sleep(10);
                }
                CheckNetwork(session);
                var battle = session.Battle;
                Console.WriteLine("连接成功；本方：" + battle.Sides[session.MySide].Name + "；房主种子：" + battle.Seed);
                PrintTeams(battle);
                int logIndex = 0;
                int hashEventCount = -1;
                bool waitingPrinted = false;
                while (true)
                {
                    network.Poll();
                    battle = session.Battle;
                    PrintNewLog(battle, ref logIndex);
                    if (options.Hash && !session.IsDisconnected && session.State != NetState.Waiting
                        && battle.Record.Events.Count != hashEventCount)
                    {
                        Console.WriteLine("同步状态哈希（第 " + battle.Turn + " 回合）：" + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
                        hashEventCount = battle.Record.Events.Count;
                    }
                    if (session.State == NetState.Over && !session.IsDisconnected) break;
                    if (session.State == NetState.Waiting || session.IsDisconnected)
                    {
                        if (!waitingPrinted && !session.IsDisconnected) Console.WriteLine("等待对手……");
                        waitingPrinted = true;
                        Thread.Sleep(10);
                        continue;
                    }

                    waitingPrinted = false;
                    Console.WriteLine();
                    Console.WriteLine("===== 第 " + battle.Turn + " 回合 =====");
                    PrintFighter(battle, 1 - session.MySide);
                    PrintFighter(battle, session.MySide);
                    var promptState = new NetworkPromptState(session);
                    try
                    {
                        if (session.State == NetState.Choose)
                        {
                            BattleAction? action = options.Auto ? Ai.Choose(battle, session.MySide, options.Level)
                                : ReadAction(battle, session.MySide, prompt => ReadNetworkNumber(network, promptState, prompt));
                            if (!action.HasValue) return LeaveNetwork(session);
                            session.Submit(action.Value);
                        }
                        else if (session.State == NetState.Replace)
                        {
                            int? to = options.Auto ? Ai.ChooseReplacement(battle, session.MySide, options.Level)
                                : ReadReplacement(battle, session.MySide, prompt => ReadNetworkNumber(network, promptState, prompt));
                            if (!to.HasValue) return LeaveNetwork(session);
                            session.SubmitReplace(to.Value);
                        }
                    }
                    catch (NetworkPromptChangedException)
                    {
                        Console.WriteLine("重连后对战状态已更新，请按新的选项重新选择。");
                        continue;
                    }
                    CheckNetwork(session);
                    Console.WriteLine("等待对手……");
                    waitingPrinted = true;
                }
                FinishNetwork(network);
                battle = session.Battle;
                Console.WriteLine("最终状态哈希：" + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
                return SaveCompletedBattle(battle, options, session.MySide);
            }
        }

        sealed class NetworkConnection : IDisposable
        {
            readonly CliOptions options;
            readonly Stopwatch clock = Stopwatch.StartNew();
            TcpHost listener;
            double lastPollSeconds;
            double nextReconnectSeconds;
            int printedSeconds = -1;
            bool wasDisconnected;

            public TcpTransport Connection { get; private set; }
            public NetSession Session { get; private set; }

            public NetworkConnection(ContentDb db, IList<string> team, CliOptions options)
            {
                this.options = options;
                bool host = options.HostPort.HasValue;
                try
                {
                    if (host)
                    {
                        // 监听器持续到整局退出，随机端口也能接受同一会话的后续连接。
                        listener = TcpTransport.Listen(options.HostPort.Value, out int actualPort);
                        Console.WriteLine("监听端口：" + actualPort);
                        Console.WriteLine("等待对手连接……（Ctrl+C 退出）");
                        Console.Out.Flush();
                        do { Connection = listener.Accept(250); } while (Connection == null);
                    }
                    else
                    {
                        Console.WriteLine("正在连接房主……");
                        Connection = TcpTransport.Connect(options.JoinHost, options.JoinPort, 10000);
                    }
                    Session = new NetSession(db, Connection, host, team, host ? "房主" : "客机",
                        host ? options.Seed : (uint?)null, enableReconnect: true);
                    lastPollSeconds = clock.Elapsed.TotalSeconds;
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Poll()
            {
                AdvanceClock();
                if (!Session.IsDisconnected)
                {
                    if (wasDisconnected)
                    {
                        Console.WriteLine("重连成功，继续第 " + Session.Battle.Turn + " 回合。");
                        wasDisconnected = false;
                        printedSeconds = -1;
                    }
                    return;
                }
                if (Session.Battle == null || string.IsNullOrEmpty(Session.SessionId))
                    throw new IOException("握手尚未完成，对方已断开，无法恢复本局。");
                wasDisconnected = true;
                int remaining = (int)Math.Ceiling(Math.Max(0, Session.ReconnectSecondsRemaining));
                if (remaining != printedSeconds)
                {
                    Console.WriteLine("对方断线，等待重连（剩余 " + remaining + " 秒）");
                    Console.Out.Flush();
                    printedSeconds = remaining;
                }
                if (Session.IsReconnecting || clock.Elapsed.TotalSeconds < nextReconnectSeconds) return;

                Connection?.Dispose();
                Connection = null;
                TcpTransport replacement = null;
                try
                {
                    try
                    {
                        int timeout = (int)Math.Max(1, Math.Min(250, Math.Ceiling(Session.ReconnectSecondsRemaining * 1000)));
                        replacement = listener != null ? listener.Accept(timeout)
                            : TcpTransport.Connect(options.JoinHost, options.JoinPort, timeout);
                    }
                    catch (Exception ex) when (ex is IOException || ex is TimeoutException)
                    {
                        // 一次连接失败不结束恢复窗口；倒计时仍从最初断线累计。
                    }
                    nextReconnectSeconds = clock.Elapsed.TotalSeconds + 0.25;
                    // 连接等待占用的时间也交给 Core，不能借反复连接延长 60 秒。
                    AdvanceClock();
                    if (replacement == null) return;
                    Session.Reconnect(replacement, Session.SessionId);
                    Connection = replacement;
                    replacement = null;
                    CheckNetwork(Session);
                }
                finally { replacement?.Dispose(); }
            }

            void AdvanceClock()
            {
                double now = clock.Elapsed.TotalSeconds;
                double elapsed = now - lastPollSeconds;
                lastPollSeconds = now;
                Session.Poll(elapsed);
                CheckNetwork(Session);
            }

            public void Dispose()
            {
                Connection?.Dispose();
                Connection = null;
                listener?.Dispose();
                listener = null;
            }
        }

        sealed class NetworkPromptChangedException : Exception { }

        sealed class NetworkPromptState
        {
            readonly Battle battle;
            readonly NetState state;
            readonly Phase phase;
            readonly int turn;
            readonly int eventCount;

            public NetworkPromptState(NetSession session)
            {
                battle = session.Battle;
                state = session.State;
                phase = battle.Phase;
                turn = battle.Turn;
                eventCount = battle.Record.Events.Count;
            }

            public bool Matches(NetSession session)
            {
                return ReferenceEquals(battle, session.Battle) && session.State == state && !session.HasSubmitted
                    && battle.Phase == phase && battle.Turn == turn && battle.Record.Events.Count == eventCount;
            }
        }

        static int LeaveNetwork(NetSession session)
        {
            session.Leave("对方已断开（主动退出，对局未完成）。");
            return Quit();
        }

        static void CheckNetwork(NetSession session)
        {
            if (session.State == NetState.Error || session.State == NetState.Desync)
                throw new IOException(session.Error);
        }

        static void FinishNetwork(NetworkConnection network)
        {
            var session = network.Session;
            Console.WriteLine("等待对手完成终局校验……");
            var completion = Stopwatch.StartNew();
            TcpTransport completingConnection = null;
            while (true)
            {
                network.Poll();
                if (session.IsDisconnected)
                {
                    completingConnection = null;
                    Thread.Sleep(10);
                    continue;
                }
                if (session.State != NetState.Over) throw new IOException("恢复后的对战尚未完成终局校验，对局未保存。");
                var connection = network.Connection;
                if (!ReferenceEquals(completingConnection, connection))
                {
                    session.PrepareClose();
                    CheckNetwork(session);
                    if (session.IsDisconnected) continue;
                    // 关闭发送方向后仍接收数据，保证对端最终哈希报错先于 EOF 被处理。
                    try { connection.CompleteSending(); }
                    catch (IOException)
                    {
                        network.Poll();
                        if (session.IsDisconnected || !ReferenceEquals(connection, network.Connection)) continue;
                        throw;
                    }
                    completingConnection = connection;
                    completion.Restart();
                }
                if (connection.PeerClosed)
                {
                    // EOF 可能刚好在前一次 Poll 返回后到达，再排空关闭前的完整消息。
                    network.Poll();
                    if (!session.IsDisconnected && ReferenceEquals(connection, network.Connection)) return;
                    continue;
                }
                if (completion.ElapsedMilliseconds >= 10000)
                    throw new IOException("等待对手完成终局校验超时，对局未保存。");
                Thread.Sleep(10);
            }
        }

        static int? ReadNetworkNumber(NetworkConnection network, NetworkPromptState promptState, string prompt)
        {
            while (true)
            {
                Console.Write(prompt);
                // 断线重连期间保留同一个输入任务，避免多个 ReadLine 抢走下一次输入。
                var input = Task.Run(() => Console.ReadLine());
                string line;
                while (true)
                {
                    bool ready = input.Wait(10);
                    network.Poll();
                    if (!ready) continue;
                    line = input.Result;
                    if (line == null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) return null;
                    if (network.Session.IsDisconnected)
                    {
                        Thread.Sleep(10);
                        continue;
                    }
                    if (!promptState.Matches(network.Session)) throw new NetworkPromptChangedException();
                    break;
                }
                int value;
                if (int.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value)) return value;
                Console.WriteLine("请输入数字，或输入 q 退出。");
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
