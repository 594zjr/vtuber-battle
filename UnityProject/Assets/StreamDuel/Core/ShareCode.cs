using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace StreamDuel.Core
{
    public static class ShareCode
    {
        private const string Prefix = "SD1-";
        private const int MaxJsonBytes = 1024 * 1024;
        private const int MaxCompressedBytes = MaxJsonBytes + 1024;
        private const int MaxCodeCharacters = 4 + (MaxCompressedBytes + 4 + 2) / 3 * 4;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static string Encode(BattleRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            string json = record.ToJson();
            if (json.Length > MaxJsonBytes || Utf8.GetByteCount(json) > MaxJsonBytes)
                throw new FormatException("对战记录过大，分享码最多支持 1 MiB 的 JSON。");
            byte[] text = Utf8.GetBytes(json);
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
                    deflate.Write(text, 0, text.Length);
                byte[] compressed = output.ToArray();
                if (compressed.Length > MaxCompressedBytes) throw new FormatException("分享码压缩数据过长。");
                uint checksum = Checksum(compressed, compressed.Length);
                // 校验压缩字节并按小端追加，解压前即可拒绝传输损坏；这不是安全认证。
                for (int i = 0; i < 4; i++) output.WriteByte((byte)(checksum >> (i * 8)));
                return Prefix + Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
        }

        public static BattleRecord Decode(string code)
        {
            try { return DecodeCore(code); }
            catch (FormatException ex)
            {
                throw new FormatException("分享码格式错误：" + ex.Message, ex);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is ArgumentException || ex is OverflowException)
            {
                throw new FormatException("分享码压缩数据或文本内容无效。", ex);
            }
        }

        private static BattleRecord DecodeCore(string code)
        {
            if (code == null) throw new FormatException("分享码不能为空。");
            var clean = new StringBuilder(Math.Min(code.Length, MaxCodeCharacters));
            foreach (char c in code)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (clean.Length >= MaxCodeCharacters) throw new FormatException("分享码过长。");
                clean.Append(c);
            }
            string value = clean.ToString();
            if (!value.StartsWith(Prefix, StringComparison.Ordinal)) throw new FormatException("分享码必须以 SD1- 开头。");
            string encoded = value.Substring(Prefix.Length);
            if (encoded.Length == 0 || encoded.Length % 4 == 1) throw new FormatException("分享码编码长度无效。");
            foreach (char c in encoded)
                if (Base64Value(c) < 0) throw new FormatException("分享码包含非法编码字符。");
            int remainder = encoded.Length % 4;
            int last = Base64Value(encoded[encoded.Length - 1]);
            if (remainder == 2 && (last & 15) != 0 || remainder == 3 && (last & 3) != 0)
                throw new FormatException("分享码末尾编码不是规范形式。");
            string padded = encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - remainder) % 4);
            byte[] data = Convert.FromBase64String(padded);
            int compressedLength = data.Length - 4;
            if (compressedLength <= 0 || compressedLength > MaxCompressedBytes)
                throw new FormatException("分享码压缩数据长度无效。");
            uint supplied = 0;
            for (int i = 0; i < 4; i++) supplied |= (uint)data[compressedLength + i] << (i * 8);
            if (Checksum(data, compressedLength) != supplied) throw new FormatException("分享码校验不符，内容可能已损坏。");

            using (var input = new DeflateInput(data, compressedLength))
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress, true))
                {
                    var buffer = new byte[4096];
                    int count;
                    while ((count = deflate.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + count > MaxJsonBytes) throw new FormatException("分享码解压后的 JSON 超过 1 MiB。");
                        output.Write(buffer, 0, count);
                    }
                }
                if (input.Position != compressedLength) throw new FormatException("分享码压缩流末尾含多余数据。");
                return BattleRecord.FromJson(Utf8.GetString(output.ToArray()));
            }
        }

        private static uint Checksum(byte[] data, int count)
        {
            uint hash = 2166136261;
            for (int i = 0; i < count; i++) hash = unchecked((hash ^ data[i]) * 16777619);
            return hash;
        }

        private static int Base64Value(char c)
        {
            if (c >= 'A' && c <= 'Z') return c - 'A';
            if (c >= 'a' && c <= 'z') return c - 'a' + 26;
            if (c >= '0' && c <= '9') return c - '0' + 52;
            if (c == '-') return 62;
            if (c == '_') return 63;
            return -1;
        }

        internal sealed class DeflateInput : MemoryStream
        {
            public DeflateInput(byte[] data, int length) : base(data, 0, length, false) { }

            // DeflateStream 可能预读或静默接受提前 EOF；逐字节供应并拒绝 EOF，让完整终止与尾随字节可检查。
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count == 0) return 0;
                RequireData();
                return base.Read(buffer, offset, Math.Min(count, 1));
            }

            public override int Read(Span<byte> buffer)
            {
                if (buffer.Length == 0) return 0;
                RequireData();
                return base.Read(buffer.Slice(0, 1));
            }

            public override int ReadByte()
            {
                RequireData();
                return base.ReadByte();
            }

            private void RequireData()
            {
                if (Position == Length) throw new InvalidDataException("分享码压缩流尚未完整结束。");
            }
        }
    }
}
