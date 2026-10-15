using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public sealed partial class NetSession
    {
        private readonly bool[] aiControlled = new bool[2];
        private Dictionary<string, object> pendingTakeoverHello;
        private bool takeoverReady;
        private bool aiSelectionsPrepared;
        private bool disconnectedQueueDrained;
        private bool incomingQueueDrained, commitTakeoverPending;
        private uint? aiResolutionRng;
        private double takeoverHandshakeSeconds, disconnectedSeconds;
        private Battle confirmedBattle;

        public bool AiTakeoverOnTimeout { get; private set; }
        public bool IsOfflineContinuation { get; private set; }
        public bool WaitingForAiTakeover { get; private set; }

        public bool IsAiControlled(int side)
        {
            if (side < 0 || side > 1) throw new ArgumentOutOfRangeException(nameof(side));
            return aiControlled[side];
        }

        private int AiMask { get { return (aiControlled[0] ? 1 : 0) | (aiControlled[1] ? 2 : 0); } }

        private string TakeoverHello()
        {
            return AiTakeoverOnTimeout ? ",\"aiTakeoverVersion\":1,\"aiTakeover\":true" : "";
        }

        private bool ReceiveTakeoverHello(Dictionary<string, object> obj)
        {
            bool declared = obj.TryGetValue("aiTakeover", out var enabled);
            bool hasVersion = obj.ContainsKey("aiTakeoverVersion");
            if (MySide == 0)
            {
                if (declared || hasVersion) throw new FormatException("电脑代打规则只能由主机声明。");
                if (!AiTakeoverOnTimeout || takeoverReady) return true;
                if (pendingTakeoverHello != null) throw new FormatException("对方重复发送握手。");
                pendingTakeoverHello = obj;
                TimerRevision++;
                return false;
            }
            if (!declared && !hasVersion) return true;
            if (!(enabled is bool value) || !value || NetJson.Integer(obj, "aiTakeoverVersion", 1, 1) != 1)
                throw new FormatException("主机电脑代打选项或能力版本无效。");
            AiTakeoverOnTimeout = true;
            return Send("{\"type\":\"takeover-ready\",\"version\":1}");
        }

        private void ReceiveTakeoverReady(Dictionary<string, object> obj)
        {
            if (MySide != 0 || !AiTakeoverOnTimeout || pendingTakeoverHello == null || takeoverReady
                || Battle != null || obj.Count != 2 || NetJson.Integer(obj, "version", 1, 1) != 1)
                throw new FormatException("电脑代打能力确认无效或重复。");
            takeoverReady = true;
            var hello = pendingTakeoverHello;
            pendingTakeoverHello = null;
            ReceiveHello(hello);
        }

        private bool TickTakeoverWait(double seconds)
        {
            if (Terminal) return true;
            if (Battle == null)
            {
                if (AiTakeoverOnTimeout && MySide == 0 && !takeoverReady)
                {
                    takeoverHandshakeSeconds += Math.Min(seconds, TimerGraceSeconds - takeoverHandshakeSeconds);
                    if (takeoverHandshakeSeconds >= TimerGraceSeconds)
                        Fault(NetState.Error, "对方未在 10 秒内确认支持电脑代打，无法开始对战。");
                }
                return true;
            }
            if (!WaitingForAiTakeover) return false;
            if (commitTakeoverPending)
            {
                if (incomingQueueDrained) StartOfflineContinuation();
                return true;
            }
            disconnectedSeconds += Math.Min(seconds, TimerGraceSeconds - disconnectedSeconds);
            if (disconnectedSeconds >= TimerGraceSeconds && disconnectedQueueDrained) StartOfflineContinuation();
            return true;
        }

        private bool SelectionParticipant(int side)
        {
            return phase == Phase.Choose || phase == Phase.Replace && needsReplacement[side];
        }

        private void BeginTakeoverPhase()
        {
            if (!AiTakeoverOnTimeout) return;
            if (Terminal)
            {
                if (State == NetState.Over) WaitingForAiTakeover = false;
                return;
            }
            if (!IsOfflineContinuation) confirmedBattle = Battle.Clone();
            if (WaitingForAiTakeover)
            {
                State = NetState.Waiting;
                return;
            }
            if (aiControlled[MySide] && SelectionParticipant(MySide))
            {
                HasSubmitted = true;
                State = NetState.Waiting;
            }
            // 离线双 AI 一次 Poll 只推进一阶段，不能递归跑完整局。
            if (!IsOfflineContinuation) Advance();
        }

        private void DeclareLocalTakeover()
        {
            if (aiControlled[MySide] || HasSubmitted || commitments[MySide] != null)
                throw new InvalidOperationException("已经锁定的选择不能更换为电脑代打。");
            aiControlled[MySide] = true;
            HasSubmitted = true;
            Clock?.Submit(MySide);
            State = NetState.Waiting;
            if (IsOfflineContinuation || Send("{\"type\":\"takeover\",\"turn\":" + Number(turn)
                + ",\"kind\":" + NetJson.Quote(SelectionKind) + "}"))
            {
                BeginRevealTimerIfReady();
                Advance();
            }
        }

        private void ReceiveTakeover(Dictionary<string, object> obj)
        {
            if (!AiTakeoverOnTimeout || obj.Count != 3)
                throw new FormatException("本局未启用电脑代打，或代打声明结构无效。");
            ValidateSelectionKind(obj);
            int peer = 1 - MySide;
            if (aiControlled[peer] || commitments[peer] != null || revealed[peer])
                throw new FormatException("对方重复声明电脑代打，或试图替换已锁定的承诺。");
            aiControlled[peer] = true;
            Clock?.Submit(peer);
            CancelUnresponsiveTakeover();
        }

        private void PrepareAiSelections()
        {
            if (!AiTakeoverOnTimeout || aiSelectionsPrepared || !AllCommitmentsReady()) return;
            aiSelectionsPrepared = true;
            // 人类选招期间产生的建议抽样不属于共同状态；代打双方按相同次序共用检查点。
            Battle.RestoreReplayRng(rngCheckpoint);
            for (int side = 0; side < 2; side++)
            {
                if (!aiControlled[side] || !SelectionParticipant(side)) continue;
                if (phase == Phase.Choose)
                {
                    var action = Ai.Choose(Battle, side, AiLevel.Normal);
                    if (action.Type == ActionType.Struggle || action.Type == ActionType.PassControlled)
                        action = BattleAction.Pass();
                    actions[side] = action;
                }
                else replacements[side] = Ai.ChooseReplacement(Battle, side, AiLevel.Normal);
                revealed[side] = true;
            }
            aiResolutionRng = Battle.NetRngState;
            Battle.RestoreReplayRng(rngCheckpoint);
        }

        private void SubmitOfflineSelection(string wire)
        {
            StoreRevealedSelection(MySide, wire);
            HasSubmitted = true;
            Clock?.Submit(MySide);
            State = NetState.Waiting;
            Advance();
        }

        private bool BeginDisconnectedTakeover(bool receiveDrained = false)
        {
            if (!AiTakeoverOnTimeout || Battle == null || Terminal || confirmedBattle == null) return false;
            disconnectedQueueDrained |= receiveDrained;
            commitTakeoverPending = false;
            if (WaitingForAiTakeover || IsOfflineContinuation) return true;
            WaitingForAiTakeover = true;
            disconnectedSeconds = 0;
            State = NetState.Waiting;
            TimerRevision++;
            return true;
        }

        private void BeginUnresponsiveTakeover()
        {
            commitTakeoverPending = true;
            WaitingForAiTakeover = true;
            State = NetState.Waiting;
            TimerRevision++;
            if (incomingQueueDrained) StartOfflineContinuation();
        }

        private void CancelUnresponsiveTakeover()
        {
            if (!commitTakeoverPending) return;
            commitTakeoverPending = false;
            WaitingForAiTakeover = false;
            TimerRevision++;
            State = HasSubmitted ? NetState.Waiting : phase == Phase.Choose ? NetState.Choose
                : needsReplacement[MySide] ? NetState.Replace : NetState.Waiting;
        }

        private void StartOfflineContinuation()
        {
            // 未确认阶段整体废弃，不能把未公开的承诺或未确认的哈希当成已验证结果。
            Battle = confirmedBattle.Clone();
            Battle.Record.IsOfflineContinuation = true;
            rngCheckpoint = Battle.NetRngState;
            hashes.Clear();
            IsOfflineContinuation = true;
            WaitingForAiTakeover = false;
            commitTakeoverPending = false;
            aiControlled[1 - MySide] = true;
            BeginPhase();
        }
    }
}
