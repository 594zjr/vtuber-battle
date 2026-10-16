using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class PackPolicy
    {
        readonly HashSet<string> packs;
        readonly HashSet<string> streamers;
        public IReadOnlyList<string> DisabledPacks { get; }
        public IReadOnlyList<string> DisabledStreamers { get; }
        public IReadOnlyDictionary<string, string> Reasons { get; }
        public static PackPolicy Empty { get; } = new PackPolicy(new string[0], new string[0], new Dictionary<string, string>());

        PackPolicy(IEnumerable<string> disabledPacks, IEnumerable<string> disabledStreamers, Dictionary<string, string> reasons)
        {
            packs = new HashSet<string>(disabledPacks, StringComparer.Ordinal);
            streamers = new HashSet<string>(disabledStreamers, StringComparer.Ordinal);
            DisabledPacks = Array.AsReadOnly(packs.OrderBy(id => id, StringComparer.Ordinal).ToArray());
            DisabledStreamers = Array.AsReadOnly(streamers.OrderBy(id => id, StringComparer.Ordinal).ToArray());
            Reasons = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(reasons, StringComparer.Ordinal));
        }

        public static PackPolicy FromJson(string json)
        {
            var root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null) throw new FormatException("下架策略必须是 JSON 对象。");
            RejectDuplicateKeys(json);
            foreach (string key in root.Keys)
                if (key != "disabledPacks" && key != "disabledStreamers" && key != "reason")
                    throw new FormatException("下架策略包含未知字段：" + key);
            var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetValue("reason", out var value))
            {
                var entries = value as Dictionary<string, object>;
                if (entries == null) throw new FormatException("下架策略 reason 必须是对象。");
                foreach (var entry in entries)
                {
                    ValidateId(entry.Key);
                    if (!(entry.Value is string reason)) throw new FormatException("下架原因必须是字符串：" + entry.Key);
                    reasons.Add(entry.Key, reason);
                }
            }
            return new PackPolicy(ReadIds(root, "disabledPacks"), ReadIds(root, "disabledStreamers"), reasons);
        }

        public static PackPolicy Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("下架策略路径不能为空。", nameof(path));
            // 只有确实不存在才使用兼容默认值；读不到或损坏不能悄悄重新开放内容。
            try { return FromJson(File.ReadAllText(path)); }
            catch (FileNotFoundException) { return Empty; }
            catch (DirectoryNotFoundException) { return Empty; }
        }

        public static PackPolicy LoadDefault()
        {
            return Load(Path.Combine(AppContext.BaseDirectory, "Content", "PackPolicy.json"));
        }

        public bool IsPackDisabled(string id) { return id != null && packs.Contains(id); }
        public bool IsStreamerDisabled(string id) { return id != null && streamers.Contains(id); }
        public string PackReason(string id)
        {
            if (IsPackDisabled(id)) return Explain("该内容包已下架", id);
            // 录像库可能尚未加载内容库，组合的原包门禁必须在政策本身生效。
            var sources = MixedSources(id);
            if (sources != null)
                foreach (string source in sources)
                    if (IsPackDisabled(source)) return Explain("该内容包已下架", source);
            return null;
        }
        public string StreamerReason(string id) { return IsStreamerDisabled(id) ? Explain("该主播已下架", id) : null; }

        public string CheckTeam(string packId, IEnumerable<string> ids)
        {
            string reason = PackReason(packId);
            if (reason != null) return reason;
            if (ids != null)
                foreach (string id in ids)
                {
                    reason = StreamerReason(id);
                    if (reason != null) return reason;
                }
            return null;
        }

        static List<string> MixedSources(string id)
        {
            if (id == null || !id.StartsWith("mix:", StringComparison.Ordinal)) return null;
            var sources = new List<string>();
            string previous = null;
            try
            {
                foreach (string encoded in id.Substring(4).Split(','))
                {
                    string source = Uri.UnescapeDataString(encoded);
                    if (string.IsNullOrWhiteSpace(source) || source.Any(char.IsControl)
                        || source.StartsWith("mix:", StringComparison.Ordinal)
                        || Uri.EscapeDataString(source) != encoded
                        || (previous != null && StringComparer.Ordinal.Compare(previous, source) >= 0)) return null;
                    sources.Add(source);
                    previous = source;
                }
                return sources;
            }
            catch (UriFormatException) { return null; }
        }

        string Explain(string message, string id)
        {
            string result = message + "：" + id;
            if (Reasons.TryGetValue(id, out string reason) && !string.IsNullOrWhiteSpace(reason)) result += "（" + reason + "）";
            return result;
        }

        static List<string> ReadIds(Dictionary<string, object> root, string name)
        {
            var result = new List<string>();
            if (!root.TryGetValue(name, out object value)) return result;
            var values = value as List<object>;
            if (values == null) throw new FormatException("下架策略 " + name + " 必须是数组。");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in values)
            {
                if (!(item is string id)) throw new FormatException("下架策略 " + name + " 的编号必须是字符串。");
                ValidateId(id);
                if (!seen.Add(id)) throw new FormatException("下架策略 " + name + " 的编号重复：" + id);
                result.Add(id);
            }
            return result;
        }

        static void ValidateId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Trim() != id || id.Any(char.IsControl))
                throw new FormatException("下架策略的编号不能为空、带首尾空白或控制字符。");
        }

        static void RejectDuplicateKeys(string json)
        {
            // 旧内容解析保留重复键兼容性，政策单独拒绝，避免后一项清空前一项禁用名单。
            var objects = new Stack<HashSet<string>>();
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] == '{') objects.Push(new HashSet<string>(StringComparer.Ordinal));
                else if (json[i] == '}') objects.Pop();
                else if (json[i] == '"')
                {
                    int start = i++;
                    while (i < json.Length && json[i] != '"')
                    {
                        if (json[i] == '\\') i++;
                        i++;
                    }
                    int next = i + 1;
                    while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                    if (next < json.Length && json[next] == ':')
                    {
                        string key = (string)MiniJson.Parse(json.Substring(start, i - start + 1));
                        if (!objects.Peek().Add(key)) throw new FormatException("下架策略的字段重复：" + key);
                    }
                }
            }
        }
    }

    public sealed class PackDisabledException : InvalidOperationException
    {
        public PackDisabledException(string message) : base(message) { }
    }
}
