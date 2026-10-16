using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public sealed partial class NetSession
    {
        public const int TerminationProtocolVersion = 4;
        readonly bool terminationEnabled;
        bool terminationFrozen, terminationHashPending, terminationKeepPhase, terminationReadySent;
        int terminationPhase, terminationSequence;
        string terminationRequest;
        double terminationWaitSeconds;

        public int SessionProtocolVersion { get { return terminationEnabled ? TerminationProtocolVersion : ProtocolVersion; } }
        public bool DrawOffered { get { return Battle != null && Battle.HasDrawOffer(1 - MySide); } }
        public bool DrawOfferPending { get { return Battle != null && Battle.HasDrawOffer(MySide); } }
        public bool CanSurrender { get { return CanRequestTermination; } }
        public bool CanOfferDraw { get { return CanRequestTermination && Battle.CanOfferDraw(MySide); } }
        public bool CanRespondDraw { get { return CanRequestTermination && DrawOffered; } }

        bool CanRequestTermination
        {
            get
            {
                return terminationEnabled && Battle != null && !Terminal && !terminationFrozen && hashes.Count == 0
                    && (Battle.Phase == Phase.Choose || Battle.Phase == Phase.Replace);
            }
        }

        public void Surrender()
        {
            RequireTerminationInput(CanSurrender, "现在不能投降，请等待握手或同步完成。");
            QueueTermination("surrender");
        }

        public void OfferDraw()
        {
            RequireTerminationInput(CanOfferDraw, "现在不能提议和局：请等待同步、上一提议的回应以及 5 回合间隔。");
            QueueTermination("offer");
        }

        public void RespondDraw(bool accept)
        {
            RequireTerminationInput(CanRespondDraw, "现在没有可以回应的对方和局提议，或同步尚未完成。");
            QueueTermination(accept ? "accept" : "reject");
        }

        void RequireTerminationInput(bool allowed, string message)
        {
            if (!terminationEnabled) throw new InvalidOperationException("投降与和局需要双方显式启用终止协议版本 4。");
            if (!allowed) throw new InvalidOperationException(message);
        }

        string TerminationHello() { return terminationEnabled ? ",\"terminationVersion\":1" : string.Empty; }

        void ReceiveTerminationHello(Dictionary<string, object> obj)
        {
            if (!terminationEnabled) return;
            if (!obj.TryGetValue("terminationVersion", out var value) || !(value is double version) || version != 1)
                throw new FormatException("对方未声明终止协议能力 terminationVersion 1。");
        }

        void QueueTermination(string request)
        {
            terminationRequest = request;
            RequestTerminationBarrier();
        }

        void RequestTerminationBarrier()
        {
            if (terminationFrozen) return;
            FreezeTermination();
            if (MySide == 0)
            {
                terminationSequence++;
                Send(TerminationMessage("freeze") + "}");
            }
            else Send(TerminationMessage("wake", false) + "}");
        }

        void FreezeTermination()
        {
            terminationFrozen = true;
            terminationWaitSeconds = 0;
            State = NetState.Waiting;
            if (Clock != null) TimerRevision++;
        }

        string TerminationMessage(string kind, bool sequence = true)
        {
            return "{\"type\":\"termination-" + kind + "\",\"phase\":" + Number(terminationPhase)
                + ",\"turn\":" + Number(turn) + (sequence ? ",\"seq\":" + Number(terminationSequence) : string.Empty);
        }

        void ReceiveTermination(string type, Dictionary<string, object> obj)
        {
            if (!terminationEnabled) throw new FormatException("未协商终止协议，不能接收投降或和局控制消息。");
            if (hashes.Count > 0) throw new FormatException("终止协议必须先等待当前状态哈希确认。");
            if (NetJson.Integer(obj, "phase", 1, int.MaxValue) != terminationPhase
                || NetJson.Integer(obj, "turn", 1, int.MaxValue) != turn)
                throw new FormatException("终止协议的阶段或回合不匹配。");
            switch (type)
            {
                case "termination-wake":
                    if (MySide != 0 || terminationReadySent) throw new FormatException("终止请求的协调角色或顺序错误。");
                    RequestTerminationBarrier();
                    break;
                case "termination-freeze":
                    if (MySide != 1 || terminationReadySent
                        || NetJson.Integer(obj, "seq", 1, int.MaxValue) != terminationSequence + 1)
                        throw new FormatException("终止同步起点重复或序号错误。");
                    terminationSequence++;
                    FreezeTermination();
                    terminationReadySent = true;
                    // TCP 的同向顺序保证 ready 之前的承诺和公开已全部送达协调端。
                    Send(TerminationMessage("ready") + ",\"request\":" + NetJson.Quote(terminationRequest ?? "none")
                        + ",\"resolve\":" + (SelectionsReady() ? "true" : "false") + "}");
                    break;
                case "termination-ready":
                    RequireTerminationSequence(obj);
                    if (MySide != 0 || !terminationFrozen || terminationReadySent)
                        throw new FormatException("没有等待对方终止同步确认，或确认重复。");
                    terminationReadySent = true;
                    string remote = NetJson.Text(obj, "request");
                    string local = terminationRequest ?? "none";
                    ValidateTerminationRequests(local, remote);
                    bool resolve = ReadTerminationBoolean(obj, "resolve") && SelectionsReady();
                    // 先发出决定，再执行；对端总会在本端哈希之前收到完全相同的操作顺序。
                    if (Send(TerminationMessage("apply") + ",\"requests\":[" + NetJson.Quote(local) + ","
                        + NetJson.Quote(remote) + "],\"resolve\":" + (resolve ? "true" : "false") + "}"))
                        ApplyTermination(local, remote, resolve);
                    break;
                case "termination-apply":
                    RequireTerminationSequence(obj);
                    if (MySide != 1 || !terminationFrozen || !terminationReadySent)
                        throw new FormatException("没有就绪的终止同步，不能接收决定。");
                    var requests = NetJson.Array(obj, "requests");
                    if (requests.Count != 2 || !(requests[0] is string first) || !(requests[1] is string second))
                        throw new FormatException("终止同步必须包含双方各一项请求。");
                    if (second != (terminationRequest ?? "none"))
                        throw new FormatException("对方改变了本方已确认的投降或和局请求。");
                    ValidateTerminationRequests(first, second);
                    bool applyResolve = ReadTerminationBoolean(obj, "resolve");
                    if (applyResolve && !SelectionsReady()) throw new FormatException("行动尚未公开齐全，不能提前结算。");
                    ApplyTermination(first, second, applyResolve);
                    break;
                default:
                    throw new FormatException("未知终止协议消息类型：" + type);
            }
        }

        void RequireTerminationSequence(Dictionary<string, object> obj)
        {
            if (NetJson.Integer(obj, "seq", 1, int.MaxValue) != terminationSequence)
                throw new FormatException("终止同步序号不匹配。");
        }

        static bool ReadTerminationBoolean(Dictionary<string, object> obj, string key)
        {
            if (!obj.TryGetValue(key, out var value) || !(value is bool result))
                throw new FormatException("终止协议字段「" + key + "」必须是布尔值。");
            return result;
        }

        void ValidateTerminationRequests(string first, string second)
        {
            var requests = new[] { first, second };
            for (int side = 0; side < 2; side++)
            {
                string request = requests[side];
                if (request != "none" && request != "surrender" && request != "offer" && request != "accept" && request != "reject")
                    throw new FormatException("未知投降或和局请求：" + request);
                if (request == "offer" && !Battle.CanOfferDraw(side))
                    throw new FormatException("和局提议重复或未满足 5 回合间隔。");
                if ((request == "accept" || request == "reject") && !Battle.HasDrawOffer(1 - side))
                    throw new FormatException("不存在可回应的对方和局提议。");
            }
        }

        void ApplyTermination(string first, string second, bool resolve)
        {
            Battle.RestoreReplayRng(rngCheckpoint);
            int operationTurn = Battle.Turn;
            if (first == "surrender" || second == "surrender")
                Battle.ResolveSurrenders(first == "surrender", second == "surrender");
            else
            {
                var requests = new[] { first, second };
                for (int side = 0; side < 2 && Battle.Phase != Phase.Over; side++)
                {
                    if (requests[side] == "offer") Battle.OfferDraw(side);
                    else if (requests[side] == "accept" || requests[side] == "reject")
                        Battle.RespondDraw(side, requests[side] == "accept");
                }
            }
            terminationHashPending = true;
            terminationKeepPhase = !resolve && Battle.Phase != Phase.Over;
            if (resolve && Battle.Phase != Phase.Over) ResolveSelections();
            else CaptureHash(operationTurn);
        }

        void CompleteTerminationBarrier()
        {
            bool keepPhase = terminationKeepPhase;
            terminationFrozen = terminationHashPending = terminationKeepPhase = terminationReadySent = false;
            terminationRequest = null;
            terminationWaitSeconds = 0;
            if (!keepPhase) { BeginPhase(); return; }
            // 拒绝或仅提出和局不会撤回承诺、重置剩余时间，也不会丢失已提交的选择。
            Battle.RestoreReplayRng(rngCheckpoint);
            State = HasSubmitted ? NetState.Waiting : phase == Phase.Choose ? NetState.Choose
                : needsReplacement[MySide] ? NetState.Replace : NetState.Waiting;
            if (Clock != null) TimerRevision++;
        }

        bool TickTerminationBarrier(double seconds)
        {
            if (!terminationFrozen || hashes.Count > 0) return false;
            terminationWaitSeconds += seconds;
            if (terminationWaitSeconds >= TimerGraceSeconds)
                Fault(NetState.Error, "等待对方投降或和局同步超过 10 秒，对方已掉线。");
            return true;
        }
    }
}
