// 极简 JSON 读取器：让规则引擎不依赖 Unity 或任何第三方库。
// 对象 -> Dictionary<string, object>，数组 -> List<object>，数字 -> double。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StreamDuel.Core
{
    public static class MiniJson
    {
        const int MaxDepth = 64;

        public static object Parse(string json)
        {
            if (json == null) throw new FormatException("JSON 不能为空");
            int i = 0;
            var v = ReadValue(json, ref i, 0);
            SkipWs(json, ref i);
            if (i != json.Length) throw new FormatException("JSON 末尾有多余内容，位置 " + i);
            return v;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        static char Current(string s, int i)
        {
            if (i >= s.Length) throw new FormatException("JSON 意外结束，位置 " + i);
            return s[i];
        }

        static object ReadValue(string s, ref int i, int depth)
        {
            SkipWs(s, ref i);
            char c = Current(s, i);
            // 外部内容包不可信，限制容器嵌套，避免递归解析耗尽调用栈。
            if ((c == '{' || c == '[') && depth >= MaxDepth)
                throw new FormatException("JSON 嵌套超过 " + MaxDepth + " 层");
            if (c == '{') return ReadObject(s, ref i, depth + 1);
            if (c == '[') return ReadArray(s, ref i, depth + 1);
            if (c == '"') return ReadString(s, ref i);
            if (c == 't' && Match(s, ref i, "true")) return true;
            if (c == 'f' && Match(s, ref i, "false")) return false;
            if (c == 'n' && Match(s, ref i, "null")) return null;
            return ReadNumber(s, ref i);
        }

        static bool Match(string s, ref int i, string word)
        {
            if (s.Length - i < word.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException("JSON 无法识别，位置 " + i);
            i += word.Length;
            return true;
        }

        static Dictionary<string, object> ReadObject(string s, ref int i, int depth)
        {
            var d = new Dictionary<string, object>();
            i++;
            SkipWs(s, ref i);
            if (Current(s, i) == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                string key = ReadString(s, ref i);
                SkipWs(s, ref i);
                if (Current(s, i) != ':') throw new FormatException("JSON 缺少冒号，位置 " + i);
                i++;
                d[key] = ReadValue(s, ref i, depth);
                SkipWs(s, ref i);
                char separator = Current(s, i);
                if (separator == ',') { i++; continue; }
                if (separator == '}') { i++; return d; }
                throw new FormatException("JSON 对象格式错误，位置 " + i);
            }
        }

        static List<object> ReadArray(string s, ref int i, int depth)
        {
            var list = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (Current(s, i) == ']') { i++; return list; }
            while (true)
            {
                list.Add(ReadValue(s, ref i, depth));
                SkipWs(s, ref i);
                char separator = Current(s, i);
                if (separator == ',') { i++; continue; }
                if (separator == ']') { i++; return list; }
                throw new FormatException("JSON 数组格式错误，位置 " + i);
            }
        }

        static string ReadString(string s, ref int i)
        {
            if (Current(s, i) != '"') throw new FormatException("JSON 需要字符串，位置 " + i);
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c < 0x20) throw new FormatException("JSON 字符串包含未转义控制符，位置 " + (i - 1));
                if (c != '\\') { sb.Append(c); continue; }
                char e = Current(s, i++);
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        int code = 0;
                        for (int digit = 0; digit < 4; digit++)
                        {
                            char h = Current(s, i++);
                            int value = h >= '0' && h <= '9' ? h - '0' :
                                h >= 'a' && h <= 'f' ? h - 'a' + 10 : h >= 'A' && h <= 'F' ? h - 'A' + 10 : -1;
                            if (value < 0) throw new FormatException("JSON Unicode 转义格式错误，位置 " + (i - 1));
                            code = code * 16 + value;
                        }
                        sb.Append((char)code);
                        break;
                    default: throw new FormatException("JSON 转义无法识别，位置 " + (i - 1));
                }
            }
            throw new FormatException("JSON 字符串没有结束");
        }

        static double ReadNumber(string s, ref int i)
        {
            int start = i;
            if (Current(s, i) == '-') i++;
            char first = Current(s, i);
            if (first == '0')
            {
                i++;
                if (IsDigit(s, i)) throw new FormatException("JSON 数字不能含前导零，位置 " + i);
            }
            else
            {
                if (first < '1' || first > '9') throw new FormatException("JSON 需要数字，位置 " + i);
                while (IsDigit(s, i)) i++;
            }
            if (i < s.Length && s[i] == '.')
            {
                i++;
                ReadDigits(s, ref i);
            }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                ReadDigits(s, ref i);
            }
            double number;
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
                double.IsNaN(number) || double.IsInfinity(number))
                throw new FormatException("JSON 数值超出支持范围，位置 " + start);
            return number;
        }

        static bool IsDigit(string s, int i) { return i < s.Length && s[i] >= '0' && s[i] <= '9'; }

        static void ReadDigits(string s, ref int i)
        {
            if (!IsDigit(s, i)) throw new FormatException("JSON 数字不完整，位置 " + i);
            while (IsDigit(s, i)) i++;
        }
    }
}
