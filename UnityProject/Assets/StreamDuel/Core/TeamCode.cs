using System;
using System.Collections.Generic;
using System.Text;

namespace StreamDuel.Core
{
    public static class TeamCode
    {
        const string Prefix = "SDT1-";
        const int ChecksumSize = 8;
        static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static string Encode(TeamPreset preset)
        {
            if (preset == null) throw new ArgumentNullException(nameof(preset));
            var text = new StringBuilder("[");
            TeamPresetJson.WriteString(text, preset.Name);
            text.Append(',');
            TeamPresetJson.WriteString(text, preset.PackId);
            text.Append(',');
            TeamPresetJson.WriteIds(text, preset.Ids);
            text.Append(']');

            byte[] payload = Utf8.GetBytes(text.ToString());
            byte[] data = new byte[payload.Length + ChecksumSize];
            Buffer.BlockCopy(payload, 0, data, 0, payload.Length);
            ulong checksum = Checksum(data, payload.Length);
            for (int i = 0; i < ChecksumSize; i++) data[payload.Length + i] = (byte)(checksum >> (8 * i));
            return Prefix + Base64Url(data);
        }

        public static TeamPreset Decode(string code)
        {
            if (code == null) throw new FormatException("队伍码不能为空值");
            var compact = new StringBuilder();
            foreach (char c in code) if (!char.IsWhiteSpace(c)) compact.Append(c);
            string normalized = compact.ToString();
            if (!normalized.StartsWith(Prefix, StringComparison.Ordinal))
                throw new FormatException("队伍码必须以 " + Prefix + " 开头");

            string encoded = normalized.Substring(Prefix.Length);
            if (encoded.Length == 0 || encoded.Length % 4 == 1)
                throw new FormatException("队伍码长度无效");
            foreach (char c in encoded)
                if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-' && c != '_')
                    throw new FormatException("队伍码包含无效字符");

            byte[] data = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/')
                + new string('=', (4 - encoded.Length % 4) % 4));
            // 标准解码器允许末尾未使用位不为零；规范化比较能让这些抄错的码也被拒绝。
            if (!string.Equals(Base64Url(data), encoded, StringComparison.Ordinal) || data.Length <= ChecksumSize)
                throw new FormatException("队伍码数据无效或不完整");

            int payloadLength = data.Length - ChecksumSize;
            ulong expected = Checksum(data, payloadLength);
            for (int i = 0; i < ChecksumSize; i++)
                if (data[payloadLength + i] != (byte)(expected >> (8 * i)))
                    throw new FormatException("队伍码校验失败，内容可能损坏");

            string json;
            try
            {
                json = Utf8.GetString(data, 0, payloadLength);
            }
            catch (DecoderFallbackException error)
            {
                throw new FormatException("队伍码文字编码无效", error);
            }
            var fields = MiniJson.Parse(json) as List<object>;
            if (fields == null || fields.Count != 3) throw new FormatException("队伍码数据格式无效");
            return new TeamPreset
            {
                Name = TeamPresetJson.ReadString(fields[0]),
                PackId = TeamPresetJson.ReadString(fields[1]),
                Ids = TeamPresetJson.ReadIds(fields[2])
            };
        }

        static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        static ulong Checksum(byte[] data, int length)
        {
            // 此校验只用于识别复制损坏；公开队伍码不承担身份认证。
            ulong hash = 14695981039346656037UL;
            unchecked
            {
                for (int i = 0; i < length; i++) hash = (hash ^ data[i]) * 1099511628211UL;
            }
            return hash;
        }
    }
}
