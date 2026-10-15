using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.PackCheck
{
    internal static class CodeUsageReport
    {
        public static string Render(IEnumerable<ContentDb> content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            var packs = content.OrderBy(db => db.PackId, StringComparer.Ordinal).ToList();
            var effects = CreateIndex(EffectCodes.All);
            var personas = CreateIndex(PersonaCodes.All);
            foreach (var db in packs)
            {
                var skills = db.Skills.ToList();
                skills.Add(new KeyValuePair<string, SkillDef>("struggle", db.Struggle));
                foreach (var skill in skills.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ThenBy(pair => ReferenceEquals(pair.Value, db.Struggle) ? 1 : 0))
                    AddSkill(effects, db, skill.Value,
                        (ReferenceEquals(skill.Value, db.Struggle) ? "硬撑 " : "技能 ") + skill.Key);
                foreach (var persona in db.Personas.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    AddUse(personas, persona.Value.Code, db, "人设 " + persona.Key, persona.Value.Name);
            }

            var text = new StringBuilder();
            text.AppendLine("代码使用报告");
            text.AppendLine("范围：" + packs.Count + " 个内容包；按已加载的全部技能（含硬撑）和人设定义统计。");
            text.AppendLine("包含继承的基础定义、本包新增定义及主播复制的人设；定义未被主播选入仍算使用，同一技能的重复效果代码只列一次。");
            text.AppendLine("“未使用”仅指所选内容包的全部定义均没有引用，不代表其他内容包或玩家队伍的使用情况。");
            foreach (var db in packs)
                text.AppendLine("内容包 " + db.PackId + "「" + db.PackName + "」");
            AppendGroup(text, "效果", effects);
            AppendGroup(text, "人设", personas);
            return text.ToString();
        }

        private static SortedDictionary<string, List<string>> CreateIndex(IReadOnlyList<CodeInfo> codes)
        {
            var index = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var code in codes) index.Add(code.Code, new List<string>());
            return index;
        }

        private static void AddSkill(SortedDictionary<string, List<string>> index,
            ContentDb db, SkillDef skill, string location)
        {
            foreach (var code in skill.Effects.Select(effect => effect.Code).Distinct(StringComparer.Ordinal))
                AddUse(index, code, db, location, skill.Name);
        }

        private static void AddUse(SortedDictionary<string, List<string>> index,
            string code, ContentDb db, string location, string name)
        {
            List<string> uses;
            if (code == null || !index.TryGetValue(code, out uses))
                throw new KeyError("内容包 " + db.PackId + " 的" + location + " 使用了未注册代码：" + code);
            uses.Add("  包 " + db.PackId + "「" + db.PackName + "」：" + location + "「" + name + "」");
        }

        private static void AppendGroup(StringBuilder text, string kind,
            SortedDictionary<string, List<string>> index)
        {
            text.AppendLine();
            foreach (var entry in index)
            {
                text.AppendLine(kind + " " + entry.Key);
                if (entry.Value.Count == 0) text.AppendLine("  未使用（所选内容定义中没有引用）");
                else foreach (var usage in entry.Value) text.AppendLine(usage);
            }
            var unused = index.Where(entry => entry.Value.Count == 0).Select(entry => entry.Key).ToArray();
            text.AppendLine(kind + "代码汇总：" + index.Count + " 个代码，" + unused.Length + " 个未使用。");
            text.AppendLine("未使用" + kind + "代码：" + (unused.Length == 0 ? "无" : string.Join("、", unused)) + "。");
        }
    }
}
