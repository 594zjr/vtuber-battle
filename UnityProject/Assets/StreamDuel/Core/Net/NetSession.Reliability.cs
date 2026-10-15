using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed partial class NetSession
    {
        public const int ResendAfterPolls = 8;
        public const int DeliveryTimeoutPolls = 512;
        public const int ReliableWindow = 256;
        public int PendingReliableCount { get { return unconfirmed.Count; } }
        public bool HasPendingReliableMessages { get { return unconfirmed.Count > 0 || reordered.Count > 0; } }

        private long reliabilityPoll;
        private long nextSequence = 1;
        private long receivedSequence;
        private long gapSequence = -1;
        private long gapStarted;
        private object responseToken;
        private long responseStarted;
        private readonly SortedDictionary<long, PendingPacket> unconfirmed = new SortedDictionary<long, PendingPacket>();
        private readonly SortedDictionary<long, ReceivedPacket> reordered = new SortedDictionary<long, ReceivedPacket>();

        private sealed class PendingPacket
        {
            public string Wire;
            public long FirstSent;
            public long LastSent;
        }

        private sealed class ReceivedPacket
        {
            public string Wire;
            public Dictionary<string, object> Body;
        }

        private void SendReliable(string message)
        {
            if (unconfirmed.Count >= ReliableWindow)
                throw new InvalidOperationException("未确认消息超过可靠传输窗口，连接已停止。");
            if (nextSequence > int.MaxValue)
                throw new InvalidOperationException("消息序号已经耗尽，请重新建立对局。");
            string wire = message.Substring(0, message.Length - 1) + ",\"seq\":"
                + nextSequence.ToString(CultureInfo.InvariantCulture) + "}";
            unconfirmed.Add(nextSequence++, new PendingPacket
            {
                Wire = wire,
                FirstSent = reliabilityPoll,
                LastSent = reliabilityPoll
            });
            transport.Send(wire);
        }

        private void ReceivePacket(string message)
        {
            Dictionary<string, object> obj;
            try { obj = NetJson.Object(message); }
            // 不完整的聊天无法安全确认序号，沿用聊天容错，但不猜测或补造序号。
            catch (FormatException) when (HasQuickChatHeader(message)) { return; }
            string type = NetJson.Text(obj, "type");
            if (type == "ack")
            {
                if (obj.Count != 2) throw new FormatException("确认消息只能包含 type 和 ack。");
                long acknowledged = NetJson.Integer(obj, "ack", 0, int.MaxValue);
                if (acknowledged >= nextSequence) throw new FormatException("对方确认了尚未发送的消息序号。");
                foreach (long sequence in unconfirmed.Keys.TakeWhile(sequence => sequence <= acknowledged).ToArray())
                    unconfirmed.Remove(sequence);
                return;
            }
            // 旧端没有序号时也明确报告版本差异，不能静默降级到不可靠协议。
            if (type == "hello" && !obj.ContainsKey("seq"))
            {
                long version = NetJson.Integer(obj, "version", 0, int.MaxValue);
                if (version != ProtocolVersion)
                    throw new FormatException("协议版本不同：本端 " + Number(ProtocolVersion) + "，对方 " + version + "。");
            }
            long incoming = NetJson.Integer(obj, "seq", 1, int.MaxValue);
            if (incoming <= receivedSequence)
            {
                SendAcknowledgement();
                return;
            }
            if (incoming - receivedSequence > ReliableWindow)
                throw new FormatException("消息序号超出接收窗口。");
            if (reordered.TryGetValue(incoming, out var previous))
            {
                if (!string.Equals(previous.Wire, message, StringComparison.Ordinal))
                    throw new FormatException("相同消息序号携带不同内容。");
                SendAcknowledgement();
                return;
            }
            obj.Remove("seq");
            reordered.Add(incoming, new ReceivedPacket { Wire = message, Body = obj });
            if (incoming != receivedSequence + 1) SendAcknowledgement();
        }

        private void ProcessOrderedMessages(ref int processed)
        {
            while (processed < 64 && reordered.TryGetValue(receivedSequence + 1, out var packet))
            {
                reordered.Remove(++receivedSequence);
                processed++;
                // 先确认已交付的线路包，业务错误再发独立 error；错误状态仍能收尾 ACK。
                SendAcknowledgement();
                if (!Faulted && Receive(packet.Body)) Advance();
            }
        }

        private void SendAcknowledgement()
        {
            try
            {
                transport.Send("{\"type\":\"ack\",\"ack\":" + receivedSequence.ToString(CultureInfo.InvariantCulture) + "}");
            }
            // TCP 已正常半关闭时仍须读出对端最后的错误，不能让无法回 ACK 盖住正文。
            catch (TcpSendCompletedException) when (Terminal) { }
        }

        private void RetryUnconfirmedMessages()
        {
            if (unconfirmed.Values.Any(packet => reliabilityPoll - packet.FirstSent >= DeliveryTimeoutPolls))
            {
                DeliveryFailed("消息在 " + Number(DeliveryTimeoutPolls) + " 次轮询内未获确认，对方可能已掉线。");
                return;
            }
            if (!Faulted && ResponseTimedOut()) return;
            int resent = 0;
            foreach (var packet in unconfirmed.Values)
            {
                if (reliabilityPoll - packet.LastSent < ResendAfterPolls) continue;
                if (resent++ >= 64) break;
                packet.LastSent = reliabilityPoll;
                // 重发完全相同的字节，不再生成承诺 nonce，也不再次提交行动。
                transport.Send(packet.Wire);
            }
        }

        private bool ResponseTimedOut()
        {
            long missing = reordered.Count > 0 && !reordered.ContainsKey(receivedSequence + 1)
                ? receivedSequence + 1 : -1;
            if (missing != gapSequence)
            {
                gapSequence = missing;
                gapStarted = reliabilityPoll;
            }
            if (missing >= 0 && reliabilityPoll - gapStarted >= DeliveryTimeoutPolls)
            {
                DeliveryFailed("等待缺失序号超过 " + Number(DeliveryTimeoutPolls) + " 次轮询，对方可能已掉线。");
                return true;
            }

            // 只约束已必需的协议响应，不把尚未操作的玩家当成断线；聊天不会延长等待。
            object required = null;
            if (State == NetState.Handshake) required = this;
            else if (hashes.Count > 0) required = hashes.Peek();
            else if (!Terminal && TimerParticipant(1 - MySide) && AllCommitmentsReady() && !revealed[1 - MySide])
                required = commitments[1 - MySide];
            if (!ReferenceEquals(required, responseToken))
            {
                responseToken = required;
                responseStarted = ReferenceEquals(required, this) ? 0 : reliabilityPoll;
            }
            if (required == null || reliabilityPoll - responseStarted < DeliveryTimeoutPolls) return false;
            DeliveryFailed("等待对方握手、公开或状态哈希超过 " + Number(DeliveryTimeoutPolls) + " 次轮询，对方可能已掉线。");
            return true;
        }

        private void DeliveryFailed(string reason)
        {
            unconfirmed.Clear();
            reordered.Clear();
            // 发送方向仍可能畅通（例如仅 ACK 丢失），尽力告知对端不能保存此局。
            // Faulted 后再次到期只清理旧通知，不生成无限的错误消息。
            Fault(NetState.Error, reason);
        }
    }
}
