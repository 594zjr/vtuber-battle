using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class GameSettings
    {
        public const int CurrentVersion = 1;

        public bool FastPlay, TurnTimer, ColorblindMode;
        public bool Animations = true;
        public AiLevel AiLevel = StreamDuel.Core.AiLevel.Normal;
        public string DefaultPack = "fan_asoul_vr";
        public string Language = "zh-CN";
        public int Version = CurrentVersion;

        public string ToJson()
        {
            var text = new StringBuilder("{\"aiLevel\":");
            WriteString(text, AiLevel.ToString());
            text.Append(",\"animations\":").Append(Animations ? "true" : "false");
            // 缺省即关闭，保留旧设置的序列化形状；开启时才写入新增选项。
            if (ColorblindMode) text.Append(",\"colorblindMode\":true");
            text.Append(",\"defaultPack\":");
            WriteString(text, DefaultPack);
            text.Append(",\"fastPlay\":").Append(FastPlay ? "true" : "false");
            text.Append(",\"language\":");
            WriteString(text, Language);
            text.Append(",\"turnTimer\":").Append(TurnTimer ? "true" : "false");
            return text.Append(",\"version\":").Append(Version.ToString(CultureInfo.InvariantCulture)).Append('}').ToString();
        }

        public static GameSettings FromJson(string json)
        {
            var settings = new GameSettings();
            if (string.IsNullOrWhiteSpace(json)) return settings;
            Dictionary<string, object> source;
            try
            {
                source = MiniJson.Parse(json) as Dictionary<string, object>;
            }
            catch (FormatException)
            {
                // 损坏的本地设置不能阻止对战启动；语法失败时不采用部分读取的值。
                return settings;
            }
            if (source == null) return settings;

            settings.FastPlay = ReadBool(source, "fastPlay", settings.FastPlay);
            settings.TurnTimer = ReadBool(source, "turnTimer", settings.TurnTimer);
            settings.Animations = ReadBool(source, "animations", settings.Animations);
            settings.ColorblindMode = ReadBool(source, "colorblindMode", settings.ColorblindMode);
            settings.DefaultPack = ReadText(source, "defaultPack", settings.DefaultPack);
            settings.Language = ReadText(source, "language", settings.Language);
            if (source.TryGetValue("aiLevel", out var value))
            {
                AiLevel level;
                if (value is string name && Enum.TryParse(name, true, out level) && Enum.IsDefined(typeof(AiLevel), level)
                    && string.Equals(name, level.ToString(), StringComparison.OrdinalIgnoreCase))
                    settings.AiLevel = level;
                else if (value is double number && number >= 0 && number <= int.MaxValue && number == Math.Truncate(number)
                    && Enum.IsDefined(typeof(AiLevel), (int)number))
                    settings.AiLevel = (AiLevel)(int)number;
            }
            // 只读取当前认识的设置，旧存档缺失的字段沿用默认值并写入当前版本。
            settings.Version = CurrentVersion;
            return settings;
        }

        static bool ReadBool(Dictionary<string, object> source, string key, bool fallback)
        {
            return source.TryGetValue(key, out var value) && value is bool flag ? flag : fallback;
        }

        static string ReadText(Dictionary<string, object> source, string key, string fallback)
        {
            return source.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
                ? text : fallback;
        }

        static void WriteString(StringBuilder text, string value)
        {
            if (value == null) { text.Append("null"); return; }
            text.Append('"');
            foreach (char c in value)
            {
                if (c == '"') text.Append("\\\"");
                else if (c == '\\') text.Append("\\\\");
                else if (c < ' ' || char.IsSurrogate(c))
                    text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else text.Append(c);
            }
            text.Append('"');
        }
    }
}
