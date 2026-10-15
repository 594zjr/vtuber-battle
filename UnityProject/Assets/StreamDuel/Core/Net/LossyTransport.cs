using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public interface IPollableNetTransport : INetTransport
    {
        void Poll();
    }

    /// <summary>
    /// 在发送方向模拟故障；由调用方在同一线程显式 Poll，TryReceive 不推进时间。
    /// 即使延迟为零也先缓存到下一次 Poll；底层传输的生命周期仍由调用方管理。
    /// </summary>
    public sealed class LossyTransport : IPollableNetTransport
    {
        readonly INetTransport inner;
        readonly Rng rng;
        readonly double dropRate;
        readonly double duplicateRate;
        readonly int minDelayPolls;
        readonly int maxDelayPolls;
        readonly bool reorder;
        readonly int maxBufferedMessages;
        readonly LinkedList<PendingMessage> pending = new LinkedList<PendingMessage>();

        sealed class PendingMessage
        {
            public string Text;
            public long Due;
        }

        public long PollCount { get; private set; }
        public long SentCount { get; private set; }
        public long DroppedCount { get; private set; }
        public long DuplicatedCount { get; private set; }
        public long DeliveredCount { get; private set; }
        public int BufferedCount { get { return pending.Count; } }

        public LossyTransport(INetTransport inner, uint seed, double dropRate = 0,
            int minDelayPolls = 0, int maxDelayPolls = 0, double duplicateRate = 0,
            bool reorder = true, int maxBufferedMessages = 4096)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            ValidateRate(dropRate, nameof(dropRate));
            ValidateRate(duplicateRate, nameof(duplicateRate));
            if (minDelayPolls < 0)
                throw new ArgumentOutOfRangeException(nameof(minDelayPolls), "最小延迟不能为负数。");
            if (maxDelayPolls < minDelayPolls)
                throw new ArgumentOutOfRangeException(nameof(maxDelayPolls), "最大延迟不能小于最小延迟。");
            if (maxBufferedMessages < 1)
                throw new ArgumentOutOfRangeException(nameof(maxBufferedMessages), "缓存消息上限必须大于零。");
            rng = new Rng(seed);
            this.dropRate = dropRate;
            this.duplicateRate = duplicateRate;
            this.minDelayPolls = minDelayPolls;
            this.maxDelayPolls = maxDelayPolls;
            this.reorder = reorder;
            this.maxBufferedMessages = maxBufferedMessages;
        }

        public void Send(string msg)
        {
            if (msg == null) throw new ArgumentNullException(nameof(msg));
            uint before = rng.State;
            if (Chance(dropRate))
            {
                SentCount++;
                DroppedCount++;
                return;
            }
            bool duplicate = Chance(duplicateRate);
            int copies = duplicate ? 2 : 1;
            if (pending.Count > maxBufferedMessages - copies)
            {
                // 容量错误不是模拟丢包，失败的调用也不能改变之后的随机轨迹。
                rng.State = before;
                throw new InvalidOperationException("模拟传输的缓存已满，请先轮询发送剩余消息。");
            }
            long firstDue = DuePoll();
            long secondDue = duplicate ? DuePoll() : 0;
            pending.AddLast(new PendingMessage { Text = msg, Due = firstDue });
            if (duplicate) pending.AddLast(new PendingMessage { Text = msg, Due = secondDue });
            SentCount++;
            if (duplicate) DuplicatedCount++;
        }

        public bool TryReceive(out string msg)
        {
            return inner.TryReceive(out msg);
        }

        public void Poll()
        {
            PollCount = checked(PollCount + 1);
            var ready = new List<LinkedListNode<PendingMessage>>();
            for (var node = pending.First; node != null; node = node.Next)
            {
                if (node.Value.Due <= PollCount) ready.Add(node);
                else if (!reorder) break;
            }
            if (reorder)
            {
                for (int i = ready.Count - 1; i > 0; i--)
                {
                    int j = (int)(rng.Next() * (i + 1));
                    var other = ready[j];
                    ready[j] = ready[i];
                    ready[i] = other;
                }
            }
            foreach (var node in ready)
            {
                // 只有底层接受发送后才移出缓存，异常不能静默吞掉这一批的后续消息。
                inner.Send(node.Value.Text);
                pending.Remove(node);
                DeliveredCount++;
            }
            if (inner is IPollableNetTransport pollable) pollable.Poll();
        }

        long DuePoll()
        {
            long width = (long)maxDelayPolls - minDelayPolls + 1;
            int delay = minDelayPolls + (width == 1 ? 0 : (int)(rng.Next() * width));
            return checked(PollCount + delay);
        }

        bool Chance(double rate)
        {
            return rate > 0 && (rate == 1 || rng.Next() < rate);
        }

        static void ValidateRate(double rate, string name)
        {
            if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0 || rate > 1)
                throw new ArgumentOutOfRangeException(name, "概率必须是 0～1 之间的有限数值。");
        }
    }
}
