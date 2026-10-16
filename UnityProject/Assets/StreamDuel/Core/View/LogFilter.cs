using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class LogQuery
    {
        public int? Turn;
        public int? Side;
        public HashSet<string> Kinds;
        public string Text;
        public bool OnlyImportant;
    }

    public sealed class LogGroup
    {
        public int Turn;
        public List<int> Indices = new List<int>();
        public string Summary;
    }

    public static class LogFilter
    {
        public static List<int> Apply(Battle b, LogQuery q)
        {
            if (b == null) throw new ArgumentNullException(nameof(b));
            var result = new List<int>();
            var text = Normalize(q == null ? null : q.Text);
            var kinds = q != null && q.Kinds != null && q.Kinds.Count > 0
                ? new HashSet<string>(q.Kinds, StringComparer.Ordinal) : null;
            var important = q != null && q.OnlyImportant ? ImportantLines(b) : null;
            for (int i = 0; i < b.Log.Count; i++)
            {
                var entry = b.Log[i];
                if (entry == null) continue;
                if (q != null && q.Turn.HasValue && entry.Turn != q.Turn.Value) continue;
                if (q != null && q.Side.HasValue && entry.Side != q.Side.Value) continue;
                if (kinds != null && !kinds.Contains(entry.Kind)) continue;
                if (important != null && !important[i]) continue;
                if (text.Length > 0 && Normalize(entry.Text).IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                result.Add(i);
            }
            return result;
        }

        public static List<LogGroup> GroupByTurn(Battle b)
        {
            if (b == null) throw new ArgumentNullException(nameof(b));
            var groups = new SortedDictionary<int, LogGroup>();
            var important = ImportantLines(b);
            for (int i = 0; i < b.Log.Count; i++)
            {
                var entry = b.Log[i];
                if (entry == null) continue;
                if (!groups.TryGetValue(entry.Turn, out var group))
                {
                    group = new LogGroup { Turn = entry.Turn };
                    groups.Add(entry.Turn, group);
                }
                group.Indices.Add(i);
            }
            var result = new List<LogGroup>(groups.Count);
            foreach (var group in groups.Values)
            {
                var excerpts = new List<string>();
                string fallback = null;
                foreach (var index in group.Indices)
                {
                    var entry = b.Log[index];
                    if (string.IsNullOrWhiteSpace(entry.Text)) continue;
                    if (fallback == null && entry.Node != Node.TurnHeader) fallback = entry.Text;
                    if (important[index] && excerpts.Count < 3) excerpts.Add(entry.Text);
                }
                if (excerpts.Count == 0 && fallback != null) excerpts.Add(fallback);
                // 击倒事件不带来源，直接引用日志，避免把反弹或持续伤害归给最近行动者。
                group.Summary = "第" + group.Turn.ToString(CultureInfo.InvariantCulture) + "回合：" +
                    (excerpts.Count == 0 ? "共" + group.Indices.Count.ToString(CultureInfo.InvariantCulture) + "条记录"
                        : string.Join("；", excerpts));
                result.Add(group);
            }
            return result;
        }

        static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder valid = null;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    if (valid != null) { valid.Append(c); valid.Append(text[i + 1]); }
                    i++;
                }
                else if (char.IsSurrogate(c))
                {
                    // 回放保留原始输入；只修复搜索副本，防止 Normalize 拒绝孤立代理项。
                    if (valid == null) valid = new StringBuilder(text.Substring(0, i));
                    valid.Append('\uFFFD');
                }
                else if (valid != null) valid.Append(c);
            }
            return (valid == null ? text : valid.ToString()).Normalize(NormalizationForm.FormKC);
        }

        static bool[] ImportantLines(Battle b)
        {
            var result = new bool[b.Log.Count];
            var templates = new ImportantTemplates(b.Db);
            foreach (var ev in b.Events)
            {
                if (ev == null || ev.LogIndex < 0 || ev.LogIndex >= b.Log.Count) continue;
                var entry = b.Log[ev.LogIndex];
                if (entry == null) continue;
                switch (ev.Kind)
                {
                    case BattleEventKind.Faint:
                    case BattleEventKind.Crit:
                    case BattleEventKind.Switch:
                    case BattleEventKind.End:
                        result[ev.LogIndex] = true;
                        break;
                    case BattleEventKind.Enter:
                        if (entry.Node == Node.Switch || entry.Node == Node.Replace) result[ev.LogIndex] = true;
                        break;
                    case BattleEventKind.StatusAdd:
                    case BattleEventKind.StatusRemove:
                        if (ev.Id != null && b.Db.Statuses.TryGetValue(ev.Id, out var status) && status.IsControl)
                            result[ev.LogIndex] = true;
                        break;
                    case BattleEventKind.FieldAdd:
                        if (ev.Id != null && b.Db.Fields.TryGetValue(ev.Id, out var field) &&
                            (field.Kind != "mark" || ev.Amount != 0)) result[ev.LogIndex] = true;
                        break;
                    case BattleEventKind.Damage:
                        if (ev.Amount > 0 && templates.Damage(entry, ev.Amount, FighterName(b, ev))) result[ev.LogIndex] = true;
                        break;
                    case BattleEventKind.Heal:
                        if (ev.Amount > 0 && templates.Heal(entry.Text, ev.Amount, FighterName(b, ev))) result[ev.LogIndex] = true;
                        break;
                }
            }
            for (int i = 0; i < b.Log.Count; i++)
            {
                var entry = b.Log[i];
                if (entry != null && !result[i]) result[i] = templates.Other(entry, FighterName(b, entry));
            }
            return result;
        }

        static string FighterName(Battle b, BattleEvent ev)
        {
            if (ev.Side < 0 || ev.Side >= b.Sides.Length) return null;
            var team = b.Sides[ev.Side].Team;
            return ev.Slot < 0 || ev.Slot >= team.Count ? null : team[ev.Slot].Name;
        }

        static string FighterName(Battle b, LogEntry entry)
        {
            int side = entry.Side;
            if (side < 0 || side >= b.Sides.Length || entry.SnapActive == null || side >= entry.SnapActive.Length) return null;
            int slot = entry.SnapActive[side];
            var team = b.Sides[side].Team;
            return slot < 0 || slot >= team.Count ? null : team[slot].Name;
        }

        // 这些来源尚无独立事件，只解析引擎明确写出的模板，不从战斗当前状态推测历史。
        sealed class ImportantTemplates
        {
            readonly HashSet<string> controlNames = new HashSet<string>(StringComparer.Ordinal);
            readonly HashSet<string> weakNames = new HashSet<string>(StringComparer.Ordinal);
            readonly HashSet<string> healFields = new HashSet<string>(StringComparer.Ordinal);
            readonly HashSet<string> dotFields = new HashSet<string>(StringComparer.Ordinal);
            readonly HashSet<string> reflectFields = new HashSet<string>(StringComparer.Ordinal);
            readonly HashSet<string> weakShields = new HashSet<string>(StringComparer.Ordinal);
            readonly HashSet<string> markFields = new HashSet<string>(StringComparer.Ordinal);
            readonly List<NoteSource> noteSources = new List<NoteSource>();
            readonly List<string> personaPrefixes = new List<string>();
            bool hasControlShield;

            sealed class NoteSource
            {
                public string Prefix;
                public bool Persona, Important;
            }

            public ImportantTemplates(ContentDb db)
            {
                foreach (var status in db.Statuses.Values)
                {
                    (status.IsControl ? controlNames : weakNames).Add(status.Name);
                    if (status.InteractMul != 1)
                        noteSources.Add(new NoteSource { Prefix = status.Name + " ×" });
                }
                foreach (var persona in db.Personas.Values)
                {
                    personaPrefixes.Add("【人设·" + persona.Name + "】");
                    noteSources.Add(new NoteSource { Prefix = "人设·" + persona.Name + "（", Persona = true, Important = true });
                }
                foreach (var field in db.Fields.Values)
                {
                    if (field.Heal > 0) healFields.Add(field.Name);
                    if (field.Dot > 0) dotFields.Add(field.Name);
                    if (field.ReflectDamage > 0) reflectFields.Add(field.Name);
                    if (field.ImmuneWeak) weakShields.Add(field.Name);
                    if (field.TalentMul != 1 || field.DmgTakenMul != 1)
                        noteSources.Add(new NoteSource { Prefix = field.Name + " ×", Important = true });
                    if (field.Kind == "mark") markFields.Add(field.Name);
                    hasControlShield |= field.ImmuneControl;
                }
                noteSources.Add(new NoteSource { Prefix = "暴击 ×", Important = true });
                // 名称可包含日志分隔符；先消费完整的最长名称，不能把名称里的逗号当成下一条来源。
                noteSources.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));
            }

            public bool Heal(string text, int amount, string name)
            {
                if (text == null || name == null) return false;
                string suffix = name + " 人气 +" + amount.ToString(CultureInfo.InvariantCulture);
                if (!text.EndsWith(suffix, StringComparison.Ordinal)) return false;
                // 事件指向固定队伍槽位；先剥去名字，名字本身不能冒充来源前缀。
                string prefix = text.Substring(0, text.Length - suffix.Length);
                return HasPersonaPrefix(prefix) || QuotedPrefix(prefix, "」：", healFields);
            }

            public bool Damage(LogEntry entry, int amount, string name)
            {
                var text = entry.Text ?? "";
                var number = amount.ToString(CultureInfo.InvariantCulture);
                string suffix = name + " 人气 -" + number;
                if (name != null && text.EndsWith(suffix, StringComparison.Ordinal) &&
                    QuotedPrefix(text.Substring(0, text.Length - suffix.Length), "」反弹：", reflectFields)) return true;
                if (name != null && text.StartsWith(suffix, StringComparison.Ordinal))
                {
                    var tail = text.Substring(suffix.Length);
                    if (tail.StartsWith("，赛道克制", StringComparison.Ordinal) ||
                        tail.StartsWith("，赛道被克", StringComparison.Ordinal)) return true;
                    if (tail.StartsWith("（", StringComparison.Ordinal) && tail.EndsWith("）", StringComparison.Ordinal) &&
                        ImportantNotes(tail.Substring(1, tail.Length - 2))) return true;
                }
                string cause = name + " 因「";
                string damageEnd = "」人气 -" + number;
                return name != null && entry.Node == Node.TurnEnd && text.StartsWith(cause, StringComparison.Ordinal) &&
                    text.EndsWith(damageEnd, StringComparison.Ordinal) && text.Length >= cause.Length + damageEnd.Length &&
                    dotFields.Contains(text.Substring(cause.Length, text.Length - cause.Length - damageEnd.Length));
            }

            bool ImportantNotes(string notes)
            {
                int offset = 0;
                while (offset < notes.Length)
                {
                    NoteSource matched = null;
                    int next = -1;
                    foreach (var source in noteSources)
                    {
                        if (source.Prefix.Length > notes.Length - offset ||
                            string.CompareOrdinal(notes, offset, source.Prefix, 0, source.Prefix.Length) != 0) continue;
                        int number = offset + source.Prefix.Length;
                        if (source.Persona)
                        {
                            int endReason = notes.IndexOf("）×", number, StringComparison.Ordinal);
                            if (endReason < 0) continue;
                            number = endReason + 2;
                        }
                        int separator = notes.IndexOf('，', number);
                        int end = separator < 0 ? notes.Length : separator;
                        if (!double.TryParse(notes.Substring(number, end - number).Replace(',', '.'),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out _)) continue;
                        matched = source;
                        next = end + 1;
                        break;
                    }
                    if (matched == null) return false;
                    if (matched.Important) return true;
                    offset = next;
                }
                return false;
            }

            public bool Other(LogEntry entry, string name)
            {
                var text = entry.Text ?? "";
                if (entry.Kind == "trait")
                    return HasPersonaPrefix(text) && !text.EndsWith("% 判定：未触发）", StringComparison.Ordinal);
                if (entry.Kind == "status" && name != null && text.StartsWith(name + " ", StringComparison.Ordinal))
                {
                    var body = text.Substring(name.Length + 1);
                    if (body == "刚回过神来，本回合来不及行动") return true;
                    foreach (var control in controlNames)
                    {
                        string prefix = "处于「" + control + "」，无法行动";
                        if (body == prefix) return true;
                        prefix += "（还要跳过 ";
                        if (body.Length > prefix.Length + 3 && body.StartsWith(prefix, StringComparison.Ordinal) &&
                            body.EndsWith(" 次）", StringComparison.Ordinal) &&
                            int.TryParse(body.Substring(prefix.Length, body.Length - prefix.Length - 3), out int left) && left > 0) return true;
                    }
                    const string shield = "有「重连保护」，";
                    if (hasControlShield && body.StartsWith(shield, StringComparison.Ordinal) && body.EndsWith("无效", StringComparison.Ordinal) &&
                        controlNames.Contains(body.Substring(shield.Length, body.Length - shield.Length - 2))) return true;
                    if (body.EndsWith("挡下", StringComparison.Ordinal))
                        foreach (var weak in weakNames)
                        {
                            string prefix = "的「" + weak + "」被";
                            if (body.StartsWith(prefix, StringComparison.Ordinal) && body.Length >= prefix.Length + 2 &&
                                weakShields.Contains(body.Substring(prefix.Length, body.Length - prefix.Length - 2))) return true;
                        }
                }
                if (entry.Kind == "check" && text.StartsWith("〔命中前判定〕", StringComparison.Ordinal))
                {
                    const string marker = " 层 → 威力 ×";
                    int end = text.LastIndexOf(marker, StringComparison.Ordinal);
                    int space = end < 0 ? -1 : text.LastIndexOf(' ', end - 1);
                    if (space >= 7 && markFields.Contains(text.Substring(7, space - 7)) &&
                        int.TryParse(text.Substring(space + 1, end - space - 1), out int stacks) && stacks > 0)
                    {
                        // 引擎的 0.## 格式可能使用逗号小数点，但无增益始终恰好写作 1。
                        return text.Substring(end + marker.Length) != "1";
                    }
                }
                return false;
            }

            bool HasPersonaPrefix(string text)
            {
                if (text == null) return false;
                foreach (var prefix in personaPrefixes)
                    if (text.StartsWith(prefix, StringComparison.Ordinal)) return true;
                return false;
            }

            static bool QuotedPrefix(string text, string suffix, HashSet<string> names)
            {
                if (string.IsNullOrEmpty(text) || text[0] != '「') return false;
                return text.EndsWith(suffix, StringComparison.Ordinal) && text.Length >= suffix.Length + 1 &&
                    names.Contains(text.Substring(1, text.Length - suffix.Length - 1));
            }
        }
    }
}
