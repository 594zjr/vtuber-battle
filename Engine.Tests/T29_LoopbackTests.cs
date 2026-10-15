using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamDuel.Core;
using Xunit;

public class T29_LoopbackTests
{
    [Fact]
    public void DirectionsAndPairsStayIndependentAndConsumeMessagesInOrder()
    {
        LoopbackPair.Create(out var a, out var b);
        LoopbackPair.Create(out var otherA, out var otherB);
        Assert.False(a.TryReceive(out var empty));
        Assert.Null(empty);
        Assert.False(b.TryReceive(out _));

        var messages = new[] { "甲方一", "", "{\"type\":\"hello\"}", "原样保留\n换行" };
        foreach (var message in messages) a.Send(message);
        Assert.False(a.TryReceive(out _));
        b.Send("乙方一");
        b.Send("乙方二");
        otherA.Send("另一对连接");

        Assert.Equal(new[] { "乙方一", "乙方二" }, Drain(a));
        Assert.Equal(messages, Drain(b));
        Assert.Equal(new[] { "另一对连接" }, Drain(otherB));
        Assert.Empty(Drain(otherA));
        Assert.Empty(Drain(a));
        Assert.Empty(Drain(b));
        Assert.Empty(Drain(otherB));
    }

    [Fact]
    public void ConcurrentWritersPreserveEachWritersFifoWithoutLosingOrDuplicatingMessages()
    {
        LoopbackPair.Create(out var a, out var b);
        const int writersPerSide = 4;
        const int messagesPerWriter = 256;
        Parallel.For(0, writersPerSide * 2, writer =>
        {
            var sender = writer < writersPerSide ? a : b;
            for (int index = 0; index < messagesPerWriter; index++) sender.Send(writer + ":" + index);
        });

        var receivedByA = Drain(a);
        var receivedByB = Drain(b);
        Assert.Equal(writersPerSide * messagesPerWriter, receivedByA.Count);
        Assert.Equal(writersPerSide * messagesPerWriter, receivedByB.Count);
        for (int writer = 0; writer < writersPerSide * 2; writer++)
        {
            var received = writer < writersPerSide ? receivedByB : receivedByA;
            string prefix = writer + ":";
            // 并发发送者之间没有预定顺序，但每位发送者内部的先后必须保留。
            Assert.Equal(Enumerable.Range(0, messagesPerWriter).Select(index => prefix + index),
                received.Where(message => message.StartsWith(prefix, StringComparison.Ordinal)));
        }
        Assert.Empty(Drain(a));
        Assert.Empty(Drain(b));
    }

    [Fact]
    public async Task ConcurrentSendersAndReceiversDeliverEveryMessageExactlyOnce()
    {
        LoopbackPair.Create(out var a, out var b);
        const int count = 1024;
        var receivedByA = new ConcurrentBag<string>();
        var receivedByB = new ConcurrentBag<string>();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool finished = false;
        var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(async () =>
        {
            await start.Task;
            var receiver = reader % 2 == 0 ? a : b;
            var received = reader % 2 == 0 ? receivedByA : receivedByB;
            while (!Volatile.Read(ref finished))
            {
                if (receiver.TryReceive(out var message)) received.Add(message);
                else await Task.Yield();
            }
            while (receiver.TryReceive(out var remaining)) received.Add(remaining);
        })).ToArray();
        var writer = Task.Run(async () =>
        {
            await start.Task;
            try
            {
                for (int index = 0; index < count; index++)
                {
                    a.Send("甲:" + index);
                    b.Send("乙:" + index);
                    if (index % 32 == 0) await Task.Yield();
                }
            }
            finally
            {
                Volatile.Write(ref finished, true);
            }
        });

        start.SetResult(true);
        await Task.WhenAll(readers.Append(writer)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Enumerable.Range(0, count).Select(index => "乙:" + index).OrderBy(message => message, StringComparer.Ordinal),
            receivedByA.OrderBy(message => message, StringComparer.Ordinal));
        Assert.Equal(Enumerable.Range(0, count).Select(index => "甲:" + index).OrderBy(message => message, StringComparer.Ordinal),
            receivedByB.OrderBy(message => message, StringComparer.Ordinal));
        Assert.Empty(Drain(a));
        Assert.Empty(Drain(b));
    }

    static List<string> Drain(INetTransport transport)
    {
        var messages = new List<string>();
        while (transport.TryReceive(out var message)) messages.Add(message);
        return messages;
    }
}
