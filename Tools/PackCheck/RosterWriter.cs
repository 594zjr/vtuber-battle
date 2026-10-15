using System;
using System.Globalization;
using System.Linq;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.PackCheck
{
    internal static class RosterWriter
    {
        public static string Render(ContentDb db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                // Describe 的数值文字受当前区域影响，统一格式后文档才能稳定比较。
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                var text = new StringBuilder();
                Line(text, "# " + Escape(db.PackName ?? db.PackId) + "：主播名单");
                Line(text);
                Line(text, "内容包编号：" + Escape(db.PackId));
                Line(text);
                if (!string.IsNullOrWhiteSpace(db.PackNotice))
                {
                    Line(text, Escape(db.PackNotice));
                    Line(text);
                }
                Line(text, "主播按编号排列；普通技能保留内容包中的顺序，第五技能单独标明；效果按规则数据中的顺序列出。");
                Line(text);

                foreach (var streamer in db.Streamers.OrderBy(s => s.Id, StringComparer.Ordinal))
                {
                    var persona = db.Personas[streamer.Persona];
                    Line(text, "## " + Escape(streamer.Name) + "（" + Escape(streamer.Id) + "）");
                    Line(text);
                    Line(text, "- 所属：" + Escape(string.IsNullOrWhiteSpace(streamer.Org) ? "未填写" : streamer.Org));
                    string trackIds = streamer.Type + (streamer.Type2 == null ? "" : "/" + streamer.Type2);
                    Line(text, "- 赛道：" + Escape(Describe.Tracks(db, streamer)) + "（" + Escape(trackIds) + "）");
                    Line(text, "- 简介：" + Escape(string.IsNullOrWhiteSpace(streamer.Blurb) ? "未填写" : streamer.Blurb));
                    Line(text, "- 人设：" + Escape(persona.Name) + "（" + Escape(persona.Id) + "）");
                    Line(text, "- 人设判定时机：" + Escape(persona.Node));
                    Line(text, "- 人设说明：" + Escape(persona.Desc));
                    Line(text, "- 人设消除规则：人设永久生效，不能消除；其产生的状态或直播间效果遵守各自的消除规则。");
                    Line(text);

                    for (int i = 0; i < streamer.Skills.Count; i++)
                        WriteSkill(text, db, db.Skills[streamer.Skills[i]], "普通技能 " + (i + 1).ToString(CultureInfo.InvariantCulture));
                    WriteSkill(text, db, db.Skills[streamer.Fifth], "第五技能");
                }
                return text.ToString().TrimEnd('\n') + "\n";
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        private static void WriteSkill(StringBuilder text, ContentDb db, SkillDef skill, string label)
        {
            Line(text, "### " + label + "：" + Escape(skill.Name) + "（" + Escape(skill.Id) + "）");
            Line(text);
            string category;
            if (!db.CatNames.TryGetValue(skill.Cat, out category)) category = skill.Cat;
            Line(text, "赛道：" + Escape(db.Types[skill.Type].Name) + "；类别：" + Escape(category)
                + "；威力：" + skill.Power.ToString(CultureInfo.InvariantCulture)
                + "；PP：" + skill.Pp.ToString(CultureInfo.InvariantCulture)
                + "；命中：" + skill.Acc.ToString(CultureInfo.InvariantCulture) + "%"
                + "；先制：" + skill.Prio.ToString(CultureInfo.InvariantCulture) + "。");
            Line(text);
            if (skill.Effects.Count == 0)
            {
                Line(text, "无附加效果。");
                Line(text);
                return;
            }

            Line(text, "| 序号 | 判定时机 | 效果 | 能否消除 |");
            Line(text, "| --- | --- | --- | --- |");
            for (int i = 0; i < skill.Effects.Count; i++)
            {
                var effect = skill.Effects[i];
                var description = Describe.Effect(db, effect);
                Line(text, "| " + (i + 1).ToString(CultureInfo.InvariantCulture)
                    + " | " + Escape(description.At) + " | " + Escape(description.Text)
                    + " | " + Escape(DispelText(effect, description)) + " |");
            }
            Line(text);
        }

        private static string DispelText(EffectDef effect, EffectText description)
        {
            if (!string.IsNullOrWhiteSpace(description.Dispel)) return description.Dispel;
            switch (effect.Code)
            {
                case "STAGE": return "能力下降不可被消强化；下场清零";
                case "HEAL":
                case "RECOIL":
                case "DRAIN":
                case "POWER_MUL":
                case "POWER_PER_MARK":
                case "DISPEL_TURNS":
                case "DISPEL_BOOST":
                case "CLEANSE":
                case "PP_REDUCE":
                case "FIXED_DAMAGE": return "不适用（即时结算）";
                default: return "未定义消除说明";
            }
        }

        private static string Escape(string value)
        {
            if (value == null) return "";
            var escaped = new StringBuilder();
            string normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (char c in normalized)
            {
                switch (c)
                {
                    case '\n': escaped.Append("<br>"); break;
                    case '&': escaped.Append("&amp;"); break;
                    case '<': escaped.Append("&lt;"); break;
                    case '>': escaped.Append("&gt;"); break;
                    case '\\': case '`': case '*': case '_': case '{': case '}':
                    case '[': case ']': case '(': case ')': case '#': case '+':
                    case '-': case '.': case '!': case '|': case '~':
                        escaped.Append('\\').Append(c);
                        break;
                    default: escaped.Append(c); break;
                }
            }
            return escaped.ToString();
        }

        private static void Line(StringBuilder text, string value = "")
        {
            text.Append(value).Append('\n');
        }
    }
}
