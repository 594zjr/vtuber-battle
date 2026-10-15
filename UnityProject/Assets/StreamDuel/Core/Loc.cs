using System;
using System.Collections.Generic;
using System.Globalization;

namespace StreamDuel.Core
{
    public sealed partial class Loc
    {
        public string Language;

        private readonly Dictionary<string, string> texts;
        private readonly Loc fallback;
        private static readonly Lazy<Loc> builtinChinese = new Lazy<Loc>(() => FromJson(BuiltinChineseJson));
        private static volatile Loc current;

        public static Loc Current
        {
            get { return current ?? builtinChinese.Value; }
            // 调用方可在首次查询前注入资源；清空注入后恢复内置中文，不依赖 Unity 或文件系统。
            set { current = value; }
        }

        private Loc(string language, Dictionary<string, string> texts, Loc fallback)
        {
            Language = language;
            this.texts = texts;
            this.fallback = fallback;
        }

        public static Loc FromJson(string json, Loc fallback = null)
        {
            if (!(MiniJson.Parse(json) is Dictionary<string, object> root))
                throw new FormatException("文本表必须是 JSON 对象。");
            if (!root.TryGetValue("language", out var languageValue)
                || !(languageValue is string language) || string.IsNullOrWhiteSpace(language))
                throw new FormatException("文本表 language 必须是非空的语言编号字符串。");
            if (!root.TryGetValue("texts", out var textsValue)
                || !(textsValue is Dictionary<string, object> source))
                throw new FormatException("文本表 texts 必须是对象。");

            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in source)
            {
                if (!(pair.Value is string text))
                    throw new FormatException("文本表键「" + pair.Key + "」的值必须是字符串。");
                texts.Add(pair.Key, text);
            }
            return new Loc(language, texts, fallback);
        }

        public string T(string key, params object[] args)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (!TryGetText(key, out string text)) return "[" + key + "]";
            return string.Format(CultureInfo.InvariantCulture, text, args ?? Array.Empty<object>());
        }

        public bool Has(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            return TryGetText(key, out _);
        }

        private bool TryGetText(string key, out string text)
        {
            if (texts.TryGetValue(key, out text)) return true;
            return fallback != null && fallback.TryGetText(key, out text);
        }
    }
}
