using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StreamDuel.Core;
using Xunit;

public class T96_LossyTransportTests
{
    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidProbabilitiesAreRejected(double rate)
    {
        LoopbackPair.Create(out var a, out _);
        Assert.Throws<ArgumentOutOfRangeException>(() => new LossyTransport(a, 0, dropRate: rate));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LossyTransport(a, 0, duplicateRate: rate));
    }

    [Fact]
    public void InvalidTransportDelayAndCapacityAreRejected()
    {
        LoopbackPair.Create(out var a, out _);
        Assert.Throws<ArgumentNullException>(() => new LossyTransport(null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LossyTransport(a, 0, minDelayPolls: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LossyTransport(a, 0, maxDelayPolls: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LossyTransport(a, 0, minDelayPolls: 2, maxDelayPolls: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LossyTransport(a, 0, maxBufferedMessages: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LossyTransport(a, 0, maxBufferedMessages: -1));
        var transport = new LossyTransport(a, 0);
        Assert.Throws<ArgumentNullException>(() => transport.Send(null));
        Assert.Equal(0, transport.SentCount);
        Assert.Equal(0, transport.BufferedCount);
    }

    [Fact]
    public void OnlyExplicitPollAdvancesDelayAndReceiveDoesNotApplyFaultsAgain()
    {
        LoopbackPair.Create(out var a, out var b);
        var transport = new LossyTransport(a, 42, minDelayPolls: 3, maxDelayPolls: 3);
        transport.Send("延迟消息");
        b.Send("底层入站消息");
        Assert.True(transport.TryReceive(out string incoming));
        Assert.Equal("底层入站消息", incoming);
        for (int i = 0; i < 100; i++) Assert.False(transport.TryReceive(out _));
        Assert.Equal(0, transport.PollCount);
        Assert.False(b.TryReceive(out _));

        transport.Poll();
        transport.Poll();
        Assert.False(b.TryReceive(out _));
        Assert.Equal(1, transport.BufferedCount);
        transport.Poll();
        Assert.Equal(new[] { "延迟消息" }, Drain(b));
        Assert.Equal(3, transport.PollCount);
        Assert.Equal(1, transport.DeliveredCount);
        Assert.Equal(0, transport.BufferedCount);
    }

    [Fact]
    public void ZeroDelayStillNeedsOnePollAndPreservesArbitraryStrings()
    {
        LoopbackPair.Create(out var a, out var b);
        var transport = new LossyTransport(a, 0, reorder: false);
        string[] messages = { "", "消息一", "包含\n换行", "{\"type\":\"chat\",\"index\":0}" };
        foreach (string message in messages) transport.Send(message);
        Assert.False(b.TryReceive(out _));
        transport.Poll();
        Assert.Equal(messages, Drain(b));
        Assert.Equal(messages.Length, transport.SentCount);
        Assert.Equal(messages.Length, transport.DeliveredCount);
        Assert.Equal(0, transport.DroppedCount);
        Assert.Equal(0, transport.DuplicatedCount);
    }

    [Fact]
    public void TotalLossDoesNotAllocateCopiesOrFillTheBuffer()
    {
        LoopbackPair.Create(out var a, out var b);
        var transport = new LossyTransport(a, uint.MaxValue, dropRate: 1, duplicateRate: 1, maxBufferedMessages: 1);
        for (int i = 0; i < 100; i++) transport.Send("丢弃");
        transport.Poll();
        Assert.Equal(100, transport.SentCount);
        Assert.Equal(100, transport.DroppedCount);
        Assert.Equal(0, transport.DuplicatedCount);
        Assert.Equal(0, transport.DeliveredCount);
        Assert.Equal(0, transport.BufferedCount);
        Assert.False(b.TryReceive(out _));
    }

    [Fact]
    public void TotalDuplicationCreatesExactlyOneAdditionalCopyPerSend()
    {
        LoopbackPair.Create(out var a, out var b);
        var transport = new LossyTransport(a, 1, duplicateRate: 1, reorder: false);
        transport.Send("甲");
        transport.Send("乙");
        Assert.Equal(4, transport.BufferedCount);
        transport.Poll();
        Assert.Equal(new[] { "甲", "甲", "乙", "乙" }, Drain(b));
        Assert.Equal(2, transport.SentCount);
        Assert.Equal(2, transport.DuplicatedCount);
        Assert.Equal(4, transport.DeliveredCount);
    }

    [Fact]
    public void ReorderingShufflesAnEqualDelayBatchAndCanBeDisabled()
    {
        var expected = Enumerable.Range(0, 30).Select(i => i.ToString()).ToArray();
        foreach (bool reorder in new[] { false, true })
        {
            LoopbackPair.Create(out var a, out var b);
            var transport = new LossyTransport(a, 96, minDelayPolls: 2, maxDelayPolls: 2, reorder: reorder);
            foreach (string message in expected) transport.Send(message);
            transport.Poll();
            Assert.False(b.TryReceive(out _));
            transport.Poll();
            var received = Drain(b);
            Assert.Equal(expected.OrderBy(text => text), received.OrderBy(text => text));
            if (reorder) Assert.False(expected.SequenceEqual(received));
            else Assert.Equal(expected, received);
        }
    }

    [Fact]
    public void DisabledReorderingKeepsFifoDespiteDifferentSampledDelays()
    {
        LoopbackPair.Create(out var a, out var b);
        var transport = new LossyTransport(a, 96, minDelayPolls: 1, maxDelayPolls: 8, reorder: false);
        var expected = Enumerable.Range(0, 60).Select(i => i.ToString()).ToArray();
        var received = new List<string>();
        foreach (string message in expected) transport.Send(message);
        for (int i = 0; i < 8; i++)
        {
            transport.Poll();
            received.AddRange(Drain(b));
        }
        Assert.Equal(expected, received);
        Assert.Equal(0, transport.BufferedCount);
    }

    [Fact]
    public void SameSeedAndOperationsReproduceFaultsAndTheDeliveryTimeline()
    {
        string[] first = Trace(96);
        Assert.Equal(first, Trace(96));
        Assert.False(first.SequenceEqual(Trace(97)));
    }

    [Fact]
    public void BufferLimitRejectsTheWholeSendWithoutLosingAcceptedMessages()
    {
        LoopbackPair.Create(out var a, out var b);
        var tooSmall = new LossyTransport(a, 0, duplicateRate: 1, maxBufferedMessages: 1);
        Assert.Throws<InvalidOperationException>(() => tooSmall.Send("不能只缓存半份"));
        Assert.Equal(0, tooSmall.BufferedCount);
        Assert.Equal(0, tooSmall.SentCount);
        Assert.Equal(0, tooSmall.DuplicatedCount);

        var transport = new LossyTransport(a, 0, duplicateRate: 1, reorder: false, maxBufferedMessages: 2);
        transport.Send("甲");
        Assert.Throws<InvalidOperationException>(() => transport.Send("乙"));
        Assert.Equal(2, transport.BufferedCount);
        Assert.Equal(1, transport.SentCount);
        Assert.Equal(0, transport.DroppedCount);
        transport.Poll();
        transport.Send("乙");
        transport.Poll();
        Assert.Equal(new[] { "甲", "甲", "乙", "乙" }, Drain(b));
        Assert.Equal(2, transport.SentCount);
        Assert.Equal(4, transport.DeliveredCount);
    }

    [Fact]
    public void UnderlyingSendFailureKeepsUnsentMessagesAndIsNotSimulatedLoss()
    {
        var inner = new FailingTransport { FailSendNumber = 2 };
        var transport = new LossyTransport(inner, 0, reorder: false);
        transport.Send("甲");
        transport.Send("乙");
        transport.Send("丙");
        Assert.Throws<IOException>(() => transport.Poll());
        Assert.Equal(new[] { "甲" }, inner.Sent);
        Assert.Equal(2, transport.BufferedCount);
        Assert.Equal(1, transport.DeliveredCount);
        Assert.Equal(0, transport.DroppedCount);
        inner.FailSendNumber = 0;
        transport.Poll();
        Assert.Equal(new[] { "甲", "乙", "丙" }, inner.Sent);
        Assert.Equal(0, transport.BufferedCount);
        Assert.Equal(3, transport.DeliveredCount);
        Assert.Throws<IOException>(() => transport.TryReceive(out _));
        Assert.Equal(2, transport.PollCount);
    }

    [Fact]
    public void NestedPollableTransportAdvancesOnlyOncePerOuterPoll()
    {
        LoopbackPair.Create(out var a, out var b);
        var inner = new LossyTransport(a, 0, minDelayPolls: 2, maxDelayPolls: 2);
        var outer = new LossyTransport(inner, 1, minDelayPolls: 2, maxDelayPolls: 2);
        outer.Send("双层延迟");
        outer.Poll();
        outer.Poll();
        Assert.Equal(2, inner.PollCount);
        Assert.False(b.TryReceive(out _));
        outer.Poll();
        Assert.Equal(3, inner.PollCount);
        Assert.Equal(new[] { "双层延迟" }, Drain(b));
    }

    [Fact]
    public void LargestDelayDoesNotOverflowAtScheduling()
    {
        LoopbackPair.Create(out var a, out var b);
        var fixedDelay = new LossyTransport(a, 0, minDelayPolls: int.MaxValue, maxDelayPolls: int.MaxValue);
        fixedDelay.Send("远期");
        fixedDelay.Poll();
        Assert.Equal(1, fixedDelay.BufferedCount);
        Assert.False(b.TryReceive(out _));
        var wideDelay = new LossyTransport(a, 0, maxDelayPolls: int.MaxValue);
        wideDelay.Send("宽范围");
        Assert.Equal(1, wideDelay.BufferedCount);
    }

    static string[] Trace(uint seed)
    {
        LoopbackPair.Create(out var a, out var b);
        var transport = new LossyTransport(a, seed, dropRate: 0.2, minDelayPolls: 1,
            maxDelayPolls: 5, duplicateRate: 0.4);
        var trace = new List<string>();
        for (int i = 0; i < 90; i++)
        {
            transport.Send("消息" + i);
            if (i % 3 != 0) continue;
            transport.Poll();
            trace.AddRange(Drain(b).Select(text => transport.PollCount + ":" + text));
        }
        for (int i = 0; i < 5; i++)
        {
            transport.Poll();
            trace.AddRange(Drain(b).Select(text => transport.PollCount + ":" + text));
        }
        Assert.True(transport.DroppedCount > 0);
        Assert.True(transport.DuplicatedCount > 0);
        Assert.Equal(90 - transport.DroppedCount + transport.DuplicatedCount, transport.DeliveredCount);
        Assert.Equal(0, transport.BufferedCount);
        trace.Add("计数:" + transport.DroppedCount + ":" + transport.DuplicatedCount + ":" + transport.DeliveredCount);
        return trace.ToArray();
    }

    static List<string> Drain(INetTransport transport)
    {
        var result = new List<string>();
        while (transport.TryReceive(out string message)) result.Add(message);
        return result;
    }

    sealed class FailingTransport : INetTransport
    {
        public int FailSendNumber;
        public readonly List<string> Sent = new List<string>();

        public void Send(string message)
        {
            if (Sent.Count + 1 == FailSendNumber) throw new IOException("测试底层发送失败。");
            Sent.Add(message);
        }

        public bool TryReceive(out string message)
        {
            throw new IOException("测试底层接收失败。");
        }
    }
}
