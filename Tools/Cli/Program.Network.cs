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
            var rating = ProfileStore.LoadRating(options.SaveDir);
            bool host = options.HostPort.HasValue;
            Console.WriteLine("开播对决 · 局域网对战（仅用于可信局域网，无加密、无账号）");
            Console.WriteLine("内容包：" + db.PackName);
            Console.WriteLine(db.PackNotice);
            using var spectators = options.SpectatePort.HasValue ? new SpectatorHost(options.SpectatePort.Value, options.SpectatorChat) : null;
            using (var network = new NetworkConnection(db, team, options, rating, () => spectators?.Pump()))
            {
                var session = network.Session;
                if (session == null) return Quit();
                Console.WriteLine("等待对手握手……");
                var handshake = Stopwatch.StartNew();
                while (session.State == NetState.Handshake)
                {
                    network.Poll();
                    spectators?.Pump();
                    if (session.State != NetState.Handshake) break;
                    if (handshake.ElapsedMilliseconds >= 10000) throw new IOException("等待对手握手超时。");
                    Thread.Sleep(10);
                }
                CheckNetwork(session);
                var battle = session.Battle;
                ValidateRandomNetworkTeams(db, battle, options);
                spectators?.Start(battle);
                Console.WriteLine("连接成功；本方：" + session.DisplayNames[session.MySide] + "；房主种子：" + battle.Seed);
                Console.WriteLine("本方自报排位分：" + rating.Rating + "；对手自报排位分：" + session.OpponentRating.Rating
                    + "（仅用于本机显示，无防作弊）");
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
                var pump = new NetworkPump(session, network.Poll);
                bool drawNotified = false;
                bool terminalCommand = false;
                int awaitingDrawResponse = -1;
                bool disconnectedInput = false;
                Action poll = () =>
                {
                    pump.Pump();
                    battle = session.Battle;
                    spectators?.Pump();
                    if (session.IsDisconnected || disconnectedInput)
                    {
                        // 恢复窗口里完成的旧选择不能用于恢复后的局面；仍保留 EOF 和 q。
                        input.DiscardCompleted();
                        disconnectedInput = session.IsDisconnected;
                    }
                    if (!session.IsDisconnected) ShowNetworkDraw(session, options.Auto, ref drawNotified);
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
                            || !terminalCommand && awaitingDrawResponse < 0 && !session.DrawOfferPending && battle.Phase != Phase.Over)) return LeaveNetwork(session);
                    }
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
                        if (!session.IsDisconnected && !options.Auto && !terminalCommand && input.TryTake(out string line))
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
                    int recoveryRevision = network.RecoveryRevision;
                    NetState choosingState = session.State;
                    Func<bool> expired = () => network.RecoveryRevision != recoveryRevision || session.IsDisconnected
                        || !stamp.Matches(battle) || session.HasSubmitted || session.State != choosingState;
                    try
                    {
                        if (choosingState == NetState.Choose)
                        {
                            BattleAction? action = options.Auto ? Ai.Choose(battle, session.MySide, options.Level)
                                : ReadAction(battle, session.MySide, prompt => ReadPumpedNumber(input, prompt, poll, expired, command));
                            if (!action.HasValue) return LeaveNetwork(session);
                            poll();
                            if (!expired()) session.Submit(action.Value);
                        }
                        else if (choosingState == NetState.Replace)
                        {
                            int? to = options.Auto ? Ai.ChooseReplacement(battle, session.MySide, options.Level)
                                : ReadReplacement(battle, session.MySide, prompt => ReadPumpedNumber(input, prompt, poll, expired, command));
                            if (!to.HasValue) return LeaveNetwork(session);
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
                FinishNetwork(network, spectators);
                spectators?.Finish();
                Console.WriteLine("最终状态哈希：" + battle.StateHash().ToString("x16", CultureInfo.InvariantCulture));
                return SaveCompletedBattle(battle, options, session.MySide, session.OpponentRating);
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
            public int RecoveryRevision { get; private set; }

            public NetworkConnection(ContentDb db, IList<string> team, CliOptions options, PlayerRating rating, Action poll)
            {
                this.options = options;
                bool host = options.HostPort.HasValue;
                try
                {
                    if (host)
                    {
                        // 监听器持续到整局退出，随机端口也能接受同一会话的后续连接。
                        listener = TcpTransport.Listen(options.HostPort.Value, out int actualPort);
                        Connection = WaitForNetworkGuest(db, options, listener, actualPort, poll);
                        if (Connection == null) return;
                    }
                    else
                    {
                        Console.WriteLine("正在连接房主……");
                        Connection = TcpTransport.Connect(options.JoinHost, options.JoinPort, 10000);
                    }
                    Session = new NetSession(db, Connection, host, team, host ? "房主" : "客机",
                        host ? options.Seed : (uint?)null, turnSeconds: options.TurnSeconds, bankSeconds: options.BankSeconds,
                        enableTermination: true, rating: rating, enableReconnect: true);
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
                if (!wasDisconnected) RecoveryRevision++;
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

        static void FinishNetwork(NetworkConnection network, SpectatorHost spectators)
        {
            var session = network.Session;
            Console.WriteLine("等待对手完成终局校验……");
            var completion = Stopwatch.StartNew();
            TcpTransport completingConnection = null;
            while (true)
            {
                network.Poll();
                spectators?.Pump();
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
                    // 最终哈希和 goodbye 先于半关闭，关闭后仍排空完整消息和远端错误。
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
                if (connection.ReceiveDrained)
                {
                    // 接收线程可能在上次 Poll 后才发布 EOF，交给会话验证对方确已完成。
                    network.Poll();
                    if (!session.IsDisconnected && ReferenceEquals(connection, network.Connection)
                        && session.State == NetState.Over && connection.ReceiveDrained) return;
                    continue;
                }
                if (completion.ElapsedMilliseconds >= 10000)
                    throw new IOException("等待对手完成终局校验超时，对局未保存。");
                Thread.Sleep(10);
            }
        }

        static int SaveCompletedBattle(Battle battle, CliOptions options, int mySide, PlayerRating opponentRating = null,
            bool exportReport = true)
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
                if (exportReport) SaveReport(battle, options, mySide);
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
            var profile = ProfileStore.Record(battle, options.SaveDir, mySide, opponentRating);
            var stats = profile.Packs[battle.Db.PackId];
            Console.WriteLine("本包战绩：" + stats.Wins + " 胜 / " + stats.Losses + " 负 / " + stats.Draws + " 平");
            if (opponentRating != null)
            {
                var rating = profile.Ratings[Profile.DefaultRatingFormat];
                Console.WriteLine("本机排位（" + Profile.DefaultRatingFormat + "）：" + rating.Rating + " 分 · "
                    + RatingSystem.TierOf(rating.Rating).Name + " · " + rating.Games + " 局");
            }
            Console.WriteLine("档案已保存：" + Path.Combine(options.SaveDir, "profile.json"));
            return replayWarning == null && !reportFailed && !shareFailed && !libraryFailed ? 0 : 1;
        }
    }
}
