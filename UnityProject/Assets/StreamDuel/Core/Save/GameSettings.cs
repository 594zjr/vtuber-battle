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
        public double AnimationSpeed = 1;
        public int TurnSeconds = 10;
        public bool Fullscreen;
        public int Width = 1600, Height = 900;
        public AiLevel AiLevel = StreamDuel.Core.AiLevel.Normal;
        public string DefaultPack = "fan_asoul_vr";
        public string Language = "zh-CN";
        public int Version = CurrentVersion;

        public string ToJson()
        {
            double speed = ValidSpeed(AnimationSpeed);
            int seconds = ValidInt(TurnSeconds, 1, 300, 10);
            int width = ValidInt(Width, 640, 7680, 1600);
            int height = ValidInt(Height, 360, 4320, 900);
            var text = new StringBuilder("{\"aiLevel\":");
            WriteString(text, AiLevel.ToString());
            // 可选字段不改变旧默认设置的编码，旧存档缺省时仍使用原窗口与计时。
            if (speed != 1)
                text.Append(",\"animationSpeed\":").Append(speed.ToString(CultureInfo.InvariantCulture));
            text.Append(",\"animations\":").Append(Animations ? "true" : "false");
            // 缺省即关闭，保留旧设置的序列化形状；开启时才写入新增选项。
            if (ColorblindMode) text.Append(",\"colorblindMode\":true");
            text.Append(",\"defaultPack\":");
            WriteString(text, DefaultPack);
            text.Append(",\"fastPlay\":").Append(FastPlay ? "true" : "false");
            if (Fullscreen) text.Append(",\"fullscreen\":true");
            if (height != 900)
                text.Append(",\"height\":").Append(height.ToString(CultureInfo.InvariantCulture));
            text.Append(",\"language\":");
            WriteString(text, Language);
            if (seconds != 10)
                text.Append(",\"turnSeconds\":").Append(seconds.ToString(CultureInfo.InvariantCulture));
            text.Append(",\"turnTimer\":").Append(TurnTimer ? "true" : "false");
            text.Append(",\"version\":").Append(Version.ToString(CultureInfo.InvariantCulture));
            if (width != 1600)
                text.Append(",\"width\":").Append(width.ToString(CultureInfo.InvariantCulture));
            return text.Append('}').ToString();
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
            if (source.TryGetValue("animationSpeed", out var speed) && speed is double numberSpeed)
                settings.AnimationSpeed = ValidSpeed(numberSpeed);
            settings.TurnSeconds = ReadInt(source, "turnSeconds", 1, 300, settings.TurnSeconds);
            settings.Fullscreen = ReadBool(source, "fullscreen", settings.Fullscreen);
            settings.Width = ReadInt(source, "width", 640, 7680, settings.Width);
            settings.Height = ReadInt(source, "height", 360, 4320, settings.Height);
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

        static double ValidSpeed(double value)
        {
            return value == 0.5 || value == 1 || value == 2 ? value : 1;
        }

        static int ValidInt(int value, int minimum, int maximum, int fallback)
        {
            return value >= minimum && value <= maximum ? value : fallback;
        }

        static int ReadInt(Dictionary<string, object> source, string key, int minimum, int maximum, int fallback)
        {
            return source.TryGetValue(key, out var value) && value is double number
                && number >= minimum && number <= maximum && number == Math.Truncate(number)
                ? (int)number : fallback;
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
