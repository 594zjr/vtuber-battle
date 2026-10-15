using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StreamDuel.Core
{
    internal static class NetJson
    {
        public static Dictionary<string, object> Object(string msg)
        {
            if (msg == null) throw new FormatException("网络消息不能为空。");
            return MiniJson.Parse(msg) as Dictionary<string, object>
                ?? throw new FormatException("网络消息必须是 JSON 对象。");
        }

        public static string Text(Dictionary<string, object> obj, string key)
        {
            return Required(obj, key) as string
                ?? throw new FormatException("网络消息字段「" + key + "」必须是字符串。");
        }

        public static long Integer(Dictionary<string, object> obj, string key, long min, long max)
        {
            object value = Required(obj, key);
            string error = "网络消息字段「" + key + "」必须是 " + min.ToString(CultureInfo.InvariantCulture)
                + ".." + max.ToString(CultureInfo.InvariantCulture) + " 范围内的整数。";
            if (!(value is double number) || double.IsNaN(number) || double.IsInfinity(number)
                || number != Math.Truncate(number)) throw new FormatException(error);

            long integer;
            try
            {
                // long.MaxValue 转为 double 会向上舍入，先检查转换才能可靠拒绝越界值。
                integer = checked((long)number);
            }
            catch (OverflowException ex)
            {
                throw new FormatException(error, ex);
            }
            if (integer < min || integer > max) throw new FormatException(error);
            return integer;
        }

        public static List<object> Array(Dictionary<string, object> obj, string key)
        {
            return Required(obj, key) as List<object>
                ?? throw new FormatException("网络消息字段「" + key + "」必须是数组。");
        }

        public static string Quote(string value)
        {
            if (value == null) throw new FormatException("网络消息字符串不能为空值。");
            var text = new StringBuilder();
            text.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\b': text.Append("\\b"); break;
                    case '\t': text.Append("\\t"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\f': text.Append("\\f"); break;
                    case '\r': text.Append("\\r"); break;
                    default:
                        if (char.IsControl(c) || char.IsSurrogate(c) || c == '\u2028' || c == '\u2029')
                            text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else text.Append(c);
                        break;
                }
            }
            return text.Append('"').ToString();
        }

        static object Required(Dictionary<string, object> obj, string key)
        {
            if (obj == null) throw new FormatException("网络消息必须是 JSON 对象。");
            if (key == null) throw new FormatException("网络消息字段名不能为空值。");
            if (!obj.TryGetValue(key, out var value))
                throw new FormatException("网络消息缺少字段「" + key + "」。");
            return value;
        }
    }
}
