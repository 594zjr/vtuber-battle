using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.PackCheck
{
    internal static class PackValidator
    {
        public static int Check(ContentDb db, string packPath, IEnumerable<string> sensitiveWords,
            Action<bool, string, string> report)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (report == null) throw new ArgumentNullException(nameof(report));

            int errors = 0;
            bool fanPack = db.PackId != null && db.PackId.StartsWith("fan_", StringComparison.Ordinal);
            var words = (sensitiveWords ?? Array.Empty<string>())
                .Where(word => !string.IsNullOrWhiteSpace(word))
                .Select(word => word.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(word => word, StringComparer.Ordinal)
                .ToArray();
            var usedSkills = new HashSet<string>(StringComparer.Ordinal);

            if (fanPack && (string.IsNullOrWhiteSpace(db.PackNotice) ||
                db.PackNotice.IndexOf("非官方同人作品", StringComparison.Ordinal) < 0))
            {
                report(true, packPath + ":$.notice", "同人包必须有 notice，且包含「非官方同人作品」。");
                errors++;
            }

            if (fanPack)
            {
                CheckText(db.PackName, packPath + ":$.name", words, report);
                CheckText(db.PackNotice, packPath + ":$.notice", words, report);
            }

            for (int i = 0; i < db.Streamers.Count; i++)
            {
                var streamer = db.Streamers[i];
                string location = packPath + ":$.streamers[" + i + "]（id=" + streamer.Id + "）";
                foreach (string skillId in streamer.Skills) usedSkills.Add(skillId);
                usedSkills.Add(streamer.Fifth);

                if (!fanPack) continue;
                CheckText(streamer.Org, location + ".org", words, report);
                CheckText(streamer.Blurb, location + ".blurb", words, report);

                // 覆盖后的基础模板不再显示，只检查主播实际绑定的人设，保留继承说明的检查。
                var persona = db.Personas[streamer.Persona];
                string personaLocation = location + ".persona（载入后 id=" + persona.Id + "）";
                CheckText(persona.Name, personaLocation + ".name / personaName", words, report);
                CheckText(persona.Desc, personaLocation + ".desc / personaDesc", words, report);
                CheckText(persona.Node, personaLocation + ".node", words, report);
            }

            foreach (var pair in db.Skills.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string location = LoadedLocation(packPath, "skills", pair.Key);
                if (fanPack) CheckText(pair.Value.Name, location + ".name / skillNames", words, report);
                if (!usedSkills.Contains(pair.Key))
                    report(false, location, "技能「" + pair.Value.Name + "」没有被此内容包中的任何主播使用（含第五技能）。");
            }

            if (!fanPack) return errors;
            if (db.Struggle != null)
                CheckText(db.Struggle.Name, packPath + "（载入后规则）:$.struggle.name", words, report);

            foreach (var pair in db.Statuses.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string location = LoadedLocation(packPath, "statuses", pair.Key);
                CheckText(pair.Value.Name, location + ".name / statusNames", words, report);
                CheckText(pair.Value.Desc, location + ".desc", words, report);
            }

            foreach (var pair in db.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string location = LoadedLocation(packPath, "fields", pair.Key);
                CheckText(pair.Value.Name, location + ".name / fieldNames", words, report);
                CheckText(pair.Value.Desc, location + ".desc", words, report);
                CheckText(pair.Value.When, location + ".when", words, report);
            }

            foreach (var pair in db.Types.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                CheckText(pair.Value.Name, LoadedLocation(packPath, "types", pair.Key) + ".name", words, report);
            foreach (var pair in db.StatNames.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                CheckText(pair.Value, LoadedLocation(packPath, "stats", pair.Key), words, report);
            foreach (var pair in db.CatNames.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                CheckText(pair.Value, LoadedLocation(packPath, "categories", pair.Key), words, report);
            foreach (var pair in db.Seasons.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                CheckText(pair.Value.Name, packPath + ":$.seasons[\"" + pair.Key + "\"].name", words, report);

            return errors;
        }

        private static void CheckText(string value, string location, IEnumerable<string> words,
            Action<bool, string, string> report)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (string word in words)
            {
                if (value.IndexOf(word, StringComparison.Ordinal) >= 0)
                    report(false, location, "显示文字包含敏感词「" + word + "」，请人工确认是否可能影射真人。");
            }
        }

        private static string LoadedLocation(string packPath, string section, string id)
        {
            return packPath + "（载入后规则）:$." + section + "[\"" + id + "\"]";
        }
    }
}
