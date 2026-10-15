using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace StreamDuel.Core
{
    public static class QuickChat
    {
        public const int Count = 8;
        public static readonly string[] Lines = CreateLines();

        public static string Text(int index)
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            return Loc.Current.T("quickchat." + index.ToString(CultureInfo.InvariantCulture));
        }

        private static string[] CreateLines()
        {
            var lines = new string[Count];
            for (int index = 0; index < lines.Length; index++) lines[index] = Text(index);
            return lines;
        }
    }

    public sealed class QuickChatMessage
    {
        public int Side { get; }
        public int Index { get; }
        public int Turn { get; }

        internal QuickChatMessage(int side, int index, int turn)
        {
            Side = side;
            Index = index;
            Turn = turn;
        }
    }

    public sealed partial class NetSession
    {
        private readonly Queue<double> chatSendTimes = new Queue<double>();
        private readonly List<QuickChatMessage> receivedChats = new List<QuickChatMessage>();
        private ReadOnlyCollection<QuickChatMessage> receivedChatsView;
        private double lastChatTime = -1;
        private int chatReceiveTurn = -1;
        private int chatsThisTurn;

        public bool MuteOpponent { get; set; }
        public IReadOnlyList<QuickChatMessage> ReceivedChats
        {
            get { return receivedChatsView ?? (receivedChatsView = receivedChats.AsReadOnly()); }
        }

        public bool SendQuickChat(int index, double now)
        {
            if (Battle == null || Faulted || index < 0 || index >= QuickChat.Count
                || double.IsNaN(now) || double.IsInfinity(now) || now < 0 || now < lastChatTime) return false;

            lastChatTime = now;
            while (chatSendTimes.Count > 0 && now - chatSendTimes.Peek() >= 10) chatSendTimes.Dequeue();
            if (chatSendTimes.Count >= 3) return false;
            if (!Send("{\"type\":\"chat\",\"index\":" + Number(index) + "}")) return false;
            chatSendTimes.Enqueue(now);
            return true;
        }

        private void ReceiveQuickChat(Dictionary<string, object> obj)
        {
            if (Battle == null || obj.Count != 2) return;
            int index;
            try { index = (int)NetJson.Integer(obj, "index", 0, QuickChat.Count - 1); }
            catch (FormatException) { return; }

            if (chatReceiveTurn != Battle.Turn)
            {
                chatReceiveTurn = Battle.Turn;
                chatsThisTurn = 0;
            }
            if (chatsThisTurn >= 6) return;
            chatsThisTurn++;
            // 静音也计入接收配额，切换静音不能让同一回合的刷屏重新获得额度。
            if (!MuteOpponent) receivedChats.Add(new QuickChatMessage(1 - MySide, index, Battle.Turn));
        }

        private static bool HasQuickChatHeader(string message)
        {
            if (message == null) return false;
            int offset = 0;
            SkipChatWhitespace(message, ref offset);
            if (offset >= message.Length || message[offset++] != '{') return false;
            if (!ReadChatHeaderText(message, ref offset, "type")) return false;
            SkipChatWhitespace(message, ref offset);
            if (offset >= message.Length || message[offset++] != ':') return false;
            if (!ReadChatHeaderText(message, ref offset, "chat")) return false;
            SkipChatWhitespace(message, ref offset);
            // 只有明确的首字段路由头可豁免坏 JSON，不能让其它协议载荷中的 chat 字样掩盖错误。
            return offset == message.Length || message[offset] == ',' || message[offset] == '}';
        }

        private static bool ReadChatHeaderText(string message, ref int offset, string expected)
        {
            SkipChatWhitespace(message, ref offset);
            int start = offset;
            if (offset >= message.Length || message[offset++] != '"') return false;
            while (offset < message.Length)
            {
                char current = message[offset++];
                if (current == '\\')
                {
                    if (offset == message.Length) return false;
                    offset++;
                }
                else if (current == '"')
                {
                    try { return MiniJson.Parse(message.Substring(start, offset - start)) as string == expected; }
                    catch (FormatException) { return false; }
                }
            }
            return false;
        }

        private static void SkipChatWhitespace(string message, ref int offset)
        {
            while (offset < message.Length && (message[offset] == ' ' || message[offset] == '\t'
                || message[offset] == '\r' || message[offset] == '\n')) offset++;
        }
    }
}
