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
            bool foundChat = false;
            while (offset < message.Length)
            {
                SkipChatWhitespace(message, ref offset);
                if (offset == message.Length || message[offset] == '}') return foundChat;
                if (!ReadChatHeaderText(message, ref offset, out var key)) return foundChat;
                SkipChatWhitespace(message, ref offset);
                if (offset == message.Length || message[offset++] != ':') return foundChat;
                if (key == "type")
                {
                    if (!ReadChatHeaderText(message, ref offset, out var type) || type != "chat") return false;
                    SkipChatWhitespace(message, ref offset);
                    if (offset < message.Length && message[offset] != ',' && message[offset] != '}') return false;
                    foundChat = true;
                }
                else if (!SkipChatValue(message, ref offset)) return foundChat;
                SkipChatWhitespace(message, ref offset);
                if (offset == message.Length || message[offset] == '}') return foundChat;
                if (message[offset++] != ',') return false;
            }
            return foundChat;
        }

        private static bool ReadChatHeaderText(string message, ref int offset, out string value)
        {
            value = null;
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
                    try { value = MiniJson.Parse(message.Substring(start, offset - start)) as string; }
                    catch (FormatException) { }
                    // 已闭合但无效的键不能遮住后续根字段；无效 type 的 null 值仍会被拒绝。
                    return true;
                }
            }
            return false;
        }

        private static bool SkipChatValue(string message, ref int offset)
        {
            SkipChatWhitespace(message, ref offset);
            if (offset == message.Length) return false;
            // 只找根对象的字段边界；不转换载荷数值，溢出的 index 仍能按聊天丢弃。
            var closers = new Stack<char>();
            while (offset < message.Length)
            {
                char current = message[offset];
                if (closers.Count == 0 && (current == ',' || current == '}')) return true;
                if (current == '"')
                {
                    if (!SkipChatString(message, ref offset)) return false;
                    continue;
                }
                if (current == '{' || current == '[') closers.Push(current == '{' ? '}' : ']');
                else if (current == '}' || current == ']')
                {
                    if (closers.Count == 0 || closers.Pop() != current) return false;
                }
                offset++;
            }
            return closers.Count == 0;
        }

        private static bool SkipChatString(string message, ref int offset)
        {
            offset++;
            while (offset < message.Length)
            {
                char current = message[offset++];
                // 非路由文本即使有非法转义，也不能遮住后续明确的非聊天类型。
                if (current == '\\')
                {
                    if (offset == message.Length) return false;
                    offset++;
                }
                else if (current == '"') return true;
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
