using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace StreamDuel.Core
{
    public sealed partial class NetSession
    {
        public const int ReconnectProtocolVersion = 3;
        public const double ReconnectTimeoutSeconds = 60;
        public string SessionId { get; private set; }
        public bool IsDisconnected { get; private set; }
        public bool IsReconnecting { get; private set; }
        public double ReconnectSecondsRemaining
        {
            get { return Math.Max(0, ReconnectTimeoutSeconds - disconnectedSeconds); }
        }

        readonly bool enableReconnect;
        readonly List<string> sentMessages = new List<string>();
        readonly List<string> receivedMessages = new List<string>();
        readonly List<ResumeCheckpoint> checkpoints = new List<ResumeCheckpoint>();
        double disconnectedSeconds;
        bool receivedResume, receivedResumed, peerCompleted;
        int peerReplayEnd, peerEventIndex, peerTurn;
        string peerResumeHash;

        sealed class ResumeCheckpoint
        {
            public int Turn;
            public string Hash;
        }

        static string NewSessionId()
        {
            // 与承诺 nonce 一样，网络会话标识不参与规则、回放或对战随机流。
            var bytes = new byte[16];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Hex(bytes);
        }

        void RememberCheckpoint()
        {
            if (!enableReconnect) return;
            checkpoints.Add(new ResumeCheckpoint
            {
                Turn = Battle.Turn,
                Hash = Battle.StateHash().ToString("x16", CultureInfo.InvariantCulture)
            });
        }

        void TickReconnect(double elapsedSeconds)
        {
            if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) || elapsedSeconds < 0)
                throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), "轮询经过的秒数必须是非负有限数。");
            if (!IsDisconnected || Faulted) return;
            disconnectedSeconds += elapsedSeconds;
            if (disconnectedSeconds >= ReconnectTimeoutSeconds)
                Fault(NetState.Error, "对方已断开，等待重连超过 60 秒，对局未完成。", false);
        }

        bool PauseForReconnect(Exception failure)
        {
            if (!enableReconnect || Battle == null || Faulted) return false;
            if (!(failure is IOException) && !(failure is ObjectDisposedException)) return false;
            if (failure is InvalidDataException) return false;
            if (!IsDisconnected) disconnectedSeconds = 0;
            IsDisconnected = true;
            IsReconnecting = false;
            State = NetState.Waiting;
            return true;
        }

        /// <summary>只替换短暂断线的传输；双方必须保留各自的原会话对象。</summary>
        public void Reconnect(INetTransport replacement, string sessionId)
        {
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));
            if (!enableReconnect || !IsDisconnected || IsReconnecting || Faulted || Battle == null)
                throw new InvalidOperationException("当前会话不能替换重连传输。");
            if (!string.Equals(SessionId, sessionId, StringComparison.Ordinal))
                throw new ArgumentException("重连会话标识不匹配。", nameof(sessionId));
            if (!string.Equals(contentHash, db.ContentHash(), StringComparison.Ordinal))
            {
                Fault(NetState.Error, "内容不一致：重连前本端内容已变化。", false);
                return;
            }
            transport = replacement;
            IsReconnecting = true;
            receivedResume = receivedResumed = false;
            peerResumeHash = null;
            peerReplayEnd = 0;
            var checkpoint = checkpoints[checkpoints.Count - 1];
            SendControl("{\"type\":\"resume\"" + IdentityFields
                + ",\"received\":" + Number(receivedMessages.Count)
                + ",\"eventIndex\":" + Number(checkpoints.Count - 1)
                + ",\"turn\":" + Number(checkpoint.Turn)
                + ",\"hash\":" + NetJson.Quote(checkpoint.Hash) + "}");
        }

        public void Leave(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("退出原因不能为空。", nameof(reason));
            Fault(NetState.Error, reason);
        }

        /// <summary>终局半关闭前发送确认，区别于最终哈希仍在途中时的断线。</summary>
        public void PrepareClose()
        {
            if (State != NetState.Over || IsDisconnected)
                throw new InvalidOperationException("尚未完成终局校验，不能关闭连接。");
            if (enableReconnect) Send("{\"type\":\"goodbye\"}");
        }

        string IdentityFields
        {
            get { return ",\"sessionId\":" + NetJson.Quote(SessionId) + ",\"side\":" + Number(MySide); }
        }

        bool SendControl(string message)
        {
            try { transport.Send(message); return true; }
            catch (Exception ex)
            {
                if (!PauseForReconnect(ex)) Fault(NetState.Error, "发送消息失败：" + ex.Message, false);
                return false;
            }
        }

        bool SendJournaled(string message)
        {
            // 先保存原始承诺/公开再尝试发送，发送报错也不能重新选择或生成 nonce。
            sentMessages.Add(message);
            if (!IsDisconnected || IsReconnecting) SendPacket(sentMessages.Count, message);
            return !Faulted;
        }

        bool SendPacket(int sequence, string payload)
        {
            return SendControl("{\"type\":\"packet\"" + IdentityFields
                + ",\"sequence\":" + Number(sequence) + ",\"payload\":" + NetJson.Quote(payload) + "}");
        }

        void ValidateIdentity(Dictionary<string, object> obj)
        {
            if (NetJson.Text(obj, "sessionId") != SessionId
                || NetJson.Integer(obj, "side", 0, 1) != 1 - MySide)
                throw new FormatException("重连会话标识或对方角色不匹配。");
            if (!string.Equals(contentHash, db.ContentHash(), StringComparison.Ordinal))
                throw new FormatException("内容不一致：会话进行中本端内容已变化。");
        }

        void ReceiveReconnectMessage(string message)
        {
            var obj = NetJson.Object(message);
            string type = NetJson.Text(obj, "type");
            // 尚未建立 Battle 的对方可能因握手校验失败而发送原始 error。
            if (type == "error" && sentMessages.Count == 0 && !IsDisconnected)
            {
                Receive(message);
                return;
            }
            ValidateIdentity(obj);
            if (type == "packet") ReceivePacket(obj);
            else if (type == "resume") ReceiveResume(obj);
            else if (type == "resumed") ReceiveResumed(obj);
            else throw new FormatException("重连协议收到未知消息类型：" + type);
        }

        void ReceivePacket(Dictionary<string, object> obj)
        {
            int sequence = (int)NetJson.Integer(obj, "sequence", 1, int.MaxValue);
            string payload = NetJson.Text(obj, "payload");
            if (sequence <= receivedMessages.Count)
            {
                if (!IsReconnecting || receivedMessages[sequence - 1] != payload)
                    throw new FormatException("收到重复或被更改的协议消息。");
                return;
            }
            if (sequence != receivedMessages.Count + 1) throw new FormatException("协议消息序号不连续。");
            receivedMessages.Add(payload);
            Receive(payload);
        }

        void ReceiveResume(Dictionary<string, object> obj)
        {
            if (!IsReconnecting || receivedResume) throw new FormatException("当前不能接收重复或未经请求的重连。");
            int received = (int)NetJson.Integer(obj, "received", 0, sentMessages.Count);
            peerEventIndex = (int)NetJson.Integer(obj, "eventIndex", 0, int.MaxValue);
            peerTurn = (int)NetJson.Integer(obj, "turn", 1, int.MaxValue);
            peerResumeHash = NetJson.Text(obj, "hash");
            if (!LowerHex(peerResumeHash, 16)) throw new FormatException("重连状态哈希格式无效。");
            receivedResume = true;
            if (peerEventIndex < checkpoints.Count && !VerifyPeerCheckpoint()) return;
            int replayEnd = sentMessages.Count;
            for (int index = received; index < replayEnd; index++)
                if (!SendPacket(index + 1, sentMessages[index])) return;
            SendControl("{\"type\":\"resumed\"" + IdentityFields + ",\"sent\":" + Number(replayEnd) + "}");
        }

        void ReceiveResumed(Dictionary<string, object> obj)
        {
            if (!IsReconnecting || !receivedResume || receivedResumed)
                throw new FormatException("当前不能接收重连完成确认。");
            peerReplayEnd = (int)NetJson.Integer(obj, "sent", 0, int.MaxValue);
            // 对方先按顺序发完缺失消息，再发这个边界；不能用缺失消息的确认跳过补齐。
            if (receivedMessages.Count < peerReplayEnd) throw new FormatException("重连缺失消息尚未补齐。");
            receivedResumed = true;
        }

        bool VerifyPeerCheckpoint()
        {
            if (peerEventIndex >= checkpoints.Count)
            {
                Fault(NetState.Desync, "重连状态不同步：缺少对方的已结算操作。");
                return false;
            }
            var local = checkpoints[peerEventIndex];
            if (local.Turn != peerTurn || local.Hash != peerResumeHash)
            {
                Fault(NetState.Desync, "重连状态不同步：第 " + Number(peerTurn) + " 回合操作 " + Number(peerEventIndex)
                    + "，本端哈希 " + local.Hash + "，对方哈希 " + peerResumeHash + "。");
                return false;
            }
            return true;
        }

        void TryFinishReconnect()
        {
            if (Faulted || !IsReconnecting || !receivedResume || !receivedResumed) return;
            if (!VerifyPeerCheckpoint()) return;
            IsDisconnected = IsReconnecting = false;
            disconnectedSeconds = 0;
            Battle.RestoreReplayRng(rngCheckpoint);
            if (hashes.Count > 0) State = NetState.Waiting;
            else if (phase == Phase.Over) State = NetState.Over;
            else if (HasSubmitted) State = NetState.Waiting;
            else if (phase == Phase.Choose) State = NetState.Choose;
            else State = needsReplacement[MySide] ? NetState.Replace : NetState.Waiting;
            Advance();
        }
    }
}
