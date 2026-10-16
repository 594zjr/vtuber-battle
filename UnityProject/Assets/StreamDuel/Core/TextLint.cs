using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace StreamDuel.Core
{
    public sealed class LintIssue
    {
        public string Level, Where, Id, Message;
    }

    public static class TextLint
    {
        public const int StreamerNameLimit = 10;
        public const int SkillNameLimit = 6;
        public const int PersonaNameLimit = 8;
        public const int StatusNameLimit = 6;
        public const int FieldNameLimit = 6;
        public const int BlurbLimit = 40;

        // 默认入口不依赖 Unity 或工作目录；资源维护入口与此快照的一致性由测试约束。
        public static IReadOnlyList<string> DefaultBannedWords { get; } = Array.AsReadOnly(new[]
        {
            "赛尔号", "淘米", "中之人", "前世", "转生", "本人", "真名", "黑历史", "炎上", "毕业"
        });

        private static readonly Regex Url = new Regex(@"https?://[^\s<>「」“”，。！？；：\u3400-\u9fff]+", RegexOptions.CultureInvariant);
        private static readonly Regex UnitSpace = new Regex(@"[0-9][ \t\u00a0\u3000]+(?=回合|次|点|层|秒|分钟|小时|人气|人|位|个|倍|PP\b|[%％])", RegexOptions.CultureInvariant);
        private static readonly Regex SkillReference = new Regex(@"技能(?:\[(?<id>[^\]\r\n]+)\])?[「“](?<name>[^」”\r\n]+)[」”]", RegexOptions.CultureInvariant);

        public static List<LintIssue> Check(ContentDb db)
        {
            return Check(db, DefaultBannedWords);
        }

        public static List<LintIssue> Check(ContentDb db, IEnumerable<string> bannedWords)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (bannedWords == null) throw new ArgumentNullException(nameof(bannedWords));
            var words = bannedWords.Where(word => !string.IsNullOrWhiteSpace(word))
                .Select(word => word.Trim()).Where(word => !word.StartsWith("#", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).OrderBy(word => word, StringComparer.Ordinal).ToArray();
            var issues = new List<LintIssue>();
            bool fan = db.PackId != null && db.PackId.StartsWith("fan_", StringComparison.Ordinal);
            var skillNames = new HashSet<string>(db.Skills.Values.Select(skill => skill.Name), StringComparer.Ordinal);
            if (db.Struggle != null) skillNames.Add(db.Struggle.Name);

            void CheckSkillReferences(string value, string where, string id)
            {
                void Add(string level, string message)
                {
                    issues.Add(new LintIssue { Level = level, Where = where, Id = id, Message = message });
                }

                foreach (System.Text.RegularExpressions.Match reference in SkillReference.Matches(value))
                {
                    string skillId = reference.Groups["id"].Value;
                    string name = reference.Groups["name"].Value;
                    if (skillId.Length > 0)
                    {
                        SkillDef skill;
                        if (skillId == "struggle") skill = db.Struggle;
                        else db.Skills.TryGetValue(skillId, out skill);
                        if (skill == null || !string.Equals(name, skill.Name, StringComparison.Ordinal))
                            Add("warn", "技能引用「" + skillId + "」的名字「" + name + "」与当前 name 不一致或技能不存在。");
                    }
                    else if (!skillNames.Contains(name))
                        Add("warn", "说明中的技能名字「" + name + "」与当前技能 name 不一致或技能不存在。");
                }
            }

            void CheckText(string value, string where, string id, int limit = 0, bool required = true, bool notice = false)
            {
                void Add(string level, string message)
                {
                    issues.Add(new LintIssue { Level = level, Where = where, Id = id, Message = message });
                }

                if (!required && value == null) return;
                if (string.IsNullOrWhiteSpace(value))
                {
                    Add("error", "文本不能为空或只有空白。");
                    return;
                }
                int length = CountCharacters(value);
                if (limit > 0 && length > limit)
                {
                    Add((long)length * 2 > limit * 3 ? "error" : "warn",
                        "文本长度为 " + length.ToString(CultureInfo.InvariantCulture) + " 字，建议不超过 " +
                        limit.ToString(CultureInfo.InvariantCulture) + " 字；超过 1.5 倍为错误。");
                }
                foreach (string word in words)
                    if (value.IndexOf(word, StringComparison.Ordinal) >= 0)
                        Add("error", "文本包含禁用词「" + word + "」。");
                if (fan && !notice)
                    foreach (string word in new[] { "官方", "授权" })
                        if (value.IndexOf(word, StringComparison.Ordinal) >= 0)
                            Add("error", "同人包声明以外的文本不能包含「" + word + "」。");

                string prose = Url.Replace(value, "");
                if (HasChinese(prose) && HasHalfWidthPunctuation(prose))
                    Add("warn", "中文文本混用半角标点，请统一使用，。：；！？（小数点和网址除外）。");
                if (UnitSpace.IsMatch(prose))
                    Add("warn", "数字和单位之间有多余空格。");
                CheckSkillReferences(value, where, id);
            }

            CheckText(db.PackName, "$.name", db.PackId);
            CheckText(db.PackNotice, "$.notice", db.PackId, required: fan, notice: true);
            foreach (var pair in db.StatNames.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                CheckText(pair.Value, "$.stats[" + pair.Key + "]", pair.Key);
            foreach (var pair in db.CatNames.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                CheckText(pair.Value, "$.categories[" + pair.Key + "]", pair.Key);
            foreach (var pair in db.Types.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                CheckText(pair.Value.Name, "$.types[" + pair.Key + "].name", pair.Key);
            foreach (var pair in db.Skills.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                CheckText(pair.Value.Name, "$.skills[" + pair.Key + "].name", pair.Key, SkillNameLimit);
                for (int i = 0; i < pair.Value.Effects.Count; i++)
                    CheckSkillReferences(Describe.Effect(db, pair.Value.Effects[i]).Text,
                        "$.skills[" + pair.Key + "].effects[" + i.ToString(CultureInfo.InvariantCulture) + "].generatedText", pair.Key);
            }
            if (db.Struggle != null)
            {
                CheckText(db.Struggle.Name, "$.struggle.name", db.Struggle.Id, SkillNameLimit);
                for (int i = 0; i < db.Struggle.Effects.Count; i++)
                    CheckSkillReferences(Describe.Effect(db, db.Struggle.Effects[i]).Text,
                        "$.struggle.effects[" + i.ToString(CultureInfo.InvariantCulture) + "].generatedText", db.Struggle.Id);
            }
            foreach (var pair in db.Statuses.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                CheckText(pair.Value.Name, "$.statuses[" + pair.Key + "].name", pair.Key, StatusNameLimit);
                CheckText(pair.Value.Desc, "$.statuses[" + pair.Key + "].desc", pair.Key, required: false);
            }
            foreach (var pair in db.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                CheckText(pair.Value.Name, "$.fields[" + pair.Key + "].name", pair.Key, FieldNameLimit);
                CheckText(pair.Value.Desc, "$.fields[" + pair.Key + "].desc", pair.Key, required: false);
                CheckText(pair.Value.When, "$.fields[" + pair.Key + "].when", pair.Key, required: false);
            }
            foreach (var pair in db.Personas.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                CheckText(pair.Value.Name, "$.personas[" + pair.Key + "].name", pair.Key, PersonaNameLimit);
                CheckText(pair.Value.Desc, "$.personas[" + pair.Key + "].desc", pair.Key, required: false);
                CheckText(pair.Value.Node, "$.personas[" + pair.Key + "].node", pair.Key, required: false);
            }
            for (int i = 0; i < db.Streamers.Count; i++)
            {
                var streamer = db.Streamers[i];
                string where = "$.streamers[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                CheckText(streamer.Name, where + ".name", streamer.Id, StreamerNameLimit);
                CheckText(streamer.Blurb, where + ".blurb", streamer.Id, BlurbLimit);
                CheckText(streamer.Org, where + ".org", streamer.Id, required: false);
            }
            foreach (var pair in db.Seasons.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                CheckText(pair.Value.Name, "$.seasons[" + pair.Key + "].name", pair.Key);
            return issues;
        }

        private static int CountCharacters(string value)
        {
            int count = 0;
            for (int i = 0; i < value.Length; i++, count++)
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
            return count;
        }

        private static bool HasChinese(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                int code = value[i];
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    code = char.ConvertToUtf32(value[i], value[++i]);
                if ((code >= 0x3400 && code <= 0x9fff) || (code >= 0xf900 && code <= 0xfaff) ||
                    (code >= 0x20000 && code <= 0x323af)) return true;
            }
            return false;
        }

        private static bool HasHalfWidthPunctuation(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (",:;!?".IndexOf(value[i]) >= 0) return true;
                if (value[i] == '.' && !(i > 0 && i + 1 < value.Length &&
                    char.IsDigit(value[i - 1]) && char.IsDigit(value[i + 1]))) return true;
            }
            return false;
        }
    }
}
