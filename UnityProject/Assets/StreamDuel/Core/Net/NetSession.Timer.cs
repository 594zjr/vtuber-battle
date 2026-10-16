using System;
using System.Collections.Generic;
using System.Globalization;

namespace StreamDuel.Core
{
    public sealed partial class NetSession
    {
        private const double TimerGraceSeconds = 10;
        private double? configuredTurnSeconds, configuredBankSeconds;
        private double commitWaitSeconds, revealWaitSeconds, hashWaitSeconds;
        private bool revealTimerStarted;

        public TurnClock Clock { get; private set; }
        public long TimerRevision { get; private set; }

        private bool ConfigureTimer(bool host, double? turnSeconds, double? bankSeconds)
        {
            if (turnSeconds.HasValue != bankSeconds.HasValue
                || turnSeconds.HasValue && (!ValidSeconds(turnSeconds.Value) || !ValidSeconds(bankSeconds.Value)))
            {
                Fault(NetState.Error, "计时参数必须同时提供有限、非负的回合秒数和总时间秒数。");
                return false;
            }
            if (host && turnSeconds.HasValue)
            {
                configuredTurnSeconds = turnSeconds;
                configuredBankSeconds = bankSeconds;
                Clock = new TurnClock(turnSeconds.Value, bankSeconds.Value);
            }
            return true;
        }

        private string TimerHello()
        {
            string hello = ",\"timerVersion\":1";
            if (configuredTurnSeconds.HasValue)
                hello += ",\"timer\":{\"turnSeconds\":" + configuredTurnSeconds.Value.ToString("R", CultureInfo.InvariantCulture)
                    + ",\"bankSeconds\":" + configuredBankSeconds.Value.ToString("R", CultureInfo.InvariantCulture) + "}";
            return hello;
        }

        private void ReceiveTimerHello(Dictionary<string, object> obj)
        {
            bool hasTimer = obj.TryGetValue("timer", out var timer);
            if (MySide == 0)
            {
                if (hasTimer) throw new FormatException("计时规则由主机决定，客机不能声明或更改计时秒数。");
                if (Clock != null) RequireTimerCapability(obj);
                return;
            }
            if (!hasTimer) return;
            RequireTimerCapability(obj);
            if (!(timer is Dictionary<string, object> values))
                throw new FormatException("主机计时字段 timer 必须是对象。");
            Clock = new TurnClock(ReadTimerSeconds(values, "turnSeconds"), ReadTimerSeconds(values, "bankSeconds"));
        }

        private static void RequireTimerCapability(Dictionary<string, object> obj)
        {
            if (!obj.TryGetValue("timerVersion", out var version) || !(version is double number) || number != 1)
                throw new FormatException("对方未声明支持计时协议 timerVersion 1，无法启用对战计时。");
        }

        private static double ReadTimerSeconds(Dictionary<string, object> values, string key)
        {
            if (!values.TryGetValue(key, out var value) || !(value is double seconds) || !ValidSeconds(seconds))
                throw new FormatException("主机计时字段「" + key + "」必须是有限、非负的秒数。");
            return seconds;
        }

        private static bool ValidSeconds(double seconds)
        {
            return !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0;
        }

        private bool TimerParticipant(int side)
        {
            return phase == Phase.Choose || phase == Phase.Replace && needsReplacement[side];
        }

        private void BeginTimerPhase()
        {
            if (Clock == null) return;
            commitWaitSeconds = revealWaitSeconds = hashWaitSeconds = 0;
            revealTimerStarted = false;
            TimerRevision++;
            if (phase == Phase.Choose || phase == Phase.Replace) Clock.StartTurn();
            for (int side = 0; side < 2; side++)
                if (!TimerParticipant(side)) Clock.Submit(side);
        }

        private void BeginRevealTimerIfReady()
        {
            int peer = 1 - MySide;
            if (Clock == null || revealTimerStarted || !TimerParticipant(peer)
                || commitments[peer] == null || revealed[peer] || !AllCommitmentsReady()) return;
            revealTimerStarted = true;
            revealWaitSeconds = 0;
            TimerRevision++;
        }

        private void BeginHashTimer()
        {
            hashWaitSeconds = 0;
            if (Clock != null) TimerRevision++;
        }

        public void Tick(double seconds)
        {
            if (!ValidSeconds(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds), "推进时间必须是有限、非负的秒数。");
            // 重连窗口只由 Poll(elapsedSeconds) 计时，不能同时耗尽选招预算或十秒网络宽限。
            if (Clock == null || Battle == null || Terminal || IsDisconnected) return;
            if (TickTerminationBarrier(seconds)) return;
            double remaining = seconds;
            int peer = 1 - MySide;
            bool automaticallySubmitted = false;
            while (!Terminal && !IsDisconnected)
            {
                // 只提交本方选择，远端始终必须自己提交并通过承诺校验。
                if (hashes.Count == 0 && TimerParticipant(MySide) && !HasSubmitted && Clock.TimedOut(MySide))
                {
                    automaticallySubmitted = true;
                    if (phase == Phase.Choose) Submit(TurnClock.TimeoutAction(Battle, MySide));
                    else
                    {
                        int replacement = TurnClock.TimeoutReplacement(Battle, MySide);
                        if (replacement < 0) { Fault(NetState.Error, "补位超时，但本方没有可上场的队友。"); return; }
                        SubmitReplace(replacement);
                    }
                    if (Terminal || IsDisconnected) return;
                }
                if (remaining <= 0) return;
                if (hashes.Count > 0)
                {
                    // 新消息现在才发出，不能把发送前的时间追算为对端的响应迟延。
                    if (automaticallySubmitted) return;
                    hashWaitSeconds += Math.Min(remaining, TimerGraceSeconds - hashWaitSeconds);
                    if (hashWaitSeconds >= TimerGraceSeconds)
                        Fault(NetState.Error, "等待对方状态哈希确认超过 10 秒，对方已掉线。");
                    return;
                }

                bool waitingCommit = TimerParticipant(peer) && commitments[peer] == null && Clock.TimedOut(peer);
                bool waitingReveal = !automaticallySubmitted && TimerParticipant(peer)
                    && commitments[peer] != null && !revealed[peer] && AllCommitmentsReady();
                double step = remaining;
                for (int side = 0; side < 2; side++)
                    if (TimerParticipant(side) && commitments[side] == null && !Clock.TimedOut(side))
                        step = Math.Min(step, Clock.TurnLeft(side) + Clock.BankLeft(side));
                if (waitingCommit) step = Math.Min(step, TimerGraceSeconds - commitWaitSeconds);
                if (waitingReveal) step = Math.Min(step, TimerGraceSeconds - revealWaitSeconds);

                // 按超时边界分段，让一次大 Tick 的剩余时间仍计入网络宽限。
                for (int side = 0; side < 2; side++)
                    if (TimerParticipant(side) && commitments[side] == null) Clock.Tick(side, step);
                remaining -= step;
                if (waitingCommit) commitWaitSeconds += step;
                if (waitingReveal) revealWaitSeconds += step;
                if (commitWaitSeconds >= TimerGraceSeconds)
                    Fault(NetState.Error, "对方计时预算已用完，额外等待 10 秒仍未收到承诺，对方已掉线。");
                else if (revealWaitSeconds >= TimerGraceSeconds)
                    Fault(NetState.Error, "对方承诺已齐备，但超过 10 秒仍未公开行动，对方已掉线。");
            }
        }
    }
}
