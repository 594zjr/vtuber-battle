using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class LanRoom
    {
        public string Name, Address, ContentHash, Ruleset;
        public int Port, Protocol;
        public double LastSeen;
    }

    public static class LanBeacon
    {
        public const int Port = 47777;
        private const int MaxBytes = 512;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static string Encode(LanRoom room)
        {
            if (room == null || room.Name == null || room.ContentHash == null || room.Ruleset == null
                || room.Port < 1 || room.Port > 65535 || room.Protocol < 1
                || room.ContentHash.Length > MaxBytes || room.Ruleset.Length > MaxBytes) return null;
            try
            {
                string name = ShortName(room.Name);
                Utf8.GetByteCount(name);
                Utf8.GetByteCount(room.ContentHash);
                Utf8.GetByteCount(room.Ruleset);
                string text = "{\"magic\":\"SD-LAN-1\",\"name\":" + NetJson.Quote(name)
                    + ",\"port\":" + room.Port.ToString(CultureInfo.InvariantCulture)
                    + ",\"protocol\":" + room.Protocol.ToString(CultureInfo.InvariantCulture)
                    + ",\"contentHash\":" + NetJson.Quote(room.ContentHash)
                    + ",\"ruleset\":" + NetJson.Quote(room.Ruleset) + "}";
                return Utf8.GetByteCount(text) <= MaxBytes ? text : null;
            }
            catch (ArgumentException) { return null; }
            catch (FormatException) { return null; }
        }

        public static LanRoom Decode(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > MaxBytes) return null;
            try
            {
                if (Utf8.GetByteCount(text) > MaxBytes) return null;
                var fields = NetJson.Object(text);
                // 通用解析器会覆盖重复键，公告必须先拒绝这种含糊的数据。
                if (HasDuplicateKeys(text) || NetJson.Text(fields, "magic") != "SD-LAN-1") return null;
                string name = NetJson.Text(fields, "name");
                string contentHash = NetJson.Text(fields, "contentHash");
                string ruleset = NetJson.Text(fields, "ruleset");
                Utf8.GetByteCount(name);
                Utf8.GetByteCount(contentHash);
                Utf8.GetByteCount(ruleset);
                return new LanRoom
                {
                    Name = ShortName(name),
                    Port = (int)NetJson.Integer(fields, "port", 1, 65535),
                    Protocol = (int)NetJson.Integer(fields, "protocol", 1, int.MaxValue),
                    ContentHash = contentHash,
                    Ruleset = ruleset
                };
            }
            catch (ArgumentException) { return null; }
            catch (FormatException) { return null; }
        }

        private static string ShortName(string name)
        {
            if (name.Length <= 24) return name;
            int length = 24;
            // 避免截断后只剩半个 Unicode 代理对，使合法房名变成损坏文本。
            if (char.IsHighSurrogate(name[length - 1]) && char.IsLowSurrogate(name[length])) length--;
            return name.Substring(0, length);
        }

        private static bool HasDuplicateKeys(string text)
        {
            var objects = new Stack<HashSet<string>>();
            for (int index = 0; index < text.Length; index++)
            {
                char current = text[index];
                if (current == '{') objects.Push(new HashSet<string>(StringComparer.Ordinal));
                else if (current == '[') objects.Push(null);
                else if (current == '}' || current == ']') objects.Pop();
                else if (current == '"')
                {
                    int start = index++;
                    while (text[index] != '"')
                    {
                        if (text[index] == '\\') index++;
                        index++;
                    }
                    string token = (string)MiniJson.Parse(text.Substring(start, index - start + 1));
                    Utf8.GetByteCount(token);
                    int next = index + 1;
                    while (next < text.Length && (text[next] == ' ' || text[next] == '\t'
                        || text[next] == '\r' || text[next] == '\n')) next++;
                    if (next < text.Length && text[next] == ':')
                    {
                        if (!objects.Peek().Add(token)) return true;
                    }
                }
            }
            return false;
        }
    }

    public sealed class LanRoomList
    {
        private readonly List<LanRoom> rooms = new List<LanRoom>();
        private double lastNow;

        public void Receive(string text, string fromAddress, double now)
        {
            ValidateNow(now);
            var room = LanBeacon.Decode(text);
            if (room == null || !IPAddress.TryParse(fromAddress, out var source)) return;
            room.Address = source.IsIPv4MappedToIPv6 ? source.MapToIPv4().ToString() : source.ToString();
            room.LastSeen = now;
            RemoveExpired(now);
            int index = rooms.FindIndex(item => item.Address == room.Address && item.Port == room.Port);
            if (index < 0) rooms.Add(room);
            else rooms[index] = room;
        }

        public List<LanRoom> Rooms(double now)
        {
            ValidateNow(now);
            RemoveExpired(now);
            var snapshot = new List<LanRoom>(rooms.Count);
            foreach (var room in rooms)
                snapshot.Add(new LanRoom
                {
                    Name = room.Name,
                    Address = room.Address,
                    ContentHash = room.ContentHash,
                    Ruleset = room.Ruleset,
                    Port = room.Port,
                    Protocol = room.Protocol,
                    LastSeen = room.LastSeen
                });
            return snapshot;
        }

        private void RemoveExpired(double now)
        {
            rooms.RemoveAll(room => now - room.LastSeen > 3);
        }

        private void ValidateNow(double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || now < 0 || now < lastNow)
                throw new ArgumentOutOfRangeException(nameof(now), "发现时间必须是有限、非负且单调不减的秒数。");
            lastNow = now;
        }
    }
}
