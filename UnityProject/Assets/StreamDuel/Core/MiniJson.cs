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
        public static object Parse(string json)
        {
            int i = 0;
            var v = ReadValue(json, ref i);
            SkipWs(json, ref i);
            if (i != json.Length) throw new FormatException("JSON 末尾有多余内容，位置 " + i);
            return v;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static object ReadValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON 意外结束");
            char c = s[i];
            if (c == '{') return ReadObject(s, ref i);
            if (c == '[') return ReadArray(s, ref i);
            if (c == '"') return ReadString(s, ref i);
            if (c == 't' && Match(s, ref i, "true")) return true;
            if (c == 'f' && Match(s, ref i, "false")) return false;
            if (c == 'n' && Match(s, ref i, "null")) return null;
            return ReadNumber(s, ref i);
        }

        static bool Match(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException("JSON 无法识别，位置 " + i);
            i += word.Length;
            return true;
        }

        static Dictionary<string, object> ReadObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++;
            SkipWs(s, ref i);
            if (s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                string key = ReadString(s, ref i);
                SkipWs(s, ref i);
                if (s[i] != ':') throw new FormatException("JSON 缺少冒号，位置 " + i);
                i++;
                d[key] = ReadValue(s, ref i);
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("JSON 对象格式错误，位置 " + i);
            }
        }

        static List<object> ReadArray(string s, ref int i)
        {
            var list = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ReadValue(s, ref i));
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException("JSON 数组格式错误，位置 " + i);
            }
        }

        static string ReadString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("JSON 需要字符串，位置 " + i);
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("JSON 字符串没有结束");
        }

        static double ReadNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (start == i) throw new FormatException("JSON 无法识别，位置 " + i);
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
