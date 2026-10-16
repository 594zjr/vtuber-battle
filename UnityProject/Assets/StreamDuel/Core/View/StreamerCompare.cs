using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class StreamerStatComparison
    {
        public string Id, Name;
        public int A, B, HigherSide;
    }

    public sealed class StreamerTrackComparison
    {
        public string Id, Name, Symbol;
        public double Multiplier;
    }

    public sealed class StreamerSkillComparison
    {
        public int Index, BasePriority, Priority;
        public string Id, Name;
        public bool IsFifth;
        public List<string> PriorityNotes = new List<string>();
        public Battle.DamagePreview Range;
        public int? MinHits, MaxHits;
    }

    public sealed class StreamerCompare
    {
        public StreamerCard A, B;
        public string ASourcePackId, ASourcePackName, BSourcePackId, BSourcePackName;
        public List<StreamerStatComparison> Stats = new List<StreamerStatComparison>();
        public List<StreamerTrackComparison> AToBTracks = new List<StreamerTrackComparison>();
        public List<StreamerTrackComparison> BToATracks = new List<StreamerTrackComparison>();
        public List<StreamerSkillComparison> AToBSkills = new List<StreamerSkillComparison>();
        public List<StreamerSkillComparison> BToASkills = new List<StreamerSkillComparison>();

        public static StreamerCompare Build(ContentDb db, string a, string b)
        {
            if (db == null) throw new ArgumentNullException(nameof(db), "内容库不能为空。");
            if (string.IsNullOrWhiteSpace(a)) throw new ArgumentException("甲方主播编号不能为空。", nameof(a));
            if (string.IsNullOrWhiteSpace(b)) throw new ArgumentException("乙方主播编号不能为空。", nameof(b));
            var first = db.Streamer(a);
            var second = db.Streamer(b);
            if (first.Stats["hp"] <= 0 || second.Stats["hp"] <= 0)
                throw new ArgumentException("参与对比的主播最大人气必须大于 0。");
            var comparison = new StreamerCompare
            {
                A = StreamerCard.Build(db, a), B = StreamerCard.Build(db, b),
                ASourcePackId = db.SourcePackIdFor(a), ASourcePackName = db.SourcePackNameFor(a),
                BSourcePackId = db.SourcePackIdFor(b), BSourcePackName = db.SourcePackNameFor(b)
            };
            foreach (var stat in comparison.A.Stats)
            {
                int other = second.Stats[stat.Id];
                comparison.Stats.Add(new StreamerStatComparison
                {
                    Id = stat.Id, Name = stat.Name, A = stat.Value, B = other,
                    HigherSide = stat.Value == other ? -1 : stat.Value > other ? 0 : 1
                });
            }
            AddTracks(db, comparison.A, second, comparison.AToBTracks);
            AddTracks(db, comparison.B, first, comparison.BToATracks);
            AddSkills(db, comparison.A, second, comparison.AToBSkills);
            AddSkills(db, comparison.B, first, comparison.BToASkills);
            return comparison;
        }

        static void AddTracks(ContentDb db, StreamerCard attacker, StreamerDef defender,
            List<StreamerTrackComparison> destination)
        {
            foreach (var track in attacker.Tracks)
                destination.Add(new StreamerTrackComparison
                {
                    Id = track.Id, Name = track.Name, Symbol = track.Symbol,
                    Multiplier = db.TypeMul(track.Id, defender.Type, defender.Type2)
                });
        }

        static void AddSkills(ContentDb db, StreamerCard attacker, StreamerDef defender,
            List<StreamerSkillComparison> destination)
        {
            // 与 Range 使用相同的一对一开场，双方各自作为攻击方，不凭空加入后排。
            var opening = new Battle(db, new IList<string>[] { new[] { attacker.Id }, new[] { defender.Id } },
                new[] { "你", "对手" }, 0);
            var zeroStages = Fighter.NewStages();
            foreach (string stat in ContentDb.StatKeys)
            {
                opening.Active(0).Stages[stat] = 0;
                opening.Active(1).Stages[stat] = 0;
            }
            for (int index = 0; index < attacker.Skills.Count; index++)
            {
                var skill = attacker.Skills[index];
                var range = DamageCalc.Range(db, attacker.Id, index, defender.Id, zeroStages, zeroStages);
                var priority = opening.PrioOf(0, db.Skills[skill.Id]);
                destination.Add(new StreamerSkillComparison
                {
                    Index = index, Id = skill.Id, Name = skill.Name, IsFifth = skill.IsFifth,
                    BasePriority = skill.Priority, Priority = priority.Value,
                    PriorityNotes = new List<string>(priority.Parts), Range = range,
                    MinHits = Hits(defender.Stats["hp"], range.Max),
                    MaxHits = Hits(defender.Stats["hp"], range.Min)
                });
            }
        }

        static int? Hits(int hp, int damage)
        {
            // 减一后整除，避免最大人气与伤害相加溢出。
            return damage > 0 ? (int?)((hp - 1) / damage + 1) : null;
        }

        public string ToText() { return ToText(this); }

        public static string ToText(StreamerCompare comparison)
        {
            if (comparison == null) throw new ArgumentNullException(nameof(comparison), "主播对比不能为空。");
            var text = new StringBuilder();
            text.Append("主播对比：甲方 ").Append(comparison.A.Name).Append(" / 乙方 ").Append(comparison.B.Name).Append('\n');
            text.Append("项目 | 甲方 | 乙方 | 较高方\n");
            text.Append("来源包 | ").Append(comparison.ASourcePackName).Append("（").Append(comparison.ASourcePackId)
                .Append("） | ").Append(comparison.BSourcePackName).Append("（").Append(comparison.BSourcePackId).Append("） | —\n");
            foreach (var stat in comparison.Stats)
                text.Append(stat.Name).Append(" | ").Append(Number(stat.A)).Append(" | ").Append(Number(stat.B))
                    .Append(" | ").Append(stat.HigherSide < 0 ? "相同" : stat.HigherSide == 0 ? "甲方" : "乙方").Append('\n');
            text.Append("\n赛道关系（攻击赛道对目标主副赛道的乘积，不含同赛道加成或实际技能覆盖）：\n");
            AppendTracks(text, "甲方 → 乙方", comparison.AToBTracks);
            AppendTracks(text, "乙方 → 甲方", comparison.BToATracks);
            text.Append("\n先制技能对比（开场优先度大于 0，非行动先后保证）：\n");
            AppendPriority(text, "甲方", comparison.AToBSkills);
            AppendPriority(text, "乙方", comparison.BToASkills);
            text.Append("\n伤害范围与理论击倒次数：双方满人气、五项能力等级为 0，单人首发，无后排、无修饰器。\n")
                .Append("保留计算器原有开场人设与直播间效果；每个方向各按本方先手、技能命中且无暴击预览。\n")
                .Append("几下击倒仅为满人气除以单次预览伤害的向上取整，假定每次伤害不变；不模拟回复、保命、反弹、后续效果或命中率，不保证实际击倒。\n")
                .Append("固定伤害、持续伤害和其他预览未覆盖的效果不计入；列出全部技能，不表示 PP 充足或行动可用。\n");
            AppendSkills(text, "甲方 → 乙方", comparison.AToBSkills);
            AppendSkills(text, "乙方 → 甲方", comparison.BToASkills);
            text.Append("\n资料卡并排（左为甲方，右为乙方；排名基于当前内容库）：\n");
            string[] left = comparison.A.ToText().TrimEnd('\n').Split('\n');
            string[] right = comparison.B.ToText().TrimEnd('\n').Split('\n');
            int width = left.Max(DisplayWidth);
            for (int index = 0; index < Math.Max(left.Length, right.Length); index++)
            {
                string first = index < left.Length ? left[index] : "";
                text.Append(first).Append(' ', width - DisplayWidth(first)).Append(" │ ")
                    .Append(index < right.Length ? right[index] : "").Append('\n');
            }
            return text.ToString();
        }

        static void AppendTracks(StringBuilder text, string direction, List<StreamerTrackComparison> tracks)
        {
            foreach (var track in tracks)
                text.Append("  ").Append(direction).Append("：").Append(track.Symbol).Append(' ').Append(track.Name)
                    .Append(" ×").Append(track.Multiplier.ToString("G", CultureInfo.InvariantCulture))
                    .Append(track.Multiplier > 1 ? "（克制）" : track.Multiplier < 1 ? "（受抵抗）" : "（普通）").Append('\n');
        }

        static void AppendPriority(StringBuilder text, string side, List<StreamerSkillComparison> skills)
        {
            var positive = skills.Where(skill => skill.Priority > 0).ToList();
            text.Append("  ").Append(side).Append("：");
            if (positive.Count == 0) text.Append("无");
            else text.Append(string.Join("、", positive.Select(skill => skill.Name + "（" + Signed(skill.Priority) + "）")));
            text.Append('\n');
        }

        static void AppendSkills(StringBuilder text, string direction, List<StreamerSkillComparison> skills)
        {
            text.Append('\n').Append(direction).Append("：\n");
            foreach (var skill in skills)
            {
                text.Append("  ").Append(Number(skill.Index + 1)).Append(". ").Append(skill.Name)
                    .Append(skill.IsFifth ? "〔必杀技能〕" : "〔常规技能〕")
                    .Append("；基础先制 ").Append(Signed(skill.BasePriority)).Append("，开场先制 ").Append(Signed(skill.Priority))
                    .Append("；伤害 ").Append(Number(skill.Range.Min)).Append('～').Append(Number(skill.Range.Max))
                    .Append("；赛道倍率 ×").Append(skill.Range.Eff.ToString("G", CultureInfo.InvariantCulture)).Append("；");
                if (!skill.MinHits.HasValue) text.Append("无可预览的常规伤害，几下击倒不适用");
                else if (!skill.MaxHits.HasValue) text.Append("理论最少 ").Append(Number(skill.MinHits.Value)).Append(" 下，上限无法估算");
                else text.Append("理论 ").Append(Number(skill.MinHits.Value)).Append('～').Append(Number(skill.MaxHits.Value)).Append(" 下击倒");
                text.Append('\n');
                foreach (string note in skill.PriorityNotes) text.Append("    先制说明：").Append(note).Append('\n');
                foreach (string note in skill.Range.Notes) text.Append("    ").Append(note).Append('\n');
            }
        }

        static int DisplayWidth(string value)
        {
            int width = 0;
            for (int index = 0; index < value.Length; index++)
            {
                char c = value[index];
                if (char.IsHighSurrogate(c) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                { width += 2; index++; continue; }
                var category = char.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark) continue;
                width += c >= 0x1100 && (c <= 0x115f || c >= 0x2e80 && c <= 0xa4cf
                    || c >= 0xac00 && c <= 0xd7a3 || c >= 0xf900 && c <= 0xfaff
                    || c >= 0xfe10 && c <= 0xfe6f || c >= 0xff00 && c <= 0xff60 || c >= 0xffe0 && c <= 0xffe6) ? 2 : 1;
            }
            return width;
        }

        static string Number(int value) { return value.ToString(CultureInfo.InvariantCulture); }
        static string Signed(int value) { return value.ToString("+0;-0;0", CultureInfo.InvariantCulture); }
    }
}
