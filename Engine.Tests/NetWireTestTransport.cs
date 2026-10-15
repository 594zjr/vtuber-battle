using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StreamDuel.Core;
using Xunit;

// 协议 4 的线路重发不等于再次提交业务；旧业务断言按序号计数，并检查重发字节不变。
internal static class NetWireTestTransport
{
    public static bool IsAck(string wire)
    {
        using var json = JsonDocument.Parse(wire);
        return json.RootElement.GetProperty("type").GetString() == "ack";
    }

    public static long Sequence(string wire)
    {
        using var json = JsonDocument.Parse(wire);
        return json.RootElement.TryGetProperty("seq", out var seq) ? seq.GetInt64() : 0;
    }

    public static string WithSequence(string wire, long sequence)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(wire);
        fields["seq"] = sequence;
        return JsonSerializer.Serialize(fields);
    }

    public static string[] Business(IEnumerable<string> wires)
    {
        return wires.Where(wire => !IsAck(wire)).GroupBy(Sequence).Select(group =>
        {
            Assert.True(group.Key > 0);
            Assert.All(group, wire => Assert.Equal(group.First(), wire));
            return group.First();
        }).ToArray();
    }

    public static string StampNew(string wire, ref long highest)
    {
        try
        {
            if (!(MiniJson.Parse(wire) is Dictionary<string, object>)) return wire;
        }
        catch (FormatException) { return wire; }
        return WithSequence(wire, ++highest);
    }

    // 借真实会话分配可靠序号与 ACK 状态，再替换业务体；不会让注入包与后续正常操作撞号。
    public sealed class PayloadSender
    {
        readonly NetSession sender;
        readonly Action<Func<string, string>> rewriteNext;
        readonly Action<string> rawSend;
        double now = 1000000;

        public PayloadSender(NetSession sender, Action<Func<string, string>> rewriteNext, Action<string> rawSend)
        {
            this.sender = sender;
            this.rewriteNext = rewriteNext;
            this.rawSend = rawSend;
        }

        public string Send(string payload)
        {
            Dictionary<string, object> parsed;
            try { parsed = MiniJson.Parse(payload) as Dictionary<string, object>; }
            catch (FormatException) { rawSend(payload); return payload; }
            if (parsed == null || parsed.ContainsKey("seq")) { rawSend(payload); return payload; }
            string sent = null;
            rewriteNext(wire =>
            {
                // 保留恶意内容和合法空白前缀，确保 FIFO 测试仍逐字验证收到的线路数据。
                int end = payload.LastIndexOf('}');
                sent = payload.Substring(0, end) + ",\"seq\":" + Sequence(wire) + payload.Substring(end);
                return sent;
            });
            now += 10;
            Assert.True(sender.SendQuickChat(0, now));
            Assert.NotNull(sent);
            return sent;
        }
    }
}
