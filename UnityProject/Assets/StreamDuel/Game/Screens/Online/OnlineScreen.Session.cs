using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class OnlineScreen
    {
        readonly Stopwatch networkTime = new Stopwatch();
        TcpHost listener;
        PlayerRating openingRating;
        GameSettings pendingSettings;
        string pendingAddress, renderedSignature, settlementMessage, chatNotice;
        int pendingPort;
        double previousPoll, nextReconnect;

        internal string SaveDirectoryOverride { get; set; }

        string SaveDirectory
        {
            get
            {
                if (SaveDirectoryOverride != null) return SaveDirectoryOverride;
                // 验收必须显式注入工作树内的目录，旧验收也不能意外触及玩家档案。
                if (GameRuntimeSettings.IsSmokeTest) throw new InvalidOperationException("联网验收尚未指定隔离存档目录。");
                return Application.persistentDataPath;
            }
        }

        PlayerRating LoadRating()
        {
            var profile = FileSaveStore.LoadProfileSnapshot(SaveDirectory);
            return profile.Ratings.TryGetValue(Profile.DefaultRatingFormat, out var rating) ? rating.Clone() : new PlayerRating();
        }

        void TickNetwork()
        {
            if (attempt != null)
            {
                if (Session == null)
                {
                    ListeningPort = attempt.ListeningPort;
                    TickBeacon();
                }
                bool ready = attempt.TakeResult(out var connected, out var error, out var notice);
                if (Session == null) status.text = notice + (beaconNotice == null ? "" : "\n" + beaconNotice);
                if (ready)
                {
                    if (Session == null) listener = attempt.TakeListener();
                    attempt.Dispose();
                    attempt = null;
                    StopBeacon();
                    if (error != null)
                    {
                        if (Session == null) { Fail(error, true); return; }
                        nextReconnect = networkTime.Elapsed.TotalSeconds + 1;
                    }
                    else if (Session == null)
                    {
                        transport = connected;
                        Session = new NetSession(SelectedContent, transport, pendingHost, pendingTeam, pendingName, null,
                            turnSeconds: pendingHost && pendingSettings.TurnTimer ? pendingSettings.TurnSeconds : (double?)null,
                            bankSeconds: pendingHost && pendingSettings.TurnTimer ? 0 : (double?)null,
                            enableTermination: true, rating: openingRating, enableReconnect: true);
                        phaseTime.Restart();
                        networkTime.Restart();
                        previousPoll = nextReconnect = 0;
                    }
                    else if (Session.IsDisconnected && !Session.IsReconnecting)
                    {
                        CloseTransport();
                        transport = connected;
                        Session.Reconnect(connected, Session.SessionId);
                        IsClosing = false;
                        closeTime.Reset();
                    }
                    else Task.Run(() => connected.Dispose());
                }
            }
            if (Session == null) return;
            double now = networkTime.Elapsed.TotalSeconds;
            double elapsed = Math.Max(0, now - previousPoll);
            previousPoll = now;
            bool wasDisconnected = Session.IsDisconnected;
            NetState oldState = Session.State;
            int oldEvents = Session.Battle?.Record.Events.Count ?? -1;
            long oldTimer = Session.TimerRevision;
            Session.Poll(elapsed);
            // 收到消息后新阶段才开始；恢复窗口和上一阶段时间不能追扣新预算。
            bool samePhase = !wasDisconnected && !Session.IsDisconnected && oldState == Session.State
                && oldEvents == (Session.Battle?.Record.Events.Count ?? -1) && oldTimer == Session.TimerRevision;
            Session.Tick(samePhase ? elapsed : 0);
            if (Session.State == NetState.Error || Session.State == NetState.Desync)
            {
                RefreshBattle();
                Fail(Session.Error ?? "联网会话已中止，请重新连接。", false);
                return;
            }
            if (Session.State == NetState.Handshake)
            {
                status.text = "已连接，正在核对协议与内容包……";
                // 承诺与公开各有独立等待期限，由上面的 Core Tick 统一处理。
                return;
            }
            editor.style.display = DisplayStyle.None;
            if (Session.IsDisconnected)
            {
                IsClosing = false;
                closeTime.Reset();
                status.text = "对方断线，等待重连（剩余 " + Math.Ceiling(Session.ReconnectSecondsRemaining) + " 秒）"
                    + (Session.IsReconnecting ? "；正在核对并补齐原会话。" : "；可取消返回，不会保存未完成对局。");
                if (!Session.IsReconnecting && attempt == null && now >= nextReconnect)
                {
                    nextReconnect = now + 1;
                    attempt = new OnlineConnectionAttempt(pendingHost, pendingAddress,
                        pendingHost ? ListeningPort : pendingPort, listener);
                }
            }
            else if (Session.State == NetState.Over) FinishNetwork();
            else status.text = Session.State == NetState.Choose ? "请选择本回合行动。双方锁定后一起结算。"
                : Session.State == NetState.Replace ? "我方需要补位，请选择下一位开播的队员。"
                : Session.HasSubmitted ? "选择已锁定，正在等待对方或核对结果……" : "正在等待对方补位或核对结果……";
            RefreshBattle();
        }

        void FinishNetwork()
        {
            if (!IsClosing)
            {
                Session.PrepareClose();
                if (Session.IsDisconnected || Session.State != NetState.Over) return;
                try { transport.CompleteSending(); }
                catch (IOException)
                {
                    Session.Poll();
                    if (Session.IsDisconnected) return;
                    throw;
                }
                IsClosing = true;
                closeTime.Restart();
            }
            status.text = "最终状态已核对，正在等待对方完成收尾……";
            if (transport.ReceiveDrained)
            {
                // EOF 可能在上次轮询之后发布，仍须让会话检查 goodbye 及迟到错误。
                Session.Poll();
                if (Session.IsDisconnected) { IsClosing = false; return; }
                if (Session.State != NetState.Over) { Fail(Session.Error ?? "终局核对失败。", false); return; }
                if (!transport.ReceiveDrained) return;
                IsClosing = false;
                IsFinished = true;
                var names = Session.DisplayNames;
                status.text = Session.Battle.Winner < 0 ? "对战完成 · 平局"
                    : "对战完成 · " + names[Session.Battle.Winner] + " 获胜";
                SaveFinishedBattle();
                CloseTransport();
                CloseListener();
            }
            else if (closeTime.Elapsed.TotalSeconds >= 10)
                Fail("终局收尾超时，未能确认对方正常结束。本局未保存。", false);
        }

        void SaveFinishedBattle()
        {
            var messages = new System.Collections.Generic.List<string>();
            var battle = Session.Battle;
            string directory = SaveDirectory;
            try { new FileSaveStore(directory).SaveReplay(battle); messages.Add("回放已保存。"); }
            catch (Exception ex) { messages.Add("回放保存提示：" + ex.Message); }
            try
            {
                new ReplayLibrary(Path.Combine(directory, "replay-library"), policy: battle.Db.Policy)
                    .Save(battle.Record, BattleSummary.From(battle));
                messages.Add("录像库已更新。");
            }
            catch (Exception ex) { messages.Add("录像库保存提示：" + ex.Message); }
            try
            {
                var profile = new FileSaveStore(directory).RecordProfile(battle, Session.MySide, Session.OpponentRating);
                var rating = profile.Ratings[Profile.DefaultRatingFormat];
                long change = (long)rating.Rating - openingRating.Rating;
                messages.Add("本机排位：开局 " + openingRating.Rating + " → 已保存 " + rating.Rating
                    + "（较开局 " + change.ToString("+0;-0;0", CultureInfo.InvariantCulture) + "） · "
                    + RatingSystem.TierOf(rating.Rating).Name + " · 本季 " + rating.Games + " 局。");
                messages.Add("分数为双方自报，仅保存本机结果，不防作弊。");
            }
            catch (Exception ex) { messages.Add("战绩与排位保存失败：" + ex.Message); }
            settlementMessage = string.Join("\n", messages.Select(message => TextInput.Clean(message, TextInputKind.Note).Text));
        }

        bool RefreshInput()
        {
            if (disposed || failed || IsClosing || IsFinished || Session?.Battle == null || Session.IsDisconnected) return false;
            int events = Session.Battle.Record.Events.Count;
            int turn = Session.Battle.Turn;
            var state = Session.State;
            Tick(0);
            return !disposed && !failed && !IsClosing && !IsFinished && !Session.IsDisconnected
                && Session.State == state && Session.Battle.Turn == turn && Session.Battle.Record.Events.Count == events;
        }

        void Terminate(string command)
        {
            if (!RefreshInput()) return;
            try
            {
                if (command == "surrender" && Session.CanSurrender) Session.Surrender();
                else if (command == "offer" && Session.CanOfferDraw) Session.OfferDraw();
                else if ((command == "accept" || command == "reject") && Session.CanRespondDraw) Session.RespondDraw(command == "accept");
                Tick(0);
            }
            catch (InvalidOperationException ex) { chatNotice = TextInput.Clean(ex.Message, TextInputKind.Note).Text; RefreshBattle(); }
        }

        void SendChat(int index)
        {
            if (disposed || failed || IsClosing || IsFinished || Session == null) return;
            Tick(0);
            if (failed || IsClosing || IsFinished || Session.IsDisconnected) return;
            chatNotice = Session.SendQuickChat(index, networkTime.Elapsed.TotalSeconds)
                ? "我方：" + QuickChat.Text(index) : "快捷语发送过于频繁或当前不能发送，请稍后再试。";
            RefreshBattle();
        }

        void SetChatMuted(bool value)
        {
            if (Session == null) return;
            Session.MuteOpponent = value;
            RefreshBattle();
        }

        string ViewSignature()
        {
            var clock = Session.Clock;
            return Session.State + ":" + Session.Battle.Log.Count + ":" + Session.HasSubmitted + ":" + Session.IsDisconnected
                + ":" + Session.CanSurrender + ":" + Session.CanOfferDraw + ":" + Session.CanRespondDraw
                + ":" + Session.DrawOffered + ":" + Session.DrawOfferPending + ":" + Session.MuteOpponent
                + ":" + Session.ReceivedChats.Count + ":" + IsClosing + ":" + IsFinished + ":" + chatNotice + ":" + settlementMessage
                + (clock == null ? "" : ":" + Math.Ceiling(clock.TurnLeft(0)) + ":" + Math.Ceiling(clock.BankLeft(0))
                    + ":" + Math.Ceiling(clock.TurnLeft(1)) + ":" + Math.Ceiling(clock.BankLeft(1)));
        }

        void LeaveSession()
        {
            var leaving = Session;
            if (leaving == null || IsFinished || leaving.State == NetState.Error || leaving.State == NetState.Desync) return;
            var closing = transport;
            transport = null;
            // 返回立即释放界面；有界的网络写入和接收线程回收留给后台。
            Task.Run(() =>
            {
                try { leaving.Leave("对方已断开（主动退出，对局未完成）。"); }
                finally { closing?.Dispose(); }
            });
        }

        void CloseListener()
        {
            listener?.Dispose();
            listener = null;
        }
    }
}
