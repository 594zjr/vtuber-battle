using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class SpectatorChatMessage
    {
        public string SenderName { get; }
        public int Index { get; }
        public int Turn { get; }

        internal SpectatorChatMessage(string senderName, int index, int turn)
        {
            SenderName = senderName;
            Index = index;
            Turn = turn;
        }
    }

    public sealed partial class SpectatorFeed
    {
        readonly bool receiveChatRequests;
        public bool ChatEnabled { get; }
        public const int ChatProtocolVersion = 1;

        void PollQuickChats()
        {
            if (!receiveChatRequests) return;
            foreach (var viewer in viewers.ToArray())
            {
                if (!viewers.Contains(viewer) || viewer.ChatInputClosed) continue;
                try
                {
                    for (int count = 0; count < SpectatorChatProtocol.PollLimit; count++)
                    {
                        if (!viewer.Transport.TryReceive(out var message)) break;
                        // 关闭聊天和终局后的输入也有限量排空，不能阻塞后续观战推进。
                        if (!ChatEnabled || ending != null || !viewer.Started) continue;
                        try { ReceiveQuickChat(viewer, SpectatorProtocol.Object(message)); }
                        catch (FormatException) { }
                    }
                }
                catch (TcpDisconnectedException)
                {
                    // 观众可以只关闭上传方向，仍继续接收观战和其他观众的快捷语。
                    viewer.ChatInputClosed = true;
                }
                catch (Exception)
                {
                    // 上传故障也只移除此观众，不影响选手或其它观众的数据流。
                    viewers.Remove(viewer);
                }
            }
        }

        void ReceiveQuickChat(Viewer viewer, Dictionary<string, object> obj)
        {
            if (NetJson.Text(obj, "type") != SpectatorChatProtocol.Type
                || NetJson.Integer(obj, "version", 1, int.MaxValue) != ChatProtocolVersion) return;
            string kind = NetJson.Text(obj, "kind");
            if (kind == "join")
            {
                SpectatorProtocol.Keys(obj, "type", "version", "kind", "name");
                string name = NetJson.Text(obj, "name");
                if (viewer.ChatJoined) return;
                viewer.DisplayName = TextInput.Clean(name, TextInputKind.PlayerName).Text;
                viewer.ChatJoined = true;
                return;
            }
            if (kind != "send" || !viewer.ChatJoined) return;
            SpectatorProtocol.Keys(obj, "type", "version", "kind", "index");
            int index = (int)NetJson.Integer(obj, "index", 0, QuickChat.Count - 1);
            // 以已公开给此观众的回合限流，聊天不能透露延迟之外的真实进度。
            if (viewer.ChatTurn != viewer.ReleasedTurn)
            {
                viewer.ChatTurn = viewer.ReleasedTurn;
                viewer.ChatCount = 0;
            }
            if (viewer.ChatCount >= 3) return;
            viewer.ChatCount++;
            string message = SpectatorChatProtocol.Prefix("message")
                + ",\"name\":" + NetJson.Quote(viewer.DisplayName)
                + ",\"index\":" + SpectatorProtocol.Number(index)
                + ",\"turn\":" + SpectatorProtocol.Number(viewer.ReleasedTurn) + "}";
            foreach (var recipient in viewers.ToArray())
            {
                // 未声明能力的旧客户端会拒绝未知类型，绝不能向它发送扩展帧。
                if (ReferenceEquals(recipient, viewer) || !recipient.Started || !recipient.ChatJoined || recipient.Finished) continue;
                try { recipient.Transport.Send(message); }
                catch (Exception) { viewers.Remove(recipient); }
            }
        }
    }

    public sealed partial class SpectatorClient
    {
        readonly List<SpectatorChatMessage> receivedChats = new List<SpectatorChatMessage>();
        ReadOnlyCollection<SpectatorChatMessage> receivedChatsView;
        bool chatJoined;
        int chatSendTurn = -1, chatsSentThisTurn;

        public bool ChatEnabled { get; }
        public string DisplayName { get; }
        public IReadOnlyList<SpectatorChatMessage> ReceivedChats
        {
            get { return receivedChatsView ?? (receivedChatsView = receivedChats.AsReadOnly()); }
        }

        public bool SendQuickChat(int index)
        {
            if (!ChatEnabled || !chatJoined || Battle == null || Error != null || IsFinished || gameEnded
                || index < 0 || index >= QuickChat.Count) return false;
            if (chatSendTurn != Battle.Turn)
            {
                chatSendTurn = Battle.Turn;
                chatsSentThisTurn = 0;
            }
            if (chatsSentThisTurn >= 3) return false;
            try
            {
                transport.Send(SpectatorChatProtocol.Prefix("send")
                    + ",\"index\":" + SpectatorProtocol.Number(index) + "}");
                chatsSentThisTurn++;
                return true;
            }
            catch (Exception) { return false; }
        }

        void JoinQuickChat()
        {
            if (!ChatEnabled || chatJoined) return;
            try
            {
                transport.Send(SpectatorChatProtocol.Prefix("join") + ",\"name\":" + NetJson.Quote(DisplayName) + "}");
                chatJoined = true;
            }
            catch (Exception) { }
        }

        void ReceiveQuickChat(Dictionary<string, object> obj)
        {
            if (!ChatEnabled || Battle == null || IsFinished || gameEnded) return;
            try
            {
                SpectatorProtocol.Keys(obj, "type", "version", "kind", "name", "index", "turn");
                if (NetJson.Integer(obj, "version", 1, int.MaxValue) != SpectatorFeed.ChatProtocolVersion
                    || NetJson.Text(obj, "kind") != "message") return;
                string name = TextInput.Clean(NetJson.Text(obj, "name"), TextInputKind.PlayerName).Text;
                int index = (int)NetJson.Integer(obj, "index", 0, QuickChat.Count - 1);
                int turn = (int)NetJson.Integer(obj, "turn", 1, int.MaxValue);
                receivedChats.Add(new SpectatorChatMessage(name, index, turn));
            }
            catch (FormatException) { }
        }
    }

    internal static class SpectatorChatProtocol
    {
        internal const string Type = "spectator_chat";
        internal const int PollLimit = 64;

        internal static string Prefix(string kind)
        {
            return "{\"type\":\"spectator_chat\",\"version\":" + SpectatorProtocol.Number(SpectatorFeed.ChatProtocolVersion)
                + ",\"kind\":" + NetJson.Quote(kind);
        }

        internal static bool HasHeader(string message)
        {
            if (message == null) return false;
            int offset = 0;
            SkipWhitespace(message, ref offset);
            if (offset >= message.Length || message[offset++] != '{') return false;
            bool foundChat = false;
            while (offset < message.Length)
            {
                SkipWhitespace(message, ref offset);
                if (offset == message.Length || message[offset] == '}') return foundChat;
                if (!ReadText(message, ref offset, out var key)) return foundChat;
                SkipWhitespace(message, ref offset);
                if (offset == message.Length || message[offset++] != ':') return foundChat;
                if (key == "type")
                {
                    if (!ReadText(message, ref offset, out var type) || type != Type) return false;
                    SkipWhitespace(message, ref offset);
                    if (offset < message.Length && message[offset] != ',' && message[offset] != '}') return false;
                    foundChat = true;
                }
                else if (!SkipValue(message, ref offset)) return foundChat;
                SkipWhitespace(message, ref offset);
                if (offset == message.Length || message[offset] == '}') return foundChat;
                if (message[offset++] != ',') return false;
            }
            return foundChat;
        }

        static bool ReadText(string message, ref int offset, out string value)
        {
            value = null;
            SkipWhitespace(message, ref offset);
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
                    return true;
                }
            }
            return false;
        }

        static bool SkipValue(string message, ref int offset)
        {
            SkipWhitespace(message, ref offset);
            if (offset == message.Length) return false;
            // 只辨认根字段，不转换载荷数字；溢出编号仍属于可丢弃的扩展帧。
            var closers = new Stack<char>();
            while (offset < message.Length)
            {
                char current = message[offset];
                if (closers.Count == 0 && (current == ',' || current == '}')) return true;
                if (current == '"')
                {
                    if (!SkipString(message, ref offset)) return false;
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

        static bool SkipString(string message, ref int offset)
        {
            offset++;
            while (offset < message.Length)
            {
                char current = message[offset++];
                if (current == '\\')
                {
                    if (offset == message.Length) return false;
                    offset++;
                }
                else if (current == '"') return true;
            }
            return false;
        }

        static void SkipWhitespace(string message, ref int offset)
        {
            while (offset < message.Length && (message[offset] == ' ' || message[offset] == '\t'
                || message[offset] == '\r' || message[offset] == '\n')) offset++;
        }
    }
}
