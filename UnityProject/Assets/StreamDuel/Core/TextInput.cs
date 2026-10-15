using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public enum TextInputKind { PlayerName, TeamName, RoomName, Note }

    public sealed class TextInputResult
    {
        public string Text { get; }
        public string Message { get; }
        public bool Changed { get; }
        public bool UsedDefault { get; }

        internal TextInputResult(string text, string message, bool changed, bool usedDefault)
        {
            Text = text;
            Message = message;
            Changed = changed;
            UsedDefault = usedDefault;
        }
    }

    public static class TextInput
    {
        public static TextInputResult Clean(string raw, TextInputKind kind)
        {
            int limit;
            string fallback;
            switch (kind)
            {
                case TextInputKind.PlayerName: limit = 12; fallback = "玩家"; break;
                case TextInputKind.TeamName: limit = 16; fallback = "我的队伍"; break;
                case TextInputKind.RoomName: limit = 24; fallback = "对战房间"; break;
                case TextInputKind.Note: limit = 60; fallback = "无备注"; break;
                default: throw new ArgumentOutOfRangeException(nameof(kind), "未知的文字输入类型");
            }

            var messages = new List<string>();
            var text = new StringBuilder();
            bool removed = false, spaces = false;
            string source = raw ?? "";
            for (int i = 0; i < source.Length; i++)
            {
                char ch = source[i];
                int width = 1;
                if (char.IsHighSurrogate(ch) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1])) width = 2;
                else if (char.IsSurrogate(ch)) { removed = true; continue; }
                var category = CharUnicodeInfo.GetUnicodeCategory(source, i);
                if (category == UnicodeCategory.Control || category == UnicodeCategory.Format ||
                    category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator || ch == '\u034f')
                {
                    removed = true;
                    i += width - 1;
                    continue;
                }
                if (char.IsWhiteSpace(ch))
                {
                    if (ch != ' ' || text.Length == 0 || text[text.Length - 1] == ' ') spaces = true;
                    if (text.Length > 0 && text[text.Length - 1] != ' ') text.Append(' ');
                }
                else text.Append(source, i, width);
                i += width - 1;
            }
            if (text.Length > 0 && text[text.Length - 1] == ' ') { text.Length--; spaces = true; }
            if (removed) messages.Add("已移除控制字符、换行、不可见格式字符或无效字符");
            if (spaces) messages.Add("已统一空格并整理连续及首尾空白");

            string cleaned = text.ToString();
            bool banned = false;
            // 与内容包检查共用唯一词表；先移除不可见字符，防止它们隔开词语绕过匹配。
            foreach (string word in TextLint.DefaultBannedWords.OrderByDescending(word => word.Length))
            {
                if (cleaned.IndexOf(word, StringComparison.Ordinal) < 0) continue;
                cleaned = cleaned.Replace(word, new string('*', ScalarCount(word)));
                banned = true;
            }
            if (banned) messages.Add("已用 * 替换不适宜显示的词语");

            int end = 0, count = 0;
            while (end < cleaned.Length && count < limit)
            {
                end += char.IsHighSurrogate(cleaned[end]) ? 2 : 1;
                count++;
            }
            if (end < cleaned.Length)
            {
                cleaned = cleaned.Substring(0, end).TrimEnd(' ');
                messages.Add("已截断至不超过 " + limit.ToString(CultureInfo.InvariantCulture) + " 字");
            }
            bool usedDefault = cleaned.Length == 0;
            if (usedDefault)
            {
                cleaned = fallback;
                messages.Add("内容为空，已使用默认文字「" + fallback + "」");
            }
            return new TextInputResult(cleaned, string.Join("；", messages),
                !string.Equals(raw, cleaned, StringComparison.Ordinal), usedDefault);
        }

        static int ScalarCount(string text)
        {
            int count = 0;
            for (int i = 0; i < text.Length; i++, count++)
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            return count;
        }

        /// <summary>生成显示副本；原始录像、网络同步值和战斗随机流保持不变。</summary>
        public static Battle ForDisplay(Battle source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var copy = source.Clone();
            string[] rawNames = source.Sides.Select(side => side.Name).ToArray();
            string[] names = rawNames.Select(name => Clean(name, TextInputKind.PlayerName).Text).ToArray();
            for (int side = 0; side < copy.Sides.Length; side++) copy.Sides[side].Name = names[side];
            if (copy.Record.Names != null)
                copy.Record.Names = copy.Record.Names.Select(name => Clean(name, TextInputKind.PlayerName).Text).ToArray();
            foreach (var entry in copy.Log) entry.Text = DisplayLog(entry, rawNames, names, source.Winner);
            return copy;
        }

        static string DisplayLog(LogEntry entry, string[] rawNames, string[] names, int winner)
        {
            string value = entry.Text;
            if (value == null) return null;
            // 只替换日志模板中的玩家位置，避免空白昵称或同名片段改写规则文字和主播名。
            int side = entry.Side;
            if (side >= 0 && side < names.Length)
            {
                string prefix = null, replacement = null;
                if (entry.Kind == "enter")
                {
                    prefix = rawNames[side] + "：";
                    replacement = names[side] + "：";
                }
                else if (entry.Kind == "lead")
                {
                    prefix = rawNames[side] + " 选择「";
                    replacement = names[side] + " 选择「";
                }
                else if (entry.Kind == "ban")
                {
                    prefix = rawNames[side] + " 禁用了 " + rawNames[1 - side] + " 的「";
                    replacement = names[side] + " 禁用了 " + names[1 - side] + " 的「";
                }
                if (prefix != null && value.StartsWith(prefix, StringComparison.Ordinal))
                    return replacement + value.Substring(prefix.Length);
            }
            if (entry.Node == Node.Over && entry.Kind == "turn" && winner >= 0 && winner < names.Length
                && value == rawNames[winner] + " 获胜") return names[winner] + " 获胜";
            return value;
        }
    }
}
