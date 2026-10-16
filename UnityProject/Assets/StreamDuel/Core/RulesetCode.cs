using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace StreamDuel.Core
{
    public static class RulesetCode
    {
        const string Prefix = "SDRULE1-";
        const int ChecksumSize = 8;
        internal const int MaxJsonBytes = 64 * 1024;
        const int MaxCompressedBytes = MaxJsonBytes + 1024;
        const int MaxCodeCharacters = 8 + (MaxCompressedBytes + ChecksumSize + 2) / 3 * 4;
        static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static string Encode(Ruleset ruleset)
        {
            if (ruleset == null) throw new ArgumentNullException(nameof(ruleset));
            string json = ruleset.ToJson();
            CheckJsonSize(json);
            byte[] text = Utf8.GetBytes(json);
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
                    deflate.Write(text, 0, text.Length);
                byte[] compressed = output.ToArray();
                if (compressed.Length > MaxCompressedBytes) throw new FormatException("赛制码压缩数据过长。");
                // 复用队伍码的校验；压缩只用于新前缀，不改变已有队伍码和回放码。
                ulong checksum = TeamCode.Checksum(compressed, compressed.Length);
                for (int i = 0; i < ChecksumSize; i++) output.WriteByte((byte)(checksum >> (8 * i)));
                return Prefix + TeamCode.Base64Url(output.ToArray());
            }
        }

        public static Ruleset Decode(string code)
        {
            try { return DecodeCore(code); }
            catch (FormatException ex)
            {
                throw new FormatException("赛制码格式错误：" + ex.Message, ex);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is ArgumentException || ex is OverflowException)
            {
                throw new FormatException("赛制码压缩数据或文本内容无效。", ex);
            }
        }

        public static Ruleset Decode(string code, ContentDb db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            var ruleset = Decode(code);
            ruleset.Validate(db);
            return ruleset;
        }

        public static Ruleset FromJson(string json, ContentDb db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            Ruleset ruleset;
            try { ruleset = ParseJson(json); }
            catch (EncoderFallbackException ex)
            {
                throw new FormatException("赛制 JSON 含无效的 Unicode 字符。", ex);
            }
            ruleset.Validate(db);
            return ruleset;
        }

        internal static void CheckJsonSize(string json)
        {
            if (json.Length > MaxJsonBytes || Utf8.GetByteCount(json) > MaxJsonBytes)
                throw new FormatException("赛制 JSON 不能超过 64 KiB。");
        }

        static Ruleset ParseJson(string json)
        {
            if (json == null) throw new FormatException("赛制 JSON 不能为空。");
            CheckJsonSize(json);
            var value = MiniJson.Parse(json);
            RulesetJson.RejectDuplicateKeys(json);
            return RulesetJson.ReadValue(value);
        }

        static Ruleset DecodeCore(string code)
        {
            if (code == null) throw new FormatException("赛制码不能为空。");
            var compact = new StringBuilder(Math.Min(code.Length, MaxCodeCharacters));
            foreach (char c in code)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (compact.Length >= MaxCodeCharacters) throw new FormatException("赛制码过长。");
                compact.Append(c);
            }
            string normalized = compact.ToString();
            if (!normalized.StartsWith(Prefix, StringComparison.Ordinal))
                throw new FormatException("赛制码必须以 " + Prefix + " 开头。");
            string encoded = normalized.Substring(Prefix.Length);
            if (encoded.Length == 0 || encoded.Length % 4 == 1) throw new FormatException("赛制码编码长度无效。");
            foreach (char c in encoded)
                if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-' && c != '_')
                    throw new FormatException("赛制码包含无效字符。");
            byte[] data = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/')
                + new string('=', (4 - encoded.Length % 4) % 4));
            if (!string.Equals(TeamCode.Base64Url(data), encoded, StringComparison.Ordinal))
                throw new FormatException("赛制码末尾编码不是规范形式。");
            int compressedLength = data.Length - ChecksumSize;
            if (compressedLength <= 0 || compressedLength > MaxCompressedBytes)
                throw new FormatException("赛制码压缩数据长度无效。");
            ulong expected = TeamCode.Checksum(data, compressedLength);
            for (int i = 0; i < ChecksumSize; i++)
                if (data[compressedLength + i] != (byte)(expected >> (8 * i)))
                    throw new FormatException("赛制码校验失败，内容可能损坏。");

            using (var input = new ShareCode.DeflateInput(data, compressedLength))
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress, true))
                {
                    var buffer = new byte[4096];
                    int count;
                    while ((count = deflate.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + count > MaxJsonBytes) throw new FormatException("赛制码解压后的 JSON 超过 64 KiB。");
                        output.Write(buffer, 0, count);
                    }
                }
                if (input.Position != compressedLength) throw new FormatException("赛制码压缩流末尾含多余数据。");
                return ParseJson(Utf8.GetString(output.ToArray()));
            }
        }
    }

    internal static class RulesetJson
    {
        static readonly HashSet<string> Fields = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "name", "season", "banOne", "leadPick", "bestOf", "modifiers", "turnSeconds", "bankSeconds"
        };

        internal static Ruleset ReadValue(object value)
        {
            if (!(value is Dictionary<string, object> source)) throw new FormatException("赛制必须是 JSON 对象。");
            foreach (string field in source.Keys)
                if (!Fields.Contains(field)) throw new FormatException("赛制含未知字段：" + field);
            return Ruleset.ReadValue(source);
        }

        internal static void RejectDuplicateKeys(string json)
        {
            // 仅在完整 JSON 已解析成功后调用，避免重复字段悄悄覆盖档案或导入配置。
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
                        if (!objects.Peek().Add(key)) throw new FormatException("JSON 字段重复：" + key);
                    }
                }
            }
        }
    }
}
